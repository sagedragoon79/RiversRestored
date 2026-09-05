[h2]Disclaimer:[/h2] [i]This mod activates partial river creation pathway within the map generation system. If Crate ever brings rivers into a DLC, this mod will be retired immediately to support their work.[/i] [hr][/hr] [h1]Rivers Restored[/h1] Farthest Frontier already ships a sophisticated river system and a complete ocean system that no vanilla map ever switches on. Rivers Restored activates both on new maps and carves the terrain so rivers and coastlines settle naturally into the landscape: winding rivers with carved beds and sloped banks, flowing water, full fishing support, and one edge of open sea.

[b]New in v1.8.0 — Map Edge:[/b] reclaim the out-of-bounds strip (buildable land starts 50 m from the edge instead of 150 m, so a Small map goes from 77% usable to 92%) and shrink or remove the edge mountain ring. [b]New in v1.7.0 — Coastal Maps:[/b] one edge of every new map is open sea, and the first river drains into it. [hr][/hr] [h2]What You Get[/h2] [b]🌊 Winding Rivers[/b] 1 to 8 rivers per map, winding between lakes and coast, settled into carved beds with sloped, smoothed banks.

[b]🏝 Coastal Maps[/b] One map edge becomes real sea — the game's own water, sandy shoreline props, and surf ambience. Pick the edge or let the seed decide; tune beach, shelf, reach, and coastline shape. Its own on/off switch; works with rivers off.

[b]⛰ Map Edge[/b] Buildable boundary at 50 m from the edge and a half-size mountain ring (1 = vanilla, 0 = none), both tunable. Arrivals still spawn at the terrain edge, so they appear closer to your land.

[b]🎣 Fishable Water[/b] Rivers and coasts are proper fishing water. Rivers and lakes are stocked by their real size, so a map-spanning river holds thousands of fish, and counts persist through reload. Productivity multiplier and [code]River Fish Per Water Cell[/code] are tunable.

[b]🦌 Wildlife Across Rivers[/b] Optional, off by default: let deer spawn on the far side of a river. Hunting and pathing are unchanged.

[b]🗺 Live Map Preview[/b] With the preview pref on, Advanced Settings shows a biome-colored, contour-shaded render with rivers, lakes, and coast that regenerates as you change size, biome, or seed. Every render is saved as a PNG under [code]UserData/RiversRestored/Previews/[/code] (newest 25 kept). A stalled render shows a timeout message and you can start anyway.

[b]🎚 Per-Biome Tuning[/b] Five presets — Idyllic Valley, Lowland Lakes, Arid Highlands, Plains, Alpine Valleys. Enable [code]GranularSettings[/code] for 13 sliders per preset.

[b]💧 Optional Flow Animation[/b] Toggle the flowing-water ribbon off for performance; rivers still render as water.

[b]💾 Save/Load Persistence[/b] Rivers and coasts survive reload through small sidecar files next to your save (copy them if you move saves between PCs).

[b]⚙ Keep Clarity Integration[/b] Every setting, including Coastal Maps and Map Edge, appears in Keep Clarity's in-game panel. Soft dependency; works without KC. [hr][/hr] [h2]Important — New Maps Only[/h2] Rivers, coasts, and Map Edge apply to [b]new maps only[/b]. Existing saves are never altered. [hr][/hr] [h2]⚠ First-Time Setup[/h2] [b]Generation is slower[/b] the first time (Small ~45s, Medium ~70s, Large ~110s) and may stutter. It only happens once.

[b]Save and reload[/b] once you've placed your Town Center. It settles river data and clears the faint yellow lines you may see along riverbanks.

[b]No fish nodes on a river shack?[/b] Nudge its work area radius once to rescan.

[b]Coastal Kingdom test mod:[/b] remove [code]CoastalKingdom.dll[/code]. It's built in from 1.7.0 and must not run alongside.

[b]Heavily modded?[/b] Generate with only Rivers Restored active, save, then re-enable the rest. [hr][/hr] [h2]Performance Notes[/h2] Many rivers plus flow animation can be CPU-heavy: set [code]EnableRibbonAnimation = false[/code] if you see stutter. v1.6.2 fixed the main-menu stutter from earlier versions. On coast maps keep [code]Flow Bias Strength[/code] around 0.1–0.3; higher values flood the low side.
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
