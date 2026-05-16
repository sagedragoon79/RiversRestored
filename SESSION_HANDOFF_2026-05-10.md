# Rivers Restored — Session Handoff (2026-05-10)

**Last updated:** 2026-05-10 (end of long session — Pangu preview integration shipped + two hotfixes)
**Current branch:** `main` (feature work merged + released)
**Released versions:** v1.4.0, v1.4.1 (hotfix), v1.4.2 (hotfix) — all tagged + GitHub-released
**Steam-shipped DLL:** Workshop entry still on **v1.4.0** as of this write; v1.4.2 awaits Workshop publish by the user.

This handoff consolidates the entire preview-integration arc and follow-up. Prior handoffs:
- [SESSION_HANDOFF_2026-05-08.md](SESSION_HANDOFF_2026-05-08.md) — visual polish on the preview render
- [SESSION_HANDOFF_2026-05-09.md](SESSION_HANDOFF_2026-05-09.md) — initial Pangu integration attempts (v3 manual-stage replication, the misdiagnosed OnGenerated-skip approach)

This doc supersedes the "open issues" section of the 2026-05-09 handoff. Everything in it is resolved or has a known disposition.

---

## TL;DR

**The Pangu preview integration is shipped and working.** Three releases over two days got us there:

- **v1.4.0** (2026-05-09) — Full Pangu-canonical preview gen, auto-regen UX, progress bar, per-preset tuning, KC settings, filename metadata, auto-prune.
- **v1.4.1** (2026-05-09) — Hotfix: tightened lake-absorb criterion so independent lakes near rivers don't lose their fish nodes on save load.
- **v1.4.2** (2026-05-10) — Hotfix: stop writing a preview PNG on every save load.

The integration was a long, fix-iterate cycle. Each session attempt produced a new failure mode that exposed a misdiagnosis from the prior one. The misdiagnosis chain matters because it informs how to debug the next layer of issues:

1. v3 thought `Sliced_OnGenerated` itself caused the gameplay-load hang — actually, the hang was the polluted Map scene staying loaded when FF began its gameplay scene transition.
2. After landing the StartNewGame prefix + scene-init discriminator, the next hang was *still* present — because RR's own Stage 38 / RiverCarver / ForceWaterPlaneRebuild hooks fired during preview gen, mutating the live Map scene with 1.5M-cell heightmap edits and dozens of orphan terrain refs. Unity's automatic asset cleanup walked them all → 3-minute stall.
3. After the `IsPreviewActive` gate landed, the 3-min stall *still* hit because the polling-loop hard-timeout flipped `IsPreviewActive=false` while `tg.generating` was still true. RR's hooks then fired post-timeout on the still-live Map scene.
4. After fixing the timeout to keep the gate held, the reroll button showed a 30-60 s frozen progress bar because `StopWorkerCoroutines` killed the gen mid-flight without flipping `tg.generating` — polling loop hit the 60s hard ceiling every reroll.
5. Independent lakes near rivers vanished on save load because the absorb pass treated "lake center inside river bbox" as proof of merge duplication. Wrong — center can land inside without the lake being a duplicate.
6. Preview PNGs were saved on every save load because the renderer hooked the late-stage carriers FF rebuilds run.

Each of those is now fixed and shipped.

---

## Architecture — what the preview path actually does

### Trigger flow

1. User opens New Game UI → opens Advanced Settings panel.
2. `PreviewOverlay.Update` detects `(onNewGameScreen && prefOn && advancedOpen)` becomes true.
3. `HandleAutoRegen` polls per frame:
   - `SettingsManager.mapSizeValue` (property, not field — see "key bugs" below)
   - Seed input field text via cached `TMP_InputField` ref
   - Skips polling while the input field has focus (= user typing)
4. Any change resets a 300 ms debounce timer. When the timer expires, calls `PreviewGenWorker.TriggerPreviewSoftRestart`.
5. If a gen is already in flight, the soft-restart sets `_cancelled = true` + `StopWorkerCoroutines` and queues `_pendingRestart`. Running coroutine's polling loop sees `_cancelled` and bails next frame; finally re-fires `TriggerPreview`.

