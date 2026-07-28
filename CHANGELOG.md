# Changelog

## v1.6.2 — 2026-07-28

### Performance

- **Main-menu stutter/jitter fixed** ([Plugin.cs](Plugin.cs)). `OnUpdate`'s `FindObjectOfType<TerrainGenerator>` fallback ran every 0.5s even at the main menu, where `RiverSettingsPatch.CachedGenerator` is always null. Profiling (FFPerfProbe) measured each scan at **250–320 ms** against the menu vista's objects — two quarter-second stalls per second, i.e. the choppy menu players kept reporting (Workshop v1.5.6 has this too). The fallback is now gated on a new `AnyTerrainSceneLoaded()` check (name-based scan of all loaded scenes for `"Map"`/`"Frontier"`) after the existing throttle, so it never runs at the pure main menu where no terrain exists. Menu returns to a locked 60 FPS; seed preview (which additively loads its own `Map` scene) is unaffected. The check reads scene **names across all loaded scenes** rather than the active scene's build index, avoiding the additive-scene trap that broke an earlier gate.

## v1.6.1 — 2026-06-27

### Fixed

- **Deer flooding the entire map with `RiversDontBlockWildlife` ON** ([Patches/WildlifeRiverPatch.cs](Patches/WildlifeRiverPatch.cs)). A uniform grid of deer spawn clusters appeared across the whole map mid-session. Two causes, both fixed:
  - **The runtime bypass was too broad.** It approved *every* spawn point while the deer spawn-validity check ran. It now probes the path check with `FloodFillType.IgnoreBuildings` and only bypasses points that are **water-isolated** (cut off by an RR river) — points that are merely **building-blocked** (inside town/walls) are left invalid, as vanilla intends.
  - **The load-time repair flagged the whole spawn grid `inUse`.** `TryRepairLoadedSpawnAreas` set `inUse=true` on all ~1600 areas before the empty-check, so FF's daily respawn seeded deer into every cell. It now recomputes an empty area's spawn points first and only activates the area if the recompute actually produces points (i.e. it was river-isolated) — town-covered areas stay dormant. The feature still repopulates genuinely cut-off regions.

> Note: this only prevents *new* over-flagging. A save made during the buggy run may already have areas flagged `inUse`; reload a pre-bug save or a fresh map to verify a clean result.

## v1.6.0 — 2026-06-26

### Added

- **River fish pool sized by actual river size** ([Patches/FishingShackPatch.cs](Patches/FishingShackPatch.cs), new pref **River Fish Per Water Cell**). Since v1.5.4 batched each river into one merged polygon, FF's area→maxFish curve saturated and a huge map-bisecting river held the same capped fish as a pond. River fish are now sized by the river's actual filled water-cell count (`cells × RiverFishPerCell`, default 1.0 ≈ one fish per water cell). A large bisecting river now holds ~10k fish without a giant multiplier crutch.
- **Lake/pond fish sized by area too** (new pref **Size Lake/Pond Fish By Area Too**, `ScaleLakeFish`, default ON). Applies the same per-water-cell ratio to lakes and ponds, so a big lake holds proportionally more fish than a small pond instead of being capped by FF's saturating curve. Boost-only — never drops a water body below its vanilla count; the river productivity multiplier is not applied to lakes. Turn OFF to keep vanilla lake/pond counts.
- **Rivers Don't Block Wildlife Spawning** (new pref `RiversDontBlockWildlife`, default OFF) ([Patches/WildlifeRiverPatch.cs](Patches/WildlifeRiverPatch.cs)). When ON, deer/herd wildlife may spawn on the far side of a river instead of being walled off by FF's path-to-town flood-fill gate. Scoped to the wildlife spawn-validity check only — villager pathing, building placement, and hunter trapping still see real reachability. Fresh gens seed deer across the river immediately; existing saves get their cut-off spawn areas recomputed and flagged in-use so deer repopulate over time.
- **Map-preview timeout message** ([Patches/PreviewGenWorker.cs](Patches/PreviewGenWorker.cs), [Patches/PreviewOverlay.cs](Patches/PreviewOverlay.cs)). On slow/Proton gens where the preview render stalls, the overlay now shows "Map Preview Timed Out — You can still start the game though!" instead of an indefinite spinner.

