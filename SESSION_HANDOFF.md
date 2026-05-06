# Rivers Restored — Session Handoff

**Last updated:** 2026-05-06
**Current branch:** `main`
**Current Steam-shipped DLL:** `bin/Release/net46/RiversRestored.dll` (143,872 bytes, 16:04)

For deep technical history of how the mod was originally built, see [HANDOFF.md](HANDOFF.md).
For roadmap notes and known design decisions, see [V0_2_PLAN.md](V0_2_PLAN.md).

This file is a **focused current-state summary** so a fresh session can pick up cleanly.

---

## State of the mod

### Working
- Procedural river generation (Voronoi paths, Stage 38/60 injection)
- Heightmap carving + splat painting
- Save persistence: rivers' heightmap geometry survives reload
- Lake water areas: geometry survives reload (vertices/polygons)
- Compatible with Pangu (shares terrain API)

### Known broken (player-impacting)
- **Lake/pond water *visual* missing on save/reload.** Geometry is there (carve channels visible), but the rendered water surface is gone. See `WaterType-orphan` section below.

---

## Today's session changes (2026-05-06)

### Perf wins shipped on `main` (commit `3e83da2`, merge `941f09c`)

Two reflection/scene-scan caches on the per-frame OnUpdate path:

1. **`RiverSettingsPatch.IsLoadingSavedMap`** — caches `FieldInfo` for `_generationData` and `useSavedMap` once per process, with a Type-keyed guard. Was re-resolving both fields every frame.

2. **`RiverCarver.CarveAllRivers`** — caches `AccessTools.TypeByName` results for `TerrainManagerBase`/`Terrain2` plus their `FindObjectOfType` instances. Pre-cache, two scene-wide scans ran every frame for the entire load window (~3,600 redundant scans on a 60s+ load). Instance refs are dropped by `ResetGuard()` so a new save load gets fresh lookups; Type refs persist for the runtime.

### Critical post-merge note

**Do not remove the `IsLoadingSavedMap` check inside `CarveAllRivers`** ([RiverCarver.cs:263](Patches/RiverCarver.cs#L263)). It looks like a duplicate of OnUpdate's gate but is not — `CarveAllRivers` is *also* called directly from `RiverSettingsPatch.LateCarvePostfix`, a Harmony postfix on FF's late-stage terrain methods. Those methods fire during save reload as part of FF's terrain reconstruction. Without the per-call gate the carver runs during reload and overwrites/breaks the saved water state. The original perf-branch attempt removed this check and broke water on reload until restored.

The code now has a fat warning comment. Don't make me regret it.

---

## Open issue: WaterType-orphan on reload

### Symptoms
- Lake water polygons exist on the map after reload (geometry intact)
- But they render as invisible — no blue water surface
- Rivers also exhibit a mild form (always render as green/pond color, never as the intended blue/lake color)

### Diagnosis (confirmed via persist log)
After reload, the persistence layer's `BTS03 postfix` triggers `ForceWaterPlaneRebuild` which rebuilds every water area (e.g., `chunks 0 → 37, built=37`). The rebuild succeeds — but the diagnostic log shows `wt=''` (blank `WaterType`) on every water area.

In FF, water visual style appears to be driven by the assigned `WaterType` ScriptableObject reference. With that reference null/blank on reload, the renderer:
1. Falls back to a depth/area-based auto-classifier
2. If that yields no match (complex polygon, intermediate depth), renders nothing

This matches the visible symptom: **carve channels visible, no water in them.**

### What's been tried (per V0_2_PLAN.md and earlier sessions)
- `lakeTypes`-borrowed approach for RR's *own* river polygons (works for those — they survive reload as green pond water)
- `MarkAllWaterTypesAsRiverEnd` (sets a flag on shared SOs but doesn't solve the orphan issue)

### What hasn't been done
- Extending the `lakeTypes`-borrow / `ResolveRiverWaterType` pattern to **all rebuilt water areas**, including vanilla lake polygons. Right now those go through the rebuild path with no WaterType resolution applied.

### Confirmed not the cause
- Today's perf branch is **not** contributing to this bug. Reproduced identically with the pre-perf master Release DLL.
- Save-side write looks complete (river polygons captured cleanly).
- Issue manifests on the very first reload of a fresh save (no playtime/edit drift component).

### Suggested next investigation
Start in [`RiverWaterAreaBuilder.cs`](Patches/RiverWaterAreaBuilder.cs) — specifically `ResolveRiverWaterType` (~line 614) and `BuildWaterShared` (~line 850). The hooks that call these only target RR-added river polygons. Vanilla lake polygons take a different path during `ForceWaterPlaneRebuild` ([RiverPersistence.cs:1511](Patches/RiverPersistence.cs#L1511)). That second path needs the same WaterType resolution logic — probably "find the WaterType this polygon was using before save, look up by name in `lakeTypes`, reassign on the rebuilt area."

---

## Build / deploy / Steam workflow

Per project convention (recorded in user memory):

- **Branch off** for fixes: `git checkout -b debug/<topic>` or `perf/<topic>`
- **Iterate** with `dotnet build -c Debug` and copy the Debug DLL to `Mods/` for in-game validation. Release folder stays untouched so accidental Steam pushes ship the prior known-good version.
- **Once validated**: commit, merge to `main` with `--no-ff`, push, then **rebuild `-c Release`** so `bin/Release/net46/RiversRestored.dll` matches main. Without this rebuild, the next Steam Workshop push silently ships stale code.
- Steam push artifact: `bin/Release/net46/RiversRestored.dll`. Always.

---

## Repo layout (quick ref)

```
RiversRestored/
├── Plugin.cs                     # Entry, prefs, OnUpdate polling
├── Patches/
│   ├── RiverSettingsPatch.cs     # Stage 38/60 injection + IsLoadingSavedMap
│   ├── RiverCarver.cs            # Heightmap carve + splat paint (cached lookups)
│   ├── RiverPersistence.cs       # Save/load sidecar + ForceWaterPlaneRebuild
│   ├── RiverWaterAreaBuilder.cs  # WaterType resolution for RR rivers (← extend for lakes)
│   └── FishingShackPatch.cs      # Fishing AI compatibility for rivers
├── KeepClarityIntegration.cs     # KC settings panel registration
├── HANDOFF.md                    # Original deep technical handoff
├── V0_2_PLAN.md                  # Roadmap + design decisions
└── SESSION_HANDOFF.md            # ← this file (current state summary)
```

---

## Useful log greps

```
# Did the carver run during reload? (should NOT)
grep "CarveAllRivers:" Latest.log

# Was save-load detected?
grep "save-load detected" Latest.log

# Did the persistence rebuild succeed?
grep "ForceWaterPlaneRebuild\|chunksBefore\|BuildWaterShared" Latest.log

# WaterType orphan check (look for wt='' on rebuilt areas)
grep "WA\[" Latest.log | grep "wt=''"
```
