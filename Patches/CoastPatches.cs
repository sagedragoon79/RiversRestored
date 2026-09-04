using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using TerrainGen;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Coastal Maps: one map edge becomes open sea.
    ///
    /// Farthest Frontier ships a complete, live ocean pathway that vanilla maps
    /// never trigger. Any below-water basin that touches the heightmap edge with
    /// enough shoreline and edge cells is classified as waterSettings.oceanType,
    /// and rendering, shore points, fishing, ambient audio, pathing, and map-edge
    /// arrivals already handle it. Two things keep oceans from existing: the
    /// border mountain ring stamped around all four edges in Stage 5, and the
    /// ocean type's shorelinePoints of 900000. This feature mirrors the ring
    /// without the stamps on one edge, lowers a band of terrain along that edge
    /// below the water plane before the classification pass (Stage 37), and
    /// lowers the thresholds. Vanilla does the rest.
    ///
    /// Hooks on <c>TerrainGen.TerrainGenerator</c>:
    ///  • GenerateBorderFeatures (prefix, skips original): vanilla's ring, minus the coastal edge.
    ///  • GenerateAsync_PreWater_Stage37 (prefix): carve before classification.
    ///  • Stage 37 postfix at Priority.Last: safety re-carve after every other mod's
    ///    postfix, then the river and water-area reports.
    ///  • Stage 50 (postfix): final classification report.
    /// The river injection in <see cref="RiverSettingsPatch"/> also calls
    /// <see cref="ReapplyForRivers"/> directly after the flow bias, so the river
    /// walk always sees the final shoreline.
    /// Independent of <c>RiversEnabled</c>: the coast works with rivers off.
    /// </summary>
    internal static class CoastPatches
    {
        private static MethodInfo? _paintFeature;
        private static FieldInfo? _resourcesField;
        private static FieldInfo? _featuresUsedField;
        private static FieldInfo? _borderFeaturesField;   // resolved from the private GenerationResources type

        /// <summary>Plans applied this session, keyed by generator instance id.</summary>
        private static readonly Dictionary<int, CoastPlan> _applied = new Dictionary<int, CoastPlan>();

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            Type tg = typeof(TerrainGenerator);

            _paintFeature = AccessTools.Method(tg, "PaintFeature",
                new[] { typeof(float), typeof(float), typeof(TerrainFeature), typeof(float), typeof(float) });
            _resourcesField = AccessTools.Field(tg, "resources");
            _featuresUsedField = AccessTools.Field(tg, "featuresUsed");
            CoastLog.Msg($"Reflection: PaintFeature={(_paintFeature != null ? "OK" : "MISSING")} " +
                         $"resources={(_resourcesField != null ? "OK" : "MISSING")} " +
                         $"featuresUsed={(_featuresUsedField != null ? "OK" : "MISSING")}");

            Hook(harmony, tg, "GenerateBorderFeatures", nameof(BorderFeaturesPrefix), null);
            Hook(harmony, tg, "GenerateAsync_PreWater_Stage37", nameof(PreWaterPrefix), nameof(PreWaterPostfix), Priority.Last);
            Hook(harmony, tg, "GenerateAsync_Water_Stage50", null, nameof(WaterPostfix));
        }

        private static void Hook(HarmonyLib.Harmony harmony, Type type, string method, string? prefix, string? postfix,
            int postfixPriority = Priority.Normal)
        {
            try
            {
                MethodInfo? target = AccessTools.Method(type, method, Type.EmptyTypes);
                if (target == null)
                {
                    CoastLog.Warning($"{type.Name}.{method} not found; hook skipped");
                    return;
                }
                HarmonyMethod? pre = prefix == null ? null
                    : new HarmonyMethod(typeof(CoastPatches).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic));
                HarmonyMethod? post = postfix == null ? null
                    : new HarmonyMethod(typeof(CoastPatches).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic))
                      { priority = postfixPriority };
                harmony.Patch(target, prefix: pre, postfix: post);
                string kind = prefix != null && postfix != null ? "prefix+postfix" : prefix != null ? "prefix" : "postfix";
                CoastLog.Msg($"Hooked {method} ({kind})");
            }
            catch (Exception ex)
            {
                CoastLog.Error($"Hook {method} failed: {ex}");
            }
        }

        /// <summary>The coast plan carved for this generator's current run, or
        /// null when it has no coast.</summary>
        internal static CoastPlan? AppliedPlan(TerrainGenerator tg) =>
            _applied.TryGetValue(tg.GetInstanceID(), out CoastPlan plan) ? plan : null;

        /// <summary>The plan to apply to this generation, or null to leave the
        /// generator alone. Fresh generation: preferences plus seed. Save load:
        /// the sidecar written with that save, so a map generated without the
        /// coast (no sidecar) is never altered.</summary>
        internal static CoastPlan? ResolvePlan(TerrainGenerator tg)
        {
            if (!(RiversRestoredMod.CoastalMapsEnabled?.Value ?? false)) return null;
            if (!tg.Data.useSavedMap) return CoastPlan.Build(tg);
            return CoastPersistence.PlanForLoadingSave(tg);
        }

        /// <summary>Called by the river injection after the flow bias and before
        /// Stage 38, so river walks stop at the true shoreline.</summary>
        internal static void ReapplyForRivers(TerrainGenerator tg)
        {
            try
            {
                CoastPlan? plan = AppliedPlan(tg);
                if (plan == null) return;
                int lowered = CoastCarver.Carve(tg, plan, "re-carve before Stage 38 (river paths)");
                if (lowered > 0)
                    CoastLog.Msg($"Restored {lowered} sea cells after the flow bias so river walks stop at the true shoreline.");
            }
            catch (Exception ex)
            {
                CoastLog.Error($"ReapplyForRivers failed: {ex}");
            }
        }

        // ── Stage 5: the border mountain ring ──────────────────────────────

        private static bool BorderFeaturesPrefix(TerrainGenerator __instance)
        {
            List<TerrainFeature>? borderFeatures;
            List<TerrainGenerator.FeatureEntry>? featuresUsed;
            CoastPlan? plan;
            try
            {
                plan = ResolvePlan(__instance);
                if (plan == null) return true;
                if (_paintFeature == null || _resourcesField == null || _featuresUsedField == null)
                {
                    CoastLog.Warning("Border-ring mirror unavailable (reflection miss); running the vanilla ring. " +
                                     "The coast is still carved, but border mountains may sit at the shoreline.");
                    return true;
                }
                object? resources = _resourcesField.GetValue(__instance);
                if (resources == null)
                {
                    CoastLog.Warning("TerrainGenerator.resources is null; running the vanilla ring.");
                    return true;
                }
                _borderFeaturesField ??= AccessTools.Field(resources.GetType(), "borderfeatures");
                borderFeatures = _borderFeaturesField?.GetValue(resources) as List<TerrainFeature>;
                featuresUsed = _featuresUsedField.GetValue(__instance) as List<TerrainGenerator.FeatureEntry>;
                if (borderFeatures == null || featuresUsed == null)
                {
                    CoastLog.Warning("Border feature lists unavailable; running the vanilla ring.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                CoastLog.Error($"BorderFeaturesPrefix setup failed; running the vanilla ring: {ex}");
                return true;
            }

            // From here on the mirror owns the stamps. Never fall through to vanilla
            // after starting, or the ring would be stamped twice.
            try
            {
                RunBorderRingSkippingEdge(__instance, plan.Edge, borderFeatures, featuresUsed);
            }
            catch (Exception ex)
            {
                CoastLog.Error($"Border-ring mirror failed part way: {ex}");
            }
            return false;
        }

        /// <summary>Line-for-line mirror of vanilla <c>GenerateBorderFeatures</c>
        /// (same loop order, same RNG calls per placed stamp) that skips the
        /// stamps whose grid point lies on the coastal edge.</summary>
        private static void RunBorderRingSkippingEdge(TerrainGenerator tg, CoastEdge edge,
            List<TerrainFeature> borderFeatures, List<TerrainGenerator.FeatureEntry> featuresUsed)
        {
            var list = new List<TerrainFeature>(borderFeatures);
            if (list.Count == 0)
            {
                CoastLog.Msg("This theme has no border features; nothing to skip.");
                return;
            }

            float buffer = tg.borderFeatureSettings.borderBuffer;
            float spacing = tg.borderFeatureSettings.borderSpacing;
            float far = Mathf.Round(((float)tg.mapSettings.depth - buffer * 2f) / spacing) * spacing + buffer;
            RNG rng = tg.RNG;

            int placed = 0, skipped = 0;
            for (float z = buffer; z <= far; z += spacing)
            {
                for (float x = buffer; x <= far; x += spacing)
                {
                    // Display convention verified in game (2026-09-03): the map shows
                    // maximum X on the LEFT and maximum Z at the BOTTOM, so
                    // West = max X, East = min X, North = min Z, South = max Z.
                    bool east = x == buffer, north = z == buffer, west = x == far, south = z == far;
                    if (!(west || south || east || north)) continue;

                    bool onCoast;
                    switch (edge)
                    {
                        case CoastEdge.West:  onCoast = west;  break;
                        case CoastEdge.East:  onCoast = east;  break;
                        case CoastEdge.South: onCoast = south; break;
                        case CoastEdge.North: onCoast = north; break;
                        default:              onCoast = false; break;
                    }
                    if (onCoast) { skipped++; continue; }

                    int index = rng.Range(0, list.Count - 1);
                    TerrainFeature feature = list[index];
                    float size = rng.Range(feature.minSize, feature.maxSize);
                    _paintFeature!.Invoke(tg, new object[] { x, z, feature, size, feature.height });
                    featuresUsed.Add(new TerrainGenerator.FeatureEntry
                    {
                        feature = feature,
                        size = size,
                        position = new Vector3(x, 0f, z),
                        biome = false,
                    });
                    placed++;
                }
            }

            CoastLog.Msg($"Border ring: {placed} features stamped, {skipped} skipped on the {edge} edge " +
                         $"(buffer {buffer}, spacing {spacing}, far {far})");
        }

        // ── Stage 37: carve, then vanilla classifies ───────────────────────

        private static void PreWaterPrefix(TerrainGenerator __instance)
        {
            try
            {
                int id = __instance.GetInstanceID();
                CoastPlan? plan = ResolvePlan(__instance);
                // Whatever this generator run produces is the map a later save
                // describes: a coast plan, or null for a vanilla map.
                CoastPersistence.CurrentPlan = plan;
                RiverToSea.BeginGeneration();
                if (plan == null)
                {
                    _applied.Remove(id);
                    return;
                }

                if (RiversRestoredMod.VerboseDiagnostics?.Value ?? false)
                    CoastDiagnostics.DumpSettings(__instance, plan);
                if (plan.OceanThresholdOverride)
                    CoastCarver.ApplyOceanThresholdOverride(__instance);

                CoastCarver.Carve(__instance, plan, "carve");
                _applied[id] = plan;
            }
            catch (Exception ex)
            {
                CoastLog.Error($"PreWaterPrefix failed: {ex}");
            }
        }

        /// <summary>Runs after every other mod's Stage 37 postfix (Priority.Last):
        /// a safety re-carve, then the river and classification reports.</summary>
        private static void PreWaterPostfix(TerrainGenerator __instance)
        {
            try
            {
                if (_applied.TryGetValue(__instance.GetInstanceID(), out CoastPlan plan))
                {
                    int restored = CoastCarver.Carve(__instance, plan, "re-carve after Stage 37 postfixes");
                    if (restored > 0)
                        CoastLog.Msg($"Re-carve lowered {restored} sea cells that another patch had raised.");
                    RiverToSea.LogSummary();
                    CoastDiagnostics.ReportRivers(__instance);
                }
            }
            catch (Exception ex)
            {
                CoastLog.Error($"PreWaterPostfix failed: {ex}");
            }
            Report(__instance, "Stage 37 (PreWater)");
        }

        private static void WaterPostfix(TerrainGenerator __instance) => Report(__instance, "Stage 50 (Water)");

        private static void Report(TerrainGenerator tg, string stage)
        {
            try
            {
                bool applied = _applied.TryGetValue(tg.GetInstanceID(), out CoastPlan plan);
                if (!applied && !(RiversRestoredMod.VerboseDiagnostics?.Value ?? false)) return;
                CoastDiagnostics.ReportWaterAreas(tg, stage, applied ? plan : null);
            }
            catch (Exception ex)
            {
                CoastLog.Error($"Report ({stage}) failed: {ex}");
            }
        }
    }
}
