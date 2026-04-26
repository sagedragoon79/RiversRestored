# Rivers Restored — Technical Handoff

A Farthest Frontier (Crate Entertainment, Unity Mono build) MelonLoader mod
that re-enables the developer-shelved river generation system.

**Status:** Functional generation, persistent carved heightmap + splat textures,
**unsolved problem: water visual disappears on save/reload.**

---

## 1. Mod Purpose & Premise

Farthest Frontier ships with a complete river generation system in
`Assembly-CSharp.dll` (TerrainGen.* namespace) that doesn't fire on shipped
maps. Crate publicly stated rivers were shelved and may return as DLC.

This mod:
1. Hooks the terrain generation pipeline to enable river generation
2. Manually carves heightmap trenches along the generated river paths
3. Paints riverbed splat textures
4. Persists rivers across save/load (partially — see Section 7)

**Built using:** MelonLoader 0.7.0, HarmonyLib, .NET Framework 4.6, C# 8 nullable.

---

## 2. Repository Layout

```
C:\Users\saged\source\repos\RiversRestored\
├── Plugin.cs                          # Mod entry, MelonPreferences, OnUpdate polling
├── RiversRestored.csproj              # net46, Assembly-CSharp + UnityEngine.TerrainModule refs
├── Patches\
│   ├── RiverSettingsPatch.cs          # Harmony hooks for Stage 38 + 60 injection
│   ├── RiverCarver.cs                 # Manual carve via Pangu API + smoothing passes
│   └── RiverPersistence.cs            # Sidecar (.rivers) save/load + WaterPath spawn
└── HANDOFF.md                         # this document
```

**Build:** `dotnet build -c Release` — auto-deploys to
`G:\SteamLibrary\steamapps\common\Farthest Frontier\Farthest Frontier (Mono)\Mods\RiversRestored.dll`
via post-build Copy target in csproj.

**Logs:** `G:\SteamLibrary\steamapps\common\Farthest Frontier\Farthest Frontier (Mono)\MelonLoader\Latest.log`

---

## 3. FF Architecture Discoveries

### 3.1 Generation Pipeline

FF's `TerrainGen.TerrainGenerator` runs a staged pipeline:

| Stage | Method (Generate*) | Method (GenSliced_*) |
|-------|---|---|
| Setup | Stage_1 | yes |
| Voronoi | Stage_2 | yes |
| Noise | Stage_3, 5 | yes |
| Features | Stage_10, 20, 35 | yes |
| **PreWater** | Stage_37 | yes |
| **River Paths** | Stage_38 (Voronoi-based) | **NO** |
| Paint Biomes | Stage_40 | yes |
| **Water (lakes)** | Stage_50 | yes |
| **River Geometry** | Stage_60 | **NO** |
| Roads | Stage_70 | yes |
| WaterDetails | Stage_97 | yes |

**Critical finding:** the synchronous `Generate()` path includes river stages
(38 + 60), but the **sliced/coroutine runtime pipeline `GenSliced_Generate`
omits them entirely.** That's the dev's "kill switch" for shipped maps —
not a config flag, just a missing call site.

### 3.2 Two Index Spaces for Splat Textures

FF has two terrain texture systems:

**Generation-side (in `_generationData`):**
- `splatTexturesList` (List<TerrainTexturePair>, Count=24)
  - Each entry has `diffuse` (Texture2D) + `normal` (Texture2D)
  - Indices: 0..23
- `splatMaps` (Single[hmRes, hmRes, 24]) — noise weights per cell
- `riverUnderwaterTexture` / `riverShorelineTexture` — int indices into above

**Runtime-side (on `Terrain2.Data`):**
- `ControlTextures` (List<Terrain2Control>, Count=6 once gen completes)
  - Each is an RGBA texture; 4 channels each = 24 channels total
- `TextureLayers` (Count=1, mostly null on tested maps)
- `CustomLayers` (Count=2 — possibly the river texture pair on tested maps)

**No direct mapping.** To paint a splat at gen-side index 22, you must:
1. Get `splatTexturesList[22].diffuse` (a Texture2D asset)
2. Walk `Terrain2.Data.TextureLayers + CustomLayers` looking for one whose
   `.diffuse` matches that asset by reference
