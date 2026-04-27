using System.Linq;
using HarmonyLib;
using MelonLoader;

// ─────────────────────────────────────────────────────────────────────────────
//  Rivers Restored v1.0.0
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

[assembly: MelonInfo(typeof(RiversRestored.RiversRestoredMod), "Rivers Restored", "1.0.0", "SageDragoon")]
[assembly: MelonGame("Crate Entertainment", "Farthest Frontier")]

namespace RiversRestored
{
    public class RiversRestoredMod : MelonMod
    {
        public static RiversRestoredMod Instance { get; private set; } = null!;
        public static MelonLogger.Instance Log => Instance.LoggerInstance;
        public static new HarmonyLib.Harmony HarmonyInstance { get; private set; } = null!;

        // ── Master toggle ───────────────────────────────────────────────────
        /// <summary>Kill-switch. When false, the mod does nothing — vanilla
        /// behavior resumes.</summary>
        public static MelonPreferences_Entry<bool> RiversEnabled { get; private set; } = null!;

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

            RiverOuterRadius = cat.CreateEntry("RiverOuterRadius", 8,
                display_name: "River Bank Width (slope to ground)",
                description: "How far out the sloped banks extend before reaching " +
                             "natural ground level, measured in cells from centerline. " +
                             "The cells between Channel Width and Bank Width get a " +
                             "smooth ramp from riverbed up to terrain. " +
                             "(Bank Width − Channel Width) is the slope distance: " +
                             "1 cell = sharp drop-off, 2-3 cells = natural slope (default), " +
                             "5+ = very gradual sloping banks. Must be ≥ Channel Width.");

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

            RiverSmoothPasses = cat.CreateEntry("RiverSmoothPasses", 2,
                display_name: "Bank Smoothness",
                description: "How many smoothing passes are applied to the riverbanks " +
                             "after carving. Smooths out the staircase look from the " +
                             "raw cell-by-cell carve. " +
                             "0 = no smoothing (rough/blocky banks), " +
                             "2 = good default, " +
                             "4 = gentler, " +
                             "8 = very gentle. " +
                             "Each pass adds a couple seconds to map generation on large maps.");

            RiverTrenchDepth = cat.CreateEntry("RiverTrenchDepth", 2.0f,
                display_name: "River Depth (metres below water)",
                description: "How deep below the water surface the riverbed is dug, " +
                             "in metres. Affects how visibly 'wet' the river looks — " +
                             "shallow rivers can render patchy or transparent. " +
                             "1.5 = visibility floor (anything shallower may look thin), " +
                             "2.0 = comfortable default, " +
                             "3-4 = dramatic deeper rivers, " +
                             "5+ = canyon-like. " +
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

            // ── Harmony ───────────────────────────────────────────────────
            HarmonyInstance = new HarmonyLib.Harmony("SageDragoon.RiversRestored");

            try
            {
                Patches.RiverSettingsPatch.Apply(HarmonyInstance);
                Patches.RiverPersistence.Apply(HarmonyInstance);
                Patches.FishingShackPatch.Apply(HarmonyInstance);
                Log.Msg($"[RR] Rivers Restored 1.0.0 loaded. NumRivers={NumRivers.Value}, " +
                        $"RiversEnabled={RiversEnabled.Value}");
            }
            catch (System.Exception ex)
            {
                Log.Error($"[RR] Failed to apply patches: {ex}");
            }
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
