# Water / River / Lake Generation Levers — Complete Reference

This document is the canonical map of every knob in Farthest Frontier's terrain pipeline that affects water generation, whether or not Rivers Restored currently surfaces it as a `MelonPreferences` setting. It exists to support tuning decisions and to make it easy to identify candidates for new mod settings.

**Sections:**

1. **Currently exposed by Rivers Restored** — every `MelonPreferences` entry under `[RiversRestored]`. Verified, documented, tunable today.
2. **Vanilla fields RR reads but doesn't expose** — fields RR consumes internally but doesn't expose to the player.
3. **Vanilla fields RR doesn't read at all** — fields a runtime reflection dump revealed on the relevant settings structs, untouched by RR.
4. **`WaterType` ScriptableObject fields** — per-type properties (color, transparency, river/lake/pond classification) that govern rendering.

Sections 2–4 are populated from a one-shot reflection dump emitted at gen entry (look for `[RR][WaterDump]` lines in `Latest.log`). If those sections appear to say `(pending dump output)` below, the dump hasn't been ingested yet — run a fresh map gen and the doc will be updated.

---

## Section 1: Currently exposed by Rivers Restored (MelonPreferences)

All entries live under `[RiversRestored]` in `UserData/MelonPreferences.cfg`. Defaults shown match `Plugin.cs` source; tuning hints are taken from in-mod descriptions.

### Master switch

