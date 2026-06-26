# RR Session Handoff — River fish per-cell sizing + wildlife-across-rivers

**Status:** staged in **Debug** (deployed to Mods), **NOT released**. Last release = v1.5.6.
Next release should bundle this whole batch once the reload test below passes.

## What's in this batch (post-v1.5.6, unreleased)

1. **River fish per-cell sizing** — `FishingShackPatch.FishingManagerInitializePostfix` scales each
   river's **polygon** FishArea: `maxFish = max(vanillaCurve, filledCells × RiverFishPerCell) × FishingAreaMultiplier`,
   and tops `fishCount` to the cap (full-restock-on-load). Cells popcounted at `FishAreaCtorPostfix`
   into `RiverFishAreaCells` (faId → cells).
   - New cfg **`RiverFishPerCell`** (default **1.0** ≈ 1 fish per water cell; calibrated from real
     saves: a bisecting river ≈ its cell count). Recommend users keep **multiplier = 1**.
2. **Wildlife across rivers** — `WildlifeRiverPatch` + cfg **`RiversDontBlockWildlife`** (default OFF).
   Scoped bypass of the path-to-town gate inside `AnimalSpawnArea.IsValidForSpawnOrWanderPoint`
   (flag-gated prefix on it + a prefix on `AIPathfinder.DoesGeneralPathExistToTown`). Plus
   `TryRepairLoadedSpawnAreas()` (OnUpdate one-shot) for existing saves: sets `inUse=true` +
   recomputes empty spawn areas. **VERIFIED on fresh gen** — deer on all 3 river-cut sections.
   Trapping intentionally NOT bypassed (its gate protects hunters from unreachable traps).
3. **Preview-timeout message** — `PreviewGenWorker.PreviewTimedOut` + `PreviewOverlay` shows
   "Map Preview Timed Out — You can still start the game though!" instead of an infinite spinner
   on slow/Proton gens. Untested (Linux-only path).

## Verified
- ✅ Fresh gen: river fish sized per-river (e.g. 8,954 / 7,531 at perCell 1, mult 1).
- ✅ Fresh gen: wildlife-across-rivers (deer in all cut-off sections, toggle on).
- ✅ Gameplay: fishers extract from the scaled pool; `MonthlyFishGrowth` regrows to it (decomp-confirmed).

## ★★ ROOT CAUSE FOUND & FIXED (next day) — Load() discards the scaling ★★
The shack reads the **isRiver** FishArea (below). But scaling it at FishingManager.Initialize
postfix did NOT stick on reloaded saves: `FishingManager.Load(ES2Reader)` (ff_full_dlc.cs:153210)
runs AFTER Initialize and does `fishAreas = reader.ReadDictionary<int,FishArea>()` — it REPLACES
the entire dict with the SAVED objects, discarding the Initialize-time scaling. (That's also why an
old save that was SAVED while scaled showed the scaled value — ES2 restored it; a save made pre-fix
restores the vanilla ~600/1590.)

**FIX (implemented in FishingShackPatch.cs):**
- `BuildRiverPolyDescriptors(fishAreas)` at Initialize-postfix captures per-river polygon world-bbox
  + popcount cells into `_riverPolys` (geometry is identical across Init and Load — RR's stamped
  WaterAreas persist).
- `ScaleRiverFishAreas(dict, phase)` scales isRiver areas (nearest polygon by world bbox) + the
  polygon (exact bbox match; lakes skip via `RiverBboxMatchEpsilon`). Overwrites maxFish/fishCount
  UNCONDITIONALLY (fixes vanilla-low AND stale-high).
- Called at **both** Initialize-postfix (fresh gen) **and a NEW `FishingManager.Load` postfix**
  (`FishingManagerLoadPostfix`) — the latter re-scales the final saved dict the shack reads.
- Verify in log: `[Load] FishingManager.Load done — re-scaling…` + `[Load] River FishArea id=0
  (isRiver→polyNN): maxFish 1590 → ~10000`. Built Debug, awaiting in-game confirm.

## ★ DECISIVE FINDING (F10 dump, 21:06) — shack reads the isRiver area ★
The shack panel reads the **isRiver** FishArea, NOT the polygon. F10 dump:
```
[Panel] areas=11 :: id=0(fc=16485,mf=16485) | id=0 | … (EVERY entry id=0)
  ⇒ mostFish.id=0, DISPLAYED = GetMaxFish(0) = 16485   (RiverFishAreaIds=[12,13])
fishAreas[0]/[1]: maxFish=16485 isRiver=True   ← shack reads these
fishAreas[10]/[11]: 8954 / 7531 isRiver=False  ← RR was scaling THESE (never read)
```
So **RR has been scaling the wrong FishArea for the display all along.** The shack enrolls the
river via FF's river path → `riverInfo.id` (the isRiver/Bounds FishArea, id=0/1), not the
WaterArea polygon (id=10/11). `RiverFishAreaCells` is keyed by the **polygon** faIds → the
polygon-only scaling (current build) never touches what the shack reads.

