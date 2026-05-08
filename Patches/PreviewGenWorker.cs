using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using TerrainGen;
using UnityEngine;

namespace RiversRestored.Patches
{
    /// <summary>
    /// On-demand preview-gen trigger. Bridges the gap between RR's panel
    /// and FF's gen pipeline — when the user clicks the panel's "PREVIEW"
    /// button, this finds a TerrainGenerator instance and runs the minimum
    /// gen-stage sequence to populate <c>_generationData.heightNoise</c>
    /// and <c>_generationData.waterAreas</c>, then triggers our renderer.
    ///
    /// V1 strategy: use whatever <see cref="TerrainGenerator"/> is already
    /// in the scene (live or Pangu's worker). If nothing is found, log
    /// and bail. This avoids the complexity of spawning our own worker
    /// (Pangu's pattern needs scene-template loading and a coroutine
    /// dispatcher) — a future iteration can add a true RR-spawned worker
    /// for full standalone behavior.
    /// </summary>
    internal static class PreviewGenWorker
    {
        // Throttle re-clicks while a preview is mid-flight.
        private static bool _busy = false;

        // Lazily-spawned worker GameObject. Used only when no live
        // TerrainGenerator exists in the scene (e.g. user is on the New
        // Game screen without Pangu running). Persists across previews
        // so we don't pay the AddComponent cost per click.
        private static GameObject? _workerGO;

        public static void TriggerPreview()
        {
            if (_busy)
            {
                Log("Already running — ignoring click.");
                return;
            }
            _busy = true;
            try
            {
                var tg = FindUsableTerrainGenerator();
                if (tg == null)
                {
                    tg = SpawnWorkerGenerator();
                    if (tg == null)
                    {
                        Log("Could not spawn a TerrainGenerator worker. " +
                            "Preview unavailable. Try enabling Pangu's 'Preview Map Seed'.");
                        return;
                    }
                    Log($"Spawned worker TerrainGenerator on '{tg.gameObject.name}'.");
                }
                else
                {
                    Log($"Using existing TerrainGenerator on '{tg.gameObject.name}' for preview.");
                }

                // Read seed from FF's seed input field if available.
                string seed = TryReadSeedFromUI();
                if (!string.IsNullOrEmpty(seed))
                {
                    Log($"Seed from UI: '{seed}'");
                    TrySetSeedOnGenerator(tg, seed);
                }

                // Reset our renderer's per-gen flag so it'll fire again.
                MapPreviewRenderer.RenderedThisGen = false;

                // Invoke the minimum sequence: Stage 38 (river paths) +
                // Stage 50 (water). Stages 1-3 (heightnoise) etc. are
                // assumed already-run by FF's PreGenerateShared chain on
                // the live TerrainGenerator. If heightnoise turns out null,
                // we'd need to invoke earlier stages too.
                InvokeStageIfPresent(tg, "PreGenerateShared");
                InvokeStageIfPresent(tg, "PreGenerate");
                // The full pipeline:
                foreach (var s in new[] {
                    "GenerateAsync_Setup_Stage1",
                    "GenerateAsync_Voronoi_Stage2",
                    "GenerateAsync_Noise_Stage3",
                    "GenerateAsync_Features_Stage5",
                    "GenerateAsync_Biomes_Stage10",
                    "GenerateAsync_Prototypes_Stage20",
                    "GenerateAsync_PreWater_Stage37",
                    "GenerateAsync_RiverPaths_Stage38",
                    "GenerateAsync_PaintBiomes_Stage40",
                    "GenerateAsync_Water_Stage50",
                })
                {
                    InvokeStageIfPresent(tg, s);
                }

                // Diagnostic state dump so we can see what initialization
                // worked vs failed. Log-only; doesn't affect rendering.
                DumpGeneratorState(tg);

                // Render the result — TryRender is gated by EnableMapPreviewRender
                // so it'll only fire if the user has the pref on.
                MapPreviewRenderer.TryRender(tg, "PreviewButton");
                Log("Preview gen complete.");
            }
            catch (Exception ex)
            {
                Log($"Preview gen failed: {ex.Message}");
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>Find any TerrainGenerator instance in the scene.
        /// Could be the live one, Pangu's worker, or a previously-cached
        /// one from RR's own hooks.</summary>
        private static TerrainGenerator? FindUsableTerrainGenerator()
        {
            // First: RR's cached generator from any prior gen entry.
            if (RiverSettingsPatch.CachedGenerator != null)
                return RiverSettingsPatch.CachedGenerator;

            // Fallback: scene-wide search.
            return UnityEngine.Object.FindObjectOfType<TerrainGenerator>();
        }

        /// <summary>Spawn a hidden GameObject with a TerrainGenerator
        /// component for previewing. Reuses the same GO across previews.
        /// V2: this is a minimal worker — no scene template loading,
        /// just the bare component. Some stages may NRE if they reach
        /// for prefabs that aren't loaded; if so, the stage invocation
        /// catches the exception and continues to the next.</summary>
        private static TerrainGenerator? SpawnWorkerGenerator()
        {
            try
            {
                if (_workerGO != null)
                {
                    var existing = _workerGO.GetComponent<TerrainGenerator>();
                    if (existing != null) return existing;
                    UnityEngine.Object.Destroy(_workerGO);
                    _workerGO = null;
                }

                _workerGO = new GameObject("RR_PreviewWorker");
                UnityEngine.Object.DontDestroyOnLoad(_workerGO);
                var tg = _workerGO.AddComponent<TerrainGenerator>();
                return tg;
            }
            catch (Exception ex)
            {
                Log($"Worker spawn failed: {ex.Message}");
                if (_workerGO != null) { UnityEngine.Object.Destroy(_workerGO); _workerGO = null; }
                return null;
            }
        }

        /// <summary>Best-effort: read the seed text from FF's seed input
        /// field on the New Game UI. Returns empty if not found.</summary>
        private static string TryReadSeedFromUI()
        {
            try
            {
                // The seed input is a TMP_InputField with text containing
                // the current seed value. We'd need to find it by name.
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

        /// <summary>Try to set the seed on the TerrainGenerator (or its
        /// _generationData) via reflection. Field names vary across FF
        /// versions, so try common ones.</summary>
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
                // Fallback: try _generationData.seed.
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
            catch (Exception ex)
            {
                Log($"Seed set failed: {ex.Message}");
            }
        }

        /// <summary>Invoke a stage method by name, no-op if not present.
        /// Unwraps TargetInvocationException to show the real inner
        /// exception in the log — the default "Exception has been thrown
        /// by the target of an invocation" message is useless on its own.</summary>
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

        /// <summary>Diagnostic dump of TerrainGenerator's instance state
        /// after the stage sequence runs. Logs which key fields are
        /// populated vs null, so we know what initialization is missing.</summary>
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

                // Inspect _generationData for heightnoise + waterAreas
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
            catch (Exception ex)
            {
                Log($"  state dump failed: {ex.Message}");
            }
        }

        private static void Log(string msg) =>
            MelonLogger.Msg($"[RR][PreviewGen] {msg}");
    }
}
