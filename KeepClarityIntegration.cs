using System;
using System.Reflection;
using MelonLoader;

namespace RiversRestored
{
    /// <summary>Optional integration with Keep Clarity's settings panel. No-op when KeepClarity.dll is absent.</summary>
    internal static class KeepClarityIntegration
    {
        private static bool _resolved, _present;
        private static MethodInfo? _registerMod;
        private static MethodInfo? _registerEntry;
        private static Type? _settingsMetaType;

        private const string ModId = "RiversRestored";
        private const string ModDisplayName = "Rivers Restored";

        public static void TryRegisterAll()
        {
            if (!ResolveApi()) return;
            try
            {
                RegisterMod();
                RegisterEntries();
                MelonLogger.Msg("[RR] Registered with Keep Clarity settings panel");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RR] Keep Clarity registration failed: {ex.Message}");
            }
        }

        private static bool ResolveApi()
        {
            if (_resolved) return _present;
            _resolved = true;
            var apiType = Type.GetType("FFUIOverhaul.Settings.SettingsAPI, KeepClarity");
            if (apiType == null) { _present = false; return false; }
            _settingsMetaType = Type.GetType("FFUIOverhaul.Settings.SettingsMeta, KeepClarity");
            if (_settingsMetaType == null) { _present = false; return false; }
            _registerMod = apiType.GetMethod("RegisterMod", BindingFlags.Public | BindingFlags.Static);
            foreach (var m in apiType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "Register" && m.IsGenericMethodDefinition) { _registerEntry = m; break; }
            _present = _registerMod != null && _registerEntry != null;
            return _present;
        }

        private static void RegisterMod()
        {
            _registerMod!.Invoke(null, new object?[] {
                ModId, ModDisplayName,
                "Restores river generation on FF maps and exposes river density/shape/fishing tuning",
                /*version*/ null,
                /*iconResourcePath*/ null,
                /*accentRgb — river blue*/ new[] { 0.30f, 0.55f, 0.75f, 1f },
                /*order*/ 50
            });
        }

        private static object NewMeta(string? label = null, string? tooltip = null,
            object? min = null, object? max = null, bool restartRequired = false,
            int order = 0, Func<bool>? visibleWhen = null)
        {
            var m = Activator.CreateInstance(_settingsMetaType!);
            void Set(string field, object? value)
            {
                var f = _settingsMetaType!.GetField(field);
                if (f != null) f.SetValue(m, value);
            }
            Set("Label", label);
            Set("Tooltip", tooltip);
            Set("Min", min);
            Set("Max", max);
            Set("RestartRequired", restartRequired);
            Set("Order", order);
            Set("VisibleWhen", visibleWhen);
            return m!;
        }

        private static void Reg<T>(string category, MelonPreferences_Entry<T>? entry, object meta)
        {
            if (entry == null) return;
            var closed = _registerEntry!.MakeGenericMethod(typeof(T));
            closed.Invoke(null, new object?[] { ModId, ModDisplayName, category, entry, meta });
        }

