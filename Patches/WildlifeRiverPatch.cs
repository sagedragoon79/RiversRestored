using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Optional feature (cfg <c>RiversDontBlockWildlife</c>, OFF by default):
    /// let DEER / herd wildlife spawn across an RR river instead of being walled
    /// off by FF's path-to-town gate.
    ///
    /// Vanilla gates wildlife spawn/wander points on
    /// <c>aiPathfinder.DoesGeneralPathExistToTown(point, BridgesOnly)</c>
    /// (AnimalSpawnArea.IsValidForSpawnOrWanderPoint, decomp ff_full_dlc.cs:36510).
    /// An RR river carves impassable water that splits the flood-fill, so a
    /// region not connected to the town fails the gate → no deer there (seeding
    /// AND respawn). See game-systems/river-system.md.
    ///
    /// This bypass is **scoped**: a flag is raised only for the duration of
    /// IsValidForSpawnOrWanderPoint, and the DoesGeneralPathExistToTown prefix
    /// short-circuits to true ONLY while that flag is set. So we neutralize the
    /// one gate that blocks deer without touching the same API everywhere else
    /// (villager work assignment, building placement, raider pathing, resource
    /// reachability) — those still see real reachability, so villagers never get
    /// assigned jobs they can't walk to. Deer are area-bound grazers that never
    /// path to town, so spawning them in a cut-off region is safe.
    ///
    /// TRAPPING is intentionally NOT bypassed: its gate (WallsBlock) protects
    /// hunters from being sent to traps they can't physically reach.
    ///
    /// Fresh gen: the bypass makes spawn points validate everywhere, so deer
    /// seed across rivers and inUse is set naturally. Existing save (region
    /// already seeded zero at gen, inUse locked false, spawn points empty):
    /// <see cref="TryRepairLoadedSpawnAreas"/> re-runs CalculateSpawnPoints on
    /// empty areas (now passing the bypassed gate) and sets inUse=true, so the
    /// daily respawn loop repopulates them.
    /// </summary>
    internal static class WildlifeRiverPatch
    {
        // Raised only inside AnimalSpawnArea.IsValidForSpawnOrWanderPoint so the
        // DoesGeneralPathExistToTown bypass is confined to the deer spawn gate.
        private static bool _inSpawnValidity = false;
        private static bool _repairedThisScene = false;

        public static void ResetForSceneLoad()
        {
            _inSpawnValidity = false;
            _repairedThisScene = false;
        }

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                Type? saType = AccessTools.TypeByName("AnimalSpawnArea");
                Type? pfType = AccessTools.TypeByName("AIPathfinder");
                Type? ffType = AccessTools.TypeByName("AIPathfinding.AIGridGraph+FloodFillType");
                if (saType == null || pfType == null || ffType == null)
                {
                    Log($"types missing — AnimalSpawnArea={saType != null} AIPathfinder={pfType != null} FloodFillType={ffType != null}; wildlife bypass disabled.");
                    return;
                }

                // Scope flag around IsValidForSpawnOrWanderPoint(Vector3).
                MethodInfo? isValid = AccessTools.Method(saType, "IsValidForSpawnOrWanderPoint",
                    new[] { typeof(Vector3) });
                if (isValid != null)
                {
                    harmony.Patch(isValid,
                        prefix: new HarmonyMethod(typeof(WildlifeRiverPatch)
                            .GetMethod(nameof(IsValidPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                        finalizer: new HarmonyMethod(typeof(WildlifeRiverPatch)
                            .GetMethod(nameof(IsValidFinalizer), BindingFlags.Static | BindingFlags.NonPublic)));
                }
                else
                {
                    Log("IsValidForSpawnOrWanderPoint(Vector3) not found — wildlife bypass disabled.");
                    return;
                }

                // Bypass the path-to-town result while the flag is set.
                MethodInfo? doesPath = AccessTools.Method(pfType, "DoesGeneralPathExistToTown",
                    new[] { typeof(Vector3), ffType, typeof(bool) });
                if (doesPath != null)
                {
                    harmony.Patch(doesPath,
                        prefix: new HarmonyMethod(typeof(WildlifeRiverPatch)
                            .GetMethod(nameof(DoesPathPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                    Log("Hooked AnimalSpawnArea.IsValidForSpawnOrWanderPoint + AIPathfinder.DoesGeneralPathExistToTown (wildlife river bypass; gated by RiversDontBlockWildlife).");
                }
                else
                {
                    Log("DoesGeneralPathExistToTown(Vector3, FloodFillType, bool) not found — wildlife bypass disabled.");
                }
            }
            catch (Exception ex)
            {
                Log($"Apply failed: {ex.Message}");
            }
        }

        private static void IsValidPrefix()
        {
            if (RiversRestoredMod.RiversDontBlockWildlife?.Value ?? false)
                _inSpawnValidity = true;
        }

        // Finalizer always clears, even if the method threw — so the flag can't
        // leak into unrelated DoesGeneralPathExistToTown callers.
        private static void IsValidFinalizer()
        {
            _inSpawnValidity = false;
        }

        private static bool DoesPathPrefix(ref bool __result)
        {
            if (_inSpawnValidity && (RiversRestoredMod.RiversDontBlockWildlife?.Value ?? false))
            {
                __result = true;   // pretend the spawn point is reachable from town
                return false;      // skip the real flood-fill check
            }
            return true;           // everything else: real reachability
        }

        /// <summary>One-shot per scene (toggle-gated): on an existing save the
        /// cut-off region's spawn areas were computed at gen WITH the gate, so
        /// they have empty spawn points and inUse=false. Recompute the empty
        /// ones (now passing the bypassed gate) and flag them inUse so the daily
        /// respawn loop repopulates them. Fresh gens already seed correctly via
        /// the bypass, so only empty areas are recomputed (keeps cost down).
        /// Call from OnUpdate once terrain is loaded.</summary>
        public static void TryRepairLoadedSpawnAreas()
        {
            if (_repairedThisScene) return;
            if (!(RiversRestoredMod.RiversDontBlockWildlife?.Value ?? false)) return;
            try
            {
                Type? amType = AccessTools.TypeByName("AnimalManager");
                if (amType == null) { _repairedThisScene = true; return; }
                var am = UnityEngine.Object.FindObjectOfType(amType);
                if (am == null) return; // AnimalManager not up yet — retry next frame

                var gridField = amType.GetField("spawnAreaGrid",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var grid = gridField?.GetValue(am) as IList;
                if (grid == null || grid.Count == 0) return; // not populated yet — retry

                Type? saType = AccessTools.TypeByName("AnimalSpawnArea");
                if (saType == null) { _repairedThisScene = true; return; }
                var inUseProp = saType.GetProperty("inUse",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var allSpawnPtsField = saType.GetField("allSpawnPoints",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var calcMI = saType.GetMethod("CalculateSpawnPoints",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);

                int touched = 0, recomputed = 0;
                foreach (var area in grid)
                {
                    if (area == null) continue;
                    inUseProp?.SetValue(area, true);
                    touched++;

                    // Recompute only areas with no spawn points (the cut-off
                    // ones). Areas that already have points keep theirs.
                    var pts = allSpawnPtsField?.GetValue(area) as ICollection;
                    if ((pts == null || pts.Count == 0) && calcMI != null)
                    {
                        try { calcMI.Invoke(area, null); recomputed++; }
                        catch { }
                    }
                }

                _repairedThisScene = true;
                Log($"Wildlife river bypass: flagged inUse on {touched} spawn area(s), recomputed {recomputed} empty one(s) — deer can now repopulate cut-off regions.");
            }
            catch (Exception ex)
            {
                Log($"TryRepairLoadedSpawnAreas exception: {ex.Message}");
                _repairedThisScene = true;
            }
        }

        private static void Log(string msg) => RiversRestoredMod.Log.Msg($"[RR][Wildlife] {msg}");
    }
}