### Fixed

- **River fish count not surviving save/reload** ([Patches/FishingShackPatch.cs](Patches/FishingShackPatch.cs)). The fishing shack's info panel reads the river's `isRiver` FishArea, and `FishingManager.Load` replaces the whole `fishAreas` dictionary with the saved objects *after* `Initialize` — discarding any scaling done at Initialize. River (and lake) fish are now also re-scaled in a `FishingManager.Load` postfix so the correct size persists on reloaded saves, and the scaling overwrites unconditionally so both vanilla-low and any stale-high values from older builds are corrected. Lakes/rivers are matched to their cell counts by persistent `waterAreaId` (robust across reload) with a world-bbox fallback.

### Changed (diagnostics)

- The fishing-panel `[Panel]` auto-dump and the **F10** fishing-state dump are now gated behind **`VerboseDiagnostics`** (off by default), matching the biome dumper and F9 path-to-town probe.

## v1.5.6 — 2026-06-19

### Fixed

- **`OnUpdate` never ran during gameplay** ([Plugin.cs](Plugin.cs)). The v1.5.4 "main menu gate" used `SceneManager.GetActiveScene().buildIndex < 2` to skip menu/loading frames — but the active gameplay scene is `Frontier` (buildIndex 1); the terrain `Map` scene (buildIndex 2) is loaded *additively* and isn't the active scene. So the gate returned out of `OnUpdate` for the entire session in-game. Impact was masked because save-load river restore runs from the `BuildTerrainShared03` Harmony postfix (which resolves the save name itself), not `OnUpdate`. Removed the broken buildIndex gate; the existing 0.5s throttle on the `FindObjectOfType` fallback already addresses the perf concern it was meant to solve.

### Added (diagnostics — `VerboseDiagnostics`-gated, off by default)

- **Biome stats dumper** ([Patches/BiomeStatsDumper.cs](Patches/BiomeStatsDumper.cs)). One-shot dump of every map type's themes, mountain/water ranges, and per-biome resource percentages / mineral-site curves / foragables to `UserData/RiversRestored/biome_stats.txt`.
- **Path-to-town debug probe** ([Patches/PathToTownProbe.cs](Patches/PathToTownProbe.cs)). **Ctrl+Shift+F9** logs, for the tile under the cursor, whether a flood-fill path to town exists for both gates that an RR river can break: `BridgesOnly` (deer/wildlife spawn eligibility) and `WallsBlock` (hunter trap placement). The authoritative river-connectivity check — villager foot traffic is NOT a reliable proxy (movement is Unity NavMesh, a separate system from the spawn/trap flood-fill gates). See `FF-Modding-Knowledge/game-systems/river-system.md`.

## v1.5.5 — 2026-05-22

### Fixed

- **Fishing-area markers leaking into the scene** ([Patches/FishingShackPatch.cs](Patches/FishingShackPatch.cs)). `CreateFishingAreasPostfix` deduped river fishing areas by water-area id, dropping entries via `RemoveAt` without destroying their marker GameObjects — orphaned markers piled up and stayed visible with no shack selected. Now deduplicates by object reference: every distinct vanilla marker is preserved, while RR's own reference-equal duplicates still collapse. Postfix stays idempotent.

## v1.5.4 — 2026-05-22

### Performance

- **Batched WaterArea builder** ([Patches/RiverWaterAreaBuilder.cs](Patches/RiverWaterAreaBuilder.cs)). Replaced per-stamp `AddWaterAreaWithPanguMerge` (hundreds of merge iterations per river, each scanning all waterAreas, allocating masks, recomputing edges) with single-pass `BuildRiverMask` that collects disc cells into one HashSet, then `AddRiverWaterArea` creates one WaterArea per river. Major reduction in allocation churn, GC pressure, and map-gen stutter.
- **Throttled scene scans** ([Plugin.cs](Plugin.cs), [Patches/RiverCarver.cs](Patches/RiverCarver.cs)). `FindObjectOfType` fallback during loading window now throttled to 0.5s intervals instead of every frame.
- **Main menu gate** ([Plugin.cs](Plugin.cs)). `OnUpdate` bails immediately on main menu and loading screens (`buildIndex < 2`). Zero per-frame work when no terrain exists.

