# RiversRestored — Session Handoff (2026-05-13 → 2026-05-14)

**Repo:** `C:\Users\saged\source\repos\RiversRestored\` → https://github.com/sagedragoon79/RiversRestored
**End state:** v1.4.6 released

Parallel work in a separate thread shipped SeedVault v0.1.0 (P1+P2 partial); see `C:\Users\saged\source\repos\SeedVault\HANDOFF.md`.

---

## Versions shipped this thread
| Version | Date | Type | Key change |
|---|---|---|---|
| v1.4.3 | 05-11 | hotfix | Rivers-off preview hung + never rendered |
| v1.4.4 | 05-13 | hotfix | Lakes had no fish nodes (cachedAreas + merge fixes) |
| v1.4.5 | 05-14 | minor | KC RegisterMod order (earlier, by user — unrelated to this thread) |
| **v1.4.6** | **05-14** | **minor** | **Per-preset WaterMultiplier + preview/gameplay parity** |

---

## Bugs fixed (in order tackled)

### 1. Preview hung when `RiversEnabled = false` (v1.4.3)
**Location:** `Patches/PreviewGenWorker.cs:ConfigureDebugOptions` (line 1179)
**Cause:** `Set("generateRivers", true)` was hard-coded. With rivers disabled, FF's terrain gen entered river stages while RR's patches no-op'd → indefinite hang past timeouts.
**Fix:** `Set("generateRivers", RiversRestoredMod.RiversEnabled?.Value ?? true)` — honor the master toggle so river stages are skipped cleanly.

### 2. Preview never rendered when `RiversEnabled = false` (v1.4.3)
**Location:** `Patches/RiverSettingsPatch.cs:LateCarvePostfix` (around line 185)
**Cause:** Method early-returned on `!RiversEnabled` BEFORE calling `MapPreviewRenderer.TryRender`. With rivers off, overlay stayed stuck on "generating preview" even after gen completed.
**Fix:** Restructured to gate ONLY the carve on `RiversEnabled`. `TryRender` fires unconditionally (modulo `IsLoadingSavedMap` and the later `IsPreviewActive` gate added in v1.4.6).

### 3. Lakes near rivers had no fish nodes (v1.4.4 — merge half)
**Location:** `Patches/RiverWaterAreaBuilder.cs:AddWaterAreaWithPanguMerge`
**Cause:** When a river stamp's bbox + cell-adjacency tolerance reached a vanilla lake, the merge fused them and deleted the standalone lake. The merged `edge[]` outline (thin river disc + lake blob, with a possible 1-cell gap from `padding=1`) wasn't a clean closed loop, so FF's `WaterAreaInfo.area = ClosedPolygonArea(SortAdjacentPoints(edge))` returned near-zero, `GetFishDataForWaterArea` returned 0 fish.
**Fix:** Merge-set selection now requires the candidate to be one of RR's *own* river areas (tracked via `RiverWaterAreaBounds` or river WaterType reference). Vanilla water bodies are never absorbed.
**Side benefit:** Closes the v1.4.1 "lake deleted on save reload" bug class at its gen-time root.

### 4. ALL lakes had no fish nodes globally (v1.4.4 — cachedAreas half) ⭐ Big find
**Location:** `Patches/RiverSettingsPatch.cs:PrepareForStage60` → `InitNullList("cachedAreas")` (line 1289)
**Cause:** RR's heal pass before injecting early Stage 60 set `TerrainGenerator.cachedAreas` to a fresh empty `List<WaterAreaInfo>` as defensive prep against a GridTrace NRE. But FF's `GetAllWaterAreas()` is lazy-cached and **never re-invalidates** (`if (cachedAreas != null) return cachedAreas;`) — so the empty seed stuck for the rest of the session. `FishingManager.Initialize` later iterated that empty list, created zero lake FishAreas. River fishing still worked via the separate `riverInfos` path with hardcoded `area=100000f` — masking how broken lakes were.
**Diagnostic that caught it:** `fishAreas.Count=5 riverInfos.Count=5` (5 rivers + 0 lakes on a 30+ lake map). After fix: `fishAreas.Count=34 (lakes=32, rivers=2)`.
**Fix:** Null out `cachedAreas` after Stage 60 injection completes (in `RiverSettingsPatch.cs` after the try/catch around `_miStage60.Invoke`). The next caller (`FishingManager.Initialize` or any rendering subsystem) triggers a fresh rebuild against the real `_generationData.waterAreas`.
**Permanent diagnostic:** Added `FishingManagerInitializePostfix` in `Patches/FishingShackPatch.cs` that logs `fishAreas.Count=N (lakes=L, rivers=R)` — instant signal if this regresses.

### 5. Spurious preview overlay re-engaging after Start click (v1.4.6)
**Location:** `Patches/MapPreviewRenderer.cs:TryRender`
**Cause:** `LateCarvePostfix` is hooked on Stage 40/50/60/70/97 carriers — those fire during BOTH preview gen AND actual gameplay terrain gen. After HardCancel ran (Start click → unload preview Map scene → load gameplay Map scene), FF's gameplay gen ran the same carriers and `TryRender` fired from a gameplay context. Wrote spurious PNG with `seed=''`, preview overlay re-engaged with progress bar ("preview goes black and starts generating another preview").
**Fix:** Gated `TryRender` on `PreviewGenWorker.IsPreviewActive`. Only fires when the preview pipeline is actively requesting a gen.

### 6. WaterMultiplier broke preview-vs-gameplay terrain parity (v1.4.6)
**Location:** `Patches/WaterValueOverridePatch.cs`
**Cause:** v1 of the patch modified BOTH `SettingsManager.mapWaterValue` AND `tgc.water` on every gen. But only one is actually used per path: gameplay's `if (game)` block reads `SM.mapWaterValue` into `tgc.water` (so modifying tgc.water is wasted); preview's `if (game)` is skipped, so it uses `tgc.water` directly (SM modification has no effect on the gen but DOES affect mid-gen collateral readers — caption builder, diagnostics — which consume CERandom differently, producing different heightmap output).
**Fix:** Asymmetric per-path modification. `game=true` → modify only `SM.mapWaterValue`. `game=false` → modify only `tgc.water`. Finalizer restores whichever was modified.
**Confirmed via control test:** Built a temporary Debug DLL that hard-coded `multiplier=1.0`. With that, terrain matched between preview and gameplay → confirmed the multiplier patch was the divergence source.

### 7. Two preview gens per panel-open with same final seed (v1.4.6)
**Location:** `Patches/PreviewOverlay.cs:HandleAutoRegen`
**Cause:** Advanced Settings panel became visible a few frames before FF wrote the initial seed into the input field. First-show fired with `seed=''`, kicking gen #1 with empty-seed input; seed populated soon after, change-detect fired (`'' → '<seed>'`), kicking gen #2 with the real seed. Both gens used the SAME final seed value but had different RNG state (one fresh, one after gen #1's RNG consumption) → different river-Voronoi outputs. The user saw gen #2's preview but gameplay matched gen #1's RNG state.
**Fix:** Defer first-show until `curSeedText` is non-empty:
```csharp
if (!_wasPanelOpenAndEnabled) {
    if (string.IsNullOrEmpty(curSeedText)) return;  // wait for seed
    ...first-show logic...
}
```
Result: only one gen per panel-open, and the visible preview matches what gameplay generates.

### 8. Panel-visibility flicker triggered spurious soft-restarts (intermediate fix in v1.4.6)
**Location:** `Patches/PreviewOverlay.cs:HandleAutoRegen`
**Cause:** The `IsAdvancedSettingsPanelOpen` cached `CanvasGroup` ref could go stale for up to `PANEL_LOOKUP_INTERVAL_FRAMES = 30` frames during re-resolution. My initial 6-frame hysteresis was way too small. State-reset on those false readings was producing redundant gens.
**Fix:** Distinguish "left Start scene entirely" (reset state) from "panel briefly invisible while on Start scene" (suspend triggering, don't reset state). Pass `onNewGameScreen` separately into `HandleAutoRegen`. Reset only on scene exit.

---

## New feature: per-preset WaterMultiplier (v1.4.6)

**Files:**
- `Patches/WaterValueOverridePatch.cs` (new) — Harmony prefix+finalizer on `TerrainGeneratorController.GenerateInternal(bool)`
- `Plugin.cs` — `WaterMultiplier` added to `RiverPresetValues` struct, `RiverPresetEntries` class, `Presets` dict defaults, `CreatePresetEntries`, `DESC_WATER_MULTIPLIER`
- `KeepClarityIntegration.cs` — slider registration in the Preset · <Name> category

**Per-preset defaults (chosen with the user):**
| Preset | Default | Rationale |
|---|---|---|
| IdyllicValley | 0.75 | Feels oversaturated once rivers are added |
| LowlandLakes | 0.7 | Wettest preset; biggest trim |
| AlpineValleys | 0.85 | Mild trim — alpine drainages are dry-ish |
| AridHighlands | 1.0 | Already dry — no trim |
| Plains | 1.0 | Sparse-water biome — keep lakes alone |

**Range:** 0.3–1.5 via KC slider. Cfg accepts any float.
**Caveat documented in description:** "A given seed will look different on machines with different multipliers — note your value when sharing seeds."
**Slider label includes default**: e.g. `[IdyllicValley] Water Amount Multiplier (default: 0.75)` so users can revert without guessing.

---

## Known issues / punch list

1. **River-path CERandom drift between two consecutive same-seed gens** — pre-existing. Same seed produces different Voronoi outputs on gen #1 vs gen #2. Was visibly hitting users because of the first-show double-gen (fix #7 masks it). Could still surface if the user spam-clicks "Randomize" rapidly. Root cause unknown; suspect RR's preview-side prep (heal pass, scene wrangling) consumes RNG asymmetrically vs gameplay's path before Stage 38 fires. Would need RNG-call logging at Stage 38 entry to localize.
2. **~10% lake variation between preview and gameplay** — borderline cells flip differently due to floating-point ordering in the flood-fill. Acceptable for v1; user confirmed.
3. **MelonLoader 0.7.0-beta** — Pangu_FF.dll fails to register silently. Doesn't affect us unless we want to test Pangu coexistence paths.

---

## DLC prepatch discovery (2026-05-14)

Assembly-CSharp.dll was rebuilt today (16:09) — the Pets DLC prepatch landed. Checked critical signatures: **GenerateInternal, Stage 37, Stage 50, TGC class** all byte-identical. Our Harmony hooks attach correctly post-DLC. Inspector defaults and AnimationCurve assets could still have changed (those don't show as code diffs) but we have no evidence anything broke. Current decomp at `C:\Users\saged\ClaudeCodeLocalSessions\ff_full_dlc.cs` (16.3MB, from 19:22 today). Old decomp at `ff_full.cs`.

---

## Diagnostic / process notes worth keeping

- **`FishingManager.Initialize` observability hook** (added v1.4.4, refined v1.4.5/.6): Logs `fishAreas.Count=N (lakes=L, rivers=R)` at end of init. Future regressions show up immediately as `lakes=0`.
- **`[RR][WaterValue]` log lines per gen path**: `preview: tgc.water X→Y` vs `gameplay: SM.mapWaterValue X→Y`. Per-path identification at a glance.
- **`[RR][PreviewOverlay] AutoRegen` log lines**: `first-show`, `change detected`, `debounce fired`, `left Start scene`. Lets you trace every trigger cause in the log.
- **Control-test technique**: For "is X feature causing this?" questions, hard-code the feature to a no-op in Debug build, leave Release alone. Used this to isolate WaterMultiplier as the terrain-divergence culprit in one launch.
- **Convention reaffirmed (memory: `feedback_debug_builds.md`)**: All iterative dev builds use `-c Debug -p:Platform=x64`. Release only on explicit ship request.

---

## Files modified this thread

```
Plugin.cs                                  — version bump to 1.4.6 + WaterMultiplier struct/defaults/entries
RiversRestored.csproj                      — version bump to 1.4.6
CHANGELOG.md                               — v1.4.3 / v1.4.4 / v1.4.6 entries
KeepClarityIntegration.cs                  — WaterMultiplier KC slider registration
Patches/RiverSettingsPatch.cs              — LateCarvePostfix restructure + cachedAreas null-out
Patches/RiverWaterAreaBuilder.cs           — merge-set restricted to RR's own rivers
Patches/MapPreviewRenderer.cs              — IsPreviewActive gate on TryRender
Patches/PreviewGenWorker.cs                — generateRivers honors RiversEnabled
Patches/PreviewOverlay.cs                  — HandleAutoRegen redesign (defer first-show, scene-aware reset, no flicker hysteresis)
Patches/FishingShackPatch.cs               — FishingManager.Initialize observability postfix
Patches/WaterValueOverridePatch.cs (new)   — per-preset WaterMultiplier hook
```

---

## Reference paths

- RR repo: `C:\Users\saged\source\repos\RiversRestored\`
- RR GitHub: https://github.com/sagedragoon79/RiversRestored
- v1.4.6 release: https://github.com/sagedragoon79/RiversRestored/releases/tag/v1.4.6
- MelonLoader log: `G:\SteamLibrary\steamapps\common\Farthest Frontier\Farthest Frontier (Mono)\MelonLoader\Latest.log`
- Mods folder: `G:\SteamLibrary\steamapps\common\Farthest Frontier\Farthest Frontier (Mono)\Mods\`
- Current decomp (post-DLC): `C:\Users\saged\ClaudeCodeLocalSessions\ff_full_dlc.cs` (16.3MB)
- Old decomp (pre-DLC): `C:\Users\saged\ClaudeCodeLocalSessions\ff_full.cs` (16.2MB)
- Pangu decomp: `C:\Users\saged\ClaudeCodeLocalSessions\pangu_decomp.cs` (11199 lines)

---

## Parallel work in other thread

SeedVault v0.1.0 — P1 (skeleton + DB) ✅, P2 partial (in-game F8 overlay) ⚠️ outstanding "cursor spin on save" bug. Full details: `C:\Users\saged\source\repos\SeedVault\HANDOFF.md`. Note from constellation update in `MEMORY.md`: Divine Hands (planned) overlaps with SeedVault on 4 of 10 candidate features — resolve before scaffolding either.