3. The matched index → ControlTextures[idx/4] channel idx%4

**API:** `Terrain2Control.SetPixelComponent(x, z, channel, weight01)` +
`Upload(false)` to commit. Pangu uses this. Coordinates in
`ControlTextureSize` space (independent of heightmap resolution).

### 3.3 Custom Mesh Terrain (NOT Unity Terrain)

FF doesn't use `UnityEngine.Terrain`. The ground is composed of many
`MeshObject` GameObjects (Transform + MeshFilter + MeshRenderer) under a
hierarchy of `LibTerrain2.Terrain2`. Heightmap manipulation goes through:

```
TerrainManagerBase.SetHeight(int hX, int hZ, float worldHeight)   // per-cell write
Terrain2.SmoothHeightsNotify(tm, minX, minZ, maxX, maxZ, true)    // batch rebuild
GenerationData.heightNoise[x, z] = normalized                     // dual-write for persistence
```

Pangu (`G:\...\Mods\Pangu_FF.dll`, decompiled at
`C:\Users\saged\ClaudeCodeLocalSessions\Pangu_FF.decompiled.cs`)
uses this exact pattern.

### 3.4 WaterPath Visual System

`TerrainGen.WaterPath` (CEMonoBehaviour, RequireComponent
`MeshFilter` + `MeshRenderer`):

- Fields: `private List<TerrainRiver.ControlPoint> points`, `polygonPoints`,
  `polygonPointsRO`, `public Material material`, `bool debug`
- **No `Awake`/`Start`/`OnEnable`/`Update`** — pure mesh container
- Built by: `WaterPath.SetPoints(Terrain2 terrain, List<ControlPoint>,
  AnimationCurve transparencyCurve, AnimationCurve extinctionCurve, float waterY)`

The prefab lives on `WaterPlane.waterPathPrefab` (a field on the WaterPlane
component attached to the "Sea Layer" child GameObject).

**Instantiation in vanilla:** done by `Terrain2Builder.BuildTerrainShared03`,
**not** by Stage 60. Stage 60 only paints the heightmap trench. Vanilla code:

```csharp
GameObject obj = base.gameObject.transform.Find("Sea Layer").gameObject;
float y = obj.transform.localPosition.y;
GameObject riversBucket = TerrainGenerator.CreateBucket(base.gameObject, "Rivers");
WaterPlane wp = obj.GetComponent<WaterPlane>();
foreach (river in generationData.rivers) {
    WaterPath path = Object.Instantiate(wp.waterPathPrefab);
    path.transform.parent = riversBucket.transform;
    path.transform.localPosition = Vector3.zero;
    path.material = terrain2Builder.riverMaterial;
    path.SetPoints(terrain2, river.points, river.transparencyCurve,
                   river.extinctionCurve, y);
}
```

### 3.5 SaveManager API

```csharp
void Save(string savedGameFileNameNoExtension, bool isHighMemoryAutoSave,
          bool isAutoSave)   // patchable via Harmony
static string folderName = "Save/"
static string activeSaveFileName    // set on load to "{slot}/{savename}"
static string mapFileExtension = ".map"
static string GameFolder(string saveName)   // returns nested slot path
```

Saves go to `Save/{slot}_{timestamp}/{saveName}.map`. Pangu does NOT patch
Load — relies on FF re-reading its `.map` to recover Pangu's heightmap edits.

### 3.6 useSavedMap Detection

`_generationData.useSavedMap` is `false` during fresh map gen, `true` when
FF is loading a save. This is THE signal to gate mod behavior — used in
`RiverSettingsPatch.IsLoadingSavedMap(__instance)`.

---

## 4. Solved Problems

### 4.1 Stage 38 + Stage 60 don't fire in sliced pipeline
**Solution:** Hook postfixes on `GenerateAsync_PreWater_Stage37` and
`GenerateAsync_Water_Stage50` (which DO fire in sliced mode), use those
to manually `Invoke` Stage 38 and Stage 60 reflectively.