## v1.5.3 — 2026-05-22

### Fixed

- **River fishing areas compounding on every CreateFishingAreas call** ([Patches/FishingShackPatch.cs](Patches/FishingShackPatch.cs)). The 8× area multiplier re-applied to the already-multiplied list each time a fishing shack called `CreateFishingAreas`, snowballing from 99 → 211 → 1520 entries. Now idempotent: strips all existing river entries, re-adds exactly `unique × multiplier`. Safe to call any number of times.
- **River fish population not scaling with area multiplier** ([Patches/FishingShackPatch.cs](Patches/FishingShackPatch.cs)). Duplicating FishArea references inflated the displayed area count (1520 / +15,120%) but all duplicates shared the same ~297-fish pool — river shacks starved cycling "no fish available." Now scales `maxFish` and `fishCount` on each river FishArea in `FishingManager.Initialize` postfix (e.g., 297 × 8 = 2376), so the actual fish population matches the intended multiplier.

## v1.5.2 — 2026-05-21

### Fixed

- **Ribbon toggle not fully respected** ([Patches/RiverPersistence.cs](Patches/RiverPersistence.cs)). FF's own `BuildTerrainShared03` iterates `_generationData.rivers` and creates WaterPath ribbon objects — it doesn't check our `EnableRibbonAnimation` toggle. Our gates covered Stage 60 (gen) and `SpawnWaterPathsFromSidecar` (reload), but missed this third path. BTS03 prefix now checks the toggle after fixing cp.y; when disabled, caches river data for sidecar save then clears the list so FF creates no ribbons. Static water polygon is unaffected. Toggle works on existing saves — just flip the setting and reload.

## v1.5.1 — 2026-05-19

### Fixed

- **Fish areas not created on reload** ([Patches/FishingShackPatch.cs](Patches/FishingShackPatch.cs)). `GameManager.isLoadedGame` is a computed property that delegates to `PreGameInitializer.isLoadedGame` — it has no setter or backing field on GameManager itself. Previous attempts to flip it via GameManager always failed silently. Now traverses through `GameManager._preGameInitializer` to reach the actual auto-property on `PreGameInitializer`, which has a protected setter accessible via reflection.
- **Preview rivers lost on gameplay gen** ([Patches/RiverSettingsPatch.cs](Patches/RiverSettingsPatch.cs)). Preview and gameplay generate different heightmaps from the same seed (different CERandom consumption order in the sliced pipeline), so the Voronoi pathfinder often produced different rivers — or none at all — in gameplay. Now caches preview-gen river paths and replays them into gameplay gen, guaranteeing the user gets the rivers they approved in preview.

### Changed

- **Retuned all preset defaults** to match playtested values across all five biomes. Key changes: narrower inner channels (InnerRadius=2) with wider water polygons (BlobRadius=7-10), higher jitter for natural meander, more smoothing passes (10-12), and adjusted river counts per biome (IdyllicValley=5, LowlandLakes=12, AridHighlands=2, Plains=1, AlpineValleys=2).

## v1.5.0 — 2026-05-18

### Fixed

- **Sidecar not found on save/reload** ([Patches/RiverPersistence.cs](Patches/RiverPersistence.cs)). Root cause: FF creates a new slot folder with a fresh timestamp on every save (`Arkham_2026075203052/` -> `Arkham_2026185173135/`). The sidecar was written into the slot folder from the save arg, but on reload `activeSaveFileName` pointed to a different slot folder. Neither the flat path nor the canonical path matched the original write location. Fix: save now strips the slot folder via `Path.GetFileName()` and writes to a flat path (`Save/{bareName}.rivers`). Load checks flat first, canonical as fallback. Sidecar reliably survives save-over and reload regardless of FF's ephemeral slot folder naming.
- **Ribbon mesh underground on elevated terrain** ([Patches/RiverPersistence.cs](Patches/RiverPersistence.cs)). On Alpine Valleys, Sea Layer Y (3.15) diverges from computed waterHeight (6.19). The trench floor sits at 3.94 — using Sea Layer Y put the ribbon below the trench floor, making it invisible. Both `SpawnWaterPathsFromSidecar` (reload) and `BuildTerrainShared03Prefix` (fresh gen) now use `ComputeWaterY()` (which calls `GetWaterHeight()`), falling back to Sea Layer Y only if computation fails. Ribbon sits at the correct water surface on all terrain elevations.
- **Fish areas empty on reload (fishAreas.Count=0)** ([Patches/FishingShackPatch.cs](Patches/FishingShackPatch.cs)). `FishingManager.Initialize` skips all fish-area creation on loaded games (`isLoadedGame` gate) — it expects `FishingManager.Load()` to deserialize them from the save. But ES2 deserialization silently returns empty when FishArea objects reference Unity types that don't survive serialization. Fix: added a prefix on `FishingManager.Initialize` that detects empty fishAreas on a loaded game and temporarily flips `isLoadedGame` to false so Initialize's own creation logic runs from `GetAllWaterAreas()`. Postfix restores the flag.

