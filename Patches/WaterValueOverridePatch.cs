using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Per-preset water-amount multiplier. Scales
    /// <c>SettingsManager.mapWaterValue</c> right before
    /// <c>TerrainGeneratorController.GenerateInternal(bool)</c> reads it,
    /// then restores the original in a finalizer so the static stays clean
    /// across reroll attempts.
    ///
    /// Vanilla flow (Assembly-CSharp TerrainGeneratorController.GenerateInternal,
    /// line ~17784):
    /// <code>
    ///   water = SettingsManager.mapWaterValue;                                          // line 17792
    ///   component.waterSettings.height = (minWaterHeight + (maxWaterHeight - minWaterHeight) * water)
    ///                                    * maxWater.Evaluate(mountains);                // line 17844
    /// </code>
    /// Lower <c>water</c> → lower <c>waterSettings.height</c> → fewer heightmap cells
    /// drop below the water threshold in Stage 50's flood-fill → smaller and fewer
    /// lakes / less wet biome.
    ///
    /// The same method is invoked by RR's preview pipeline
    /// (<c>PreviewGenWorker</c> calls <c>tgc.GenSliced_Generate(false)</c>
    /// which routes through <c>GenerateInternal</c>), so a single hook covers
    /// both the new-settlement preview and the actual gameplay gen — preview
    /// always matches what the player will see in-game.
    ///
    /// Active preset selection comes from <see cref="RiversRestoredMod.GetEffectiveValues"/>;
    /// the per-preset entry defaults are seeded so IdyllicValley/LowlandLakes/AlpineValleys
    /// trim back slightly (since adding rivers makes those biomes feel
    /// oversaturated) and AridHighlands/Plains stay at vanilla 1.0.
    /// </summary>
    internal static class WaterValueOverridePatch
    {
        private const string PrefixTag = "[RR][WaterValue]";
        private static bool _patched;

        // Stash for the original values across the prefix → finalizer window.
        // Static is fine: GenerateInternal is the only writer/reader and it
        // runs on the Unity main thread.
        private static float _originalMapWaterValue;
        private static float _originalTgcWater;
        private static object? _tgcRef;
        private static FieldInfo? _tgcWaterField;
        private static bool _wasOverridden;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            if (_patched) return;
            try
            {
                Type? tgcType = AccessTools.TypeByName("TerrainGen.TerrainGeneratorController")
                                ?? AccessTools.TypeByName("TerrainGeneratorController");
                if (tgcType == null)
                {
                    RiversRestoredMod.Log.Warning($"{PrefixTag} TerrainGeneratorController type not found — patch disabled.");
                    return;
                }

                MethodInfo? mi = AccessTools.Method(tgcType, "GenerateInternal", new[] { typeof(bool) });
                if (mi == null)
                {
                    RiversRestoredMod.Log.Warning($"{PrefixTag} GenerateInternal(bool) not found — patch disabled.");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(WaterValueOverridePatch)
                    .GetMethod(nameof(PrefixApplyMultiplier),
                        BindingFlags.Static | BindingFlags.NonPublic));
                var finalizer = new HarmonyMethod(typeof(WaterValueOverridePatch)
                    .GetMethod(nameof(FinalizerRestore),
                        BindingFlags.Static | BindingFlags.NonPublic));

                harmony.Patch(mi, prefix: prefix, finalizer: finalizer);
                _patched = true;
                RiversRestoredMod.Log.Msg($"{PrefixTag} Hooked TerrainGeneratorController.GenerateInternal(bool) for per-preset water multiplier.");
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Warning($"{PrefixTag} Apply failed: {ex.Message}");
            }
        }

        // Two parallel water inputs need scaling for the gen pipeline to use
        // our multiplier consistently:
        //
        // 1. SettingsManager.mapWaterValue (static, FF asset state). Read by
        //    GenerateInternal at line 17792 INSIDE `if (game) { ... }`, so
        //    only consulted when `game=true` (actual gameplay start).
        //
        // 2. TGC.water (instance field on the TerrainGeneratorController).
        //    Used at line 17844 to compute waterSettings.height:
        //        component.waterSettings.height =
        //            (minWaterHeight + (maxWaterHeight - minWaterHeight) * water)
        //            * maxWater.Evaluate(mountains);
        //    For game=true, the `if (game)` block above writes
        //    SettingsManager.mapWaterValue into this field, so modifying #1
        //    propagates to #2 automatically.
        //    For game=false (RR's preview path), the `if (game)` block is
        //    SKIPPED entirely — the instance field keeps whatever was set
        //    earlier by PreviewGenWorker.ApplyTgcGenParameters (which writes
        //    the raw seed water value, NOT the static). #2 must be modified
        //    explicitly in this case, otherwise the preview shows the raw
        //    seed's water amount while gameplay shows the multiplied amount
        //    — preview-vs-gameplay divergence.
        //
        // Modifying both unconditionally is harmless: for game=true, the
        // `if (game)` block overwrites our pre-set tgc.water with the
        // already-modified static value (identical). For game=false, only
        // our tgc.water modification matters.
        private static void PrefixApplyMultiplier(object __instance, bool game)
        {
            _wasOverridden = false;
            _tgcRef = null;
            _tgcWaterField = null;
            if (!(RiversRestoredMod.RiversEnabled?.Value ?? false)) return;

            try
            {
                var values = RiversRestoredMod.GetEffectiveValues();
                float mult = values.WaterMultiplier;
                if (Mathf.Approximately(mult, 1.0f)) return;  // vanilla — nothing to do

                // Only modify the field that GenerateInternal actually consumes
                // on this path. Modifying both was overstepping: the path that
                // SHOULDN'T have read our modified value still saw it via
                // collateral readers (caption builder, mid-gen diagnostics,
                // etc.), consumed RNG differently, and produced a different
                // heightmap. Confirmed by a Debug control build that forced
                // multiplier=1.0: terrain matched, divergence vanished.
                //
                // Path matrix (FF GenerateInternal, Assembly-CSharp.cs:17792):
                //   if (game) { water = SettingsManager.mapWaterValue; ... }
                //
                //   game=true  → SM.mapWaterValue is read into tgc.water,
                //                tgc.water is then used at line 17867.
                //                Modify SM.mapWaterValue ONLY. Touching
                //                tgc.water is wasted (FF overwrites it).
                //
                //   game=false → `if (game)` block skipped. tgc.water is
                //                whatever RR's PreviewGenWorker set earlier
                //                from the decoded seed; that's the value
                //                used at line 17867. Modify tgc.water ONLY.
                //                Leave SM.mapWaterValue alone so collateral
                //                readers see the user's actual slider state.
                if (game)
                {
                    _originalMapWaterValue = SettingsManager.mapWaterValue;
                    float modifiedStatic = Mathf.Clamp01(_originalMapWaterValue * mult);
                    SettingsManager.mapWaterValue = modifiedStatic;
                    _wasOverridden = true;
                    RiversRestoredMod.Log.Msg(
                        $"{PrefixTag} gameplay: SM.mapWaterValue {_originalMapWaterValue:0.000}→{modifiedStatic:0.000} " +
                        $"(multiplier={mult:0.00}, preset={RiversRestoredMod.RiverPreset?.Value})");
                }
                else
                {
                    _tgcWaterField = AccessTools.Field(__instance.GetType(), "water");
                    if (_tgcWaterField != null && _tgcWaterField.FieldType == typeof(float))
                    {
                        _tgcRef = __instance;
                        _originalTgcWater = (float)_tgcWaterField.GetValue(__instance);
                        float modifiedTgc = Mathf.Clamp01(_originalTgcWater * mult);
                        _tgcWaterField.SetValue(__instance, modifiedTgc);
                        _wasOverridden = true;
                        RiversRestoredMod.Log.Msg(
                            $"{PrefixTag} preview: tgc.water {_originalTgcWater:0.000}→{modifiedTgc:0.000} " +
                            $"(multiplier={mult:0.00}, preset={RiversRestoredMod.RiverPreset?.Value})");
                    }
                }
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Warning($"{PrefixTag} PrefixApplyMultiplier failed: {ex.Message}");
                _wasOverridden = false;
            }
        }

        // Finalizer runs after the original method whether it returned normally
        // or threw. Restore both the static AND the tgc.water instance field
        // so subsequent reads (UI display, re-rolls, save state inspection,
        // any caller that inspects tgc.water post-gen) see the user's actual
        // slider/seed values — not our temporarily-shifted versions.
        private static Exception? FinalizerRestore(Exception? __exception)
        {
            try
            {
                if (_wasOverridden)
                {
                    // Only restore the field we actually modified. Path-symmetric
                    // with PrefixApplyMultiplier: _tgcRef is non-null iff we
                    // modified tgc.water (preview path); otherwise the prefix
                    // modified SM.mapWaterValue (gameplay path).
                    if (_tgcRef != null && _tgcWaterField != null)
                    {
                        _tgcWaterField.SetValue(_tgcRef, _originalTgcWater);
                    }
                    else
                    {
                        SettingsManager.mapWaterValue = _originalMapWaterValue;
                    }
                    _wasOverridden = false;
                    _tgcRef = null;
                    _tgcWaterField = null;
                }
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Warning($"{PrefixTag} FinalizerRestore failed: {ex.Message}");
            }
            // Pass through whatever the original method threw (or null).
            return __exception;
        }
    }
}