```csharp
HookCarrier(harmony, tgType, "GenerateAsync_PreWater_Stage37",
            nameof(InjectStage38Postfix));
HookCarrier(harmony, tgType, "GenerateAsync_Water_Stage50",
            nameof(InjectStage60Postfix));
```

### 4.2 Stage 38 generates 0 rivers despite numRivers=4
**Cause:** Voronoi validator requires candidate rivers to terminate at a
water area whose `WaterType.riverEndPoint == true`. Vanilla data has 2/4
WaterTypes flipped (Lake, BorderOcean) but Pond/LakeSmall are false.

**Solution:** In `RiversRestoredMod.MarkAllWaterTypesAsRiverEnd()`, walk all
`UnityEngine.Resources.FindObjectsOfTypeAll(WaterType)` and set
`riverEndPoint = true` on each.

### 4.3 Stage 60 NREs on per-river-segment dereference
**Cause:** Stage 60's `TraceRiver → GridTrace` references state in
`TerrainGenerator` instance fields that are uninitialized on the sliced
pipeline. We healed visible nulls (waterBiome, generatorThread,
cachedAreas, usedText, usedText2) — Stage 60 still NREs at offset 0x76
in GridTrace, suggesting a sub-field of one of the populated objects is
the actual culprit.

**Workaround:** Stage 60 partial execution **is enough** — it instantiates
WaterPath visuals at endpoints + early segments before the NRE kills it.
We don't need full execution if we do the carve ourselves.

### 4.4 Stage 60's heightmap carve doesn't fire
**Solution:** Manual carve via Pangu's API in `RiverCarver.cs`:

```csharp
foreach (river in _generationData.rivers) {
    foreach (segment a→b in river.points) {
        BresenhamWalk(a, b) {
            for each cell in radius:
                terrainManager.SetHeight(cellX, cellZ, trenchHeight);
                heightNoise[cellX, cellZ] = trenchHeight / mapH;  // dual-write
        }
    }
}
terrain.SmoothHeightsNotify(tm, minX, minZ, maxX, maxZ, true);
```

With banked falloff (inner radius full carve, outer radius cubic-eased ramp
back to original) and 2-pass box-blur smoothing.

### 4.5 Yellow shoreline texture appears on reload
**Cause:** Our mod was re-running Stage 38 on load, which generates rivers
at *slightly different positions* due to RNG state divergence. Stage 60
partial then paints shoreline splat at these new (wrong) positions, which
exposes FF's shoreline texture at full saturation = bright fluorescent yellow.

**Solution:** Gate ALL injection postfixes behind `IsLoadingSavedMap()`
check — don't re-run any generation logic on save load. The saved heightmap
and ControlTextures already render correctly.

### 4.6 Heightmap and splat textures persist across save/load
**Confirmed working:** FF's vanilla save format serializes:
- `Terrain2Data.heightmap` (carved trenches survive)
- `Terrain2Data.ControlTextures` (riverbed colors survive)

Our manual carve writes to both `terrainManager.SetHeight()` (live) AND
`heightNoise[,]` (regen-resilient), and the splat writes go via Pangu's
SetPixelComponent path. Both persist correctly.

---

## 5. UNSOLVED: Water Visual on Reload

**This is the v0.1.0 ship-blocker.**

### 5.1 The problem
On a freshly generated map, water is visible (Stage 60 partial spawns
WaterPath GameObjects). After save → reload, **the water visual is gone**
— FF doesn't serialize the WaterPath GameObjects.

### 5.2 What we tried
1. **Re-invoke Stage 60 on load** → caused the "void map" (entire terrain
   mesh disappeared). Stage 60 corrupts FF's render state when invoked
   outside its expected pipeline context.
2. **Restore river data to `_generationData.rivers` from sidecar** →
   ALSO caused void. Just writing to that list confuses FF's load process.
3. **Direct WaterPath spawn** — replicate `Terrain2Builder.BuildTerrainShared03`
   pattern: `Object.Instantiate(prefab) + SetPoints(...)`. This SUCCEEDS
   diagnostically (active=true, vertices=1268, material attached) but
   **produces invisible water in-game.**