#### `RiversEnabled` — bool, default `true`
- **Effect:** Master ON/OFF for the whole mod. When false, the entire pipeline becomes inert and FF behaves like vanilla (no rivers — vanilla's filter rejects every candidate seed).
- **Tuning:** Leave on. Set false only to A/B test "what would this map look like vanilla?"

### Preset & UI

#### `RiverPreset` — enum (`IdyllicValley` / `LowlandLakes` / `AridHighlands` / `Plains` / `AlpineValleys` / `Custom`), default `IdyllicValley`
- **Effect:** Pre-tuned bundle of river settings. When set to anything except `Custom`, the preset's values override every individual slider — granular settings are silently suppressed.
- **Tuning hints:**
  - `IdyllicValley` — balanced rolling terrain (general-purpose)
  - `LowlandLakes` — flat with ponds; short shallow streams
  - `AridHighlands` — high dry terrain; fewer narrower deeper rivers
  - `Plains` — open semi-flat; moderate width and count
  - `AlpineValleys` — mountains/valleys; long deep alpine drainages
  - `Custom` — granular sliders take effect
- **Interacts with:** every river-shaping setting (overrides them when not `Custom`).

#### `GranularSettings` — bool, default `false`
- **Effect:** When true, individual river-shaping sliders become visible in the in-game settings UI (Keep Clarity panel etc.). Doesn't change behavior on its own; values only apply when `RiverPreset = Custom`.
- **Warning:** Custom values can produce visibly broken rivers if the trench/water/bank interaction is mis-tuned (visible trench bottom, water spillover, harsh banks). Recommended only for users who understand the geometry.

### Heightmap pre-tilt

#### `RiverFlowBias` — enum (`None` / `NE_to_SW` / `NW_to_SE` / `SW_to_NE` / `SE_to_NW` / `N_to_S` / `S_to_N` / `E_to_W` / `W_to_E`), default `None`
- **Effect:** Tilts the heightmap before river-path generation so rivers statistically flow from a chosen high corner/edge to a chosen low one. Lakes also nudge toward the low end (water pools where it's lowest — physically realistic).
- **Visible result:** ~70-90% of rivers follow the chosen direction depending on `RiverFlowBiasStrength`.
- **Affects new map gens only.** Existing saves are unaffected.
- **Interacts with:** `RiverFlowBiasStrength` (no effect when `None`).

#### `RiverFlowBiasStrength` — float (0.0–1.0), default `0.4`
- **Effect:** How strongly the heightmap is tilted. Higher = more rivers follow the chosen direction.
- **Tuning:**
  - `0.3` — subtle (some rivers still go their own way)
  - `0.4` — balanced default; reliable for most maps
  - `0.5` — strong (most rivers follow the bias)
  - `0.7+` — visible map tilt; can look unnatural in flat biomes
- **Interacts with:** `RiverFlowBias` (no effect when `None`).

### Visuals

#### `EnableRibbonAnimation` — bool, default `true`
- **Effect:** When on, rivers display the animated flowing-water ribbon mesh on top of the static water surface (visible swirling flow effect). When off, rivers render as a static green water surface like lakes — no flow animation.
- **Performance:** The ribbon's per-frame UV scrolling and per-cp subdivisions can be expensive on river-heavy maps. Turn off if you see CPU/GPU stutter.
- **Doesn't affect:** fishing, resource avoidance, save/reload, the carved riverbed — all still work when off.

#### `RiverPreferLakeWaterType` — bool, default `true`
- **Effect:** When on, rivers use a Lake-type water material (clear blue, like normal lakes). When off, rivers use whatever water type FF assigns first (often Pond — green/murky).
- **Visible result:** Pond water is muddy-green with lower transparency. Lake water is clear blue. Leave on unless you specifically want green murky rivers.
- **Note (observed limitation):** FF appears to apply additional render-time classification based on water-area dimensions; small/narrow river polygons may still render as pond regardless of this setting. This is an open issue.

### River count and length

#### `NumRivers` — int, default `4`
- **Effect:** How many rivers the mod will *try* to generate on each new map. The actual count may be lower if the seed/heightmap can't fit them all.
- **Tuning:**
  - `1-2` — sparse
  - `4` — balanced default
  - `6+` — water-rich
- **Vanilla:** `2`, but vanilla maps typically end up with 0 rivers because the rejection filter (`MinPoints`) is too strict.
- **Interacts with:** `MinPoints`, `MarkWaterTypesAsRiverEnd`, terrain/biome.

#### `MinPoints` — int, default `15`
- **Effect:** Minimum length each river must reach to be accepted, in internal waypoints (each waypoint is roughly 1-3 cells of river path).
- **Tuning:**
  - `0` — vanilla off-equivalent (accept any length)
  - `15` — default; lets short winding rivers through
  - `40` — vanilla; too strict, rejects almost everything
- **Vanilla:** `40`. This is the single biggest reason vanilla maps almost never have rivers.

### River geometry (legacy / deprecated)

#### `MinWidth` — int, default `0`
- **Effect:** Minimum width of the visible flowing-water ribbon. `0` = use vanilla (which picks 2–8 cells per river). Set 4+ to force every river to be at least medium width.

#### `MaxWidth` — int, default `0`
- **Effect:** Maximum width of the visible flowing-water ribbon. `0` = use vanilla. Set 6+ to allow some grand rivers in the mix.

#### `MinDepth` — float, default `-1.0`
- **Effect:** Internal depth setting from FF's original river system. Has limited visual effect since the actual carve depth is controlled by `RiverTrenchDepth`. Leave at `-1` to use vanilla default.
- **Status:** Legacy; mostly unused.

#### `MaxDepth` — float, default `-1.0`
- **Effect:** Companion to `MinDepth`. Visible depth is governed by `RiverTrenchDepth`, not this. Leave at `-1`.
- **Status:** Legacy; mostly unused.

### River endpoint selection

#### `MarkWaterTypesAsRiverEnd` — bool, default `true`
- **Effect:** When on, rivers may end in any water body — ponds, small lakes, large lakes, or the border ocean. When off (vanilla), only Lakes and the Border Ocean count as valid endpoints, which is why vanilla rivers often fail to spawn at all.
- **Tuning:** Leave on for normal use. The implementation flips `WaterType.riverEndPoint = true` on every loaded `WaterType` ScriptableObject; the change persists for the rest of the session.

### Carve geometry (channel + banks)

#### `RiverInnerRadius` — int, default `6`
- **Effect:** How wide the deep carved channel is, in cells from centerline. The riverbed is dug down to full depth this far on each side. Each cell is ≈ 2.5 m.
- **Tuning:**
  - `3` — narrow stream (~7 cells across)
  - `6` — wide visible river (default, ~13 cells across)
  - `8+` — grand river
- **Interacts with:** `RiverOuterRadius` (must be ≥ inner). Bump outer with this so banks don't get too steep.

#### `RiverOuterRadius` — int, default `10`
- **Effect:** How far out the sloped banks extend before reaching natural ground level. Cells between inner and outer get a smooth ramp from riverbed up to terrain.
- **Slope distance** = `RiverOuterRadius − RiverInnerRadius`:
  - `1 cell` — sharp drop-off
  - `4 cells` — lake-like blend (default)
  - `6+ cells` — very gradual sloping banks
- **Constraint:** Must be ≥ `RiverInnerRadius`.

#### `RiverJitterAmplitude` — float, default `1.5`
- **Effect:** How much rivers wiggle/snake between waypoints, in metres of perpendicular offset.
- **Tuning:**
  - `0` — perfectly straight lines (boring)
  - `1.5` — subtle natural curves (default)
  - `5+` — strong snaking; can self-intersect on tight bends
- **Interacts with:** `RiverJitterFrequency`.

#### `RiverJitterFrequency` — float, default `0.6`
- **Effect:** How many curves fit between each pair of main waypoints.
- **Tuning:**
  - `0.6` — one and a bit curves per segment (default, looks natural)
  - higher — more zigzaggy
  - lower — sweeping arcs
- **No effect when** `RiverJitterAmplitude = 0`.

#### `RiverSmoothPasses` — int, default `6`
- **Effect:** How many smoothing passes are applied to the riverbanks after carving. Smooths out the staircase look from the cell-by-cell carve.
- **Tuning:**
  - `0` — no smoothing (rough/blocky banks)
  - `2` — mild
  - `6` — lake-like softness (default)
  - `8+` — very gentle
- **Performance:** Each pass adds a couple seconds to gen on large maps.

#### `RiverTrenchDepth` — float (metres), default `1.8`
- **Effect:** How deep below the water surface the riverbed is dug.
- **Tuning:**
  - `1.5` — visibility floor; anything shallower may look thin
  - `1.8` — lake-like (default)
  - `2.5` — noticeably deeper
  - `4+` — dramatic canyon rivers
- **Calibration:** Vanilla FF rivers carve 2-10 m so this matches their calibration.

### Water-area registration (logistics + fishing)

#### `RiverRegisterAsWaterArea` — bool, default `true`
- **Effect:** When on, rivers behave like proper bodies of water — trees/rocks/animals don't spawn on river cells, villagers can fish, the river has a flat water surface that saves/reloads. When off, rivers are visual ribbons only.
- **Tuning:** Leave on for normal play.

#### `RiverBlobRadius` — int, default `6`
- **Effect:** Width of the visible water surface (the brown bed under the flowing-water animation that you can fish in), in cells from centerline.
- **Interacts with:** `RiverInnerRadius`. Default 6 matches it so the water fills the carved riverbed exactly.
- **Tuning:**
  - higher than channel width → water spills out onto bank slope (river overflowing)
  - lower than channel width → clean carved-trench look with rocky shores

#### `RiverBlobStride` — int, default `3`
- **Effect:** Internal setting controlling how densely the water-surface polygon is built along the river path.
- **Tuning:**
  - `3` — default (heavy overlap, smooth even on sharp curves)
  - `5+` — faster generation; may leave gaps on tight bends
- **Don't change** unless map-gen feels slow.

#### `RiverFishingAreaMultiplier` — int, default `4`
- **Effect:** Boosts Fishing Shack/Dock productivity when placed next to a river (lakes/ocean unaffected — those use vanilla values). FF normally penalises shacks with few fishing zones, and a single river only counts as one zone — leaving river fishing weak.
- **Tuning:**
  - `1` — no boost (vanilla; river fishing feels weak)
  - `4` — good balance (default, ~100% productivity for a river-side shack)
  - `8+` — lush fishing economy

### Diagnostics

#### `ForceCoastlineTerrain` — bool, default `false`
- **Effect:** [Developer-only] Forces every map to the Coastline biome regardless of UI selection.
- **Tuning:** Leave off. Was used during dev to test rivers on different biomes.

#### `VerboseDiagnostics` — bool, default `false`
- **Effect:** Verbose diagnostic logging — per-WaterArea state at save time, per-stage waterArea counts during gen, one-shot dumps of FF's internal terrain/biome/layer structures.
- **Tuning:** On only when filing a bug report. Log gets noisy fast.

---

## Section 2: Vanilla fields RR reads but doesn't expose

These are fields RR consumes internally (mostly to compute water height, resolve a river WaterType, or read terrain dimensions) but doesn't expose to the player. Tuning effect is well-understood for these because RR uses them.

#### `TerrainGenerator.waterSettings.height` — Single, default `~0.222`
- **Effect:** Water surface level expressed as a 0–1 fraction of the map's vertical extent. Multiplied by `mapSettings.height × baseSettings.scaling × noiseScaling` to compute the actual world-Y of the water plane.
- **Why RR reads it:** `RiverPersistence.ComputeWaterY` uses it to know where the river water surface should sit on reload.
- **Tuning hypothesis (uncertain — modifying it would shift the global water level):** raising would flood more terrain (more lakes/ponds visible); lowering would expose more land. Not tested as a knob.

#### `TerrainGenerator.waterSettings.lakeTypes` — List\<WaterType\>, count = 3 (Pond, LakeSmall, LakeLarge)
- **Effect:** The set of WaterType SOs FF picks from when classifying a lake polygon by size.
- **Why RR reads it:** `ResolveRiverWaterType` walks this list to find a "lake-style" WaterType to assign rivers when `RiverPreferLakeWaterType = true`.
- **Tuning hypothesis:** the list determines which water visuals are available. Replacing/adding entries would let the mod offer custom water styles.

#### `TerrainGenerator.baseSettings.scaling` — Single, default `0.2`
- **Effect:** Multiplier applied to the heightmap noise after initial Perlin sampling. Squashes/stretches the entire vertical range.
- **Why RR reads it:** part of the `ComputeWaterY` formula.
- **Tuning hypothesis:** higher = more vertical relief (sharper hills, deeper valleys); lower = flatter map. Affects ALL terrain, not just water.

#### `TerrainGenerator.mapSettings.height` — Int32, default `62` (Medium map)
- **Effect:** Maximum vertical extent of the map in world units.
- **Why RR reads it:** part of the `ComputeWaterY` formula. Also used by carve depth math.
- **Tuning hypothesis:** larger = taller mountains and deeper valleys are physically possible. FF picks values per map size: Small/Medium/Large/Huge.

#### `WaterType.riverEndPoint` — Boolean (per type)
- **Effect:** When true, this WaterType counts as a valid river termination point. Vanilla ships every WaterType with this `false`, which is why vanilla rivers fail to spawn — Voronoi finds candidate paths but every endpoint is rejected.
- **Why RR reads/writes it:** `MarkAllWaterTypesAsRiverEnd()` flips this on every loaded WaterType when `MarkWaterTypesAsRiverEnd = true`. RR partially exposes via the `MarkWaterTypesAsRiverEnd` master toggle, but doesn't let the player flip individual WaterTypes (e.g., "rivers can end in lakes but not ponds").

---

## Section 3: Vanilla fields RR doesn't read at all

These are the most interesting candidates for new MelonPreferences entries — fields vanilla FF exposes on the gen-pipeline structs but RR has never touched. Effects are best-guess from name + type and **need in-game testing to confirm** unless cited.

### From `waterSettings` (TerrainGenerator+WaterSettings)

#### `seafloorHeight` — Single, default `0.08`
- **Effect (probable):** Ocean floor depth as a 0–1 fraction of map height. Lower = deeper ocean, higher = shallower coastal water.
- **Tuning hypothesis:** could control how dramatic coastal cliffs feel.

#### `oceanType` — WaterType ref (`WaterType_Ocean`)
- **Effect:** The single WaterType used to render ocean borders (separate from the lakeTypes list).
- **Tuning hypothesis:** swap to a different WaterType for stylized oceans. Low priority.

#### `minOceanMapEdgePoints` — UInt32, default `200`
- **Effect (probable):** Minimum number of map-edge points required for a water body to be classified as "ocean" rather than a coast-touching lake. Higher = harder for partial coasts to qualify as ocean.
- **Tuning hypothesis:** lowering might let small coastal indents become ocean-styled water; raising forces only fully-edge-bordering water to be ocean.

#### `waterDepth` — AnimationCurve, 6 keys
- **Effect (probable):** Depth profile across the radius of a water body. Determines how the floor curves from shore to center.
- **Tuning hypothesis:** flatter curve = bowl-like lakes; steeper curve = funnel-shaped lakes with deep centers.

#### `useDepthCurve` — Boolean, default `true`
- **Effect:** Toggle for whether `waterDepth` curve is applied. When false, lakes likely have flat floors at a fixed depth.
- **Tuning hypothesis:** turn off for retro flat-bottom lakes.

#### `depthMap` — Texture2D, default empty
- **Effect (probable):** Optional grayscale texture overriding/blending depth across the map.
- **Tuning hypothesis:** custom-authored maps could ship with hand-painted lake depths. Mod would need a way to load the texture, which is complex.

#### `depthMapInfluence` — Single, default `0.35`
- **Effect:** How strongly the `depthMap` (when present) overrides the `waterDepth` curve.
- **Tuning hypothesis:** mostly relevant if `depthMap` is non-empty.

### From `riverSettings` (TerrainGenerator+RiverSettings)

These are vanilla river knobs RR doesn't surface. Many likely affect Voronoi pathing or carve geometry in subtle ways.

#### `aggressiveness` — Int32, default `50000`
- **Effect (probable):** Voronoi pathfinding aggressiveness — how far the path will deviate to find valid drainage. The very large default suggests it's a "search budget" cap rather than a cost.
- **Tuning hypothesis:** higher might find more valid paths on hostile terrain (more rivers); lower might fail more often. **Worth testing** as a possible mitigation for the "wanted 12, got 4" issue.

#### `depthVarianceFreq` — Single, default `0.001`
- **Effect (probable):** Frequency of depth-variance noise along a river's length. Low = depth varies slowly along river; high = rapidly.

#### `crossingDepth` — Single, default `0.4`
- **Effect (probable):** Depth of river-crossing points (where roads/paths intersect rivers). Likely a normalized 0–1 fraction of full river depth.

#### `crossingTextureWeight` — Single, default `1`
- **Effect (probable):** How much the crossing splat texture blends in at crossing points. 0 = invisible, 1 = full.

#### `crossingDetailDensity` — Single, default `0`
- **Effect (probable):** Density of detail props (rocks/reeds) at river crossings. Default 0 = none.

#### `variance` — Single, default `5`
#### `offset` — Single, default `0.8`
#### `weightBalance` — Single, default `0.185`
- **Effect (probable, all three):** Voronoi pathfinding cost-function parameters. Tuning affects the "natural-looking-ness" of paths. Hard to predict effect without empirical tweaking.

#### `pathMacroSmoothing` — Int32, default `2`
#### `pathMicroSmoothing` — Int32, default `2`
- **Effect:** Smoothing pass counts at two scales — macro for overall path shape, micro for cell-by-cell jitter. Higher = smoother rivers but less detail.

#### `widthVarianceAmp` — Single, default `1.5`
#### `widthVarianceFreq` — Single, default `0.001`
- **Effect:** River width variation along its length. Higher amp = bigger swings between narrow and wide; freq controls how rapidly width changes.

#### `spreadCurve` — AnimationCurve, 3 keys
- **Effect (probable):** Width distribution from river center outward. Sharp curve = abrupt edge; gentle = gradual taper.

#### `minSmoothing` / `maxSmoothing` — Int32, defaults `6` / `30`
#### `smoothVarianceAmp` — Single, default `3`
#### `smoothVarianceFreq` — Single, default `0.15`
- **Effect:** Smoothing-pass count is randomized along the river between min and max, modulated by the variance amp/freq. Some river segments get gently smoothed banks, others get rough banks. Visual variety control.

#### `valleyCurve` — AnimationCurve, 6 keys
- **Effect (probable):** Cross-section profile of the river valley (center → edge). Determines whether the river sits in a U-shaped or V-shaped valley.

#### `smoothingProfile` — AnimationCurve, 5 keys
- **Effect (probable):** Smoothing intensity along the river's length (start → end). Some rivers might smooth more at headwaters than mouths.

#### `textureAdjustment` — Int32, default `0`
#### `detailAdjustment` — Int32, default `5`
#### `treeAdjustment` — Int32, default `1`
- **Effect (probable):** Per-cell adjustment offsets applied to the texture/detail/tree layers in the river region. Probably tuning for how cleanly trees/details get cleared from the riverbed.

#### `shorelineSampleRadius` — Int32, default `3`
- **Effect (probable):** Radius (in cells) sampled around each river cell for shoreline classification. Larger = wider apparent shorelines.

#### `shorelineHeight` — Single, default `0.03`
- **Effect (probable):** Elevation offset (0–1 fraction) applied to shoreline cells to keep them above water level. Prevents shore from being flooded.

#### `shorelineAdjustment` — Single, default `-0.001`
- **Effect (probable):** Fine height adjustment applied to shoreline cells to soften the water-edge transition.

#### `underwaterTexture` / `underwaterNormal` — Texture2D
#### `underwaterWeight` — Single, default `1`
#### `shorelineTexture` / `shorelineNormal` — Texture2D
#### `shorelineWeight` — Single, default `1`
- **Effect:** Splat textures for the riverbed (underwater) and shoreline. RR's carve uses these for splat painting. The textures are baked vanilla assets (`DirtUnderwaterRiver01_DIF` etc.); the weights control opacity in the splat blend.

#### `detailDensity` — AnimationCurve, 5 keys
- **Effect (probable):** Detail-prop density across the river cross-section. Likely 0 in the channel and rising toward shore.

#### `transparencyCurve` — AnimationCurve, 2 keys
- **Effect (probable):** Water transparency across river width. Center transparent → edges opaque, or similar.

#### `extinctionCurve` — AnimationCurve, 4 keys
- **Effect (probable):** Underwater-light extinction with depth. Controls how quickly the riverbed fades to black at depth.

#### `detailCollection` — DetailCollection ref (`DetailCollection_Shoreline_River`)
- **Effect:** The set of prop GameObjects spawnable along river shorelines (rocks, reeds, etc.).
- **Tuning hypothesis:** swap collection to bias prop variety.

#### `geometrySimplification` — Single, default `100`
- **Effect (probable):** Mesh-simplification factor for the river ribbon. Higher = simpler mesh = better perf but coarser shape.

### From `baseSettings` (TerrainGenerator+BaseSettings)

These are heightmap-noise parameters. Affect ALL terrain, not just water.

#### `seed` — Int32, default `1964133` (read from current map)
- **Effect:** Master seed. Same seed = same heightmap (assuming all other settings match).

#### `octaves` — Int32, default `5`
- **Effect:** Number of Perlin octaves layered to make the heightmap. More = more detail at multiple scales.

#### `wavelength` — Int32, default `606`
- **Effect:** Base Perlin wavelength. Larger = broader hills; smaller = tighter terrain.

#### `lacunarity` — Single, default `0.61`
- **Effect:** Frequency multiplier per octave. Standard Perlin parameter.

#### `persistence` — Single, default `0.345`
- **Effect:** Amplitude multiplier per octave. Lower = smoother detail; higher = rougher.

#### `levels` — Int32, default `0`
- **Effect (probable):** Quantization step count. 0 = continuous heightmap; >0 = stepped/terraced terrain.
- **Tuning hypothesis:** could be a fun terrain-mod knob (Minecraft-style stepped landscapes).

#### `enabled` — Boolean, default `true`
- **Effect:** Master toggle for the base layer.

### From `mapSettings` (TerrainGenerator+MapSettings)

Map dimensions. Most are picked by the user via the New Game UI's map size selector.

- **`size` Size enum (Small/Medium/Large/Huge), `width` Int32 (1920), `depth` Int32 (1920), `height` Int32 (62)** — overall map dimensions
- **`heightmapResolution` Int32 (384), `textureResolution` Int32 (384), `treeResolution` Int32 (384)** — sampling grids
- **`textureSmoothing` Int32 (1)** — splat smoothing pass count
- **`treeSpacing` Int32 (5)** — minimum cells between trees
- **`detailResolution` Int32 (2304)** — detail layer cells
- **`edgeExclusion` Single (0)** — exclusion zone at map edges (0 = full edge usable)

These are mostly UI-driven. Modding them risks breaking saves built on different dimensions.

### From `biomeSettings` (TerrainGenerator+BiomeSettings)

Voronoi biome-zone parameters.

#### `seed` — Int32, default `0`
- **Effect:** Biome-zone seed (separate from the master heightmap seed).

#### `relaxation` — UInt32, default `5`
- **Effect (probable):** Lloyd's relaxation pass count for biome Voronoi cells. More = more even biome zones.

#### `complexity` — UInt32, default `270`
- **Effect (probable):** Number of biome zones. More = smaller/more varied biomes.

#### `blendRadius` — Single, default `64`
#### `blendFrequency` — Single, default `64`
- **Effect (probable):** Biome boundary blend region size and noise frequency. Larger = softer biome transitions.

---

## Section 4: WaterType ScriptableObject fields

Each entry in `waterSettings.lakeTypes` (and the standalone `oceanType`) is a `WaterType` SO with the following fields. Vanilla ships three lake types: **Pond**, **LakeSmall**, **LakeLarge**. Differences between them are summarized at the end of this section.

#### `shorelinePoints` — Int32 (Pond=0, LakeSmall=150, LakeLarge=300)
- **Effect (probable):** Number of shoreline sample points generated for this water-type's polygon. Higher = smoother more detailed shoreline. Pond=0 is unusual — possibly the "no shore detailing" path.

#### `waterMaterial` — Material (`Terrain2Water_Pond` / `Terrain2Water_Lake`)
- **Effect:** The Unity material used to render the water surface. Determines color, transparency, foam, ripple effects.
- **Pond uses a different material** (`Terrain2Water Pond` — note the space) than LakeSmall/Large (both `Terrain2Water_Lake`). This is why ponds look green-murky and lakes look blue-clear.

#### `foamMaterial` — Material (`Terrain2Surf_Pond` / `Terrain2Surf_Lake`)
- **Effect:** Material for the shoreline foam/surf line.

#### `shorelineTexture` / `shorelineNormal` — Texture2D
- **Effect:** Splat textures applied at the shoreline (the muddy/grassy band). All three vanilla types share `GrassGreenMuddyPlants01A`.

#### `shorelineHeight` — Single (Pond=0.03, LakeSmall=0.02, LakeLarge=0.02)
- **Effect (probable):** Height offset (0–1 fraction) for shoreline cells, raising them slightly above water level so the shore-water interface looks crisp.

#### `shorelineSampleRadius` — Int32 (8 for all)
- **Effect (probable):** Radius (cells) sampled around each shoreline point for blending into terrain.

#### `underwaterTexture` / `underwaterNormal` — Texture2D
- **Effect:** Splat textures for the lakebed below water. Pond uses `DirtUnderwaterPond01`; LakeSmall/Large use `DirtUnderwaterLake01`.

#### `detailCollection` — DetailCollection ref (all three: `DetailCollection_Shoreline_Lake`)
- **Effect:** Set of prop GameObjects spawnable along this water-type's shoreline.

#### `blendShorelineDetails` — Boolean (all three: true)
- **Effect (probable):** Toggle for blending shoreline detail props with terrain detail layer. Off would produce harder visual edges.

#### `underwaterDensity` — AnimationCurve (Pond=3 keys, LakeSmall=3 keys, LakeLarge=4 keys)
#### `shorelineDensity` — AnimationCurve (Pond=4 keys, LakeSmall=6 keys, LakeLarge=6 keys)
- **Effect:** Density profile of detail props across underwater-region and shoreline radius. The Large lake having more keys suggests finer-grained density falloff.

#### `riverEndPoint` — Boolean (default false in vanilla; RR sets all three true when `MarkWaterTypesAsRiverEnd = true`)
- **Effect:** Whether rivers may terminate in this water type.

#### `shorelineObjects` — List\<GameObjectEntry\> (Pond=11, LakeSmall=13, LakeLarge=14)
#### `shorelineObjectDensity` — AnimationCurve
- **Effect:** Specific GameObject prefabs (rocks, reeds, etc.) spawned along the shoreline, with density distribution.

#### `waterObjects` — List (all three: 0)
#### `waterObjectDensity` — AnimationCurve
- **Effect:** Object prefabs spawnable IN the water (e.g., lily pads, waterlogged stumps). Empty in vanilla — possibly an unused extension point.

### Pond vs LakeSmall vs LakeLarge — differences summary

| Property | Pond | LakeSmall | LakeLarge |
|---|---|---|---|
| `shorelinePoints` | 0 | 150 | 300 |
| `shorelineHeight` | 0.03 | 0.02 | 0.02 |
| `waterMaterial` | `Terrain2Water Pond` (green-murky) | `Terrain2Water_Lake` (blue-clear) | `Terrain2Water_Lake` (blue-clear) |
| `foamMaterial` | `Terrain2Surf_Pond` | `Terrain2Surf_Lake` | `Terrain2Surf_Lake` |
| `underwaterTexture` | `DirtUnderwaterPond01` | `DirtUnderwaterLake01` | `DirtUnderwaterLake01` |
| `shorelineObjects` count | 11 | 13 | 14 |
| `underwaterDensity` keys | 3 | 3 | 4 |
| `shorelineDensity` keys | 4 | 6 | 6 |

**Why `RiverPreferLakeWaterType = true` doesn't visibly fix the green-rivers problem:** RR assigns `LakeSmall`'s WaterType to its rivers, but the `waterMaterial` reference *is* what FF's renderer picks per-area when drawing the surface. If FF's render pipeline classifies the area on dimensions/area before consulting the WaterType reference (or applies a fallback when the polygon shape doesn't match what `waterMaterial` expects), the assignment may be ignored at draw time. That's the open question for the green-rivers issue.

---

## How this doc gets updated

Section 1 reflects current `MelonPreferences.cfg` defaults. Sections 2-4 are based on a runtime `[RR][WaterDump]` capture from a fresh Medium-size map gen at seed `1964133`. If FF updates change the field set, re-run the dump (delete/rebuild — gated by `_dumpedWaterSettings` static, fresh process clears it) and update this doc.
