using System;
using System.Reflection;
using HarmonyLib;
using TerrainGen;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Playable inset: how far in from the terrain edge the buildable, walkable
    /// world starts. Vanilla keeps 150 m per edge through one private static,
    /// <c>PreGameInitializer.navMeshBuffer = 300f</c>, read by
    /// <c>GetPathingGridRect</c> (camera clamp, NavMesh bake volume, mineral and
    /// foraging bounds, tree exclusion) and by the AI grid builder. Every
    /// consumer calls <c>GetPathingGridRect</c>, so a prefix on it can set the
    /// static from the current map-edge plan on each call, whatever order the
    /// scene's Start methods and the generator run in.
    ///
    /// Resolution: a loading save (generator replaying with useSavedMap) takes
    /// its inset from that save's sidecar, or vanilla when there is none, so
    /// maps generated with a different inset keep the grid they were built for.
    /// Anything else (fresh generation, the seed preview) takes the preference.
    /// </summary>
    internal static class MapEdgePatches
    {
        private static FieldInfo? _navMeshBuffer;
        private static float _lastApplied = float.NaN;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                Type? pgi = AccessTools.TypeByName("PreGameInitializer");
                if (pgi == null)
                {
                    CoastLog.Warning("PreGameInitializer not found; playable inset stays vanilla.");
                    return;
                }
                _navMeshBuffer = AccessTools.Field(pgi, "navMeshBuffer");
                MethodInfo? rect = AccessTools.Method(pgi, "GetPathingGridRect");
                if (_navMeshBuffer == null || rect == null)
                {
                    CoastLog.Warning($"PreGameInitializer members missing (navMeshBuffer={(_navMeshBuffer != null ? "OK" : "MISSING")}, " +
                                     $"GetPathingGridRect={(rect != null ? "OK" : "MISSING")}); playable inset stays vanilla.");
                    return;
                }
                harmony.Patch(rect, prefix: new HarmonyMethod(
                    typeof(MapEdgePatches).GetMethod(nameof(GetPathingGridRectPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                CoastLog.Msg("Hooked PreGameInitializer.GetPathingGridRect (prefix, playable inset)");
            }
            catch (Exception ex)
            {
                CoastLog.Error($"Playable-inset hook failed: {ex}");
            }
        }

        // Parameter name must match the original's first parameter.
        private static void GetPathingGridRectPrefix(TerrainGenerator terrainGenerator)
        {
            try
            {
                float inset = ResolveInset(terrainGenerator);
                ApplyInset(inset);
            }
            catch (Exception ex)
            {
                CoastLog.Error($"GetPathingGridRectPrefix failed: {ex}");
            }
        }

        private static float ResolveInset(TerrainGenerator? tg)
        {
            if (tg != null && tg.Data != null && tg.Data.useSavedMap)
            {
                CoastPlan? plan = CoastPersistence.PlanForLoadingSave(tg);
                return plan?.PlayableInset ?? CoastPlan.VanillaInset;
            }
            return CoastPlan.PrefInset();
        }

        /// <summary>Writes the static that defines the pathing rect. Cheap enough
        /// to run on every call; logs only when the value changes.</summary>
        public static void ApplyInset(float inset)
        {
            if (_navMeshBuffer == null) return;
            float buffer = inset * 2f;
            if (!float.IsNaN(_lastApplied) && Math.Abs(_lastApplied - buffer) < 0.01f) return;
            _navMeshBuffer.SetValue(null, buffer);
            _lastApplied = buffer;
            CoastLog.Msg($"Playable inset set to {inset:F0} m per edge (navMeshBuffer {buffer:F0}; vanilla 150 / 300).");
        }
    }
}