### 5.3 What we know about the invisible-water bug

Diagnostics during direct spawn:
- `controlTextures.Count = 6` ✓ (live splat data exists)
- `terrain2 = LibTerrain2.Terrain2` ✓ (component found)
- `terrain2Builder = ...` ✓ with `riverMaterial = Terrain2River` (correct material)
- `seaLayer.localPosition.y = 3.15` (water surface Y, matches fresh-gen)
- `waterPathPrefab` resolves to a real `WaterPath` instance
- After `Object.Instantiate + parent + localPos=0 + material set + SetPoints(curves, waterY)`:
  - `activeInHierarchy = true`
  - `MeshFilter.mesh.vertexCount = 1268` (or similar, scales with control points)
  - `MeshRenderer.sharedMaterial = Terrain2River`
  - `transform.position = (0,0,0)` (parented to "Rivers" bucket at origin)

But **invisible**. Same prefab, same material, same control points, same
curves — fresh-gen renders fine, our spawn doesn't.

### 5.4 Current best hypothesis (untested at handoff)

Per agent research on `WaterPath.SetPoints` decompile:
```csharp
float num5 = Mathf.Min(controlPoints[j].pos.y, b);  // running min cp.pos.y
float height = terrain.GetHeight(controlPoints[j].pos);
float num6 = Clamp(Max(0, num5 - height), 0, num5) / num5;
float r = transparencyCurve.Evaluate(num6);  // alpha modulator
```

`controlPoints[j].pos.y` in our sidecar is the **original Stage-38 spline
elevation** (e.g., 18.63m), but vanilla's `TraceRiverFunc` (line 485813
in Assembly-CSharp.decompiled.cs) **modifies** `cp.pos.y` to be the
carved bed level relative to water before Stage 60's WaterPath build.

We capture cp.pos.y in our sidecar BEFORE TraceRiverFunc runs (because
our save fires from SaveManager.Save postfix, after Stage 60 partial NRE
prevented the modification). So our cps have the wrong pos.y for
SetPoints' transparency math.

**The fix being tested at handoff:** override `cp.pos.y = waterY` for all
control points before passing to SetPoints. This puts pos.y at the water
surface, which makes `num6 = (waterY - terrainHeight) / waterY` evaluate
to roughly 0.16 for a 0.5m carved trench at waterY=3.15. Whether the
transparency curve produces visible alpha at that input is unknown.

