# Rivers Restored — Session Handoff (2026-05-08)

**Last updated:** 2026-05-08 (end of long session — preview overlay built standalone)
**Current branch:** `feature/per-preset-sliders`
**Steam-shipped DLL:** `bin/Release/net46/RiversRestored.dll` — **unchanged this session**, safe

This is a focused handoff covering what shipped during this session.
For deeper history see [SESSION_HANDOFF.md](SESSION_HANDOFF.md) and
[HANDOFF.md](HANDOFF.md).

---

## TL;DR

Built a full **in-game map preview overlay** that runs standalone (no
Pangu dependency required). Shows topographic map render with rivers,
lakes, hillshading, and a 3-column caption with seed/biome/size and
all 4 difficulty selections. Plus added **per-preset slider tuning**
(every preset now gets its own copy of all 13 settings) and
auto-scaling of `NumRivers`/`MinPoints` by map size.

Branch is **not merged to main**. Awaiting user decision on shipping.

---

## What landed this session (all on `feature/per-preset-sliders`)

### 1. Per-preset slider tuning ✅
Each non-Custom preset (IdyllicValley, LowlandLakes, AridHighlands,
Plains, AlpineValleys) now gets its own MelonPreferences category
with all 13 tunable fields. Defaults seed from the existing hardcoded
`Presets` dictionary; user can tune live without code edits.

Categories use **underscore** naming (`RiversRestored_IdyllicValley`,
etc.) — period-named categories don't persist to cfg in MelonLoader 0.7.0.

`GetEffectiveValues()` reads from these entries, falling back to the
hardcoded table if entries fail to load.

### 2. NumRivers/MinPoints auto-scale by map size ✅
At gen time, `GetEffectiveValues()` reads FF's current `mapSettings.size`
enum and scales:
- Small × 0.7
- Medium × 1.0 (baseline)
- Large × 1.3

So preset values are tuned for Medium; Small/Large get proportional
adjustments automatically.

### 3. PondUseLakeMaterial — blue water everywhere ✅
New pref (default ON). Steals every `material`-named field from
`WaterType_LakeSmall` and copies onto `WaterType_Pond` at gen time.
Pond classification still fires normally, but the visual material is
LakeSmall's — every Pond renders blue. Side-effect: also fixes the
**save/reload WaterType-orphan bug** (orphaned areas fall back to Pond's
now-blue material instead of rendering invisible).

### 4. Map preview renderer (the big one) ✅
Renders the gen output as a PNG to `UserData/RiversRestored/Previews/`.

**Render features:**
- Heightmap relief with hillshading (NW sun, 45° altitude)
- Topographic color ramp (dark green → tan → brown → white peaks)
- Percentile contrast (5th–95th) so map edges don't wash out the rest
- Water polygon raster (curved rivers, real lake shapes — not bbox rects)
- Mineral overlay scaffold (currently always 0 — minerals don't spawn
  by Stage 50, would need a later hook)
- Both axes inverted to match FF in-game minimap orientation
- Block-fill on upscale (Medium maps no longer show water as a grid)
- 512×512 output with seed/preset/river-count/water% in filename

**Gates:** `EnableMapPreviewRender` pref (default OFF, opt-in).

### 5. In-game preview overlay panel ✅
UGUI Canvas overlaid on the New Game screen at the right side.

**Visual:**
- **Border:** `BTN_Border02_UP` sprite (same one FF uses for main-menu
  Continue/Load/Exit buttons — corner ornaments baked into the 9-slice)
