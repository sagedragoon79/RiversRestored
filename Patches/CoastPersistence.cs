using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using TerrainGen;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Keeps a coast across save and reload.
    ///
    /// FF regenerates heightNoise from the seed on every load, so the coast must
    /// be carved again during that replay, with the exact plan the save was
    /// generated with. A small text sidecar next to the save records that plan;
    /// a save without one is a map generated without the coast and is left
    /// untouched.
    ///
    /// Path scheme matches the river sidecar: FF creates a new slot folder with a
    /// fresh timestamp on every save, so the file is written to the flat path
    /// <c>Save/{saveName}.coast</c>, which load can always find. The sidecar
    /// carries the map seed and is ignored when it does not match the loading
    /// map, which protects against a stale file under a reused save name
    /// (for example "AutoSave 1" from a different settlement).
    /// </summary>
    internal static class CoastPersistence
    {
        private const string Extension = ".coast";

        /// <summary>Plan in effect for the map that was generated or loaded most
        /// recently; null when that map has no coast. Written on every save.</summary>
        public static CoastPlan? CurrentPlan;

        private static bool _loadResolved;
        private static string _loadKey = "";
        private static CoastPlan? _loadPlan;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo? saveMI = AccessTools.Method(typeof(SaveManager), "Save",
                    new[] { typeof(string), typeof(bool), typeof(bool) });
                if (saveMI == null)
                {
                    CoastLog.Warning("SaveManager.Save(string,bool,bool) not found; coasts will not persist across reload.");
                    return;
                }
                harmony.Patch(saveMI, postfix: new HarmonyMethod(
                    typeof(CoastPersistence).GetMethod(nameof(SavePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                CoastLog.Msg("Hooked SaveManager.Save (postfix, coast sidecar)");
            }
            catch (Exception ex)
            {
                CoastLog.Error($"Save hook failed: {ex}");
            }
        }

        // ── Save ───────────────────────────────────────────────────────────

        private static void SavePostfix(string savedGameFileNameNoExtension)
        {
            try
            {
                if (string.IsNullOrEmpty(savedGameFileNameNoExtension)) return;
                string path = FlatSidecarPath(savedGameFileNameNoExtension);

                if (CurrentPlan == null)
                {
                    // A vanilla map saved under a name that once belonged to a
                    // coast map must not inherit that coast.
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        CoastLog.Msg($"Save: removed stale coast sidecar {path} (this map has no coast).");
                    }
                    return;
                }

                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(path, CurrentPlan.ToSidecarLines(RiversRestoredMod.Version));
                CoastLog.Msg($"Save: coast sidecar written to {path} ({CurrentPlan})");
            }
            catch (Exception ex)
            {
                CoastLog.Error($"SavePostfix failed: {ex}");
            }
        }

        // ── Load ───────────────────────────────────────────────────────────

        /// <summary>The plan to replay for the save that is loading, or null to
        /// leave the map exactly as saved. Cached per active save name because
        /// both the Stage 5 and the Stage 37 prefix ask.</summary>
        public static CoastPlan? PlanForLoadingSave(TerrainGenerator tg)
        {
            string asf = SaveManager.activeSaveFileName ?? "";
            if (_loadResolved && _loadKey == asf) return _loadPlan;
            _loadResolved = true;
            _loadKey = asf;
            _loadPlan = null;

            try
            {
                if (string.IsNullOrEmpty(asf))
                {
                    CoastLog.Warning("Load: SaveManager.activeSaveFileName is empty; leaving the map as saved.");
                    return null;
                }

                string bare = Path.GetFileName(asf.Replace('/', Path.DirectorySeparatorChar));
                string flat = FlatSidecarPath(bare);
                string canonical = Path.Combine(BaseDir, SaveManager.folderName, asf + Extension)
                    .Replace('/', Path.DirectorySeparatorChar);
                string? path = File.Exists(flat) ? flat : File.Exists(canonical) ? canonical : null;
                if (path == null)
                {
                    CoastLog.Msg($"Load: no coast sidecar for '{bare}'; leaving the map as saved.");
                    return null;
                }

                CoastPlan? plan = CoastPlan.FromSidecarLines(File.ReadAllLines(path), out string error);
                if (plan == null)
                {
                    CoastLog.Warning($"Load: coast sidecar {path} is unreadable ({error}); leaving the map as saved.");
                    return null;
                }
                if (plan.Seed != tg.terrainSettings.seed)
                {
                    CoastLog.Warning($"Load: coast sidecar {path} is for seed {plan.Seed} but this map is seed " +
                                     $"{tg.terrainSettings.seed}; ignoring it (stale file from another settlement named '{bare}').");
                    return null;
                }

                CoastLog.Msg($"Load: coast sidecar {path} -> {plan}");
                _loadPlan = plan;
                return plan;
            }
            catch (Exception ex)
            {
                CoastLog.Error($"Load: sidecar lookup failed; leaving the map as saved: {ex}");
                return null;
            }
        }

        private static string BaseDir => AppDomain.CurrentDomain.BaseDirectory;

        private static string FlatSidecarPath(string nameOrPath)
        {
            string bare = Path.GetFileName(nameOrPath.Replace('/', Path.DirectorySeparatorChar));
            return Path.Combine(BaseDir, SaveManager.folderName, bare + Extension)
                .Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
