[h2]Disclaimer:[/h2] [i]This mod activates partial river creation pathway within the map generation system. If Crate ever brings rivers into a DLC, this mod will be retired immediately to support their work.[/i] [hr][/hr] [h1]Rivers Restored[/h1] Farthest Frontier already includes a sophisticated river generation system — Voronoi-based pathfinding, flowing water animation, fishing integration — and a complete ocean system that no vanilla map ever switches on. Rivers Restored activates both on new maps and adds a complementary terrain-carving layer so rivers and coastlines settle naturally into the landscape.

New maps now generate winding rivers terminating in lakes or spanning the map edge to edge, with carved beds, sloped banks, flowing water animation, and full fishing support — and one edge of every map can be open sea.

[b]New in v1.8.0: Map Edge.[/b] Shrink the mountain ring around the map (or remove it) and reclaim the out-of-bounds strip: the buildable area now starts 50 m from the edge instead of 150 m, which turns a Small map from 77% usable land into 92%. Both tunable.

[b]New in v1.7.0: Coastal Maps.[/b] One edge of every new map is now open sea — the game's own ocean water, sand shoreline, and surf ambience, which vanilla never switches on — and the first river drains into it. Off switch and full tuning in the settings; works with rivers turned off too. [hr][/hr] [h2]What You Get[/h2] [b]🌊 Winding Rivers[/b] New maps generate between 1 and 8 rivers depending on map size and terrain. Each river winds naturally between water bodies — lakes, ocean coastlines, or both — following the landscape rather than cutting straight lines through it.

[b]⛏ Carved Riverbeds[/b] Rivers settle into the heightmap with a deep inner channel and naturally sloped outer banks, smoothed iteratively to blend seamlessly with surrounding terrain. Rivers feel like part of the world because they're shaped into it.

[b]🏝 Coastal Maps[/b] [i](v1.7.0)[/i] Farthest Frontier ships a complete ocean system that no vanilla map ever triggers. Rivers Restored opens one map edge and lowers the land there below the water plane, and the game does the rest: sea water, sandy beaches with their own shoreline props, ocean ambience, fishable shore. Pick the edge (or let the seed decide), shape the shoreline with bays and headlands, set the beach and shelf widths, and choose how far the sea reaches into the map. The first river drains into the sea. The coast survives save and reload, saves made without it are never touched, and the whole feature has its own on/off switch independent of rivers.

[b]⛰ Map Edge[/b] [i](v1.8.0)[/i] Vanilla keeps 150 m on every edge out of bounds behind a ring of mountains. Map Edge shrinks that ring (half-size by default — set 1 for vanilla, 0 to remove it) and moves the buildable boundary to 50 m from the edge, so a Small map goes from 77% usable land to 92%. Both tunable, and both apply with or without a coast. Raiders, traders, and immigrants still arrive at the terrain edge, so they now show up closer to your buildable land.

[b]🎣 Fishable Water[/b] Rivers register as proper water bodies with the game's fishing system. Fishing Shacks placed on rivers produce at the same rate as lake-side shacks, with a configurable productivity multiplier so river-side villages can sustain themselves on river fishing alone. Since v1.6.0, rivers and lakes are stocked in proportion to their real size — a map-spanning river holds thousands of fish instead of being capped like a pond — and the counts persist through save and reload. Tune it with [code]River Fish Per Water Cell[/code]; lake sizing has its own toggle.

[b]🦌 Wildlife Across Rivers[/b] [i](v1.6.0, optional)[/i] Rivers normally wall wildlife out of any region without a land path to your town. Turn on [code]Rivers Don't Block Wildlife Spawning[/code] and deer can spawn on the far side of a river. Off by default. Hunting, trapping, and villager pathing are unchanged.

[b]🗺 Live Map Preview[/b]  Open [b]Advanced Settings[/b] on the New Game screen with the preview pref enabled and you'll see a polished render of the map you're about to generate — biome-colored, contour-shaded, with rivers, lakes, and the coast visible. Change the size slider, biome, or click the seed re-roll dice and the preview regenerates automatically. No clicks, no confirmation — just keep tuning until you like what you see, then start the game. If a render ever stalls on a slow machine or Steam Deck, you get a clear "preview timed out" message and can start the game anyway.

The preview saves a PNG of every render to [code]UserData/RiversRestored/Previews/[/code] with the seed, size, biome, river count, water %, and difficulty selections embedded in the filename. Auto-prunes to the 25 newest so the folder doesn't fill up.

[b]🎚 Per-Biome Tuning[/b]  Five river presets — Idyllic Valley, Lowland Lakes, Arid Highlands, Plains, Alpine Valleys — each with their own carve-shape, density, and width settings. Enable [code]GranularSettings[/code] in the config to surface 13 sliders per preset for fine-grained control: river count, point density, channel width range, carve depth and smoothing, jitter, fishing-area multiplier, and more.

[b]💧 Optional Flow Animation[/b] The flowing water ribbon cosmetic animation can be toggled off in the config for performance-conscious players on river-heavy maps. Rivers still render as water surfaces — just without the animated flow effect.

