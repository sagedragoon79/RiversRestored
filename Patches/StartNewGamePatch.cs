using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Prefix on StartSceneManager.StartNewGame(string, string). Mirrors
    /// Pangu's "start_new_request" cancel site (Pangu_FF.decompiled.cs:2186).
    ///
    /// When the user clicks Start, we MUST tear down the preview worker
    /// before FF begins its scene transition. Otherwise the still-loaded
    /// "Map" scene with our mutated TerrainGeneratorController state gets
    /// adopted by FF's gameplay flow and hangs the load screen at ~85%.
    ///
    /// Pairs with the OnSceneWasInitialized discriminator in
    /// RiversRestoredMod, which handles the case where FF re-initializes
    /// the Map scene independently. Together they replicate Pangu's
    /// multi-layer cleanup discipline.
    /// </summary>
    internal static class StartNewGamePatch
    {
        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var ssmType = AccessTools.TypeByName("StartSceneManager");
                if (ssmType == null)
                {
                    MelonLogger.Warning("[RR][StartNewGame] StartSceneManager type not found — patch skipped.");
                    return;
                }

                var mi = ssmType.GetMethod("StartNewGame",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(string), typeof(string) }, null);
                if (mi == null)
                {
                    MelonLogger.Warning("[RR][StartNewGame] StartNewGame(string, string) not found — patch skipped.");
                    return;
                }

                var prefix = typeof(StartNewGamePatch).GetMethod(nameof(Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(mi, prefix: new HarmonyMethod(prefix));
                MelonLogger.Msg("[RR][StartNewGame] Prefix installed on StartSceneManager.StartNewGame.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RR][StartNewGame] Apply failed: {ex.Message}");
            }
        }

        private static void Prefix()
        {
            try
            {
                MelonLogger.Msg("[RR][StartNewGame] Start clicked — hard-cancelling preview worker + unloading Map scene.");
                // unload:true — FF will additively LoadSceneAsync("Map")
                // for gameplay (Assembly-CSharp.cs:99375). If our preview
                // already left a "Map" scene loaded, FF ends up with two
                // Map scenes and picks the wrong TerrainGeneratorController,
                // hanging the load screen. Empirical confirmation: Pangu
                // + release-RR works because Pangu unloads here; RR-only
                // with unload:false hangs. We have plenty of time — FF's
                // state machine takes multiple frames before reaching its
                // own LoadSceneAsync, so async unload completes first.
                PreviewGenWorker.HardCancel(unload: true);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RR][StartNewGame] Prefix failed: {ex.Message}");
            }
        }
    }
}
