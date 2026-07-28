# Perf fix: MAIN-MENU JITTER (the one players kept reporting) — READY TO SHIP as v1.6.2

**State: fix implemented + verified in-game (fix AND no-regression on seed
preview). Debug build deployed to Mods 2026-07-28. NOT committed, NOT tagged,
NOT released. This session's job: ship it.**

This is the headliner of the three 2026-07-28 perf fixes — it answers the
long-running player comments about the stuttery main menu.

## What was wrong
A temporary profiling mod (FFPerfProbe, repo
`C:\Users\saged\source\repos\FFPerfProbe`) measured the main menu:

- `Rivers Restored.OnUpdate` — **10–17 ms/frame average with 252–324ms worst
  single calls**, window after window, at the MAIN MENU. p95 frame time
  ~250ms = a visible quarter-second hitch every second or two. Every other
  mod: ≤0.05 ms/frame.
- The probe's FIND CALLS counter named it: `FindObjectsOfType[TerrainGenerator]`
  firing ~2×/second at the menu.

Root cause in `Plugin.cs` `OnUpdate` (~line 1262): when
`RiverSettingsPatch.CachedGenerator` is null — which at the main menu is
ALWAYS — the fallback ran `FindObjectOfType<TerrainGen.TerrainGenerator>()`
every 0.5s forever. The old comment claimed "the 0.5s throttle keeps the
fallback cheap"; in reality each scan walks the menu vista's full object set at
**250–320ms per call**. Two stalls/sec = the jitter. Workshop v1.5.6 almost
certainly has this too (the CHANGELOG shows the poll being throttled, not
gated) → every subscriber has been seeing it → the player comments.

History trap (why it was throttle-only): a previous `buildIndex < 2` gate was
removed because it keyed on the ACTIVE scene — gameplay's active scene is
'Frontier' (idx 1) while the terrain lives in the ADDITIVELY loaded 'Map'
scene (idx 2) — so that gate killed OnUpdate in-game. Do not resurrect it.

## The fix (already in source)
`Plugin.cs`:
- New helper `AnyTerrainSceneLoaded()` — loops
  `SceneManager.sceneCount`/`GetSceneAt(i)` checking scene NAMES across ALL
  loaded scenes for `"Map"` or `"Frontier"`. Name-based + all-scenes = immune
  to the additive-scene trap above. Cost: a tiny name loop, no object scans.
- The FindObjectOfType fallback is now gated on it (after the existing 0.5s
  throttle). At the pure main menu neither scene exists → no scan, ever.
  When gameplay loads OR the seed-preview worker additively loads its own
  'Map' scene, the gate opens and behavior is exactly as before.
- Comments at the site updated with the profiling numbers + the history trap.

## Verification (probe, post-fix session)
- Menu after launch settle: **flat 16.7ms / 60 FPS, p95 16.7ms, worst
  16.7ms** across every window — vsync-perfect, RR absent from all tables.
  (Before: p95 ~250ms.)
- **Seed preview regression check PASSED**: opening New Settlement fired RR's
  AutoRegen → "No existing TerrainGenerator — loading 'Map' scene
  additively..." → "'Map' scene load complete" → "using its TerrainGenerator
  on 'Terrain2'" — full preview pipeline intact.

## Ship checklist
1. Bump version **1.6.1 → 1.6.2** in Plugin.cs (MelonInfo/Version) AND the
   csproj (AssemblyVersion/FileVersion/Version). Add a CHANGELOG.md entry
   (repo keeps one — follow its format).
2. Release build:
   `dotnet build -c Release -p:Platform=x64 -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Farthest Frontier"`
3. Stage Release DLL to `…\Farthest Frontier (Mono)\Mods\`.
4. Commit, tag `v1.6.2`, push, `gh release create v1.6.2 <DLL> --title
   "Rivers Restored v1.6.2" --notes …` (DLL attached per fleet convention).
5. **Workshop:** still on v1.5.6 — the whole 1.6.x batch is pending manual
   upload. This fix makes that upload genuinely urgent (it's the answer to the
   standing player complaints). Include the patch note below in the change
   notes.
6. If shipping last of the three perf fixes (WotW/EP/RR): delete
   `FFPerfProbe.dll` from the Mods folder (temp diagnostic, adds overhead).

## Steam patch note (BBCode, copy-paste)
```
[h1]Rivers Restored v1.6.2[/h1]
Performance release — this one's for everyone who reported the choppy main menu.

[h2]Fixed[/h2]
[list]
[*][b]Main-menu stutter/jitter.[/b] Rivers Restored was repeatedly searching for the terrain generator while you sat at the main menu — a quarter-second stall every second or so on many machines. It now only searches when a map actually exists (in-game or while generating a seed preview). The menu runs at a locked 60 FPS again, and the seed preview works exactly as before.
[/list]
```
