Rivers Restored — Source Archive
================================
Archived: 2026-04-26 (Path B + native fishing working snapshot)

WHAT THIS IS
------------
A milestone snapshot of the mod with:
  - WaterArea registration at Stage 38 postfix (fixes resources-underwater)
  - WaterPath ribbon flow animation (gen + reload via sidecar)
  - Heightmap trench carve (gen + persists across save/reload)
  - Native fishing-shoal spawning on rivers (FF FishingManager picks up our
    WaterAreas now that they're added before resource placement)
  - Cp-containment matching for post-reload BuildWaterShared
  - lakeTypes-borrowed WaterType for save/reload friendliness
  - FishingShack/Dock postfix multiplier infrastructure (architecture in
    place, not always firing)

KNOWN LIMITATIONS / OPEN ISSUES
-------------------------------
  - Water plane on RIVERS is not visible after save/reload (lakes work).
    The WaterChunk gets built post-reload but doesn't render — possibly
    a material reference issue on reloaded WaterTypes. The flowing ribbon
    overlay handles the visual on rivers, so it's still playable.
  - Yellow shoreline band visible at river-lake junctions (vanilla FF
    shoreline painting on populated edge[] / shore[]).
  - River-lake junction has a slight cosmetic seam between the river
    polygon's water plane and the lake polygon's water plane. Cosmetic-
    only; gameplay functions.
  - Fishing-area density needs more tuning. The FishingShack hook is
    installed but only the FishArea ctor postfix tags some IDs;
    multiplier sometimes runs, sometimes doesn't. Native FF allocation
    is generally enough for playability though.

CFG STATE WHEN ARCHIVED
-----------------------
[RiversRestored]
RiversEnabled = true
NumRivers = 4
MinPoints = 15
MarkWaterTypesAsRiverEnd = true
RiverInnerRadius = 3
RiverOuterRadius = 5
RiverJitterAmplitude = 1.5
RiverJitterFrequency = 0.6
RiverSmoothPasses = 4
RiverTrenchDepth = 1.0
RiverRegisterAsWaterArea = true
RiverWaterAreaChunkSize = 0
RiverFishingAreaMultiplier = 4

WHAT'S IN THIS ARCHIVE
----------------------
RiversRestored_path-B-fishing-works_2026-04-26.zip:
  - Plugin.cs
  - Patches/RiverCarver.cs
  - Patches/RiverPersistence.cs
  - Patches/RiverSettingsPatch.cs
  - Patches/RiverWaterAreaBuilder.cs
  - Patches/FishingShackPatch.cs
  - RiversRestored.csproj
  - HANDOFF.md (original)
  - bin/Release/net46/RiversRestored.dll  (the deployed binary)

RESTORATION
-----------
To revert to this state:
  1. Extract zip into the repo root, overwriting source files.
  2. dotnet build -c Release  (auto-deploys DLL to Mods folder).
  3. Cfg values above will be re-written on first run if missing.

NEXT STEPS (v0.1.1 polish, not v0.1.0)
--------------------------------------
  1. Fix water-plane render on rivers post-reload (probably waterMaterial
     reference issue — investigate WaterType.waterMaterial state on reload)
  2. Address river-lake junction seam (extend river polygon to overlap
     adjacent lake polygons by 3-5 cells, OR call
     Pangu MergeAndAddWaterArea-equivalent at gen time)
  3. Suppress yellow shoreline at river edges (clone WaterType safely
     OR set shorelineSampleRadius >= 2 + shorelineHeight = 0)
  4. Tune fishing density via the multiplier (verify the postfix is
     firing on placement; current behavior is "click to populate")
