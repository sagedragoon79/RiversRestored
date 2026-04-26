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
    /// v0.2 walk-and-stamp WaterArea builder. Replaces v0.1's long-thin
    /// rasterized polygon construction with Pangu-style disc stamps.
    ///
    /// HOW IT WORKS:
    ///   For each river, walk the interpolated cp path at <c>RiverBlobStride</c>
    ///   cells, stamping a disc of <c>RiverBlobRadius</c> at each step.
    ///   Each stamp goes through <see cref="AddWaterAreaWithPanguMerge"/>
    ///   which finds any existing waterAreas whose bbox overlaps the
    ///   stamp + 1-cell padding (transitively), unions them all into one
    ///   merged polygon, and replaces them in <c>_generationData.waterAreas</c>.
    ///
    /// WHY (vs v0.1's long-thin rasterization):
    ///   Pangu's manually-painted thin rivers — proven to survive save/
    ///   reload — are built by stamping many small overlapping discs and
    ///   letting <c>MergeAndAddWaterArea</c> fuse them. v0.1 built one big
    ///   rasterized polygon directly. v0.2 mimics Pangu's construction
    ///   pattern in case the save/load round-trip is happier with stamp-
    ///   merged polygons than rasterized ones (see V0_2_PLAN.md §1).
    ///
    /// WATERTYPE PRIORITY:
    ///   When a stamp merges with an existing area, the merged polygon
    ///   adopts the existing area's WaterType (typically a lake/ocean's,
    ///   already in <c>waterSettings.lakeTypes</c> → serializes cleanly).
    ///   Unmerged stamps use the river fallback (also borrowed from
    ///   <c>lakeTypes[0]</c>, see <see cref="ResolveRiverWaterType"/>).
    /// </summary>
    internal static class RiverWaterAreaBuilder
    {
        // ── Reflection caches ──────────────────────────────────────────────
        private static Type? _waterAreaType;
        private static Type? _waterEdgeType;
        private static Type? _pairIntIntType;
        private static Type? _waterTypeType;
        private static FieldInfo? _faWaterType, _faPoints, _faEdge, _faShore, _faMinX, _faMinZ, _faMaxX, _faMaxZ;
        private static FieldInfo? _feX, _feZ, _feNx, _feNz;
        private static ConstructorInfo? _pairCtor;
        private static UnityEngine.Object? _cachedRiverWaterType;

        public static UnityEngine.Object? RiverWaterType => _cachedRiverWaterType;

        /// <summary>Heightmap-coord bounds key for tracking river WaterAreas.
        /// Custom struct because net46's BCL doesn't include System.ValueTuple
        /// and we don't want to pull a NuGet package for a 4-int container.</summary>
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

        /// <summary>Bounds of every river WaterArea we've registered. The
        /// FishingShack postfix uses these to identify which FishAreas
        /// came from one of our rivers (vs. vanilla lakes/oceans), so it
        /// can apply the river-fishing multiplier to those.</summary>
        public static readonly HashSet<WaterAreaBoundsKey> RiverWaterAreaBounds
            = new HashSet<WaterAreaBoundsKey>();

        public readonly struct CellCoord : IEquatable<CellCoord>
        {
            public readonly int X, Z;
            public CellCoord(int x, int z) { X = x; Z = z; }
            public bool Equals(CellCoord o) => X == o.X && Z == o.Z;
            public override bool Equals(object? obj) => obj is CellCoord o && Equals(o);
            public override int GetHashCode() => unchecked((X * 397) ^ Z);
        }

        /// <summary>Heightmap-coord positions of every river control point we
        /// know about. Used by ForceWaterPlaneRebuild to identify "ours" via
        /// cp-containment matching when bbox matching fails (e.g., when our
        /// polygon merged with a lake that has different bounds).</summary>
        public static readonly HashSet<CellCoord> RiverCpCells
            = new HashSet<CellCoord>();

        public static void ResetForSceneLoad()
        {
            RiverWaterAreaBounds.Clear();
            RiverCpCells.Clear();
            _cachedRiverWaterType = null;
        }

        /// <summary>v0.2 entry point. Walks every river's cps, stamping disc
        /// polygons via <see cref="AddWaterAreaWithPanguMerge"/>. Returns
        /// number of rivers that produced at least one stamp.</summary>
        public static int BuildAndAddForAllRivers(TerrainGenerator tg)
        {
            try
            {
                if (!ResolveTypes()) return 0;

                int blobRadius = RiversRestoredMod.RiverBlobRadius?.Value ?? 3;
                int blobStride = RiversRestoredMod.RiverBlobStride?.Value ?? 3;
                if (blobRadius < 1) blobRadius = 1;
                if (blobStride < 1) blobStride = 1;

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
                if (waterAreas == null) { Log("BuildAndAddForAllRivers: waterAreas null"); return 0; }

                var riverFallbackType = ResolveRiverWaterType(tg);
                if (riverFallbackType == null) { Log("BuildAndAddForAllRivers: no WaterType"); return 0; }

                int riversAdded = 0;
                int totalStamps = 0;
                int waterAreasBefore = waterAreas.Count;
                for (int i = 0; i < rivers.Count; i++)
                {
                    var river = rivers[i];
                    if (river == null) continue;
                    var cpsField = river.GetType().GetField("points",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var cps = cpsField?.GetValue(river) as IList;
                    if (cps == null || cps.Count < 2) continue;

                    int riverStamps = StampAlongPath(tg, waterAreas, cps,
                        hmRes, mapW, mapD, blobRadius, blobStride, riverFallbackType);
                    if (riverStamps > 0)
                    {
                        riversAdded++;
                        totalStamps += riverStamps;
                        Log($"  river[{i}]: {cps.Count} cps → {riverStamps} stamps merged");
                    }
                }

                Log($"BuildAndAddForAllRivers: {riversAdded} river(s), {totalStamps} stamps " +
                    $"(blobRadius={blobRadius}, blobStride={blobStride}). " +
                    $"waterAreas {waterAreasBefore} → {waterAreas.Count}; bounds tracked: {RiverWaterAreaBounds.Count}");
                return riversAdded;
            }
            catch (Exception ex)
            {
                Log($"BuildAndAddForAllRivers exception: {ex}");
                return 0;
            }
        }

        /// <summary>Walk through cps, interpolating intermediate stamp
        /// positions at <paramref name="stride"/> cell spacing. Each
        /// position becomes one disc-stamp call to
        /// <see cref="AddWaterAreaWithPanguMerge"/>.</summary>
        private static int StampAlongPath(TerrainGenerator tg, IList waterAreas, IList cps,
            int hmRes, int mapW, int mapD, int radius, int stride,
            UnityEngine.Object riverFallbackType)
        {
            int stamps = 0;
            int prevHx = -999, prevHz = -999;
            bool havePrev = false;

            for (int idx = 0; idx < cps.Count; idx++)
            {
                var pt = cps[idx];
                if (pt == null) continue;
                if (!TryGetPointXZ(pt, out float wx, out float wz)) continue;
                int hx = WorldToHmX(wx, mapW, hmRes);
                int hz = WorldToHmZ(wz, mapD, hmRes);
                RiverCpCells.Add(new CellCoord(hx, hz));

                if (havePrev)
                {
                    int dx = hx - prevHx, dz = hz - prevHz;
                    float dist = Mathf.Sqrt(dx * dx + dz * dz);
                    int steps = Mathf.Max(1, Mathf.CeilToInt(dist / stride));
                    for (int s = 1; s <= steps; s++)
                    {
                        float t = (float)s / steps;
                        int sx = Mathf.RoundToInt(Mathf.Lerp(prevHx, hx, t));
                        int sz = Mathf.RoundToInt(Mathf.Lerp(prevHz, hz, t));
                        if (sx < 0 || sx >= hmRes || sz < 0 || sz >= hmRes) continue;
                        if (AddWaterAreaWithPanguMerge(tg, waterAreas,
                                sx, sz, radius, hmRes, riverFallbackType) >= 0)
                            stamps++;
                    }
                }
                else
                {
                    if (hx >= 0 && hx < hmRes && hz >= 0 && hz < hmRes)
                    {
                        if (AddWaterAreaWithPanguMerge(tg, waterAreas,
                                hx, hz, radius, hmRes, riverFallbackType) >= 0)
                            stamps++;
                    }
                }

                prevHx = hx; prevHz = hz;
                havePrev = true;
            }
            return stamps;
        }

        /// <summary>Stamp a disc polygon centered at (cx, cz) of radius
        /// <paramref name="radius"/>. Find all existing waterAreas whose
        /// bbox overlaps our stamp + 1-cell padding (transitively expanded).
        /// Build a unioned polygon from all of them + the stamp; remove the
        /// merged-away entries; add the new merged entry. Returns the new
        /// entry's index in <paramref name="waterAreas"/>, or -1 on failure.
        ///
        /// Mirrors Pangu's <c>MergeAndAddWaterArea</c> at line 7487 of his
        /// decompiled source.</summary>
        private static int AddWaterAreaWithPanguMerge(TerrainGenerator tg, IList waterAreas,
            int cx, int cz, int radius, int hmRes,
            UnityEngine.Object riverFallbackType)
        {
            try
            {
                // 1) Stamp bbox (clamped to map)
                int sMinX = Mathf.Max(0, cx - radius);
                int sMinZ = Mathf.Max(0, cz - radius);
                int sMaxX = Mathf.Min(hmRes - 1, cx + radius);
                int sMaxZ = Mathf.Min(hmRes - 1, cz + radius);
                if (sMaxX < sMinX || sMaxZ < sMinZ) return -1;

                const int padding = 1;

                // 2) Find merge set — NON-transitive, bbox + cell adjacency.
                //
                // CRITICAL: we compare each candidate against the STAMP's
                // bbox (sMin/sMax) — NOT the growing union's bbox. A
                // transitive cascade with growing-bbox compare creates a
                // chain reaction across map: stamp ∪ lake1 → bbox grows
                // to include lake1 → lake2 (adjacent to lake1) gets pulled
                // in → lake3 (adjacent to lake2) too → eventually the
                // whole map merges into one super-polygon. v0.2 first cut
                // hit this with `waterAreas 48 → 1` and broken fishing.
                //
                // Bbox overlap is a cheap pre-filter; cell adjacency is
                // the precise check (a long-thin polygon's bbox might
                // overlap our stamp without any of its true cells being
                // anywhere near us).
                var mergeIndices = new HashSet<int>();
                int uMinX = sMinX, uMinZ = sMinZ, uMaxX = sMaxX, uMaxZ = sMaxZ;
                for (int i = 0; i < waterAreas.Count; i++)
                {
                    var entry = waterAreas[i];
                    if (entry == null) continue;
                    int eMinX = (int)_faMinX!.GetValue(entry);
                    int eMinZ = (int)_faMinZ!.GetValue(entry);
                    int eMaxX = (int)_faMaxX!.GetValue(entry);
                    int eMaxZ = (int)_faMaxZ!.GetValue(entry);
                    // Bbox pre-filter against STAMP's bbox + padding
                    if (eMinX > sMaxX + padding || eMaxX < sMinX - padding ||
                        eMinZ > sMaxZ + padding || eMaxZ < sMinZ - padding)
                        continue;
                    // Cell-adjacency precise check: does the polygon have
                    // a true cell within (radius + padding) of stamp center?
                    var ePts = _faPoints!.GetValue(entry) as bool[,];
                    if (ePts == null) continue;
                    if (!StampTouchesPolygon(cx, cz, radius, padding,
                            ePts, eMinX, eMinZ, eMaxX, eMaxZ))
                        continue;
                    mergeIndices.Add(i);
                    uMinX = Math.Min(uMinX, eMinX);
                    uMinZ = Math.Min(uMinZ, eMinZ);
                    uMaxX = Math.Max(uMaxX, eMaxX);
                    uMaxZ = Math.Max(uMaxZ, eMaxZ);
                }

                // 3) Allocate union mask sized to merged bbox
                int gw = uMaxX - uMinX + 1;
                int gh = uMaxZ - uMinZ + 1;
                bool[,] union = new bool[gw, gh];

                // 4) Copy each merged area's mask into the union; pick WaterType
                UnityEngine.Object? mergedWaterType = null;
                foreach (int idx in mergeIndices)
                {
                    var entry = waterAreas[idx];
                    if (entry == null) continue;
                    int eMinX = (int)_faMinX!.GetValue(entry);
                    int eMinZ = (int)_faMinZ!.GetValue(entry);
                    var pts = _faPoints!.GetValue(entry) as bool[,];
                    if (pts == null) continue;
                    int pw = pts.GetLength(0), ph = pts.GetLength(1);
                    for (int z = 0; z < ph; z++)
                        for (int x = 0; x < pw; x++)
                            if (pts[x, z])
                            {
                                int gx = (eMinX + x) - uMinX;
                                int gz = (eMinZ + z) - uMinZ;
                                if (gx >= 0 && gx < gw && gz >= 0 && gz < gh)
                                    union[gx, gz] = true;
                            }
                    var wt = _faWaterType!.GetValue(entry) as UnityEngine.Object;
                    // Prefer non-river-fallback WaterType (typically a lake/ocean's)
                    if (wt != null && wt != riverFallbackType && mergedWaterType == null)
                        mergedWaterType = wt;
                }
                if (mergedWaterType == null) mergedWaterType = riverFallbackType;

                // 5) Stamp our disc into the union
                int r2 = radius * radius;
                for (int dz = -radius; dz <= radius; dz++)
                {
                    int z = cz + dz;
                    if (z < 0 || z >= hmRes) continue;
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (dx * dx + dz * dz > r2) continue;
                        int x = cx + dx;
                        if (x < 0 || x >= hmRes) continue;
                        int gx = x - uMinX;
                        int gz = z - uMinZ;
                        if (gx >= 0 && gx < gw && gz >= 0 && gz < gh)
                            union[gx, gz] = true;
                    }
                }

                // 6) Recompute edge[] + shore[] from union mask
                var edges = new List<object>();
                var shores = new List<object>();
                for (int m = uMinZ; m <= uMaxZ; m++)
                {
                    for (int n = uMinX; n <= uMaxX; n++)
                    {
                        int gx = n - uMinX, gz = m - uMinZ;
                        if (!union[gx, gz]) continue;
                        bool n_w = IsMaskFilled(union, uMinX, uMinZ, uMaxX, uMaxZ, n - 1, m);
                        bool n_e = IsMaskFilled(union, uMinX, uMinZ, uMaxX, uMaxZ, n + 1, m);
                        bool n_s = IsMaskFilled(union, uMinX, uMinZ, uMaxX, uMaxZ, n, m - 1);
                        bool n_n = IsMaskFilled(union, uMinX, uMinZ, uMaxX, uMaxZ, n, m + 1);
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

                // 7) Build merged WaterArea (boxed struct)
                object area = Activator.CreateInstance(_waterAreaType!)!;
                _faWaterType!.SetValue(area, mergedWaterType);
                _faPoints!.SetValue(area, union);
                _faEdge!.SetValue(area, edgesArr);
                _faShore!.SetValue(area, shoresArr);
                _faMinX!.SetValue(area, uMinX);
                _faMinZ!.SetValue(area, uMinZ);
                _faMaxX!.SetValue(area, uMaxX);
                _faMaxZ!.SetValue(area, uMaxZ);

                // 8) Remove merged-away entries (descending order) + drop their bounds keys
                if (mergeIndices.Count > 0)
                {
                    foreach (int idx in mergeIndices)
                    {
                        var entry = waterAreas[idx];
                        if (entry == null) continue;
                        int eMinX = (int)_faMinX!.GetValue(entry);
                        int eMinZ = (int)_faMinZ!.GetValue(entry);
                        int eMaxX = (int)_faMaxX!.GetValue(entry);
                        int eMaxZ = (int)_faMaxZ!.GetValue(entry);
                        RiverWaterAreaBounds.Remove(new WaterAreaBoundsKey(eMinX, eMinZ, eMaxX, eMaxZ));
                    }
                    var sortedIndices = new List<int>(mergeIndices);
                    sortedIndices.Sort();
                    for (int k = sortedIndices.Count - 1; k >= 0; k--)
                        waterAreas.RemoveAt(sortedIndices[k]);
                }

                // 9) Add the merged entry + register its bbox
                waterAreas.Add(area);
                int newIdx = waterAreas.Count - 1;
                RiverWaterAreaBounds.Add(new WaterAreaBoundsKey(uMinX, uMinZ, uMaxX, uMaxZ));
                return newIdx;
            }
            catch (Exception ex)
            {
                Log($"AddWaterAreaWithPanguMerge exception: {ex.Message}");
                return -1;
            }
        }

        /// <summary>Walk the sidecar's cp data and stamp polygons directly,
        /// bypassing <c>_generationData.rivers</c> (which FF clears on save-
        /// load). Used by BTS03 postfix on reload to re-add the river
        /// polygons that FF's loader didn't deserialize. Mirrors gen-time
        /// <see cref="BuildAndAddForAllRivers"/> walk-and-stamp logic but
        /// reads from sidecar data instead of FF's rivers list.</summary>
        public static int BuildAndAddFromSidecar(TerrainGenerator tg,
            List<RiverPersistence.RiverData> sidecarRivers)
        {
            try
            {
                if (!ResolveTypes()) return 0;
                if (sidecarRivers == null || sidecarRivers.Count == 0) return 0;

                int blobRadius = RiversRestoredMod.RiverBlobRadius?.Value ?? 3;
                int blobStride = RiversRestoredMod.RiverBlobStride?.Value ?? 3;
                if (blobRadius < 1) blobRadius = 1;
                if (blobStride < 1) blobStride = 1;

                var msField = AccessTools.Field(typeof(TerrainGenerator), "mapSettings");
                var ms = msField?.GetValue(tg);
                if (ms == null) { Log("BuildAndAddFromSidecar: mapSettings null"); return 0; }
                var msType = ms.GetType();
                int hmRes = (int)(msType.GetField("heightmapResolution")?.GetValue(ms) ?? 0);
                int mapW = (int)(msType.GetField("width")?.GetValue(ms) ?? 0);
                int mapD = (int)(msType.GetField("depth")?.GetValue(ms) ?? 0);
                if (hmRes <= 0 || mapW <= 0 || mapD <= 0) return 0;

                var gdField = AccessTools.Field(typeof(TerrainGenerator), "_generationData");
                var gd = gdField?.GetValue(tg);
                if (gd == null) { Log("BuildAndAddFromSidecar: _generationData null"); return 0; }
                var waterAreasField = gd.GetType().GetField("waterAreas",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var waterAreas = waterAreasField?.GetValue(gd) as IList;
                if (waterAreas == null) { Log("BuildAndAddFromSidecar: waterAreas null"); return 0; }

                var riverFallbackType = ResolveRiverWaterType(tg);
                if (riverFallbackType == null) { Log("BuildAndAddFromSidecar: no WaterType"); return 0; }

                int riversAdded = 0;
                int totalStamps = 0;
                int waterAreasBefore = waterAreas.Count;
                for (int i = 0; i < sidecarRivers.Count; i++)
                {
                    var rd = sidecarRivers[i];
                    if (rd == null || rd.Points.Count < 2) continue;

                    int stamps = StampVector3Path(tg, waterAreas, rd.Points,
                        hmRes, mapW, mapD, blobRadius, blobStride, riverFallbackType);
                    if (stamps > 0)
                    {
                        riversAdded++;
                        totalStamps += stamps;
                        Log($"  sidecar river[{i}]: {rd.Points.Count} cps → {stamps} stamps merged");
                    }
                }

                Log($"BuildAndAddFromSidecar: {riversAdded} river(s), {totalStamps} stamps " +
                    $"(blobRadius={blobRadius}, blobStride={blobStride}). " +
                    $"waterAreas {waterAreasBefore} → {waterAreas.Count}; bounds tracked: {RiverWaterAreaBounds.Count}");
                return riversAdded;
            }
            catch (Exception ex)
            {
                Log($"BuildAndAddFromSidecar exception: {ex}");
                return 0;
            }
        }

        /// <summary>Walk a list of Vector3 cps (from sidecar) and stamp at
        /// stride spacing. Same as <see cref="StampAlongPath"/> but reads
        /// from RiverPersistence.PointData directly instead of via
        /// reflection.</summary>
        private static int StampVector3Path(TerrainGenerator tg, IList waterAreas,
            List<RiverPersistence.PointData> cps,
            int hmRes, int mapW, int mapD, int radius, int stride,
            UnityEngine.Object riverFallbackType)
        {
            int stamps = 0;
            int prevHx = -999, prevHz = -999;
            bool havePrev = false;

            for (int idx = 0; idx < cps.Count; idx++)
            {
                var p = cps[idx];
                int hx = WorldToHmX(p.Pos.x, mapW, hmRes);
                int hz = WorldToHmZ(p.Pos.z, mapD, hmRes);
                RiverCpCells.Add(new CellCoord(hx, hz));

                if (havePrev)
                {
                    int dx = hx - prevHx, dz = hz - prevHz;
                    float dist = Mathf.Sqrt(dx * dx + dz * dz);
                    int steps = Mathf.Max(1, Mathf.CeilToInt(dist / stride));
                    for (int s = 1; s <= steps; s++)
                    {
                        float t = (float)s / steps;
                        int sx = Mathf.RoundToInt(Mathf.Lerp(prevHx, hx, t));
                        int sz = Mathf.RoundToInt(Mathf.Lerp(prevHz, hz, t));
                        if (sx < 0 || sx >= hmRes || sz < 0 || sz >= hmRes) continue;
                        if (AddWaterAreaWithPanguMerge(tg, waterAreas,
                                sx, sz, radius, hmRes, riverFallbackType) >= 0)
                            stamps++;
                    }
                }
                else
                {
                    if (hx >= 0 && hx < hmRes && hz >= 0 && hz < hmRes)
                    {
                        if (AddWaterAreaWithPanguMerge(tg, waterAreas,
                                hx, hz, radius, hmRes, riverFallbackType) >= 0)
                            stamps++;
                    }
                }

                prevHx = hx; prevHz = hz;
                havePrev = true;
            }
            return stamps;
        }

        /// <summary>Re-populate <see cref="RiverWaterAreaBounds"/> + <see cref="RiverCpCells"/>
        /// from saved sidecar river control points. Used on cold reload to
        /// bootstrap the bounds-tracking HashSet (which is in static memory
        /// and gets cleared on game restart). Doesn't add anything to
        /// <c>waterAreas</c> — those entries already round-tripped via FF's
        /// serializer.
        ///
        /// Computes the same bbox the gen-time stamp loop would produce
        /// (every stamp position contributes ±radius cells). cps are tracked
        /// individually so cp-containment matching can identify saved
        /// waterAreas whose bbox contains the river path even when our
        /// rasterized bbox doesn't match (e.g., when our polygon merged
        /// with a lake at gen).</summary>
        public static int PopulateBoundsFromSidecar(
            List<RiverPersistence.RiverData> rivers, TerrainGenerator tg)
        {
            try
            {
                if (!ResolveTypes()) return 0;

                int blobRadius = RiversRestoredMod.RiverBlobRadius?.Value ?? 3;
                int blobStride = RiversRestoredMod.RiverBlobStride?.Value ?? 3;
                if (blobRadius < 1) blobRadius = 1;
                if (blobStride < 1) blobStride = 1;

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

                    int bMinX = int.MaxValue, bMinZ = int.MaxValue;
                    int bMaxX = int.MinValue, bMaxZ = int.MinValue;
                    int prevHx = -999, prevHz = -999;
                    bool havePrev = false;

                    foreach (var p in rd.Points)
                    {
                        int hx = WorldToHmX(p.Pos.x, mapW, hmRes);
                        int hz = WorldToHmZ(p.Pos.z, mapD, hmRes);
                        if (RiverCpCells.Add(new CellCoord(hx, hz))) cpsTracked++;
                        ExtendBbox(hx, hz, blobRadius, hmRes, ref bMinX, ref bMinZ, ref bMaxX, ref bMaxZ);

                        if (havePrev)
                        {
                            int dx = hx - prevHx, dz = hz - prevHz;
                            float dist = Mathf.Sqrt(dx * dx + dz * dz);
                            int steps = Mathf.Max(1, Mathf.CeilToInt(dist / blobStride));
                            for (int s = 1; s <= steps; s++)
                            {
                                float t = (float)s / steps;
                                int sx = Mathf.RoundToInt(Mathf.Lerp(prevHx, hx, t));
                                int sz = Mathf.RoundToInt(Mathf.Lerp(prevHz, hz, t));
                                ExtendBbox(sx, sz, blobRadius, hmRes,
                                    ref bMinX, ref bMinZ, ref bMaxX, ref bMaxZ);
                            }
                        }

                        prevHx = hx; prevHz = hz;
                        havePrev = true;
                    }
                    if (bMinX > bMaxX || bMinZ > bMaxZ) continue;
                    var key = new WaterAreaBoundsKey(bMinX, bMinZ, bMaxX, bMaxZ);
                    RiverWaterAreaBounds.Add(key);
                    populated++;
                    Log($"  re-registered river bounds [{bMinX},{bMinZ}..{bMaxX},{bMaxZ}]");
                }
                Log($"PopulateBoundsFromSidecar: tracked {cpsTracked} cp cells across {populated} rivers");
                return populated;
            }
            catch (Exception ex)
            {
                Log($"PopulateBoundsFromSidecar exception: {ex.Message}");
                return 0;
            }
        }

        private static void ExtendBbox(int cx, int cz, int r, int hmRes,
            ref int minX, ref int minZ, ref int maxX, ref int maxZ)
        {
            int x0 = Mathf.Max(0, cx - r);
            int z0 = Mathf.Max(0, cz - r);
            int x1 = Mathf.Min(hmRes - 1, cx + r);
            int z1 = Mathf.Min(hmRes - 1, cz + r);
            if (x0 < minX) minX = x0;
            if (z0 < minZ) minZ = z0;
            if (x1 > maxX) maxX = x1;
            if (z1 > maxZ) maxZ = z1;
        }

        // ── Reflection setup ────────────────────────────────────────────────
        private static bool ResolveTypes()
        {
            if (_waterAreaType != null) return true;
            _waterAreaType = AccessTools.TypeByName("TerrainGen.TerrainGenerator+WaterArea");
            _waterEdgeType = AccessTools.TypeByName("TerrainGen.TerrainGenerator+WaterEdge");
            _waterTypeType = AccessTools.TypeByName("TerrainGen.WaterType");
            var pairOpen = AccessTools.TypeByName("Pair`2");
            if (pairOpen != null)
                _pairIntIntType = pairOpen.MakeGenericType(typeof(int), typeof(int));
            if (_waterAreaType == null || _waterEdgeType == null ||
                _pairIntIntType == null || _waterTypeType == null)
            {
                Log($"ResolveTypes failed: WA={_waterAreaType != null} WE={_waterEdgeType != null} " +
                    $"Pair={_pairIntIntType != null} WT={_waterTypeType != null}");
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
            if (_pairCtor == null) { Log("ResolveTypes: Pair<int,int> ctor missing"); return false; }
            return true;
        }

        /// <summary>Pick a WaterType for the river fallback. Preferred path:
        /// borrow from <c>waterSettings.lakeTypes[0]</c> — that list is
        /// serialized with the map (see WaterSettings.Save), so the
        /// waterMaterial / foamMaterial references survive save/reload.
        /// WaterTypes from <c>Resources.FindObjectsOfTypeAll</c> that aren't
        /// in lakeTypes don't get saved → on reload the SO is recreated empty
        /// and our river WaterChunks render invisible.</summary>
        private static UnityEngine.Object? ResolveRiverWaterType(TerrainGenerator tg)
        {
            if (_cachedRiverWaterType != null) return _cachedRiverWaterType;
            try
            {
                var wsField = tg.GetType().GetField("waterSettings",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var ws = wsField?.GetValue(tg);
                if (ws != null)
                {
                    var lakeTypesField = ws.GetType().GetField("lakeTypes",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var lakeTypes = lakeTypesField?.GetValue(ws) as IList;
                    if (lakeTypes != null && lakeTypes.Count > 0 && lakeTypes[0] is UnityEngine.Object first)
                    {
                        _cachedRiverWaterType = first;
                        Log($"ResolveRiverWaterType: borrowing waterSettings.lakeTypes[0] = '{first.name}' " +
                            "(serialized with map → survives save/reload).");
                        return first;
                    }
                }

                // Fallback: scan loaded WaterType SOs (may not survive save/reload)
                var all = UnityEngine.Resources.FindObjectsOfTypeAll(_waterTypeType!);
                if (all == null || all.Length == 0) return null;
                string[] preferred = { "LakeSmall", "Pond", "Lake" };
                foreach (var name in preferred)
                {
                    foreach (var wt in all)
                    {
                        if (wt != null && string.Equals(wt.name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            _cachedRiverWaterType = wt;
                            Log($"ResolveRiverWaterType: fallback Resources scan picked '{wt.name}' " +
                                "(may not survive save/reload — prefer lakeTypes path).");
                            return wt;
                        }
                    }
                }
                foreach (var wt in all)
                {
                    if (wt == null) continue;
                    string n = wt.name ?? "";
                    if (n.IndexOf("ocean", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    _cachedRiverWaterType = wt;
                    Log($"ResolveRiverWaterType: last-resort non-ocean '{wt.name}'");
                    return wt;
                }
                return null;
            }
            catch (Exception ex)
            {
                Log($"ResolveRiverWaterType exception: {ex.Message}");
                return null;
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────
        /// <summary>True iff the candidate polygon (mask + bounds) has a
        /// true cell within (radius + padding) of stamp center (cx, cz).
        /// Used as the precise adjacency check after the bbox pre-filter.
        /// Stops the merge from chaining through faraway polygons whose
        /// bbox happens to overlap a long-thin polygon's bbox without
        /// actually being near our stamp.</summary>
        private static bool StampTouchesPolygon(int cx, int cz, int radius, int padding,
            bool[,] mask, int eMinX, int eMinZ, int eMaxX, int eMaxZ)
        {
            int rp = radius + padding;
            int rp2 = rp * rp;
            int pw = mask.GetLength(0), ph = mask.GetLength(1);
            int minX = cx - rp, maxX = cx + rp;
            int minZ = cz - rp, maxZ = cz + rp;
            // Clip to polygon bounds
            if (maxX < eMinX || minX > eMaxX || maxZ < eMinZ || minZ > eMaxZ) return false;
            int x0 = Math.Max(minX, eMinX), x1 = Math.Min(maxX, eMaxX);
            int z0 = Math.Max(minZ, eMinZ), z1 = Math.Min(maxZ, eMaxZ);
            for (int z = z0; z <= z1; z++)
            {
                int dz = z - cz;
                for (int x = x0; x <= x1; x++)
                {
                    int dx = x - cx;
                    if (dx * dx + dz * dz > rp2) continue;
                    int gx = x - eMinX, gz = z - eMinZ;
                    if (gx < 0 || gx >= pw || gz < 0 || gz >= ph) continue;
                    if (mask[gx, gz]) return true;
                }
            }
            return false;
        }

        private static bool IsMaskFilled(bool[,] mask, int gMinX, int gMinZ,
                                          int gMaxX, int gMaxZ, int x, int z)
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
