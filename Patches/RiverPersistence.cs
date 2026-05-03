using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TerrainGen;
using UnityEngine;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Persistence layer for rivers across save/load.
    ///
    /// FF doesn't natively serialize the river path data (`_generationData.rivers`)
    /// or the WaterPath GameObjects that Stage 60 instantiates. We solve this by:
    ///
    ///  1. **At save**: Harmony postfix on SaveManager.Save captures the save
    ///     name; we write a `.rivers` sidecar binary file alongside the .map
    ///     containing every river's control points (pos / height / width).
    ///
    ///  2. **At load**: when our existing IsLoadingSavedMap check trips, we
    ///     read the sidecar (looked up via `SaveManager.activeSaveFileName`),
    ///     restore the rivers list onto `_generationData.rivers`, and re-invoke
    ///     Stage 60 — which (despite NREing on the carve) successfully spawns
    ///     the WaterPath visual GameObjects before the exception.
    ///
    /// Sidecar format (binary):
    ///   int32 magic   = 0x52525452 ("RRTR")
    ///   int32 version = 1
    ///   int32 numRivers
    ///   per river:
    ///     int32 numPoints
    ///     per point:
    ///       float posX, posY, posZ   // Vector3
    ///       float height
    ///       float width
    /// </summary>
    internal static class RiverPersistence
    {
        const int SIDECAR_MAGIC = 0x52525452; // 'RRTR' (LE: little-endian artist's sigil)
        // Sidecar format history:
        //   v1 — cps only (pos, height, width)
        //   v2 — adds AnimationCurves per river
        //   v3 — adds embedded polygon shapes per river (bbox, mask, waterTypeName).
        //        On reload, BTS03 deserializes the polygons and slots them
        //        directly into _generationData.waterAreas, skipping the
        //        expensive walk-and-stamp + cell-adjacency merge that v1/v2
        //        had to run. Reload time drops from seconds to milliseconds.
        //        v1/v2 sidecars are still readable (we fall back to walk-and-stamp).
        const int SIDECAR_VERSION = 3;
        const string SIDECAR_EXTENSION = ".rivers";

        // Restoration is gated behind these flags so we only restore once per
        // load and we don't fight the carver/injection guards.
        public static bool RestorePending { get; private set; } = false;
        public static string? PendingSaveName { get; private set; } = null;
        // PERMANENT per-scene flag — once a restore succeeds we don't try
        // again until the next scene load. Without this, OnUpdate would
        // re-detect the save-load every frame and re-invoke Stage 60
        // repeatedly, corrupting render state.
        public static bool RestoredThisLoad { get; private set; } = false;

        /// <summary>Cached sidecar river data, populated either at gen-time
        /// save (we read what we wrote) or at BTS03 reload (we read the
        /// loaded sidecar). Used as a fallback in SavePostfix when
        /// <c>_generationData.rivers</c> is empty — FF clears that list on
        /// save-load, so subsequent saves after a reload would skip the
        /// sidecar write without this cache.</summary>
        public static List<RiverData>? CachedSidecarData { get; private set; } = null;

        /// <summary>Reset on scene load so the next save load can restore again.</summary>
        public static void ResetForSceneLoad()
        {
            RestorePending = false;
            PendingSaveName = null;
            RestoredThisLoad = false;
            CachedSidecarData = null;
        }

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                Type? smType = AccessTools.TypeByName("SaveManager");
                if (smType == null)
                {
                    Log("SaveManager type not found — persistence disabled.");
                    return;
                }

                // Save hook — Pangu's exact signature: (string, bool, bool)
                MethodInfo? saveMI = AccessTools.Method(smType, "Save",
                    new[] { typeof(string), typeof(bool), typeof(bool) });
                if (saveMI != null)
                {
                    var preStub = typeof(RiverPersistence).GetMethod(nameof(SavePrefix),
                        BindingFlags.Static | BindingFlags.NonPublic);
                    var postStub = typeof(RiverPersistence).GetMethod(nameof(SavePostfix),
                        BindingFlags.Static | BindingFlags.NonPublic);
                    harmony.Patch(saveMI,
                        prefix: new HarmonyMethod(preStub),
                        postfix: new HarmonyMethod(postStub));
                    Log($"Hooked SaveManager.Save(string, bool, bool) — prefix dumps waterAreas, postfix writes sidecar");
                }
                else
                {
                    Log("SaveManager.Save(string, bool, bool) not found — sidecar writes disabled.");
                }

                // ── Restore hook ─────────────────────────────────────────────
                // Hook Terrain2Builder.BuildTerrainShared03 with a postfix.
                // This is the SAME method FF uses to spawn river WaterPath
                // visuals during fresh gen and save-load (it's called from
                // BuildTerrainLoadedGame). On save load, it iterates an
                // empty `_generationData.rivers` so no rivers spawn — but
                // the call to TerrainGenerator.CreateBucket("Rivers") at
                // its head DESTROYS all children of any existing "Rivers"
                // bucket. That's why our earlier OnUpdate-driven spawn
                // produced invisible water: we spawned BEFORE this method
                // ran, and our WaterPaths were obliterated by CreateBucket.
                //
                // Hooking a postfix here gives us the right timing — after
                // FF has cleared and (no-op) populated the bucket, we
                // Instantiate fresh WaterPaths from the sidecar, and they
                // persist because nothing else touches them.
                Type? builderType = AccessTools.TypeByName("Terrain2Builder");
                if (builderType != null)
                {
                    MethodInfo? bts03 = AccessTools.Method(builderType, "BuildTerrainShared03");
                    if (bts03 != null)
                    {
                        var stub = typeof(RiverPersistence).GetMethod(
                            nameof(BuildTerrainShared03Postfix),
                            BindingFlags.Static | BindingFlags.NonPublic);
                        harmony.Patch(bts03, postfix: new HarmonyMethod(stub));
                        Log("Hooked Terrain2Builder.BuildTerrainShared03 (post-load river respawn point).");
                    }
                    else
                    {
                        Log("Terrain2Builder.BuildTerrainShared03 not found — restore disabled.");
                    }
                }
                else
                {
                    Log("Terrain2Builder type not found — restore disabled.");
                }
            }
            catch (Exception ex)
            {
                Log($"Apply failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix on Terrain2Builder.BuildTerrainShared03 — fires AFTER FF
        /// has cleared the "Rivers" bucket and (no-op) iterated its empty
        /// generationData.rivers. Spawns fresh WaterPath visuals from the
        /// sidecar. Idempotent via RestoredThisLoad latch.
        ///
        /// Vanilla also calls this on FRESH gen (with a populated rivers
        /// list, so it spawns naturally). We gate to save-load only via
        /// IsLoadingSavedMap so we don't double-spawn on fresh gen.
        /// </summary>
        private static void BuildTerrainShared03Postfix(Component __instance)
        {
            try
            {
                if (RestoredThisLoad) return;

                var tgType = AccessTools.TypeByName("TerrainGen.TerrainGenerator");
                if (tgType == null) { Log("BTS03 postfix: TerrainGenerator type missing"); return; }
                var tgComp = __instance.GetComponent(tgType);
                var tg = tgComp as TerrainGenerator ?? RiverSettingsPatch.CachedGenerator;
                if (tg == null) { Log("BTS03 postfix: TerrainGenerator instance not found"); return; }

                // Skip on fresh gen — vanilla code already spawned the rivers
                // from the populated _generationData.rivers list.
                if (!RiverSettingsPatch.IsLoadingSavedMap(tg)) return;

                Log("BTS03 postfix: save-load detected, attempting river restore…");

                // Diagnostic: dump post-reload waterAreas state so we can
                // see whether our river WaterAreas survived FF's
                // deserialization round-trip and what bounds they have.
                try
                {
                    var gdField2 = AccessTools.Field(typeof(TerrainGenerator), "_generationData");
                    var gd2 = gdField2?.GetValue(tg);
                    if (gd2 != null)
                    {
                        var waField = gd2.GetType().GetField("waterAreas",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var was = waField?.GetValue(gd2) as System.Collections.IList;
                        Log($"BTS03 postfix: post-reload waterAreas.Count = {was?.Count ?? -1}");
                        if (was != null)
                        {
                            for (int i = 0; i < was.Count; i++)
                            {
                                var area = was[i];
                                if (area == null) continue;
                                Type at = area.GetType();
                                int aMinX = (int)(at.GetField("minX")?.GetValue(area) ?? -1);
                                int aMinZ = (int)(at.GetField("minZ")?.GetValue(area) ?? -1);
                                int aMaxX = (int)(at.GetField("maxX")?.GetValue(area) ?? -1);
                                int aMaxZ = (int)(at.GetField("maxZ")?.GetValue(area) ?? -1);
                                var wt = at.GetField("waterType")?.GetValue(area) as UnityEngine.Object;
                                var pts = at.GetField("points")?.GetValue(area) as System.Array;
                                int ptsW = pts?.GetLength(0) ?? -1;
                                int ptsH = pts?.GetLength(1) ?? -1;
                                var edge = at.GetField("edge")?.GetValue(area) as System.Array;
                                var shore = at.GetField("shore")?.GetValue(area) as System.Array;
                                Log($"  WA[{i}] wt='{wt?.name ?? "NULL"}' bounds=[{aMinX},{aMinZ}..{aMaxX},{aMaxZ}] " +
                                    $"points={ptsW}x{ptsH} edge={edge?.Length ?? -1} shore={shore?.Length ?? -1}");
                            }
                        }
                    }
                }
                catch (Exception dex) { Log($"  diag-dump exception: {dex.Message}"); }

                string? saveName = PendingSaveName;
                if (string.IsNullOrEmpty(saveName)) saveName = TryFindLoadedSaveName();
                if (string.IsNullOrEmpty(saveName))
                {
                    Log("BTS03 postfix: no save name resolvable — skipping.");
                    RestoredThisLoad = true;
                    return;
                }

                Type? smType = AccessTools.TypeByName("SaveManager");
                if (smType == null) { Log("BTS03 postfix: SaveManager type missing"); return; }
                var smInstance = UnityEngine.Object.FindObjectOfType(smType);
                if (smInstance == null) { Log("BTS03 postfix: SaveManager instance not found"); return; }

                string sidecarPath = ResolveSidecarPath(smInstance, saveName!, isSave: false);
                if (string.IsNullOrEmpty(sidecarPath) || !File.Exists(sidecarPath))
                {
                    Log($"BTS03 postfix: no sidecar at {sidecarPath} — vanilla save or non-Rivers map.");
                    RestoredThisLoad = true;
                    return;
                }

                var riverData = ReadSidecarRivers(sidecarPath);
                if (riverData == null || riverData.Count == 0)
                {
                    Log("BTS03 postfix: sidecar empty/unreadable — skipping.");
                    RestoredThisLoad = true;
                    return;
                }

                // Ribbon respawn is gated on EnableRibbonAnimation so the
                // perf toggle works on reload too. The static water polygon
                // (added below via walk-and-stamp) is independent.
                bool ribbonsEnabled = RiversRestoredMod.EnableRibbonAnimation?.Value ?? true;
                if (ribbonsEnabled)
                {
                    Log($"BTS03 postfix: spawning {riverData.Count} rivers from sidecar (sum {riverData.Sum(r => r.Points.Count)} points)");
                    int spawned = SpawnWaterPathsFromSidecar(riverData, tg);
                    Log($"BTS03 postfix: spawned {spawned} WaterPath visual(s) post-rebuild.");
                }
                else
                {
                    Log("BTS03 postfix: EnableRibbonAnimation=false — skipping ribbon respawn (static water plane only).");
                }

                // Cache the loaded sidecar so subsequent saves can fall back
                // to it when _generationData.rivers is empty (FF clears that
                // list on save-load — without this cache, the next save
                // would skip writing the sidecar entirely).
                CachedSidecarData = riverData;
                Log($"BTS03 postfix: cached {riverData.Count} rivers for next-save fallback.");

                // ── Reload-time water restoration ────────────────────────
                // FF's load pipeline doesn't deserialize our waterAreas
                // additions — it regenerates the list from seed. So we have
                // to re-add our polygons here. Two paths:
                //   v3 fast-path: sidecar carries the merged polygon shapes
                //     directly. Just deserialize them into waterAreas.
                //     Reload time: milliseconds.
                //   v1/v2 fallback: sidecar has only cps. Walk and stamp
                //     them through the merge logic to reconstruct the
                //     polygons. Reload time: a few seconds for long rivers.
                RiverWaterAreaBuilder.RiverWaterAreaBounds.Clear();
                var embeddedPolys = new List<PolygonData>();
                foreach (var rd in riverData)
                    if (rd?.Polygons != null) embeddedPolys.AddRange(rd.Polygons);

                int reAdded;
                if (embeddedPolys.Count > 0)
                {
                    reAdded = RiverWaterAreaBuilder.AddPrebuiltWaterAreas(tg, embeddedPolys);
                    Log($"BTS03 postfix: re-added {reAdded} river polygon(s) post-load via v3 fast-path (embedded shapes).");
                }
                else
                {
                    reAdded = RiverWaterAreaBuilder.BuildAndAddFromSidecar(tg, riverData);
                    Log($"BTS03 postfix: re-added {reAdded} river polygon(s) post-load via v1/v2 walk-and-stamp fallback.");
                }

                // ── Force WaterPlane rebuild ─────────────────────────────
                // FF's load pipeline doesn't build WaterChunk meshes for
                // the regenerated waterAreas list, AND our newly-added
                // river polygons need fresh meshes. Mirrors Pangu's
                // pattern of calling WaterPlane.Rebuild after a runtime
                // polygon add.
                ForceWaterPlaneRebuild(tg);

                RestoredThisLoad = true;
            }
            catch (Exception ex)
            {
                Log($"BuildTerrainShared03Postfix exception: {ex}");
            }
        }

        // ── Save hook ───────────────────────────────────────────────────────
        /// <summary>Prefix on SaveManager.Save — diagnostic dump of every
        /// waterArea right before FF serializes the map. We log count + per-
        /// entry summary (bbox, mask cells, waterType name, isOurs) so we can
        /// see whether our river polygons are still alive at save time, or
        /// whether some late stage stripped them between gen-add and save.
        ///
        /// If post-reload count &lt; pre-save count, FF's serializer strips
        /// some entries → we need to fix the saved-data shape.
        /// If pre-save count is already short → a late gen stage strips them
        /// → we need to re-add or hook a later carrier.</summary>
        private static void SavePrefix(object __instance, string savedGameFileNameNoExtension,
                                         bool isHighMemoryAutoSave, bool isAutoSave)
        {
            try
            {
                var tg = RiverSettingsPatch.CachedGenerator;
                if (tg == null) { Log("[SavePre] no cached TerrainGenerator"); return; }

                var gdField = AccessTools.Field(typeof(TerrainGen.TerrainGenerator), "_generationData");
                var gd = gdField?.GetValue(tg);
                if (gd == null) { Log("[SavePre] _generationData null"); return; }
                var waField = gd.GetType().GetField("waterAreas",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var was = waField?.GetValue(gd) as IList;
                if (was == null) { Log("[SavePre] waterAreas null"); return; }

                int beforeCount = was.Count;
                int trackedBounds = RiverWaterAreaBuilder.RiverWaterAreaBounds.Count;

                // Resolve WaterArea fields once
                Type? waType = AccessTools.TypeByName("TerrainGen.TerrainGenerator+WaterArea");
                if (waType == null) { Log("[SavePre] WaterArea type missing"); return; }
                var fMinX = waType.GetField("minX");
                var fMinZ = waType.GetField("minZ");
                var fMaxX = waType.GetField("maxX");
                var fMaxZ = waType.GetField("maxZ");

                int oursPresent = 0;
                if (fMinX != null && fMinZ != null && fMaxX != null && fMaxZ != null)
                {
                    for (int i = 0; i < was.Count; i++)
                    {
                        var entry = was[i];
                        if (entry == null) continue;
                        int minX = (int)fMinX.GetValue(entry);
                        int minZ = (int)fMinZ.GetValue(entry);
                        int maxX = (int)fMaxX.GetValue(entry);
                        int maxZ = (int)fMaxZ.GetValue(entry);
                        var key = new RiverWaterAreaBuilder.WaterAreaBoundsKey(minX, minZ, maxX, maxZ);
                        if (RiverWaterAreaBuilder.RiverWaterAreaBounds.Contains(key)) oursPresent++;
                    }
                }

                bool verbose = RiversRestoredMod.VerboseDiagnostics?.Value ?? false;
                if (verbose)
                    Log($"[SavePre] waterAreas.Count={beforeCount}  ours={oursPresent}/{trackedBounds}");

                // ── Defensive re-add fallback ──────────────────────────────
                // The dual Stage 38 + Stage 60 add (RiverSettingsPatch) keeps
                // our polygons alive through Stage 50's strip, so this branch
                // shouldn't fire in normal operation. It stays as a safety
                // net in case a future gen-pipeline change re-introduces a
                // strip — log loudly so we notice.
                if (oursPresent < trackedBounds && trackedBounds > 0)
                {
                    Log($"[SavePre] {trackedBounds - oursPresent} river polygon(s) missing at save time — re-adding via walk-and-stamp.");
                    RiverWaterAreaBuilder.RiverWaterAreaBounds.Clear();
                    int reAdded = RiverWaterAreaBuilder.BuildAndAddForAllRivers(tg);
                    int afterCount = was.Count;
                    Log($"[SavePre] Re-add result: {reAdded} river polygon(s) re-stamped. waterAreas {beforeCount} → {afterCount}.");
                }
            }
            catch (Exception ex)
            {
                Log($"SavePrefix exception: {ex}");
            }
        }

        /// <summary>Postfix on SaveManager.Save — write our river sidecar
        /// alongside FF's .map file.</summary>
        private static void SavePostfix(object __instance, string savedGameFileNameNoExtension,
                                          bool isHighMemoryAutoSave, bool isAutoSave)
        {
            try
            {
                if (string.IsNullOrEmpty(savedGameFileNameNoExtension)) return;

                var tg = RiverSettingsPatch.CachedGenerator;
                if (tg == null) { Log("Save: no cached TerrainGenerator — skipping."); return; }

                string sidecarPath = ResolveSidecarPath(__instance, savedGameFileNameNoExtension, isSave: true);
                if (string.IsNullOrEmpty(sidecarPath))
                {
                    Log("Save: could not resolve sidecar path — skipping.");
                    return;
                }

                // Prefer live _generationData.rivers (fresh gen has it populated).
                // Fall back to CachedSidecarData for second-save-after-reload —
                // FF clears _generationData.rivers during save-load, so without
                // the cache the rivers list looks empty here and we'd skip the
                // sidecar write, losing the river data for the next reload.
                IList? rivers = GetRiversList(tg);
                List<RiverData> dataToWrite;
                string source;
                if (rivers != null && rivers.Count > 0)
                {
                    dataToWrite = ConvertLiveRiversToRiverData(rivers);
                    source = "live data";

                    // v3: capture the merged WaterArea polygon shapes from
                    // _generationData.waterAreas (matched against
                    // RiverWaterAreaBounds — those are the polygons WE added
                    // or merged with at gen time). Embedding these in the
                    // sidecar lets reload skip the expensive walk-and-stamp
                    // and just slot the polygons straight into waterAreas.
                    var capturedPolys = CaptureLivePolygonsForRivers(tg);
                    if (capturedPolys.Count > 0 && dataToWrite.Count > 0)
                    {
                        // Attach all polygons to the first river entry (FF's
                        // post-merge waterAreas don't preserve per-river
                        // ownership, so we don't try to split them up).
                        dataToWrite[0].Polygons = capturedPolys;
                        source = $"live data + {capturedPolys.Count} embedded polygon(s)";
                    }
                    // Cache what we're about to write so subsequent saves
                    // after a reload still have data even if FF clears the
                    // rivers list.
                    CachedSidecarData = dataToWrite;
                }
                else if (CachedSidecarData != null && CachedSidecarData.Count > 0)
                {
                    dataToWrite = CachedSidecarData;
                    source = "cache (FF cleared live rivers list during prior reload)";
                }
                else
                {
                    Log("Save: no rivers in _generationData and no cached sidecar — skipping (vanilla map).");
                    return;
                }
                int totalPoints = WriteSidecar(sidecarPath, dataToWrite);
                Log($"Saved {dataToWrite.Count} rivers ({totalPoints} points) from {source} → {sidecarPath}");
            }
            catch (Exception ex)
            {
                Log($"SavePostfix exception: {ex}");
            }
        }

        /// <summary>
        /// Compute the .rivers sidecar path next to FF's .map for this save.
        /// Format mirrors Pangu's path construction: `Save/<gameFolder>/<saveName>.rivers`.
        /// </summary>
        /// <summary>
        /// Compute the .rivers sidecar path. Behavior differs between save
        /// and load because <c>SaveManager.activeSaveFileName</c> is unreliable:
        ///
        /// At SAVE time, asf is often stale (still pointing at the previous
        /// save name) — so we use the bare <paramref name="saveNameOrPath"/>
        /// (which is the actual save name being written), giving a flat path
        /// like <c>Save/{saveName}.rivers</c>.
        ///
        /// At LOAD time, FF passes the canonical "{slotFolder}/{saveName}"
        /// form via both arg and asf, so we try canonical first
        /// (<c>Save/{slotFolder}/{saveName}.rivers</c>), then fall back to the
        /// flat path used by save (<c>Save/{saveName}.rivers</c>).
        /// </summary>
        private static string ResolveSidecarPath(object saveManager, string saveNameOrPath, bool isSave)
        {
            try
            {
                Type smType = saveManager.GetType();
                string folderName = (string)(smType.GetField("folderName",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?.GetValue(null) ?? "Save/");

                // Read activeSaveFileName — the canonical "{slotFolder}/{saveName}"
                string asf = (string)(smType.GetField("activeSaveFileName",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?.GetValue(null) ?? "");

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                Log($"ResolveSidecarPath ({(isSave ? "SAVE" : "LOAD")}): folderName='{folderName}'  asf='{asf}'  arg='{saveNameOrPath}'");

                if (isSave)
                {
                    // SAVE: always use the bare arg (the save name being
                    // written this very moment). asf is unreliable on save —
                    // it can still point at the previously loaded save.
                    if (string.IsNullOrEmpty(saveNameOrPath))
                    {
                        Log("  empty save name — cannot write sidecar");
                        return string.Empty;
                    }
                    string flatPath = Path.Combine(baseDir, folderName,
                        saveNameOrPath + SIDECAR_EXTENSION)
                        .Replace('/', Path.DirectorySeparatorChar);
                    string parent = Path.GetDirectoryName(flatPath) ?? "";
                    if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    {
                        Directory.CreateDirectory(parent);
                        Log($"  Created directory: {parent}");
                    }
                    Log($"  → save target: {flatPath}");
                    return flatPath;
                }

                // LOAD: prefer canonical (slot-folder path), fall back to flat.
                string identifier = !string.IsNullOrEmpty(asf) ? asf : saveNameOrPath;
                if (string.IsNullOrEmpty(identifier))
                {
                    Log("  empty identifier — cannot resolve sidecar for load");
                    return string.Empty;
                }
                string canonical = Path.Combine(baseDir, folderName,
                    identifier + SIDECAR_EXTENSION)
                    .Replace('/', Path.DirectorySeparatorChar);
                if (File.Exists(canonical))
                {
                    Log($"  → load canonical (file exists): {canonical}");
                    return canonical;
                }
                string bareName = Path.GetFileName(identifier.Replace('/', Path.DirectorySeparatorChar));
                string flatLoad = Path.Combine(baseDir, folderName, bareName + SIDECAR_EXTENSION);
                if (File.Exists(flatLoad))
                {
                    Log($"  → load flat fallback: {flatLoad}");
                    return flatLoad;
                }
                Log($"  → no sidecar found at canonical {canonical} or flat {flatLoad}");
                return canonical; // return canonical anyway so caller logs the expected path
            }
            catch (Exception ex)
            {
                Log($"ResolveSidecarPath exception: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>Serialize a RiverData list to the sidecar binary (v3
        /// format — cps + curves + embedded polygon shapes). Single writer
        /// for both gen-time live data (converted via
        /// <see cref="ConvertLiveRiversToRiverData"/>) and reload-time
        /// cached data.</summary>
        private static int WriteSidecar(string path, List<RiverData> rivers)
        {
            int totalPoints = 0;
            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(SIDECAR_MAGIC);
                bw.Write(SIDECAR_VERSION);
                bw.Write(rivers.Count);
                foreach (var rd in rivers)
                {
                    if (rd == null)
                    {
                        bw.Write(0);
                        WriteCurve(bw, null);
                        WriteCurve(bw, null);
                        bw.Write(0); // polygon count
                        continue;
                    }
                    bw.Write(rd.Points.Count);
                    foreach (var p in rd.Points)
                    {
                        bw.Write(p.Pos.x); bw.Write(p.Pos.y); bw.Write(p.Pos.z);
                        bw.Write(p.Height);
                        bw.Write(p.Width);
                        totalPoints++;
                    }
                    WriteCurve(bw, rd.TransparencyCurve);
                    WriteCurve(bw, rd.ExtinctionCurve);

                    // v3 polygon section. Empty list → 0 polygons (still
                    // valid; reload falls back to walk-and-stamp from cps).
                    int polyCount = rd.Polygons?.Count ?? 0;
                    bw.Write(polyCount);
                    if (rd.Polygons != null)
                    {
                        foreach (var poly in rd.Polygons)
                        {
                            bw.Write(poly.MinX);
                            bw.Write(poly.MinZ);
                            bw.Write(poly.MaxX);
                            bw.Write(poly.MaxZ);
                            bw.Write(poly.WaterTypeName ?? "");
                            bw.Write(poly.PackedMask.Length);
                            bw.Write(poly.PackedMask);
                        }
                    }
                }
            }
            return totalPoints;
        }

        /// <summary>Reflectively extract live FF rivers (TerrainRiver list)
        /// into the in-memory RiverData format used by both the sidecar
        /// reader and the cache. Lets us share a single binary writer for
        /// gen-time and reload-time save paths.</summary>
        private static List<RiverData> ConvertLiveRiversToRiverData(IList rivers)
        {
            var result = new List<RiverData>(rivers.Count);
            foreach (var river in rivers)
            {
                if (river == null) { result.Add(null!); continue; }
                var rd = new RiverData();
                var points = GetPointsList(river);
                if (points != null)
                {
                    foreach (var pt in points)
                    {
                        if (pt == null) continue;
                        rd.Points.Add(new PointData
                        {
                            Pos = ReadVector3(pt, "pos"),
                            Height = ReadFloat(pt, "height"),
                            Width = ReadFloat(pt, "width"),
                        });
                    }
                }
                rd.TransparencyCurve = ReadCurveField(river, "transparencyCurve");
                rd.ExtinctionCurve = ReadCurveField(river, "extinctionCurve");
                result.Add(rd);
            }
            return result;
        }

        /// <summary>Walk the live <c>_generationData.waterAreas</c> list and
        /// pull out the polygon shapes whose bbox is registered in
        /// <see cref="RiverWaterAreaBuilder.RiverWaterAreaBounds"/> — these
        /// are "ours" (rivers, possibly merged with adjacent lakes/ocean).
        /// Returns a flat list of PolygonData; the caller distributes them
        /// across RiverData entries (currently we attach them all to river[0]
        /// since FF doesn't preserve per-river → polygon ownership after the
        /// merge step).</summary>
        private static List<PolygonData> CaptureLivePolygonsForRivers(TerrainGenerator tg)
        {
            var captured = new List<PolygonData>();
            try
            {
                var gdField = AccessTools.Field(typeof(TerrainGen.TerrainGenerator), "_generationData");
                var gd = gdField?.GetValue(tg);
                if (gd == null) return captured;
                var waField = gd.GetType().GetField("waterAreas",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var was = waField?.GetValue(gd) as IList;
                if (was == null) return captured;

                Type? waType = AccessTools.TypeByName("TerrainGen.TerrainGenerator+WaterArea");
                if (waType == null) return captured;
                var fMinX = waType.GetField("minX");
                var fMinZ = waType.GetField("minZ");
                var fMaxX = waType.GetField("maxX");
                var fMaxZ = waType.GetField("maxZ");
                var fPoints = waType.GetField("points");
                var fWT = waType.GetField("waterType");
                if (fMinX == null || fMinZ == null || fMaxX == null || fMaxZ == null
                    || fPoints == null || fWT == null) return captured;

                foreach (var entry in was)
                {
                    if (entry == null) continue;
                    int minX = (int)fMinX.GetValue(entry);
                    int minZ = (int)fMinZ.GetValue(entry);
                    int maxX = (int)fMaxX.GetValue(entry);
                    int maxZ = (int)fMaxZ.GetValue(entry);
                    var key = new RiverWaterAreaBuilder.WaterAreaBoundsKey(minX, minZ, maxX, maxZ);
                    if (!RiverWaterAreaBuilder.RiverWaterAreaBounds.Contains(key)) continue;
                    var mask = fPoints.GetValue(entry) as bool[,];
                    if (mask == null) continue;
                    var wt = fWT.GetValue(entry) as UnityEngine.Object;
                    captured.Add(new PolygonData
                    {
                        MinX = minX,
                        MinZ = minZ,
                        MaxX = maxX,
                        MaxZ = maxZ,
                        WaterTypeName = wt?.name ?? "",
                        PackedMask = PolygonData.PackMask(mask),
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"CaptureLivePolygonsForRivers exception: {ex.Message}");
            }
            return captured;
        }

        /// <summary>
        /// Compute water Y the same way vanilla TerrainGenerator.OnGenerated
        /// does: mapSettings.height * baseSettings.scaling * waterSettings.height * noiseScaling.
        /// Falls back to TerrainGenerator.GetWaterHeight() or Sea Layer position
        /// if any setting is unreadable.
        /// </summary>
        private static float ComputeWaterY(TerrainGenerator tg)
        {
            try
            {
                Type tgType = tg.GetType();
                // Prefer the public method GetWaterHeight() if available
                var ghMI = tgType.GetMethod("GetWaterHeight",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ghMI != null && ghMI.GetParameters().Length == 0)
                {
                    var v = ghMI.Invoke(tg, null);
                    if (v is float f && f > 0f) return f;
                }

                // Manual computation: mapSettings.height * baseSettings.scaling * waterSettings.height * noiseScaling
                int mapH = (int)(tgType.GetField("mapSettings")?.GetValue(tg) is object ms
                    ? (ms.GetType().GetField("height")?.GetValue(ms) ?? 0) : 0);
                object? baseSettings = tgType.GetField("baseSettings")?.GetValue(tg);
                object? waterSettings = tgType.GetField("waterSettings")?.GetValue(tg);
                float scaling = baseSettings != null
                    ? (float)(baseSettings.GetType().GetField("scaling")?.GetValue(baseSettings) ?? 1f)
                    : 1f;
                float waterH = waterSettings != null
                    ? (float)(waterSettings.GetType().GetField("height")?.GetValue(waterSettings) ?? 0.05f)
                    : 0.05f;
                float noiseScaling = (float)(tgType.GetField("noiseScaling")?.GetValue(tg) ?? 1f);
                float computed = mapH * scaling * waterH * noiseScaling;
                if (computed > 0f) return computed;
            }
            catch (Exception ex)
            {
                Log($"ComputeWaterY exception: {ex.Message}");
            }
            return 3.15f; // last-resort fallback (typical FF water level)
        }

        private static AnimationCurve? ReadCurveField(object obj, string fieldName)
        {
            try
            {
                var f = obj.GetType().GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return f?.GetValue(obj) as AnimationCurve;
            }
            catch { return null; }
        }

        private static void WriteCurve(BinaryWriter bw, AnimationCurve? c)
        {
            if (c == null || c.keys == null) { bw.Write(0); return; }
            var keys = c.keys;
            bw.Write(keys.Length);
            foreach (var k in keys)
            {
                bw.Write(k.time);
                bw.Write(k.value);
                bw.Write(k.inTangent);
                bw.Write(k.outTangent);
            }
        }

        private static AnimationCurve? ReadCurve(BinaryReader br)
        {
            int count = br.ReadInt32();
            if (count <= 0) return null;
            var keys = new Keyframe[count];
            for (int i = 0; i < count; i++)
            {
                float time = br.ReadSingle();
                float value = br.ReadSingle();
                float inTan = br.ReadSingle();
                float outTan = br.ReadSingle();
                keys[i] = new Keyframe(time, value, inTan, outTan);
            }
            return new AnimationCurve(keys);
        }

        // ── Load (called from OnUpdate when conditions ready) ──────────────
        /// <summary>Mark that we should attempt restoration on the next OnUpdate
        /// when TerrainGenerator becomes available. Called from IsLoadingSavedMap
        /// detection or scene-load handler.</summary>
        public static void MarkRestorePending(string saveName)
        {
            RestorePending = true;
            PendingSaveName = saveName;
            Log($"Restore queued for save '{saveName}'");
        }

        public static void ClearPending()
        {
            RestorePending = false;
            PendingSaveName = null;
        }

        /// <summary>One-shot dump of SaveManager's structure: every field +
        /// property with values. Helps us find the right way to identify
        /// the currently-loaded save.</summary>
        public static void DumpSaveManager()
        {
            try
            {
                Type? smType = AccessTools.TypeByName("SaveManager");
                if (smType == null) { Log("DumpSaveManager: type not found"); return; }
                var smInstance = UnityEngine.Object.FindObjectOfType(smType);
                Log($"===== [SaveManagerDump] type={smType.FullName}  instance={(smInstance != null ? smInstance.GetType().Name : "null")} =====");

                // Static fields
                foreach (var f in smType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    object? v = null;
                    try { v = f.GetValue(null); } catch { }
                    Log($"  static field {f.Name} ({f.FieldType.Name}) = {Repr(v)}");
                }
                // Instance fields (if instance found)
                if (smInstance != null)
                {
                    foreach (var f in smType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        object? v = null;
                        try { v = f.GetValue(smInstance); } catch { }
                        Log($"  inst field {f.Name} ({f.FieldType.Name}) = {Repr(v)}");
                    }
                    foreach (var p in smType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (p.GetIndexParameters().Length > 0) continue;
                        object? v = null;
                        try { v = p.GetValue(smInstance, null); } catch { continue; }
                        Log($"  inst prop {p.Name} ({p.PropertyType.Name}) = {Repr(v)}");
                    }
                }
                // Static properties
                foreach (var p in smType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    object? v = null;
                    try { v = p.GetValue(null, null); } catch { continue; }
                    Log($"  static prop {p.Name} ({p.PropertyType.Name}) = {Repr(v)}");
                }
                Log("===== [SaveManagerDump] end =====");
            }
            catch (Exception ex)
            {
                Log($"DumpSaveManager exception: {ex.Message}");
            }
        }

        /// <summary>Try several known field/property names to locate the
        /// currently-loaded save's name. Returns null if nothing found.</summary>
        public static string? TryFindLoadedSaveName()
        {
            try
            {
                Type? smType = AccessTools.TypeByName("SaveManager");
                if (smType == null) return null;
                var smInstance = UnityEngine.Object.FindObjectOfType(smType);

                // Candidate names ordered by likelihood
                string[] candidates = {
                    "activeSaveFileName", "currentSaveFileName", "activeSaveName",
                    "activeFileName", "lastSavedGameFileName", "currentSaveName",
                    "saveFileName", "activeSlotName", "lastGameFolder"
                };

                foreach (var name in candidates)
                {
                    // Try static field
                    var sf = smType.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (sf != null && sf.FieldType == typeof(string))
                    {
                        var v = sf.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(v))
                        {
                            Log($"TryFindLoadedSaveName: matched static field '{name}' = '{v}'");
                            return v;
                        }
                    }
                    // Try instance field
                    if (smInstance != null)
                    {
                        var f = smType.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (f != null && f.FieldType == typeof(string))
                        {
                            var v = f.GetValue(smInstance) as string;
                            if (!string.IsNullOrEmpty(v))
                            {
                                Log($"TryFindLoadedSaveName: matched inst field '{name}' = '{v}'");
                                return v;
                            }
                        }
                    }
                    // Try property
                    var p = smType.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                    if (p != null && p.PropertyType == typeof(string))
                    {
                        try
                        {
                            object? target = p.GetGetMethod(true)?.IsStatic == true ? null : smInstance;
                            var v = p.GetValue(target, null) as string;
                            if (!string.IsNullOrEmpty(v))
                            {
                                Log($"TryFindLoadedSaveName: matched prop '{name}' = '{v}'");
                                return v;
                            }
                        }
                        catch { }
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                Log($"TryFindLoadedSaveName exception: {ex.Message}");
                return null;
            }
        }

        private static string Repr(object? v)
        {
            if (v == null) return "null";
            if (v is string s) return $"\"{s}\"";
            if (v is UnityEngine.Object uo) return $"UO[{uo.name}]";
            if (v is System.Collections.ICollection col) return $"Collection(Count={col.Count})";
            string r = v.ToString() ?? "";
            if (r.Length > 80) r = r.Substring(0, 80) + "…";
            return r;
        }

        /// <summary>Try to restore rivers from sidecar onto a live TerrainGenerator.
        /// Returns true if restoration was attempted (success or fail), false if
        /// preconditions weren't met and we should retry next frame.</summary>
        public static bool TryRestore(TerrainGenerator __instance)
        {
            try
            {
                if (string.IsNullOrEmpty(PendingSaveName)) return true; // nothing to do

                // Find SaveManager so we can resolve sidecar path consistently
                Type? smType = AccessTools.TypeByName("SaveManager");
                if (smType == null) { Log("TryRestore: SaveManager type missing"); return true; }
                var smInstance = UnityEngine.Object.FindObjectOfType(smType);
                if (smInstance == null) return false; // wait for SaveManager to spawn

                string sidecarPath = ResolveSidecarPath(smInstance, PendingSaveName!, isSave: false);
                if (string.IsNullOrEmpty(sidecarPath) || !File.Exists(sidecarPath))
                {
                    Log($"No sidecar at {sidecarPath} — skipping (vanilla save or non-Rivers map).");
                    return true;
                }

                // Direct WaterPath spawn — read sidecar (now includes
                // AnimationCurves at v2) and instantiate visuals at saved
                // positions. NEVER touches _generationData.rivers, NEVER
                // re-paints splats, NEVER triggers FF generation. Just
                // GameObject.Instantiate + WaterPath.SetPoints.
                var riverData = ReadSidecarRivers(sidecarPath);
                if (riverData == null || riverData.Count == 0)
                {
                    Log("Sidecar empty or unreadable — skipping.");
                    RestoredThisLoad = true;
                    return true;
                }
                Log($"Read {riverData.Count} rivers from sidecar (sum {riverData.Sum(r => r.Points.Count)} points).");
                // Diagnostic: dump first cp of first river — agent's secondary
                // suspect was that pos.y might be 0 in our sidecar, which would
                // collapse SetPoints' alpha math to zero.
                if (riverData.Count > 0 && riverData[0].Points.Count > 0)
                {
                    var p0 = riverData[0].Points[0];
                    Log($"  First cp: pos=({p0.Pos.x:F2},{p0.Pos.y:F2},{p0.Pos.z:F2})  height={p0.Height:F2}  width={p0.Width:F2}");
                    Log($"  Curves[0]: transparency={(riverData[0].TransparencyCurve != null ? "ok" : "null")}  extinction={(riverData[0].ExtinctionCurve != null ? "ok" : "null")}");
                }

                bool ribbonsEnabled2 = RiversRestoredMod.EnableRibbonAnimation?.Value ?? true;
                if (ribbonsEnabled2)
                {
                    int spawned = SpawnWaterPathsFromSidecar(riverData, __instance);
                    Log($"Spawned {spawned} WaterPath visual(s).");
                }
                else
                {
                    Log("EnableRibbonAnimation=false — skipping ribbon respawn (static water plane only).");
                }
                RestoredThisLoad = true;
                return true;
                #pragma warning disable CS0162 // unreachable
                int loaded = ReadAndApplySidecar(sidecarPath, __instance);
                Log($"Restored {loaded} rivers from sidecar.");

                if (loaded > 0)
                {
                    // ⚠ DISABLED in v0.1.0: Re-invoking Stage 60 on a loaded
                    // save corrupts FF's render state — the entire terrain
                    // mesh disappears (the "void map" bug). Even a single
                    // invocation is enough to nuke rendering.
                    //
                    // For v0.2.0, the right fix is to find FF's WaterPath
                    // prefab in Resources and Instantiate it directly with
                    // our saved control points, bypassing Stage 60 entirely.
                    //
                    // For now: river data is restored (useful for fishing
                    // detection later), but water visuals are absent on
                    // reload. Players must regenerate to see flowing water.
                    //
                    // TryReinvokeStage60(__instance); // ← DO NOT UNCOMMENT
                    Log("Skipping Stage 60 re-invoke (causes void map). Trench preserved; water visual will be missing.");
                }
                // Latch — never try again this scene. Reset on scene load.
                RestoredThisLoad = true;
                return true;
                #pragma warning restore CS0162
            }
            catch (Exception ex)
            {
                Log($"TryRestore exception: {ex}");
                return true;
            }
        }

        /// <summary>A pure-data record of one control point read from sidecar.</summary>
        public struct PointData
        {
            public Vector3 Pos;
            public float Height;
            public float Width;
        }

        /// <summary>One river's worth of data plus its rendering curves.
        /// In v3 sidecars, also carries the merged WaterArea polygon shape(s)
        /// that resulted from gen-time walk-and-stamp + Pangu-style merging,
        /// so reload can deserialize the polygon directly instead of
        /// re-walking the cps.</summary>
        public class RiverData
        {
            public System.Collections.Generic.List<PointData> Points = new();
            public AnimationCurve? TransparencyCurve;
            public AnimationCurve? ExtinctionCurve;
            /// <summary>v3 polygon shapes (one or more per river — typically
            /// one merged shape, but a river that splits across two disjoint
            /// areas could have multiple). Null/empty when reading a v1/v2
            /// sidecar — caller falls back to walk-and-stamp from cps.</summary>
            public System.Collections.Generic.List<PolygonData>? Polygons;
        }

        /// <summary>One serialized WaterArea polygon shape. Mask is bit-packed
        /// row-major (cell (x,z) is bit (z*width + x), low bit first).
        /// WaterTypeName is the asset name of the SO — looked up in
        /// waterSettings.lakeTypes on reload, with a lakeTypes[0] fallback if
        /// not found.</summary>
        public class PolygonData
        {
            public int MinX, MinZ, MaxX, MaxZ;
            public string WaterTypeName = "";
            public byte[] PackedMask = Array.Empty<byte>();

            public int Width => MaxX - MinX + 1;
            public int Height => MaxZ - MinZ + 1;

            /// <summary>Decode the bit-packed mask into a bool[,] sized [Width, Height].</summary>
            public bool[,] UnpackMask()
            {
                int w = Width, h = Height;
                var result = new bool[w, h];
                for (int z = 0; z < h; z++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int idx = z * w + x;
                        int byteIdx = idx >> 3;
                        if (byteIdx >= PackedMask.Length) continue;
                        if ((PackedMask[byteIdx] & (1 << (idx & 7))) != 0)
                            result[x, z] = true;
                    }
                }
                return result;
            }

            /// <summary>Encode a bool[,] mask into the packed-bytes representation.</summary>
            public static byte[] PackMask(bool[,] mask)
            {
                int w = mask.GetLength(0), h = mask.GetLength(1);
                int total = w * h;
                var packed = new byte[(total + 7) >> 3];
                for (int z = 0; z < h; z++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (!mask[x, z]) continue;
                        int idx = z * w + x;
                        packed[idx >> 3] |= (byte)(1 << (idx & 7));
                    }
                }
                return packed;
            }
        }

        /// <summary>Read sidecar into a pure-data list. Handles v1 (no curves),
        /// v2 (curves) and v3 (curves + embedded polygons). Returns null on
        /// read failure.</summary>
        public static System.Collections.Generic.List<RiverData>?
            ReadSidecarRivers(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                using (var br = new BinaryReader(fs))
                {
                    int magic = br.ReadInt32();
                    int version = br.ReadInt32();
                    if (magic != SIDECAR_MAGIC || version < 1 || version > SIDECAR_VERSION)
                    {
                        Log($"ReadSidecarRivers: bad magic/version 0x{magic:X8}/v{version}");
                        return null;
                    }
                    Log($"ReadSidecarRivers: format v{version}");
                    int numRivers = br.ReadInt32();
                    var result = new System.Collections.Generic.List<RiverData>(numRivers);
                    for (int i = 0; i < numRivers; i++)
                    {
                        int numPts = br.ReadInt32();
                        var rd = new RiverData();
                        rd.Points = new System.Collections.Generic.List<PointData>(numPts);
                        for (int p = 0; p < numPts; p++)
                        {
                            float x = br.ReadSingle();
                            float y = br.ReadSingle();
                            float z = br.ReadSingle();
                            float h = br.ReadSingle();
                            float w = br.ReadSingle();
                            rd.Points.Add(new PointData { Pos = new Vector3(x, y, z), Height = h, Width = w });
                        }
                        if (version >= 2)
                        {
                            rd.TransparencyCurve = ReadCurve(br);
                            rd.ExtinctionCurve = ReadCurve(br);
                        }
                        if (version >= 3)
                        {
                            int polyCount = br.ReadInt32();
                            rd.Polygons = new System.Collections.Generic.List<PolygonData>(polyCount);
                            for (int q = 0; q < polyCount; q++)
                            {
                                var poly = new PolygonData
                                {
                                    MinX = br.ReadInt32(),
                                    MinZ = br.ReadInt32(),
                                    MaxX = br.ReadInt32(),
                                    MaxZ = br.ReadInt32(),
                                    WaterTypeName = br.ReadString(),
                                };
                                int packedLen = br.ReadInt32();
                                poly.PackedMask = br.ReadBytes(packedLen);
                                rd.Polygons.Add(poly);
                            }
                        }
                        result.Add(rd);
                    }
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log($"ReadSidecarRivers exception: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Spawn WaterPath GameObjects directly using FF's existing prefab and
        /// the saved control points. Replicates Terrain2Builder.BuildTerrainShared03's
        /// pattern. Does NOT touch _generationData.rivers, splats, heightmap,
        /// or any other generation state. Just instantiates GameObjects.
        /// </summary>
        public static int SpawnWaterPathsFromSidecar(
            System.Collections.Generic.List<RiverData> rivers,
            TerrainGenerator tg)
        {
            int spawned = 0;
            try
            {
                // Find type references via reflection (avoids hard dependencies on FF's exact assembly layout)
                Type? waterPathType = AccessTools.TypeByName("TerrainGen.WaterPath");
                Type? terrain2Type = AccessTools.TypeByName("LibTerrain2.Terrain2") ?? AccessTools.TypeByName("Terrain2");
                Type? terrain2BuilderType = AccessTools.TypeByName("Terrain2Builder")
                                             ?? AccessTools.TypeByName("LibTerrain2.Terrain2Builder");
                Type? waterPlaneType = AccessTools.TypeByName("WaterPlane")
                                        ?? AccessTools.TypeByName("TerrainGen.WaterPlane");
                Type? terrainRiverType = AccessTools.TypeByName("TerrainRiver");
                Type? cpType = terrainRiverType?.GetNestedType("ControlPoint",
                    BindingFlags.Public | BindingFlags.NonPublic);

                if (waterPathType == null || terrain2Type == null || cpType == null)
                {
                    Log($"Spawn: missing types — WaterPath={waterPathType != null} Terrain2={terrain2Type != null} CP={cpType != null}");
                    return 0;
                }

                // Find game objects / components on the terrain GO
                var tgComp = tg as Component;
                if (tgComp == null) { Log("Spawn: TerrainGenerator is not a Component"); return 0; }
                GameObject terrainGO = tgComp.gameObject;

                Component? terrain2 = terrainGO.GetComponent(terrain2Type);
                Component? builder = terrain2BuilderType != null ? terrainGO.GetComponent(terrain2BuilderType) : null;
                if (terrain2 == null) { Log("Spawn: Terrain2 component missing on terrainGO"); return 0; }
                if (builder == null) { Log("Spawn: Terrain2Builder component missing on terrainGO"); return 0; }

                Transform? seaLayer = terrainGO.transform.Find("Sea Layer");
                if (seaLayer == null) { Log("Spawn: 'Sea Layer' child not found"); return 0; }
                Component? waterPlane = waterPlaneType != null ? seaLayer.GetComponent(waterPlaneType) : null;
                if (waterPlane == null) { Log("Spawn: WaterPlane on Sea Layer missing"); return 0; }

                // Use Sea Layer's actual Y as the water surface — vanilla uses
                // this value too. Empirically more reliable than computing from
                // settings (which gave a different value than the rendered surface).
                float waterY = seaLayer.localPosition.y;
                float computedY = ComputeWaterY(tg);
                Log($"Spawn: waterY={waterY:F2} from Sea Layer  (computed would be {computedY:F2})");

                // Get the prefab via WaterPlane.waterPathPrefab
                var prefabField = waterPlaneType!.GetField("waterPathPrefab",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var prefabObj = prefabField?.GetValue(waterPlane) as UnityEngine.Object;
                if (prefabObj == null) { Log("Spawn: waterPathPrefab is null"); return 0; }

                // Get/create the "Rivers" bucket parent
                Transform? riversBucket = terrainGO.transform.Find("Rivers");
                if (riversBucket == null)
                {
                    var createBucket = AccessTools.Method(typeof(TerrainGenerator), "CreateBucket",
                        new[] { typeof(GameObject), typeof(string) });
                    if (createBucket != null)
                    {
                        var bucket = createBucket.Invoke(null, new object[] { terrainGO, "Rivers" }) as GameObject;
                        if (bucket != null) riversBucket = bucket.transform;
                    }
                }
                if (riversBucket == null)
                {
                    // Fallback: just parent under terrainGO
                    riversBucket = terrainGO.transform;
                    Log("Spawn: couldn't get/create 'Rivers' bucket — parenting to terrain root");
                }

                // Get river material from Terrain2Builder
                var riverMatField = terrain2BuilderType!.GetField("riverMaterial",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var riverMaterial = riverMatField?.GetValue(builder) as Material;
                Log($"Spawn: riverMaterial={(riverMaterial != null ? riverMaterial.name : "NULL")}  waterY={waterY:F2}");

                // Fallback: if riverMaterial null on load, try to get it from
                // an existing WaterPath in the scene OR copy from prefab
                if (riverMaterial == null)
                {
                    // Look for any pre-existing WaterPath instance in scene
                    var anyWP = UnityEngine.Object.FindObjectOfType(waterPathType);
                    if (anyWP != null)
                    {
                        var matField = waterPathType.GetField("material",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        riverMaterial = matField?.GetValue(anyWP) as Material;
                        if (riverMaterial != null)
                            Log($"Spawn: fallback riverMaterial from scene WaterPath = {riverMaterial.name}");
                    }
                }
                if (riverMaterial == null)
                {
                    // Last resort: try the prefab itself
                    var prefabComp = prefabObj as Component;
                    if (prefabComp != null)
                    {
                        var matField = waterPathType.GetField("material",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        riverMaterial = matField?.GetValue(prefabComp) as Material;
                        if (riverMaterial != null)
                            Log($"Spawn: fallback riverMaterial from prefab = {riverMaterial.name}");
                    }
                }
                if (riverMaterial == null)
                    Log("Spawn: ⚠ riverMaterial null from all sources — water will be invisible");

                // ControlPoint has constructor (float x, float y, float z, float w, float h)
                var cpCtor = cpType.GetConstructor(new[] {
                    typeof(float), typeof(float), typeof(float), typeof(float), typeof(float)
                });
                if (cpCtor == null) { Log("Spawn: ControlPoint(float,float,float,float,float) ctor missing"); return 0; }

                Type cpListType = typeof(System.Collections.Generic.List<>).MakeGenericType(cpType);

                // SetPoints(Terrain2, List<ControlPoint>, AnimationCurve, AnimationCurve, float)
                var setPointsMI = waterPathType.GetMethod("SetPoints",
                    BindingFlags.Public | BindingFlags.Instance);
                if (setPointsMI == null) { Log("Spawn: WaterPath.SetPoints missing"); return 0; }

                // Curves come per-river from sidecar (v2). v1 sidecars or
                // missing curves fall through to a constant-opaque fallback.

                foreach (var rd in rivers)
                {
                    if (rd == null || rd.Points.Count < 2) continue;
                    var pts = rd.Points;
                    var transparency = rd.TransparencyCurve ?? AnimationCurve.Constant(0f, 1f, 0f);
                    var extinction = rd.ExtinctionCurve ?? AnimationCurve.Constant(0f, 1f, 0.5f);

                    // Instantiate prefab
                    var newWP = UnityEngine.Object.Instantiate(prefabObj);
                    var newWPComp = newWP as Component;
                    if (newWPComp == null) continue;
                    newWPComp.transform.parent = riversBucket;
                    newWPComp.transform.localPosition = Vector3.zero;

                    // Set material
                    if (riverMaterial != null)
                    {
                        var matField = waterPathType.GetField("material",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        matField?.SetValue(newWPComp, riverMaterial);
                    }

                    // Build typed List<TerrainRiver.ControlPoint>.
                    // Match vanilla Terrain2Builder.BuildTerrainShared03 (line
                    // 17235 in Assembly-CSharp.decompiled.cs) — copy the
                    // saved Stage-38 pos.y AS-IS. Stage 38's TraceRiverFunc
                    // already clamps pos.y to >= waterHeight; the resulting
                    // values typically range from waterHeight (at outlets)
                    // up to ~16 m (at sources). The shader's transparency
                    // curve is calibrated against (pos.y - terrain_height)
                    // / pos.y — overriding pos.y to waterY collapses that
                    // ratio to a near-zero region of the curve, rendering
                    // the river fully transparent.
                    var cpList = (IList)Activator.CreateInstance(cpListType)!;
                    foreach (var p in pts)
                    {
                        var cp = cpCtor.Invoke(new object[] {
                            p.Pos.x, p.Pos.y, p.Pos.z, p.Width, p.Height
                        });
                        cpList.Add(cp);
                    }

                    // Call SetPoints
                    try
                    {
                        setPointsMI.Invoke(newWPComp, new object[] {
                            terrain2, cpList, transparency, extinction, waterY
                        });
                        spawned++;

                        // Post-spawn diagnostics: is the visual actually configured?
                        if (spawned <= 2) // log first 2 to avoid spam
                        {
                            var go = newWPComp.gameObject;
                            var mf = go.GetComponent<MeshFilter>();
                            var mr = go.GetComponent<MeshRenderer>();
                            int verts = mf?.mesh != null ? mf.mesh.vertexCount : -1;
                            string matName = mr?.sharedMaterial != null ? mr.sharedMaterial.name : "NULL";
                            bool active = go.activeInHierarchy;
                            Log($"  WP #{spawned}: active={active}  vertices={verts}  rendererMaterial={matName}  pos={newWPComp.transform.position}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Exception inner = (ex is TargetInvocationException tie && tie.InnerException != null)
                            ? tie.InnerException : ex;
                        Log($"  WaterPath.SetPoints FAILED: {inner.GetType().Name}: {inner.Message}");
                        UnityEngine.Object.Destroy(newWP);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"SpawnWaterPathsFromSidecar exception: {ex}");
            }
            return spawned;
        }

        /// <summary>Read sidecar, allocate new TerrainRiver+ControlPoint instances
        /// from the binary data, and assign them to _generationData.rivers.</summary>
        private static int ReadAndApplySidecar(string path, TerrainGenerator __instance)
        {
            // Locate the relevant types
            Type? riverType = AccessTools.TypeByName("TerrainGen.TerrainRiver")
                              ?? AccessTools.TypeByName("TerrainRiver");
            if (riverType == null) { Log("ReadAndApplySidecar: TerrainRiver type missing"); return 0; }
            Type? cpType = riverType.GetNestedType("ControlPoint",
                BindingFlags.Public | BindingFlags.NonPublic);
            if (cpType == null) { Log("ReadAndApplySidecar: ControlPoint nested type missing"); return 0; }

            // _generationData.rivers
            var gdField = __instance.GetType().GetField("_generationData",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var gd = gdField?.GetValue(__instance);
            if (gd == null) return 0;
            var riversField = gd.GetType().GetField("rivers",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var rivers = riversField?.GetValue(gd) as IList;
            if (rivers == null) return 0;

            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                int magic = br.ReadInt32();
                int version = br.ReadInt32();
                if (magic != SIDECAR_MAGIC)
                {
                    Log($"ReadAndApplySidecar: bad magic 0x{magic:X8}");
                    return 0;
                }
                if (version != SIDECAR_VERSION)
                {
                    Log($"ReadAndApplySidecar: unsupported version {version}");
                    return 0;
                }

                int numRivers = br.ReadInt32();
                rivers.Clear();
                for (int i = 0; i < numRivers; i++)
                {
                    int numPts = br.ReadInt32();
                    var river = Activator.CreateInstance(riverType);
                    var pointsField = riverType.GetField("points",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    // Build a List<ControlPoint> of the right type
                    Type ptListType = typeof(System.Collections.Generic.List<>).MakeGenericType(cpType);
                    var ptList = (IList)Activator.CreateInstance(ptListType)!;

                    for (int p = 0; p < numPts; p++)
                    {
                        float x = br.ReadSingle();
                        float y = br.ReadSingle();
                        float z = br.ReadSingle();
                        float h = br.ReadSingle();
                        float w = br.ReadSingle();
                        var cp = Activator.CreateInstance(cpType);
                        SetVector3(cp!, "pos", new Vector3(x, y, z));
                        SetFloat(cp!, "height", h);
                        SetFloat(cp!, "width", w);
                        ptList.Add(cp);
                    }
                    pointsField?.SetValue(river, ptList);
                    rivers.Add(river);
                }
                return numRivers;
            }
        }

        /// <summary>Build WaterChunk meshes for our river WaterAreas using
        /// Pangu's incremental pattern — call WaterPlane.BuildWaterShared
        /// (private) per river area. Skips vanilla lakes/oceans so we don't
        /// disturb them.
        ///
        /// Why incremental instead of <c>WaterPlane.Rebuild</c>:
        ///   * <c>Rebuild</c> calls <c>Clean()</c> first, destroying ALL
        ///     WaterChunk children — including the vanilla lake/ocean chunks
        ///     that WERE rendering correctly. Then it tries to rebuild
        ///     everything from waterAreas data. After deserialization the
        ///     <c>waterArea.waterType.waterMaterial</c> references can be
        ///     missing/broken, so per-area BuildWater calls silently fail
        ///     and we end up with NO water plane anywhere — strictly worse
        ///     than before.
        ///   * <c>BuildWaterShared</c> is a private method that creates one
        ///     WaterChunk for one WaterArea without disturbing existing
        ///     chunks. This is exactly what Pangu uses (see line 430 in
        ///     Pangu_FF.decompiled.cs — WaterPlaneBuildWaterSharedMethod).
        /// </summary>
        public static void ForceWaterPlaneRebuild(TerrainGenerator tg)
        {
            try
            {
                var tgComp = tg as Component;
                if (tgComp == null) { Log("ForceWaterPlaneRebuild: TG not a Component"); return; }
                Transform? seaLayer = tgComp.transform.Find("Sea Layer");
                if (seaLayer == null) { Log("ForceWaterPlaneRebuild: 'Sea Layer' not found"); return; }

                Type? waterPlaneType = AccessTools.TypeByName("TerrainGen.WaterPlane")
                                       ?? AccessTools.TypeByName("WaterPlane");
                if (waterPlaneType == null) { Log("ForceWaterPlaneRebuild: WaterPlane type missing"); return; }
                var waterPlane = seaLayer.GetComponent(waterPlaneType);
                if (waterPlane == null) { Log("ForceWaterPlaneRebuild: WaterPlane component missing"); return; }

                Type? terrain2Type = AccessTools.TypeByName("LibTerrain2.Terrain2")
                                     ?? AccessTools.TypeByName("Terrain2");
                if (terrain2Type == null) { Log("ForceWaterPlaneRebuild: Terrain2 type missing"); return; }
                var terrain2 = tgComp.GetComponent(terrain2Type);
                if (terrain2 == null) { Log("ForceWaterPlaneRebuild: Terrain2 component missing"); return; }

                Type? waterAreaType = AccessTools.TypeByName("TerrainGen.TerrainGenerator+WaterArea");
                if (waterAreaType == null) { Log("ForceWaterPlaneRebuild: WaterArea type missing"); return; }

                var gdField = AccessTools.Field(typeof(TerrainGenerator), "_generationData");
                var gd = gdField?.GetValue(tg);
                if (gd == null) { Log("ForceWaterPlaneRebuild: _generationData null"); return; }
                var waField = gd.GetType().GetField("waterAreas",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var was = waField?.GetValue(gd) as System.Collections.IList;
                if (was == null) { Log("ForceWaterPlaneRebuild: waterAreas null"); return; }

                // Pre-call diagnostic: existing WaterChunk count
                int chunksBefore = waterPlane.GetComponentsInChildren(
                    AccessTools.TypeByName("WaterChunk") ?? typeof(Component), true)?.Length ?? -1;

                // Resolve the private BuildWaterShared(Terrain2, WaterArea, int) method.
                MethodInfo? buildWaterSharedMI = waterPlaneType.GetMethod("BuildWaterShared",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { terrain2Type, waterAreaType, typeof(int) }, null);
                if (buildWaterSharedMI == null)
                {
                    Log("ForceWaterPlaneRebuild: BuildWaterShared(Terrain2, WaterArea, int) not found");
                    return;
                }

                // Resolve WaterChunk type + its 8-param Rebuild method.
                // BuildWaterShared just creates the GameObject + WaterChunk
                // component and sets metadata. The mesh is built by
                // chunk.Rebuild(terrain, minX, minZ, maxX, maxZ, points,
                // shore, edge). Pangu's pattern at line 8787-8799.
                Type? waterChunkType = AccessTools.TypeByName("TerrainGen.WaterChunk")
                                       ?? AccessTools.TypeByName("WaterChunk");
                if (waterChunkType == null)
                {
                    Log("ForceWaterPlaneRebuild: WaterChunk type not found");
                    return;
                }
                Type? waterEdgeArrType = AccessTools.TypeByName("TerrainGen.TerrainGenerator+WaterEdge")?.MakeArrayType();
                Type pairIntIntArrType = AccessTools.TypeByName("Pair`2")!.MakeGenericType(typeof(int), typeof(int)).MakeArrayType();
                MethodInfo? chunkRebuildMI = waterChunkType.GetMethod("Rebuild",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { terrain2Type, typeof(int), typeof(int), typeof(int), typeof(int),
                            typeof(bool[,]), pairIntIntArrType, waterEdgeArrType! },
                    null);
                if (chunkRebuildMI == null)
                {
                    Log("ForceWaterPlaneRebuild: WaterChunk.Rebuild(8-param) not found — falling back to chunk-only build");
                }

                // Decide whether to rebuild ALL waterAreas or just ours.
                // If pre-existing chunk count is 0, FF didn't rebuild any
                // chunks on this load — we need to rebuild EVERYTHING (all
                // waterAreas), otherwise nothing renders. If chunks > 0,
                // FF did some rebuild work; we skip non-river entries to
                // avoid duplicating their chunks.
                bool rebuildAll = chunksBefore == 0;
                if (rebuildAll)
                {
                    Log($"  → chunksBefore=0 → rebuilding ALL {was.Count} waterAreas (FF skipped chunk rebuild on load)");
                }

                int built = 0, skippedVanilla = 0, skippedNullType = 0;
                for (int i = 0; i < was.Count; i++)
                {
                    var area = was[i];
                    if (area == null) continue;
                    Type at = area.GetType();
                    int aMinX = (int)(at.GetField("minX")?.GetValue(area) ?? 0);
                    int aMinZ = (int)(at.GetField("minZ")?.GetValue(area) ?? 0);
                    int aMaxX = (int)(at.GetField("maxX")?.GetValue(area) ?? 0);
                    int aMaxZ = (int)(at.GetField("maxZ")?.GetValue(area) ?? 0);

                    if (!rebuildAll)
                    {
                        // Cp-containment matching for partial rebuild.
                        bool containsRiverCp = false;
                        foreach (var cp in RiverWaterAreaBuilder.RiverCpCells)
                        {
                            if (cp.X >= aMinX && cp.X <= aMaxX && cp.Z >= aMinZ && cp.Z <= aMaxZ)
                            {
                                containsRiverCp = true;
                                break;
                            }
                        }
                        if (!containsRiverCp)
                        {
                            skippedVanilla++;
                            continue;
                        }
                    }

                    // Sanity-check waterType is non-null before BuildWater
                    var wt = at.GetField("waterType")?.GetValue(area) as UnityEngine.Object;
                    if (wt == null)
                    {
                        skippedNullType++;
                        Log($"  WA[{i}] waterType is NULL — skipping");
                        continue;
                    }

                    try
                    {
                        // Step 1: BuildWaterShared returns a WaterChunk
                        // (creates GameObject + sets material/curves; no mesh yet)
                        var chunk = buildWaterSharedMI.Invoke(waterPlane, new object[] { terrain2, area, i });
                        if (chunk == null)
                        {
                            Log($"  WA[{i}] BuildWaterShared returned null");
                            continue;
                        }

                        // Step 2: extract bounds + arrays from WaterArea
                        // Step 3: invoke chunk.Rebuild(terrain, minX, minZ,
                        // maxX, maxZ, points, shore, edge) — this builds
                        // the actual mesh (Pangu line 8799 pattern).
                        if (chunkRebuildMI != null)
                        {
                            var pts = at.GetField("points")?.GetValue(area);
                            var shore = at.GetField("shore")?.GetValue(area);
                            var edge = at.GetField("edge")?.GetValue(area);
                            if (pts == null || shore == null || edge == null)
                            {
                                Log($"  WA[{i}] BuildWaterShared OK but Rebuild skipped (null arrays)");
                                built++;
                                continue;
                            }
                            chunkRebuildMI.Invoke(chunk, new object[] {
                                terrain2, aMinX, aMinZ, aMaxX, aMaxZ, pts, shore, edge });
                            built++;
                            Log($"  WA[{i}] BuildWaterShared + Rebuild OK (cp-contained)");
                        }
                        else
                        {
                            built++;
                            Log($"  WA[{i}] BuildWaterShared OK (no Rebuild — chunk has no mesh)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Exception inner = (ex is TargetInvocationException tie && tie.InnerException != null)
                            ? tie.InnerException : ex;
                        Log($"  WA[{i}] BuildWaterShared/Rebuild FAILED: {inner.GetType().Name}: {inner.Message}");
                    }
                }

                int chunksAfter = waterPlane.GetComponentsInChildren(
                    AccessTools.TypeByName("WaterChunk") ?? typeof(Component), true)?.Length ?? -1;

                Log($"ForceWaterPlaneRebuild: chunks {chunksBefore} → {chunksAfter}, " +
                    $"built={built} skippedVanilla={skippedVanilla} skippedNullType={skippedNullType}");
            }
            catch (Exception ex)
            {
                Exception inner = (ex is TargetInvocationException tie && tie.InnerException != null)
                    ? tie.InnerException : ex;
                Log($"ForceWaterPlaneRebuild exception: {inner.GetType().Name}: {inner.Message}");
            }
        }

        /// <summary>Invoke GenerateAsync_RiverGeometry_Stage60 reflectively.
        /// We use the same path the original injection used; Stage 60 partial
        /// execution gets us WaterPath visuals before its carve-pass NRE.</summary>
        private static void TryReinvokeStage60(TerrainGenerator __instance)
        {
            try
            {
                var tgType = __instance.GetType();
                var stage60 = AccessTools.Method(tgType,
                    "GenerateAsync_RiverGeometry_Stage60", new Type[0]);
                if (stage60 == null) { Log("TryReinvokeStage60: method not found"); return; }
                Log(">>> Re-invoking Stage 60 to restore WaterPath visuals…");
                try
                {
                    stage60.Invoke(__instance, null);
                    Log("<<< Stage 60 restoration completed without exception.");
                }
                catch (Exception ex)
                {
                    Exception inner = (ex is TargetInvocationException tie && tie.InnerException != null)
                        ? tie.InnerException : ex;
                    // Same NRE as during gen — expected. WaterPath should be alive.
                    Log($"Stage 60 restoration partial (expected NRE on carve pass): {inner.GetType().Name}");
                }
            }
            catch (Exception ex)
            {
                Log($"TryReinvokeStage60 exception: {ex.Message}");
            }
        }

        // ── Reflection helpers ─────────────────────────────────────────────
        private static IList? GetRiversList(TerrainGenerator tg)
        {
            try
            {
                var gdField = tg.GetType().GetField("_generationData",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var gd = gdField?.GetValue(tg);
                if (gd == null) return null;
                var riversField = gd.GetType().GetField("rivers",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return riversField?.GetValue(gd) as IList;
            }
            catch { return null; }
        }

        private static IList? GetPointsList(object river)
        {
            try
            {
                var f = river.GetType().GetField("points",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return f?.GetValue(river) as IList;
            }
            catch { return null; }
        }

        private static Vector3 ReadVector3(object obj, string fieldName)
        {
            try
            {
                var f = obj.GetType().GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return f != null ? (Vector3)f.GetValue(obj) : Vector3.zero;
            }
            catch { return Vector3.zero; }
        }

        private static float ReadFloat(object obj, string fieldName)
        {
            try
            {
                var f = obj.GetType().GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return f != null ? (float)f.GetValue(obj) : 0f;
            }
            catch { return 0f; }
        }

        private static void SetVector3(object obj, string fieldName, Vector3 v)
        {
            try
            {
                var f = obj.GetType().GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                f?.SetValue(obj, v);
            }
            catch { }
        }

        private static void SetFloat(object obj, string fieldName, float v)
        {
            try
            {
                var f = obj.GetType().GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                f?.SetValue(obj, v);
            }
            catch { }
        }

        private static void Log(string msg) => RiversRestoredMod.Log.Msg($"[RR][Persist] {msg}");
    }
}
