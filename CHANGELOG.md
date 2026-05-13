# Changelog

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
