using System.Linq;
using HarmonyLib;
using MelonLoader;

// ─────────────────────────────────────────────────────────────────────────────
//  Rivers Restored v1.2.0
//
//  Discovery: Farthest Frontier ships with a COMPLETE river generation system
//  that simply isn't active on shipped maps. The Voronoi-based path generator,
//  geometry painter, and runtime WaterPath rendering all exist in
//  Assembly-CSharp.dll but never fire because `numRivers` is (likely) 0 on
//  shipped game data.
//
//  This mod overrides TerrainGenerator.riverSettings during the PreGenerate
//  phase of map creation, setting numRivers to a configured value so the
//  vanilla pipeline spawns actual rivers.
//
//  IMPORTANT: only works on NEW maps (generation-time only). Existing saves
//  are unaffected.
//
//  Compatibility: stacks with Warden of the Wilds (rivers = automatic
//  Angler / Creeler fishing bonuses since FishingManager.FindRivers /
//  IsInRiver are already wired into vanilla fishing shacks).
// ─────────────────────────────────────────────────────────────────────────────

[assembly: MelonInfo(typeof(RiversRestored.RiversRestoredMod), "Rivers Restored", "1.2.0", "SageDragoon")]
[assembly: MelonGame("Crate Entertainment", "Farthest Frontier")]

namespace RiversRestored
{
    /// <summary>Preset bundles for river generation, named to match the
    /// map biomes available in Farthest Frontier's New Game UI. Selected via
    /// <see cref="RiversRestoredMod.RiverPreset"/> in the cfg / in-game
    /// settings. When set to anything other than <see cref="Custom"/>, the
    /// preset's values override the individual granular cfg entries at
    /// gen time.</summary>
    public enum RiverPresetMode
    {
        /// <summary>Use the individual granular cfg values directly. Pick this
        /// if you want full manual control over every slider.</summary>
        Custom,
        /// <summary>Balanced rolling-terrain biome. Default choice for most
        /// players; produces medium-length winding rivers with clean banks.</summary>
        IdyllicValley,
        /// <summary>Flat biome with many ponds. Aggressively lowers
        /// MinPoints so short Voronoi candidates pass validation, since
        /// flat terrain can't produce long drainage paths. Shallow
        /// narrow streams between water bodies.</summary>
        LowlandLakes,
        /// <summary>High dry biome with moderate elevation but sparse water.
        /// Fewer rivers (arid), slightly narrower channels, deeper trenches
        /// to read as rocky canyons.</summary>
        AridHighlands,
        /// <summary>Open semi-flat biome. Moderate river count and width;
        /// gentler than LowlandLakes since terrain has slight gradient.</summary>
        Plains,
        /// <summary>Mountainous biome with valley drainages. Long deep
        /// rivers, wide gradual banks, strong meander — alpine
        /// drainage character.</summary>
        AlpineValleys,
    }

    public class RiversRestoredMod : MelonMod
    {
        public static RiversRestoredMod Instance { get; private set; } = null!;
        public static MelonLogger.Instance Log => Instance.LoggerInstance;
        public static new HarmonyLib.Harmony HarmonyInstance { get; private set; } = null!;

        // ── Master toggle ───────────────────────────────────────────────────
        /// <summary>Kill-switch. When false, the mod does nothing — vanilla
        /// behavior resumes.</summary>
        public static MelonPreferences_Entry<bool> RiversEnabled { get; private set; } = null!;

        // ── Preset chooser (drives all granular settings when != Custom) ────
        /// <summary>Biome-style preset selector. When set to any value except
        /// <see cref="RiverPresetMode.Custom"/>, the preset's bundled values
        /// override the individual granular cfg entries at gen time. Pick
        /// the option that matches the map-biome you're playing on.
        /// FFModSettingsManager renders this as a dropdown.</summary>
        public static MelonPreferences_Entry<RiverPresetMode> RiverPreset { get; private set; } = null!;

        /// <summary>When false, the granular slider entries (NumRivers,
        /// MinPoints, RiverInnerRadius, RiverOuterRadius, etc.) are hidden
        /// from the in-game settings UI to keep the surface clean. Cfg-file
        /// edits still work either way. Toggling this re-applies hidden
        /// state on every entry without restart.</summary>
        public static MelonPreferences_Entry<bool> GranularSettings { get; private set; } = null!;

        // ── Path / count ────────────────────────────────────────────────────
        /// <summary>Number of rivers the generator will attempt to produce
        /// during map gen. Each candidate path still has to pass Voronoi
        /// validation (≥ MinPoints control points, terminates in a valid
        /// water body), so the actual river count may be lower on difficult
        /// seeds. Vanilla default was probably 0 or 5.</summary>
        public static MelonPreferences_Entry<int> NumRivers { get; private set; } = null!;

