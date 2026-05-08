using System.Linq;
using HarmonyLib;
using MelonLoader;

// ─────────────────────────────────────────────────────────────────────────────
//  Rivers Restored v1.3.0
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

[assembly: MelonInfo(typeof(RiversRestored.RiversRestoredMod), "Rivers Restored", "1.3.0", "SageDragoon")]
[assembly: MelonGame("Crate Entertainment", "Farthest Frontier")]

namespace RiversRestored
{
    /// <summary>Directional bias for river drainage paths. When set to
    /// anything except <see cref="None"/>, the heightmap is subtly tilted
    /// at gen time before Stage 38's Voronoi pathfinder runs, so rivers
    /// statistically prefer flowing from the named "high" corner toward
    /// the named "low" corner. Strength is controlled by
    /// <see cref="RiversRestoredMod.RiverFlowBiasStrength"/>.</summary>
    public enum RiverFlowBiasMode
    {
        /// <summary>No bias — pure Voronoi seed-driven directions (default).</summary>
        None,
        /// <summary>High in NE, low in SW. Rivers flow from northeast to southwest.</summary>
        NE_to_SW,
        /// <summary>High in NW, low in SE. Rivers flow from northwest to southeast.</summary>
        NW_to_SE,
        /// <summary>High in SW, low in NE. Rivers flow from southwest to northeast.</summary>
        SW_to_NE,
        /// <summary>High in SE, low in NW. Rivers flow from southeast to northwest.</summary>
        SE_to_NW,
        /// <summary>High in N, low in S. Rivers flow from north to south.</summary>
        N_to_S,
        /// <summary>High in S, low in N. Rivers flow from south to north.</summary>
        S_to_N,
        /// <summary>High in E, low in W. Rivers flow from east to west.</summary>
        E_to_W,
        /// <summary>High in W, low in E. Rivers flow from west to east.</summary>
        W_to_E,
    }

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

        // ── River flow direction bias ──────────────────────────────────────
        /// <summary>Tilt the heightmap before river-path generation so
        /// rivers statistically flow from a chosen high corner/edge to a
        /// chosen low one. Lets you guarantee scenic edge-to-edge rivers
        /// in a desired direction without hand-painting paths.
        /// FFModSettingsManager renders this as a dropdown.</summary>
        public static MelonPreferences_Entry<RiverFlowBiasMode> RiverFlowBias { get; private set; } = null!;

        /// <summary>How strongly to tilt the heightmap when
        /// <see cref="RiverFlowBias"/> is non-None. Range 0.0–1.0. 0.3 is
        /// a subtle hint (some rivers will still go their own way), 0.5
        /// is reliable (most rivers follow the bias), 1.0 is dramatic
        /// (visible map tilt, all rivers follow). Default 0.4.</summary>
        public static MelonPreferences_Entry<float> RiverFlowBiasStrength { get; private set; } = null!;

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

        /// <summary>When true, swaps the Pond WaterType's visual materials
        /// (waterMaterial, foamMaterial, etc.) with LakeSmall's. Pond
        /// classification still happens normally — small/marshy water
        /// areas are still tagged as Pond — but they render with the blue
        /// lake material instead of the green pond one. Side-effects every
        /// Pond water body in the game session, not just RR-tracked rivers
        /// or lakes. Persists for the rest of the process; no re-do per gen.
        /// Defaults to false; opt-in for users who want guaranteed
        /// blue/clear water everywhere.</summary>
        public static MelonPreferences_Entry<bool>? PondUseLakeMaterial { get; private set; }

        /// <summary>When true, RR renders a top-down preview image of each
        /// generated map (heightmap as grayscale relief + water/rivers as
        /// blue overlay) and writes it as a PNG to
        /// <c>UserData/RiversRestored/Previews/&lt;seed&gt;_&lt;timestamp&gt;.png</c>.
        /// Stage 2 of the in-game previewer feature — at this stage, the
        /// preview is verified by the user opening the PNG file directly.
        /// Default OFF — opt-in until the rendering is dialed in. No
        /// gameplay impact; image is generated post-Stage-60 and is purely
        /// observational.</summary>
        public static MelonPreferences_Entry<bool>? EnableMapPreviewRender { get; private set; }

