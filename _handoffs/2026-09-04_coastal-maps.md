# Coastal Maps folded into Rivers Restored — v1.7.0 (2026-09-04)

**State:** built and deployed to Mods from branch `coastal-maps` (commit `6a7fa76`, from tag `v1.6.2`).
In-game test pending. Not merged to main, not tagged, not pushed, not on the Workshop.

## What landed

Ported from the standalone Coastal Kingdom prototype (repo `C:\Users\saged\source\repos\CoastalKingdom`,
v0.3.3, proven in game on 2026-09-03/04: ocean classification, save/reload, river to sea). Files:
`Patches/CoastLog.cs`, `CoastPlan.cs`, `CoastCarver.cs`, `CoastPatches.cs`, `CoastPersistence.cs`,
`CoastDiagnostics.cs`, `RiverToSea.cs`; prefs and the `CoastEdge` enum in `Plugin.cs`; one direct call in
`RiverSettingsPatch.InjectStage38Postfix` (after `ApplyRiverFlowBias`, before `_miStage38.Invoke`):
`CoastPatches.ReapplyForRivers(__instance)`; a "Coastal Maps" category in `KeepClarityIntegration.cs`.

Default ON (`CoastalMapsEnabled`), independent of `RiversEnabled`. Prefs use a `Coast` prefix in the
`[RiversRestored]` category; defaults are the user's tuned set (coastline 300 m, beach 40 m, shelf 90 m,
depth 0.5, jitter 90 m per 700 m, Random edge, threshold override on, 1 river drained to the sea).

## Why each piece exists (facts verified in the 2026-06-10 decompile)

- Vanilla's ocean is killed by `WaterType_Ocean.shorelinePoints = 900000`; everything else (materials,
  textures, `DetailCollection_Ocean`, `riverEndPoint`) is populated. The override lowers both ocean thresholds
  to heightmapResolution / 2 every generation (waterSettings is re-read from the save on load).
- Water threshold in heightNoise units is `scaling × waterSettings.height × noiseScaling`; Stage 50 re-derives
  seabed depth through `waterDepth`, so the carve only chooses the curve input.
- The generator REPLAYS every stage on load with per-stage RNG checkpoints; heightNoise and waterAreas are never
  serialised. Hence the `.coast` sidecar (flat `Save/{name}.coast`, seed-checked, same path scheme as `.rivers`).
- In-game orientation: max X displays WEST, max Z displays SOUTH. The RR preview matches on X only.
- RR's flow bias runs after the Stage 37 carve; it can lift or dip the coast. Hence the direct re-carve before
  Stage 38 and the Priority.Last Stage 37 postfix safety re-carve. Strength above ~0.5 floods the low side.
- Vanilla Stage 38 aims rivers only at ABOVE-water perimeter points, so a coast edge was never a target.
  `RiverToSea` retargets the first river's walk to the nearest coastal perimeter point and vetoes below-water
  points that are not the sea (`__result = false`), so vanilla retries; 300-attempt cap.
- `ScaleLakeFish` treats the sea as a huge lake (cells × 1 fish). Left on purpose ("more fish in the sea").

## Test matrix before merge / release

1. New map, rivers ON: `[RR][Coast] Stage 50 (Water): ... 1 classified as ocean`, `River 1 ... ends in the SEA`,
   sea material + sand shoreline in game, sea on the side named by `CoastEdge`.
2. New map, `RiversEnabled = false`: coast still present.
3. Save → reload: `[RR][Coast] Load: coast sidecar ... -> ...`, ocean classified again, no
   `Load seed before stage #5 differs` errors.
4. Load a pre-1.7.0 save: `Load: no coast sidecar ...; leaving the map as saved`, terrain unchanged.
5. Seed preview shows the coast.

## Release steps (fleet convention)

Merge `coastal-maps` → `main`, `dotnet build -c Release -p:Platform=x64`, stage the Release DLL to Mods, tag
`v1.7.0`, `gh release create v1.7.0 <DLL>`, Workshop upload with the CHANGELOG entry as patch notes, refresh the
Steam description (already updated in `STEAM_DESCRIPTION.md`). The standalone `CoastalKingdom.dll` was removed
from Mods on 2026-09-04; never run both.
