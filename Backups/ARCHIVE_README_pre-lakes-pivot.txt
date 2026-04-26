Rivers Restored — Source Archive
================================
Archived: 2026-04-25 (pre-lakes-pivot snapshot)

WHAT THIS IS
------------
Frozen source + DLL state captured immediately before the planned
"lakes-only architectural pivot" — i.e., the moment when:
  - Path B (WaterArea registration) is shipping and working for water
    rendering, fishing, save/reload of the water plane.
  - WaterPath ribbon is still the primary water-display path with a
    sidecar respawn system layered on (BuildTerrainShared03 postfix).
  - The two systems coexist; the ribbon is largely vestigial for water
    visibility but still provides flow animation.

The next session plans to PIVOT to "lakes-only" — delete the sidecar
+ ribbon-respawn complexity, move WaterArea registration earlier
(InjectStage38Postfix) to fix resource-placement-underwater, and
demote the ribbon to a pure cosmetic overlay that doesn't carry any
critical persistence responsibility.

THIS ARCHIVE LETS US REVERT IF THE PIVOT GOES SIDEWAYS.

KNOWN STATE AT ARCHIVE TIME
---------------------------
Working:
  - Stage 38 / Stage 60 sliced-pipeline injection (rivers generate
    on shipped maps).
  - Heightmap carve with banked profile, smoothing passes, soft
    bank protection (0.5m dip floor).
  - Trench depth default 2.0m, outer radius 7 cells (cfg-tunable).
  - WaterArea registration after carve (LateCarvePostfix end).
  - Water plane covers river polygon on gen + reload (FF native).
  - WaterPath ribbon flow animation visible on gen + reload.
  - Heightmap + ControlTextures persist across save/reload.
  - Sidecar binary format v2 (.rivers files), spawn via BTS03 postfix.

Known issues at archive:
  - 3 fishing areas per river (productivity penalty −50%).
  - Yellow shoreline tint on bank edges from FF's natural shoreline
    painting on the WaterArea.
  - Resources (trees, rocks) sometimes placed on river cells (because
    WaterArea is added AFTER resource placement runs).
  - Failed chunking attempt (RiverWaterAreaChunkSize > 0 produces
    overlapping polygons that break water-plane render — REVERTED to 0).

CFG STATE WHEN ARCHIVED
-----------------------
RiversEnabled = true
NumRivers = 4
MinPoints = 15
MarkWaterTypesAsRiverEnd = true
RiverInnerRadius = 3
RiverOuterRadius = 7
RiverJitterAmplitude = 1.5
RiverJitterFrequency = 0.6
RiverSmoothPasses = 4
RiverTrenchDepth = 2.0
RiverRegisterAsWaterArea = true
RiverWaterAreaChunkSize = 0    (chunking disabled — caused water-plane breakage)

CONTENTS
--------
RiversRestored_pre-lakes-pivot_2026-04-25.zip:
  - Plugin.cs
  - Patches/RiverCarver.cs
  - Patches/RiverPersistence.cs
  - Patches/RiverSettingsPatch.cs
  - Patches/RiverWaterAreaBuilder.cs
  - RiversRestored.csproj
  - HANDOFF.md
  - bin/Release/net46/RiversRestored.dll  (the deployed binary at archive time)

RESTORATION
-----------
To revert to this state:
  1. Extract zip into the repo root, overwriting source files.
  2. dotnet build -c Release  (auto-deploys DLL to Mods folder).
  3. In MelonPreferences.cfg, ensure [RiversRestored] section matches
     CFG STATE above (especially RiverWaterAreaChunkSize = 0).