### Added

- **`FindRivers` refresh in BTS03 postfix**. After spawning WaterPath objects from sidecar, re-calls `FishingManager.FindRivers()` so the fishing system's `IsInRiver` placement checks recognize the newly-spawned ribbon geometry.

## v1.4.6 — 2026-05-14

### Added — Per-Preset Water Multiplier

- **`WaterMultiplier` cfg entry per preset** ([Patches/WaterValueOverridePatch.cs](Patches/WaterValueOverridePatch.cs), [Plugin.cs](Plugin.cs)). Scales the seed's water value before terrain generation, so the same seed produces drier or wetter maps. Per-preset defaults: `IdyllicValley=0.75`, `LowlandLakes=0.7`, `AlpineValleys=0.85`, `AridHighlands=1.0`, `Plains=1.0`. Trims excess water on biomes that feel oversaturated once rivers are added. `1.0` = vanilla. Exposed in `MelonPreferences.cfg` under `[RiversRestored_<Preset>]` and as a slider in the Keep Clarity panel ("Preset · <Name>" → "Water Amount Multiplier", range 0.3–1.5). Slider's display name includes the preset's default in parentheses so users can revert without guessing.
- **Applies to both preview and gameplay**. Harmony prefix + finalizer on `TerrainGeneratorController.GenerateInternal(bool)`. Asymmetric application by path:
  - `game=true` (gameplay): modifies `SettingsManager.mapWaterValue` only. FF's `if (game)` block reads that into `tgc.water` at line 17792.
  - `game=false` (preview): modifies `tgc.water` (instance field) only. FF skips the `if (game)` block, so the value RR's `PreviewGenWorker.ApplyTgcGenParameters` set from the decoded seed is what gets used.
  - Modifying both fields on every gen (the v1 attempt) caused collateral mid-gen readers to see the modified `SettingsManager.mapWaterValue` and consume CERandom differently, producing different heightmap output between preview and gameplay even with identical seeds. Path-specific application keeps the gen pipeline deterministic.
- Finalizer restores whichever field was modified so subsequent reads (UI display, save state, re-rolls) see the user's actual slider value.

### Fixed

- **Spurious preview overlay re-engaged after clicking "Start Settlement"** ([Patches/MapPreviewRenderer.cs](Patches/MapPreviewRenderer.cs)). `LateCarvePostfix` is hooked on FF's terrain gen stage carriers (Stage 40/50/60/70/97), which fire during BOTH preview gen AND actual gameplay terrain gen. After HardCancel ran (Start click → unload preview Map scene → load gameplay Map scene), FF's gameplay gen ran through those same carriers and `MapPreviewRenderer.TryRender` fired from a gameplay stage hook — writing a spurious PNG with `seed=''` and the preview overlay UI re-engaging with a progress bar ("preview goes black and starts generating another preview"). Gated `TryRender` behind `PreviewGenWorker.IsPreviewActive` so it only fires when the preview pipeline is actively requesting a gen, not during gameplay's own terrain pipeline.
- **Two preview gens per panel-open with the same final seed**. The Advanced Settings panel became visible a few frames before FF finished writing the initial seed string into the input field. First-show fired with `seed=''`, kicking gen #1; the seed then populated, change-detect fired (`'' → '...'`), kicking gen #2. The user saw gen #2's preview but gameplay matched gen #1's RNG state (same seed value, but distinct RNG paths produced visibly different river-Voronoi outputs). Now defers first-show until `curSeedText` is non-empty — collapses to a single trigger and the visible preview's gen state matches what gameplay reproduces.
- **Preview rendering still went through `LateCarvePostfix` during gameplay gen** even after the IsPreviewActive gate above — same root cause, single fix covers both surfaces.