        /// <summary>Minimum control points required for a river to be
        /// accepted. Lower values let "stub" rivers through. Set to 0 to
        /// leave the vanilla value alone.</summary>
        public static MelonPreferences_Entry<int> MinPoints { get; private set; } = null!;

        // ── Width / depth ───────────────────────────────────────────────────
        /// <summary>Minimum width of generated rivers in heightmap cells.
        /// Set to 0 to leave the vanilla value alone.</summary>
        public static MelonPreferences_Entry<int> MinWidth { get; private set; } = null!;

        /// <summary>Maximum width of generated rivers in heightmap cells.
        /// Set to 0 to leave the vanilla value alone.</summary>
        public static MelonPreferences_Entry<int> MaxWidth { get; private set; } = null!;

        /// <summary>Minimum depth in world units. Set to -1 to leave
        /// vanilla value alone.</summary>
        public static MelonPreferences_Entry<float> MinDepth { get; private set; } = null!;

        /// <summary>Maximum depth in world units. Set to -1 to leave
        /// vanilla value alone.</summary>
        public static MelonPreferences_Entry<float> MaxDepth { get; private set; } = null!;

        // ── The actual gate (probably) ──────────────────────────────────────
        /// <summary>When true, marks all loaded WaterType ScriptableObjects
        /// as riverEndPoint=true so the Voronoi validator accepts candidate
        /// rivers. This is THE gate — without this, no river ever validates
        /// regardless of NumRivers / MinPoints settings.</summary>
        public static MelonPreferences_Entry<bool> MarkWaterTypesAsRiverEnd { get; private set; } = null!;

        /// <summary>Diagnostic: forces TerrainGeneratorController.terrainType
        /// to Coastline (1), bypassing the biome-theme UI restriction. The
        /// Coastline type ships with ocean as the dominant water body, which
        /// gives rivers a guaranteed downhill endpoint and may be the real
        /// gate for river generation on inland map types.</summary>
        public static MelonPreferences_Entry<bool> ForceCoastlineTerrain { get; private set; } = null!;

        /// <summary>When true, each generated river is also registered as a
        /// <c>TerrainGenerator+WaterArea</c> in <c>_generationData.waterAreas</c>
        /// — the same list FF uses for lakes/oceans. Provides consistent
        /// water-plane coverage across save/reload (no manual respawn for the
        /// underlying water plane) and lets <c>FishingManager</c> spawn fishing
        /// nodes on rivers like it does on lakes. The WaterPath ribbon still
        /// runs on top to give the flow animation. Disable to fall back to
        /// ribbon-only rivers (no fishing, requires our manual respawn).</summary>
        public static MelonPreferences_Entry<bool> RiverRegisterAsWaterArea { get; private set; } = null!;

        /// <summary>v0.2 disc-stamp radius in heightmap cells. Each stamp is
        /// a small disc polygon added via <c>AddWaterAreaWithPanguMerge</c>;
        /// stamps along a river path merge transitively into one big polygon
        /// (Pangu pattern). Default 3 = matches <c>RiverInnerRadius</c> so
        /// the merged polygon fills the carved trench bottom exactly.
        /// Larger = wider river polygon but more cells to merge.</summary>
        public static MelonPreferences_Entry<int> RiverBlobRadius { get; private set; } = null!;

        /// <summary>v0.2 spacing between disc stamps in heightmap cells along
        /// the interpolated cp path. Heavy overlap at default (3) gives a
        /// smooth merged outline with no gaps even on tight curves. Raise
        /// for fewer stamps (faster gen) at the risk of gaps where the
        /// path bends sharply.</summary>
        public static MelonPreferences_Entry<int> RiverBlobStride { get; private set; } = null!;

        /// <summary>How many duplicate FishingArea entries to add per
        /// river-tagged area in Fishing Shack / Dock CreateFishingAreas
        /// results. Pangu uses the same pattern for lakes. 1 = no boost
        /// (vanilla, fishing rivers feels weak); 4 = nice playable density.</summary>
        public static MelonPreferences_Entry<int> RiverFishingAreaMultiplier { get; private set; } = null!;

