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

                var key = new RiverWaterAreaBuilder.WaterAreaBoundsKey(minX, minZ, maxX, maxZ);
                if (RiverWaterAreaBuilder.RiverWaterAreaBounds.Contains(key))
                {
                    RiverFishAreaIds.Add(_id);
                    if (RiverFishAreaIds.Count <= 20)  // throttle log spam
                        Log($"FishArea id={_id} tagged as river-fishing-area (bounds match)");
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
                // Set isLoadedGame=false via property setter or backing field.
                bool set = false;
                // Try 1: property setter (protected set is accessible via reflection)
                var setter = isLoadedProp.GetSetMethod(true); // true = include non-public
                if (setter != null)
                {
                    setter.Invoke(gmInstance, new object[] { false });
                    set = true;
                }
                // Try 2: backing field (name varies by compiler)
                if (!set)
                {
                    foreach (string candidate in new[] {
                        "<isLoadedGame>k__BackingField",
                        "isLoadedGame",
                        "_isLoadedGame",
                        "m_isLoadedGame" })
                    {
                        var bf = gmType.GetField(candidate,
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        if (bf != null && bf.FieldType == typeof(bool))
                        {
                            bf.SetValue(gmInstance, false);
                            set = true;
                            break;
                        }
                    }
                }
                if (set)
                {
                    _forcedIsLoadedGameFalse = true;
                    Log("FishingManager.Initialize PREFIX: fishAreas empty on loaded game — temporarily forcing isLoadedGame=false for fish-area creation.");
                }
                else
                {
                    Log("FishingManager.Initialize PREFIX: could not set isLoadedGame — cannot force fish creation.");
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
                            var prop = gmType!.GetProperty("isLoadedGame",
                                BindingFlags.Public | BindingFlags.Instance);
                            var setter = prop?.GetSetMethod(true);
                            if (setter != null)
                                setter.Invoke(gmInstance, new object[] { true });
                            Log("FishingManager.Initialize POSTFIX: restored isLoadedGame=true.");
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
                Log($"FishingManager.Initialize done. fishAreas.Count={totalFa} (lakes={lakeFa}, rivers={totalRiver})");
            }
            catch (Exception ex)
            {
                Log($"FishingManagerInitializePostfix exception: {ex.Message}");
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
        /// untouched (they're not in RiverFishAreaIds).</summary>
        private static void CreateFishingAreasPostfix(IList __result)
        {
            try
            {
                int multiplier = RiversRestoredMod.GetEffectiveValues().FishingAreaMultiplier;
                if (multiplier <= 1 || __result == null || __result.Count == 0) return;
                if (RiverFishAreaIds.Count == 0) return;

                int origCount = __result.Count;
                int riverAreasFound = 0;

                for (int i = 0; i < origCount; i++)
                {
                    var area = __result[i];
                    int id = TryGetFishingAreaId(area);
                    if (RiverFishAreaIds.Contains(id))
                    {
                        for (int n = 1; n < multiplier; n++)
                            __result.Add(area);
                        riverAreasFound++;
                    }
                }

                if (riverAreasFound > 0)
                {
                    Log($"Multiplied {riverAreasFound} river fishing-area entry(ies) by {multiplier}× — list now {__result.Count} (was {origCount})");
                }
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
    }
}