## v1.4.4 — 2026-05-13 (hotfix)

### Fixed

- **Lakes had no fish nodes / no fishing zones** ([Patches/RiverSettingsPatch.cs](Patches/RiverSettingsPatch.cs)). Root cause: RR's "heal" pass before injecting the early Stage 60 (RiverGeometry) on `TerrainGenerator` set `cachedAreas` to a fresh empty `List<WaterAreaInfo>` as a defensive measure against a GridTrace NRE. But FF's `TerrainManager.GetAllWaterAreas()` is lazy-cached and never re-invalidated by FF, so that empty seed stuck for the rest of the session. When `FishingManager.Initialize` later called `GetAllWaterAreas()` it received the empty list — its lake-iteration loop ran zero times, **zero `FishArea`s were created for any lake**. The parallel `riverInfos` loop still ran, so river fishing worked (FF native rivers, hardcoded area), but every lake on every map had zero fish nodes / zero fishing zones (a fishing shack placed on a lake shore showed `Fishing Areas: 0, Fish Count: 0`). Fix: null out `cachedAreas` after our Stage 60 injection finishes — the next caller (`FishingManager.Initialize` or any rendering subsystem) triggers a fresh rebuild against the real, fully-populated `_generationData.waterAreas`. Diagnostic confirmation: `fishAreas.Count` went from `5` (rivers only) → `34` (32 lakes + 2 rivers) on the same seed after the fix.
- **River merge could absorb adjacent lakes during gen-time** ([Patches/RiverWaterAreaBuilder.cs](Patches/RiverWaterAreaBuilder.cs)). When a river stamp's bbox + cell-adjacency tolerance reached a vanilla lake polygon, `AddWaterAreaWithPanguMerge` fused the lake into the river's `WaterArea` and deleted the standalone lake entry. The merged polygon's `edge[]` array — a thin river disc joined to a lake blob, sometimes with a 1-cell gap from the `padding=1` adjacency tolerance — was no longer a clean single closed loop, so FF's `WaterAreaInfo.area = ClosedPolygonArea(SortAdjacentPoints(edge))` returned a bogus near-zero value, and `FishingManager.GetFishDataForWaterArea` produced 0 fish / 0 schools / 0 shoreline fish for the merged area. Rivers themselves still fished fine (separate `riverInfos` path with hard-coded `area = 100000f`), but a lake the river grazed came up empty. Restricted the merge-set selection so river stamps only fuse with RR's own previously-added river areas (tracked via `RiverWaterAreaBounds` / river WaterType reference). Vanilla lakes/ponds/ocean are never absorbed — they keep their original Stage-50 flood-fill polygons, FF computes the correct area, and fish spawn normally. Side benefit: this also closes the v1.4.1 "independent lake deleted on save reload" bug class at its gen-time source.

### Added

- **`FishingManager.Initialize` observability hook**. Logs one line at end of init: `fishAreas.Count=N (lakes=L, rivers=R)`. Future regressions of either fix above show up immediately in the log as `lakes=0`.

## v1.4.3 — 2026-05-11 (hotfix)

### Fixed

- **Preview hung / never rendered when `RiversEnabled = false`.** Two coupled bugs in the preview pipeline:
  - `PreviewGenWorker.ConfigureDebugOptions` hard-coded `generateRivers = true` regardless of the master toggle. With rivers disabled, FF's terrain gen would enter river stages while RR's river patches early-returned on `!RiversEnabled` — leaving FF waiting for state RR's patches wouldn't produce. Preview hung indefinitely (past the inner-gen timeout because the stall happened before any timeout-protected stage). Now reads `RiversEnabled.Value` and passes it through, so river stages are skipped cleanly when rivers are off.
  - `RiverSettingsPatch.LateCarvePostfix` early-returned on `!RiversEnabled` BEFORE calling `MapPreviewRenderer.TryRender`, so the preview overlay never received a texture when rivers were disabled. The overlay stayed stuck on "generating preview" indefinitely even after gen completed gracefully. Restructured to gate only the carve on `RiversEnabled` — `TryRender` now fires whether or not rivers are enabled, so the preview always renders.