        /// <summary>When true, displays the most-recently-rendered map
        /// preview as an in-game overlay panel (right side of screen, mid-
        /// vertical). Toggleable mid-session via F8 hotkey too. Requires
        /// <see cref="EnableMapPreviewRender"/> to also be ON so a preview
        /// actually exists to display.</summary>
        public static MelonPreferences_Entry<bool>? ShowPreviewOverlay { get; private set; }

        // ── Preset value table ─────────────────────────────────────────────
        /// <summary>Bundle of preset values that override the granular cfg
        /// entries when <see cref="RiverPreset"/> is anything other than
        /// <see cref="RiverPresetMode.Custom"/>.</summary>
        public struct RiverPresetValues
        {
            public int NumRivers;
            public int MinPoints;
            public int MinWidth;     // ribbon mesh min width (cells), 0 = vanilla 2-8
            public int MaxWidth;     // ribbon mesh max width (cells), 0 = vanilla 2-8
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
            // IdyllicValley is the recommended default — balanced rolling
            // terrain with gentle banks and lake-style water rendering.
            //
            // Geometry rationale (from in-game tuning, see WATER_LEVERS.md):
            //   • InnerRadius=4 / BlobRadius=4 — water polygon covers the
            //     full carved trench, no exposed riverbed.
            //   • OuterRadius=12 — 8-cell slope distance for gentle banks.
            //   • TrenchDepth=3.0 — comfortably above vanilla's minDepth=2.5
            //     "is this water?" floor. Below that, water can fail to
            //     render in thin spots.
            //   • MinWidth=8 / MaxWidth=12 — ribbon mesh wider than the
            //     8-cell polygon, so the ribbon visually covers the green
            //     Pond-classified water surface underneath. Trade-off:
            //     gameplay zones (fishing, no-spawn) are bound by the
            //     polygon, not the ribbon — narrower than visible river.
            //   • SmoothPasses=8 — extra smoothing for the wider slope.
            [RiverPresetMode.IdyllicValley] = new RiverPresetValues
            {
                NumRivers = 4, MinPoints = 15,
                MinWidth = 8, MaxWidth = 12,    // ribbon hides the 8-cell water polygon
                InnerRadius = 4, OuterRadius = 12, BlobRadius = 4, BlobStride = 3,
                TrenchDepth = 3.0f, SmoothPasses = 8,
                JitterAmplitude = 1.5f, JitterFrequency = 0.6f,
                FishingAreaMultiplier = 4,
            },
            // LowlandLakes: flat with ponds. Aggressive count + short paths,
            // but polygon footprint matched to IdyllicValley so the merged
            // shape clears FF's Lake-area threshold (otherwise reload tags
            // it as Pond and water can fail to render).
            [RiverPresetMode.LowlandLakes] = new RiverPresetValues
            {
                NumRivers = 8, MinPoints = 6,
                MinWidth = 8, MaxWidth = 12,    // ribbon inset within 12-cell polygon
                InnerRadius = 6, OuterRadius = 10, BlobRadius = 6, BlobStride = 3,
                TrenchDepth = 1.2f, SmoothPasses = 8,
                JitterAmplitude = 0.8f, JitterFrequency = 0.4f,
                FishingAreaMultiplier = 5,
            },
            // AridHighlands: high but dry. Fewer rivers, deeper trenches to
            // read as rocky. Polygon footprint matched to IdyllicValley so
            // the merged shape clears FF's Lake-area threshold.
            [RiverPresetMode.AridHighlands] = new RiverPresetValues
            {
                NumRivers = 3, MinPoints = 18,
                MinWidth = 8, MaxWidth = 12,    // ribbon inset within 12-cell polygon
                InnerRadius = 6, OuterRadius = 10, BlobRadius = 6, BlobStride = 3,
                TrenchDepth = 2.5f, SmoothPasses = 5,
                JitterAmplitude = 1.0f, JitterFrequency = 0.5f,
                FishingAreaMultiplier = 3,
            },
            // Plains: single long river bisecting the map. One dominant
            // waterway, deep and meandering, edge-to-edge. Terrain is open
            // so the river dominates the visual — no competing rivers.
            //
            // For the bisecting effect to actually trigger, the player MUST
            // also set in MelonPreferences (these are not part of the
            // preset bundle):
            //   RiverFlowBias = "E_to_W" (or any cardinal/diagonal)
            //   RiverFlowBiasStrength = 0.7  (strong tilt)
            //   MarkWaterTypesAsRiverEnd = true  (lets map-edge ocean be a terminator)
            //
            // Trade-offs:
            //   • NumRivers = 1 means no "failed-rivers-become-lakes" side
            //     channel — you get one river plus only natural lakes.
            //   • If validation fails (rare, but possible on hostile seeds),
            //     the map gets zero rivers. Drop MinPoints to ~25 if you
            //     see this happen.
            //   • Wide deep trench + heavy meander + max fishing multiplier
            //     intentionally make this single river feel like the
            //     map's centerpiece feature.
            [RiverPresetMode.Plains] = new RiverPresetValues
            {
                NumRivers = 1, MinPoints = 35,         // single long river
                MinWidth = 14, MaxWidth = 18,          // major-river ribbon
                InnerRadius = 5, OuterRadius = 14, BlobRadius = 5, BlobStride = 3,
                TrenchDepth = 4.0f, SmoothPasses = 10, // deep + very smooth banks
                JitterAmplitude = 2.5f, JitterFrequency = 0.4f, // sweeping bends
                FishingAreaMultiplier = 8,             // sole river is the food source
            },
            // AlpineValleys: mountains/valleys. Long deep alpine drainages
            // with wide gradual banks and strong meander.
            [RiverPresetMode.AlpineValleys] = new RiverPresetValues
            {
                NumRivers = 4, MinPoints = 20,
                MinWidth = 10, MaxWidth = 14,   // big rivers in 12-cell polygon
                InnerRadius = 6, OuterRadius = 12, BlobRadius = 6, BlobStride = 3,
                TrenchDepth = 3.0f, SmoothPasses = 4,
                JitterAmplitude = 2.5f, JitterFrequency = 0.6f,
                FishingAreaMultiplier = 4,
            },
        };

