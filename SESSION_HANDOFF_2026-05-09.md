# Rivers Restored — Session Handoff (2026-05-09)

**Last updated:** 2026-05-09 (end of long session — visual polish + Pangu-match attempt)
**Current branch:** `feature/per-preset-sliders`
**Steam-shipped DLL:** `bin/Release/net46/RiversRestored.dll` — **unchanged this session**, safe
**Active dev DLL:** `bin/Debug/net46/RiversRestored.dll` (deployed to Mods/)

This handoff covers the 2026-05-09 session. Prior session: [SESSION_HANDOFF_2026-05-08.md](SESSION_HANDOFF_2026-05-08.md).

---

## TL;DR

Spent the session on two big tracks:

1. **Visual polish on the map preview render** — biome-based coloring, per-area variation, water depth/texture, contour lines, snow-capped border, shadow tuning. Rendering looks good now.

2. **Trying to make our preview match Pangu/actual-gen exactly** — burned hours on this. Got close but not pixel-perfect. Hit a hard problem where running the full FF gen pipeline (`tgc.GenSliced_Generate(false)`) on the live TGC triggers `Sliced_OnGenerated` builders that mutate scene state and hang the actual game's load screen. Skipping those builders leaves slight RNG drift between preview and actual gen.

User explicitly paused at end of session. Pick up tomorrow.

---

## What landed this session (all on `feature/per-preset-sliders`)

### 1. Map render polish — visual ✅
[Patches/MapPreviewRenderer.cs](Patches/MapPreviewRenderer.cs)

**Resolution bumped 512 → 768.** PNGs and overlay panel sharper.

**Biome-based coloring.** New `BuildBiomeColorMap` rasterizes each `TerrainArea` polygon onto an hn-cell grid using `biome.editorColor`. Reads `_generationData.areas`, uses `TerrainPoly.Contains(x, z)` for point-in-polygon. Bbox-clipped per area for speed. Heightmap loop blends biome 60% over elevation ramp at low/mid altitudes, fading back to elevation ramp on peaks (n>0.7) so mountains stay rocky/snowy regardless of biome.

**Box-blur smoothing pass on biomeMap.** Two passes of radius-2 separable box blur (`BoxBlurColorMap`) so polygon boundaries between adjacent biomes blend instead of producing hard Voronoi edges. Critical for matching Pangu's smooth look.

**Per-area color jitter** (±2% per channel, hashed by `areaIndex * 2654435761`). Adjacent same-biome polygons get slightly different shades.

**Tree-density darkening** — reads `TerrainArea.treeDensity` field, darkens biome color up to 12%. Heavy-forest areas read denser than pasture.

**Slope-based rocky tint** — steep cells (high gradient magnitude) blend up to 35% toward warm brown/gray (150,130,105). Adds variation in mid-elevation band where elevation ramp alone is uniform tan/green.

**Per-pixel land noise** — small ±5 hash-based jitter per channel. Fine-grain micro-texture so flat patches don't read as paint.

**Per-preset color cast.** New `GetPresetTint(RiverPresetMode)` returns subtle ≈0.85-1.10 channel multipliers:
- IdyllicValley: lush green
- LowlandLakes: cool/blue
- AridHighlands: warm/red-yellow
- Plains: pale yellow-tan
- AlpineValleys: cool gray-blue

Without this, AridHighlands and IdyllicValley render nearly identical because they share most biome assets.

**Subtle contour lines** — 0.08 normalized-elevation steps, 20% darken on cells where neighbor's quantized step differs. Topographic-map feel.

**Hillshade lambert floor lifted 0.35 → 0.60.** Shadows less harsh.

### 2. Water visual treatment ✅
**Replace, don't blend.** `PaintWaterPixel` now writes opaque water tones (no blend with underlying hillshaded land). Fixes the "water = raised plateau" illusion that came from mid-gen heightnoise leaking through (RiverCarver carves Unity Terrain but doesn't update `_generationData.heightNoise`).

**River vs lake tones:**
- River: bright cyan (130, 200, 255)
- Lake: deep navy (50, 110, 195)

Discriminator: `WaterArea.waterType.name` substring match for "river".

**Multi-layer water texture.** Per-pixel render pass adds:
- Fine hash noise (±10, weighted toward blue for shimmer)
- Coarse 8×8 patches (±12)
- Sine ripple bands (high freq for rivers, gentle longer waves for lakes)
- Depth-from-shore gradient (water pixel surrounded by water → darker; shore pixels stay lighter). Lakes darken up to 35%, rivers up to 15%.