- Net effect: rivers-off previews now generate and display correctly in the same time budget as rivers-on, and the toggle can be flipped freely between previews.

## v1.4.2 — 2026-05-10 (hotfix)

### Fixed

- **Preview PNG written on every save load.** `MapPreviewRenderer.TryRender` was firing from `RiverSettingsPatch.LateCarvePostfix`, which hooks FF's late-stage terrain carriers — those run on every save load when FF rebuilds terrain. Result: every save load wrote a fresh PNG to `UserData/RiversRestored/Previews/`, and because RR's carver short-circuits on save load (`RestorePending` / `RestoredThisLoad` guards), the captured render was a pre-river-overlay state. Players saw a stream of rivers-less PNGs accumulating per-load. Added an `IsLoadingSavedMap` guard at the call site — render now skips on save loads. New game gen and auto-regen previews unaffected.

## v1.4.1 — 2026-05-09 (hotfix)

### Fixed

- **Independent lakes deleted from save reload** ([Patches/RiverWaterAreaBuilder.cs](Patches/RiverWaterAreaBuilder.cs)). The post-load absorb pass in `AddPrebuiltWaterAreas` was removing any waterArea whose center cell landed inside a river polygon's mask. When a river snaked near or around an unrelated lake, the lake's center could fall inside the river's bbox without the lake being a merge duplicate — and got deleted from the live `_generationData.waterAreas` list. `FishingManager` then never saw the lake, no `FishArea` spawned, no fishing nodes, the lake was un-fishable.
- Tightened the absorb criterion: a candidate lake's full bbox must fit inside the river's bbox, AND every sampled corner + center of the lake must land on a filled cell of the river mask. This still catches the merged-into-river case (lake fully consumed by the river polygon) without consuming adjacent independent lakes.
- Added per-lake log line `absorbing existing waterArea bounds=[...] wt='...' (fully contained in river polygon)` so future cases of unwanted absorption are diagnosable from the log.

### Recovery for v1.4.0 saves affected by the bug

If you saved a game after loading it with v1.4.0 and a lake lost its fish nodes, the disk save now also lacks that lake. Recovery options:
- Revert to a backup save from before the v1.4.0 load if you have one
- Manually carve out fish-spawn alternatives via Pangu's lake creation (radius-zero ponds in the lake area)
- Accept the loss; future loads on v1.4.1+ will not repeat the bug

## v1.4.0 — 2026-05-09

Major feature release: Pangu-style preview integration, per-preset slider tuning, KC settings registration, and a long tail of stability fixes.

### Added — Map Preview System