        // ── Per-preset live-tunable entry bundles ──────────────────────────
        /// <summary>Holds MelonPreferences_Entry references for one preset's
        /// 13 tunable values. Mirrors the field set in <see cref="RiverPresetValues"/>.
        /// Populated in <see cref="OnInitializeMelon"/> via
        /// <see cref="CreatePresetEntries(string, RiverPresetValues)"/> — one
        /// per non-Custom preset. <see cref="GetEffectiveValues"/> reads from
        /// the active preset's entries so the user can tune live in
        /// MelonPreferences.cfg / KC's settings UI without needing a code
        /// change. Defaults seeded from the hardcoded <see cref="Presets"/>
        /// dictionary so first-launch behavior is unchanged.</summary>
        public class RiverPresetEntries
        {
            public MelonPreferences_Entry<int> NumRivers = null!;
            public MelonPreferences_Entry<int> MinPoints = null!;
            public MelonPreferences_Entry<int> MinWidth = null!;
            public MelonPreferences_Entry<int> MaxWidth = null!;
            public MelonPreferences_Entry<int> InnerRadius = null!;
            public MelonPreferences_Entry<int> OuterRadius = null!;
            public MelonPreferences_Entry<int> BlobRadius = null!;
            public MelonPreferences_Entry<int> BlobStride = null!;
            public MelonPreferences_Entry<float> TrenchDepth = null!;
            public MelonPreferences_Entry<int> SmoothPasses = null!;
            public MelonPreferences_Entry<float> JitterAmplitude = null!;
            public MelonPreferences_Entry<float> JitterFrequency = null!;
            public MelonPreferences_Entry<int> FishingAreaMultiplier = null!;

