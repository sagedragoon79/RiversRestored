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
                var tgc = tg.GetComponent<TerrainGeneratorController>()
                          ?? tg.GetComponentInParent<TerrainGeneratorController>()
                          ?? UnityEngine.Object.FindObjectOfType<TerrainGeneratorController>();
                if (tgc == null)
                {
                    Log("No TerrainGeneratorController found — can't run native gen path.");
                    yield break;
                }
                Log($"Using TerrainGeneratorController on '{tgc.gameObject.name}'.");

                // ── Step 4: read seed from UI, decode all encoded values ──
                // Pangu's insight: the seed string encodes terrainSeed +
                // themeId + mountainValue + waterValue. SettingsManager
                // .SeedToSettings(seedStr, ref ts, ref t, ref m, ref w)
                // decodes them in one call. The user's biome/water/mountain
                // sliders write into the seed; we just read the seed and
                // get all the values for free.
                string rawSeed = TryReadSeedFromUI();
                if (string.IsNullOrEmpty(rawSeed))
                {
                    Log("No seed in UI input — can't generate preview.");
                    yield break;
                }
                Log($"Seed from UI: '{rawSeed}'");

                if (!TryDecodeSeedString(rawSeed, out int terrainSeed,
                    out int themeId, out int mountainValue, out int waterValue))
                {
                    Log("Failed to decode seed string — can't generate preview.");
                    yield break;
                }
                Log($"Decoded: terrainSeed={terrainSeed} themeId={themeId} " +
                    $"mountains={mountainValue} water={waterValue}");

                // Read map size from SettingsManager.mapSizeValue (static).
                int mapSizeIdx = TryReadMapSizeIndex();
                Log($"MapSize index: {mapSizeIdx}");

                // Resolve theme from themeId via GlobalAssets.mapTypeData.
                object? theme = ResolveTheme(themeId);
                if (theme == null)
                {
                    Log($"Could not resolve theme for themeId={themeId} — gen may fail.");
                }

                // Apply all the gen parameters to TGC + TG.
                ApplyTgcGenParameters(tgc, terrainSeed, mapSizeIdx, theme,
                    mountainValue, waterValue);
                TrySetIsLoadedFalse(tgc);

                // Reset our renderer's per-gen flag.
                MapPreviewRenderer.RenderedThisGen = false;

                // Pangu's full sequence after parameter application:
                //   1. TGC.GenerateInternal(false)          — TGC-side init
                //   2. TG.ResetGeneratedData()              — clear prior state
                //   3. Configure TG.debugOptions            — skip expensive stages
                //   4. TG.inGame = false                    — preview mode
                //   5. TG.generating = true                 — flag for stages
                //   6. Set TG.rng.Seed from terrainSettings — deterministic
                //   7. TG.PreGenerate()                     — pre-stage init
                //   8. Create fresh _generationData         — clean output container
                //   9. TG.GenerateAsync()                   — RUN THE ACTUAL GEN
                bool ok = InvokeGenerateInternal(tgc);
                if (!ok)
                    Log("GenerateInternal threw — continuing anyway.");
                TryResetGeneratedData(tg);
                ConfigureDebugOptions(tg);
                SetWorkerFlag(tg, "inGame", false);
                SetWorkerFlag(tg, "generating", true);
                SetRngSeed(tg);
                InvokeMethodIfPresent(tg, "PreGenerate");
                CreateFreshGenerationData(tg);
                ok = InvokeMethodIfPresent(tg, "GenerateAsync");
                if (!ok)
                    Log("GenerateAsync threw — preview will likely be empty.");

                DumpGeneratorState(tg);

                MapPreviewRenderer.TryRender(tg, "PreviewButton");

                // Override caption with rich metadata: actual seed string,
                // map size, biome, all 4 difficulty selections, river count,
                // water%. The default caption built by MapPreviewRenderer
                // is missing seed string + difficulty info that we can
                // now read.
                BuildRichCaption(rawSeed, mapSizeIdx, tg);

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

        /// <summary>Decode the user's seed string into its component values
        /// using FF's static <c>SettingsManager.SeedToSettings</c>. The
        /// seed string encodes terrainSeed (int) + themeId (byte) +
        /// mountainValue (byte) + waterValue (byte) in one string —
        /// reading the seed gets all four UI values for free without
        /// having to find/read each individual UI control.</summary>
        private static bool TryDecodeSeedString(string rawSeed, out int terrainSeed,
            out int themeId, out int mountainValue, out int waterValue)
        {
            terrainSeed = 1; themeId = 0; mountainValue = 0; waterValue = 0;
            try
            {
                var smType = AccessTools.TypeByName("SettingsManager");
                if (smType == null) { Log("SettingsManager type not found."); return false; }

                // SeedToSettings(string, ref int, ref byte, ref byte, ref byte)
                var m = smType.GetMethod("SeedToSettings",
                    BindingFlags.Public | BindingFlags.Static);
                if (m == null) { Log("SettingsManager.SeedToSettings not found."); return false; }

                // Build args: string + 4 ref values (boxed)
                int ts = 1; byte b1 = 0, b2 = 0, b3 = 0;
                object[] args = new object[] { rawSeed, ts, b1, b2, b3 };
                m.Invoke(null, args);
                terrainSeed = (int)args[1];
                themeId = (byte)args[2];
                mountainValue = (byte)args[3];
                waterValue = (byte)args[4];
                if (terrainSeed <= 0)
                {
                    Log("SeedToSettings returned terrainSeed <= 0 — invalid seed.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log($"TryDecodeSeedString failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Read SettingsManager.mapSizeValue (static). Returns
        /// 1 (Medium) on any failure.</summary>
        private static int TryReadMapSizeIndex()
        {
            try
            {
                var smType = AccessTools.TypeByName("SettingsManager");
                if (smType == null) return 1;
                var f = smType.GetField("mapSizeValue",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var v = f?.GetValue(null);
                if (v is byte b) return b;
                if (v is int i) return i;
                if (v is Enum e) return Convert.ToInt32(e);
            }
            catch { }
            return 1;
        }

        /// <summary>Resolve a Terrain2Theme from a theme ID via
        /// <c>GlobalAssets.mapTypeData.GetMapThemeFromID(themeId, false)</c>.
        /// Required input to TGC.theme — without it, GenerateInternal NREs.</summary>
        private static object? ResolveTheme(int themeId)
        {
            try
            {
                var gaType = AccessTools.TypeByName("GlobalAssets");
                if (gaType == null) { Log("GlobalAssets type not found."); return null; }
                var mtdField = gaType.GetField("mapTypeData",
                    BindingFlags.Public | BindingFlags.Static);
                var mtdProp = gaType.GetProperty("mapTypeData",
                    BindingFlags.Public | BindingFlags.Static);
                var mtd = mtdField?.GetValue(null) ?? mtdProp?.GetValue(null);
                if (mtd == null) { Log("GlobalAssets.mapTypeData is null."); return null; }

                var m = mtd.GetType().GetMethod("GetMapThemeFromID",
                    new[] { typeof(byte), typeof(bool) });
                if (m == null)
                {
                    Log("GetMapThemeFromID(byte, bool) not found on mapTypeData.");
                    return null;
                }
                var theme = m.Invoke(mtd, new object[] { (byte)themeId, false });
                if (theme == null) { Log($"GetMapThemeFromID({themeId}) returned null."); }
                return theme;
            }
            catch (Exception ex)
            {
                Log($"ResolveTheme failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Apply all the gen parameters to the worker TGC. Mirrors
        /// the field set Pangu writes before calling GenerateInternal:
        /// terrainType (from SettingsManager.mapType), seed, size, theme,
        /// mountains, water, lakes (from SettingsManager.mapLakeValue).</summary>
        private static void ApplyTgcGenParameters(TerrainGeneratorController tgc,
            int terrainSeed, int mapSizeIdx, object? theme,
            int mountainValue, int waterValue)
        {
            var tgcType = tgc.GetType();
            try
            {
                // terrainType from SettingsManager.Instance.mapType.
                var smType = AccessTools.TypeByName("SettingsManager");
                if (smType != null)
                {
                    var instProp = smType.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static);
                    var inst = instProp?.GetValue(null);
                    if (inst != null)
                    {
                        var mtField = smType.GetField("mapType",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var mtVal = mtField?.GetValue(inst);
                        if (mtVal != null)
                        {
                            var ttField = AccessTools.Field(tgcType, "terrainType");
                            ttField?.SetValue(tgc, mtVal);
                        }
                    }
                }

                SetTgcField(tgc, tgcType, "seed", terrainSeed);
                // size is an enum — convert from int.
                var sizeField = AccessTools.Field(tgcType, "size");
                if (sizeField != null && sizeField.FieldType.IsEnum)
                {
                    sizeField.SetValue(tgc, Enum.ToObject(sizeField.FieldType, mapSizeIdx));
                }
                if (theme != null) SetTgcField(tgc, tgcType, "theme", theme);
                SetTgcField(tgc, tgcType, "mountains", Mathf.Clamp01(mountainValue / 255f));
                SetTgcField(tgc, tgcType, "water", Mathf.Clamp01(waterValue / 255f));

                // lakes from static SettingsManager.mapLakeValue
                if (smType != null)
                {
                    var lakesF = smType.GetField("mapLakeValue",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    var lakesV = lakesF?.GetValue(null);
                    if (lakesV != null) SetTgcField(tgc, tgcType, "lakes", lakesV);
                }

                Log($"Applied TGC params: seed={terrainSeed} size={mapSizeIdx} " +
                    $"theme={(theme != null ? theme.GetType().Name : "null")} " +
                    $"mountains={mountainValue} water={waterValue}");
            }
            catch (Exception ex) { Log($"ApplyTgcGenParameters failed: {ex.Message}"); }
        }

        private static void SetTgcField(object tgc, Type tgcType, string fieldName, object value)
        {
            try
            {
                var f = AccessTools.Field(tgcType, fieldName);
                if (f != null) { f.SetValue(tgc, value); return; }
                var p = tgcType.GetProperty(fieldName);
                if (p != null && p.CanWrite) p.SetValue(tgc, value);
            }
            catch { }
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

        /// <summary>Override PreviewOverlay's caption with a comprehensive
        /// metadata string: seed, biome, map size, 4 difficulty selections,
        /// river count, water %. Reads the difficulty values from
        /// SettingsManager's static fields:
        ///   startingResourcesDifficultyValue (Resources)
        ///   diseaseDifficultyValue           (Maladies)
        ///   animalDifficultyValue            (Wildlife)
        ///   raiderDifficultyValue            (Raiders)
        /// FF's Difficulty enum maps Easy/Normal/Hard → Pioneer/Trailblazer/Vanquisher
        /// in the UI.</summary>
        private static void BuildRichCaption(string rawSeed, int mapSizeIdx, TerrainGenerator tg)
        {
            try
            {
                string biome = (RiversRestoredMod.RiverPreset?.Value ?? RiverPresetMode.IdyllicValley).ToString();
                string size = mapSizeIdx switch
                {
                    0 => "Small",
                    1 => "Medium",
                    2 => "Large",
                    _ => $"Size{mapSizeIdx}",
                };

                // River + water % — reuse what MapPreviewRenderer's caption
                // would compute. Recompute here from gen state.
                int riverCount = 0;
                int waterPct = 0;
                try
                {
                    var gd = AccessTools.Field(typeof(TerrainGenerator), "_generationData")?.GetValue(tg);
                    if (gd != null)
                    {
                        var rField = gd.GetType().GetField("rivers",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var rivers = rField?.GetValue(gd) as System.Collections.IList;
                        riverCount = rivers?.Count ?? 0;

                        var waField = gd.GetType().GetField("waterAreas",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var waterAreas = waField?.GetValue(gd) as System.Collections.IList;
                        var hnField = gd.GetType().GetField("heightNoise",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (hnField?.GetValue(gd) is float[,] hn && waterAreas != null)
                        {
                            // Approximate: count cells inside water area bboxes
                            // / total cells. Same metric MapPreviewRenderer uses.
                            int totalCells = hn.GetLength(0) * hn.GetLength(1);
                            int waterCells = 0;
                            var waType = AccessTools.TypeByName("TerrainGen.TerrainGenerator+WaterArea");
                            var fMinX = waType?.GetField("minX");
                            var fMinZ = waType?.GetField("minZ");
                            var fMaxX = waType?.GetField("maxX");
                            var fMaxZ = waType?.GetField("maxZ");
                            if (fMinX != null && fMinZ != null && fMaxX != null && fMaxZ != null)
                            {
                                foreach (var w in waterAreas)
                                {
                                    if (w == null) continue;
                                    int minX = (int)fMinX.GetValue(w);
                                    int minZ = (int)fMinZ.GetValue(w);
                                    int maxX = (int)fMaxX.GetValue(w);
                                    int maxZ = (int)fMaxZ.GetValue(w);
                                    waterCells += Math.Max(0, (maxX - minX + 1)) * Math.Max(0, (maxZ - minZ + 1));
                                }
                            }
                            // Clamp to 100 — bbox-area double-counts when
                            // multiple WaterAreas overlap (e.g., a river
                            // terminating in a lake), can otherwise produce
                            // values > 100. Real fix would walk masks for
                            // unique cells but the clamp is cheap and visually
                            // correct.
                            waterPct = totalCells > 0
                                ? Math.Min(100, (int)Math.Round(100.0 * waterCells / totalCells))
                                : 0;
                        }
                    }
                }
                catch { }

                // Read 4 difficulty values from SettingsManager (static).
                string res = ReadDifficulty("startingResourcesDifficultyValue");
                string mal = ReadDifficulty("diseaseDifficultyValue");
                string wld = ReadDifficulty("animalDifficultyValue");
                string raid = ReadDifficulty("raiderDifficultyValue");

                // 3 columns × 2 rows:
                //   Left:    "Seed X · Biome · Size"     | "N river(s) · N% water"
                //   Mid:     "Resources: T"              | "Wildlife: T"
                //   Right:   "Maladies: T"               | "Raiders: T"
                PreviewOverlay.LatestCaptionLeft =
                    $"Seed {rawSeed} · {biome} · {size}\n" +
                    $"{riverCount} river(s) · {waterPct}% water";
                PreviewOverlay.LatestCaptionMid =
                    $"Resources: {res}\nWildlife: {wld}";
                PreviewOverlay.LatestCaptionRight =
                    $"Maladies: {mal}\nRaiders: {raid}";
                PreviewOverlay.LatestCaption = ""; // unused with split columns
            }
            catch (Exception ex)
            {
                Log($"BuildRichCaption failed: {ex.Message}");
            }
        }

        /// <summary>Read a Difficulty-typed static field/property from
        /// SettingsManager and convert to its UI label (Pioneer/
        /// Trailblazer/Vanquisher/VeryHard).</summary>
        private static string ReadDifficulty(string fieldName)
        {
            try
            {
                var smType = AccessTools.TypeByName("SettingsManager");
                if (smType == null) return "?";
                // Try property first (FF exposes via property; backing field has _ prefix)
                var prop = smType.GetProperty(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                object? val = prop?.GetValue(null);
                if (val == null)
                {
                    var fld = smType.GetField(fieldName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                              ?? smType.GetField("_" + fieldName,
                                  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    val = fld?.GetValue(null);
                }
                if (val == null) return "?";
                return DifficultyToUiLabel(val.ToString());
            }
            catch { return "?"; }
        }

        /// <summary>FF's Difficulty enum (Easy/Normal/Hard/VeryHard) maps to
        /// the UI labels (Pioneer/Trailblazer/Vanquisher/VeryHard) — but for
        /// the caption we use single-letter abbreviations to fit narrow
        /// columns.</summary>
        private static string DifficultyToUiLabel(string difficultyName) => difficultyName switch
        {
            "Easy"     => "P",   // Pioneer
            "Normal"   => "T",   // Trailblazer
            "Hard"     => "V",   // Vanquisher
            "VeryHard" => "X",   // (UI-hidden tier)
            _ => difficultyName,
        };

        /// <summary>Call TG.ResetGeneratedData() to clear any prior gen state.</summary>
        private static void TryResetGeneratedData(TerrainGenerator tg)
        {
            try
            {
                var m = AccessTools.Method(tg.GetType(), "ResetGeneratedData", new Type[0]);
                m?.Invoke(tg, null);
            }
            catch (Exception ex) { Log($"ResetGeneratedData threw: {ex.Message}"); }
        }

        /// <summary>Set a public/non-public bool field on the worker TG.</summary>
        private static void SetWorkerFlag(TerrainGenerator tg, string fieldName, bool value)
        {
            try
            {
                var f = AccessTools.Field(tg.GetType(), fieldName);
                if (f != null && f.FieldType == typeof(bool)) f.SetValue(tg, value);
            }
            catch { }
        }

        /// <summary>Set the worker's RNG seed from terrainSettings.seed —
        /// makes the gen deterministic per seed.</summary>
        private static void SetRngSeed(TerrainGenerator tg)
        {
            try
            {
                var rngField = AccessTools.Field(tg.GetType(), "rng")
                              ?? AccessTools.Field(tg.GetType(), "_rng");
                var rng = rngField?.GetValue(tg);
                if (rng == null) return;

                var tsField = AccessTools.Field(tg.GetType(), "terrainSettings");
                var ts = tsField?.GetValue(tg);
                int seed = 1;
                if (ts != null)
                {
                    var sField = ts.GetType().GetField("seed",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (sField != null) seed = Math.Max(1, (int)(sField.GetValue(ts) ?? 1));
                }

                var seedProp = rng.GetType().GetProperty("Seed");
                if (seedProp != null && seedProp.CanWrite)
                {
                    seedProp.SetValue(rng, (uint)seed);
                }
                else
                {
                    var seedF = rng.GetType().GetField("Seed",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (seedF != null) seedF.SetValue(rng, (uint)seed);
                }
            }
            catch (Exception ex) { Log($"SetRngSeed failed: {ex.Message}"); }
        }

        /// <summary>Allocate a fresh _generationData and assign to TG.
        /// Pangu does this between PreGenerate and GenerateAsync.</summary>
        private static void CreateFreshGenerationData(TerrainGenerator tg)
        {
            try
            {
                var gdField = AccessTools.Field(typeof(TerrainGenerator), "_generationData");
                if (gdField == null) { Log("_generationData field not found."); return; }
                var gdType = gdField.FieldType;
                var inst = Activator.CreateInstance(gdType);
                gdField.SetValue(tg, inst);
            }
            catch (Exception ex) { Log($"CreateFreshGenerationData failed: {ex.Message}"); }
        }

        /// <summary>Invoke a no-arg method on the TG. Returns true on success.</summary>
        private static bool InvokeMethodIfPresent(TerrainGenerator tg, string methodName)
        {
            try
            {
                var m = AccessTools.Method(tg.GetType(), methodName, new Type[0]);
                if (m == null) { Log($"{methodName} method not found."); return false; }
                m.Invoke(tg, null);
                return true;
            }
            catch (TargetInvocationException tex)
            {
                var inner = tex.InnerException;
                Log($"  {methodName} threw: {(inner != null ? inner.GetType().Name + ": " + inner.Message : tex.Message)}");
                return false;
            }
            catch (Exception ex)
            {
                Log($"  {methodName} threw: {ex.GetType().Name}: {ex.Message}");
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