        // ── Carve shape ─────────────────────────────────────────────────────
        /// <summary>Inner radius (heightmap cells) of the river trench.
        /// Cells within this distance of the centerline get slammed to the
        /// trench depth. Smaller = narrower river bed.</summary>
        public static MelonPreferences_Entry<int>? RiverInnerRadius { get; private set; }
        /// <summary>Outer radius (heightmap cells) where banks blend back to
        /// original terrain. The band between inner and outer radius gets a
        /// smooth ramp. Larger = gentler banks.</summary>
        public static MelonPreferences_Entry<int>? RiverOuterRadius { get; private set; }
        /// <summary>Perpendicular jitter amplitude in world units. 0 = straight
        /// Bresenham between Voronoi control points. Larger = more meandering.
        /// 1.5 ≈ subtle natural wiggle.</summary>
        public static MelonPreferences_Entry<float>? RiverJitterAmplitude { get; private set; }
        /// <summary>Jitter wave frequency multiplier per segment. Higher = more
        /// wiggles per Voronoi segment. 0.6 ≈ one and a bit oscillations.</summary>
        public static MelonPreferences_Entry<float>? RiverJitterFrequency { get; private set; }

        /// <summary>Number of 3×3 box-blur smoothing passes applied to the
        /// carved region after the initial carve. Pangu's "Shore Blend"
        /// equivalent. Higher = smoother banks but more processing time.
        /// 0 = no smoothing (raw carve), 4 = good default, 8 = very gentle.</summary>
        public static MelonPreferences_Entry<int>? RiverSmoothPasses { get; private set; }

        /// <summary>How deep below water surface the trench is carved, in
        /// world units (metres). Vanilla FF rivers were ~0.5–1m. Larger
        /// values create dramatic canyons but may trigger excess shoreline
        /// auto-paint and persistence issues on save/reload.</summary>
        public static MelonPreferences_Entry<float>? RiverTrenchDepth { get; private set; }

        /// <summary>When true, the mod logs detailed per-WaterArea state at
        /// save time and per-stage waterAreas counts during gen. Useful for
        /// diagnosing save/reload bugs, noisy in normal play. Default false.</summary>
        public static MelonPreferences_Entry<bool>? VerboseDiagnostics { get; private set; }

        /// <summary>When true, the visible flowing-water ribbon mesh is spawned
        /// on each river. When false, rivers render as a static green water
        /// surface only — no flow animation. Disabling reduces CPU/GPU load
        /// significantly on river-heavy maps; everything else (fishing,
        /// resource avoidance, save/reload, polygon water plane) still works.
        /// Default true.</summary>
        public static MelonPreferences_Entry<bool>? EnableRibbonAnimation { get; private set; }

        /// <summary>When true, ResolveRiverWaterType iterates waterSettings.lakeTypes
        /// and prefers entries whose name contains "Lake" over "Pond". Gives
        /// rivers consistent blue lake-like appearance across most map seeds.
        /// When false, falls back to lakeTypes[0] (whatever happens to be
        /// first — sometimes Pond, sometimes Lake, depending on seed). Default
        /// true.</summary>
        public static MelonPreferences_Entry<bool>? RiverPreferLakeWaterType { get; private set; }

        // ── Preset value table ─────────────────────────────────────────────
        /// <summary>Bundle of preset values that override the granular cfg
        /// entries when <see cref="RiverPreset"/> is anything other than
        /// <see cref="RiverPresetMode.Custom"/>.</summary>
        public struct RiverPresetValues
        {
            public int NumRivers;
            public int MinPoints;
            public int InnerRadius;
            public int OuterRadius;
            public int BlobRadius;
            public int BlobStride;
            public float TrenchDepth;
            public int SmoothPasses;
            public float JitterAmplitude;
            public float JitterFrequency;
            public int FishingAreaMultiplier;
        }

