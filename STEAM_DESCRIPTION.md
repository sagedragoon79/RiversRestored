[h2]Disclaimer:[/h2] [i]This mod activates partial river creation pathway within the map generation system. If Crate ever brings rivers into a DLC, this mod will be retired immediately to support their work.[/i]
[hr][/hr]
[h1]Rivers Restored[/h1]
Farthest Frontier already includes a sophisticated river generation system — Voronoi-based pathfinding, flowing water animation, fishing integration. Rivers Restored activates that system on new maps and adds a complementary terrain-carving layer so the rivers settle naturally into the landscape.

New maps now generate winding rivers terminating in lakes or spanning the map edge to edge, with carved beds, sloped banks, flowing water animation, and full fishing support.

[b]New in v1.4.0:[/b] live map preview that updates the moment you change a setting, plus per-biome slider tuning for fine-grained control over river shape and density.
[hr][/hr]
[h2]What You Get[/h2]
[b]🌊 Winding Rivers[/b]
New maps generate between 1 and 8 rivers depending on map size and terrain. Each river winds naturally between water bodies — lakes, ocean coastlines, or both — following the landscape rather than cutting straight lines through it.

[b]⛏ Carved Riverbeds[/b]
Rivers settle into the heightmap with a deep inner channel and naturally sloped outer banks, smoothed iteratively to blend seamlessly with surrounding terrain. Rivers feel like part of the world because they're shaped into it.

[b]🎣 Fishable Water[/b]
Rivers register as proper water bodies with the game's fishing system. Fishing Shacks placed on rivers produce at the same rate as lake-side shacks, with a configurable productivity multiplier so river-side villages can sustain themselves on river fishing alone.

[b]🗺 Live Map Preview[/b] [i](v1.4.0)[/i]
Open [b]Advanced Settings[/b] on the New Game screen with the preview pref enabled and you'll see a polished render of the map you're about to generate — biome-colored, contour-shaded, with rivers and lakes visible. Change the size slider, biome, or click the seed re-roll dice and the preview regenerates automatically. No clicks, no confirmation — just keep tuning until you like what you see, then start the game.

The preview saves a PNG of every render to [code]UserData/RiversRestored/Previews/[/code] with the seed, size, biome, river count, water %, and difficulty selections embedded in the filename. Auto-prunes to the 25 newest so the folder doesn't fill up.

[b]🎚 Per-Biome Tuning[/b] [i](v1.4.0)[/i]
Five river presets — Idyllic Valley, Lowland Lakes, Arid Highlands, Plains, Alpine Valleys — each with their own carve-shape, density, and width settings. Enable [code]GranularSettings[/code] in the config to surface 13 sliders per preset for fine-grained control: river count, point density, channel width range, carve depth and smoothing, jitter, fishing-area multiplier, and more.

[b]💧 Optional Flow Animation[/b]
The flowing water ribbon cosmetic animation can be toggled off in the config for performance-conscious players on river-heavy maps. Rivers still render as water surfaces — just without the animated flow effect.

[b]💾 Save/Load Persistence[/b]
Rivers survive save/load cycles completely. A small sidecar save file stores river data alongside your map file and restores everything on reload — water areas, carved terrain, fishing nodes, and flow animation all return exactly as you left them. (Note: loading saves on different PCs or drives requires files to be copied manually)

[b]⚙ Keep Clarity Integration[/b] [i](v1.4.0)[/i]
All Rivers Restored settings — master sliders, per-preset tuning, the preview toggle, flow animation, save behavior — show up in Keep Clarity's in-game settings panel for live tuning without restarting. Soft dependency: works without KC installed (settings still available via MelonPreferences).
[hr][/hr]
[h2]Important — New Maps Only[/h2]
Rivers Restored affects [b]new map generation only[/b]. Existing saves will not have rivers added retroactively — terrain shaping happens at world generation time and cannot be safely applied to an established map.

Start a new game to experience rivers.
[hr][/hr]
[h2]⚠ First-Time Setup — Read This[/h2]
River generation happens at map creation and requires some extra processing time. Here's what to expect and how to get the best experience:

[b]Initial generation is slow[/b]
When generating a new map, expect the loading screen to take longer than usual and possibly stutter briefly. This is normal — the mod is shaping riverbeds, painting terrain, and registering water areas during this pass. It only happens once. Typical times: Small ~45s, Medium ~70s, Large ~110s.

[b]After generation — save immediately[/b]
Once your map loads and you place your Town Center, [b]save the game and reload it[/b]. This is the recommended first step. The reload settles all river data into the normal play state. Load times after this first save are normal.

[b]Yellow border lines[/b]
You may see faint yellow lines between river water and terrain on map creation. These disappear completely after saving and reloading. They're a transient artifact of the initial generation pass, not a permanent issue.

[b]Fishing Shacks not finding fish nodes[/b]
If a Fishing Shack placed on a river shows no fish nodes, [b]move the work area radius slightly[/b]. This triggers the game to rescan for fishing nodes and will pick up the river's fish sources. You only need to do this once.

[b]Tip for heavily-modded saves[/b]
If you run many mods, consider generating your map with only Rivers Restored active, saving, then re-enabling your other mods before loading the save. This reduces generation time and avoids potential mod-stacking slowdowns during the first load.

[hr][/hr]
[h2]Performance Notes[/h2]
On maps with many rivers, the flowing water animation can be CPU-heavy. If you experience stutter or high system impact, set [code]EnableRibbonAnimation = false[/code] in the config file. Rivers will continue to render as still water surfaces and all other features (fishing, save/load, carved beds) work unchanged.

The map preview is opt-in and disabled by default. Enable it via [code]EnableMapPreviewRender = true[/code] and [code]ShowPreviewOverlay = true[/code] in the config when you want it.
[hr][/hr]
[h2]Compatibility[/h2]
[list]
[*]Compatible with Pangu and other terrain-shaping mods — rivers use the same water-area pattern. (Tip: use Pangu's lake creation feature, turn the size all the way down and adjust fish density, then click in the middle of the river or any other body of water. You should see new Fishing Shoals (+50 fishing) pop up here and there.)
[*]Compatible with fishing mods — the river productivity multiplier stacks correctly with other fishing bonuses
[*][b]Keep Clarity[/b] users get an in-game settings panel for all RR options
[*]No known conflicts with other mods at release
[/list]
[hr][/hr]
[b]Requirements[/b]
[list]
[*]Farthest Frontier v1.1.0 (Mono build)
[*]MelonLoader v0.7.0 or newer
[*][i]Optional:[/i] Keep Clarity for in-game settings panel
[/list]
[hr][/hr]
[h2]Known Limitations[/h2]
[list]
[*]Existing saves do not receive rivers — new map generation only
[*]River count (1–8) varies by map seed and terrain — some seeds connect fewer water bodies than others
[*]Very flat maps produce shallower river carving than mountainous terrain
[*]Initial map generation is slower than vanilla — save and reload after placing your Town Center for normal performance going forward
[*]Map preview's Custom map type doesn't auto-regen by itself (Custom doesn't reroll the seed). Pair with manual seed entry or any other setting change to trigger a new preview
[/list]
[hr][/hr]
[h2]Changelog[/h2]
[b]v1.4.0[/b] — Live map preview with auto-regen, per-biome slider tuning, Keep Clarity integration, filename metadata for saved previews, auto-prune of saved PNGs to 25 newest. Bug fixes: map size mismatch (preview ran at Medium when slider was Large), 3-minute load stall after Start, 30-60s frozen progress bar after seed reroll, 15-20s caption lag.
[b]v1.3.0[/b] — NW→SE flow direction bias for heightnoise, water-settings dump diagnostic.
[b]v1.2.x[/b] — Carver hot-path performance optimizations, save reload reliability fixes.
[b]v1.1.x[/b] — Multi-save persistence, water-survives-reload guarantees.
[b]v1.0.0[/b] — Initial release.
