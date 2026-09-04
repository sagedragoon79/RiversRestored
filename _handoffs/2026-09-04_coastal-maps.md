# Coastal Maps folded into Rivers Restored — v1.7.0 / Map Edge — v1.8.0 (2026-09-04)

**State:** v1.7.0 passed the full test matrix in game, was fast-forwarded to `main` and tagged `v1.7.0`
(`5e702e4`). v1.8.0 "Map Edge" (`6bd4fd9`) adds `BorderRingScale` (0.5) and `PlayableInset` (50 m), both on by
default; it passed its Small-map test matrix on 2026-09-04 and is tagged `v1.8.0`, Release build deployed to
Mods (18:07). Nothing is pushed or on the Workshop yet: `git push --tags`, `gh release create v1.8.0
bin/Release/RiversRestored.dll`, then the Workshop upload with the two CHANGELOG entries (1.7.0 + 1.8.0) as
patch notes and the refreshed STEAM_DESCRIPTION.md.

## v1.8.0 — what landed

- `Patches/MapEdgePatches.cs`: prefix on `PreGameInitializer.GetPathingGridRect` (public static, decompile
  100761) writes the private static `navMeshBuffer` (100690, vanilla 300f = 150 m per edge) from the current
  plan on every call. Consumers of the rect: camera clamp (`size.x/2 − 55`, 60602), NavMesh surface bounds
  (100797), AI grid width (100805, reads the static directly after BuildNavMesh), `MineralManager.Start`
  (160811), `ForagingManager.OnGameInitializeEvent` (89712), tree/detail exclusion (490772+). A loading save
  (`useSavedMap`) takes the inset from its sidecar or vanilla; anything else takes the pref.
- Ring scale lives in the border mirror (`CoastPatches.RunBorderRing`): stamp size and height × scale, 0 = no
  ring, same RNG calls per placed stamp. The mirror now runs whenever the plan is non-null (coast OR ring ≠ 1
  OR inset ≠ 150), skipping stamps only on the coast edge when there is one.
- `CoastPlan` gained `HasCoast`, `RingScale`, `PlayableInset`; sidecar v2 writes them; v1 files (1.7.0 and
  the Coastal Kingdom prototype) read as `coast=true, ring=1, inset=150`, i.e. exactly how they were generated.
- Known soft spot: `MineralManager.Start` runs before the save's `useSavedMap` flag is set, so on a loaded save
  its one rect read uses the pref value. Cosmetic (mineral placement bounds).

## v1.8.0 test matrix

1. Small map, new game: half-size edge hills, buildable to ~50 m from the edge, camera travels closer, log
   `[RR][Coast] Playable inset set to 50 m per edge` and `Border ring: N features stamped at scale 0.50`.
2. Save → reload: same grid (buildings at the edge still reachable), same ring.
3. Load a 1.7.0 coast save and a pre-1.7.0 save: `Playable inset set to 150 m`, vanilla ring, untouched.
4. Rivers on with a coast: river to sea still works; RR flow bias still re-carved before Stage 38.
5. Watch a raid or trader arrival: spawns now sit ~50 m from buildable land.

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
