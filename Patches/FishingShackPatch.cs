using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Pangu-style fishing-area multiplier, scoped to OUR rivers only.
    ///
    /// The problem: FF spawns one FishArea per WaterArea (with size-based
    /// fish count). A single long-river WaterArea polygon ends up with only
    /// ~3 fishing-zone markers, which trips the FishingManager's "below
    /// numAreasForFullBonus" productivity penalty (-50%) — making rivers
    /// noticeably worse to fish than lakes.
    ///
    /// The fix: postfix on FishingShack/FishingDock.CreateFishingAreas. The
    /// method returns a list of <c>FishingArea</c> within the building's
    /// radius. We identify which entries reference river-FishAreas (tagged
    /// by ID at FishArea construction time, see <see cref="FishAreaCtorPostfix"/>),
    /// then add duplicate entries to inflate the list count by the user's
    /// configured multiplier. FF's productivity calc is based on list
    /// count, so this lifts us above the threshold.
    ///
    /// Note on identity: we tag at FishArea ctor by waterType reference
    /// (<see cref="RiverWaterAreaBuilder.RiverWaterType"/> — the cloned SO
    /// we created). That makes "is this from our river" a one-instance
    /// reference compare, no bounds heuristics, no name matching.
    /// </summary>
    internal static class FishingShackPatch
    {
        /// <summary>FishArea IDs whose source WaterArea uses our cloned river
        /// WaterType. Populated at FishArea ctor postfix; consulted by the
        /// CreateFishingAreas postfix to decide which entries to multiply.</summary>
        public static readonly HashSet<int> RiverFishAreaIds = new HashSet<int>();

        /// <summary>River FishArea id → filled water-cell count of its polygon
        /// (popcount of the WaterArea mask, captured when the FishArea is tagged).
        /// Used to size the base fish pool by actual river size — see
        /// RiverFishPerCell. Empty/absent → fall back to the vanilla curve base.</summary>
        public static readonly Dictionary<int, int> RiverFishAreaCells = new Dictionary<int, int>();

        /// <summary>ALL water-area FishArea id → filled water-cell count (popcount of
        /// the WaterArea mask), captured at ctor for lakes AND rivers. Used to size
        /// lake/pond fish by area too (see ScaleLakeFish). River subset mirrored in
        /// <see cref="RiverFishAreaCells"/>.</summary>
        public static readonly Dictionary<int, int> AllPolyCells = new Dictionary<int, int>();

        /// <summary>Per-river polygon descriptors (world bbox + cell count) captured
        /// during Initialize, reused to scale the dict again after FishingManager.Load
        /// (which replaces the dict with saved objects). Matched by world bbox, so
        /// stable across the id churn between Initialize and the saved dict.</summary>
        private static readonly System.Collections.Generic.List<PolyDesc> _riverPolys =
            new System.Collections.Generic.List<PolyDesc>();

        /// <summary>Max world-units corner-distance for a non-river FishArea to be
        /// treated as a river POLYGON (its bbox == a polygon's). Lakes sit far away,
        /// so this cleanly separates the river polygon from lakes.</summary>
        private const float RiverBboxMatchEpsilon = 2.0f;

        /// <summary>Set by prefix, read by postfix. When true, the prefix
        /// temporarily forced isLoadedGame=false so Initialize creates fish
        /// areas from scratch (because FishingManager.Load() deserialization
        /// returned empty). Postfix restores the original value.</summary>
        private static bool _forcedIsLoadedGameFalse = false;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                // ── 1) Tag FishArea IDs at construction time ────────────────
                Type? fishAreaType = AccessTools.TypeByName("FishArea");
                if (fishAreaType != null)
                {
                    // Find the ctor that takes a WaterAreaInfo (there are two
                    // overloads in FF — Bounds-based and WaterAreaInfo-based;
                    // we want the WaterAreaInfo one because its WaterArea is
                    // what we tagged).
                    foreach (var ctor in fishAreaType.GetConstructors())
                    {
                        bool hasWai = false;
                        foreach (var p in ctor.GetParameters())
                        {
                            if (p.ParameterType.Name == "WaterAreaInfo") { hasWai = true; break; }
                        }
                        if (!hasWai) continue;

                        var stub = typeof(FishingShackPatch).GetMethod(
                            nameof(FishAreaCtorPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic);
                        harmony.Patch(ctor, postfix: new HarmonyMethod(stub));
                        Log("Hooked FishArea(.., WaterAreaInfo, ..) ctor — will tag river IDs at FishArea creation.");
                        break;
                    }
                }
                else
                {
                    Log("FishArea type not found — fishing multiplier disabled.");
                }

                // ── 2) Postfix CreateFishingAreas on shack + dock ───────────
                Type? shackType = AccessTools.TypeByName("FishingShack");
                Type? dockType = AccessTools.TypeByName("FishingDock");
                if (shackType != null) PatchCreateFishingAreas(harmony, shackType);
                if (dockType != null) PatchCreateFishingAreas(harmony, dockType);

                // ── 3) DIAGNOSTIC: postfix FishingManager.Initialize ────────
                //
                // Log what FishingManager actually built so we can diagnose
                // the "lakes have no fish" symptom. We want to see:
                //   - how many water areas FF iterated
                //   - per-area: id, area, startFish, numFishSchools, type name
                //   - how many river FishAreas (riverInfos path) it built
                // If this loop never fires or aborts early, lakes won't get
                // FishAreas at all → shack sees zero fishing areas.
                Type? fmType = AccessTools.TypeByName("FishingManager");
                if (fmType != null)
                {
                    var initMI = fmType.GetMethod("Initialize",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (initMI != null)
                    {
                        var prefixStub = typeof(FishingShackPatch).GetMethod(
                            nameof(FishingManagerInitializePrefix),
                            BindingFlags.Static | BindingFlags.NonPublic);
                        var postfixStub = typeof(FishingShackPatch).GetMethod(
                            nameof(FishingManagerInitializePostfix),
                            BindingFlags.Static | BindingFlags.NonPublic);
                        harmony.Patch(initMI,
                            prefix: new HarmonyMethod(prefixStub),
                            postfix: new HarmonyMethod(postfixStub));
                        Log("Hooked FishingManager.Initialize (prefix=fish safety net, postfix=diagnostic dump).");
                    }

                    // ── 3b) Postfix FishingManager.Load ─────────────────────
                    // On a loaded game, Load() REPLACES the whole fishAreas dict
                    // with the saved objects AFTER Initialize runs — discarding the
                    // Initialize-time river scaling. Re-apply the scaling here so it
                    // sticks on reloaded saves (this is the dict the shack reads).
                    var loadMI = fmType.GetMethod("Load",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (loadMI != null)
                    {
                        var loadPost = typeof(FishingShackPatch).GetMethod(
                            nameof(FishingManagerLoadPostfix),
                            BindingFlags.Static | BindingFlags.NonPublic);
                        harmony.Patch(loadMI, postfix: new HarmonyMethod(loadPost));
                        Log("Hooked FishingManager.Load (re-scale river fish after the saved-dict restore).");
                    }
                    else
                    {
                        Log("FishingManager.Load not found — reloaded saves won't re-scale river fish.");
                    }
                }

                // ── 4) DIAGNOSTIC: postfix UIFishingProductivitySubWidget.UpdateText
                //
                // Capture EXACTLY what the info-panel "Fish Count" line resolved
                // to: the chosen GetFishingAreaWithMostFish().id and the
                // GetMaxFish it displayed. Auto-fires whenever a shack panel
                // refreshes (throttled), so no user hotkey needed.
                Type? widgetType = AccessTools.TypeByName("UIFishingProductivitySubWidget");
                if (widgetType != null)
                {
                    var updMI = widgetType.GetMethod("UpdateText",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    if (updMI != null)
                    {
                        var post = typeof(FishingShackPatch).GetMethod(
                            nameof(UpdateTextPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                        harmony.Patch(updMI, postfix: new HarmonyMethod(post));
                        Log("Hooked UIFishingProductivitySubWidget.UpdateText (diagnostic: dumps panel Fish Count source).");
                    }
                    else
                    {
                        Log("UIFishingProductivitySubWidget.UpdateText not found.");
                    }
                }
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Warning($"[RR][Fish] Apply failed: {ex}");
            }
        }

        public static void ResetForSceneLoad()
        {
            RiverFishAreaIds.Clear();
            RiverFishAreaCells.Clear();
            AllPolyCells.Clear();
            _riverPolys.Clear();
        }

        // ── Hook bodies ─────────────────────────────────────────────────────
        /// <summary>Postfix on FishArea ctor. Looks up the supplied
        /// WaterAreaInfo's waterArea bounds; if those bounds match one of
        /// the river WaterAreas we registered, the new FishArea's id is
        /// added to the river-id set for the CreateFishingAreas postfix
        /// to multiply later.
        ///
        /// Bounds matching (instead of waterType reference compare) because
        /// we no longer clone the WaterType — see RiverWaterAreaBuilder
        /// history note.</summary>
        private static void FishAreaCtorPostfix(object __instance, int _id, object _waterAreaInfo)
        {
            try
            {
                if (_waterAreaInfo == null) return;

                var waField = _waterAreaInfo.GetType().GetField("waterArea",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var wa = waField?.GetValue(_waterAreaInfo);
                if (wa == null) return;

                Type waType = wa.GetType();
                var minXF = waType.GetField("minX");
                var minZF = waType.GetField("minZ");
                var maxXF = waType.GetField("maxX");
                var maxZF = waType.GetField("maxZ");
                if (minXF == null || minZF == null || maxXF == null || maxZF == null) return;

                int minX = (int)minXF.GetValue(wa);
                int minZ = (int)minZF.GetValue(wa);
                int maxX = (int)maxXF.GetValue(wa);
                int maxZ = (int)maxZF.GetValue(wa);

                // Popcount the polygon mask now (the WaterArea's `points` bool[,])
                // so we can size the fish pool by the water body's actual filled-cell
                // count rather than FF's saturating area→maxFish curve. Done for ALL
                // water areas (lakes AND rivers) — bbox would massively overestimate
                // (water fills only a fraction of its bbox). Captured at ctor because
                // the FishArea doesn't keep the WaterArea/mask afterward.
                int cells = 0;
                try
                {
                    var mask = waType.GetField("points")?.GetValue(wa) as bool[,];
                    if (mask != null) foreach (bool b in mask) if (b) cells++;
                }
                catch { }
                AllPolyCells[_id] = cells;

                var key = new RiverWaterAreaBuilder.WaterAreaBoundsKey(minX, minZ, maxX, maxZ);
                if (RiverWaterAreaBuilder.RiverWaterAreaBounds.Contains(key))
                {
                    RiverFishAreaIds.Add(_id);
                    RiverFishAreaCells[_id] = cells;

                    if (RiverFishAreaIds.Count <= 20)  // throttle log spam
                        Log($"FishArea id={_id} tagged as river-fishing-area (bounds match, filledCells={cells})");
                }
            }
            catch (Exception ex)
            {
                Log($"FishAreaCtorPostfix exception: {ex.Message}");
            }
        }

        /// <summary>Prefix on FishingManager.Initialize — safety net for reload.
        ///
        /// On a loaded game, Initialize skips all fish-area creation (lakes AND
        /// rivers) because it expects FishingManager.Load() to have deserialized
        /// them from the save. But Load() uses ES2 to deserialize
        /// <c>Dictionary&lt;int, FishArea&gt;</c>, which can silently return an
        /// empty dict when the FishArea objects reference Unity types that don't
        /// survive serialization (vanilla FF never exercises river FishAreas).
        ///
        /// Fix: if fishAreas is empty AND this is a loaded game, temporarily
        /// flip <c>isLoadedGame</c> to false so Initialize's own creation logic
        /// runs. The postfix restores the flag.</summary>
        private static void FishingManagerInitializePrefix(object __instance)
        {
            _forcedIsLoadedGameFalse = false;
            try
            {
                // Get GameManager.isLoadedGame
                Type? gmType = AccessTools.TypeByName("GameManager");
                if (gmType == null) return;
                var gmInstance = UnityEngine.Object.FindObjectOfType(gmType);
                if (gmInstance == null) return;

                var isLoadedProp = gmType.GetProperty("isLoadedGame",
                    BindingFlags.Public | BindingFlags.Instance);
                if (isLoadedProp == null) return;
                bool isLoaded = (bool)(isLoadedProp.GetValue(gmInstance) ?? false);
                if (!isLoaded) return; // fresh gen — Initialize will create normally

                // Check if fishAreas is empty (Load() deserialization failed)
                Type fmType = __instance.GetType();
                var faField = fmType.GetField("fishAreas",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var fishAreas = faField?.GetValue(__instance) as System.Collections.IDictionary;
                if (fishAreas != null && fishAreas.Count > 0) return; // Load() worked

                // fishAreas is empty on a loaded game — force Initialize to create them.
                // GameManager.isLoadedGame is a computed property that delegates
                // to PreGameInitializer.isLoadedGame (which IS an auto-prop with
                // a protected setter). We need to go through the chain:
                //   GameManager._preGameInitializer → PreGameInitializer.isLoadedGame
                bool set = false;
                try
                {
                    // Get PreGameInitializer via GameManager.preGameInitializer property
                    var pgiProp = gmType.GetProperty("preGameInitializer",
                        BindingFlags.Public | BindingFlags.Instance);
                    var pgiField = gmType.GetField("_preGameInitializer",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    object? pgi = pgiProp?.GetValue(gmInstance)
                                  ?? pgiField?.GetValue(gmInstance);
                    if (pgi != null)
                    {
                        Type pgiType = pgi.GetType();
                        // Try the property setter on PreGameInitializer
                        var pgiIsLoadedProp = pgiType.GetProperty("isLoadedGame",
                            BindingFlags.Public | BindingFlags.Instance);
                        var pgiSetter = pgiIsLoadedProp?.GetSetMethod(true);
                        if (pgiSetter != null)
                        {
                            pgiSetter.Invoke(pgi, new object[] { false });
                            set = true;
                        }
                        // Fallback: backing field on PreGameInitializer
                        if (!set)
                        {
                            foreach (string candidate in new[] {
                                "<isLoadedGame>k__BackingField",
                                "_isLoadedGame",
                                "isLoadedGame" })
                            {
                                var bf = pgiType.GetField(candidate,
                                    BindingFlags.NonPublic | BindingFlags.Instance);
                                if (bf != null && bf.FieldType == typeof(bool))
                                {
                                    bf.SetValue(pgi, false);
                                    set = true;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception pgiEx)
                {
                    Log($"FishingManager.Initialize PREFIX: PGI access failed: {pgiEx.Message}");
                }
                if (set)
                {
                    _forcedIsLoadedGameFalse = true;
                    Log("FishingManager.Initialize PREFIX: fishAreas empty on loaded game — temporarily forcing isLoadedGame=false via PreGameInitializer.");
                }
                else
                {
                    Log("FishingManager.Initialize PREFIX: could not set isLoadedGame on PreGameInitializer — cannot force fish creation.");
                }
            }
            catch (Exception ex)
            {
                Log($"FishingManagerInitializePrefix exception: {ex.Message}");
            }
        }

        /// <summary>Observability: log FishArea / riverInfo counts at the
        /// end of FishingManager.Initialize. Also restores isLoadedGame if
        /// the prefix temporarily forced it to false.</summary>
        private static void FishingManagerInitializePostfix(object __instance)
        {
            try
            {
                // Restore isLoadedGame if prefix forced it
                if (_forcedIsLoadedGameFalse)
                {
                    _forcedIsLoadedGameFalse = false;
                    try
                    {
                        Type? gmType = AccessTools.TypeByName("GameManager");
                        var gmInstance = gmType != null ? UnityEngine.Object.FindObjectOfType(gmType) : null;
                        if (gmInstance != null)
                        {
                            // Same PGI chain as prefix
                            var pgiProp = gmType!.GetProperty("preGameInitializer",
                                BindingFlags.Public | BindingFlags.Instance);
                            var pgiField = gmType.GetField("_preGameInitializer",
                                BindingFlags.NonPublic | BindingFlags.Instance);
                            object? pgi = pgiProp?.GetValue(gmInstance)
                                          ?? pgiField?.GetValue(gmInstance);
                            if (pgi != null)
                            {
                                var pgiIsLoadedProp = pgi.GetType().GetProperty("isLoadedGame",
                                    BindingFlags.Public | BindingFlags.Instance);
                                var setter = pgiIsLoadedProp?.GetSetMethod(true);
                                if (setter != null)
                                    setter.Invoke(pgi, new object[] { true });
                            }
                            Log("FishingManager.Initialize POSTFIX: restored isLoadedGame=true via PreGameInitializer.");
                        }
                    }
                    catch (Exception rex)
                    {
                        Log($"FishingManager.Initialize POSTFIX: failed to restore isLoadedGame: {rex.Message}");
                    }
                }

                Type fmType = __instance.GetType();
                var faField = fmType.GetField("fishAreas",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var fishAreas = faField?.GetValue(__instance) as System.Collections.IDictionary;
                int totalFa = fishAreas?.Count ?? -1;

                var riField = fmType.GetField("riverInfos",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var riverInfos = riField?.GetValue(__instance) as System.Collections.IList;
                int totalRiver = riverInfos?.Count ?? -1;

                int lakeFa = totalFa - totalRiver;

                // ── Size + scale fish population on river FishAreas ─────────
                // Since v1.5.4 each river is ONE merged polygon, and FF's
                // area→maxFish curve saturates, so a huge map-bisecting river
                // gets the same capped maxFish (~few hundred) as a pond. We
                // instead size the BASE pool by the river's actual filled-cell
                // count (RiverFishAreaCells, popcounted at tag time):
                //     base = max(vanillaCurveMaxFish, filledCells × RiverFishPerCell)
                // then apply the existing FishingAreaMultiplier on top (left
                // as-is). fishCount scales by the same factor to keep fill ratio.
                // RR's one river exists as TWO FishAreas: (1) the stamped
                // WaterArea polygon FishArea (bounds-matched into
                // RiverFishAreaCells, sized by its OWN popcount) and (2) the
                // FishArea FF's river loop builds for the WaterPath via FindRivers
                // (isRiver==true, keyed by riverInfo.id, maxFish hardcoded off a
                // saturating area curve → only ~600 even for a map-bisecting river).
                //
                // ★ The FISHING SHACKS enroll & display the **isRiver** area, NOT
                // the polygon (proven by the F10 panel dump: every shack entry was
                // the isRiver id, and GetMaxFish read it). So we MUST size the
                // isRiver area — sizing only the polygon never reaches the shack.
                // We match each isRiver area to its river's polygon by WORLD bbox
                // (FishArea stores minX/maxX/minZ/maxZ in world space for BOTH
                // ctors — WaterAreaInfo.minX = ConvertHeightMapWidthToWorld(...),
                // isRiver ctor = bounds.min), then size it off that polygon's cells.
                // We OVERWRITE unconditionally (no vanilla floor): corrects a
                // vanilla-low ~600 on a fresh/old save AND a stale-high value baked
                // by an earlier buggy build (e.g. the summed 16485) — both snap to
                // the true per-river size. Full restock on load is intended.
                // Capture the per-river polygon descriptors NOW (the ctor-tagged
                // RiverFishAreaCells + the freshly-created FishAreas' world bbox
                // are both available here), then scale. The capture is reused at
                // FishingManager.Load time — see FishingManagerLoadPostfix — because
                // on a LOADED game Load() REPLACES the whole fishAreas dict with the
                // saved objects AFTER this postfix runs, discarding any scaling done
                // here. The river geometry is identical across Initialize and Load
                // (RR's stamped WaterAreas persist), so the captured world bboxes
                // still match the loaded areas.
                if (fishAreas != null)
                {
                    BuildRiverPolyDescriptors(fishAreas);
                    ScaleRiverFishAreas(fishAreas, "Init");
                }

                Log($"FishingManager.Initialize done. fishAreas.Count={totalFa} (lakes={lakeFa}, rivers={totalRiver})");
            }
            catch (Exception ex)
            {
                Log($"FishingManagerInitializePostfix exception: {ex.Message}");
            }
        }

        /// <summary>Water-body polygon descriptor: world-space bbox + popcounted
        /// cell count, used to size fish (rivers via their isRiver area, lakes in
        /// place) and matched by bbox so it survives the id churn across Load.
        /// <c>isRiverPoly</c> distinguishes an RR river polygon from a lake/pond;
        /// <c>vanillaMax</c> is FF's curve maxFish at creation (lake boost floor).</summary>
        private struct PolyDesc
        {
            public int faId;
            public int waterAreaId;   // persistent terrain water-area id (survives save/load)
            public int cells;
            public float minX, maxX, minZ, maxZ;
            public bool isRiverPoly;
            public int vanillaMax;
        }

        /// <summary>Capture per-river polygon descriptors (world bbox + cell count)
        /// from the just-created FishAreas whose ids were ctor-tagged into
        /// RiverFishAreaCells. Stored in _riverPolys for reuse at Load time.</summary>
        private static void BuildRiverPolyDescriptors(System.Collections.IDictionary fishAreas)
        {
            _riverPolys.Clear();
            if (fishAreas == null || AllPolyCells.Count == 0) return;
            int rivers = 0, lakes = 0;
            foreach (System.Collections.DictionaryEntry de in fishAreas)
            {
                var fa = de.Value; if (fa == null) continue;
                int faId = (de.Key is int k) ? k : -1;
                if (!AllPolyCells.TryGetValue(faId, out int cells)) continue;   // only WaterAreaInfo polygons (lakes + river poly)
                if (!TryReadBounds(fa, out float minX, out float maxX, out float minZ, out float maxZ)) continue;
                bool isRiverPoly = RiverFishAreaIds.Contains(faId);
                int vanillaMax = ReadMaxFish(fa);   // captured pre-scale (Init creates with the vanilla curve)
                _riverPolys.Add(new PolyDesc
                {
                    faId = faId, waterAreaId = ReadWaterAreaId(fa), cells = cells,
                    minX = minX, maxX = maxX, minZ = minZ, maxZ = maxZ,
                    isRiverPoly = isRiverPoly, vanillaMax = vanillaMax
                });
                if (isRiverPoly) rivers++; else lakes++;
            }
            if (_riverPolys.Count > 0)
                Log($"Captured {_riverPolys.Count} water polygon descriptor(s) for load-time re-scaling ({rivers} river, {lakes} lake/pond).");
        }

        /// <summary>Scale the river FishAreas in <paramref name="fishAreas"/> using the
        /// captured <see cref="_riverPolys"/>. The shacks enroll & DISPLAY the isRiver
        /// area (F10 dump proof), so sizing it is what reaches the panel + gameplay;
        /// the polygon FishArea is sized too. Matching is by world bbox: isRiver areas
        /// take the nearest polygon's cells; a non-river area is only scaled if its
        /// bbox closely matches a polygon (the polygon FishArea itself) — lakes skip.
        /// Overwrites maxFish/fishCount UNCONDITIONALLY so a vanilla-low (~600) AND a
        /// stale-high (e.g. 16485 baked by an older build) both snap to the true size.
        /// Called after Init (fresh gen) AND after Load (loaded game — Load replaces
        /// the dict, discarding the Init-time scaling).</summary>
        private static void ScaleRiverFishAreas(System.Collections.IDictionary fishAreas, string phase)
        {
            if (fishAreas == null || _riverPolys.Count == 0) return;
            int multiplier = RiversRestoredMod.GetEffectiveValues().FishingAreaMultiplier;
            if (multiplier < 1) multiplier = 1;
            float perCell = RiversRestoredMod.RiverFishPerCell?.Value ?? 1.0f;
            if (perCell <= 0f) return;
            bool scaleLakes = RiversRestoredMod.ScaleLakeFish?.Value ?? true;
            int scaled = 0, lakesScaled = 0;
            foreach (System.Collections.DictionaryEntry de in fishAreas)
            {
                var fa = de.Value; if (fa == null) continue;
                int faId = (de.Key is int k) ? k : -1;
                try
                {
                    Type faType = fa.GetType();
                    var maxFishBF = faType.GetField("<maxFish>k__BackingField",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    var fishCountBF = faType.GetField("<fishCount>k__BackingField",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (maxFishBF == null) continue;
                    if (!TryReadBounds(fa, out float minX, out float maxX, out float minZ, out float maxZ)) continue;
                    bool isR = ReadIsRiver(fa, faType);

                    int target;
                    string tag;
                    if (isR)
                    {
                        // River isRiver area (what the shack reads) → nearest RIVER
                        // polygon's cells × perCell × river multiplier.
                        int best = NearestPoly(minX, maxX, minZ, maxZ, riverOnly: true, out _);
                        if (best < 0) continue;
                        int cells = _riverPolys[best].cells;
                        target = UnityEngine.Mathf.RoundToInt(cells * perCell) * multiplier;
                        tag = $"isRiver→poly{_riverPolys[best].faId}";
                    }
                    else
                    {
                        // A WaterAreaInfo FishArea (lake / pond / river polygon).
                        // Match by PERSISTENT waterAreaId first (survives save/load
                        // exactly), then fall back to world-bbox. waterAreaId is far
                        // more reliable on reloaded saves than float bbox matching.
                        int waId = ReadWaterAreaId(fa);
                        int best = -1;
                        for (int i = 0; i < _riverPolys.Count; i++)
                            if (_riverPolys[i].waterAreaId == waId) { best = i; break; }
                        if (best < 0)
                        {
                            best = NearestPoly(minX, maxX, minZ, maxZ, riverOnly: false, out float bestD);
                            if (best < 0 || bestD > RiverBboxMatchEpsilon) continue; // not a tracked water body
                        }
                        var d = _riverPolys[best];
                        if (d.isRiverPoly)
                        {
                            target = UnityEngine.Mathf.RoundToInt(d.cells * perCell) * multiplier; // river polygon
                            tag = "river-poly";
                        }
                        else
                        {
                            if (!scaleLakes) continue;                              // lakes left vanilla
                            // Lake/pond: same per-cell ratio, NO river multiplier,
                            // floored at vanilla so it only ever boosts.
                            target = UnityEngine.Mathf.Max(d.vanillaMax, UnityEngine.Mathf.RoundToInt(d.cells * perCell));
                            tag = "lake";
                            lakesScaled++;
                        }
                    }
                    if (target <= 0) continue;

                    int origMax = (int)maxFishBF.GetValue(fa);
                    maxFishBF.SetValue(fa, target);
                    int origCount = 0;
                    if (fishCountBF != null)
                    {
                        origCount = (int)fishCountBF.GetValue(fa);
                        fishCountBF.SetValue(fa, target);   // full restock to cap
                    }
                    scaled++;
                    if (scaled <= 10)
                        Log($"  [{phase}] FishArea id={faId} ({tag}): maxFish {origMax} → {target}, fishCount {origCount} → {target}");
                }
                catch (Exception fex) { Log($"  [{phase}] Fish scale id={faId}: {fex.Message}"); }
            }
            if (scaled > 0)
                Log($"[{phase}] Sized+scaled fish on {scaled} water FishArea(s) — {lakesScaled} lake/pond (perCell={perCell:0.##}, riverMult=×{multiplier}, scaleLakes={scaleLakes}).");
        }

        /// <summary>Index into _riverPolys of the descriptor whose world bbox is
        /// closest (by corner distance) to the given bbox; -1 if none. When
        /// <paramref name="riverOnly"/>, only river polygons are considered.</summary>
        private static int NearestPoly(float minX, float maxX, float minZ, float maxZ, bool riverOnly, out float bestDist)
        {
            int best = -1; float bestD = float.MaxValue;
            for (int i = 0; i < _riverPolys.Count; i++)
            {
                var p = _riverPolys[i];
                if (riverOnly && !p.isRiverPoly) continue;
                float d = UnityEngine.Mathf.Abs(p.minX - minX) + UnityEngine.Mathf.Abs(p.maxX - maxX)
                        + UnityEngine.Mathf.Abs(p.minZ - minZ) + UnityEngine.Mathf.Abs(p.maxZ - maxZ);
                if (d < bestD) { bestD = d; best = i; }
            }
            bestDist = bestD;
            return best;
        }

        /// <summary>Read a FishArea's world-space bounding box (minX/maxX/minZ/maxZ
        /// are public float auto-props on FishArea).</summary>
        private static bool TryReadBounds(object fa, out float minX, out float maxX, out float minZ, out float maxZ)
        {
            minX = maxX = minZ = maxZ = 0f;
            try
            {
                Type t = fa.GetType();
                var pMinX = t.GetProperty("minX", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var pMaxX = t.GetProperty("maxX", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var pMinZ = t.GetProperty("minZ", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var pMaxZ = t.GetProperty("maxZ", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pMinX == null || pMaxX == null || pMinZ == null || pMaxZ == null) return false;
                minX = Convert.ToSingle(pMinX.GetValue(fa));
                maxX = Convert.ToSingle(pMaxX.GetValue(fa));
                minZ = Convert.ToSingle(pMinZ.GetValue(fa));
                maxZ = Convert.ToSingle(pMaxZ.GetValue(fa));
                return true;
            }
            catch { return false; }
        }

        /// <summary>isRiver is a public bool auto-prop on FishArea — read the
        /// getter (GetField on the name silently returns null).</summary>
        private static bool ReadIsRiver(object fa, Type faType)
        {
            try
            {
                var p = faType.GetProperty("isRiver",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null) return (bool)(p.GetValue(fa) ?? false);
                var bf = faType.GetField("<isRiver>k__BackingField",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (bf != null) return (bool)(bf.GetValue(fa) ?? false);
            }
            catch { }
            return false;
        }

        /// <summary>Read a FishArea's current maxFish (auto-prop backing field).</summary>
        private static int ReadMaxFish(object fa)
        {
            try
            {
                var bf = fa.GetType().GetField("<maxFish>k__BackingField",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (bf != null) return (int)bf.GetValue(fa);
            }
            catch { }
            return 0;
        }

        /// <summary>Read a FishArea's persistent waterAreaId (public int auto-prop,
        /// saved/loaded with the area). isRiver areas have 0 (not set). Used as the
        /// primary match key for lake/pond polygons across save/reload.</summary>
        private static int ReadWaterAreaId(object fa)
        {
            try
            {
                var p = fa.GetType().GetProperty("waterAreaId",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null) return (int)(p.GetValue(fa) ?? 0);
                var bf = fa.GetType().GetField("<waterAreaId>k__BackingField",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (bf != null) return (int)(bf.GetValue(fa) ?? 0);
            }
            catch { }
            return 0;
        }

        /// <summary>Postfix on FishingManager.Load. Load() reassigns
        /// <c>fishAreas = reader.ReadDictionary&lt;int,FishArea&gt;()</c>, REPLACING the
        /// dict (and any Initialize-time scaling) with the saved objects. Re-apply
        /// the river scaling to that final dict so reloaded saves show/fish the
        /// correct per-river size. Descriptors were captured at Initialize (runs
        /// before Load); the river geometry is identical, so bbox matching holds.</summary>
        private static void FishingManagerLoadPostfix(object __instance)
        {
            try
            {
                Type fmType = __instance.GetType();
                var faField = fmType.GetField("fishAreas",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var fishAreas = faField?.GetValue(__instance) as System.Collections.IDictionary;
                if (fishAreas == null) { Log("[Load] fishAreas null after Load — nothing to re-scale."); return; }
                if (_riverPolys.Count == 0)
                {
                    Log($"[Load] fishAreas.Count={fishAreas.Count} but no river descriptors captured (Initialize didn't tag) — cannot re-scale.");
                    return;
                }
                Log($"[Load] FishingManager.Load done — fishAreas.Count={fishAreas.Count}; re-scaling river fish (Init-time scaling was discarded by the saved-dict restore).");
                ScaleRiverFishAreas(fishAreas, "Load");
            }
            catch (Exception ex)
            {
                Log($"FishingManagerLoadPostfix exception: {ex.Message}");
            }
        }

        private static void PatchCreateFishingAreas(HarmonyLib.Harmony harmony, Type buildingType)
        {
            try
            {
                // Be explicit about the (Vector3, float) overload — there
                // are multiple CreateFishingAreas methods on the building
                // class and Harmony's IL compiler chokes on the ambiguous
                // pick if we just pass the name. The call site at
                // line 134662 in Assembly-CSharp uses these exact param
                // types: component.CreateFishingAreas(transform.position,
                // component.fishingRadius).
                MethodInfo? mi = AccessTools.Method(buildingType, "CreateFishingAreas",
                    new[] { typeof(UnityEngine.Vector3), typeof(float) });

                // Fall back to no-arg signature if the (Vector3, float) one
                // doesn't exist on this building class.
                if (mi == null)
                    mi = AccessTools.Method(buildingType, "CreateFishingAreas", new Type[0]);

                if (mi == null)
                {
                    // Last resort: enumerate and pick whichever exists
                    foreach (var candidate in buildingType.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (candidate.Name == "CreateFishingAreas")
                        {
                            mi = candidate;
                            Log($"  ↳ found non-standard {buildingType.Name}.CreateFishingAreas overload " +
                                $"with {candidate.GetParameters().Length} param(s)");
                            break;
                        }
                    }
                }

                if (mi == null)
                {
                    Log($"{buildingType.Name}.CreateFishingAreas not found (any overload)");
                    return;
                }
                var stub = typeof(FishingShackPatch).GetMethod(
                    nameof(CreateFishingAreasPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(mi, postfix: new HarmonyMethod(stub));
                Log($"Hooked {buildingType.Name}.CreateFishingAreas " +
                    $"({mi.GetParameters().Length}-param overload)");
            }
            catch (Exception ex)
            {
                Log($"Patch failed for {buildingType.Name}: {ex.Message}");
            }
        }

        /// <summary>Postfix that multiplies entries pointing at our river
        /// FishAreas. The list parameter (__result) is the building's local
        /// list of in-radius fishing areas. Adding duplicate entries
        /// inflates the count above FishingManager.numAreasForFullBonus,
        /// killing the productivity penalty. Vanilla lake/ocean entries
        /// untouched (they're not in RiverFishAreaIds).
        ///
        /// IDEMPOTENT: collects unique river areas, strips ALL existing
        /// river entries (including any previous duplicates), then re-adds
        /// exactly multiplier copies of each unique. Safe to call multiple
        /// times — result is always unique_count × multiplier.</summary>
        private static void CreateFishingAreasPostfix(IList __result)
        {
            try
            {
                int multiplier = RiversRestoredMod.GetEffectiveValues().FishingAreaMultiplier;
                if (multiplier <= 1 || __result == null || __result.Count == 0) return;
                if (RiverFishAreaIds.Count == 0) return;

                // Collect DISTINCT river FishingArea objects by REFERENCE
                // identity — NOT by water-area id.
                //
                // Vanilla CreateFishingAreas creates one FishingArea (each with
                // its OWN marker GameObject) per shore point, and many shore
                // points on a single river share the same water-area id. The
                // old code deduped by id, kept one FishingArea, then stripped
                // the rest from the tracked list with RemoveAt() WITHOUT
                // destroying their markers. Those dropped markers became
                // orphans the shack no longer tracked, so neither
                // ClearFishingAreas nor SetFishingAreasVisibility could ever
                // reclaim/hide them — every work-area rebuild leaked another
                // batch of stuck-visible markers ("FISHING AREA" debug cubes
                // piling up along the shore).
                //
                // Reference dedup preserves every real marker (all are re-added
                // below) while still collapsing RR's OWN re-added duplicates
                // (which are reference-equal to an original), keeping this
                // postfix idempotent if it ever runs on an already-multiplied
                // list.
                var uniqueRiverAreas = new System.Collections.Generic.List<object>();
                for (int i = 0; i < __result.Count; i++)
                {
                    var area = __result[i];
                    int id = TryGetFishingAreaId(area);
                    if (!RiverFishAreaIds.Contains(id)) continue;

                    bool already = false;
                    foreach (var u in uniqueRiverAreas)
                        if (ReferenceEquals(u, area)) { already = true; break; }
                    if (!already) uniqueRiverAreas.Add(area);
                }
                if (uniqueRiverAreas.Count == 0) return;

                // Strip ALL existing river entries (base + any prior duplicates).
                // Safe: every DISTINCT marker is captured in uniqueRiverAreas
                // above and re-added below, so RemoveAt here orphans nothing.
                for (int i = __result.Count - 1; i >= 0; i--)
                {
                    int id = TryGetFishingAreaId(__result[i]);
                    if (RiverFishAreaIds.Contains(id))
                        __result.RemoveAt(i);
                }

                // Re-add exactly multiplier copies of each unique river area
                foreach (var area in uniqueRiverAreas)
                {
                    for (int n = 0; n < multiplier; n++)
                        __result.Add(area);
                }

                Log($"River fishing areas: {uniqueRiverAreas.Count} unique × {multiplier} = {uniqueRiverAreas.Count * multiplier} entries (list total: {__result.Count})");
            }
            catch (Exception ex)
            {
                Log($"CreateFishingAreasPostfix exception: {ex.Message}");
            }
        }

        private static int TryGetFishingAreaId(object fishingArea)
        {
            try
            {
                var idProp = fishingArea.GetType().GetProperty("id",
                    BindingFlags.Public | BindingFlags.Instance);
                if (idProp != null && idProp.PropertyType == typeof(int))
                    return (int)idProp.GetValue(fishingArea, null);
                var idField = fishingArea.GetType().GetField("id",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (idField != null && idField.FieldType == typeof(int))
                    return (int)idField.GetValue(fishingArea);
            }
            catch { }
            return -1;
        }

        private static void Log(string msg) => RiversRestoredMod.Log.Msg($"[RR][Fish] {msg}");

        // ── DIAGNOSTIC: F10 dumps the truth behind the info-panel "Fish Count" ──
        // Press F10 in-game to dump, for every FishingShack:
        //   • the shack's fishingAreas list: each entry's id + GetFishCount(id)
        //     + GetMaxFish(id)
        //   • GetFishingAreaWithMostFish().id and the GetMaxFish that the
        //     UIFishingProductivitySubWidget panel would display
        //   • the FULL fishAreas dictionary in FishingManager: id → maxFish,
        //     fishCount, isRiver
        // This settles whether the panel reads RR's scaled river FishAreas or
        // resolves to an unscaled lake.
        // Auto-dump from the actual panel refresh. Throttled so dragging a
        // work area (which fires UpdateText repeatedly) doesn't spam.
        private static float _nextWidgetDumpTime = 0f;
        private static void UpdateTextPostfix(object __instance)
        {
            try
            {
                if (!(RiversRestoredMod.VerboseDiagnostics?.Value ?? false)) return;
                float now = UnityEngine.Time.unscaledTime;
                if (now < _nextWidgetDumpTime) return;
                _nextWidgetDumpTime = now + 1.0f;

                Type wt = __instance.GetType();
                var shackField = wt.GetField("fishingShack",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                object? shack = shackField?.GetValue(__instance);
                if (shack == null) { Log("[Panel] UpdateText: fishingShack == null"); return; }

                Type? fmType = AccessTools.TypeByName("FishingManager");
                object? fm = fmType != null ? UnityEngine.Object.FindObjectOfType(fmType) : null;
                var getMaxFishMI = fmType?.GetMethod("GetMaxFish", BindingFlags.Public | BindingFlags.Instance);
                var getFishCountMI = fmType?.GetMethod("GetFishCount",
                    BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);

                Type shackType = shack.GetType();
                var fishingAreasField = shackType.GetField("_fishingAreas",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                var list = fishingAreasField?.GetValue(shack) as System.Collections.IList;
                var getMostFishMI = shackType.GetMethod("GetFishingAreaWithMostFish",
                    BindingFlags.Public | BindingFlags.Instance);

                var ids = new System.Collections.Generic.List<string>();
                if (list != null && fm != null)
                {
                    foreach (var fa in list)
                    {
                        int id = TryGetFishingAreaId(fa);
                        int fc = getFishCountMI != null ? (int)getFishCountMI.Invoke(fm, new object[] { id }) : -1;
                        int mf = getMaxFishMI != null ? (int)getMaxFishMI.Invoke(fm, new object[] { id }) : -1;
                        ids.Add($"id={id}(fc={fc},mf={mf})");
                    }
                }
                object? most = getMostFishMI?.Invoke(shack, null);
                int mostId = most != null ? TryGetFishingAreaId(most) : -999;
                int panel = (most != null && getMaxFishMI != null && fm != null)
                    ? (int)getMaxFishMI.Invoke(fm, new object[] { mostId }) : 0;

                Log($"[Panel] '{(shack as UnityEngine.Object)?.name}' areas={list?.Count ?? -1} :: {string.Join(" | ", ids)}");
                Log($"[Panel]   ⇒ mostFish.id={mostId}, DISPLAYED FishCount = GetMaxFish({mostId}) = {panel}  (RiverFishAreaIds=[{string.Join(",", RiverFishAreaIds)}])");
            }
            catch (Exception ex)
            {
                Log($"[Panel] UpdateTextPostfix exception: {ex.Message}");
            }
        }

        private static bool _dumpKeyWasDown = false;
        public static void CheckDiagnosticHotkey()
        {
            try
            {
                if (!(RiversRestoredMod.VerboseDiagnostics?.Value ?? false)) return;
                bool down = UnityEngine.Input.GetKey(UnityEngine.KeyCode.F10);
                if (down && !_dumpKeyWasDown) DumpFishingState();
                _dumpKeyWasDown = down;
            }
            catch { }
        }

        private static void DumpFishingState()
        {
            try
            {
                Log("──────── F10 FISHING STATE DUMP ────────");

                // 1) Find the FishingManager and dump its full fishAreas dict.
                Type? fmType = AccessTools.TypeByName("FishingManager");
                object? fm = fmType != null ? UnityEngine.Object.FindObjectOfType(fmType) : null;
                if (fm == null) { Log("FishingManager instance not found."); return; }

                var faField = fmType!.GetField("fishAreas",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var fishAreas = faField?.GetValue(fm) as System.Collections.IDictionary;
                var waiField = fmType.GetField("waterAreaIdToId",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var waterAreaIdToId = waiField?.GetValue(fm) as System.Collections.IDictionary;

                Log($"FishingManager.fishAreas.Count = {fishAreas?.Count ?? -1}; RiverFishAreaIds = [{string.Join(",", RiverFishAreaIds)}]");
                if (fishAreas != null)
                {
                    foreach (System.Collections.DictionaryEntry de in fishAreas)
                    {
                        object fa = de.Value;
                        if (fa == null) { Log($"  fishAreas[{de.Key}] = NULL"); continue; }
                        Type t = fa.GetType();
                        int mf = (int)(t.GetProperty("maxFish")?.GetValue(fa) ?? -1);
                        int fc = (int)(t.GetProperty("fishCount")?.GetValue(fa) ?? -1);
                        bool ir = (bool)(t.GetProperty("isRiver")?.GetValue(fa) ?? false);
                        int wa = (int)(t.GetProperty("waterAreaId")?.GetValue(fa) ?? -1);
                        Log($"  fishAreas[{de.Key}] : maxFish={mf}, fishCount={fc}, isRiver={ir}, waterAreaId={wa}");
                    }
                }
                if (waterAreaIdToId != null)
                {
                    var pairs = new System.Collections.Generic.List<string>();
                    foreach (System.Collections.DictionaryEntry de in waterAreaIdToId)
                        pairs.Add($"{de.Key}->{de.Value}");
                    Log($"  waterAreaIdToId: [{string.Join(", ", pairs)}]");
                }

                var getMaxFishMI = fmType.GetMethod("GetMaxFish", BindingFlags.Public | BindingFlags.Instance);
                var getFishCountMI = fmType.GetMethod("GetFishCount",
                    BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);

                // 2) Every FishingShack: dump its tracked fishingAreas + the
                //    panel's resolved value.
                Type? shackType = AccessTools.TypeByName("FishingShack");
                if (shackType == null) { Log("FishingShack type not found."); return; }
                var shacks = UnityEngine.Object.FindObjectsOfType(shackType);
                Log($"FishingShack instances: {shacks.Length}");

                var fishingAreasField = shackType.GetField("_fishingAreas",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                var getMostFishMI = shackType.GetMethod("GetFishingAreaWithMostFish",
                    BindingFlags.Public | BindingFlags.Instance);

                int idx = 0;
                foreach (var shack in shacks)
                {
                    idx++;
                    var list = fishingAreasField?.GetValue(shack) as System.Collections.IList;
                    var ids = new System.Collections.Generic.List<string>();
                    if (list != null)
                    {
                        foreach (var fa in list)
                        {
                            int id = TryGetFishingAreaId(fa);
                            int fc = getFishCountMI != null ? (int)getFishCountMI.Invoke(fm, new object[] { id }) : -1;
                            int mf = getMaxFishMI != null ? (int)getMaxFishMI.Invoke(fm, new object[] { id }) : -1;
                            ids.Add($"id={id}(fc={fc},mf={mf})");
                        }
                    }
                    object? most = getMostFishMI?.Invoke(shack, null);
                    int mostId = most != null ? TryGetFishingAreaId(most) : -999;
                    int panelMaxFish = (most != null && getMaxFishMI != null)
                        ? (int)getMaxFishMI.Invoke(fm, new object[] { mostId }) : 0;
                    Log($"  Shack #{idx} '{(shack as UnityEngine.Object)?.name}': areas.Count={list?.Count ?? -1}");
                    Log($"    entries: {string.Join(" | ", ids)}");
                    Log($"    GetFishingAreaWithMostFish().id = {mostId}  ⇒  PANEL Fish Count = GetMaxFish({mostId}) = {panelMaxFish}");
                }
                Log("──────── END DUMP ────────");
            }
            catch (Exception ex)
            {
                Log($"DumpFishingState exception: {ex}");
            }
        }
    }
}