Also: **id=0 is stale at 16485** in this save (serialized while the buggy summed-isRiver build was
active; the isRiver Bounds FishArea evidently DID persist here). Current build stopped scaling
isRiver, so it no longer overwrites that stale value → it lingers on pre-fix saves.

### CORRECT FIX (next session)
Scale the **isRiver** FishAreas (id=0/1 — what the shack reads), each sized to **its OWN river**
(not the sum that caused the combined-value bug, not the polygon). Need to map each isRiver area
→ its river's cell count:
- isRiver FishArea carries bounds from `riverInfo.bounds` (WORLD coords); polygon cells are in
  CELL coords. Match by spatial overlap (convert one space to the other via hmRes/mapW), OR by
  river-creation order (FindRivers riverInfo order vs RR's `_generationData.rivers` order).
- Also OVERWRITE stale isRiver values unconditionally (don't `if (newMax <= origMax) continue`
  past a stale-high value — or it sticks on old saves). Simplest: set isRiver maxFish/fishCount =
  its river's `cells × RiverFishPerCell × multiplier`, replacing whatever's there.
- Alternative (Pangu force-enroll): re-point the shack's id=0 entries to the polygon id in
  `CreateFishingAreasPostfix` so it reads the already-correct polygon — but that ALSO needs the
  isRiver→polygon match, so scaling the isRiver directly is simpler.

Keep scaling the polygons too (harmless; gameplay/other readers may use them).

## THE PENDING TEST (superseded by the decisive finding above — kept for record)
The **reload** fish-count display was showing the **combined total of all rivers** on multi-river maps.
Root cause: the old code ALSO scaled the **isRiver** FishAreas (id=0/id=1, from FF's `FindRivers`
WaterPath path) sized off `riverCellsTotal` = SUM of all rivers' cells → each isRiver = combined,
and on reload the shack's `GetFishingAreaWithMostFish` picked the isRiver over each river's polygon.

**Fix applied (latest Debug build):** scale **ONLY** the per-river polygon FishAreas
(`RiverFishAreaCells`), **never** the isRiver areas. With the isRiver left vanilla (~1590), each
river's polygon (8,954/7,531) outranks it in the most-fish pick → panel reads per-river on reload too.

**To verify:** restart FF → reload a multi-river save → each river shack should read **its own**
count (not the combined). Log should show only polygon ids scaled (e.g. `id=12 → 8954`,
`id=13 → 7531`), **no `id=0`/`id=1` lines**.

**If it still reads wrong on reload** (e.g. a lake ~100, meaning the polygon isn't winning/enrolled):
implement the **Pangu force-enroll** — mirror `AugmentFishingAreasForShack`
(`Pangu_FF.decompiled.cs:9883-9990`, key line 9975): in `CreateFishingAreasPostfix`, add a shack
`FishingArea` entry pointing at the river polygon's FishArea id via `GetIdFromWaterAreaId(waterArea.id)`
with the real `waterArea.area`, so the shack reliably reads (and fishes) the scaled per-river polygon.

## Gen-vs-reload asymmetry (explained, for context)
Both isRiver and polygon FishAreas exist on gen AND reload. The display differed only by which one
the shack's `GetFishingAreaWithMostFish` picked: gen → polygon (per-river), reload → isRiver
(combined). The fix removes the combined value at the source so the pick no longer matters.

## DIAGNOSTICS TO STRIP BEFORE RELEASE
Staged by the investigation (in `FishingShackPatch.cs`): a `UpdateText` postfix (`UpdateTextPostfix`)
+ F10 `DumpFishingState` that log `[RR][Fish] [Panel]` / `FISHING STATE DUMP`. Remove (or gate behind
VerboseDiagnostics) before shipping. The biome dumper + path-to-town probe (Ctrl+Shift+F9) already
shipped in v1.5.6 (VerboseDiagnostics-gated — fine to keep).

## Decomp reference (FF fishing display)
Panel "Fish Count" = `UIFishingProductivitySubWidget.UpdateText` → `GetMaxFish(GetFishingAreaWithMostFish().id)`
(ff_full_dlc.cs:244444). `GetFishingAreaWithMostFish` picks by live `fishCount` (342648-342716).
Two river FishAreas: WaterAreaInfo-ctor polygon (isRiver=false, RR scales) + Bounds-ctor isRiver=true
(FindRivers, RR no longer scales). Full analysis: knowledge doc `game-systems/river-system.md`.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
