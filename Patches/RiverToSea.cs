using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using TerrainGen;
using Voronoi;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Steers the first river(s) of a coast map into the sea.
    ///
    /// Vanilla Stage 38 picks every river's start and target from the perimeter
    /// Voronoi points that sit ABOVE the water plane, so a coastal edge, whose
    /// perimeter points are all below water, is never targeted: rivers reach the
    /// sea only by accident. This prefix on the private <c>RecurseRiver</c> does
    /// two things for a steered walk:
    ///
    ///  1. On the top-level call (empty point list) it swaps the target for the
    ///     coastal perimeter point nearest the river's start.
    ///  2. On each recursive step it checks the point the walk is about to stop
    ///     on. RecurseRiver ends a river at the first below-water point; if that
    ///     water is a lake or pond rather than the sea, the step is vetoed, the
    ///     walk fails, and vanilla retries with a new start.
    ///
    /// Vanilla's own endpoint check accepts the result because the ocean type
    /// allows river ends. A cap on attempts hands the remaining tries back to
    /// vanilla so a map never loses its rivers because of this feature.
    /// </summary>
    internal static class RiverToSea
    {
        private const int MaxAttempts = 300;

        private static int _attempts;
        private static int _retargeted;
        private static int _lakeVetoes;
        private static bool _gaveUp;
        private static TerrainRiver? _activeWalk;
        private static TerrainGenerator.WaterArea? _sea;
        private static bool _seaLookedUp;

        private static MethodInfo? _interpolatedHeight;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo? target = AccessTools.Method(typeof(TerrainGenerator), "RecurseRiver");
                if (target == null)
                {
                    CoastLog.Warning("TerrainGenerator.RecurseRiver not found; rivers will not be steered to the sea.");
                    return;
                }
                _interpolatedHeight = AccessTools.Method(typeof(TerrainGenerator), "GetInterpolatedHeight",
                    new[] { typeof(float), typeof(float) });
                harmony.Patch(target, prefix: new HarmonyMethod(
                    typeof(RiverToSea).GetMethod(nameof(RecurseRiverPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                CoastLog.Msg($"Hooked RecurseRiver (prefix, rivers to sea; interpolatedHeight={(_interpolatedHeight != null ? "OK" : "MISSING")})");
            }
            catch (Exception ex)
            {
                CoastLog.Error($"RecurseRiver hook failed: {ex}");
            }
        }

        /// <summary>Called from the Stage 37 prefix, before Stage 38 can run.</summary>
        public static void BeginGeneration()
        {
            _attempts = 0;
            _retargeted = 0;
            _lakeVetoes = 0;
            _gaveUp = false;
            _activeWalk = null;
            _sea = null;
            _seaLookedUp = false;
        }

        public static void LogSummary()
        {
            if (_attempts == 0) return;
            CoastLog.Msg($"Rivers to sea: {_attempts} steered attempt(s), {_lakeVetoes} vetoed for ending in a lake" +
                         (_gaveUp ? "; gave up and let vanilla place the remaining rivers" : ""));
        }

        // Parameter names must match the original: (river, p, end, averageDir, limit, pointsUsed).
        private static bool RecurseRiverPrefix(TerrainGenerator __instance, TerrainRiver river, VoronoiPoint p,
            ref VoronoiPoint end, ref bool __result)
        {
            try
            {
                if (river == null || river.points == null) return true;

                if (river.points.Count == 0)
                {
                    // Top-level call of a new walk: decide whether to steer it.
                    _activeWalk = null;
                    int want = RiversRestoredMod.CoastRiversToSea?.Value ?? 0;
                    if (want <= 0) return true;

                    CoastPlan? plan = CoastPatches.AppliedPlan(__instance);
                    if (plan == null) return true;                        // no coast on this generation

                    var rivers = __instance.Data.rivers;
                    if (rivers != null && rivers.Count >= want) return true;  // enough rivers already steered

                    if (_attempts >= MaxAttempts)
                    {
                        if (!_gaveUp)
                        {
                            _gaveUp = true;
                            CoastLog.Warning($"Rivers to sea: no walk reached the sea in {MaxAttempts} attempts on this map; " +
                                             "vanilla places the remaining rivers.");
                        }
                        return true;
                    }
                    _attempts++;

                    VoronoiPoint? target = NearestCoastalPoint(__instance, plan, p);
                    if (target == null)
                    {
                        if (_attempts == 1)
                            CoastLog.Warning($"Rivers to sea: no perimeter Voronoi points found on the {plan.Edge} edge; leaving river targets alone.");
                        return true;
                    }

                    end = target;
                    _activeWalk = river;
                    _retargeted++;
                    if ((RiversRestoredMod.VerboseDiagnostics?.Value ?? false) && _retargeted <= 3)
                    {
                        CoastLog.Msg($"Rivers to sea: attempt {_attempts}, start ({p.x:F0}, {p.y:F0}) -> " +
                                     $"{plan.Edge} coast target ({target.x:F0}, {target.y:F0})");
                    }
                    return true;
                }

                // Recursive step of a steered walk: veto stopping in a lake.
                if (!ReferenceEquals(river, _activeWalk)) return true;
                if (_interpolatedHeight == null) return true;

                float h = (float)_interpolatedHeight.Invoke(__instance, new object[] { (float)p.x, (float)p.y });
                if (h > __instance.GetWaterHeight()) return true;   // still on land: walk on
                if (IsSea(__instance, p)) return true;              // reached the sea: vanilla adds the point and stops

                // Below water but not the sea: a lake or pond would end this river.
                // Fail the walk so Stage 38 retries with a new start.
                _lakeVetoes++;
                __result = false;
                return false;
            }
            catch (Exception ex)
            {
                CoastLog.Error($"RecurseRiverPrefix failed: {ex}");
                return true;
            }
        }

        private static bool IsSea(TerrainGenerator tg, VoronoiPoint p)
        {
            if (!_seaLookedUp)
            {
                _seaLookedUp = true;
                WaterType? ocean = tg.waterSettings.oceanType;
                foreach (var a in tg.Data.waterAreas)
                {
                    if (ocean != null && a.waterType == ocean) { _sea = a; break; }
                }
            }
            if (!_sea.HasValue) return false;

            var ms = tg.mapSettings;
            int res = ms.heightmapResolution;
            int cx = Mathf.Clamp(Mathf.RoundToInt((float)p.x / ms.width * (res - 1)), 0, res - 1);
            int cz = Mathf.Clamp(Mathf.RoundToInt((float)p.y / ms.depth * (res - 1)), 0, res - 1);
            var a2 = _sea.Value;
            if (a2.points == null) return false;
            int px = cx - a2.minX, pz = cz - a2.minZ;
            return px >= 0 && pz >= 0 && px < a2.points.GetLength(0) && pz < a2.points.GetLength(1) && a2.points[px, pz];
        }

        /// <summary>The perimeter Voronoi point on the coastal edge closest to
        /// <paramref name="from"/>. VoronoiPoint.x/y are world X/Z.</summary>
        private static VoronoiPoint? NearestCoastalPoint(TerrainGenerator tg, CoastPlan plan, VoronoiPoint from)
        {
            VoronoiDiagram? diagram = tg.Data.diagram;
            if (diagram == null || diagram.Boundary == null) return null;

            float w = tg.mapSettings.width;
            float d = tg.mapSettings.depth;
            float tol = Mathf.Max(w, d) * 0.02f;

            VoronoiPoint? best = null;
            double bestDist = double.MaxValue;
            foreach (VoronoiPoint pt in diagram.Boundary)
            {
                if (pt == null || ReferenceEquals(pt, from)) continue;
                bool onCoast;
                switch (plan.Edge)
                {
                    case CoastEdge.East:  onCoast = pt.x <= tol;     break;   // min X
                    case CoastEdge.West:  onCoast = pt.x >= w - tol; break;   // max X
                    case CoastEdge.North: onCoast = pt.y <= tol;     break;   // min Z
                    case CoastEdge.South: onCoast = pt.y >= d - tol; break;   // max Z
                    default:              onCoast = false;           break;
                }
                if (!onCoast) continue;

                double dx = pt.x - from.x, dz = pt.y - from.y;
                double dist = dx * dx + dz * dz;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = pt;
                }
            }
            return best;
        }
    }
}