        // Preset values are tuned starting points — adjust over time as
        // user feedback comes in. Names match FF's New Game biome selector.
        private static readonly System.Collections.Generic.Dictionary<RiverPresetMode, RiverPresetValues> Presets
            = new System.Collections.Generic.Dictionary<RiverPresetMode, RiverPresetValues>
        {
            // IdyllicValley is the recommended default — mirrors v1.2.0
            // calibrated baseline. Balanced rolling terrain with clean banks.
            [RiverPresetMode.IdyllicValley] = new RiverPresetValues
            {
                NumRivers = 4, MinPoints = 15,
                InnerRadius = 6, OuterRadius = 10, BlobRadius = 6, BlobStride = 3,
                TrenchDepth = 1.8f, SmoothPasses = 6,
                JitterAmplitude = 1.5f, JitterFrequency = 0.6f,
                FishingAreaMultiplier = 4,
            },
            // LowlandLakes: flat with ponds. Aggressive: short paths, more
            // attempts, narrow shallow streams.
            [RiverPresetMode.LowlandLakes] = new RiverPresetValues
            {
                NumRivers = 8, MinPoints = 6,
                InnerRadius = 4, OuterRadius = 6, BlobRadius = 4, BlobStride = 2,
                TrenchDepth = 1.2f, SmoothPasses = 8,
                JitterAmplitude = 0.8f, JitterFrequency = 0.4f,
                FishingAreaMultiplier = 5,
            },
            // AridHighlands: high but dry. Fewer rivers, narrower channels,
            // deeper trenches to read as rocky.
            [RiverPresetMode.AridHighlands] = new RiverPresetValues
            {
                NumRivers = 3, MinPoints = 18,
                InnerRadius = 5, OuterRadius = 10, BlobRadius = 5, BlobStride = 3,
                TrenchDepth = 2.5f, SmoothPasses = 5,
                JitterAmplitude = 1.0f, JitterFrequency = 0.5f,
                FishingAreaMultiplier = 3,
            },
            // Plains: open semi-flat. Moderate river count and width;
            // less aggressive than LowlandLakes because there's slight gradient.
            [RiverPresetMode.Plains] = new RiverPresetValues
            {
                NumRivers = 5, MinPoints = 10,
                InnerRadius = 5, OuterRadius = 8, BlobRadius = 5, BlobStride = 2,
                TrenchDepth = 1.5f, SmoothPasses = 7,
                JitterAmplitude = 1.2f, JitterFrequency = 0.5f,
                FishingAreaMultiplier = 4,
            },
            // AlpineValleys: mountains/valleys. Long deep alpine drainages
            // with wide gradual banks and strong meander.
            [RiverPresetMode.AlpineValleys] = new RiverPresetValues
            {
                NumRivers = 4, MinPoints = 20,
                InnerRadius = 6, OuterRadius = 12, BlobRadius = 6, BlobStride = 3,
                TrenchDepth = 3.0f, SmoothPasses = 4,
                JitterAmplitude = 2.5f, JitterFrequency = 0.6f,
                FishingAreaMultiplier = 4,
            },
        };

        /// <summary>Resolve effective river-shape values, honoring the
        /// preset selector. When RiverPreset is Custom (or unset), values
        /// come from the individual granular cfg entries. Otherwise the
        /// preset's bundled values win. Used by gen-time code in
        /// RiverSettingsPatch / RiverCarver / RiverWaterAreaBuilder so
        /// every gen-time read hits a single source of truth.</summary>
        public static RiverPresetValues GetEffectiveValues()
        {
            var mode = RiverPreset?.Value ?? RiverPresetMode.IdyllicValley;
            if (mode != RiverPresetMode.Custom && Presets.TryGetValue(mode, out var preset))
                return preset;
            // Custom: pull from individual cfg entries (with safe fallbacks
            // so an uninitialized entry doesn't blow up gen).
            return new RiverPresetValues
            {
                NumRivers = NumRivers?.Value ?? 4,
                MinPoints = MinPoints?.Value > 0 ? MinPoints.Value : 15,
                InnerRadius = RiverInnerRadius?.Value ?? 6,
                OuterRadius = RiverOuterRadius?.Value ?? 10,
                BlobRadius = RiverBlobRadius?.Value ?? 6,
                BlobStride = RiverBlobStride?.Value ?? 3,
                TrenchDepth = RiverTrenchDepth?.Value ?? 1.8f,
                SmoothPasses = RiverSmoothPasses?.Value ?? 6,
                JitterAmplitude = RiverJitterAmplitude?.Value ?? 1.5f,
                JitterFrequency = RiverJitterFrequency?.Value ?? 0.6f,
                FishingAreaMultiplier = RiverFishingAreaMultiplier?.Value ?? 4,
            };
        }