If THAT doesn't work, deeper investigation needed:
- Full `WaterPath.CreateMesh` body (only partial visible in agent's report)
- Look for shader parameters set by `Terrain2Builder.BuildTerrainShared01/02`
  that the river material requires
- Check `Camera.cullingMask` / layer assignment on spawned GameObjects
- Verify `riverMaterial` instance vs sharedMaterial reference behavior

---

## 6. Code Patterns Worth Preserving

### 6.1 Reflection-only Harmony patching for unstable types
```csharp
Type tgType = AccessTools.TypeByName("TerrainGen.TerrainGenerator");
MethodInfo m = AccessTools.Method(tgType, "GenerateAsync_PreWater_Stage37");
harmony.Patch(m, postfix: new HarmonyMethod(stub));
```
Avoids hard compile-time deps on FF types that may shift between game versions.

### 6.2 Idempotent struct/class field write (handles both)
```csharp
var rsField = tgType.GetField("riverSettings", BindingFlags...);
object rsBox = rsField.GetValue(__instance);
rsType.GetField("numRivers").SetValue(rsBox, wantValue);
rsField.SetValue(__instance, rsBox); // CRITICAL writeback for structs;
                                     // no-op for classes (same reference)
```

### 6.3 OnUpdate polling for "TerrainGenerator becomes available"
Cached in `RiverSettingsPatch.CachedGenerator` from `DoOverride` (now
also set during save-load detection — earlier code missed that path).

### 6.4 Save-load detection
```csharp
public static bool IsLoadingSavedMap(TerrainGenerator __instance) {
    var gd = __instance._generationData;
    return gd?.useSavedMap == true;
}
```
Use this to gate ALL generation-pipeline interactions.

### 6.5 Sidecar binary format (.rivers file)
```
int32 magic    = 0x52525452 ("RRTR")
int32 version  = 2 (v1 omits curves)
int32 numRivers
per river:
    int32 numPoints
    per point:
        float posX, posY, posZ
        float height
        float width
    int32 transparencyKeyCount
    per key: float time, value, inTangent, outTangent
    int32 extinctionKeyCount
    per key: float time, value, inTangent, outTangent
```

Stored at `Save/{activeSaveFileName}.rivers` (canonical) with fallback
to `Save/{baseName}.rivers` (older flat layout) for migration.

---

## 7. MelonPreferences (User-Tunable)

```
[RiversRestored]
RiversEnabled                 = true   # Master kill switch
NumRivers                     = 4      # Stage 38 numRivers (vanilla 2)
MinPoints                     = 15     # Voronoi validator (vanilla 40 = rejects all)
MinWidth, MaxWidth            = 0      # 0 = vanilla
MinDepth, MaxDepth            = -1     # -1 = vanilla
MarkWaterTypesAsRiverEnd      = true   # Crucial for Voronoi acceptance
ForceCoastlineTerrain         = false  # Diagnostic
RiverInnerRadius              = 2      # Cells of full-depth trench from centerline
RiverOuterRadius              = 5      # Outer edge of bank blend zone
RiverJitterAmplitude          = 1.5    # Meander wiggle in world units
RiverJitterFrequency          = 0.6    # Wave count per Voronoi segment
RiverSmoothPasses             = 2      # 3x3 box-blur passes after carve
RiverTrenchDepth              = 0.5    # Metres below water surface
```

Defaults tuned to vanilla-FF scale (creek, not canyon).

---

## 8. Known Issues / Gotchas

1. **Generation time**: Large-map gen takes 5-6 minutes due to reflection-heavy
   carve loops + smoothing passes. Could be 10-50× faster with cached
   `Delegate.CreateDelegate` instead of `MethodInfo.Invoke`. Not done.

2. **Pangu interaction**: User reported water disappearing once after Pangu
   edits + save + reload. Couldn't reproduce. Possibly a Pangu-side
   `SaveManager.Save` patch interaction.

3. **Map-edge yellow streak**: A thin yellow band can appear at the very
   map border even on fresh gen — this is FF's "border shoreline" texture
   bleeding through, unrelated to our mod. Visible in screenshots taken
   at extreme camera angles.

4. **NavMesh not updated**: Carved trenches don't have NavMesh updated, so
   citizens can walk through them as if on flat ground. v0.2.0+ should
   add `NavMeshModifierVolume` updates.

5. **Confluences look like deltas**: When two rivers terminate at the same
   point, our carve overlap creates an unnatural Y-shape. Realistic-ish
   but may want explicit handling.

6. **Fishing integration unverified**: Building a Fishing Shack on a fresh-gen
   river was not testable because placement validator requires a water
   surface, and our trench may or may not register correctly. Need testing
   once water-on-reload is fixed.

---

## 9. Tools & Resources

- **Decompiler**: `ilspycmd` (`/c/Users/saged/.dotnet/tools/ilspycmd`)
- **Decompiled FF source**:
  `C:\Users\saged\ClaudeCodeLocalSessions\AssemblyCSharp_Decompiled\Assembly-CSharp.decompiled.cs`
  (16 MB, ~488000 lines)
- **Decompiled Pangu source**:
  `C:\Users\saged\ClaudeCodeLocalSessions\Pangu_FF.decompiled.cs` (11197 lines)
- **FFDataDump mod** (sibling project at `C:\Users\saged\source\repos\FFDataDump\`):
  CSV exports of FF's static data — items, buildings, rivers, etc.

### Key line numbers in Assembly-CSharp.decompiled.cs:
- `Terrain2Builder` class: line 17008
- `BuildTerrainShared03` (river spawn): line 17217
- `BuildTerrainLoadedGame`: line 17290
- `WaterPlane`: line 221066
- `WaterPath`: line 488185
- `WaterPath.SetPoints`: line 488343
- `WaterPath.CreateMesh`: line 488210
- `TerrainGenerator.CreateBucket`: line 487470
- `Sea Layer Y assignment`: line 487388, 487434
- `TraceRiverFunc` (cp.pos.y modification): line 485813
- `FishingManager.FindRivers`: line 152160

---

## 10. v0.2.0 Backlog (Priority Order)

1. **Solve invisible-water bug** (current blocker). Path: continue with
   cp.pos.y override; if that fails, deep dive into `WaterPath.CreateMesh`
   shader uniforms or capture/clone WaterPath GameObjects entirely at
   gen time.

2. **Initialize git repo** + commit current state. Past time.

3. **Cache `MethodInfo.Invoke` as `Delegate.CreateDelegate`** for
   `SetHeight`, `SetPixelComponent`, etc. Should drop gen time from
   ~6 min to <30s on large maps.

4. **NavMesh integration**: add NavMeshModifierVolume around carved
   trenches.

5. **FishingManager registration**: re-trigger `FishingManager.FindRivers`
   after any river spawn so anglers/creelers see new rivers.

6. **Confluence handling**: detect when two rivers' control point
   endpoints are within N cells, snap them to the same point, treat as
   delta/Y-junction.

7. **Optional: river-bed depth varies along path**: river is shallow at
   source, deeper at mouth. Currently uniform `RiverTrenchDepth`.

---

## 11. Final Architecture Diagram

```
GENERATION PATH (fresh map):
  TerrainGenerator → GenSliced_Generate (FF)
    → Stage_PreWater_Stage37 [our hook]
       → InjectStage38Postfix:
          → manually invoke Stage_RiverPaths_Stage38
            → populates _generationData.rivers
    → Stage_Water_Stage50 [our hook]
       → InjectStage60Postfix:
          → heal instance nulls (waterBiome, generatorThread, etc)
          → manually invoke Stage_RiverGeometry_Stage60 (NREs partway)
            → spawns WaterPath visuals before NRE
    → ...other stages...
  OnUpdate polling sees TerrainGenerator + Terrain2 alive:
    → RiverCarver.CarveAllRivers:
       → for each river, Bresenham walk + DropDisc per cell
          → terrainManager.SetHeight + heightNoise dual-write
          → splat painting via Terrain2Control.SetPixelComponent
       → terrain.SmoothHeightsNotify (mesh rebuild)
       → SmoothBoxBlur (4 iterative passes)

SAVE PATH:
  User clicks Save → SaveManager.Save(saveName, ...)
    → [our postfix] RiverPersistence.SavePostfix:
       → ResolveSidecarPath via activeSaveFileName
       → WriteSidecar v2 (positions + curves)
       → file: Save/{slot}/{name}.rivers

LOAD PATH:
  User loads save → FF deserializes .map → terrain rendered
    → PreGenerateShared fires [our hook, gated, returns early]
    → OnUpdate detects useSavedMap=true:
       → RiverPersistence.TryRestore:
          → ReadSidecarRivers (v1 or v2)
          → SpawnWaterPathsFromSidecar:
             → for each river: Object.Instantiate(prefab)
             → Override cp.pos.y → waterY  [current experiment]
             → SetPoints(terrain2, cps, transparency, extinction, waterY)
       → RestoredThisLoad latch (one-shot per scene)
```

---

## 12. Tonight's Net Achievement

Going from **"Crate's shelved feature, no way to access"** to:
- ✅ Fully working procedural river generation
- ✅ Voronoi-based natural meandering paths
- ✅ Lake-to-lake routing via riverEndPoint detection
- ✅ Carved heightmap trenches with banked falloff
- ✅ Riverbed splat textures (sandy banks, brown beds)
- ✅ Visible flowing water on first session
- ✅ Configurable river count, dimensions, smoothness, jitter
- ✅ Persistent across save/reload (heightmap + splats)
- ✅ Compatible with Pangu mod (uses same terrain API)
- ❌ Water visual on save/reload (the one remaining bug)
- ❌ FishingManager integration verification
- ❌ NavMesh updates

**The water-on-reload problem is the last major gap.** Solving it would
make this a complete feature-parity restore of Crate's shelved system.
