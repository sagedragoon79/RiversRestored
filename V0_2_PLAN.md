# RiversRestored v0.2 — Architecture Plan

Date: 2026-04-26
Predecessor: `RiversRestored_path-B-with-merge_2026-04-26.zip` (v0.1.0 archive)

---

## 1. Where we tripped

Looking back, four decisions compounded into the save/reload mess:

1. **One long-thin polygon per river.** We rasterized cp-to-cp with `BresenhamMarkDisc` into a single `WaterArea` per river. FF's `WaterArea` serialization + `BuildWaterShared` + `WaterChunk.Rebuild` round-trip handles lake-shaped polygons fine, but something about ours (aspect ratio? minX/maxX span vs. point density?) makes some chunks render invisibly on cold reload. Pangu's manually-painted thin rivers — which ARE the same final shape — survive reload because they're built from many small overlapping blob stamps that get merged transitively. The merged polygon is mathematically the same, but built differently, and the save/load path apparently cares.

2. **We chased the yellow band before the water plane was stable.** Setting `shorelineSampleRadius = 0` on a cloned WaterType triggered an infinite loop in `BuildShoreline` (`for (l += radius / 2)` with radius 0). Reverting that broke save/reload. We oscillated between visual cleanliness and persistence instead of locking persistence first.

3. **We extended the ribbon endpoints to fix a cosmetic seam.** That pushed cps into adjacent lakes, which got transitively merged into a super-polygon, which broke reload worse than before. The seam is a symptom of the polygon-shape problem, not a cause.

4. **We trusted that mimicking vanilla `TerrainRiver` would Just Work.** Vanilla never finished this codepath. The serialization, fish-spawning, and water-plane build code were tuned for lakes; our extended use stressed undocumented assumptions.

---

## 2. What stays (battle-tested, keep verbatim)

These are isolated, working pieces. v0.2 inherits them as-is:

- **`RiverSettingsPatch`** — Stage 37/38/60 sliced-pipeline injection. Took weeks to dial in.
- **`RiverPersistence`** — sidecar binary v2 (cp positions only), `BuildTerrainShared03` postfix timing, `ResetForSceneLoad`.
- **`FishingShackPatch`** — `FishArea` ctor tagging + `CreateFishingAreas` postfix multiplier. Clean win.
- **`RiverCarver`** — heightmap trench carve with smoothing + bank protection. The physical river bed shape is correct.
- **`WaterPath` ribbon spawn** — flow animation MeshFilter+Renderer. Independent of water-plane survival; always visible inside the carved trench.
- **`lakeTypes`-borrowed `WaterType`** — solves the orphan-SO problem on save/reload. Don't go back to `Resources.FindObjectsOfTypeAll`.
- **Custom `WaterAreaBoundsKey` + `CellCoord` structs** — net46 needed these.

---

## 3. What changes (the pivot)

**Replace `RiverWaterAreaBuilder.BuildAndAddForAllRivers` with a Pangu-style blob-stamp builder.**

### Current (v0.1) algorithm
```
for each river:
    rasterize entire cp-to-cp path with BresenhamMarkDisc into one cell mask
    extract one polygon (points[,], edge[], shore[])
    add as one WaterArea
    [later] merge adjacent lakes into it
```

### v0.2 algorithm
```
for each river:
    walk interpolated cp path at RiverBlobStride cells (default 3)
    at each step, stamp ONE disc polygon of radius RiverBlobRadius cells (default 3)
        each blob is its own WaterArea, added immediately
        each add goes through Pangu-style merge-with-padding
    when a stamp overlaps an existing lake polygon, merge happens automatically
        merged polygon adopts lake's WaterType (already working in v0.1)
    where two stamps overlap, they merge into the river's running polygon
```

Default `RiverBlobRadius=3` matches `RiverInnerRadius` so the polygon fills the
full-depth trench bottom exactly; the blend zone (`RiverOuterRadius=4`) slopes
up the bank dry, leaving a clean visual edge. Default `RiverBlobStride=3` gives
heavy stamp overlap — smooth merged outline, no gaps even on sharp curves.

### Why this should work
- Each blob is small (~InnerRadius cells), lake-shaped (squarish bbox), low aspect ratio.
- Pangu has empirically proven this exact shape persists across save/reload.
- Junction with terminating lake is handled naturally by merge — no special "extend ribbon endpoints" hack needed.
- The final merged polygon IS the same shape as v0.1's rasterized polygon, but constructed via a path the save/load code is happy with.

### Code locality
The change is one file: `RiverWaterAreaBuilder.cs`. Specifically `BuildAndAddForAllRivers`. The merge function we already have (`MergeRiverWaterAreasWithAdjacent`) is reused — we just call it after each stamp instead of once at the end.

`RiverCpCells` and `RiverWaterAreaBounds` tracking stays the same — we update them per stamp.

---

## 4. v0.2 generation flow (full picture)

```
Stage 37 carrier postfix (RiverSettingsPatch)
    └─ invokes Stage 38
        └─ TerrainRivers built (vanilla)
        └─ ExtendEndPoints (vanilla, 150m)
        └─ RiverWaterAreaBuilder.BuildAndAddForAllRivers  ← REWRITTEN
            └─ for each river, walk cps and stamp blobs
                └─ each stamp: AddWaterAreaWithPanguMerge(...)
                    └─ small disc polygon
                    └─ merge with overlapping existing waterAreas (1-cell padding)
                    └─ adopt lakeTypes[0] WaterType for unmerged stamps
            └─ track all merged polygon ids in RiverWaterAreaBounds
        └─ RiverCarver.CarveAllRivers (heightmap trench)
        └─ ribbon WaterPath spawn (unchanged)
            (note: no MergeRiverWaterAreasWithAdjacent post-pass — merge happens inline)

Stage 60 carrier postfix
    └─ resources spawn (already avoid water cells via our Stage 38 registration)

Save: native FF serialization (waterAreas list saved with map)
    + sidecar (cp positions only, for ribbon respawn)

Load:
    BuildTerrainShared03 postfix
        └─ restore sidecar cps
        └─ RiverWaterAreaBuilder.PopulateBoundsFromSidecar (re-rasterize bbox tracking)
        └─ ribbon respawn from cps
        └─ ForceWaterPlaneRebuild (BuildWaterShared + WaterChunk.Rebuild per area)
            ← should now succeed for all areas because shapes are lake-friendly
```

