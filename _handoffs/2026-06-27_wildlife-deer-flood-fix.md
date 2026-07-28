# RR Session Handoff — Wildlife bypass: deer-flood ("nodes everywhere") fix

**Status:** code fix made + built in **Debug** + deployed to Mods (`RiversRestored.dll`).
**UNCOMMITTED** in the working tree — per user, let it ride with the **next RR push** (don't push a
lone commit for it). Last release = Workshop v1.5.6; local/GitHub = v1.6.0. This belongs in the next batch.

## The bug
With `RiversDontBlockWildlife` ON, **a uniform, perfectly-spaced grid of deer spawn nodes suddenly
appeared across the WHOLE map** mid-session — small 2–3-deer clusters in every grid cell. Surfaced while
the user was testing Divine Hands (lake/fertility), but it's an RR bug. The tell in the log:

```
[15:16:28] [Rivers_Restored] [RR][Wildlife] Wildlife river bypass: flagged inUse on 1600 spawn area(s),
recomputed 357 empty one(s) — deer can now repopulate cut-off regions.
```

`flagged inUse on 1600` = the ENTIRE `spawnAreaGrid`, not just the river-cut-off ones.

## ★★ Root cause ★★
`Patches/WildlifeRiverPatch.cs` → `TryRepairLoadedSpawnAreas()` (one-shot per scene, gated by
`RiversDontBlockWildlife`, called from OnUpdate once `spawnAreaGrid` is populated).

Its **documented intent** (and the 2026-06-20 handoff) is: repair only the *empty / cut-off* areas —
flag **those** `inUse=true` + recompute **their** spawn points so the daily respawn loop repopulates the
river-walled-off regions. But the code set `inUse=true` on **every** area in the grid *before* the
empty-check; only the *recompute* was scoped to empties:

```csharp
foreach (var area in grid) {
    if (area == null) continue;
    inUseProp?.SetValue(area, true);   // ← BUG: flags ALL 1600, not just cut-off
    touched++;
    var pts = allSpawnPtsField?.GetValue(area) as ICollection;
    if ((pts == null || pts.Count == 0) && calcMI != null) { calcMI.Invoke(area, null); recomputed++; }
}
```

Flagging the whole grid `inUse` made FF's daily respawn loop seed deer into **every** cell → the map-wide
uniform flood. (Why mid-game, not at load: the repair retries until `spawnAreaGrid` has areas, which FF
doesn't build until the deer-spawn system kicks in well into a session — so it sat idle, then fired once
and flagged everything at once.)

## The fix (WildlifeRiverPatch.cs, `TryRepairLoadedSpawnAreas`)
Scope BOTH the `inUse` flag and the recompute to empty (cut-off) areas only; leave already-populated
areas completely untouched:

```csharp
foreach (var area in grid) {
    if (area == null) continue;
    var pts = allSpawnPtsField?.GetValue(area) as ICollection;
    if (pts != null && pts.Count > 0) continue;   // already populated — keep its natural state
    inUseProp?.SetValue(area, true);
    touched++;
    if (calcMI != null) { try { calcMI.Invoke(area, null); recomputed++; } catch { } }
}
```

Now `touched == recomputed == #empties` (e.g. ~357, not 1600). The feature still works for its purpose
(deer trickle into genuinely cut-off regions); no more grid-wide bloom.

## Testing
- ✅ User: "Looks good so far on deer" after staging the fixed DLL (initial in-game check).
- ⏳ Not yet verified across a long play session / the reload-persistence caveat below.

## ⚠ Open caveat — already-flagged saves
The fix prevents *new* over-flagging but never sets `inUse=false`. If `inUse` is serialized AND the user
saved during the buggy run, that save already has all 1600 areas flagged → the flood could persist on
reload of *that* save. Cleanest verification = reload a **pre-bug save** or a **fresh map**.
- If the flood persists after reload with the fixed DLL → `inUse` is serialized; add a **one-time un-flag
  cleanup** (un-flag areas that shouldn't be active). Distinguishing the wrongly-flagged ones after the
  fact is the tricky part — likely "un-flag areas whose spawn points are empty / that fail the real
  path-to-town gate," run once per scene.

## Context (not RR — committed separately)
This surfaced during Divine Hands testing. DH got several unrelated fixes this session (minimap
click-through guard, god-view fog + max-zoom perf cap, fertility brush, soil readout) — all committed +
pushed to the DivineHands repo independently. Only this RR change is intentionally left uncommitted.
