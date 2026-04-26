using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TerrainGen;
using UnityEngine;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Path-B "hybrid Pangu-style" support: registers each generated river as a
    /// real <c>TerrainGenerator+WaterArea</c> in <c>_generationData.waterAreas</c>,
    /// the same list FF uses for lakes/oceans.
    ///
    /// Why bother (vs. relying on the WaterPath ribbon alone):
    ///   * <c>waterAreas</c> IS serialized in the .map. Once we add a river
    ///     polygon there, FF restores it on save/reload automatically — no
    ///     manual respawn needed for the underlying water plane.
    ///   * <c>FishingManager</c> queries <c>waterAreas</c> for fishable bodies.
    ///     With rivers in the list, fishing nodes spawn naturally.
    ///   * The water-plane mesh covers the polygon at <c>waterHeight</c> on
    ///     both gen and reload, so we no longer get the "wide water on gen,
    ///     muddy strip on reload" mismatch when the heightmap dips below
    ///     waterHeight outside the WaterPath ribbon's coverage.
    ///
    /// The WaterPath ribbon stays — it's what gives the river its flow
    /// animation. The polygon water-plane provides the consistent base
    /// coverage; the ribbon rides on top to animate the flow.
    ///
    /// Polygon construction follows Pangu's pattern (TryBuildLakeWaterArea):
    /// rasterize the river path into a heightmap-resolution bool mask,
    /// scan for edge cells with their normal directions, build the WaterArea
    /// struct and append. Shaped like a thick line instead of Pangu's
    /// circle/rectangle.
    /// </summary>
    internal static class RiverWaterAreaBuilder
    {
        // Reflection caches — populated lazily on first call.
        private static Type? _waterAreaType;
        private static Type? _waterEdgeType;
        private static Type? _pairIntIntType;
        private static Type? _waterTypeType;
        private static FieldInfo? _faWaterType, _faPoints, _faEdge, _faShore, _faMinX, _faMinZ, _faMaxX, _faMaxZ;
        private static FieldInfo? _feX, _feZ, _feNx, _feNz;
        private static ConstructorInfo? _pairCtor;
        private static UnityEngine.Object? _cachedRiverWaterType;

        /// <summary>The WaterType all our river WaterAreas use. Currently
        /// the original (un-cloned) SO — see <see cref="ResolveRiverWaterType"/>
        /// for why cloning + modifying broke things.</summary>
        public static UnityEngine.Object? RiverWaterType => _cachedRiverWaterType;

        /// <summary>Heightmap-coord bounds key for tracking river WaterAreas.
        /// We use a struct (instead of ValueTuple) because net46's BCL doesn't
        /// include System.ValueTuple by default and we'd rather not pull in
        /// a NuGet package for a 4-int container.</summary>
        public readonly struct WaterAreaBoundsKey : IEquatable<WaterAreaBoundsKey>
        {
            public readonly int MinX, MinZ, MaxX, MaxZ;
            public WaterAreaBoundsKey(int minX, int minZ, int maxX, int maxZ)
            { MinX = minX; MinZ = minZ; MaxX = maxX; MaxZ = maxZ; }
            public bool Equals(WaterAreaBoundsKey o)
                => MinX == o.MinX && MinZ == o.MinZ && MaxX == o.MaxX && MaxZ == o.MaxZ;
            public override bool Equals(object? obj)
                => obj is WaterAreaBoundsKey o && Equals(o);
            public override int GetHashCode()
                => unchecked((MinX * 397) ^ (MinZ * 13) ^ (MaxX * 7) ^ MaxZ);
        }

        /// <summary>Bounds of every river WaterArea we've registered.
        /// FishingShack postfix uses these to distinguish OUR fishing areas
        /// from vanilla lakes/oceans.</summary>
        public static readonly HashSet<WaterAreaBoundsKey> RiverWaterAreaBounds
            = new HashSet<WaterAreaBoundsKey>();

        /// <summary>Heightmap-coord cell on a river path.</summary>
        public readonly struct CellCoord : IEquatable<CellCoord>
        {
            public readonly int X, Z;
            public CellCoord(int x, int z) { X = x; Z = z; }
            public bool Equals(CellCoord o) => X == o.X && Z == o.Z;
            public override bool Equals(object? obj) => obj is CellCoord o && Equals(o);
            public override int GetHashCode() => unchecked((X * 397) ^ Z);
        }

        /// <summary>Heightmap-coord positions of every river control point we
        /// know about. ForceWaterPlaneRebuild uses these to identify "our"
        /// WaterAreas in the post-reload list when bounds matching fails
        /// (e.g., when our long thin river polygon got fragmented or merged
        /// with adjacent lakes during FF's serialization round-trip — the
        /// merged saved waterArea contains our cps but has different bounds).
        /// Any saved waterArea whose bounding box contains at least one of
        /// our cps is "ours" (or hosts a piece of one of our rivers).</summary>
        public static readonly HashSet<CellCoord> RiverCpCells
            = new HashSet<CellCoord>();

        /// <summary>Reset on scene load.</summary>
        public static void ResetForSceneLoad()
        {
            RiverWaterAreaBounds.Clear();
            RiverCpCells.Clear();
        }

        /// <summary>Pangu-style merge: for each river WaterArea we registered
        /// at Stage 38 postfix, find any waterAreas in the list that touch
        /// it (bounds-overlap + 1-cell padding adjacency check, mirroring
        /// Pangu's <c>MergeAndAddWaterArea</c> at line 7487 of Pangu_FF.dll
        /// decompiled source). Combine their <c>points[,]</c> masks into a
        /// single bigger polygon, rebuild edges/shore, REPLACE the existing
        /// entries in <c>_generationData.waterAreas</c> with one merged
        /// polygon that uses the LAKE's WaterType.
        ///
        /// Why merge:
        ///   * Eliminates the visual seam at river-lake junctions (one
        ///     polygon = one continuous water plane).
        ///   * The merged polygon adopts a lake's waterType — and lake
        ///     waterTypes are in <c>waterSettings.lakeTypes</c>, which IS
        ///     saved with the map. So the merged WaterArea round-trips
        ///     cleanly through save/reload (waterMaterial reference stays
        ///     valid → chunks render after reload).
        ///   * Same architectural pattern Pangu uses for runtime lake
        ///     creation, applied at gen-time for our rivers.
        ///
        /// Also updates <see cref="RiverWaterAreaBounds"/> with the merged
        /// area's bounds so cp-containment matching still works on reload.
        /// </summary>
        public static int MergeRiverWaterAreasWithAdjacent(TerrainGenerator tg)
        {
            try
            {
                if (!ResolveTypes()) return 0;
                var gdField = AccessTools.Field(typeof(TerrainGenerator), "_generationData");
                var gd = gdField?.GetValue(tg);
                if (gd == null) return 0;
                var waField = gd.GetType().GetField("waterAreas",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var was = waField?.GetValue(gd) as System.Collections.IList;
                if (was == null || was.Count == 0) return 0;

                int mergesPerformed = 0;
                int padding = 1;  // Pangu uses 1-cell adjacency padding

                // For each river WaterArea (by bounds), find and merge with adjacent.
                // Iterate the bounds set; for each, find ALL touching waterAreas (transitively),
                // then build one merged WaterArea replacing them.
                var pendingBounds = new List<WaterAreaBoundsKey>(RiverWaterAreaBounds);
                RiverWaterAreaBounds.Clear();

                foreach (var riverKey in pendingBounds)
                {
                    // Find indices of waterAreas that touch this river or each other
                    // (transitive closure via Pangu's pattern).
                    var mergeIndices = new HashSet<int>();
                    int mMinX = riverKey.MinX, mMinZ = riverKey.MinZ;
                    int mMaxX = riverKey.MaxX, mMaxZ = riverKey.MaxZ;
                    bool grew = true;
                    while (grew)
                    {
                        grew = false;
                        for (int i = 0; i < was.Count; i++)
                        {
                            if (mergeIndices.Contains(i)) continue;
                            var entry = was[i];
                            if (entry == null) continue;
                            int eMinX = (int)_faMinX!.GetValue(entry);
                            int eMinZ = (int)_faMinZ!.GetValue(entry);
                            int eMaxX = (int)_faMaxX!.GetValue(entry);
                            int eMaxZ = (int)_faMaxZ!.GetValue(entry);
                            if (eMinX > mMaxX + padding || eMaxX < mMinX - padding ||
                                eMinZ > mMaxZ + padding || eMaxZ < mMinZ - padding)
                                continue;  // bounds don't touch
                            mergeIndices.Add(i);
                            mMinX = Math.Min(mMinX, eMinX);
                            mMinZ = Math.Min(mMinZ, eMinZ);
                            mMaxX = Math.Max(mMaxX, eMaxX);
                            mMaxZ = Math.Max(mMaxZ, eMaxZ);
                            grew = true;
                        }
                    }
                    if (mergeIndices.Count == 0) continue;

                    // Combine all masks into one big mask sized to merged bounds.
                    int gw = mMaxX - mMinX + 1, gh = mMaxZ - mMinZ + 1;
                    bool[,] merged = new bool[gw, gh];
                    UnityEngine.Object? lakeWaterType = null;
                    foreach (int idx in mergeIndices)
                    {
                        var entry = was[idx];
                        if (entry == null) continue;
                        int eMinX = (int)_faMinX!.GetValue(entry);
                        int eMinZ = (int)_faMinZ!.GetValue(entry);
                        int eMaxX = (int)_faMaxX!.GetValue(entry);
                        int eMaxZ = (int)_faMaxZ!.GetValue(entry);
                        var pts = _faPoints!.GetValue(entry) as bool[,];
                        if (pts == null) continue;
                        int pw = pts.GetLength(0), ph = pts.GetLength(1);
                        for (int z = 0; z < ph; z++)
                            for (int x = 0; x < pw; x++)
                                if (pts[x, z])
                                {
                                    int gx = (eMinX + x) - mMinX;
                                    int gz = (eMinZ + z) - mMinZ;
                                    if (gx >= 0 && gx < gw && gz >= 0 && gz < gh)
                                        merged[gx, gz] = true;
                                }
                        // Prefer a non-river waterType (any lake one we're merging with)
                        var wt = _faWaterType!.GetValue(entry) as UnityEngine.Object;
                        if (wt != null && lakeWaterType == null)
                            lakeWaterType = wt;
                    }
                    if (lakeWaterType == null)
                        lakeWaterType = ResolveRiverWaterType(tg);  // fallback

                    // Recompute edges + shore for combined mask.
                    var edges = new List<object>();
                    var shores = new List<object>();
                    for (int m = mMinZ; m <= mMaxZ; m++)
                    {
                        for (int n = mMinX; n <= mMaxX; n++)
                        {
                            int sx = n - mMinX, sz = m - mMinZ;
                            if (!merged[sx, sz]) continue;
                            bool n_w = IsMaskFilled(merged, mMinX, mMinZ, mMaxX, mMaxZ, n - 1, m);
                            bool n_e = IsMaskFilled(merged, mMinX, mMinZ, mMaxX, mMaxZ, n + 1, m);
                            bool n_s = IsMaskFilled(merged, mMinX, mMinZ, mMaxX, mMaxZ, n, m - 1);
                            bool n_n = IsMaskFilled(merged, mMinX, mMinZ, mMaxX, mMaxZ, n, m + 1);
                            if (!(n_w && n_e && n_s && n_n))
                            {
                                int nx = 0, nz = 0;
                                if (!n_w) nx--;
                                if (!n_e) nx++;
                                if (!n_s) nz--;
                                if (!n_n) nz++;
                                if (nx == 0 && nz == 0) nz = 1;
                                var edge = Activator.CreateInstance(_waterEdgeType!)!;
                                _feX!.SetValue(edge, n);
                                _feZ!.SetValue(edge, m);
                                _feNx!.SetValue(edge, nx);
                                _feNz!.SetValue(edge, nz);
                                edges.Add(edge);
                                var pair = _pairCtor!.Invoke(new object[] { n, m });
                                shores.Add(pair);
                            }
                        }
                    }
                    var edgesArr = Array.CreateInstance(_waterEdgeType!, edges.Count);
                    for (int i = 0; i < edges.Count; i++) edgesArr.SetValue(edges[i], i);
                    var shoresArr = Array.CreateInstance(_pairIntIntType!, shores.Count);
                    for (int i = 0; i < shores.Count; i++) shoresArr.SetValue(shores[i], i);

                    // Construct merged WaterArea
                    object mergedArea = Activator.CreateInstance(_waterAreaType!)!;
                    _faWaterType!.SetValue(mergedArea, lakeWaterType);
                    _faPoints!.SetValue(mergedArea, merged);
                    _faEdge!.SetValue(mergedArea, edgesArr);
                    _faShore!.SetValue(mergedArea, shoresArr);
                    _faMinX!.SetValue(mergedArea, mMinX);
                    _faMinZ!.SetValue(mergedArea, mMinZ);
                    _faMaxX!.SetValue(mergedArea, mMaxX);
                    _faMaxZ!.SetValue(mergedArea, mMaxZ);

                    // Remove all merge candidates from list (descending index order)
                    var sortedIndices = new List<int>(mergeIndices);
                    sortedIndices.Sort();
                    for (int k = sortedIndices.Count - 1; k >= 0; k--)
                        was.RemoveAt(sortedIndices[k]);

                    // Add merged
                    was.Add(mergedArea);

                    // Update RiverWaterAreaBounds with merged area's bounds for save/reload tracking
                    RiverWaterAreaBounds.Add(new WaterAreaBoundsKey(mMinX, mMinZ, mMaxX, mMaxZ));

                    Log($"Merged river [{riverKey.MinX},{riverKey.MinZ}..{riverKey.MaxX},{riverKey.MaxZ}] " +
                        $"with {mergeIndices.Count - 1} adjacent waterArea(s) → " +
                        $"[{mMinX},{mMinZ}..{mMaxX},{mMaxZ}] " +
                        $"using waterType '{(lakeWaterType as UnityEngine.Object)?.name ?? "?"}'");
                    mergesPerformed++;
                }

                Log($"MergeRiverWaterAreasWithAdjacent: {mergesPerformed} merge(s) performed; waterAreas now {was.Count}");
                return mergesPerformed;
            }
            catch (Exception ex)
            {
                Log($"MergeRiverWaterAreasWithAdjacent exception: {ex}");
                return 0;
            }
        }

        /// <summary>Re-populate <see cref="RiverWaterAreaBounds"/> from sidecar
        /// river control points using the SAME rasterization as gen-time
        /// <see cref="BuildAndAddForAllRivers"/>. Needed on save load: the
        /// bounds HashSet lives in static memory and gets cleared every
        /// scene transition (and is empty after a game restart), so the
        /// FishingShack and ForceWaterPlaneRebuild logic that depends on
        /// "is this WaterArea one of ours?" needs the bounds re-registered
        /// from saved data. Doesn't add anything to <c>waterAreas</c> —
        /// those entries already round-tripped via FF's serializer.
        /// </summary>
        public static int PopulateBoundsFromSidecar(
            System.Collections.Generic.List<RiverPersistence.RiverData> rivers,
            TerrainGenerator tg, int innerRadius)
        {
            try
            {
                if (!ResolveTypes()) return 0;

                var msField = AccessTools.Field(typeof(TerrainGenerator), "mapSettings");
                var ms = msField?.GetValue(tg);
                if (ms == null) { Log("PopulateBoundsFromSidecar: mapSettings null"); return 0; }
                var msType = ms.GetType();
                int hmRes = (int)(msType.GetField("heightmapResolution")?.GetValue(ms) ?? 0);
                int mapW = (int)(msType.GetField("width")?.GetValue(ms) ?? 0);
                int mapD = (int)(msType.GetField("depth")?.GetValue(ms) ?? 0);
                if (hmRes <= 0 || mapW <= 0 || mapD <= 0) return 0;

                int populated = 0;
                int cpsTracked = 0;
                foreach (var rd in rivers)
                {
                    if (rd == null || rd.Points.Count < 2) continue;

                    // Mirror the rasterization pass in BuildOneRiverWaterArea
                    // to produce the same bounds.
                    bool[,] full = new bool[hmRes, hmRes];
                    int gMinX = int.MaxValue, gMinZ = int.MaxValue;
                    int gMaxX = int.MinValue, gMaxZ = int.MinValue;
                    int prevHx = -1, prevHz = -1;
                    bool havePrev = false;
                    foreach (var p in rd.Points)
                    {
                        int hx = WorldToHmX(p.Pos.x, mapW, hmRes);
                        int hz = WorldToHmZ(p.Pos.z, mapD, hmRes);
                        // Track every cp's heightmap coord — used by
                        // cp-containment matching to identify saved
                        // WaterAreas whose bounds contain our river path.
                        if (RiverCpCells.Add(new CellCoord(hx, hz)))
                            cpsTracked++;
                        if (havePrev)
                            BresenhamMarkDisc(full, hmRes, prevHx, prevHz, hx, hz, innerRadius,
                                ref gMinX, ref gMinZ, ref gMaxX, ref gMaxZ);
                        prevHx = hx; prevHz = hz;
                        havePrev = true;
                    }
                    if (gMinX > gMaxX || gMinZ > gMaxZ) continue;
                    var key = new WaterAreaBoundsKey(gMinX, gMinZ, gMaxX, gMaxZ);
                    RiverWaterAreaBounds.Add(key);
                    populated++;
                    Log($"  re-registered river bounds [{gMinX},{gMinZ}..{gMaxX},{gMaxZ}]");
                }
                Log($"PopulateBoundsFromSidecar: tracked {cpsTracked} unique cp cells across {populated} rivers");
                return populated;
            }
            catch (Exception ex)
            {
                Log($"PopulateBoundsFromSidecar exception: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Build river WaterAreas from each entry in <c>_generationData.rivers</c>
        /// and append to <c>_generationData.waterAreas</c>. Idempotent — does
        /// nothing if the rivers list is empty or types aren't resolvable.
        /// </summary>
        /// <returns>Number of WaterAreas added.</returns>
        public static int BuildAndAddForAllRivers(TerrainGenerator tg, int innerRadius)
        {
            try
            {
                if (!ResolveTypes()) return 0;

                // ── Read map dimensions ──────────────────────────────────────
                var msField = AccessTools.Field(typeof(TerrainGenerator), "mapSettings");
                var ms = msField?.GetValue(tg);
                if (ms == null) { Log("BuildAndAddForAllRivers: mapSettings null"); return 0; }
                var msType = ms.GetType();
                int hmRes = (int)(msType.GetField("heightmapResolution")?.GetValue(ms) ?? 0);
                int mapW = (int)(msType.GetField("width")?.GetValue(ms) ?? 0);
                int mapD = (int)(msType.GetField("depth")?.GetValue(ms) ?? 0);
                if (hmRes <= 0 || mapW <= 0 || mapD <= 0)
                {
                    Log("BuildAndAddForAllRivers: invalid map dimensions");
                    return 0;
                }

                // ── Get rivers + waterAreas from generationData ──────────────
                var gdField = AccessTools.Field(typeof(TerrainGenerator), "_generationData");
                var gd = gdField?.GetValue(tg);
                if (gd == null) { Log("BuildAndAddForAllRivers: _generationData null"); return 0; }

                var riversField = gd.GetType().GetField("rivers",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var rivers = riversField?.GetValue(gd) as IList;
                if (rivers == null || rivers.Count == 0)
                {
                    Log("BuildAndAddForAllRivers: no rivers in _generationData");
                    return 0;
                }

                var waterAreasField = gd.GetType().GetField("waterAreas",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var waterAreas = waterAreasField?.GetValue(gd) as IList;
                if (waterAreas == null)
                {
                    Log("BuildAndAddForAllRivers: waterAreas list null");
                    return 0;
                }

                // ── Resolve a WaterType to use for the river polygon ─────────
                var waterType = ResolveRiverWaterType(tg);
                if (waterType == null)
                {
                    Log("BuildAndAddForAllRivers: no suitable WaterType found");
                    return 0;
                }

                // ── Build WaterArea(s) per river and append ──────────────────
                // FF spawns fishing nodes per WaterArea. Splitting each river
                // into multiple smaller polygons gives more nodes per length
                // of river — without changing the visual (water plane still
                // covers contiguous cells regardless of how many polygons we
                // split into).
                int chunkSize = RiversRestoredMod.RiverWaterAreaChunkSize?.Value ?? 25;
                int added = 0;
                for (int i = 0; i < rivers.Count; i++)
                {
                    var river = rivers[i];
                    if (river == null) continue;

                    var cpsField = river.GetType().GetField("points",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var cps = cpsField?.GetValue(river) as IList;
                    if (cps == null || cps.Count < 2) continue;

                    // Decide chunk boundaries. 0 = one polygon for whole river.
                    int totalCps = cps.Count;
                    int effectiveChunk = (chunkSize <= 0 || chunkSize >= totalCps) ? totalCps : Mathf.Max(2, chunkSize);
                    int riverChunksAdded = 0;

                    for (int start = 0; start < totalCps - 1; start += effectiveChunk)
                    {
                        // Each chunk includes one cp of overlap with the next so
                        // the water plane has no gaps at chunk boundaries.
                        int end = Mathf.Min(start + effectiveChunk + 1, totalCps);
                        if (end - start < 2) break;

                        object? area = BuildOneRiverWaterArea(cps, start, end, hmRes, mapW, mapD, innerRadius, waterType);
                        if (area == null) continue;
                        waterAreas.Add(area);
                        added++;
                        riverChunksAdded++;

                        // Track bounds so FishingShackPatch can identify
                        // FishAreas that came from one of our rivers.
                        int bMinX = (int)_faMinX!.GetValue(area);
                        int bMinZ = (int)_faMinZ!.GetValue(area);
                        int bMaxX = (int)_faMaxX!.GetValue(area);
                        int bMaxZ = (int)_faMaxZ!.GetValue(area);
                        RiverWaterAreaBounds.Add(new WaterAreaBoundsKey(bMinX, bMinZ, bMaxX, bMaxZ));

                        // (Don't log every chunk on dense rivers — too noisy.
                        //  Just summarize per-river.)
                    }
                    Log($"  river[{i}]: {totalCps} cps → {riverChunksAdded} WaterArea chunk(s) (chunkSize={effectiveChunk})");
                }

                Log($"BuildAndAddForAllRivers: appended {added} river WaterArea(s) to _generationData.waterAreas (now {waterAreas.Count} total)");
                return added;
            }
            catch (Exception ex)
            {
                Log($"BuildAndAddForAllRivers exception: {ex}");
                return 0;
            }
        }

        // ── Reflection setup ────────────────────────────────────────────────
        private static bool ResolveTypes()
        {
            if (_waterAreaType != null) return true;

            _waterAreaType = AccessTools.TypeByName("TerrainGen.TerrainGenerator+WaterArea");
            _waterEdgeType = AccessTools.TypeByName("TerrainGen.TerrainGenerator+WaterEdge");
            _waterTypeType = AccessTools.TypeByName("TerrainGen.WaterType");

            // Pair<int, int> — top-level generic
            var pairOpen = AccessTools.TypeByName("Pair`2");
            if (pairOpen != null)
                _pairIntIntType = pairOpen.MakeGenericType(typeof(int), typeof(int));

            if (_waterAreaType == null || _waterEdgeType == null || _pairIntIntType == null || _waterTypeType == null)
            {
                Log($"ResolveTypes failed: WaterArea={_waterAreaType != null} WaterEdge={_waterEdgeType != null} Pair={_pairIntIntType != null} WaterType={_waterTypeType != null}");
                return false;
            }

            _faWaterType = _waterAreaType.GetField("waterType");
            _faPoints = _waterAreaType.GetField("points");
            _faEdge = _waterAreaType.GetField("edge");
            _faShore = _waterAreaType.GetField("shore");
            _faMinX = _waterAreaType.GetField("minX");
            _faMinZ = _waterAreaType.GetField("minZ");
            _faMaxX = _waterAreaType.GetField("maxX");
            _faMaxZ = _waterAreaType.GetField("maxZ");

            _feX = _waterEdgeType.GetField("x");
            _feZ = _waterEdgeType.GetField("z");
            _feNx = _waterEdgeType.GetField("nx");
            _feNz = _waterEdgeType.GetField("nz");

            _pairCtor = _pairIntIntType.GetConstructor(new[] { typeof(int), typeof(int) });

            if (_pairCtor == null)
            {
                Log("ResolveTypes failed: Pair<int,int> constructor not found");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Pick a WaterType to attach to the river polygon. Preference order:
        /// LakeSmall, Pond, Lake, then any non-ocean type. Cached after the
        /// first successful resolution.
        ///
        /// HISTORY: an earlier version cloned the SO and nulled
        /// `shorelineSampleRadius = 0` to kill the yellow shoreline band.
        /// That triggered an infinite loop in FF's
        /// `AddShorelineDetailsForArea`-style code:
        ///   for (int l = 0; l &lt; shore.Length; l += waterType.shorelineSampleRadius / 2)
        /// With sampleRadius=0, the step is 0 → loop never advances → gen
        /// pipeline hangs there → water plane never renders for our rivers.
        ///
        /// Current approach: use the original WaterType unchanged, and
        /// disable shoreline rendering at the WATER-AREA level by setting
        /// the WaterArea's edge[] and shore[] arrays to empty. FF's
        /// shoreline-painting and detail-spawning loops iterate those
        /// arrays — empty = nothing to paint, zero infinite-loop risk.
        /// Yellow-shoreline kill stays; water plane survives.
        /// </summary>
        private static UnityEngine.Object? ResolveRiverWaterType(TerrainGenerator tg)
        {
            if (_cachedRiverWaterType != null) return _cachedRiverWaterType;
            try
            {
                // ── Preferred path: borrow from waterSettings.lakeTypes ──
                // FF serializes waterSettings with the map (see WaterSettings.Save
                // at line 482814) — every WaterType in lakeTypes round-trips
                // cleanly with its waterMaterial / foamMaterial / etc. references
                // intact. WaterTypes from Resources.FindObjectsOfTypeAll that
                // AREN'T in lakeTypes don't get saved with the map; on reload
                // the SO is recreated empty with null material refs, and our
                // river WaterChunks render invisible. So we pick the first
                // lakeTypes entry to guarantee round-trip survival.
                try
                {
                    var wsField = tg.GetType().GetField("waterSettings",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var ws = wsField?.GetValue(tg);
                    if (ws != null)
                    {
                        var lakeTypesField = ws.GetType().GetField("lakeTypes",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var lakeTypes = lakeTypesField?.GetValue(ws) as System.Collections.IList;
                        if (lakeTypes != null && lakeTypes.Count > 0)
                        {
                            var first = lakeTypes[0] as UnityEngine.Object;
                            if (first != null)
                            {
                                _cachedRiverWaterType = first;
                                Log($"ResolveRiverWaterType: using waterSettings.lakeTypes[0] = '{first.name}' " +
                                    "(serialized with map → survives save/reload).");
                                return first;
                            }
                        }
                    }
                }
                catch (Exception innerEx)
                {
                    Log($"  waterSettings.lakeTypes path failed: {innerEx.Message} — falling back to Resources scan");
                }

                // ── Fallback: Resources.FindObjectsOfTypeAll (legacy) ──
                // Used if waterSettings.lakeTypes isn't accessible. Beware:
                // WaterTypes picked this way may not survive save/reload.
                var all = UnityEngine.Resources.FindObjectsOfTypeAll(_waterTypeType!);
                if (all == null || all.Length == 0) return null;

                UnityEngine.Object? source = null;
                string[] preferred = { "LakeSmall", "Pond", "Lake" };
                foreach (var name in preferred)
                {
                    foreach (var wt in all)
                    {
                        if (wt != null && string.Equals(wt.name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            source = wt;
                            break;
                        }
                    }
                    if (source != null) break;
                }
                if (source == null)
                {
                    foreach (var wt in all)
                    {
                        if (wt == null) continue;
                        string n = wt.name ?? "";
                        if (n.IndexOf("ocean", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        source = wt;
                        break;
                    }
                }
                if (source == null) return null;

                _cachedRiverWaterType = source;
                Log($"ResolveRiverWaterType: fallback Resources scan picked '{source.name}' " +
                    "(may not survive save/reload — prefer lakeTypes path).");
                return source;
            }
            catch (Exception ex)
            {
                Log($"ResolveRiverWaterType exception: {ex.Message}");
                return null;
            }
        }

        // ── Polygon construction ────────────────────────────────────────────
        /// <summary>Build a WaterArea polygon from a chunk of a river's
        /// control points (cps[startIdx ..endIdx - 1]). Pass startIdx = 0
        /// and endIdx = cps.Count for "whole river".</summary>
        private static object? BuildOneRiverWaterArea(IList cps, int startIdx, int endIdx,
                                                       int hmRes, int mapW, int mapD,
                                                       int radiusCells, UnityEngine.Object waterType)
        {
            try
            {
                if (cps == null || endIdx - startIdx < 2) return null;

                // Allocate full-size mask. We'll crop after we know bounds.
                bool[,] full = new bool[hmRes, hmRes];
                int gMinX = int.MaxValue, gMinZ = int.MaxValue;
                int gMaxX = int.MinValue, gMaxZ = int.MinValue;

                int prevHx = -1, prevHz = -1;
                bool havePrev = false;
                for (int idx = startIdx; idx < endIdx; idx++)
                {
                    var pt = cps[idx];
                    if (pt == null) continue;
                    if (!TryGetPointXZ(pt, out float wx, out float wz)) continue;
                    int hx = WorldToHmX(wx, mapW, hmRes);
                    int hz = WorldToHmZ(wz, mapD, hmRes);
                    // Track cp cell at gen time too, so post-reload matching
                    // works even on the same session that did the gen.
                    RiverCpCells.Add(new CellCoord(hx, hz));
                    if (havePrev)
                    {
                        BresenhamMarkDisc(full, hmRes, prevHx, prevHz, hx, hz, radiusCells,
                            ref gMinX, ref gMinZ, ref gMaxX, ref gMaxZ);
                    }
                    prevHx = hx; prevHz = hz;
                    havePrev = true;
                }

                if (gMinX > gMaxX || gMinZ > gMaxZ) return null;

                // Crop to bbox-sized mask
                int gw = gMaxX - gMinX + 1;
                int gh = gMaxZ - gMinZ + 1;
                bool[,] mask = new bool[gw, gh];
                for (int z = 0; z < gh; z++)
                    for (int x = 0; x < gw; x++)
                        mask[x, z] = full[gMinX + x, gMinZ + z];

                // Populate edge[] and shore[] arrays from the polygon
                // perimeter. We previously tried leaving these empty to
                // suppress shoreline painting, but FF's WaterChunk mesh
                // build needs the shore array to construct the water-plane
                // geometry on reload — empty arrays produce a working
                // water plane on gen but no plane after save/reload.
                //
                // Keeping them populated means FF will paint a vanilla
                // shoreline texture along river edges (yellow band at
                // river-lake junctions in particular). Cosmetic-only;
                // water rendering and fishing both work on reload.
                var edges = new List<object>();
                var shores = new List<object>();
                for (int m = gMinZ; m <= gMaxZ; m++)
                {
                    for (int n = gMinX; n <= gMaxX; n++)
                    {
                        int sx = n - gMinX, sz = m - gMinZ;
                        if (!mask[sx, sz]) continue;

                        bool n_w = IsMaskFilled(mask, gMinX, gMinZ, gMaxX, gMaxZ, n - 1, m);
                        bool n_e = IsMaskFilled(mask, gMinX, gMinZ, gMaxX, gMaxZ, n + 1, m);
                        bool n_s = IsMaskFilled(mask, gMinX, gMinZ, gMaxX, gMaxZ, n, m - 1);
                        bool n_n = IsMaskFilled(mask, gMinX, gMinZ, gMaxX, gMaxZ, n, m + 1);

                        if (!(n_w && n_e && n_s && n_n))
                        {
                            int nx = 0, nz = 0;
                            if (!n_w) nx--;
                            if (!n_e) nx++;
                            if (!n_s) nz--;
                            if (!n_n) nz++;
                            if (nx == 0 && nz == 0) nz = 1;

                            var edge = Activator.CreateInstance(_waterEdgeType!)!;
                            _feX!.SetValue(edge, n);
                            _feZ!.SetValue(edge, m);
                            _feNx!.SetValue(edge, nx);
                            _feNz!.SetValue(edge, nz);
                            edges.Add(edge);

                            var pair = _pairCtor!.Invoke(new object[] { n, m });
                            shores.Add(pair);
                        }
                    }
                }

                var edgesArr = Array.CreateInstance(_waterEdgeType!, edges.Count);
                for (int i = 0; i < edges.Count; i++) edgesArr.SetValue(edges[i], i);
                var shoresArr = Array.CreateInstance(_pairIntIntType!, shores.Count);
                for (int i = 0; i < shores.Count; i++) shoresArr.SetValue(shores[i], i);

                // Build WaterArea struct (boxed). FieldInfo.SetValue on a boxed
                // struct mutates the box; we set everything then return the box.
                object area = Activator.CreateInstance(_waterAreaType!)!;
                _faWaterType!.SetValue(area, waterType);
                _faPoints!.SetValue(area, mask);
                _faEdge!.SetValue(area, edgesArr);
                _faShore!.SetValue(area, shoresArr);
                _faMinX!.SetValue(area, gMinX);
                _faMinZ!.SetValue(area, gMinZ);
                _faMaxX!.SetValue(area, gMaxX);
                _faMaxZ!.SetValue(area, gMaxZ);

                return area;
            }
            catch (Exception ex)
            {
                Log($"BuildOneRiverWaterArea exception: {ex.Message}");
                return null;
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────
        private static void BresenhamMarkDisc(bool[,] mask, int hmRes,
                                                int x0, int z0, int x1, int z1, int radius,
                                                ref int minX, ref int minZ, ref int maxX, ref int maxZ)
        {
            int dx = Math.Abs(x1 - x0), dz = Math.Abs(z1 - z0);
            int sx = x0 < x1 ? 1 : -1, sz = z0 < z1 ? 1 : -1;
            int err = dx - dz;
            int x = x0, z = z0;
            int r2 = radius * radius;

            while (true)
            {
                for (int rz = -radius; rz <= radius; rz++)
                {
                    int cz = z + rz;
                    if (cz < 0 || cz >= hmRes) continue;
                    for (int rx = -radius; rx <= radius; rx++)
                    {
                        if (rx * rx + rz * rz > r2) continue;
                        int cx = x + rx;
                        if (cx < 0 || cx >= hmRes) continue;
                        if (!mask[cx, cz])
                        {
                            mask[cx, cz] = true;
                            if (cx < minX) minX = cx;
                            if (cx > maxX) maxX = cx;
                            if (cz < minZ) minZ = cz;
                            if (cz > maxZ) maxZ = cz;
                        }
                    }
                }
                if (x == x1 && z == z1) break;
                int e2 = err * 2;
                if (e2 > -dz) { err -= dz; x += sx; }
                if (e2 < dx) { err += dx; z += sz; }
            }
        }

        private static bool IsMaskFilled(bool[,] mask, int gMinX, int gMinZ, int gMaxX, int gMaxZ, int x, int z)
        {
            if (x < gMinX || x > gMaxX || z < gMinZ || z > gMaxZ) return false;
            return mask[x - gMinX, z - gMinZ];
        }

        private static int WorldToHmX(float wx, int mapW, int hmRes)
            => Mathf.Clamp(Mathf.RoundToInt(wx / mapW * (hmRes - 1)), 0, hmRes - 1);
        private static int WorldToHmZ(float wz, int mapD, int hmRes)
            => Mathf.Clamp(Mathf.RoundToInt(wz / mapD * (hmRes - 1)), 0, hmRes - 1);

        private static bool TryGetPointXZ(object pt, out float x, out float z)
        {
            x = 0; z = 0;
            try
            {
                Type t = pt.GetType();
                foreach (var name in new[] { "position", "pos", "worldPos", "worldPosition" })
                {
                    var f = t.GetField(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f == null) continue;
                    var v = f.GetValue(pt);
                    if (v is Vector3 v3) { x = v3.x; z = v3.z; return true; }
                    if (v is Vector2 v2) { x = v2.x; z = v2.y; return true; }
                }
            }
            catch { }
            return false;
        }

        private static void Log(string msg) => RiversRestoredMod.Log.Msg($"[RR][WA] {msg}");
    }
}