### Gen flow

`PreviewGenWorker.PreviewCoroutine`:
1. Find or load FF's "Map" scene additively. On first preview this triggers an async LoadSceneAsync; subsequent previews reuse the existing scene.
2. Find the `TerrainGenerator` + `TerrainGeneratorController` in the Map scene roots.
3. Read seed from FF's input field, decode via `SettingsManager.SeedToSettings` (`terrainSeed + themeID + mountainValue + waterValue`).
4. Read map size from `SettingsManager.mapSizeValue` (property!), resolve theme via `GlobalAssets.mapTypeData.GetMapThemeFromID`, read `SettingsManager.Instance.mapType` + `mapLakeValue` (properties).
5. `EnsureRuntimeScriptableObjectManagerInitialized` — Pangu preflight; without this `GenerateInternal` NREs.
6. Apply all params to TGC (terrainType, seed, size, theme, mountains, water, lakes).
7. Configure `tg.debugOptions` to skip roads/trees/details/objects (Pangu's exact set).
8. `IsPreviewActive = true` — RR's mutation patches gate on this.
9. Invoke `tgc.GenSliced_Generate(false)`; drive it briefly via `DriveCoroutineWithTimeout` (~1 step), then poll `tg.generating` for actual completion. Hard 60 s ceiling.
10. If `_cancelled` becomes true, bail immediately.
11. On graceful completion: `genCompletedGracefully = true`, finally clears `IsPreviewActive`.
12. `BuildRichCaption` fires (or already fired mid-gen the moment `MapPreviewRenderer.RenderedThisGen` flipped).

The actual rendering happens earlier, mid-pipeline, from `RiverSettingsPatch.LateCarvePostfix → MapPreviewRenderer.TryRender`. That's the SAME hook point FF uses for in-game gen, which is why the in-game render and the preview match exactly (same pipeline phase captured).

### Cleanup flow

On Start click:
1. `StartNewGamePatch` Harmony prefix on `StartSceneManager.StartNewGame(string, string)` fires.
2. `PreviewGenWorker.HardCancel(unload: true)` runs.
3. `StopWorkerCoroutines` stops all coroutines on Map-scene roots.
4. `IsPreviewActive` stays held — RR's patches still gate during the cleanup window.
5. `UnloadMapScene` coroutine kicks off; awaits the async unload op.
6. After unload completes, `UnloadMapScene` clears `IsPreviewActive`, `_mapSceneLoadedByUs`, `_sceneInitPending`.
7. FF's gameplay flow then `LoadSceneAsync("Map", Additive)` for a fresh Map scene. No collision with our preview's mutated state.

`OnSceneWasInitialized("Map")` discriminator in `Plugin.cs`:
- If `_sceneInitPending` (we loaded it) → just clear the flag.
- Else if there's still preview state to clean up → `HardCancel(unload: false)` (FF owns the new scene; just stop coroutines).
- Otherwise no-op. **Critical**: an earlier version always called HardCancel here, which stopped FF's brand-new gameplay-gen coroutines and hung the load — that's why the predicate now uses `HasActivePreviewState()` instead of "is preview overlay alive?"

---

## Key bugs fixed (with root cause for each)

### 1. SettingsManager property-vs-field gotcha
Three values RR reads from SettingsManager are exposed as **static properties** with `_value`-prefixed backing fields, not as fields directly:

| Public name | Backing field | What |
|---|---|---|
| `mapSizeValue` (static prop) | `_mapSizeValue` | Map size enum |
| `mapType` (instance prop) | `_mapType` | Terrain type |
| `mapLakeValue` (static prop) | `_mapLakeValue` | Lake density |

`GetField("mapSizeValue")` returns null; we silently fell through to default Medium. Pangu doesn't hit this because Pangu's code is compiled C# (`SettingsManager.mapSizeValue` resolves to the property at compile time). Our reflection path needed the explicit property lookup.

Fix in `PreviewGenWorker.TryReadMapSizeEnum`, `ApplyTgcGenParameters`, `PreviewOverlay.ReadStaticProp` — read property first, fall back to underscored backing field.

### 2. 3+ minute load stall after Start click
Root cause: RR's `RiverCarver` / `ForceWaterPlaneRebuild` were firing during preview gen and mutating the live Map scene. On unload, Unity's automatic asset cleanup had to walk millions of orphan refs.

Fix: `PreviewGenWorker.IsPreviewActive` gate.
- `RiverCarver.CarveAllRivers` early-returns when set.
- `RiverPersistence.ForceWaterPlaneRebuild` early-returns when set.
- `RiverWaterAreaBuilder.BuildAndAddForAllRivers` is **NOT** gated — it only adds to `_generationData.waterAreas` (data, not scene state). Preview render needs that.

Critical: the gate stays held through scene unload. Cleared inside `UnloadMapScene` after the async op completes, not in `HardCancel` itself. Without this, mid-flight late-stage hooks would fire on the soon-to-be-unloaded Map scene.

### 3. Polling loop hard-timeout flipped gate prematurely
Symptom: 16-60 s of carve-during-preview after the polling loop "timed out."

Cause: `tg.generating` stays true forever when the gen coroutines are killed mid-flight. Old code: `IsPreviewActive = false` in `finally`. New code: track `genCompletedGracefully`; only clear gate when gracefully done. If we bail via timeout, the gate stays set until the next `TriggerPreview` or `HardCancel`.

### 4. Soft-restart 30-60 s frozen bar
Symptom: rerolling the seed showed the progress bar frozen at whatever % the previous gen was at, for up to 60s, before the new gen visibly started.

Cause: `StopWorkerCoroutines` killed the gen mid-flight; `tg.generating` stayed true; polling loop waited the full 60 s hard ceiling. Plus: stage-progress reader was still seeing the old `_generationData.stage` value at, e.g., 62/97.

Fix: polling loop checks `_cancelled` first and bails immediately. Progress bar has stall detection — if determinate value doesn't advance for 1.5 s, falls through to indeterminate slider animation.

### 5. Auto-regen didn't fire on reroll button
Symptom: clicking the seed reroll dice didn't trigger a new preview.

Cause: I tried polling `SettingsManager.mapTerrainSeedValue` (a static int). The UI's `RerollMap` ([Assembly-CSharp.cs:289327](Assembly-CSharp.decompiled.cs:289327)) writes the new seed string to the input field via `SetTextWithoutNotify` but **does NOT** update `mapTerrainSeedValue` until StartNewGame fires. So polling the int saw no change.

Fix: poll the input field's `.text` directly via a cached `TMP_InputField` ref. Lookup throttled to 30 frames when cold; per-frame is a single property accessor on the cached ref.

### 6. `Resources.FindObjectsOfTypeAll<TMP_InputField>()` per-frame GC stall
Symptom: clicking reroll caused "not responding" Windows dialog for 5+ seconds.

Cause: the input-field lookup was running every frame, allocating large arrays. Combined with the gen's own allocations, Mono's heap filled up and GC stalls compounded.

Fix: caching the input-field ref (see fix 5). Polling the cached ref is allocation-free.

### 7. Independent lakes deleted on save load
Symptom: a medium-sized lake near a river had no fish nodes after a save load, even after moving the shack's work-area radius.

Cause: `RiverWaterAreaBuilder.AddPrebuiltWaterAreas` ran an "absorb" pass to remove FF-regenerated duplicate lakes (lakes that were merged into a river at gen time and shouldn't be there on load). The criterion was "lake's center cell inside river polygon's mask." Too loose — when a river snakes near or around an independent lake, the lake's center can land inside the river's bbox without the lake being a merge duplicate.

Fix: require the lake's **full bbox** to fit inside the river's bbox, AND every sampled corner + center of the lake to land on a filled cell of the river mask. Plus a log line for every absorption so future bugs are diagnosable.

**Recovery for existing affected saves:** if the user saved after loading with v1.4.0, the disk save now lacks the lake data too. Best path is a backup save from before the v1.4.0 load. Workaround: Pangu's lake-creation feature can spawn new fishable spots.

### 8. Preview PNG written on every save load
Symptom: the `Previews/` folder grew with rivers-less PNGs every time the user loaded their save.

Cause: `MapPreviewRenderer.TryRender` was called from `LateCarvePostfix` unconditionally. That postfix hooks late-stage carriers that FF re-runs on every save load. The render captured a pre-river-overlay state (RR's carver short-circuits on save load via `RestorePending` / `RestoredThisLoad` guards).

Fix: `IsLoadingSavedMap(tg)` guard at the call site. Save loads no longer trigger renders. New game gen + auto-regen previews still fire.

### 9. Two-image-per-gen mystery
Symptom: each preview produced two visibly different PNGs — image 1 from `LateCarvePostfix` mid-pipeline, image 2 from `PreviewCoroutine` post-completion. Image 2 was the outlier.

Cause: `Sliced_OnGenerated` mutates `_generationData.heightNoise` post-carve (terrain smoothing, lake bed adjustments). The post-completion render captured the post-mutation state. The mid-pipeline render captured the pre-mutation state, which matches what FF's in-game render produces at the same hook point.

Fix: removed the post-completion render. One image per gen, matches in-game gen exactly.

### 10. Caption populated 15-20 s after image
Symptom: image rendered, then a long delay before captions appeared.

Cause: `BuildRichCaption` fired in `PreviewCoroutine`'s finally, after the polling loop completed.

Fix: fire `BuildRichCaption` (and set `CaptionReady = true`) the moment `MapPreviewRenderer.RenderedThisGen` flips true mid-gen. Belt-and-braces re-fire at the end stays in place.

### 11. Old preview flashed on panel re-open
Symptom: returning to New Game UI after a gameplay session briefly showed the previous preview before the new gen started.

Cause: `CaptionReady` and `LatestPreview` were static and persisted. Panel showed them on first frame of visibility.

Fix: on the panel-open transition (false→true), `PreviewGenWorker.ResetCaptionReady()` flips the flag to false so the panel shows the progress bar until the new gen completes.

### 12. Gameplay 2-second tick stutter
Symptom: a periodic ~2 s stutter in gameplay.

Cause: `StartScenarioHotkey.Tick` (the Town Center Confirm hotkey I added earlier) was scanning `Resources.FindObjectsOfTypeAll<Button>()` every 30 frames. Large allocation per cycle, GC stutter.

Fix: removed `StartScenarioHotkey.cs` from RR entirely. The Town Center confirm hotkey is being handled by Keep Clarity instead (per consolidation feedback). The hotkey was originally added as a workaround for a cursor-not-rendering issue on the Town Center dialog; KC fixed the underlying cursor issue (a setting that was skipping the new-game intro video).

---

## What's left on the plate

Spot-checked, working as of this session end:
- Map size mismatch ✓
- Auto-regen on size change, reroll, terrain type ✓
- Custom map type doesn't auto-regen (acceptable; Custom = manual seed, which catches via typing detection)
- Progress bar with stall fallback ✓
- Caption timing ✓
- Filename metadata ✓
- Auto-prune to 25 ✓
- Save load times: Small ~45s, Medium ~1:10, Large ~1:51 — back to "normal" range ✓
- Save load lake-absorb fix (v1.4.1) ✓
- Save load preview PNG suppression (v1.4.2) ✓

Open / deferred:

- **Pangu+RR vs RR-only performance comparison** — Never ran the controlled test (`EnableMapPreviewRender=false`, preview via Pangu, click Start). Would tell us whether the gameplay load times are intrinsic to FF gen + RR hooks or whether RR's preview path adds cost. Lower priority since current load times are acceptable.
- **Custom map type auto-regen** — Marked acceptable. If users want it, would need to poll the UI class's mapType field directly (it's a `SettingsManager.MapType`, distinct from `SettingsManager.Instance.mapType` which is `TerrainGeneratorController.TerrainType`).
- **Steam Workshop publish** — Still on the user. Workshop `_FF.dll` is at v1.4.0 as of this writing. v1.4.2 needs to be uploaded.
- **Existing affected saves** — Users who played on v1.4.0 and saved after a load may have permanently lost lake data. CHANGELOG documents this; no programmatic fix possible without the original lake data in the save.
- **Future seed share bank** — Design notes in `Knowledge/FF-Modding-Knowledge/project-seed-bank/design-notes.md`. Filename metadata in v1.4.0 is the producer-side preparation; the bank-tool reader + PNG tEXt chunk writer are future work.

---

## How to ship from here

To publish v1.4.2 to Steam Workshop:
1. Upload `releases/RiversRestored-v1.4.2.zip` (or `bin/Release/net46/RiversRestored.dll` directly) to the Workshop entry.
2. Update the Workshop description from `STEAM_DESCRIPTION.md`.

To start work on v1.5.x or a feature branch:
1. Branch off main.
2. Reference this handoff for architecture decisions; reference `CHANGELOG.md` for shipped features.
3. The preview integration is stable — heavy refactoring is unlikely to be needed. Adding features (per-preset more controls, share-bank reader, etc.) is the natural next direction.

---

## File changes this session (cumulative across v1.4.0 → v1.4.2)

### Modified
- `Patches/MapPreviewRenderer.cs` — biome-color render, 768×768 output, hillshade/contours/snow border, water depth/texture, filename metadata, auto-prune to 25 PNGs.
- `Patches/PreviewGenWorker.cs` — full Pangu-canonical gen with proper polling, `IsPreviewActive` gate held through unload, soft-restart with cancel-bail, progress reader, caption-ready signal.
- `Patches/PreviewOverlay.cs` — auto-regen polling, Advanced Settings panel-open gate, progress bar with stall fallback, cached input field ref.
- `Patches/RiverCarver.cs` — `IsPreviewActive` gate at top of `CarveAllRivers`; log-once-per-cycle.
- `Patches/RiverPersistence.cs` — `IsPreviewActive` gate at top of `ForceWaterPlaneRebuild`.
- `Patches/RiverWaterAreaBuilder.cs` — tightened absorb criterion (full bbox + mask sampling).
- `Patches/RiverSettingsPatch.cs` — `IsLoadingSavedMap` guard on the preview render call.
- `Plugin.cs` — `OnSceneWasInitialized` discriminator, `StartNewGamePatch.Apply` registration, version strings.
- `RiversRestored.csproj` — version 1.3.0 → 1.4.2.
- `CHANGELOG.md` — new file documenting all v1.4.x versions.
- `STEAM_DESCRIPTION.md` — added preview-overlay section, per-biome tuning section, KC integration note, updated changelog block.

### Added
- `Patches/StartNewGamePatch.cs` — Harmony prefix on `StartSceneManager.StartNewGame`.
- `releases/RiversRestored-v1.4.1.zip`, `releases/RiversRestored-v1.4.2.zip`.

### Removed
- `Patches/StartScenarioHotkey.cs` — Town Center confirm hotkey (KC now owns this).

### Documentation
- `SESSION_HANDOFF_2026-05-09.md` — initial integration session.
- This file (`SESSION_HANDOFF_2026-05-10.md`) — consolidated handoff.
- `Knowledge/FF-Modding-Knowledge/project-seed-bank/design-notes.md` — future share-bank design.
- `Knowledge/FF-Modding-Knowledge/mod-patterns/melonloader-deploy-while-running.md` — deploy-pattern reference.

---

## Useful diagnostics

```powershell
# Verify the deployed DLL version
$dll = "G:\SteamLibrary\steamapps\common\Farthest Frontier\Farthest Frontier (Mono)\Mods\RiversRestored.dll"
Select-String -Pattern "1\.[0-9]\.[0-9]" $dll -Encoding byte | Select-Object -First 5

# Watch for preview-gen activity
Select-String -Pattern "RR\]\[PreviewGen|RR\]\[Carve|RR\]\[WA|RR\]\[Persist" `
  "G:\SteamLibrary\steamapps\common\Farthest Frontier\Farthest Frontier (Mono)\MelonLoader\Latest.log" `
  | Select-Object -Last 50

# Find latest preview PNG
Get-ChildItem "$env:USERPROFILE\..\..\..\..\UserData\RiversRestored\Previews\*.png" `
  | Sort-Object LastWriteTime -Descending `
  | Select-Object -First 5

# Verify auto-prune is keeping the cap
(Get-ChildItem "$env:USERPROFILE\..\..\..\..\UserData\RiversRestored\Previews\*.png").Count
```

---

## Key reference points

### RR codebase
- [PreviewGenWorker.cs](Patches/PreviewGenWorker.cs) — start here for preview gen
- [PreviewOverlay.cs](Patches/PreviewOverlay.cs) — auto-regen UI logic
- [MapPreviewRenderer.cs](Patches/MapPreviewRenderer.cs) — biome render + filename metadata
- [RiverWaterAreaBuilder.cs `AddPrebuiltWaterAreas`](Patches/RiverWaterAreaBuilder.cs) — load-time water area rebuild, contains the absorb logic

### Pangu decompile (`C:\Users\saged\ClaudeCodeLocalSessions\Pangu_FF.decompiled.cs`)
- `TryGenerateHeightNoiseForSeedCoroutine` (line ~2352)
- `EnsureSeedPreviewWorkerReady` (line ~2979)
- `TryCreateSeedPreviewWorkerFromCandidate` (line ~3140) — confirms Pangu uses live TGC
- `IsGameplayContextActive` (line ~1917)
- `CancelSeedPreviewBuild` (line ~2197) — multi-trigger cleanup pattern
- `RenderSeedPreviewTexture` (line ~2930) — heightnoise-only render

### FF decompile (`C:\Users\saged\ClaudeCodeLocalSessions\AssemblyCSharp_Decompiled\Assembly-CSharp.decompiled.cs`)
- `SettingsManager.mapSizeValue` property + `_mapSizeValue` field (line 100401, 100845)
- `SettingsManager.mapType` property + `_mapType` field (line 100377, 100701)
- `SettingsManager.mapLakeValue` property + `_mapLakeValue` field (line 100391, 100785)
- `SettingsManager.mapTerrainSeedValue` (line 100383, 100737) — int, only updated at StartNewGame
- `SettingsManager.SeedToSettings` (line 101659) — decodes seed string
- `UIStartMenu_NewSettlement.RerollMap` (line 289327) — writes input field text only
- `StartSceneManager.StartNewGame(string, string)` (line 103855) — Harmony prefix target

---

## Decisions made this session (for future-us)

1. **Run the full gen pipeline, gate RR's own mutations.** Earlier v3 belief that `Sliced_OnGenerated` itself caused the hang was wrong. The right pattern: let FF's full gen run, but make RR's mutation hooks stand down during preview via `IsPreviewActive`.

2. **`IsPreviewActive` is sticky on cleanup paths.** Anywhere that tears down the worker (HardCancel, polling timeout), keep the gate held until the scene is actually gone. Race conditions otherwise.

3. **Poll canonical static state, not GameObject scans.** `Resources.FindObjectsOfTypeAll` per frame is the easy GC-pressure trap. Cache refs aggressively.

4. **Property vs field is the silent killer in reflection paths.** When a value isn't behaving, check the decompile for property-vs-field. Public static *properties* with underscored backing fields are common in FF code.

5. **One render per gen at one consistent pipeline phase.** `LateCarvePostfix` is the matched phase between preview and in-game gen. Don't double-render.

6. **The seed share bank is a deferred future project.** Filename metadata in v1.4.0 is producer-side prep. Reader/writer + bank tool are a separate scope when the time comes.

7. **Consolidate UI hotkeys into KC, not RR.** RR is for rivers. The Town Center hotkey was scope creep; it's gone from RR and KC owns it.

8. **Hotfix the moment a regression hits a user's save.** v1.4.1 went out same-day for the lake-absorb bug; v1.4.2 the next day for the save-load PNG spam. Both small enough to be cleanly hotfixed and shipped.