**NW bank shadow on water.** Water pixels with land 1-3px to NW (sun direction) darken. Reinforces "water below land" perception, fixes the residual plateau look.

**Dark-earth shoreline ring** (60, 48, 36 at 30% blend) on land pixels adjacent to water. Replaces the earlier warm-sand halo which was reading as a lit-rim plateau signal.

**Snow-capped border.** Replaces the prior gray-clamp logic. Border cells (outer 10%) blend toward cool white (232, 236, 245) with hash-based snow flecks (~⅛ bright glint at 250,252,255; ~⅛ shadow flecks at 195,205,220). Lerp by distance-to-edge so it fades from full snow at the edge to underlying terrain at the inner border boundary — reads as treeline → snow transition.

### 3. Map size inversion fix ✅
[Patches/PreviewGenWorker.cs:TryReadMapSizeEnum](Patches/PreviewGenWorker.cs)

**Bug:** `TerrainGeneratorController.Size` enum is `Large=0, Medium=1, Small=2` — REVERSED from the slider's UI order (0=Small left, 2=Large right).

**Old code** read slider value (0-2) and assigned directly to TGC.size enum → picking "Large" gave `Size.Small`.

**Fix:** Read `SettingsManager.mapSizeValue` directly — that field is enum-aligned (FF's `OnMapSizeChanged` callback writes `(Size)(2 - sliderVal)` at Assembly-CSharp.cs:288785). Pangu does the exact same thing (Pangu_FF.cs:2000).

### 4. Pangu-method copy attempts (PARTIAL — see "Open Issues") ⚠️
[Patches/PreviewGenWorker.cs](Patches/PreviewGenWorker.cs)

Spent significant time trying to make our preview produce IDENTICAL output to Pangu's preview and the actual game gen. Three iterations:

**v1 — Original (early break at Stage 60):** Drive `tgc.GenSliced_Generate(false)` coroutine, break out when `RenderedThisGen` flag flips (Stage 60 hook fired). Ran fast, didn't hang gameplay, but data was mid-gen — slight visual drift vs actual game.

**v2 — Full Pangu match (run to completion):** Removed early-break, let `GenSliced_Generate` run all the way through `Sliced_OnGenerated`. **CATASTROPHIC** — the OnGenerated tail runs `Terrain2Builder.GenSliced_BuildTerrain` and `WaterPlane.Sliced_Rebuild` on the live TGC, mutating scene state in a way that hangs the actual game's load screen at ~85%. Confirmed via log: WotW reports "animalManager never became available after 30s" → game broken.

**v3 — Manual stage replication, skip OnGenerated (current):** Replicate the body of `tg.GenSliced_Generate` manually:
1. `tgc.GenerateInternal(false)` (sync via reflection)
2. Set `tg.inGame=false`, `generating=true`, RNG seed
3. `tg.Sliced_Pregenerate(...)` (sliced — driven via `DriveCoroutineWithTimeout`)
4. Fresh `_generationData` and `debugTextures`
5. `tg.GenSliced_GenerateAsync()` (sliced — driven via `DriveCoroutineWithTimeout`)
6. **STOP** — skip `Sliced_OnGenerated` entirely

Result: game doesn't hang, preview is close but not pixel-perfect. There's RNG drift because Pangu/real-game runs `Sliced_OnGenerated` (which presumably consumes some RNG) and we don't.

**Helper: `DriveCoroutineWithTimeout`.** Yields `null` between MoveNext calls instead of forwarding inner Coroutine waits. Prevents getting stuck inside `yield return cur` when an inner Coroutine faults/orphans. Also enforces a wall-clock timeout (Pangu has 8-16s per attempt at line 2606).

**Preflight: `EnsureRuntimeScriptableObjectManagerInitialized`** (mirrors Pangu_FF.cs:3231). Calls `RuntimeScriptableObjectManager.Init()` if its static list is null. Without this, `GenerateInternal` NREs at line 17865 when duplicating biome assets via `RuntimeScriptableObjectManager.CreateInstance<TerrainBiome>()`.

**Map-scene unload watchdog.** New `PreviewGenWorker.TickUnloadWatchdog(host)` ticks every frame from `PreviewOverlay.Update`. When `IsGameplayContextActive()` (mirrors Pangu's check — `GameManager.Instance.terrainManager != null`) and `_mapSceneLoadedByUs == true`, stops worker coroutines and unloads the Map scene. Belt-and-suspenders cleanup. Less critical now that we skip `Sliced_OnGenerated` but kept for safety.

### 5. UX bug fixes ✅

**Cursor blocked / clicks not reaching Town Center confirm dialog.** Our overlay's Canvas at `sortingOrder=1000` plus `GraphicRaycaster` was intercepting pointer events on the gameplay scene. Fix: gate `Canvas.enabled` and `GraphicRaycaster.enabled` to false when off the "Start" scene. Cached the `GraphicRaycaster` lookup at `BuildHierarchy` time instead of `GetComponent` every Update.

**Three-second visual stutter during gameplay.** Caused by `Resources.FindObjectsOfTypeAll<Sprite>()` running every frame in `TryRebindSprites` (lazy retry of FF UI assets) — large array allocation triggering periodic GC sweeps. Fix: throttle to once per 30 frames AND gate to Start scene only (FF's New Game UI assets only exist there).

**Preview overlay aspect ratio wrong.** Panel was 425×425 with caption strip at bottom, leaving the preview area ~417×369 which stretched the 768×768 square texture horizontally. Fix: `PANEL_H = PANEL_W + CAPTION_H` so the inner image rect is square.

**Caption shows "Seed ?" sometimes.** When `BuildRichCaption` fails to run after a render (because the gen coroutine hung mid-pipeline), the default caption from `MapPreviewRenderer.TryRender` stays — shows `"Seed {seedDisp}"` where `seedDisp` is whatever `TryGetSeedString(tg)` returns (often empty). Partially fixed by ensuring gen completes (via timeout). Still inconsistent — see Open Issues.

### 6. Misc improvements ✅

- Log line in `Wrote preview` now includes the seed string so you can grep both flows side-by-side.
- The `Mineral overlay always shows 0` issue from previous handoff was investigated and dropped — minerals aren't part of the TG pipeline at all (`MineralManager.Initialize()` runs in `InitializeGame.Sliced_InitializeGame`, not stages). Would require a separate hook on `MineralManager.Initialize` for real-game. Preview can't render minerals because the standalone gen worker doesn't run InitializeGame. User explicitly chose to drop the feature.

---

## Open issues / known limitations

### Preview not pixel-perfect to actual gen (the big one)
Maps look very similar but specific lake/pond positions and river paths drift slightly between preview and actual game. Cause: skipping `Sliced_OnGenerated` produces different RNG consumption order than the real game.

The full Pangu-match attempt (v2) DID produce identical maps but hung the load screen. We don't fully understand WHY Pangu can run the full pipeline without hanging — the decompile doesn't show explicit cleanup we're missing. May be a timing thing or a Unity-specific scene transition behavior.

**User said this is the next-day priority.** Options to investigate:
1. **Find what we're doing differently from Pangu.** Pangu calls `tgc.GenSliced_Generate(false)` and runs the full pipeline. Why does it not hang gameplay? Possible avenues:
   - Look at exact order of cleanup ops in Pangu's `CancelSeedPreviewBuild` / `DisposeSeedPreviewWorker`
   - Check if Pangu's worker registration delays `_seedPreviewLoadedMapSceneForTemplate` flag in a way that affects unload timing
   - Check if FF's actual game gen path RESETS state our preview leaves dirty
2. **Accept the drift.** Maps are very close — same biome/water density/river count. May be good enough for shipping.
3. **Render heightnoise-only like Pangu.** Drop biome polygons/waterAreas from our render and just sample heightnoise + waterThreshold. Loses detail but matches Pangu's visual exactly. The data drift becomes invisible because both render the same simple thing.

### "Game rolls new seed on Start" claim from user (UNVERIFIED)
User stated `IT GENS A WHOLE NEW SEED` when clicking Start. Decompile traces show `StartNewGame(settlementName, gameSeed)` writes `mapTerrainSeedValue = terrainSeed` from the UI seed string, and `InitializeGame` then uses that pinned value (Assembly-CSharp.cs:99497-99503). Should NOT reroll unless `forceRandomizeTerrain=true` or `mapTerrainSeedValue=0`.

**Diagnostic to run**: have user click PREVIEW, note the seed text. Click Start. After load, find latest PNG in `UserData/RiversRestored/Previews/` (filename embeds seed). Compare. If seeds match → it's RNG drift, not seed reroll. If they don't → there really is a reroll happening somewhere.

### Pangu UX not yet matched
User shared Pangu's UX details and asked to match:
- **No PREVIEW button** — auto-regen on size/biome/seed change
- **Hidden unless Advanced Settings panel is open**
- **Auto-trigger on size slider change, terrain/biome dropdown change, seed input change, randomize button click**

Implementation outline (deferred):
1. Add UI listeners — find Map Size slider, Terrain Type dropdown, Seed Input field, Randomize button via `Resources.FindObjectsOfTypeAll`. Hook each control's `onValueChanged` / `onClick`. Each fires `PreviewGenWorker.TriggerPreview()`.
2. Find Advanced Settings toggle — gate panel visibility on its `isToggledOn`.
3. Remove our PREVIEW button from the panel (no longer needed once auto-trigger works).
4. Debounce — typing a seed character-by-character would otherwise fire many gens.

### Ribbon animation still visible despite EnableRibbonAnimation=false
User reported flowing ribbon visible on rivers in-game even with the pref off. Log confirms RR's Stage 60 invocation IS being skipped (`EnableRibbonAnimation=false — skipping Stage 60 invocation`). What's animating may be FF's vanilla water surface (always-on shader) or persistent ribbon meshes left over from prior sessions. Needs investigation — could be unrelated to RR's pref or a leftover from earlier preview's Sliced_OnGenerated.

### BuildRichCaption sometimes doesn't run
When the gen coroutine times out or hits an exception, the post-gen code path may not execute. Caption stays at the default `"Seed ? · IdyllicValley"` instead of the rich `"Seed XXX · IdyllicValley · Small | Resources: V | Wildlife: V | ..."` format. With current v3 (skip OnGenerated, manual stages with timeouts), this should be more reliable but hasn't been thoroughly tested.

---

## File changes this session

### Modified
- `Patches/MapPreviewRenderer.cs` — major rendering overhaul:
  - 512→768 resolution
  - `BuildBiomeColorMap` (rasterize TerrainArea polygons)
  - `BoxBlurColorMap` (separable 2-pass blur for smoothing)
  - `GetPresetTint` (per-preset color cast)
  - Per-area color jitter, treeDensity darkening
  - Slope-based rocky tint, per-pixel land noise
  - 0.08 contour lines at 20% darken
  - Lambert floor 0.35→0.60
  - `PaintWaterPixel` now opaque (river vs lake tones)
  - Water depth/texture pass (depth gradient, fine+coarse noise, sine ripples, NW bank shadow)
  - Dark-earth shoreline (replaces warm sand halo)
  - Snow-capped border (replaces gray-clamp)
- `Patches/PreviewGenWorker.cs` — major gen-flow rework:
  - `TryReadMapSizeEnum` (reads `SettingsManager.mapSizeValue` directly)
  - `EnsureRuntimeScriptableObjectManagerInitialized` (preflight)
  - Manual stage replication (skip `Sliced_OnGenerated`)
  - `DriveCoroutineWithTimeout` (avoids hangs in nested coroutine waits)
  - `TickUnloadWatchdog` + `IsGameplayContextActive` + `StopWorkerCoroutines` + `UnloadMapScene` (Pangu-pattern cleanup)
- `Patches/PreviewOverlay.cs`:
  - `PANEL_H = PANEL_W + CAPTION_H` (square preview area)
  - Lazy-rebind throttled to 30 frames + gated to Start scene
  - Cached `GraphicRaycaster` ref (no per-frame GetComponent)
  - Canvas + GraphicRaycaster disabled off Start scene (fixes cursor block)
  - `PreviewGenWorker.TickUnloadWatchdog(this)` called every Update

### New
- (none — all changes were modifications to existing files)

### Branch state
- Latest commits since 2026-05-08 handoff cover all of the above
- Branch is **off main**, **not merged**, debug DLL deployed to Mods/

---

## How to ship from here (DEFERRED)

Don't ship until preview-match drift is resolved AND user signs off on the v3 (skip OnGenerated) approach. Visual polish improvements alone are great, but the underlying gen-vs-actual drift is something the user wants nailed first.

If we accept drift as good-enough:
```bash
git checkout main
git merge --no-ff feature/per-preset-sliders
dotnet build -c Release   # rebuilds bin/Release/net46/RiversRestored.dll
# Push Release DLL to Steam Workshop
```

---

## Useful diagnostics

```powershell
# Verify preview gen ran (and which size)
Select-String -Pattern "Wrote preview|MapSize: enum" "G:\SteamLibrary\steamapps\common\Farthest Frontier\Farthest Frontier (Mono)\MelonLoader\Latest.log" | Select-Object -Last 10

# Check whether BuildRichCaption ran (caption was overridden with rich metadata)
Select-String -Pattern "Difficulty raw values|Preview gen complete" "G:\SteamLibrary\steamapps\common\Farthest Frontier\Farthest Frontier (Mono)\MelonLoader\Latest.log" | Select-Object -Last 5

# Find latest preview PNG (filename embeds seed and preset)
Get-ChildItem "$env:USERPROFILE\..\..\..\..\UserData\RiversRestored\Previews\*.png" | Sort-Object LastWriteTime -Descending | Select-Object -First 3
```

---

## Decisions made this session (for future-us)

1. **Polish before perfection.** When preview-match was hard, we pivoted to making the render visually strong so even close-but-not-exact previews look polished. Pangu's render is heightnoise-only and simpler — ours has more visual richness. May trade-off return to Pangu's simpler render style if the visual richness is what's making the drift more visible.

2. **`Sliced_OnGenerated` is forbidden territory.** Running it during preview hangs the actual game's load screen. Pangu apparently runs it without issue, but we couldn't reproduce safe behavior. v3 of our worker explicitly skips it.

3. **Dropped mineral overlay feature.** Minerals aren't part of TG pipeline; would require a separate hook on `MineralManager.Initialize` and standalone preview can't render them at all (doesn't run `InitializeGame`).

4. **Render style is opt-in (kept).** `EnableMapPreviewRender` and `ShowPreviewOverlay` prefs both default OFF. Good for shipping as beta.

5. **Snow border, dark-earth shoreline, opaque water.** All visual decisions made to fix the "water reads as raised plateau" optical illusion. Took several iterations.

---

## Pangu reference points (decompile @ `C:\Users\saged\ClaudeCodeLocalSessions\Pangu_FF.decompiled.cs`)

Key methods to revisit when picking up tomorrow:

- `TryGenerateHeightNoiseForSeedCoroutine` (line ~2352) — top-level coroutine, calls everything
- `EnsureSeedPreviewWorkerReady` (line ~2979) — additive scene load + worker discovery
- `TryCreateSeedPreviewWorkerFromCandidate` (line ~3140) — confirms Pangu uses live TGC, not a duplicate
- `StartSeedPreviewTemplateSceneUnloadIfNeeded` (line ~3204) — when Pangu unloads the Map scene
- `IsGameplayContextActive` (line ~1917) — Pangu's gameplay-active check
- `CancelSeedPreviewBuild` + `UpdateSeedPreviewTemplateSceneOps` (line ~2197, ~2228) — Pangu's cleanup paths
- `StopSeedPreviewWorkerCoroutines` (line ~3159) — what coroutines Pangu stops
- `DisposeSeedPreviewWorker` (line ~3183) — Pangu's full teardown
- `GetSeedPreviewAttemptTimeoutSeconds` (line ~2606) — 8-16s based on map size
- `RenderSeedPreviewTexture` (line ~2930) — Pangu's render (heightnoise-only, 320×320, theme palette)

---

## FF reference points (decompile @ `C:\Users\saged\ClaudeCodeLocalSessions\AssemblyCSharp_Decompiled\Assembly-CSharp.decompiled.cs`)

- `TerrainGeneratorController.Size` enum (line 17526) — Large=0, Medium=1, Small=2 (REVERSED from slider)
- `TerrainGeneratorController.GenSliced_Generate(bool)` (line 17912) — public API entry
- `TerrainGeneratorController.GenerateInternal(bool)` (line 17784) — sets up theme/biomes/mapSettings
- `TerrainGenerator.GenSliced_Generate(bool)` (line 487289) — sliced gen body (the one we replicate manually)
- `TerrainGenerator.GenSliced_GenerateAsync()` (line 486831) — sliced stage runner
- `TerrainGenerator.GenerateAsync()` (line 486929) — synchronous stage runner with all 1..97 stages listed
- `OnMapSizeChanged` (line 288782) — UI slider→enum conversion: `(Size)(2 - num)`
- `StartNewGame` (line 103855) — what FF does when user clicks Start (writes SettingsManager fields, NO reroll)
- `InitializeGame.Sliced_InitializeGame` (line 97988) — gameplay init flow