            public RiverPresetValues ToValues() => new RiverPresetValues
            {
                NumRivers = NumRivers.Value,
                MinPoints = MinPoints.Value,
                MinWidth = MinWidth.Value,
                MaxWidth = MaxWidth.Value,
                InnerRadius = InnerRadius.Value,
                OuterRadius = OuterRadius.Value,
                BlobRadius = BlobRadius.Value,
                BlobStride = BlobStride.Value,
                TrenchDepth = TrenchDepth.Value,
                SmoothPasses = SmoothPasses.Value,
                JitterAmplitude = JitterAmplitude.Value,
                JitterFrequency = JitterFrequency.Value,
                FishingAreaMultiplier = FishingAreaMultiplier.Value,
            };
        }

        /// <summary>One <see cref="RiverPresetEntries"/> per non-Custom preset
        /// mode. Custom mode continues to use the existing top-level granular
        /// entries (NumRivers, MinPoints, etc.) — no change there.</summary>
        public static readonly System.Collections.Generic.Dictionary<RiverPresetMode, RiverPresetEntries> PresetEntries
            = new System.Collections.Generic.Dictionary<RiverPresetMode, RiverPresetEntries>();

        // Slider descriptions — extracted to constants so per-preset entries
        // can reuse the same explanatory text without duplication. Mirror the
        // descriptions used by the Custom granular sliders below.
        private const string DESC_NUM_RIVERS =
            "How many rivers the mod will try to generate on each new map. " +
            "1-2 = sparse, 4 = balanced, 6+ = water-rich. " +
            "The actual number may be lower if a seed can't fit them all.";
        private const string DESC_MIN_POINTS =
            "Minimum length each river must reach to be accepted, in internal " +
            "waypoints (each waypoint is roughly 1-3 cells of river path). " +
            "Lower = shorter rivers allowed. 15 = lets short winding rivers through. " +
            "Vanilla = 40, which is why vanilla maps almost never have rivers.";
        private const string DESC_MIN_WIDTH =
            "Minimum width of the visible flowing-water ribbon, in cells. " +
            "0 = use vanilla (2-8). Set 4+ to force every river to be at least medium width.";
        private const string DESC_MAX_WIDTH =
            "Maximum width of the visible flowing-water ribbon, in cells. " +
            "0 = use vanilla (2-8). Set 6+ to allow some grand rivers in the mix.";
        private const string DESC_INNER_RADIUS =
            "How wide the deep carved channel is, in cells (each cell ≈ 2.5m). " +
            "3 = narrow stream, 6 = wide visible river, 8+ = grand river. " +
            "Bump River Bank Width with this so banks don't get too steep.";
        private const string DESC_OUTER_RADIUS =
            "How far out the sloped banks extend before reaching natural ground, " +
            "in cells from centerline. (Bank Width − Channel Width) is the slope " +
            "distance: 1 cell = sharp drop-off, 4 cells = lake-like blend, " +
            "6+ = very gradual. Must be ≥ Channel Width.";
        private const string DESC_BLOB_RADIUS =
            "Width of the visible water surface in cells from centerline. " +
            "Match Channel Width so water fills the carved riverbed exactly. " +
            "Higher = water spills onto bank slope (overflow look). " +
            "Lower = clean carved-trench look with rocky shores.";
        private const string DESC_BLOB_STRIDE =
            "Internal density of the water-surface polygon along the river path. " +
            "Lower = smoother shape with more building work. Higher = faster gen " +
            "but may leave gaps on tight bends. 3 = default heavy overlap. " +
            "Don't change unless gen feels slow.";
        private const string DESC_TRENCH_DEPTH =
            "How deep below the water surface the riverbed is dug, in metres. " +
            "1.5 = visibility floor, 1.8 = lake-like, 2.5 = noticeably deeper, " +
            "4+ = dramatic canyon rivers.";
        private const string DESC_SMOOTH_PASSES =
            "Smoothing passes applied to riverbanks after carving. " +
            "0 = rough/blocky, 2 = mild, 6 = lake-like softness, 8+ = very gentle. " +
            "Each pass adds a couple seconds to large-map gen.";
        private const string DESC_JITTER_AMPLITUDE =
            "How much rivers wiggle/snake between waypoints, in metres of " +
            "perpendicular offset. 0 = perfectly straight, 1.5 = subtle natural " +
            "curves, 5+ = strong snaking (can self-intersect on tight bends).";
        private const string DESC_JITTER_FREQUENCY =
            "How many curves fit between each pair of main waypoints. " +
            "0.6 ≈ one and a bit curves per segment (looks natural). " +
            "Higher = more zigzaggy, lower = sweeping arcs. " +
            "Has no effect if Meander Strength is 0.";
        private const string DESC_FISHING_MULTIPLIER =
            "Boosts Fishing Shack productivity when placed next to a river. " +
            "1 = no boost (river fishing feels weak), 4 = good balance, " +
            "8+ = lush fishing economy. Lakes and ocean fishing are unaffected.";