        // ─────────────────────────────────────────────────────────────────
        public override void OnInitializeMelon()
        {
            Instance = this;

            // ── Preferences ───────────────────────────────────────────────
            var cat = MelonPreferences.CreateCategory("RiversRestored");

            RiversEnabled = cat.CreateEntry("RiversEnabled", true,
                display_name: "Rivers Enabled",
                description: "Master ON/OFF switch for the entire mod. " +
                             "Set to false to disable everything and play with " +
                             "vanilla behaviour (no rivers will generate).");

            RiverPreset = cat.CreateEntry("RiverPreset", RiverPresetMode.IdyllicValley,
                display_name: "River Preset (matches map biome)",
                description: "Pre-tuned bundle of river settings. Pick the option " +
                             "that matches your map's biome (names match the New Game " +
                             "biome selector):\n" +
                             "  IdyllicValley  — balanced rolling terrain (recommended default)\n" +
                             "  LowlandLakes   — flat with ponds; short shallow streams\n" +
                             "  AridHighlands  — high dry terrain; fewer narrower deeper rivers\n" +
                             "  Plains         — open semi-flat; moderate width and count\n" +
                             "  AlpineValleys  — mountains/valleys; long deep alpine drainages\n" +
                             "  Custom         — use the individual sliders under 'Granular Settings'\n" +
                             "When set to anything except Custom, the preset's values override " +
                             "any individual slider settings — they're suppressed until you " +
                             "switch back to Custom.");

            GranularSettings = cat.CreateEntry("GranularSettings", false,
                display_name: "Show Granular Settings (Advanced)",
                description: "When ON, individual river-shaping sliders become visible " +
                             "in the in-game settings UI (channel width, bank width, depth, " +
                             "smoothness, jitter, etc.). " +
                             "WARNING: setting custom values can produce visibly broken rivers " +
                             "(visible trench bottom, water spillover, harsh banks). The " +
                             "presets above are tuned for clean blending; recommended only for " +
                             "users who understand the trench/water/bank interaction. " +
                             "Granular sliders only take effect when River Preset is set to Custom.");

            EnableRibbonAnimation = cat.CreateEntry("EnableRibbonAnimation", true,
                display_name: "Enable Flowing-Water Animation",
                description: "When ON (default): rivers display the animated " +
                             "flowing-water ribbon mesh on top of the static water " +
                             "surface — visible swirling flow effect. " +
                             "When OFF: rivers render as a static green water " +
                             "surface only (like lakes), no flow animation. " +
                             "Turn OFF if you experience high CPU/GPU load or stutter " +
                             "on river-heavy maps — the ribbon's per-frame UV " +
                             "scrolling and per-cp subdivisions can be expensive. " +
                             "Fishing, resource avoidance, save/reload, and the " +
                             "carved riverbed all still work unchanged when this is OFF.");

            RiverPreferLakeWaterType = cat.CreateEntry("RiverPreferLakeWaterType", true,
                display_name: "Prefer Lake (Blue) Water for Rivers",
                description: "When ON (default): rivers use a Lake-type water " +
                             "material — clear blue, like normal lakes. " +
                             "When OFF: rivers use whatever water type the map " +
                             "happens to assign first (often Pond — green/murky). " +
                             "Pond water can look weird for rivers because it's " +
                             "tinted muddy-green and has lower transparency. Leave " +
                             "ON unless you specifically want green murky rivers.");

            NumRivers = cat.CreateEntry("NumRivers", 4,
                display_name: "Number of Rivers",
                description: "How many rivers the mod will try to generate on each new map. " +
                             "1-2 = sparse, 4 = balanced (default), 6+ = water-rich. " +
                             "The actual number may be lower if a seed can't fit them all. " +
                             "Vanilla = 2, but vanilla maps typically end up with 0 rivers " +
                             "because the game's filters reject every candidate.");

            MinPoints = cat.CreateEntry("MinPoints", 15,
                display_name: "Minimum River Length",
                description: "Minimum length each river must reach to be accepted, in " +
                             "internal waypoints (each waypoint is roughly 1-3 cells of " +
                             "river path). Lower = shorter rivers allowed. " +
                             "15 = default (lets short winding rivers through). " +
                             "Vanilla = 40, which is why vanilla maps almost never have rivers — " +
                             "the rejection filter is too strict. Set to 0 for vanilla behaviour.");

            MinWidth = cat.CreateEntry("MinWidth", 0,
                display_name: "Min River Ribbon Width",
                description: "Minimum width of the visible flowing-water ribbon (the animated " +
                             "stream effect that flows down the river). Vanilla picks a width " +
                             "between 2 and 8 cells per river. " +
                             "0 = use vanilla. Set 4+ to force every river to be at least medium width.");

            MaxWidth = cat.CreateEntry("MaxWidth", 0,
                display_name: "Max River Ribbon Width",
                description: "Maximum width of the visible flowing-water ribbon. " +
                             "Vanilla picks between 2 and 8 cells. " +
                             "0 = use vanilla. Set 6+ to allow some grand rivers in the mix.");

            MinDepth = cat.CreateEntry("MinDepth", -1f,
                display_name: "Min River Depth (legacy, mostly unused)",
                description: "An internal depth setting from FF's original river system. " +
                             "Has limited visual effect since this mod's actual carve depth is " +
                             "controlled by RiverTrenchDepth below. " +
                             "Leave at -1 to use vanilla default.");

            MaxDepth = cat.CreateEntry("MaxDepth", -1f,
                display_name: "Max River Depth (legacy, mostly unused)",
                description: "Companion to MinDepth above, also part of FF's original river system. " +
                             "The visible depth of your rivers is controlled by RiverTrenchDepth, " +
                             "not this. Leave at -1 to use vanilla default.");

            MarkWaterTypesAsRiverEnd = cat.CreateEntry("MarkWaterTypesAsRiverEnd", true,
                display_name: "Allow Rivers to End in Any Water",
                description: "When ON: rivers may end in any water body — ponds, small lakes, " +
                             "large lakes, or the border ocean. " +
                             "When OFF (vanilla): only Lakes and the Border Ocean count as valid " +
                             "endpoints, which is why vanilla rivers often fail to spawn at all. " +
                             "Leave ON for normal use.");

            RiverInnerRadius = cat.CreateEntry("RiverInnerRadius", 6,
                display_name: "River Channel Width (full depth)",
                description: "How wide the deep carved channel is, measured in cells " +
                             "(each cell is roughly 2.5 metres). The riverbed is dug " +
                             "down to full depth this far on each side of the river's " +
                             "centerline. " +
                             "3 = narrow stream (~7 cells across), " +
                             "6 = wide visible river (default, ~13 cells across), " +
                             "8+ = grand river. " +
                             "Bump River Bank Width with this so banks don't get too steep.");

            RiverOuterRadius = cat.CreateEntry("RiverOuterRadius", 10,
                display_name: "River Bank Width (slope to ground)",
                description: "How far out the sloped banks extend before reaching " +
                             "natural ground level, measured in cells from centerline. " +
                             "The cells between Channel Width and Bank Width get a " +
                             "smooth ramp from riverbed up to terrain. " +
                             "(Bank Width − Channel Width) is the slope distance: " +
                             "1 cell = sharp drop-off, 4 cells = lake-like blend (default), " +
                             "6+ = very gradual sloping banks. Must be ≥ Channel Width.");

            RiverJitterAmplitude = cat.CreateEntry("RiverJitterAmplitude", 1.5f,
                display_name: "River Meander Strength (metres)",
                description: "How much rivers wiggle and snake between their main " +
                             "waypoints, in metres of perpendicular offset. " +
                             "0 = perfectly straight lines between waypoints (boring), " +
                             "1.5 = subtle natural curves (default), " +
                             "5+ = strong snaking. " +
                             "High values can cause rivers to self-intersect on tight bends.");

            RiverJitterFrequency = cat.CreateEntry("RiverJitterFrequency", 0.6f,
                display_name: "River Meander Frequency",
                description: "How many curves fit between each pair of main waypoints. " +
                             "0.6 ≈ one and a bit curves per segment (default, looks natural). " +
                             "Higher = more zigzaggy, lower = sweeping arcs. " +
                             "Has no effect if Meander Strength is 0.");

            RiverSmoothPasses = cat.CreateEntry("RiverSmoothPasses", 6,
                display_name: "Bank Smoothness",
                description: "How many smoothing passes are applied to the riverbanks " +
                             "after carving. Smooths out the staircase look from the " +
                             "raw cell-by-cell carve. " +
                             "0 = no smoothing (rough/blocky banks), " +
                             "2 = mild, " +
                             "6 = lake-like softness (default), " +
                             "8+ = very gentle. " +
                             "Each pass adds a couple seconds to map generation on large maps.");

            RiverTrenchDepth = cat.CreateEntry("RiverTrenchDepth", 1.8f,
                display_name: "River Depth (metres below water)",
                description: "How deep below the water surface the riverbed is dug, " +
                             "in metres. Affects how visibly 'wet' the river looks — " +
                             "shallow rivers can render patchy or transparent. " +
                             "1.5 = visibility floor (anything shallower may look thin), " +
                             "1.8 = lake-like (default), " +
                             "2.5 = noticeably deeper, " +
                             "4+ = dramatic canyon rivers. " +
                             "Vanilla FF rivers carve 2-10m so this matches their calibration.");

            ForceCoastlineTerrain = cat.CreateEntry("ForceCoastlineTerrain", false,
                display_name: "[Diagnostic] Force Coastline Map Type",
                description: "[Developer use only — leave OFF.] Forces every map to " +
                             "the Coastline biome regardless of UI selection. " +
                             "Was used during mod development to test rivers on different " +
                             "biome types. Has no useful effect for normal play.");

            RiverRegisterAsWaterArea = cat.CreateEntry("RiverRegisterAsWaterArea", true,
                display_name: "Treat Rivers as Real Water Bodies",
                description: "When ON: rivers behave like proper bodies of water. Trees, " +
                             "rocks, and animals don't spawn on river cells; villagers can " +
                             "fish from rivers; the river has a flat water surface that " +
                             "saves/reloads correctly. " +
                             "When OFF: rivers are visual ribbons only — no fishing, " +
                             "resources may spawn in the riverbed, no proper water plane. " +
                             "Leave ON for normal play.");

            RiverBlobRadius = cat.CreateEntry("RiverBlobRadius", 6,
                display_name: "Visible Water Width",
                description: "Width of the visible water surface (the 'brown bed under " +
                             "the flowing-water animation that you can fish in'), measured " +
                             "in cells from centerline. " +
                             "Default 6 matches Channel Width so the water surface fills " +
                             "the carved riverbed exactly. " +
                             "Set higher than Channel Width to make water spill out onto " +
                             "the bank slope (river overflowing its banks). " +
                             "Set lower for a clean carved-trench look with rocky shores.");

            RiverBlobStride = cat.CreateEntry("RiverBlobStride", 3,
                display_name: "Water Surface Density (advanced)",
                description: "Internal setting that controls how densely the water-surface " +
                             "polygon is built along the river path. Lower = smoother shape " +
                             "with more building work, higher = faster generation but may " +
                             "leave gaps on tight bends. " +
                             "3 = default (heavy overlap, smooth even on sharp curves), " +
                             "5+ = faster generation. " +
                             "Don't change unless map-gen feels slow.");

            RiverFishingAreaMultiplier = cat.CreateEntry("RiverFishingAreaMultiplier", 4,
                display_name: "River Fishing Productivity Boost",
                description: "Boosts how productive a Fishing Shack/Dock is when it's " +
                             "placed next to a river (not lakes/ocean — those use vanilla " +
                             "values). FF normally penalises shacks with few fishing zones, " +
                             "and a single river typically only counts as one zone, leaving " +
                             "river fishing weak. " +
                             "1 = no boost (vanilla, river fishing feels weak), " +
                             "4 = good balance (default, ~100% productivity for a river-side shack), " +
                             "8+ = lush fishing economy. " +
                             "Lakes and ocean fishing are unaffected.");

            VerboseDiagnostics = cat.CreateEntry("VerboseDiagnostics", false,
                display_name: "Verbose Diagnostic Logging",
                description: "When ON, the mod writes detailed per-WaterArea state " +
                             "at save time, per-stage waterArea counts during map gen, " +
                             "and one-shot dumps of FF's internal terrain/biome/layer " +
                             "structures the first time gen runs. Use this if rivers " +
                             "aren't surviving save/reload and you want to share a log " +
                             "for diagnosis. Leave OFF for normal play — the log gets " +
                             "noisy fast.");

            // ── Apply granular-visibility based on cfg, and update on toggle ──
            ApplyGranularVisibility();
            try
            {
                GranularSettings.OnEntryValueChangedUntyped.Subscribe(
                    (oldVal, newVal) => ApplyGranularVisibility());
            }
            catch (System.Exception ex)
            {
                Log.Warning($"[RR] Failed to subscribe to GranularSettings change event: {ex.Message}. " +
                            "UI will only update visibility on next launch.");
            }

            // ── Harmony ───────────────────────────────────────────────────
            HarmonyInstance = new HarmonyLib.Harmony("SageDragoon.RiversRestored");

            try
            {
                Patches.RiverSettingsPatch.Apply(HarmonyInstance);
                Patches.RiverPersistence.Apply(HarmonyInstance);
                Patches.FishingShackPatch.Apply(HarmonyInstance);
                Log.Msg($"[RR] Rivers Restored 1.2.0 loaded. NumRivers={NumRivers.Value}, " +
                        $"RiversEnabled={RiversEnabled.Value}");

                // Optional: register with Keep Clarity's settings panel if installed.
                KeepClarityIntegration.TryRegisterAll();
            }
            catch (System.Exception ex)
            {
                Log.Error($"[RR] Failed to apply patches: {ex}");
            }
        }

