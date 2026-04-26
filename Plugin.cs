using System.Linq;
using HarmonyLib;
using MelonLoader;

// ─────────────────────────────────────────────────────────────────────────────
//  Rivers Restored v0.1.0
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

[assembly: MelonInfo(typeof(RiversRestored.RiversRestoredMod), "Rivers Restored", "0.1.0", "SageDragoon")]
[assembly: MelonGame("Crate Entertainment", "Farthest Frontier")]

namespace RiversRestored
{
    public class RiversRestoredMod : MelonMod
    {
        public static RiversRestoredMod Instance { get; private set; } = null!;
        public static MelonLogger.Instance Log => Instance.LoggerInstance;
        public static HarmonyLib.Harmony HarmonyInstance { get; private set; } = null!;

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

        // ─────────────────────────────────────────────────────────────────
        public override void OnInitializeMelon()
        {
            Instance = this;

            // ── Preferences ───────────────────────────────────────────────
            var cat = MelonPreferences.CreateCategory("RiversRestored");

            RiversEnabled = cat.CreateEntry("RiversEnabled", true,
                display_name: "Rivers Enabled",
                description: "Master toggle. When false, this mod is inert " +
                             "and vanilla behavior (no rivers) resumes.");

            NumRivers = cat.CreateEntry("NumRivers", 4,
                display_name: "Number of Rivers",
                description: "Attempts to generate this many rivers per map. " +
                             "Actual count may be lower — the Voronoi validator " +
                             "filters candidates by minPoints and endpoint " +
                             "rules. Vanilla = 2 (but validation rejects all " +
                             "on typical maps, producing 0 rivers).");

            MinPoints = cat.CreateEntry("MinPoints", 15,
                display_name: "Minimum River Control Points",
                description: "Minimum control points per river. THIS IS THE " +
                             "REAL GATE: vanilla = 40, which causes validation " +
                             "to reject every Voronoi candidate on typical " +
                             "seeds. Lowering to 15 allows shorter winding " +
                             "rivers through. Set to 0 to keep vanilla (no rivers).");

            MinWidth = cat.CreateEntry("MinWidth", 0,
                display_name: "Min River Width (cells)",
                description: "Minimum river width in heightmap cells. Vanilla " +
                             "range is 2-8. Set to 0 to keep vanilla default.");

            MaxWidth = cat.CreateEntry("MaxWidth", 0,
                display_name: "Max River Width (cells)",
                description: "Maximum river width in heightmap cells. Vanilla " +
                             "range is 2-8. Set to 0 to keep vanilla default.");

            MinDepth = cat.CreateEntry("MinDepth", -1f,
                display_name: "Min River Depth (world units)",
                description: "Minimum river depth in world units. Vanilla " +
                             "range is 0.25-10. Set to -1 to keep vanilla default.");

            MaxDepth = cat.CreateEntry("MaxDepth", -1f,
                display_name: "Max River Depth (world units)",
                description: "Maximum river depth in world units. Vanilla " +
                             "range is 0.25-10. Set to -1 to keep vanilla default.");

            MarkWaterTypesAsRiverEnd = cat.CreateEntry("MarkWaterTypesAsRiverEnd", true,
                display_name: "Mark WaterTypes as Valid River Endpoints",
                description: "Sets WaterType.riverEndPoint = true on all loaded " +
                             "WaterType ScriptableObjects. Lake + BorderOcean already " +
                             "had this flag in shipped data; this also flips Pond and " +
                             "LakeSmall. Necessary but, by itself, insufficient to " +
                             "produce rivers on inland map types.");

            RiverInnerRadius = cat.CreateEntry("RiverInnerRadius", 2,
                display_name: "River Inner Radius (cells)",
                description: "Heightmap cells within this distance of centerline " +
                             "get slammed to full trench depth. 2 = roughly 5-cell-wide " +
                             "bed (vanilla scale). 3 = wider channel.");

            RiverOuterRadius = cat.CreateEntry("RiverOuterRadius", 5,
                display_name: "River Outer Radius (cells)",
                description: "Heightmap cells between inner and outer radius get a " +
                             "smooth ramp from trench depth back up to terrain. 5 = " +
                             "vanilla-ish bank width. Larger = gentler banks but may " +
                             "trigger excess shoreline auto-paint on reload.");

            RiverJitterAmplitude = cat.CreateEntry("RiverJitterAmplitude", 1.5f,
                display_name: "River Jitter Amplitude (world units)",
                description: "Perpendicular wiggle amplitude added between Voronoi " +
                             "control points. 0 = straight Bresenham; 1.5 = subtle " +
                             "meander; 5+ = strong snake.");

            RiverJitterFrequency = cat.CreateEntry("RiverJitterFrequency", 0.6f,
                display_name: "River Jitter Frequency",
                description: "Wave count per Voronoi segment. 0.6 ≈ one and a bit " +
                             "oscillations per segment.");

            RiverSmoothPasses = cat.CreateEntry("RiverSmoothPasses", 2,
                display_name: "River Bank Smooth Passes",
                description: "Iterative 3×3 box-blur passes applied after carving. " +
                             "Pangu's 'Shore Blend' equivalent. 0 = no smoothing, " +
                             "2 = good default, 4 = gentler, 8 = very gentle. Each pass " +
                             "adds ~10s of map-gen time on large maps.");

            RiverTrenchDepth = cat.CreateEntry("RiverTrenchDepth", 2.0f,
                display_name: "River Trench Depth (m below water)",
                description: "How far below the water surface to carve the riverbed. " +
                             "WaterPath's transparency shader uses (pos.y - terrain) / " +
                             "pos.y as its alpha input — at outlets where pos.y is " +
                             "clamped to waterHeight (~3.15m), shallow trenches give " +
                             "near-zero alpha and the river renders patchy. " +
                             "1.5m is the visibility floor; 2.0m default has comfortable " +
                             "headroom; 3m+ produces dramatic canyons. Crate's vanilla " +
                             "PaintRiverFunc carves 2-10m, so 2m matches their calibration.");

            ForceCoastlineTerrain = cat.CreateEntry("ForceCoastlineTerrain", false,
                display_name: "[Diag] Force Coastline Terrain Type",
                description: "Forces TerrainGeneratorController.terrainType = Coastline " +
                             "before generation. Bypasses the biome-theme UI restriction " +
                             "(which seems to lock to Default for shipped themes). Use " +
                             "this to test whether rivers spawn on Coastline maps but " +
                             "not Default — would prove elevation/ocean-gradient is the " +
                             "real gate.");

            RiverRegisterAsWaterArea = cat.CreateEntry("RiverRegisterAsWaterArea", true,
                display_name: "Register Rivers as WaterAreas (hybrid mode)",
                description: "When enabled, each river is also added to FF's " +
                             "_generationData.waterAreas list (the same list lakes/oceans " +
                             "use). Pros: water plane covers the river polygon and is " +
                             "saved/restored automatically by FF; FishingManager spawns " +
                             "fishing nodes on rivers; no gen-vs-reload water mismatch. " +
                             "The WaterPath ribbon still provides the flow animation on " +
                             "top. Disable to fall back to ribbon-only rivers.");

            RiverBlobRadius = cat.CreateEntry("RiverBlobRadius", 3,
                display_name: "[v0.2] River Blob Stamp Radius (cells)",
                description: "Disc-stamp radius (heightmap cells) used to build the " +
                             "river WaterArea polygon. Many small stamps along the cp " +
                             "path get merged transitively (Pangu pattern) into the " +
                             "final river polygon. Default 3 matches RiverInnerRadius " +
                             "so the polygon fills the carved trench bottom exactly. " +
                             "Stamps are squarish (~7×7 bbox) — the shape Pangu's " +
                             "manually-painted thin rivers use, which is proven to " +
                             "survive save/reload.");

            RiverBlobStride = cat.CreateEntry("RiverBlobStride", 3,
                display_name: "[v0.2] River Blob Stamp Stride (cells)",
                description: "Spacing between disc stamps along the interpolated cp " +
                             "path, in heightmap cells. Heavy overlap at default 3 " +
                             "gives a smooth merged outline with no gaps even on " +
                             "tight curves. Raise to 5+ for fewer stamps (faster " +
                             "gen) at the risk of gaps where the path bends sharply.");

            RiverFishingAreaMultiplier = cat.CreateEntry("RiverFishingAreaMultiplier", 4,
                display_name: "River Fishing Area Multiplier",
                description: "Multiplies the number of FishingArea entries that " +
                             "appear in a Fishing Shack / Dock's local list when " +
                             "those entries reference one of OUR river FishAreas. " +
                             "FF's productivity calculation is area-count-based, " +
                             "so 1 area = -50% productivity penalty. 4× lifts a " +
                             "single-river shack to ~12 areas → above the bonus " +
                             "threshold → 100% productivity. Vanilla lakes/oceans " +
                             "are untouched (we tag river FishAreas by waterType " +
                             "reference at construction time). 1 = disabled.");

            // ── Harmony ───────────────────────────────────────────────────
            HarmonyInstance = new HarmonyLib.Harmony("SageDragoon.RiversRestored");

            try
            {
                Patches.RiverSettingsPatch.Apply(HarmonyInstance);
                Patches.RiverPersistence.Apply(HarmonyInstance);
                Patches.FishingShackPatch.Apply(HarmonyInstance);
                Log.Msg($"[RR] Rivers Restored 0.1.0 loaded. NumRivers={NumRivers.Value}, " +
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
            _dumpedSceneOnce = false;
            _framesWithoutTerrain = 0;
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
        private bool _dumpedSceneOnce = false;
        private int _framesWithoutTerrain = 0;
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

        /// <summary>
        /// Dump every GameObject in the scene with "terrain" or "ground" or
        /// "map" in its name, plus its component types. Helps identify FF's
        /// custom terrain rendering system.
        /// </summary>
        private static void DumpTerrainCandidates()
        {
            try
            {
                Log.Msg("[RR][SceneDump] No UnityEngine.Terrain found — searching for custom terrain components…");
                var allGOs = UnityEngine.Object.FindObjectsOfType<UnityEngine.GameObject>();
                int matched = 0;
                foreach (var go in allGOs)
                {
                    if (go == null) continue;
                    string n = go.name?.ToLowerInvariant() ?? "";
                    if (!n.Contains("terrain") && !n.Contains("ground") && !n.Contains("map") &&
                        !n.Contains("world") && !n.Contains("mesh"))
                        continue;
                    matched++;
                    var components = go.GetComponents<UnityEngine.Component>();
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"[RR][SceneDump]   {go.name} : ");
                    foreach (var c in components)
                    {
                        if (c == null) continue;
                        sb.Append(c.GetType().Name).Append(" ");
                    }
                    Log.Msg(sb.ToString());
                }
                Log.Msg($"[RR][SceneDump] {matched} candidate GameObject(s) examined.");

                // Also dump any types in TerrainGen namespace that look like renderers
                var tgAsm = typeof(TerrainGen.TerrainGenerator).Assembly;
                var renderTypes = tgAsm.GetTypes()
                    .Where(t => t.Namespace == "TerrainGen" &&
                                (t.Name.Contains("Render") || t.Name.Contains("Mesh") ||
                                 t.Name.Contains("Heightmap") || t.Name.Contains("Map")))
                    .ToArray();
                Log.Msg($"[RR][SceneDump] {renderTypes.Length} candidate types in TerrainGen namespace:");
                foreach (var t in renderTypes)
                    Log.Msg($"[RR][SceneDump]   {t.FullName} ({(typeof(UnityEngine.Component).IsAssignableFrom(t) ? "Component" : "plain")})");
            }
            catch (System.Exception ex)
            {
                Log.Error($"[RR][SceneDump] failed: {ex}");
            }
        }
    }
}
