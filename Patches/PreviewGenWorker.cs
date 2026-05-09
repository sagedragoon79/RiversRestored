using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using TerrainGen;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RiversRestored.Patches
{
    /// <summary>
    /// On-demand preview-gen trigger. Adopts Pangu's approach:
    ///
    /// FF's "Map" scene contains a properly-configured prefab with the
    /// TerrainGenerator and TerrainGeneratorController. AddComponent on a
    /// fresh GameObject doesn't carry over the Editor-configured settings
    /// (mapSettings, baseSettings, asset references etc.), so worker gen
    /// against such an instance produces NaN heightnoise. The fix is to
    /// load FF's "Map" scene additively and use the TerrainGenerator that
    /// scene already has — the same trick Pangu uses internally.
    ///
    /// V3 strategy:
    ///   1. Try cached/scene-search for any TG already present (Pangu, etc.)
    ///   2. If none, load "Map" scene additively
    ///   3. Find the TG in the loaded scene
    ///   4. Run gen stage sequence on it
    ///   5. Render via MapPreviewRenderer
    ///   6. (Optionally) unload Map scene to free resources
    ///
    /// Async work runs on a coroutine hosted by PreviewOverlay.
    /// </summary>
    internal static class PreviewGenWorker
    {
        private static bool _busy = false;
        // Tracks whether RR initiated the Map scene load (vs. it was
        // already loaded by FF/Pangu). Reserved for future use — V3
        // doesn't unload the scene after each preview, but if we do
        // later, this flag tells us whether unloading is appropriate.
        #pragma warning disable CS0414
        private static bool _mapSceneLoadedByUs = false;
        #pragma warning restore CS0414

        /// <summary>Entry point — invoked from the PREVIEW button click.
        /// Kicks off a coroutine that handles the async scene load and
        /// gen stage sequence.</summary>
        public static void TriggerPreview()
        {
            if (_busy)
            {
                Log("Already running — ignoring click.");
                return;
            }

            // Find a MonoBehaviour to host the coroutine. The PreviewOverlay
            // is already DontDestroyOnLoad and present whenever we'd want
            // to trigger a preview.
            var host = UnityEngine.Object.FindObjectOfType<PreviewOverlay>();
            if (host == null)
            {
                Log("No PreviewOverlay host MonoBehaviour found — can't start coroutine.");
                return;
            }

            _busy = true;
            host.StartCoroutine(PreviewCoroutine());
        }

        private static IEnumerator PreviewCoroutine()
        {
            try
            {
                // ── Step 1: try existing TG ──────────────────────────
                var tg = FindUsableTerrainGenerator();
                if (tg != null)
                {
                    Log($"Using existing TerrainGenerator on '{tg.gameObject.name}'.");
                }
                else
                {
                    // ── Step 2: load FF's "Map" scene additively ──────
                    Log("No existing TerrainGenerator — loading 'Map' scene additively...");
                    yield return LoadMapScene();
                    tg = FindTerrainGeneratorInMapScene();
                    if (tg == null)
                    {
                        Log("Map scene loaded but no TerrainGenerator found in its roots.");
                        yield break;
                    }
                    Log($"Loaded 'Map' scene; using its TerrainGenerator on '{tg.gameObject.name}'.");
                }

                // ── Step 3: find/configure TerrainGeneratorController ──
                // Pangu's pattern: set TGC.terrainType from SettingsManager,
                // set seed/size, configure debugOptions, then invoke
                // TGC.GenerateInternal(false) which orchestrates the full
                // pipeline correctly. Calling individual TG stage methods
                // produces NaN heightnoise because state isn't initialized
                // properly — the controller's GenerateInternal handles all
                // that prep work.
                var tgc = tg.GetComponent<TerrainGeneratorController>()
                          ?? tg.GetComponentInParent<TerrainGeneratorController>()
                          ?? UnityEngine.Object.FindObjectOfType<TerrainGeneratorController>();
                if (tgc == null)
                {
                    Log("No TerrainGeneratorController found — can't run native gen path.");
                    yield break;
                }
                Log($"Using TerrainGeneratorController on '{tgc.gameObject.name}'.");

                // Apply user's biome selection from FF's SettingsManager.
                ApplyBiomeFromSettings(tgc);

                string seed = TryReadSeedFromUI();
                if (!string.IsNullOrEmpty(seed))
                {
                    Log($"Seed from UI: '{seed}'");
                    TrySetSeedOnController(tgc, seed);
                }

                // Force a fresh gen pass.
                TrySetIsLoadedFalse(tgc);

                // Skip expensive stages we don't need for preview.
                ConfigureDebugOptions(tg);

                // Reset our renderer's per-gen flag.
                MapPreviewRenderer.RenderedThisGen = false;

                // Run FF's native gen via TGC.GenerateInternal(false).
                bool genOk = InvokeGenerateInternal(tgc);
                if (!genOk)
                {
                    Log("GenerateInternal didn't run cleanly — preview may be incomplete.");
                }

                DumpGeneratorState(tg);

                MapPreviewRenderer.TryRender(tg, "PreviewButton");
                Log("Preview gen complete.");

                // Optional: unload Map scene to free resources. Skipped for
                // V3 — keeping it loaded means subsequent previews don't pay
                // the load cost again. Trade-off: extra memory while the
                // user is on the New Game screen.
                // if (_mapSceneLoadedByUs) yield return UnloadMapScene();
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>Load FF's "Map" scene additively. Returns when the
        /// load completes (isDone == true). If the scene is already
        /// loaded, returns immediately without re-loading.</summary>
        private static IEnumerator LoadMapScene()
        {
            var existing = SceneManager.GetSceneByName("Map");
            if (existing.IsValid() && existing.isLoaded)
            {
                Log("'Map' scene already loaded.");
                yield break;
            }

            AsyncOperation? op = null;
            try { op = SceneManager.LoadSceneAsync("Map", LoadSceneMode.Additive); }
            catch (Exception ex) { Log($"LoadSceneAsync failed: {ex.Message}"); yield break; }
            if (op == null) { Log("LoadSceneAsync returned null."); yield break; }
            _mapSceneLoadedByUs = true;
            while (!op.isDone) yield return null;
            Log("'Map' scene load complete.");
        }

        private static IEnumerator UnloadMapScene()
        {
            var sc = SceneManager.GetSceneByName("Map");
            if (!sc.IsValid() || !sc.isLoaded) yield break;
            AsyncOperation? op = null;
            try { op = SceneManager.UnloadSceneAsync(sc); }
            catch (Exception ex) { Log($"UnloadSceneAsync failed: {ex.Message}"); yield break; }
            if (op == null) yield break;
            while (!op.isDone) yield return null;
            _mapSceneLoadedByUs = false;
            Log("'Map' scene unloaded.");
        }

        /// <summary>Find a TerrainGenerator inside the loaded "Map" scene.
        /// Mirrors Pangu's TryCreateSeedPreviewWorkerFromLoadedObjects
        /// — walks scene root GameObjects and looks for the component.</summary>
        private static TerrainGenerator? FindTerrainGeneratorInMapScene()
        {
            try
            {
                var sc = SceneManager.GetSceneByName("Map");
                if (!sc.IsValid() || !sc.isLoaded) return null;
                var roots = sc.GetRootGameObjects();
                if (roots == null) return null;
                foreach (var root in roots)
                {
                    if (root == null) continue;
                    var tg = root.GetComponent<TerrainGenerator>()
                             ?? root.GetComponentInChildren<TerrainGenerator>(true);
                    if (tg != null) return tg;
                }
            }
            catch (Exception ex)
            {
                Log($"FindTerrainGeneratorInMapScene failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>Find any TerrainGenerator instance currently in the
        /// scene — RR's cached one (gameplay or Pangu worker) or any other.</summary>
        private static TerrainGenerator? FindUsableTerrainGenerator()
        {
            if (RiverSettingsPatch.CachedGenerator != null)
                return RiverSettingsPatch.CachedGenerator;
            return UnityEngine.Object.FindObjectOfType<TerrainGenerator>();
        }

        private static string TryReadSeedFromUI()
        {
            try
            {
                var allInputs = Resources.FindObjectsOfTypeAll<TMPro.TMP_InputField>();
                foreach (var f in allInputs)
                {
                    if (f == null || string.IsNullOrEmpty(f.gameObject.name)) continue;
                    var n = f.gameObject.name.ToLowerInvariant();
                    if (n.Contains("seed") || n.Contains("map seed"))
                    {
                        var txt = f.text;
                        if (!string.IsNullOrEmpty(txt)) return txt;
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        /// <summary>Read FF's SettingsManager.Instance.mapType and apply
        /// it to the worker controller's terrainType field. Pangu does
        /// this every preview run to honor the user's biome selection.</summary>
        private static void ApplyBiomeFromSettings(TerrainGeneratorController tgc)
        {
            try
            {
                var smType = AccessTools.TypeByName("SettingsManager");
                if (smType == null) { Log("SettingsManager type not found."); return; }
                var instProp = smType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static);
                var inst = instProp?.GetValue(null);
                if (inst == null)
                {
                    // Try UnitySingletonPersistent<SettingsManager>.Instance
                    var uspType = AccessTools.TypeByName("UnitySingletonPersistent`1")
                                  ?.MakeGenericType(smType);
                    var uspInst = uspType?.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static);
                    inst = uspInst?.GetValue(null);
                }
                if (inst == null) { Log("SettingsManager.Instance is null."); return; }
                var mapTypeField = smType.GetField("mapType",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var mapTypeProp = smType.GetProperty("mapType",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object? mapType = mapTypeField?.GetValue(inst) ?? mapTypeProp?.GetValue(inst);
                if (mapType == null) { Log("SettingsManager.mapType is null."); return; }

                var tgcTerrainTypeField = AccessTools.Field(tgc.GetType(), "terrainType");
                var tgcTerrainTypeProp = tgc.GetType().GetProperty("terrainType");
                if (tgcTerrainTypeField != null) tgcTerrainTypeField.SetValue(tgc, mapType);
                else if (tgcTerrainTypeProp != null) tgcTerrainTypeProp.SetValue(tgc, mapType);
                Log($"Set TGC.terrainType = {mapType}");
            }
            catch (Exception ex) { Log($"ApplyBiomeFromSettings failed: {ex.Message}"); }
        }

        /// <summary>Set TGC.seed (or .seedText) from the UI value.</summary>
        private static void TrySetSeedOnController(TerrainGeneratorController tgc, string seedText)
        {
            try
            {
                foreach (var name in new[] { "seed", "Seed", "currentMapSeed" })
                {
                    var f = AccessTools.Field(tgc.GetType(), name);
                    if (f == null) continue;
                    if (f.FieldType == typeof(string)) { f.SetValue(tgc, seedText); return; }
                    if (f.FieldType == typeof(int) && int.TryParse(seedText, out var iv))
                    { f.SetValue(tgc, iv); return; }
                }
                var p = tgc.GetType().GetProperty("seed");
                if (p != null && p.CanWrite)
                {
                    if (p.PropertyType == typeof(string)) p.SetValue(tgc, seedText);
                    else if (p.PropertyType == typeof(int) && int.TryParse(seedText, out var iv))
                        p.SetValue(tgc, iv);
                }
            }
            catch (Exception ex) { Log($"Seed-on-controller set failed: {ex.Message}"); }
        }

        /// <summary>Force isLoaded=false on the controller so a fresh gen runs.</summary>
        private static void TrySetIsLoadedFalse(TerrainGeneratorController tgc)
        {
            try
            {
                var f = AccessTools.Field(tgc.GetType(), "isLoaded")
                       ?? AccessTools.Field(tgc.GetType(), "IsLoaded");
                if (f != null) f.SetValue(tgc, false);
            }
            catch { }
        }

        /// <summary>Configure TG.debugOptions to skip expensive stages we
        /// don't need for preview rendering. Mirrors Pangu's settings.</summary>
        private static void ConfigureDebugOptions(TerrainGenerator tg)
        {
            try
            {
                var doField = AccessTools.Field(tg.GetType(), "debugOptions")
                              ?? AccessTools.Field(tg.GetType(), "_debugOptions");
                var debugOpts = doField?.GetValue(tg);
                if (debugOpts == null) return;
                var t = debugOpts.GetType();
                void Set(string n, bool v)
                {
                    var f = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null) f.SetValue(debugOpts, v);
                }
                Set("generateFeatures", true);
                Set("generateRivers", true);
                Set("paintTerrain", true);
                Set("paintBiomes", true);
                Set("generateRoads", false);
                Set("generateTrees", false);
                Set("generateDetails", false);
                Set("generateObjects", false);
            }
            catch (Exception ex) { Log($"ConfigureDebugOptions failed: {ex.Message}"); }
        }

        /// <summary>Invoke TGC.GenerateInternal(false) — the synchronous
        /// version of FF's gen pipeline. Returns true on success.</summary>
        private static bool InvokeGenerateInternal(TerrainGeneratorController tgc)
        {
            try
            {
                var m = AccessTools.Method(tgc.GetType(), "GenerateInternal", new[] { typeof(bool) });
                if (m == null)
                {
                    Log("GenerateInternal(bool) method not found on TGC.");
                    return false;
                }
                m.Invoke(tgc, new object[] { false });
                return true;
            }
            catch (TargetInvocationException tex)
            {
                var inner = tex.InnerException;
                Log($"GenerateInternal threw: {(inner != null ? inner.GetType().Name + ": " + inner.Message : tex.Message)}");
                return false;
            }
            catch (Exception ex)
            {
                Log($"GenerateInternal threw: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static void TrySetSeedOnGenerator(TerrainGenerator tg, string seedText)
        {
            try
            {
                foreach (var name in new[] { "currentMapSeed", "_seed", "seed", "mapSeed" })
                {
                    var f = AccessTools.Field(typeof(TerrainGenerator), name);
                    if (f == null) continue;
                    if (f.FieldType == typeof(string)) { f.SetValue(tg, seedText); return; }
                    if (f.FieldType == typeof(int) && int.TryParse(seedText, out var iv))
                    { f.SetValue(tg, iv); return; }
                }
                var gdField = AccessTools.Field(typeof(TerrainGenerator), "_generationData");
                var gd = gdField?.GetValue(tg);
                if (gd != null)
                {
                    var sf = gd.GetType().GetField("seed",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (sf != null && sf.FieldType == typeof(string))
                        sf.SetValue(gd, seedText);
                }
            }
            catch (Exception ex) { Log($"Seed set failed: {ex.Message}"); }
        }

        private static void InvokeStageIfPresent(TerrainGenerator tg, string methodName)
        {
            try
            {
                var m = AccessTools.Method(typeof(TerrainGenerator), methodName, new Type[0]);
                if (m == null) return;
                m.Invoke(tg, null);
            }
            catch (TargetInvocationException tex)
            {
                var inner = tex.InnerException;
                Log($"  {methodName} threw: {(inner != null ? inner.GetType().Name + ": " + inner.Message : tex.Message)}");
            }
            catch (Exception ex)
            {
                Log($"  {methodName} threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void DumpGeneratorState(TerrainGenerator tg)
        {
            try
            {
                var t = typeof(TerrainGenerator);
                foreach (var name in new[] { "mapSettings", "baseSettings", "riverSettings",
                                              "waterSettings", "biomeSettings", "_generationData" })
                {
                    var f = AccessTools.Field(t, name);
                    if (f == null) { Log($"  state: {name} = <field missing>"); continue; }
                    var v = f.GetValue(tg);
                    Log($"  state: {name} = {(v == null ? "<null>" : v.GetType().Name)}");
                }

                var gdField = AccessTools.Field(t, "_generationData");
                var gd = gdField?.GetValue(tg);
                if (gd != null)
                {
                    var hnField = gd.GetType().GetField("heightNoise",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (hnField?.GetValue(gd) is float[,] hn)
                    {
                        float min = float.MaxValue, max = float.MinValue;
                        for (int x = 0; x < hn.GetLength(0); x++)
                            for (int z = 0; z < hn.GetLength(1); z++)
                            {
                                if (hn[x, z] < min) min = hn[x, z];
                                if (hn[x, z] > max) max = hn[x, z];
                            }
                        Log($"  state: heightNoise[{hn.GetLength(0)}x{hn.GetLength(1)}] min={min:F3} max={max:F3}");
                    }
                    else
                    {
                        Log($"  state: heightNoise = <null>");
                    }
                    var waField = gd.GetType().GetField("waterAreas",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var wa = waField?.GetValue(gd) as System.Collections.IList;
                    Log($"  state: waterAreas count={(wa?.Count ?? -1)}");
                }
            }
            catch (Exception ex) { Log($"  state dump failed: {ex.Message}"); }
        }

        private static void Log(string msg) =>
            MelonLogger.Msg($"[RR][PreviewGen] {msg}");
    }
}