- **Pangu-canonical preview gen** ([Patches/PreviewGenWorker.cs](Patches/PreviewGenWorker.cs)). Loads FF's "Map" scene additively, runs `tgc.GenSliced_Generate(false)` on its `TerrainGenerator`, harvests `_generationData` for the renderer. Mirrors Pangu's worker-acquisition pattern (`TryCreateSeedPreviewWorkerFromCandidate`) and timeout discipline.
- **Polished map renderer** ([Patches/MapPreviewRenderer.cs](Patches/MapPreviewRenderer.cs)). Rasterizes biome polygons by `editorColor`, blends elevation ramp with biome color, applies hillshade lighting, contour lines, slope-rocky tinting, per-pixel land noise, snow-capped border, opaque water with depth/texture, dark-earth shoreline, and per-preset tinting. 768×768 output sized for the in-game overlay panel.
- **In-game overlay UI** ([Patches/PreviewOverlay.cs](Patches/PreviewOverlay.cs)). UGUI Canvas with FF-themed sprites (`IMG_BGShadowThickSoft01`, `BTN_Border02_UP`) and Andada-Bold caption font. Three-column caption: seed/biome/size + river/water % on the left, Resources/Wildlife stacked center, Maladies/Raiders stacked right.
- **Pangu-style auto-regen UX**. Preview shows only when (1) on the Start scene, (2) `EnableMapPreviewRender` pref is on, (3) FF's Advanced Settings panel is expanded. Polls `SettingsManager.mapSizeValue` + the seed input field text every frame; 300 ms debounce; cancels in-flight gen and re-fires on size change, seed reroll, manual seed entry (commits on Enter/blur, not per-keystroke), and non-Custom map-type pick.
- **Determinate progress bar with stall fallback**. Reads `_generationData.stage` (1..97) for the percent, smooths via Lerp. Falls through to indeterminate sliding-segment animation if stage stops advancing for 1.5 s (covers soft-restart handoff and edge cases).
- **Filename metadata**. Saved previews are named `<seed>_<preset>_<size>_r<rivers>_w<water>pct_d<RTVT>_<timestamp>.png`. `RTVT` = difficulty letters (P/T/V/X) in Resources/Wildlife/Maladies/Raiders order. Built for the future seed-share-bank use case.
- **Auto-prune saved PNGs to 25 newest**. Fires after each save; sorts by `LastWriteTimeUtc`; deletes the rest. Keeps `UserData/RiversRestored/Previews/` under ~10 MB even with rapid iteration.

### Added — Per-Preset Tuning

- **Per-preset granular sliders** for all 5 RiverPreset modes (IdyllicValley, LowlandLakes, AridHighlands, Plains, AlpineValleys). 13 tunable parameters per preset: NumRivers, MinPoints, MinWidth/MaxWidth, InnerRadius, OuterRadius, BlobRadius, BlobStride, TrenchDepth, SmoothPasses, JitterAmplitude, JitterFrequency, FishingAreaMultiplier.
- **Master sliders override per-preset values when set non-default**. Lets users globally tune one parameter without rewriting all 5 preset tables.
- **GranularSettings pref toggle** to surface per-preset sliders in Keep Clarity's settings panel only when the user opts in.

### Added — Keep Clarity Integration

- [KeepClarityIntegration.cs](KeepClarityIntegration.cs) — registers all RR prefs with KC's SettingsAPI via reflection-based soft-dependency. Master + per-preset categories appear in KC's in-game pref manager; per-preset categories are hidden unless GranularSettings is on.

### Added — Diagnostics

- **WaterDump diagnostic** ([WATER_LEVERS.md](WATER_LEVERS.md)). Optional WaterType field dump at startup so future tuning sessions have the live values rather than re-discovering them.
- **NW→SE flow bias** for heightnoise (`RiverFlowBias` pref). Subtle bias applied at Stage 38 so rivers prefer NW→SE flow direction. Configurable strength (0..1).

### Fixed — Map Preview System

