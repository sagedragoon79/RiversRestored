# RR Handoff — River fishing areas compounding + fish count not scaling

**Date:** 2026-05-21
**Reported via:** WotW debugging session (fishing shacks on rivers under-producing)
**Severity:** High — river fishing shacks starve; lake shacks unaffected

## Symptom

River-adjacent fishing shacks barely produce (one shack: ~76 fish/year vs the
expected 500–600). They cycle the "no fish available" warning marker
constantly. Lake shacks are fine.

The Fishing Resources tooltip on a river shack shows:

```
Fishing Areas:        1520  (+15,120%)
Fish Count:           297   (666,925 sq. meters)
Fishing Productivity: 15220%
```

Every river shack reads the **same 297 fish count + same sq. meters**,
regardless of which shack along the river is selected.

## Root cause

RR's river fishing-area multiplication is **compounding** (re-applying the 8×
multiplier to the already-multiplied list instead of from a cached base).

Evidence from MelonLoader log:
```
[RR][Fish] Multiplied 16 river fishing-area entry(ies) by 8× — list now 211 (was 99)
```
This line fires repeatedly (per load and/or per event). Each pass multiplies
the current count again, so it snowballs:
`99 → 211 → … → 1520` areas (+15,120%).

**Two distinct problems:**

1. **Non-idempotent multiplication.** The 8× transform runs more than once on
   the same list. It should cache the original base count and always compute
   `multiplied = base × 8`, never `current × 8`. (Same class of bug as the
   Tended Wilds "all forageables become blueberry" workaround — a transform
   that must run exactly once is re-running.)

2. **Area inflated, fish population NOT scaled.** Even with the area count at
   1520, the actual fish population is only **297**, shared across every river
   shack. So the river *presents* as a giant fishing ground (huge area /
   15,220% productivity) but contains almost no fish. All river shacks draw
   from the one shared 297-fish pool, deplete it fast → warning marker →
   river replenishes toward ~297 → marker clears briefly → repeat.

   **CONFIRMED (2026-05-21):** 297 is the river's MAXIMUM fish population, not
   just the depleted level. Player stopped all fishing on the river and it
   never replenished above 297. So the fish-capacity ceiling is hard-capped at
   ~297 and is fully decoupled from the (inflated) area count. RR scales the
   area metric but leaves the fish-population CAP untouched — that cap is the
   real constraint starving every river shack. Fixing the compounding alone
   won't help; the fish-capacity ceiling for river areas must scale up too
   (or be set proportional to the intended multiplied area).

## Fix direction

1. **Make the river-area multiplication idempotent.** Cache the base entry
   count (or base list) on first sight; on every subsequent apply, rebuild
   `base × multiplier` rather than multiplying the live (already-multiplied)
   list. Guard with a per-area "already multiplied this session" flag, reset
   only on a true fresh map gen.

2. **Scale fish population alongside area.** Multiplying the *area* without
   multiplying the *fish count* is what starves the shacks. If the intent is
   "rivers fish like 8× the area," the fish stock for those areas needs a
   matching bump, or river shacks will keep depleting a too-small shared pool.

3. **Verify load-path behavior.** On a loaded save, FishingManager.Initialize
   rebuilt 30 areas (28 lakes, 2 rivers) vs 62 (60 lakes, 2 rivers) on a fresh
   map in an earlier session — so the load path also drops ~half the lake
   areas. Confirm whether the compounding and the load-shortfall are the same
   bug or two separate ones.

## Confirmed NOT WotW

Warden of the Wilds only modifies per-shack output multiplier, fishing radius,
Angler/Creeler mode, and storage cap. It does not touch fishing-area counts or
fish population. WotW fishing mechanics verified working in the same log
(`CrabTrap +10/10 fish` deposits, `catch 5 -> 8 (x1.50)` rod catches). The
1520-area / 297-shared-fish behavior is entirely RR's area-multiplication path.