        /// <summary>Create a fresh MelonPreferences category for one preset
        /// and populate it with all 13 tunable entries, defaulting to that
        /// preset's hardcoded values. The category name is
        /// <c>RiversRestored.&lt;PresetName&gt;</c> so cfg layout and KC's
        /// UI grouping stay clean.</summary>
        private static RiverPresetEntries CreatePresetEntries(string presetName, RiverPresetValues defaults)
        {
            // CreateCategory returns the existing category if the name is
            // already registered, so re-init is safe. Category name uses
            // underscore, not period, because MelonLoader 0.7.0 treats
            // period-named categories as not-default-file-bound (entries
            // live in memory, never persist to MelonPreferences.cfg).
            // SetFilePath would normally be the fix but its signature in
            // 0.7.0 throws NRE on us. Underscore-named categories follow
            // the default file-binding convention and persist correctly.
            var cat = MelonPreferences.CreateCategory($"RiversRestored_{presetName}");
            var prefix = $"[{presetName}] ";

            return new RiverPresetEntries
            {
                NumRivers = cat.CreateEntry("NumRivers", defaults.NumRivers,
                    display_name: prefix + "Number of Rivers",
                    description: DESC_NUM_RIVERS),
                MinPoints = cat.CreateEntry("MinPoints", defaults.MinPoints,
                    display_name: prefix + "Minimum River Length",
                    description: DESC_MIN_POINTS),
                MinWidth = cat.CreateEntry("MinWidth", defaults.MinWidth,
                    display_name: prefix + "Min River Ribbon Width",
                    description: DESC_MIN_WIDTH),
                MaxWidth = cat.CreateEntry("MaxWidth", defaults.MaxWidth,
                    display_name: prefix + "Max River Ribbon Width",
                    description: DESC_MAX_WIDTH),
                InnerRadius = cat.CreateEntry("InnerRadius", defaults.InnerRadius,
                    display_name: prefix + "River Channel Width (full depth)",
                    description: DESC_INNER_RADIUS),
                OuterRadius = cat.CreateEntry("OuterRadius", defaults.OuterRadius,
                    display_name: prefix + "River Bank Width (slope to ground)",
                    description: DESC_OUTER_RADIUS),
                BlobRadius = cat.CreateEntry("BlobRadius", defaults.BlobRadius,
                    display_name: prefix + "Visible Water Width",
                    description: DESC_BLOB_RADIUS),
                BlobStride = cat.CreateEntry("BlobStride", defaults.BlobStride,
                    display_name: prefix + "Water Surface Density (advanced)",
                    description: DESC_BLOB_STRIDE),
                TrenchDepth = cat.CreateEntry("TrenchDepth", defaults.TrenchDepth,
                    display_name: prefix + "River Depth (metres below water)",
                    description: DESC_TRENCH_DEPTH),
                SmoothPasses = cat.CreateEntry("SmoothPasses", defaults.SmoothPasses,
                    display_name: prefix + "Bank Smoothness",
                    description: DESC_SMOOTH_PASSES),
                JitterAmplitude = cat.CreateEntry("JitterAmplitude", defaults.JitterAmplitude,
                    display_name: prefix + "River Meander Strength (metres)",
                    description: DESC_JITTER_AMPLITUDE),
                JitterFrequency = cat.CreateEntry("JitterFrequency", defaults.JitterFrequency,
                    display_name: prefix + "River Meander Frequency",
                    description: DESC_JITTER_FREQUENCY),
                FishingAreaMultiplier = cat.CreateEntry("FishingAreaMultiplier", defaults.FishingAreaMultiplier,
                    display_name: prefix + "River Fishing Productivity Boost",
                    description: DESC_FISHING_MULTIPLIER),
            };
        }