- Shadow: `IMG_BGShadowThickSoft01`
- Backdrop: dark semi-transparent fill
- Caption font: `Andada-Bold SDF` (FF's UI font)

**Layout:**
- 425×425 panel, anchored 8px from right edge, mid-vertical
- Caption strip (48px tall) split into 3 columns:
  - Left (60%): `Seed XYZ · Biome · Size` / `N river(s) · N% water`
  - Mid (20%): `Resources: T` / `Wildlife: T`
  - Right (20%): `Maladies: T` / `Raiders: T`
- "PREVIEW" button in top-right corner of panel

**Toggles:**
- `ShowPreviewOverlay` pref
- F8 hotkey
- Empty state shows "Click PREVIEW to generate" with dimmed preview area

### 6. PreviewGenWorker — standalone preview gen ✅
Triggers a full FF gen pipeline on demand, no Pangu required.

Decompiled Pangu's source (`C:\Users\saged\ClaudeCodeLocalSessions\Pangu_FF.decompiled.cs`)
and replicated its 9-step pattern via reflection:

1. Load `Map` scene additively (gives us a properly-configured TerrainGenerator + Controller)
2. Find TG/TGC in scene roots
3. Decode seed string via `SettingsManager.SeedToSettings` (extracts terrainSeed + themeId + mountains + water)
4. Resolve theme via `GlobalAssets.mapTypeData.GetMapThemeFromID`
5. Read live map size from UGUI Slider (NOT from `SettingsManager.mapSizeValue` which is stale)
6. Apply all gen params to TGC (terrainType, seed, size, theme, mountains, water, lakes)
7. Set `isLoaded=false`, configure `debugOptions` (skip roads/trees/details)
8. Run `TGC.GenerateInternal(false)` → `TG.ResetGeneratedData()` → seed RNG → `TG.PreGenerate()` → fresh `_generationData` → `TG.GenerateAsync()`
9. Render and build rich caption

**Important quirks discovered:**
- `SettingsManager.mapType`, `mapSizeValue`, and difficulty fields are
  **only updated when user clicks Start** — they're stale on the New
  Game screen. We read live UI values where possible (slider for size,
  seed string for biome).
- **Wildlife/Raiders difficulty enum is offset +1 from UI label** —
  Pioneer button = `Difficulty.Normal`, not `Easy`. Fix: `-1` offset
  applied in `ReadDifficulty` for `animalDifficultyValue` and
  `raiderDifficultyValue` only.

---

## File changes

### New files
- `Patches/MapPreviewRenderer.cs` — heightmap + water polygon raster, PNG output
- `Patches/PreviewOverlay.cs` — UGUI Canvas panel, F8 toggle, caption columns
- `Patches/PreviewGenWorker.cs` — standalone gen trigger via Map-scene-load + Pangu pattern

### Modified files
- `Plugin.cs` — per-preset entries, auto-scale, PondUseLakeMaterial pref, ShowPreviewOverlay/EnableMapPreviewRender prefs
- `KeepClarityIntegration.cs` — registers all new prefs with KC settings UI
- `Patches/RiverSettingsPatch.cs` — PondUseLakeMaterial swap logic, PondVsLake diagnostic dump, MapPreviewRenderer.RenderedThisGen reset, hooks LateCarvePostfix → MapPreviewRenderer.TryRender
- `RiversRestored.csproj` — added refs: ImageConversionModule, IMGUIModule, InputLegacyModule, TextRenderingModule, UnityEngine.UI, UnityEngine.UIModule, Unity.TextMeshPro

### Branch state
- 30+ commits on `feature/per-preset-sliders` covering all the above
- Last commit: `bc82413` (Caption: Wildlife/Raiders offset + live map size)
- Branch is **off main**, **not merged**, debug DLL deployed to Mods

---

## Open issues / known limitations

### Mineral overlay always 0
`DrawMineralOverlay` runs but `MineralManager.minerals` is empty at our
render hook (LateCarvePostfix on Stage 60). Resources spawn at Stage
70+. Would need a later render hook, OR a separate render pass after
resource stage runs. Currently always logs `minerals=0`.

### Pre-existing WaterType-orphan bug (partially mitigated)
`PondUseLakeMaterial=true` sidesteps the visual symptom (orphaned
WaterTypes fall back to Pond's now-blue material), but the underlying
classification bug isn't fixed. Lakes still classify as Pond on reload.
For the deep technical history see [SESSION_HANDOFF.md](SESSION_HANDOFF.md)
"Open issue: WaterType-orphan on reload" section.

### Caption layout still not perfect
60/20/20 column split. User had several iterations on this. Caption is
working but may need column-width tweaks if labels truncate at certain
font sizes/resolutions.

### "Auto Mode" toggling
User toggled in/out of auto mode several times during the session.
Most edits were in auto mode but plan mode was used for clarification
on caption layout. Plan files at `~/.claude/plans/*.md`.

---

## How to ship from here

```
git checkout main
git merge --no-ff feature/per-preset-sliders
dotnet build -c Release   # rebuilds bin/Release/net46/RiversRestored.dll
# Push Release DLL to Steam Workshop
```

**Recommendation:** test the per-preset slider tuning workflow once
more before merge — it's the user-facing feature most likely to have
edge cases. The preview overlay is opt-in (default OFF) so it can ship
as a beta feature without risk.

---

## Useful diagnostic greps

```bash
# Verify standalone preview gen ran cleanly
grep "PreviewGen" Latest.log | tail -20

# Verify pond material swap fired
grep "PondSwap" Latest.log

# Verify per-preset categories appeared in cfg
grep "^\[RiversRestored_" UserData/MelonPreferences.cfg

# Find latest preview PNG
ls -t UserData/RiversRestored/Previews/*.png | head -3
```

---

## Decisions made this session (for future-us)

1. **Standalone over soft-Pangu-dep.** User explicitly chose to bake
   in Pangu's worker pattern (~600 lines of reflection code) rather
   than recommend Pangu as a dependency. Reasoning: "if we'd require
   Pangu anyway, might as well code it in."

2. **Per-preset sliders, not per-size sub-presets.** Each preset has
   its own copy of all 13 fields. Combined with map-size auto-scale
   (×0.7 / ×1.0 / ×1.3), this covers the "different settings per size"
   need without 65 + per-size variant prefs.

3. **PondUseLakeMaterial default ON.** It fixes the save/reload water
   visibility regression as a side effect. The cost (no green pond
   variety) was deemed acceptable.

4. **Preview render is opt-in.** Two prefs: `EnableMapPreviewRender`
   (writes PNG) and `ShowPreviewOverlay` (in-game panel). Both default
   OFF. Ship as beta.

5. **Caption uses single-letter difficulty abbreviations** (P/T/V/X)
   to fit narrow columns in the 425px panel.

6. **Map biome auto-match deferred to KC.** User decided KC mod manager
   is the right place to wire "RR preset → FF terrain selector" — keeps
   RR focused on gen, KC focused on cross-mod UX.

---

## Decompiled Pangu reference

Saved at `C:\Users\saged\ClaudeCodeLocalSessions\Pangu_FF.decompiled.cs`
(11,197 lines). Used extensively for replicating the standalone preview
gen. Key sections:
- `EnsureSeedPreviewWorkerReady` (line ~2979) — additive scene load + worker discovery
- `TryGenerateHeightNoiseForSeed` (line ~2804) — full 9-step gen sequence
- `TryDecodeMapSeed` (line ~2728) — `SettingsManager.SeedToSettings` usage
- `RenderSeedPreviewTexture` (line ~2944) — palette + render math (we did our own, didn't copy this)

If revisiting standalone preview, this file is the canonical reference.