---

## 5. New API: `AddWaterAreaWithPanguMerge`

Single entry point we call per blob stamp. Does what `MergeAndAddWaterArea` does in vanilla, plus adopts a sensible WaterType:

```csharp
// pseudocode
private static int AddWaterAreaWithPanguMerge(
    TerrainGenerator gen,
    int cellMinX, int cellMinZ, int cellMaxX, int cellMaxZ,
    bool[,] cellMask,
    WaterType riverFallbackType)
{
    var areas = gen._generationData.waterAreas;

    // 1. find any existing area whose bbox overlaps our stamp + 1 cell padding
    // 2. transitively expand the merge set (Pangu does this — we already have logic in MergeRiverWaterAreasWithAdjacent)
    // 3. union the cellMasks
    // 4. extract the unioned polygon (points/edge/shore)
    // 5. preserve the merged areas' WaterType priority: lake > river-fallback
    // 6. remove the merged areas from the list, add the new merged area
    // 7. return new index, update RiverWaterAreaBounds
}
```

Most of this exists already — we extract from `MergeRiverWaterAreasWithAdjacent` and call it from inside the stamp loop instead of at the end.

---

## 6. Yellow band — defer

Don't touch `shorelineSampleRadius` in v0.2. Get persistence rock-solid first. v0.3 can revisit:
- Option A: post-gen, mutate the `WaterType.shoreline*` arrays to taper to 0 alpha at ribbon edge.
- Option B: tinted shore texture matching river bed.
- Option C: accept it (vanilla FF lakes have it too).

---

## 7. New cfg keys for v0.2

Locked in:
- **`RiverBlobRadius` (default 3 cells).** Disc stamp radius. Polygon fills the full-depth trench when this matches `RiverInnerRadius`.
- **`RiverBlobStride` (default 3 cells).** Spacing between stamps along the cp path. Heavy overlap at default; raise for fewer stamps (faster gen, risk gaps on tight curves).

Removed:
- **`RiverWaterAreaChunkSize`** — gone. Per-stamp granularity replaces it. Caused the over-merge bug in v0.1.

Optional (cheap wins, decide later):
- **`RiverEdgeToEdgePreferred` cfg option.** When true, mark only border-ocean cells as valid `riverEndPoint`s before vanilla `TerrainRivers.Build` runs. Sidesteps the lake-junction problem on maps where it'd otherwise generate awkward terminations. ~20 lines of patch code.
- **Diagnostic dump on Ctrl+Shift+R.** Prints all waterAreas with bbox + WaterType + isOurs flag. Useful for debugging "where did the polygon go on reload."

---

## 8. Out of scope — push to v0.3+

- In-mod paint UI (river touch-ups, force-fishable, carve-deeper).
- Custom river shore-graphics (yellow-band fix).
- Branching rivers / tributaries.
- Ribbon-extension into terminating lakes.
- Per-river WaterType selection (RiverFlowing vs RiverStill etc).

---

## 9. Migration path

1. **Branch.** `git checkout -b v0.2-blob-stamps` (after `git init` if needed — repo currently isn't versioned).
2. **Tag v0.1 archive.** Drop `Backups/RiversRestored_path-B-with-merge_2026-04-26.zip` reference into a `v0.1.0` git tag for easy revert.
3. **Delete aggressively in branch:** ribbon-extension code in `RiverSettingsPatch`, the chunked-overlap experiment, any commented-out cruft. Keep the diff focused on the polygon-construction pivot.
4. **Implement `AddWaterAreaWithPanguMerge`.** Refactor `MergeRiverWaterAreasWithAdjacent` body into a reusable helper that takes a single cell mask + bbox.
5. **Rewrite `BuildAndAddForAllRivers`** to walk-and-stamp.
6. **Test loop:** gen → save → reload → verify water plane visible on every river → verify fish nodes spawn → verify junction with terminating lake.
7. **Iterate `RiverBlobStride`** until merged polygon looks right.
8. **Ship v0.2.0** when reload-water survives 5+ random seeds without invisible chunks.

---

## 10. Locked decisions (2026-04-26)

1. **Stamp shape: disc.** Pangu-faithful. Polygon shape is independent of riverbank carve smoothing (`RiverCarver` stays untouched), so this is purely about merged-polygon outline.
2. **Stamp size: `RiverBlobRadius = 3` cells (= `RiverInnerRadius` default).** Fills full-depth trench; ribbon stays inside with margin; squarish ~7×7 bbox is lake-friendly to the save/load path.
3. **Stamp stride: `RiverBlobStride = 3` cells.** Heavy overlap, smooth merged outline. Tune up if gen feels slow.
4. **Delete `RiverWaterAreaChunkSize` cfg.** Redundant with per-stamp granularity; was a footgun.
5. **Delete the post-pass `MergeRiverWaterAreasWithAdjacent` call.** Merge happens inline per stamp via `AddWaterAreaWithPanguMerge`. Refactor the function body into a reusable helper that powers the inline merge.
