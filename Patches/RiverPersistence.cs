using System;
using System.Collections;
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
        const int SIDECAR_VERSION = 2; // v2 adds AnimationCurves per river
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

        /// <summary>Reset on scene load so the next save load can restore again.</summary>
        public static void ResetForSceneLoad()
        {
            RestorePending = false;
            PendingSaveName = null;
            RestoredThisLoad = false;
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

                string sidecarPath = ResolveSidecarPath(smInstance, saveName!);
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

                Log($"BTS03 postfix: spawning {riverData.Count} rivers from sidecar (sum {riverData.Sum(r => r.Points.Count)} points)");
                int spawned = SpawnWaterPathsFromSidecar(riverData, tg);
                Log($"BTS03 postfix: spawned {spawned} WaterPath visual(s) post-rebuild.");

                // ── KEY DISCOVERY (2026-04-26 reload test): FF's load
                // doesn't deserialize the saved waterAreas list — it
                // regenerates the list from seed data. Saved count was 33
                // (with our 2 entries), reloaded count was 36 (no entries
                // of ours). So saving river polygons via FF's serializer
                // is futile.
                //
                // Pangu's polygons survive across reload because Pangu
                // does the same thing we now do here: re-add at runtime
                // post-load. We populate sidecar cps via SaveSidecar,
                // and on load — right here — we walk those cps and
                // stamp blobs again, mirroring exactly what we do at
                // gen post-Stage-50 timing.
                //
                // Walk the sidecar's cp data and stamp polygons directly —
                // we don't need to reconstruct _generationData.rivers, just
                // run the same walk-and-stamp pass we do at gen.
                RiverWaterAreaBuilder.RiverWaterAreaBounds.Clear();
                int reAdded = RiverWaterAreaBuilder.BuildAndAddFromSidecar(tg, riverData);
                Log($"BTS03 postfix: re-added {reAdded} river polygon(s) post-load via walk-and-stamp.");

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

                Log($"[SavePre] waterAreas.Count={beforeCount}  ours={oursPresent}/{trackedBounds}");

                // ── Re-add stripped river polygons before FF serializes ────
                // Diagnostic in v0.2 first reload showed waterAreas dropping
                // from gen-time (33→36) back to 33 by save-prefix time —
                // some late gen stage strips additions made by mods. By
                // save-prefix all gen stages have run, so re-adding here
                // means our polygons survive into the .map serialization.
                if (oursPresent < trackedBounds && trackedBounds > 0)
                {
                    Log($"[SavePre] {trackedBounds - oursPresent} river polygon(s) stripped during gen — re-adding via walk-and-stamp before serialization.");
                    RiverWaterAreaBuilder.RiverWaterAreaBounds.Clear();
                    int reAdded = RiverWaterAreaBuilder.BuildAndAddForAllRivers(tg);
                    int afterCount = was.Count;
                    Log($"[SavePre] Re-add result: {reAdded} river polygon(s) re-stamped. waterAreas {beforeCount} → {afterCount}.");
                }
                else
                {
                    Log("[SavePre] All tracked river polygons already present — no re-add needed.");
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

                IList? rivers = GetRiversList(tg);
                if (rivers == null || rivers.Count == 0)
                {
                    Log("Save: no rivers in _generationData — skipping sidecar (vanilla map).");
                    return;
                }

                string sidecarPath = ResolveSidecarPath(__instance, savedGameFileNameNoExtension);
                if (string.IsNullOrEmpty(sidecarPath))
                {
                    Log("Save: could not resolve sidecar path — skipping.");
                    return;
                }

                int totalPoints = WriteSidecar(sidecarPath, rivers);
                Log($"Saved {rivers.Count} rivers ({totalPoints} points) → {sidecarPath}");
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
        /// Compute the .rivers sidecar path. Canonical form:
        ///     Save/{activeSaveFileName}.rivers
        /// where activeSaveFileName already includes "{slotFolder}/{saveName}".
        ///
        /// For save: we read activeSaveFileName from SaveManager static (it's
        /// usually set just before Save fires). If empty, fall back to the
        /// bare savedGameFileNameNoExtension passed to the Save hook.
        ///
        /// For load: we pass activeSaveFileName as saveNameOrPath here.
        ///
        /// On disk: sidecar lives at the same path FF's .map lives, e.g.
        ///     Save/Soberado_2026254195339/Soberado.rivers
        /// </summary>
        private static string ResolveSidecarPath(object saveManager, string saveNameOrPath)
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

                // Decide which identifier to use for sidecar path:
                //   1. activeSaveFileName if set (canonical, slot/name form)
                //   2. otherwise saveNameOrPath (bare savedGameFileNameNoExtension)
                string identifier = !string.IsNullOrEmpty(asf) ? asf : saveNameOrPath;
                if (string.IsNullOrEmpty(identifier))
                {
                    Log("ResolveSidecarPath: empty identifier (no activeSaveFileName, no save name)");
                    return string.Empty;
                }

                // Build sidecar path. Normalize forward slashes from FF to OS separator.
                string canonical = Path.Combine(baseDir, folderName, identifier + SIDECAR_EXTENSION)
                    .Replace('/', Path.DirectorySeparatorChar);
                Log($"ResolveSidecarPath: folderName='{folderName}'  asf='{asf}'  arg='{saveNameOrPath}'");
                Log($"  Canonical path: {canonical}");

                // For LOAD — try canonical first; if not found, fall back to flat
                // path (where we may have written sidecar in earlier mod versions).
                if (File.Exists(canonical))
                {
                    Log($"  → using canonical (file exists)");
                    return canonical;
                }

                // Migration fallback: bare-name flat path (older sidecars)
                string bareName = Path.GetFileName(identifier.Replace('/', Path.DirectorySeparatorChar));
                string flatPath = Path.Combine(baseDir, folderName, bareName + SIDECAR_EXTENSION);
                if (File.Exists(flatPath))
                {
                    Log($"  → using flat fallback (older sidecar): {flatPath}");
                    return flatPath;
                }

                // For SAVE — neither exists yet; use canonical and ensure dir exists
                string parent = Path.GetDirectoryName(canonical) ?? "";
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    Directory.CreateDirectory(parent);
                    Log($"  Created directory: {parent}");
                }
                Log($"  → using canonical (new sidecar)");
                return canonical;
            }
            catch (Exception ex)
            {
                Log($"ResolveSidecarPath exception: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>Serialize rivers to the sidecar binary file (v2 format).</summary>
        private static int WriteSidecar(string path, IList rivers)
        {
            int totalPoints = 0;
            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(SIDECAR_MAGIC);
                bw.Write(SIDECAR_VERSION);
                bw.Write(rivers.Count);

                foreach (var river in rivers)
                {
                    if (river == null) { bw.Write(0); WriteCurve(bw, null); WriteCurve(bw, null); continue; }
                    var points = GetPointsList(river);
                    int n = points?.Count ?? 0;
                    bw.Write(n);
                    if (points != null)
                    {
                        foreach (var pt in points)
                        {
                            if (pt == null) continue;
                            Vector3 pos = ReadVector3(pt, "pos");
                            float h = ReadFloat(pt, "height");
                            float w = ReadFloat(pt, "width");
                            bw.Write(pos.x); bw.Write(pos.y); bw.Write(pos.z);
                            bw.Write(h);
                            bw.Write(w);
                            totalPoints++;
                        }
                    }
                    // v2: capture curves from the river
                    AnimationCurve? tc = ReadCurveField(river, "transparencyCurve");
                    AnimationCurve? ec = ReadCurveField(river, "extinctionCurve");
                    WriteCurve(bw, tc);
                    WriteCurve(bw, ec);
                }
            }
            return totalPoints;
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

                string sidecarPath = ResolveSidecarPath(smInstance, PendingSaveName!);
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

                int spawned = SpawnWaterPathsFromSidecar(riverData, __instance);
                Log($"Spawned {spawned} WaterPath visual(s).");
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

        /// <summary>One river's worth of data plus its rendering curves.</summary>
        public class RiverData
        {
            public System.Collections.Generic.List<PointData> Points = new();
            public AnimationCurve? TransparencyCurve;
            public AnimationCurve? ExtinctionCurve;
        }

        /// <summary>Read sidecar into a pure-data list. Handles v1 (no curves)
        /// and v2 (with curves). Returns null on read failure.</summary>
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
                    if (magic != SIDECAR_MAGIC || (version != 1 && version != 2))
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