        private static void RegisterEntries()
        {
            // Hides downstream rows when master toggle is off.
            Func<bool> on = () => RiversRestoredMod.RiversEnabled.Value;
            // Granular sliders only visible when both master is on AND user
            // has opted into granular mode (matches MelonPreferences IsHidden
            // behaviour from Plugin.cs::ApplyGranularVisibility).
            Func<bool> onGranular = () =>
                RiversRestoredMod.RiversEnabled.Value
                && (RiversRestoredMod.GranularSettings?.Value ?? false);

            // === Master === — basic-tier choices, always visible (or gated
            // only by master toggle). Order field controls KC's per-row sort.
            Reg("Master", RiversRestoredMod.RiversEnabled,
                NewMeta("Rivers Enabled",
                    "Master toggle. Off = vanilla (no rivers).",
                    order: 0));
            Reg("Master", RiversRestoredMod.RiverPreset,
                NewMeta("River Preset (matches map biome)",
                    "Pre-tuned bundle of river settings matched to FF's biome names. " +
                    "When set to anything except Custom, the preset's values override " +
                    "the granular sliders. Pick the option that matches your map's biome " +
                    "for sensible defaults.",
                    order: 10, visibleWhen: on));
            Reg("Master", RiversRestoredMod.GranularSettings,
                NewMeta("Show Granular Settings (Advanced)",
                    "When ON, the per-slider river-shape controls become visible below. " +
                    "They only take effect when River Preset is set to Custom.",
                    order: 20, visibleWhen: on));
            Reg("Master", RiversRestoredMod.EnableRibbonAnimation,
                NewMeta("Flowing-Water Animation",
                    "On = animated ribbon flow. Off = static water surface only " +
                    "(cheaper, lakes-style; saves CPU/GPU on river-heavy maps).",
                    order: 30, visibleWhen: on));
            Reg("Master", RiversRestoredMod.RiverPreferLakeWaterType,
                NewMeta("Prefer Lake (Blue) Water for Rivers",
                    "On (default) = rivers use Lake-type water (clear blue). " +
                    "Off = rivers use whatever water type the map assigns first (often Pond — green/murky).",
                    order: 40, visibleWhen: on));

            // === Flow Direction === — v1.3.0 directional bias.
            Reg("Flow Direction", RiversRestoredMod.RiverFlowBias,
                NewMeta("River Flow Direction Bias",
                    "Tilt the heightmap before river-path generation so rivers " +
                    "statistically flow from a chosen high corner/edge to a chosen low one. " +
                    "None = pure seed-driven (vanilla behaviour).",
                    order: 0, visibleWhen: on));
            Reg("Flow Direction", RiversRestoredMod.RiverFlowBiasStrength,
                NewMeta("Flow Bias Strength", min: 0f, max: 1f,
                    tooltip: "How strongly to tilt the heightmap. " +
                    "0.3 = subtle, 0.4 = balanced default, 0.5 = strong, 0.7+ = visibly tilted. " +
                    "Has no effect when Direction Bias is None.",
                    order: 10, visibleWhen: on));

            // === Density === — granular only.
            Reg("Density", RiversRestoredMod.NumRivers,
                NewMeta("Number of Rivers", min: 0, max: 12,
                    tooltip: "Generator targets this count; final number depends on seed feasibility",
                    visibleWhen: onGranular));
            Reg("Density", RiversRestoredMod.MinPoints,
                NewMeta("Minimum River Length", min: 1, max: 50,
                    tooltip: "Min control points for a river to be accepted",
                    visibleWhen: onGranular));

            // === Shape — Width / Depth === — granular only.
            Reg("Shape — Width / Depth", RiversRestoredMod.MinWidth,
                NewMeta("Min Width (cells)", min: 0, max: 30,
                    tooltip: "0 = leave vanilla", visibleWhen: onGranular));
            Reg("Shape — Width / Depth", RiversRestoredMod.MaxWidth,
                NewMeta("Max Width (cells)", min: 0, max: 60,
                    tooltip: "0 = leave vanilla", visibleWhen: onGranular));
            Reg("Shape — Width / Depth", RiversRestoredMod.MinDepth,
                NewMeta("Min Depth (m)", min: -1f, max: 5f,
                    tooltip: "-1 = leave vanilla", visibleWhen: onGranular));
            Reg("Shape — Width / Depth", RiversRestoredMod.MaxDepth,
                NewMeta("Max Depth (m)", min: -1f, max: 10f,
                    tooltip: "-1 = leave vanilla", visibleWhen: onGranular));

            // === Carve Shape === — granular only.
            Reg("Carve Shape", RiversRestoredMod.RiverInnerRadius,
                NewMeta("Trench Inner Radius (cells)", min: 1, max: 10,
                    tooltip: "Cells within this distance of centerline are carved to trench depth",
                    visibleWhen: onGranular));
            Reg("Carve Shape", RiversRestoredMod.RiverOuterRadius,
                NewMeta("Bank Outer Radius (cells)", min: 2, max: 20,
                    tooltip: "Where banks blend back to original terrain",
                    visibleWhen: onGranular));
            Reg("Carve Shape", RiversRestoredMod.RiverTrenchDepth,
                NewMeta("Trench Depth (m)", min: 0.1f, max: 5f,
                    tooltip: "How deep below water surface the trench is carved",
                    visibleWhen: onGranular));
            Reg("Carve Shape", RiversRestoredMod.RiverSmoothPasses,
                NewMeta("Bank Smooth Passes", min: 0, max: 12,
                    tooltip: "0 = raw carve, 4 = good default, 8 = very gentle",
                    visibleWhen: onGranular));
            Reg("Carve Shape", RiversRestoredMod.RiverJitterAmplitude,
                NewMeta("Meander Amplitude (m)", min: 0f, max: 5f,
                    tooltip: "0 = straight, larger = more meandering",
                    visibleWhen: onGranular));
            Reg("Carve Shape", RiversRestoredMod.RiverJitterFrequency,
                NewMeta("Meander Frequency", min: 0f, max: 3f,
                    tooltip: "Wave oscillations per Voronoi segment",
                    visibleWhen: onGranular));

            // === Water Plane / Fishing === — granular only.
            Reg("Water Plane / Fishing", RiversRestoredMod.RiverRegisterAsWaterArea,
                NewMeta("Register as Water Area",
                    "Treat rivers as water areas (lakes-style) — enables fishing and stable persistence",
                    visibleWhen: onGranular));
            Reg("Water Plane / Fishing", RiversRestoredMod.RiverBlobRadius,
                NewMeta("Disc Stamp Radius (cells)", min: 1, max: 10,
                    tooltip: "Disc-stamp radius for water-area polygon merging",
                    visibleWhen: onGranular));
            Reg("Water Plane / Fishing", RiversRestoredMod.RiverBlobStride,
                NewMeta("Disc Stamp Stride (cells)", min: 1, max: 10,
                    tooltip: "Spacing between disc stamps along the path",
                    visibleWhen: onGranular));
            Reg("Water Plane / Fishing", RiversRestoredMod.RiverFishingAreaMultiplier,
                NewMeta("Fishing Area Multiplier", min: 1, max: 8,
                    tooltip: "1 = vanilla density (sparse), 4 = playable density",
                    visibleWhen: onGranular));

            // === Generator Gating === — granular only.
            Reg("Generator Gating", RiversRestoredMod.MarkWaterTypesAsRiverEnd,
                NewMeta("Mark Water as River-End",
                    "THE gate — without this, no river ever validates regardless of count",
                    visibleWhen: onGranular));
            Reg("Generator Gating", RiversRestoredMod.ForceCoastlineTerrain,
                NewMeta("Force Coastline Terrain",
                    "Diagnostic: forces Coastline biome (gives ocean as guaranteed river endpoint)",
                    visibleWhen: onGranular));

            // === Diagnostics === — always visible (no granular gate).
            Reg("Diagnostics", RiversRestoredMod.VerboseDiagnostics,
                NewMeta("Verbose Diagnostics",
                    "Per-WaterArea state on save, per-stage waterAreas counts during gen. " +
                    "Noisy in normal play."));

            // === Per-preset live-tunable sliders ============================
            // One section per preset, gated so only the currently-selected
            // preset's sliders appear in the UI. Lets the user tune any
            // preset without switching to Custom mode (which would reset to
            // the granular sliders' values). Defaults seed from the
            // hardcoded preset table at first launch.
            foreach (var kvp in RiversRestoredMod.PresetEntries)
            {
                RegisterPresetEntries(kvp.Key, kvp.Value);
            }
        }