        /// <summary>Toggle the IsHidden flag on every granular cfg entry
        /// based on the current value of <see cref="GranularSettings"/>.
        /// FFModSettingsManager (and other MelonPreferences-aware UIs that
        /// respect IsHidden) will hide/show entries accordingly.
        ///
        /// The cfg text file always contains every entry — hiding only
        /// affects the in-game UI. Power users hand-editing the cfg can
        /// see and edit hidden entries directly.</summary>
        private void ApplyGranularVisibility()
        {
            bool show = GranularSettings?.Value ?? false;
            // Granular entries: every river-shaping slider that the
            // RiverPreset overrides when active. Master toggles, presets,
            // ribbon toggle, blue-water preference, and verbose-logging
            // stay always-visible (those are basic-tier choices).
            var granularEntries = new MelonPreferences_Entry?[]
            {
                NumRivers, MinPoints, MinWidth, MaxWidth, MinDepth, MaxDepth,
                MarkWaterTypesAsRiverEnd,
                RiverInnerRadius, RiverOuterRadius,
                RiverJitterAmplitude, RiverJitterFrequency,
                RiverSmoothPasses, RiverTrenchDepth,
                ForceCoastlineTerrain,
                RiverRegisterAsWaterArea,
                RiverBlobRadius, RiverBlobStride,
                RiverFishingAreaMultiplier,
            };
            int hiddenCount = 0;
            foreach (var entry in granularEntries)
            {
                if (entry == null) continue;
                try
                {
                    entry.IsHidden = !show;
                    if (!show) hiddenCount++;
                }
                catch (System.Exception ex)
                {
                    Log.Warning($"[RR] Couldn't set IsHidden on '{entry.Identifier}': {ex.Message}");
                }
            }
            Log.Msg($"[RR] Granular settings UI: {(show ? "VISIBLE" : "HIDDEN")} " +
                    $"({granularEntries.Length} entries, {hiddenCount} hidden).");
        }

