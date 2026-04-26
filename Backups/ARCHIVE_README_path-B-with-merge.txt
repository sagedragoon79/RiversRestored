Rivers Restored — Source Archive
================================
Archived: 2026-04-26 (Path B + Pangu-style merge + collapsed-blend snapshot)

WHAT THIS IS
------------
Safety-net archive before trying the "extend ribbon endpoints further into
terminating lakes" experiment. If that change makes things worse, this is
the "good enough" working state to revert to.

WHAT WORKS IN THIS BUILD
------------------------
  - Stage 38/60 sliced-pipeline injection (river generation re-enabled)
  - Heightmap trench carve with smoothing + bank protection
  - WaterPath ribbon flow animation (gen + reload via sidecar)
  - WaterArea registration at Stage 38 postfix
    → resources avoid water cells (trees/rocks/animals don't spawn underwater)
    → fishing nodes spawn natively on rivers (FF FishingManager allocates)
  - Pangu-style MergeAndAddWaterArea pass after carve
    → river polygons merge with adjacent lake polygons
    → merged polygon adopts lake's WaterType (saves with map via lakeTypes)
    → eliminates river-lake junction polygon-edge mismatch at gen
  - lakeTypes-borrowed WaterType for unmerged rivers (save/reload friendly)
  - Cp-containment matching for post-reload BuildWaterShared+Rebuild
  - FishingShack/Dock CreateFishingAreas postfix (multiplier hook)
  - FishArea ctor postfix (tags river FishAreas by bounds)
  - Custom struct WaterAreaBoundsKey (no ValueTuple dep, net46-compatible)

KNOWN OPEN ISSUES
-----------------
  - Save/reload water plane on rivers: works for SOME merged polygons but
    not all — some chunks build silently invisible. Workaround: ribbon
    flow animation is still visible inside the carved trench, so rivers
    are still readable as water on reload.
  - Junction seam between river ribbon and terminating lake's water
    plane: small visible gap where they meet. Even with merge, the
    ribbon mesh ends short of where the lake's plane begins.

CFG STATE WHEN ARCHIVED
-----------------------
[RiversRestored]
RiversEnabled = true
NumRivers = 4
MinPoints = 15
MarkWaterTypesAsRiverEnd = true
RiverInnerRadius = 3
RiverOuterRadius = 4    ← collapsed blend zone (was 5/8 default)
RiverJitterAmplitude = 4.0
RiverJitterFrequency = 0.6
RiverSmoothPasses = (user has bumped above default 4)
RiverTrenchDepth = 1.0
RiverRegisterAsWaterArea = true
RiverWaterAreaChunkSize = 0    ← single polygon per river (no over-merging)
RiverFishingAreaMultiplier = 5    ← user bumped from 4

CONTENTS
--------
RiversRestored_path-B-with-merge_2026-04-26.zip:
  - Plugin.cs
  - Patches/RiverCarver.cs
  - Patches/RiverPersistence.cs           (BTS03 postfix, sidecar, ForceWaterPlaneRebuild)
  - Patches/RiverSettingsPatch.cs         (Stage 38/60 injection)
  - Patches/RiverWaterAreaBuilder.cs      (polygon construction, merge logic)
  - Patches/FishingShackPatch.cs          (multiplier hook)
  - RiversRestored.csproj
  - HANDOFF.md
  - bin/Release/net46/RiversRestored.dll  (the deployed binary at archive time)

RESTORATION
-----------
To revert to this state:
  1. Extract zip into the repo root, overwriting source files.
  2. dotnet build -c Release  (auto-deploys DLL to Mods folder).
  3. Cfg values above re-emerge on first run if missing.