        /// <summary>Register all 13 tunable entries for one preset under its
        /// own KC category. Visibility is gated on master toggle AND the
        /// active preset matching this preset's mode, so only the selected
        /// preset's sliders are exposed in the UI.</summary>
        private static void RegisterPresetEntries(
            RiverPresetMode mode,
            RiversRestoredMod.RiverPresetEntries entries)
        {
            var name = mode.ToString();
            var category = $"Preset · {name}";
            Func<bool> visible = () =>
                RiversRestoredMod.RiversEnabled.Value
                && (RiversRestoredMod.RiverPreset?.Value ?? RiverPresetMode.IdyllicValley) == mode;

            Reg(category, entries.NumRivers,
                NewMeta("Number of Rivers", min: 0, max: 12,
                    tooltip: "Generator targets this count; final number depends on seed feasibility",
                    order: 0, visibleWhen: visible));
            Reg(category, entries.MinPoints,
                NewMeta("Minimum River Length", min: 1, max: 50,
                    tooltip: "Min control points for a river to be accepted",
                    order: 10, visibleWhen: visible));
            Reg(category, entries.MinWidth,
                NewMeta("Min Ribbon Width (cells)", min: 0, max: 30,
                    tooltip: "0 = leave vanilla",
                    order: 20, visibleWhen: visible));
            Reg(category, entries.MaxWidth,
                NewMeta("Max Ribbon Width (cells)", min: 0, max: 60,
                    tooltip: "0 = leave vanilla",
                    order: 30, visibleWhen: visible));
            Reg(category, entries.InnerRadius,
                NewMeta("Channel Width (full depth, cells)", min: 1, max: 10,
                    tooltip: "Cells within this distance of centerline are carved to trench depth",
                    order: 40, visibleWhen: visible));
            Reg(category, entries.OuterRadius,
                NewMeta("Bank Width (slope to ground, cells)", min: 2, max: 20,
                    tooltip: "Where banks blend back to original terrain",
                    order: 50, visibleWhen: visible));
            Reg(category, entries.BlobRadius,
                NewMeta("Visible Water Width (cells)", min: 1, max: 10,
                    tooltip: "Disc-stamp radius for water-area polygon merging",
                    order: 60, visibleWhen: visible));
            Reg(category, entries.BlobStride,
                NewMeta("Water Surface Density (advanced)", min: 1, max: 10,
                    tooltip: "Stride between disc stamps along the path; 3 = default, higher = faster gen",
                    order: 70, visibleWhen: visible));
            Reg(category, entries.TrenchDepth,
                NewMeta("River Depth (m below water)", min: 0.1f, max: 5f,
                    tooltip: "How deep below water surface the trench is carved",
                    order: 80, visibleWhen: visible));
            Reg(category, entries.SmoothPasses,
                NewMeta("Bank Smoothness", min: 0, max: 12,
                    tooltip: "0 = raw carve, 4 = good default, 8 = very gentle",
                    order: 90, visibleWhen: visible));
            Reg(category, entries.JitterAmplitude,
                NewMeta("River Meander Strength (m)", min: 0f, max: 5f,
                    tooltip: "0 = straight, larger = more meandering",
                    order: 100, visibleWhen: visible));
            Reg(category, entries.JitterFrequency,
                NewMeta("River Meander Frequency", min: 0f, max: 3f,
                    tooltip: "Wave oscillations per Voronoi segment",
                    order: 110, visibleWhen: visible));
            Reg(category, entries.FishingAreaMultiplier,
                NewMeta("River Fishing Productivity Boost", min: 1, max: 8,
                    tooltip: "1 = vanilla (sparse), 4 = playable density, 8+ = lush",
                    order: 120, visibleWhen: visible));
        }
    }
}