        /// <summary>
        /// Reset the one-shot override guard whenever a scene reloads.
        /// Without this, a second new-map attempt in the same session would
        /// skip the override (thinking it already applied).
        /// </summary>
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Patches.RiverSettingsPatch.ResetGuard();
            Patches.RiverPersistence.ResetForSceneLoad();
            Patches.FishingShackPatch.ResetForSceneLoad();
            Patches.RiverWaterAreaBuilder.ResetForSceneLoad();
            // Reset the carver's _carved latch too. RiverSettingsPatch.DoOverride
            // resets it on a settings-delta path, but if a player goes
            // (new map → new map) without changing RiverSettings, the
            // "Already matches? Silent no-op" early-return skips the
            // reset. Doing it here on every scene transition is a safer
            // default — generation hasn't started yet.
            Patches.RiverCarver.ResetGuard();
            _seenGenerator = false;
            _seenTerrain = false;
            _dumpedSaveManager = false;
        }

        /// <summary>
        /// Per-frame polling: once we have rivers in generationData AND a live
        /// Terrain in the scene, fire the manual carve. This sidesteps the
        /// problem of which sliced-pipeline stages fire after Terrain init.
        /// The carver itself has a one-shot guard so we won't re-carve.
        /// </summary>
        // One-shot diagnostic flags so we log "ready" conditions exactly once
        private bool _seenGenerator = false;
        private bool _seenTerrain = false;
        private bool _dumpedSaveManager = false;