- **Map size always rendered as Medium** regardless of slider position. `SettingsManager.mapSizeValue` is a property with a `_mapSizeValue` backing field — `GetField("mapSizeValue")` returned null and the default-Medium fallback fired. Now reads property first, then backing field. Same fix applied to `SettingsManager.Instance.mapType` and `SettingsManager.mapLakeValue`.
- **3+ minute load stall after Start click**. RR's `RiverCarver` and `ForceWaterPlaneRebuild` were firing during preview gen, mutating the live Map scene's terrain heightmap and water plane. Unity's automatic asset cleanup after the unload then walked millions of orphan refs. Gated both behind `PreviewGenWorker.IsPreviewActive`. The gate stays set through the scene unload (cleared in `UnloadMapScene` after the async op completes) so RR's mutation patches don't fire on a Map scene we're about to destroy.
- **30-60 s frozen progress bar after reroll**. Soft-restart called `StopWorkerCoroutines` to kill the in-flight gen, but the polling loop only watched `tg.generating` — which stays true forever when the coroutine that flips it false gets terminated. Now the polling loop checks `_cancelled` and bails on the next frame.
- **Caption populated 15-20 s after image rendered**. Caption-build was deferred to after the polling loop exited. Now fires the moment `MapPreviewRenderer.RenderedThisGen` flips, immediately after the `LateCarvePostfix` render.
- **Old preview flashed on panel re-open** between game→main-menu→advanced-settings. `CaptionReady` was carrying over from the previous session; the panel showed the stale image during the 300 ms debounce window. Now reset on the panel-open transition.
- **Two-different-images-per-gen** mystery. Each gen rendered twice — once mid-pipeline at `LateCarvePostfix`, again post-completion in `PreviewCoroutine`. Post-`Sliced_OnGenerated` mutations to `_generationData.heightNoise` made the second render different from the first. Removed the post-completion render; one image per gen, matches in-game gen exactly.
- **Reroll button never triggered auto-regen**. The UI's `RerollMap` ([Assembly-CSharp.cs:289327](Assembly-CSharp.decompiled.cs:289327)) writes via `SetTextWithoutNotify` to the input field text but does NOT update `SettingsManager.mapTerrainSeedValue` until StartNewGame — polling that static returned the same value across reroll clicks. Now polls the input field text directly via a cached ref.
- **`Resources.FindObjectsOfTypeAll<TMP_InputField>()` running every frame** caused 5+ second GC stalls (Windows "not responding" dialog) on reroll. Replaced with a cached input-field ref (lookup throttled to 30 frames, only when cache is cold).
- **`_advancedPanelGroup` lookup keeping a 30-frame allocation cadence** in gameplay. Gated to Start scene only.
- **Preview overlay's `GraphicRaycaster` blocking clicks** on FF's Town Center confirm dialog. Disable Canvas + GraphicRaycaster off the Start scene.
- **Map size enum inversion** in TGC parameter application. `Size.Large=0`, `Medium=1`, `Small=2` — REVERSED from the slider's UI order. Read `SettingsManager.mapSizeValue` directly (already enum-aligned by FF's `OnMapSizeChanged` callback) instead of the slider value.
- **`tg.generating` polling missed gen completion** because the inner gen runs on a sibling `StartCoroutine`, not the outer `tgc.GenSliced_Generate` enumerator. Outer enumerator returns done after ~1 step; inner runs independently. Now drives the outer briefly, then polls `tg.generating` for actual completion.

### Fixed — Other

- **Gameplay 2-second stutter tick** caused by `StartScenarioHotkey.Tick`'s 30-frame `Resources.FindObjectsOfTypeAll<Button>()` scan running forever in gameplay. Removed the hotkey class entirely; KC will own this. (Hotkey was: Enter/Space/Numpad-Enter dismisses the Town Center "we've finished scouting" dialog when the OS cursor isn't rendering.)
- **`Stage 60 ribbon animation` skipped properly** when `EnableRibbonAnimation=false`.
- **Idle/IdyllicValley + Plains preset tuning** for less-aggressive carving on plains-style maps.
- **Carver hot-path reflection caching**. `Resources.FindObjectsOfTypeAll<TerrainManagerBase>` and `<Terrain2>` were scanning every OnUpdate frame during the load window; cache them on first resolve.
- **Bias axes corrected** — NW_to_SE was previously biasing the wrong axis pair.

### Performance

- `OnUpdate` reflection caching reduced per-frame allocations during load and gameplay polling.
- Preview gen's RNG seed reset preserves determinism across previews on the same seed.
- Preview gen's worker uses the live Map scene's TGC instead of cloning — Pangu's pattern, avoids state-divergence bugs from Editor-vs-runtime asset configuration.

### Internal / Diagnostic

- `SESSION_HANDOFF.md`, `SESSION_HANDOFF_2026-05-08.md`, `SESSION_HANDOFF_2026-05-09.md` — design notes and per-day session captures for future maintenance.
- `WATER_LEVERS.md` — full WaterType / WaterSettings field reference.

### Known Limitations

- **Custom map type doesn't trigger auto-regen by itself.** Custom doesn't reroll the seed (FF's `RerollMap` short-circuits on Custom), so no input-field text change to detect. Acceptable: Custom users typically pair it with manual seed entry, which is detected.
- **Map-set drift across mods.** Same seed string + size + biome shared between an RR user and a non-RR user produces visually similar terrain (heightnoise, biomes, mountains, lakes match) but different rivers and slightly drifted resource/tree/wildlife positions due to RNG state divergence after RR's Stage 38 injection.

---

## v1.3.0 and earlier

See git log for history prior to 1.4.0.