        /// <summary>Resolve effective river-shape values, honoring the
        /// preset selector. When RiverPreset is Custom (or unset), values
        /// come from the individual granular cfg entries. Otherwise the
        /// active preset's per-preset entries win (live-tunable in
        /// MelonPreferences.cfg / KC). Falls back to the hardcoded
        /// <see cref="Presets"/> dictionary if entries aren't yet populated
        /// (defensive — should always exist after OnInitializeMelon).</summary>
        public static RiverPresetValues GetEffectiveValues()
        {
            var mode = RiverPreset?.Value ?? RiverPresetMode.IdyllicValley;
            if (mode != RiverPresetMode.Custom)
            {
                if (PresetEntries.TryGetValue(mode, out var entries) && entries != null)
                    return entries.ToValues();
                if (Presets.TryGetValue(mode, out var preset))
                    return preset;
            }
            // Custom: pull from individual cfg entries (with safe fallbacks
            // so an uninitialized entry doesn't blow up gen).
            return new RiverPresetValues
            {
                NumRivers = NumRivers?.Value ?? 4,
                MinPoints = MinPoints?.Value > 0 ? MinPoints.Value : 15,
                MinWidth = MinWidth?.Value ?? 0,    // 0 = vanilla 2-8
                MaxWidth = MaxWidth?.Value ?? 0,    // 0 = vanilla 2-8
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

            RiverFlowBias = cat.CreateEntry("RiverFlowBias", RiverFlowBiasMode.None,
                display_name: "River Flow Direction Bias",
                description: "Tilt the heightmap before river-path generation so rivers " +
                             "statistically flow from a chosen high corner/edge to a chosen " +
                             "low one. Useful for guaranteeing scenic edge-to-edge rivers " +
                             "in a desired direction:\n" +
                             "  None      — pure seed-driven directions (default, no bias)\n" +
                             "  NE_to_SW  — high in NE, low in SW (rivers flow southwest)\n" +
                             "  NW_to_SE  — high in NW, low in SE\n" +
                             "  SW_to_NE  — high in SW, low in NE\n" +
                             "  SE_to_NW  — high in SE, low in NW\n" +
                             "  N_to_S, S_to_N, E_to_W, W_to_E  — cardinal directions\n" +
                             "Bias is statistical — about 70-90% of rivers follow the chosen " +
                             "direction depending on Strength. Lakes and other water bodies " +
                             "are also nudged toward the low end (realistic — water pools " +
                             "where it's lowest). Affects new map gens only; existing saves " +
                             "unaffected.");

            RiverFlowBiasStrength = cat.CreateEntry("RiverFlowBiasStrength", 0.4f,
                display_name: "Flow Bias Strength",
                description: "How strongly to tilt the heightmap when River Flow Direction " +
                             "Bias is set. Range 0.0–1.0:\n" +
                             "  0.3  — subtle (some rivers may still go their own way)\n" +
                             "  0.4  — balanced default; reliable for most maps\n" +
                             "  0.5  — strong (most rivers follow the bias)\n" +
                             "  0.7+ — visible map tilt; can look unnatural in flat biomes\n" +
                             "Has no effect when River Flow Direction Bias is None.");

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

            PondUseLakeMaterial = cat.CreateEntry("PondUseLakeMaterial", true,
                display_name: "Force Pond Water to Render as Lake (Blue)",
                description: "When ON (default): every Pond in the game session uses " +
                             "the blue Lake water material instead of the green Pond " +
                             "one. Pond classification is unchanged (small/marshy areas " +
                             "still classify as Pond), only the visual material swaps. " +
                             "Affects ALL ponds on every map. Recommended ON because " +
                             "it also fixes the save/reload WaterType-orphan bug — " +
                             "orphaned water polygons fall back to Pond's material, " +
                             "so with this swap they render blue instead of invisible. " +
                             "Set OFF if you want vanilla pond visual variety and don't " +
                             "care about save/reload water visibility.");

            EnableMapPreviewRender = cat.CreateEntry("EnableMapPreviewRender", false,
                display_name: "[Beta] Render Map Previews to PNG",
                description: "When ON: after each map gen, RR writes a top-down " +
                             "preview image of the heightmap + rivers + lakes to " +
                             "UserData/RiversRestored/Previews/<seed>_<timestamp>.png. " +
                             "Useful for verifying RR's gen output without launching " +
                             "into a settlement. Default OFF — opt-in until " +
                             "rendering is dialed in.");

            ShowPreviewOverlay = cat.CreateEntry("ShowPreviewOverlay", false,
                display_name: "[Beta] Show Preview Overlay In-Game",
                description: "When ON: the most-recently-rendered map preview " +
                             "is displayed as an overlay panel on the right side " +
                             "of the screen during play. Press F8 in-game to " +
                             "toggle visibility. Requires Render Map Previews " +
                             "to be ON so a preview exists to display.");

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

            // ── Per-preset live-tunable entries ─────────────────────────────
            // Create one MelonPreferences category per non-Custom preset, each
            // with a full mirror of the 13 tunable fields. Defaults seed from
            // the hardcoded Presets dictionary so first-launch behavior is
            // unchanged. User can tune in-game (KC settings UI) or via cfg
            // edits; gen-time reads route through GetEffectiveValues which
            // prefers these entries over the hardcoded table.
            foreach (var kvp in Presets)
            {
                try
                {
                    PresetEntries[kvp.Key] = CreatePresetEntries(kvp.Key.ToString(), kvp.Value);
                }
                catch (System.Exception ex)
                {
                    Log.Warning($"[RR] Failed to create per-preset entries for {kvp.Key}: {ex.Message}. " +
                                $"Will fall back to hardcoded preset values for that mode.");
                }
            }

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
                Log.Msg($"[RR] Rivers Restored 1.3.0 loaded. NumRivers={NumRivers.Value}, " +
                        $"RiversEnabled={RiversEnabled.Value}");

                // Optional: register with Keep Clarity's settings panel if installed.
                KeepClarityIntegration.TryRegisterAll();

                // Spawn the in-game preview overlay GameObject. Persists for
                // the entire process via DontDestroyOnLoad so it survives
                // scene changes (main menu ↔ in-game). OnGUI is gated by
                // ShowPreviewOverlay pref + a non-null LatestPreview.
                try
                {
                    var go = new UnityEngine.GameObject("RR_PreviewOverlay");
                    go.AddComponent<RiversRestored.Patches.PreviewOverlay>();
                    UnityEngine.Object.DontDestroyOnLoad(go);
                }
                catch (System.Exception ex)
                {
                    Log.Warning($"[RR] Failed to spawn preview overlay: {ex.Message}");
                }
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