        public override void OnUpdate()
        {
            if (!RiversEnabled.Value) return;
            try
            {
                // Prefer the cached generator from hooks; fall back to scene scan
                var tg = Patches.RiverSettingsPatch.CachedGenerator
                         ?? UnityEngine.Object.FindObjectOfType<TerrainGen.TerrainGenerator>();
                if (tg != null && !_seenGenerator)
                {
                    _seenGenerator = true;
                    Log.Msg("[RR][Poll] TerrainGenerator visible to OnUpdate");
                }

                if (tg == null) return;

                // ── Save-load river restoration ─────────────────────────────
                // The actual respawn is now driven by our Terrain2Builder
                // .BuildTerrainShared03 postfix — that method runs DURING
                // FF's terrain rebuild and clears the "Rivers" bucket via
                // CreateBucket. Spawning before then (which OnUpdate used
                // to do) caused the WaterPath GameObjects to be destroyed
                // by FF's load pipeline a moment later → invisible water.
                //
                // OnUpdate's job during save load is now: dump SaveManager
                // structure once for diagnostics, resolve the save name
                // and queue it (so the postfix has it ready), then bail
                // so the carver doesn't run. Spawn happens in the postfix.
                if (Patches.RiverSettingsPatch.IsLoadingSavedMap(tg))
                {
                    if (Patches.RiverPersistence.RestoredThisLoad) return;

                    if (!_dumpedSaveManager)
                    {
                        _dumpedSaveManager = true;
                        Patches.RiverPersistence.DumpSaveManager();
                    }
                    if (!Patches.RiverPersistence.RestorePending)
                    {
                        try
                        {
                            string? saveName = Patches.RiverPersistence.TryFindLoadedSaveName();
                            if (!string.IsNullOrEmpty(saveName))
                                Patches.RiverPersistence.MarkRestorePending(saveName!);
                        }
                        catch (System.Exception ex)
                        {
                            Log.Warning($"[RR] Save-name detection threw: {ex.Message}");
                        }
                    }
                    return; // postfix handles the spawn; carver must not run
                }

                // Carver checks FF's Terrain2 / TerrainManagerBase availability
                // internally and returns silently if either isn't ready yet.
                Patches.RiverCarver.CarveAllRivers(tg);
            }
            catch (System.Exception ex)
            {
                if (!_seenTerrain)
                    Log.Warning($"[RR][Poll] OnUpdate error: {ex.Message}");
            }
        }

    }
}