[b]💾 Save/Load Persistence[/b] Rivers and coasts survive save/load cycles completely. Small sidecar save files store river and coast data alongside your map file and restore everything on reload — water areas, carved terrain, fishing nodes, shoreline, and flow animation all return exactly as you left them. (Note: loading saves on different PCs or drives requires files to be copied manually)

[b]⚙ Keep Clarity Integration[/b]  All Rivers Restored settings — master sliders, per-preset tuning, the preview toggle, flow animation, save behavior, Coastal Maps, Map Edge — show up in Keep Clarity's in-game settings panel for live tuning without restarting. Soft dependency: works without KC installed (settings still available via MelonPreferences). [hr][/hr] [h2]Important — New Maps Only[/h2] Rivers Restored affects [b]new map generation only[/b]. Existing saves will not have rivers, coasts, or the Map Edge changes added retroactively — terrain shaping happens at world generation time and cannot be safely applied to an established map.

Start a new game to experience rivers and coasts. [hr][/hr] [h2]⚠ First-Time Setup — Read This[/h2] River generation happens at map creation and requires some extra processing time. Here's what to expect and how to get the best experience:

[b]Initial generation is slow[/b] When generating a new map, expect the loading screen to take longer than usual and possibly stutter briefly. This is normal — the mod is shaping riverbeds, painting terrain, and registering water areas during this pass. It only happens once. Typical times: Small ~45s, Medium ~70s, Large ~110s.

[b]After generation — save immediately[/b] Once your map loads and you place your Town Center, [b]save the game and reload it[/b]. This is the recommended first step. The reload settles all river data into the normal play state. Load times after this first save are normal.

[b]Yellow border lines[/b] You may see faint yellow lines between river water and terrain on map creation. These disappear completely after saving and reloading. They're a transient artifact of the initial generation pass, not a permanent issue.

[b]Fishing Shacks not finding fish nodes[/b] If a Fishing Shack placed on a river shows no fish nodes, [b]move the work area radius slightly[/b]. This triggers the game to rescan for fishing nodes and will pick up the river's fish sources. You only need to do this once.

[b]Coming from the Coastal Kingdom test mod?[/b] Remove [code]CoastalKingdom.dll[/code] — Rivers Restored 1.7.0 and later includes it, and the two must not run together.

[b]Tip for heavily-modded saves[/b] If you run many mods, consider generating your map with only Rivers Restored active, saving, then re-enabling your other mods before loading the save. This reduces generation time and avoids potential mod-stacking slowdowns during the first load.

[hr][/hr] [h2]Performance Notes[/h2] On maps with many rivers, the flowing water animation can be CPU-heavy. If you experience stutter or high system impact, set [code]EnableRibbonAnimation = false[/code] in the config file. Rivers will continue to render as still water surfaces and all other features (fishing, save/load, carved beds) work unchanged.

[b]v1.6.2[/b] fixed the main-menu stutter that earlier versions caused while sitting at the menu — the menu runs at a locked 60 FPS again.

[b]Coast maps:[/b] keep [code]Flow Bias Strength[/code] around 0.1–0.3. Higher values tilt the terrain enough to flood the low side of the map.
[hr][/hr]
[h2]Workshop Collection[/h2]
[url=https://steamcommunity.com/id/GameDad79/myworkshopfiles/?appid=1044720]🐉 SageDragoon's Workshop[/url]
[url=https://steamcommunity.com/sharedfiles/filedetails/?id=3717275518]📱 Keep Clarity[/url] — UI Enhancements
[url=https://steamcommunity.com/sharedfiles/filedetails/?id=3712359812]🚛 Manifest Delivery[/url] — Wagon Shop and Logistics Enhancements
[url=https://steamcommunity.com/sharedfiles/filedetails/?id=3715601527]🏹 Warden of the Wilds[/url] — Hunting and Fishing Enhancements
[url=https://steamcommunity.com/sharedfiles/filedetails/?id=3705817409]🌿 Tended Wilds[/url] — Forager Enhancements
[url=https://steamcommunity.com/sharedfiles/filedetails/?id=3715117771]🌊 Rivers Restored[/url] — Rivers!
[url=https://steamcommunity.com/sharedfiles/filedetails/?id=3724854806]📦 Essential Provisions[/url] — Curated QoL bundle, sixteen features, all opt-in
[url=https://steamcommunity.com/sharedfiles/filedetails/?id=3726825522]👑 Sovereign Boons[/url] — Power-spike tuning pack, 14 boons, all opt-in
[url=https://steamcommunity.com/sharedfiles/filedetails/?id=3697995619]🌱 Forageable Transplantation[/url] — Relocate Forageables (Included in Tended Wilds)
[url=https://steamcommunity.com/sharedfiles/filedetails/?id=3717363061]🏘️ Settlement Planner[/url] — UI Button Links to Online City Planner (Included in Keep Clarity)
[url=https://sagedragoon79.github.io/FarthestFrontierPlanner]🏘️ Settlement Planner (Direct Link)[/url] — Direct Link to Planner
