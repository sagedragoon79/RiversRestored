using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TerrainGen;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Harmony prefix on TerrainGenerator.PreGenerate() that overrides
    /// riverSettings fields BEFORE the generator's stage pipeline runs.
    ///
    /// PreGenerate is the cleanest hook point: it's a plain void method that
    /// fires on new-map generation before Stage 1 (setup) and well before
    /// Stage 38 (river path generation uses Voronoi). The generator's
    /// riverSettings field is already populated from the game's BuildingData
    /// by this point, so we can read-modify-write safely.
    ///
    /// Does NOT fire on saved-game loads because the save system has its own
    /// deserialization path that doesn't go through PreGenerate. Good — we
    /// only want rivers on new maps.
    /// </summary>
    internal static class RiverSettingsPatch
    {
        // Track which hook actually fires so we can log it once and trim
        // the others on the next iteration.
        private static bool _alreadyApplied = false;

        // ── Cached TerrainGenerator for the carver to find later ──────────
        // FindObjectOfType<TerrainGenerator> in OnUpdate may return null
        // after generation completes (instance recycled). We cache the
        // reference whenever a hook fires — Stage 38 carrier is reliable.
        public static TerrainGenerator? CachedGenerator { get; private set; }

        // ── Option 2: stage-injection guards ──────────────────────────────
        // The sliced runtime pipeline (GenSliced_*) skips the river stages
        // entirely. We re-introduce them by piggy-backing on adjacent stages
        // that DO run, then invoking the river methods reflectively.
        //
        // These guards prevent double-firing: if a future game patch starts
        // running Stage 38 / 60 on its own, we'll detect that via the existing
        // Stage38 postfix and skip our injection.
        private static bool _stage38AlreadyRanThisGen = false;
        private static bool _stage60AlreadyRanThisGen = false;
        // Cached reflected MethodInfos for the river stages we're injecting.
        private static MethodInfo? _miStage38;
        private static MethodInfo? _miStage60;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            Type tgType = AccessTools.TypeByName("TerrainGen.TerrainGenerator");
            if (tgType == null)
            {
                RiversRestoredMod.Log.Error(
                    "[RR] TerrainGen.TerrainGenerator type not found. Aborting patch.");
                return;
            }

            // Multi-hook strategy: we don't yet know which generation entry
            // point fires at runtime (PreGenerate was a miss). Hook every
            // plausible candidate; the prefix is idempotent (guarded by
            // _alreadyApplied) so multiple fires are harmless. Each logs
            // its own name so we can identify the winner next iteration.
            //
            // Primary candidates, from earliest in the pipeline to latest:
            //   1. PreGenerateShared()              — likely shared prep
            //   2. PreGenerate()                    — vanilla non-sliced prep
            //   3. Generate(bool)                   — main entry (non-sliced)
            //   4. GenerateAsync()                  — async version
            //   5. GenerateAsync_RiverPaths_Stage38 — just before river paths
            //
            // If none fire during new-map gen, the runtime uses GenSliced_*
            // IEnumerator variants; we'll switch to those next round.

            TryPatch(harmony, tgType, "PreGenerateShared",          new Type[0]);
            TryPatch(harmony, tgType, "PreGenerate",                new Type[0]);
            TryPatch(harmony, tgType, "Generate",                   new[] { typeof(bool) });
            TryPatch(harmony, tgType, "GenerateAsync",              new Type[0]);
            TryPatch(harmony, tgType, "GenerateAsync_RiverPaths_Stage38", new Type[0]);

            // ── Postfix on river path stage to see what was produced ──
            try
            {
                MethodInfo stage38 = AccessTools.Method(tgType,
                    "GenerateAsync_RiverPaths_Stage38");
                MethodInfo postStub = typeof(RiverSettingsPatch).GetMethod(
                    nameof(Stage38Postfix), BindingFlags.Static | BindingFlags.NonPublic);
                if (stage38 != null && postStub != null)
                {
                    harmony.Patch(stage38, postfix: new HarmonyMethod(postStub));
                    RiversRestoredMod.Log.Msg(
                        "[RR] Hooked Stage38 POSTFIX (river-paths result inspector)");
                }
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Warning($"[RR] Stage38 postfix hook failed: {ex.Message}");
            }

            // ── Option 2: Stage-injection postfixes ──────────────────────────
            // Cache MethodInfos for the river stages we'll be invoking
            // manually. If either lookup fails, the injection postfixes
            // bail silently (logged once on miss).
            _miStage38 = AccessTools.Method(tgType, "GenerateAsync_RiverPaths_Stage38",  new Type[0]);
            _miStage60 = AccessTools.Method(tgType, "GenerateAsync_RiverGeometry_Stage60", new Type[0]);
            RiversRestoredMod.Log.Msg(
                $"[RR] Stage-method cache: Stage38={(_miStage38 != null ? "OK" : "MISSING")}  " +
                $"Stage60={(_miStage60 != null ? "OK" : "MISSING")}");

            // ── Stage 60 PREFIX dumper — instance-field state at entry ─────
            // Stage 60's GridTrace NRE'd at offset 0x76. The null is almost
            // certainly an INSTANCE field on TerrainGenerator (not in
            // _generationData, which we already dumped fully populated).
            // Hook a prefix that dumps every instance field so we can find
            // which one is null and either set it ourselves or invoke the
            // missing prerequisite stage.
            try
            {
                if (_miStage60 != null)
                {
                    MethodInfo dumpStub = typeof(RiverSettingsPatch).GetMethod(
                        nameof(Stage60PrefixDumper),
                        BindingFlags.Static | BindingFlags.NonPublic);
                    if (dumpStub != null)
                    {
                        harmony.Patch(_miStage60, prefix: new HarmonyMethod(dumpStub));
                        RiversRestoredMod.Log.Msg(
                            "[RR] Hooked Stage60 PREFIX (instance-field state dumper)");
                    }
                }
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Warning(
                    $"[RR] Stage60 prefix hook failed: {ex.Message}");
            }

            // Hook postfixes on the carrier stages — these are the void
            // sliced-pipeline stages immediately ADJACENT to the river
            // stages we want to inject. Each carrier was reported to fire
            // during real generation in the prior session.
            //
            //   Stage 37 (PreWater)  fires  →  we then call Stage 38 (RiverPaths)
            //   Stage 50 (Water)     fires  →  we then call Stage 60 (RiverGeometry)
            //
            // Order matters: Stage 38 must run BEFORE the lakes/water at
            // Stage 50, so injecting from Stage 37's postfix gives the
            // river paths a chance to register before water painting.
            // Stage 60 runs AFTER water so river geometry can paint into
            // the established water layer.
            HookCarrier(harmony, tgType, "GenerateAsync_PreWater_Stage37",
                        nameof(InjectStage38Postfix));
            HookCarrier(harmony, tgType, "GenerateAsync_Water_Stage50",
                        nameof(InjectStage60Postfix));

            // ── Late-stage carve hooks ──────────────────────────────────
            // The carver needs Terrain.activeTerrain != null (the Unity
            // Terrain GameObject must be instantiated). That doesn't happen
            // until late in the pipeline. Hook every plausible late stage —
            // first one that fires with a live Terrain wins; carver's
            // _carved guard prevents duplicate runs.
            foreach (var name in new[] {
                "GenerateAsync_RiverGeometry_Stage60",
                "GenerateAsync_Roads_Stage70",
                "GenerateAsync_PaintBiomes_Stage40",
                "GenerateAsync_Paint_Stage90",
                "GenerateAsync_Paint_Stage91",
                "GenerateAsync_Paint_Stage93",
                "GenerateAsync_Trees_Stage95",
                "GenerateAsync_WaterDetails_Stage97",
                "GenerateAsync_Finalize_Stage99"
            })
            {
                HookCarrier(harmony, tgType, name, nameof(LateCarvePostfix));
            }
        }

        /// <summary>
        /// Postfix on a late stage. Tries to carve rivers if Terrain is
        /// available. Safe to fire from many stages — carver bails silently
        /// when Terrain isn't ready, and is guarded against duplicate runs.
        ///
        /// Also dumps waterAreas.Count + ours-count so we can spot which
        /// stage strips our additions (they vanish between Stage 38 add
        /// and save). The __originalMethod parameter is supplied by Harmony
        /// and lets us label which carrier fired.
        /// </summary>
        private static void LateCarvePostfix(TerrainGenerator __instance, MethodBase __originalMethod)
        {
            if (!RiversRestoredMod.RiversEnabled.Value) return;
            try
            {
                LogWaterAreaCount(__instance, __originalMethod?.Name ?? "?");
                RiverCarver.CarveAllRivers(__instance);
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Error($"[RR] LateCarvePostfix failed: {ex}");
            }
        }

        /// <summary>Diagnostic: dump current waterAreas.Count + how many
        /// of our tracked river bounds are still present in the list.
        /// Reveals which gen stage strips our additions — the stage where
        /// ours-count drops from N to 0 is the culprit.</summary>
        private static void LogWaterAreaCount(TerrainGenerator tg, string stageName)
        {
            try
            {
                var gdField = AccessTools.Field(typeof(TerrainGenerator), "_generationData");
                var gd = gdField?.GetValue(tg);
                if (gd == null) return;
                var waField = gd.GetType().GetField("waterAreas",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var was = waField?.GetValue(gd) as System.Collections.IList;
                if (was == null) return;

                int total = was.Count;
                int ours = 0;
                Type? waType = AccessTools.TypeByName("TerrainGen.TerrainGenerator+WaterArea");
                var fMinX = waType?.GetField("minX");
                var fMinZ = waType?.GetField("minZ");
                var fMaxX = waType?.GetField("maxX");
                var fMaxZ = waType?.GetField("maxZ");
                if (fMinX != null && fMinZ != null && fMaxX != null && fMaxZ != null)
                {
                    for (int i = 0; i < was.Count; i++)
                    {
                        var entry = was[i];
                        if (entry == null) continue;
                        int minX = (int)fMinX.GetValue(entry);
                        int minZ = (int)fMinZ.GetValue(entry);
                        int maxX = (int)fMaxX.GetValue(entry);
                        int maxZ = (int)fMaxZ.GetValue(entry);
                        var key = new RiverWaterAreaBuilder.WaterAreaBoundsKey(minX, minZ, maxX, maxZ);
                        if (RiverWaterAreaBuilder.RiverWaterAreaBounds.Contains(key)) ours++;
                    }
                }
                RiversRestoredMod.Log.Msg(
                    $"[RR][StageDump] {stageName}: waterAreas.Count={total}  ours={ours}/" +
                    $"{RiverWaterAreaBuilder.RiverWaterAreaBounds.Count}");
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Warning($"[RR] LogWaterAreaCount failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Helper: hook a postfix on a "carrier" sliced-pipeline stage.
        /// The postfix manually invokes the corresponding river stage
        /// (Stage 38 or 60) that the sliced pipeline normally skips.
        /// </summary>
        private static void HookCarrier(HarmonyLib.Harmony harmony, Type tgType,
                                         string carrierName, string postfixStubName)
        {
            try
            {
                MethodInfo carrier = AccessTools.Method(tgType, carrierName, new Type[0]);
                if (carrier == null)
                {
                    RiversRestoredMod.Log.Warning(
                        $"[RR] Carrier stage not found: {carrierName} — injection disabled.");
                    return;
                }
                MethodInfo stub = typeof(RiverSettingsPatch).GetMethod(
                    postfixStubName, BindingFlags.Static | BindingFlags.NonPublic);
                if (stub == null)
                {
                    RiversRestoredMod.Log.Warning(
                        $"[RR] Injection stub missing: {postfixStubName}");
                    return;
                }
                harmony.Patch(carrier, postfix: new HarmonyMethod(stub));
                RiversRestoredMod.Log.Msg(
                    $"[RR] Hooked carrier {carrierName} → injecting via {postfixStubName}");
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Warning(
                    $"[RR] Carrier hook failed for {carrierName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix on Stage 37 (PreWater). Manually invokes Stage 38
        /// (RiverPaths) — the Voronoi-based river-path generator that the
        /// sliced runtime pipeline skips. Guarded so a vanilla future patch
        /// that re-enables Stage 38 won't double-fire it.
        /// </summary>
        private static void InjectStage38Postfix(TerrainGenerator __instance)
        {
            if (!RiversRestoredMod.RiversEnabled.Value) return;
            if (_miStage38 == null) return;
            if (IsLoadingSavedMap(__instance)) return;
            if (_stage38AlreadyRanThisGen)
            {
                RiversRestoredMod.Log.Msg(
                    "[RR] Stage38 already ran this gen — skipping injection.");
                return;
            }
            try
            {
                RiversRestoredMod.Log.Msg(
                    "[RR] >>> Injecting Stage 38 (RiverPaths) after Stage 37 (PreWater)…");
                _miStage38.Invoke(__instance, null);
                RiversRestoredMod.Log.Msg(
                    "[RR] <<< Stage 38 injection completed without exception.");

                // ── First WaterArea registration pass (early/visibility) ──
                // FishingManager allocates fish nodes somewhere between
                // Stage 38 and Stage 60, and bases that allocation on what's
                // already in _generationData.waterAreas. If we don't add here,
                // no fish nodes get allocated for our river polygons.
                //
                // Stage 50 (Water) WILL strip these additions when it
                // rebuilds the list — that's what InjectStage60Postfix is
                // for: a SECOND pass at post-Stage-50 timing that re-adds
                // for save persistence. Both passes needed:
                //   Stage 38 add → fish-node allocation visibility
                //   Stage 60 add → survives FF gen rebuild + save serialization
                if (RiversRestoredMod.RiverRegisterAsWaterArea?.Value ?? true)
                {
                    int added = RiverWaterAreaBuilder.BuildAndAddForAllRivers(__instance);
                    if (added > 0)
                    {
                        RiversRestoredMod.Log.Msg(
                            $"[RR] Registered {added} river polygon(s) as WaterAreas at Stage 38 " +
                            "(early visibility for FishingManager allocation; will be re-added " +
                            "at Stage 60 postfix to survive Stage 50 strip).");
                    }
                }
            }
            catch (Exception ex)
            {
                // Unwrap TargetInvocationException so the inner cause is visible
                Exception inner = (ex is TargetInvocationException tie && tie.InnerException != null)
                    ? tie.InnerException : ex;
                RiversRestoredMod.Log.Error(
                    $"[RR] !!! Stage 38 injection threw: {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}");
            }
        }

        /// <summary>
        /// Postfix on Stage 50 (Water). Manually invokes Stage 60
        /// (RiverGeometry) — the geometry painter that turns Stage 38's
        /// river paths into actual carved riverbeds + WaterPath visuals.
        /// </summary>
        private static void InjectStage60Postfix(TerrainGenerator __instance)
        {
            if (!RiversRestoredMod.RiversEnabled.Value) return;
            if (_miStage60 == null) return;
            if (IsLoadingSavedMap(__instance)) return;
            if (_stage60AlreadyRanThisGen)
            {
                RiversRestoredMod.Log.Msg(
                    "[RR] Stage60 already ran this gen — skipping injection.");
                return;
            }
            try
            {
                // Preflight: heal the instance fields the sliced pipeline
                // never initialized. Without this, Stage 60's GridTrace will
                // NRE on a null waterBiome / cachedAreas / etc.
                PrepareForStage60(__instance);

                RiversRestoredMod.Log.Msg(
                    "[RR] >>> Injecting Stage 60 (RiverGeometry) after Stage 50 (Water)…");
                _miStage60.Invoke(__instance, null);
                _stage60AlreadyRanThisGen = true;
                RiversRestoredMod.Log.Msg(
                    "[RR] <<< Stage 60 injection completed without exception.");
            }
            catch (Exception ex)
            {
                Exception inner = (ex is TargetInvocationException tie && tie.InnerException != null)
                    ? tie.InnerException : ex;
                // Demoted to Warning — Stage 60 partial-execution gives us
                // WaterPath visuals + endpoint caps before NREing. The carve
                // failure is expected; we do that ourselves below.
                RiversRestoredMod.Log.Warning(
                    $"[RR] Stage 60 partial (expected): {inner.GetType().Name} at {inner.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            }

            // ── Second WaterArea registration pass (post-Stage-50, persistence) ──
            // Stage 50 (Water) rebuilds _generationData.waterAreas from its
            // own data source, stripping our Stage 38 additions. This second
            // pass — at the postfix on Stage 50's carrier, fired AFTER Stage 50
            // body has run — re-adds the polygons so they survive into
            // serialization. Both passes are needed:
            //   Stage 38 add → FishingManager allocates fish nodes during
            //                  the gap between 38 and 50
            //   Stage 60 add → survives FF gen rebuild + save serialization
            //
            // We clear RiverWaterAreaBounds first so the bounds set reflects
            // the new (post-rebuild) polygon shapes, not the stripped Stage 38
            // ones. RiverCpCells stays — cps don't change.
            //
            // ForceWaterPlaneRebuild fires immediately to render water plane
            // meshes at gen-time (Stage 50 already did that pass for the
            // polygons it knew about, but ours just appeared).
            if (RiversRestoredMod.RiverRegisterAsWaterArea?.Value ?? true)
            {
                try
                {
                    RiverWaterAreaBuilder.RiverWaterAreaBounds.Clear();
                    int added = RiverWaterAreaBuilder.BuildAndAddForAllRivers(__instance);
                    if (added > 0)
                    {
                        RiversRestoredMod.Log.Msg(
                            $"[RR] Re-registered {added} river polygon(s) as WaterAreas POST-Stage-50 " +
                            "(survives FF's gen rebuild — Pangu-pattern timing).");
                        RiverPersistence.ForceWaterPlaneRebuild(__instance);
                    }
                }
                catch (Exception ex)
                {
                    RiversRestoredMod.Log.Warning(
                        $"[RR] Post-Stage-50 WaterArea registration failed: {ex.Message}");
                }
            }

            // Manual carve happens later — see LateCarvePostfix. The carver
            // needs a live Terrain GameObject which doesn't exist until
            // late in the pipeline. We fire from multiple late carriers so
            // whichever one runs first with a real Terrain wins.
        }

        /// <summary>
        /// Postfix on GenerateAsync_RiverPaths_Stage38 — inspects generationData
        /// AFTER the river generator ran. Tells us:
        ///   - how many rivers actually got generated
        ///   - what riversValid is set to
        ///   - any per-river point counts
        /// If rivers count is 0, the validator rejected every candidate.
        /// If rivers count > 0 but no rivers in world, geometry painting is broken.
        /// </summary>
        private static void Stage38Postfix(TerrainGenerator __instance)
        {
            // Mark the generation as having seen Stage 38 fire, so our
            // injection postfix can detect and avoid double-firing.
            _stage38AlreadyRanThisGen = true;
            DumpGenerationDataState(__instance, "Stage38 POST");
            try
            {
                var tgType = __instance.GetType();
                var gdField = tgType.GetField("_generationData",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var gd = gdField?.GetValue(__instance);
                if (gd == null) return;

                var gdType = gd.GetType();
                var riversField = gdType.GetField("rivers",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var rivers = riversField?.GetValue(gd) as System.Collections.IList;
                int count = rivers?.Count ?? -1;

                var validField = gdType.GetField("riversValid",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                bool valid = validField != null && (bool)validField.GetValue(gd);

                RiversRestoredMod.Log.Msg(
                    $"[RR] Stage38 RESULT: rivers.Count = {count}  riversValid = {valid}");

                if (count > 0 && rivers != null)
                {
                    for (int i = 0; i < count; i++)
                    {
                        var r = rivers[i];
                        if (r == null) continue;
                        var pointsField = r.GetType().GetField("points",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var pts = pointsField?.GetValue(r) as System.Collections.IList;
                        RiversRestoredMod.Log.Msg(
                            $"[RR]   river[{i}]: {pts?.Count ?? -1} points");
                    }
                }
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Warning(
                    $"[RR] Stage38Postfix failed: {ex.Message}");
            }
        }

        private static void TryPatch(HarmonyLib.Harmony harmony, Type tgType,
                                      string methodName, Type[] paramTypes)
        {
            try
            {
                MethodInfo method = AccessTools.Method(tgType, methodName, paramTypes);
                if (method == null)
                {
                    RiversRestoredMod.Log.Msg(
                        $"[RR] Skipped hook — method not found: {methodName}");
                    return;
                }

                // Each hook has its own tiny prefix that routes to the shared
                // override function, so logs tell us which one fired.
                string stubName = $"{methodName}Prefix";
                MethodInfo stub = typeof(RiverSettingsPatch).GetMethod(
                    stubName, BindingFlags.Static | BindingFlags.NonPublic);
                if (stub == null)
                {
                    RiversRestoredMod.Log.Warning(
                        $"[RR] Stub {stubName} missing — cannot hook {methodName}");
                    return;
                }

                harmony.Patch(method, prefix: new HarmonyMethod(stub));
                RiversRestoredMod.Log.Msg(
                    $"[RR] Hooked TerrainGenerator.{methodName}");
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Warning(
                    $"[RR] Failed hooking {methodName}: {ex.Message}");
            }
        }

        // ── Named stubs, one per hook, so logs tell us which fired ──────────
        private static void PreGenerateSharedPrefix(TerrainGenerator __instance)
            => DoOverride(__instance, nameof(PreGenerateSharedPrefix));

        private static void PreGeneratePrefix(TerrainGenerator __instance)
            => DoOverride(__instance, nameof(PreGeneratePrefix));

        private static void GeneratePrefix(TerrainGenerator __instance, bool game)
            => DoOverride(__instance, $"{nameof(GeneratePrefix)}(game={game})");

        private static void GenerateAsyncPrefix(TerrainGenerator __instance)
            => DoOverride(__instance, nameof(GenerateAsyncPrefix));

        private static void GenerateAsync_RiverPaths_Stage38Prefix(TerrainGenerator __instance)
        {
            DoOverride(__instance, nameof(GenerateAsync_RiverPaths_Stage38Prefix));
            DumpGenerationDataState(__instance, "Stage38 PRE");
        }

        /// <summary>
        /// Shared override logic. Now fully IDEMPOTENT — checks if the
        /// current RiverSettings values already match our target and
        /// skips silently if so (no logs). Otherwise applies and logs.
        /// This way multiple hooks firing during a gen produce at most
        /// one "applied" log, and successive map generations each get
        /// a fresh application automatically.
        /// </summary>
        private static void DoOverride(TerrainGenerator __instance, string firedFrom)
        {
            try
            {
                if (!RiversRestoredMod.RiversEnabled.Value) return;
                // ALWAYS cache the generator reference, even on save-load.
                // OnUpdate uses this to find the TG for restoration; without
                // this, save-load misses our restoration path entirely.
                CachedGenerator = __instance;
                if (IsLoadingSavedMap(__instance))
                {
                    RiversRestoredMod.Log.Msg($"[RR] {firedFrom} — save-load detected (useSavedMap=true), skipping override.");
                    return;
                }

                // RiverSettings is a NESTED value type on TerrainGenerator.
                // Reading __instance.riverSettings gives us a COPY. Writes to
                // its fields modify that copy, NOT the original. We must
                // write the modified struct BACK via reflection on the parent
                // field. This is the bug we hit for several builds — every
                // run's "Pre" log read 2/40 because our previous "Post" 4/15
                // never made it back to the field.
                var tgType = __instance.GetType();
                var rsField = tgType.GetField("riverSettings",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (rsField == null)
                {
                    RiversRestoredMod.Log.Warning(
                        "[RR] riverSettings field not found via reflection.");
                    return;
                }

                // Box the value so we can mutate via reflection (FieldInfo.SetValue
                // on a struct copy boxes it; we then SetValue back to commit).
                object rsBox = rsField.GetValue(__instance);
                if (rsBox == null) return;

                Type rsType = rsBox.GetType();
                bool isStruct = rsType.IsValueType;

                // Read current values
                int curNumRivers = (int)rsType.GetField("numRivers").GetValue(rsBox);
                int curMinPoints = (int)rsType.GetField("minPoints").GetValue(rsBox);
                int curMinWidth = (int)rsType.GetField("minWidth").GetValue(rsBox);
                int curMaxWidth = (int)rsType.GetField("maxWidth").GetValue(rsBox);
                float curMinDepth = (float)rsType.GetField("minDepth").GetValue(rsBox);
                float curMaxDepth = (float)rsType.GetField("maxDepth").GetValue(rsBox);

                // Compute what we WANT
                int wantNumRivers = RiversRestoredMod.NumRivers.Value;
                int wantMinPoints = RiversRestoredMod.MinPoints.Value > 0
                    ? RiversRestoredMod.MinPoints.Value : curMinPoints;
                int wantMinWidth = RiversRestoredMod.MinWidth.Value > 0
                    ? RiversRestoredMod.MinWidth.Value : curMinWidth;
                int wantMaxWidth = RiversRestoredMod.MaxWidth.Value > 0
                    ? RiversRestoredMod.MaxWidth.Value : curMaxWidth;
                float wantMinDepth = RiversRestoredMod.MinDepth.Value >= 0f
                    ? RiversRestoredMod.MinDepth.Value : curMinDepth;
                float wantMaxDepth = RiversRestoredMod.MaxDepth.Value >= 0f
                    ? RiversRestoredMod.MaxDepth.Value : curMaxDepth;

                // Already matches? Silent no-op.
                if (curNumRivers == wantNumRivers &&
                    curMinPoints == wantMinPoints &&
                    curMinWidth == wantMinWidth &&
                    curMaxWidth == wantMaxWidth &&
                    System.Math.Abs(curMinDepth - wantMinDepth) < 0.01f &&
                    System.Math.Abs(curMaxDepth - wantMaxDepth) < 0.01f)
                {
                    return;
                }

                RiversRestoredMod.Log.Msg(
                    $"[RR] {firedFrom}  Pre:  numRivers={curNumRivers}  minPoints={curMinPoints}  " +
                    $"width={curMinWidth}-{curMaxWidth}  depth={curMinDepth:F2}-{curMaxDepth:F2}  " +
                    $"isValueType={isStruct}");

                // Fresh generation starting — reset the stage-injection guards
                // so InjectStage38Postfix / InjectStage60Postfix will fire
                // exactly once per map gen.
                _stage38AlreadyRanThisGen = false;
                _stage60AlreadyRanThisGen = false;
                RiverCarver.ResetGuard();

                // Mutate the box
                rsType.GetField("numRivers").SetValue(rsBox, wantNumRivers);
                rsType.GetField("minPoints").SetValue(rsBox, wantMinPoints);
                rsType.GetField("minWidth").SetValue(rsBox, wantMinWidth);
                rsType.GetField("maxWidth").SetValue(rsBox, wantMaxWidth);
                rsType.GetField("minDepth").SetValue(rsBox, wantMinDepth);
                rsType.GetField("maxDepth").SetValue(rsBox, wantMaxDepth);

                // CRITICAL: write the box back to the parent field. For
                // structs this commits the change; for classes it's a no-op
                // (rsBox is the same reference). Either way, safe.
                rsField.SetValue(__instance, rsBox);

                // Verify the write actually persisted (struct edge cases)
                object verifyBox = rsField.GetValue(__instance);
                int verifyNum = (int)rsType.GetField("numRivers").GetValue(verifyBox);

                RiversRestoredMod.Log.Msg(
                    $"[RR] {firedFrom}  Post: numRivers={verifyNum}  " +
                    $"(wanted {wantNumRivers}, persistent={(verifyNum == wantNumRivers)})");

                // ── Mark WaterTypes as valid river endpoints (necessary but not sufficient) ──
                if (RiversRestoredMod.MarkWaterTypesAsRiverEnd.Value)
                    MarkAllWaterTypesAsRiverEnd();

                // ── Diagnostic: force terrainType = Coastline ─────────────
                if (RiversRestoredMod.ForceCoastlineTerrain.Value)
                    ForceCoastlineTerrainType();
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Error($"[RR] DoOverride ({firedFrom}) failed: {ex}");
            }
        }

        /// <summary>Reset per-generation state on scene reloads.</summary>
        public static void ResetGuard()
        {
            _stage38AlreadyRanThisGen = false;
            _stage60AlreadyRanThisGen = false;
        }

        /// <summary>
        /// Detect "we're loading an existing save, not generating a new map."
        /// _generationData.useSavedMap is set to true when the loader runs the
        /// terrain pipeline to reconstruct visuals from saved data. We MUST
        /// skip all our injections in that case — re-running Stage 38 / 60 /
        /// the carve overwrites correct saved state with new (wrong) data,
        /// which is what causes the post-reload yellow banks.
        /// </summary>
        public static bool IsLoadingSavedMap(TerrainGenerator __instance)
        {
            try
            {
                var gdField = __instance.GetType().GetField("_generationData",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var gd = gdField?.GetValue(__instance);
                if (gd == null) return false;
                var useSavedField = gd.GetType().GetField("useSavedMap",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (useSavedField == null) return false;
                return (bool)useSavedField.GetValue(gd);
            }
            catch { return false; }
        }

        // Track which WaterType assets we've already flipped so we only log once
        private static readonly System.Collections.Generic.HashSet<int> _flippedWaterTypeIds =
            new System.Collections.Generic.HashSet<int>();

        /// <summary>
        /// Forces TerrainGeneratorController.terrainType = Coastline (enum
        /// value 1). Vanilla UI seems to lock all biome themes to Default —
        /// this lets us test whether the Coastline path produces rivers,
        /// which would prove that the inland Default path has a deeper
        /// validation gate (likely elevation gradient).
        /// </summary>
        private static void ForceCoastlineTerrainType()
        {
            try
            {
                Type tgcType = AccessTools.TypeByName("TerrainGeneratorController");
                if (tgcType == null) return;

                var tgc = UnityEngine.Object.FindObjectOfType(tgcType);
                if (tgc == null)
                {
                    RiversRestoredMod.Log.Warning(
                        "[RR] TerrainGeneratorController instance not found.");
                    return;
                }

                var ttField = tgcType.GetField("terrainType",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ttField == null)
                {
                    RiversRestoredMod.Log.Warning(
                        "[RR] terrainType field not found on TerrainGeneratorController.");
                    return;
                }

                var current = ttField.GetValue(tgc);
                if (current == null) return;

                // Coastline = 1 (per the FFDataDump enum dump)
                int coastlineValue = 1;
                var coastlineEnum = Enum.ToObject(ttField.FieldType, coastlineValue);

                if (current.ToString() == "Coastline") return; // already correct

                ttField.SetValue(tgc, coastlineEnum);
                RiversRestoredMod.Log.Msg(
                    $"[RR] Forced terrainType: {current} → Coastline");
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Error($"[RR] ForceCoastlineTerrainType failed: {ex}");
            }
        }

        /// <summary>
        /// Iterates every loaded WaterType ScriptableObject and sets
        /// riverEndPoint = true. This unblocks the Voronoi river validator,
        /// which requires candidate rivers to terminate at a water area
        /// whose waterType has the flag set. In shipped vanilla data, no
        /// WaterType has the flag — that's why setting numRivers alone
        /// produces zero rivers.
        /// </summary>
        private static void MarkAllWaterTypesAsRiverEnd()
        {
            try
            {
                Type wtType = AccessTools.TypeByName("TerrainGen.WaterType");
                if (wtType == null)
                {
                    RiversRestoredMod.Log.Warning(
                        "[RR] WaterType type not found — cannot mark river endpoints.");
                    return;
                }

                // Resources.FindObjectsOfTypeAll loads ALL ScriptableObjects of
                // a given type (including assets loaded from bundles, not just
                // ones referenced from the scene). This covers every WaterType
                // the game has shipped.
                var allWaterTypes = UnityEngine.Resources.FindObjectsOfTypeAll(wtType);
                if (allWaterTypes == null || allWaterTypes.Length == 0)
                {
                    RiversRestoredMod.Log.Warning(
                        "[RR] No WaterType ScriptableObjects found in loaded assets.");
                    return;
                }

                var rEndField = wtType.GetField("riverEndPoint",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (rEndField == null)
                {
                    RiversRestoredMod.Log.Warning(
                        "[RR] WaterType.riverEndPoint field not found.");
                    return;
                }

                int flippedNow = 0;
                int alreadyTrue = 0;
                int alreadySeen = 0;
                foreach (var wt in allWaterTypes)
                {
                    if (wt == null) continue;
                    int id = wt.GetInstanceID();
                    if (_flippedWaterTypeIds.Contains(id)) { alreadySeen++; continue; }
                    _flippedWaterTypeIds.Add(id);

                    bool current = (bool)rEndField.GetValue(wt);
                    if (current) { alreadyTrue++; continue; }

                    rEndField.SetValue(wt, true);
                    flippedNow++;
                    RiversRestoredMod.Log.Msg($"[RR]   ↳ marked {wt.name}.riverEndPoint = true");
                }

                RiversRestoredMod.Log.Msg(
                    $"[RR] WaterType pass: {flippedNow} newly-flipped, " +
                    $"{alreadyTrue} already-true, {alreadySeen} already-seen, " +
                    $"{allWaterTypes.Length} total");
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Error($"[RR] MarkAllWaterTypesAsRiverEnd failed: {ex}");
            }
        }

        /// <summary>
        /// Heals the TerrainGenerator instance fields the sliced pipeline
        /// leaves null — the ones we identified via Stage60PrefixDumper:
        ///   - waterBiome (TerrainBiome)         → resolved from biomes list
        ///   - cachedAreas (List`1)              → empty list of correct generic type
        ///   - usedText, usedText2 (List`1)      → empty lists
        ///   - generatorThread (left null — threading isn't used here)
        ///
        /// Idempotent: only sets fields that are still null. Safe to call
        /// multiple times if Stage 60 ever fires more than once.
        /// </summary>
        private static void PrepareForStage60(TerrainGenerator __instance)
        {
            try
            {
                Type t = __instance.GetType();

                // ── 1) Resolve waterBiome ─────────────────────────────────
                var waterBiomeField = WalkUpForField(t, "waterBiome");
                if (waterBiomeField != null && waterBiomeField.GetValue(__instance) == null)
                {
                    // Diagnostic: enumerate every loaded TerrainBiome SO with
                    // name + bool fields so we can pick the right one if our
                    // heuristic misses.
                    DumpAllTerrainBiomes();

                    object? wb = FindWaterBiome(__instance, t);
                    if (wb == null)
                    {
                        // Fallback: use defaultBiome to give Stage 60 a
                        // non-null reference. If this gets past the NRE,
                        // we've confirmed waterBiome was the culprit and we
                        // can pick a more appropriate biome from the dump.
                        var defField = WalkUpForField(t, "defaultBiome");
                        wb = defField?.GetValue(__instance);
                        if (wb != null)
                        {
                            RiversRestoredMod.Log.Msg(
                                $"[RR] [Heal] waterBiome — heuristic missed, falling back to defaultBiome = " +
                                $"{(wb as UnityEngine.Object)?.name ?? wb.ToString()}");
                        }
                    }

                    if (wb != null)
                    {
                        waterBiomeField.SetValue(__instance, wb);
                        RiversRestoredMod.Log.Msg(
                            $"[RR] [Heal] waterBiome ← {(wb as UnityEngine.Object)?.name ?? wb.ToString()}");
                    }
                    else
                    {
                        RiversRestoredMod.Log.Warning(
                            "[RR] [Heal] waterBiome could not be resolved at all — even defaultBiome was null.");
                    }
                }

                // ── 2) Initialize null List<T> buffers ────────────────────
                InitNullList(__instance, t, "cachedAreas");
                InitNullList(__instance, t, "usedText");
                InitNullList(__instance, t, "usedText2");

                // ── 3) Resolve generatorThread ────────────────────────────
                // PRIME SUSPECT for the GridTrace NRE — the sliced pipeline
                // never instantiates the threading wrapper, but Stage 60's
                // TraceFunction probably captures `this.generatorThread`.
                HealGeneratorThread(__instance, t);
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Error($"[RR] PrepareForStage60 failed: {ex}");
            }
        }

        // Track whether we've already dumped biomes this session — once is enough
        private static bool _dumpedBiomes = false;
        private static bool _dumpedGeneratorThread = false;

        /// <summary>
        /// Try to satisfy a non-null TerrainGenerator.generatorThread. The
        /// sliced runtime pipeline leaves this null (it's the async-thread
        /// wrapper). Stage 60's TraceFunction almost certainly captures it
        /// and NREs when it dereferences a null. Strategy:
        ///   1. Look for an existing TerrainGeneratorThread instance in the
        ///      scene / loaded ScriptableObjects.
        ///   2. If none, try Activator.CreateInstance — works for plain
        ///      classes; will throw for MonoBehaviour/SO (we'll fall back).
        ///   3. If that fails too, try AddComponent (if it's a MonoBehaviour)
        ///      attached to the TerrainGenerator's GameObject.
        /// On success, copy critical fields from __instance into the new
        /// thread wrapper so any captured field references resolve.
        /// </summary>
        private static void HealGeneratorThread(TerrainGenerator __instance, Type tgType)
        {
            try
            {
                var gtField = WalkUpForField(tgType, "generatorThread");
                if (gtField == null) return;
                if (gtField.GetValue(__instance) != null) return;

                Type gtType = gtField.FieldType;

                // One-time structure dump so we know what we're dealing with
                if (!_dumpedGeneratorThread)
                {
                    _dumpedGeneratorThread = true;
                    DumpType("TerrainGeneratorThread", gtType);
                }

                // Strategy 1: existing instance in loaded objects
                object? gt = null;
                try
                {
                    var existing = UnityEngine.Resources.FindObjectsOfTypeAll(gtType);
                    if (existing != null && existing.Length > 0)
                    {
                        gt = existing[0];
                        RiversRestoredMod.Log.Msg(
                            $"[RR] [Heal] generatorThread ← existing instance ({existing.Length} found)");
                    }
                }
                catch (Exception ex)
                {
                    RiversRestoredMod.Log.Warning(
                        $"[RR] FindObjectsOfTypeAll(TerrainGeneratorThread) failed: {ex.Message}");
                }

                // Strategy 2: Activator.CreateInstance (plain class)
                if (gt == null && !typeof(UnityEngine.Object).IsAssignableFrom(gtType))
                {
                    try
                    {
                        gt = Activator.CreateInstance(gtType);
                        RiversRestoredMod.Log.Msg(
                            "[RR] [Heal] generatorThread ← Activator.CreateInstance (new plain instance)");
                    }
                    catch (Exception ex)
                    {
                        RiversRestoredMod.Log.Warning(
                            $"[RR] Activator.CreateInstance(TerrainGeneratorThread) failed: {ex.Message}");
                    }
                }

                // Strategy 2b: try a single-arg ctor (TerrainGenerator owner)
                if (gt == null && !typeof(UnityEngine.Object).IsAssignableFrom(gtType))
                {
                    try
                    {
                        var ctor = gtType.GetConstructor(
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                            null, new[] { tgType }, null);
                        if (ctor != null)
                        {
                            gt = ctor.Invoke(new object[] { __instance });
                            RiversRestoredMod.Log.Msg(
                                "[RR] [Heal] generatorThread ← single-arg ctor(TerrainGenerator)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Exception inner = (ex is TargetInvocationException tie && tie.InnerException != null)
                            ? tie.InnerException : ex;
                        RiversRestoredMod.Log.Warning(
                            $"[RR] ctor(TerrainGenerator) failed: {inner.GetType().Name}: {inner.Message}");
                    }
                }

                // Strategy 2c: GetUninitializedObject — bypasses ctor entirely
                // so no Thread is started / ManualResetEvent allocated. We just
                // need a non-null reference whose `owner` resolves back to us.
                if (gt == null && !typeof(UnityEngine.Object).IsAssignableFrom(gtType))
                {
                    try
                    {
                        gt = System.Runtime.Serialization.FormatterServices
                            .GetUninitializedObject(gtType);
                        RiversRestoredMod.Log.Msg(
                            "[RR] [Heal] generatorThread ← GetUninitializedObject (no ctor)");
                    }
                    catch (Exception ex)
                    {
                        RiversRestoredMod.Log.Warning(
                            $"[RR] GetUninitializedObject(TerrainGeneratorThread) failed: {ex.Message}");
                    }
                }

                // Strategy 3: AddComponent (MonoBehaviour)
                if (gt == null && typeof(UnityEngine.Component).IsAssignableFrom(gtType))
                {
                    try
                    {
                        var go = (__instance is UnityEngine.Component c) ? c.gameObject : null;
                        if (go != null)
                        {
                            gt = go.AddComponent(gtType);
                            RiversRestoredMod.Log.Msg(
                                "[RR] [Heal] generatorThread ← AddComponent on TerrainGenerator GO");
                        }
                    }
                    catch (Exception ex)
                    {
                        RiversRestoredMod.Log.Warning(
                            $"[RR] AddComponent(TerrainGeneratorThread) failed: {ex.Message}");
                    }
                }

                if (gt == null)
                {
                    RiversRestoredMod.Log.Warning(
                        "[RR] [Heal] generatorThread could not be satisfied by any strategy.");
                    return;
                }

                // If TerrainGeneratorThread carries field references back to
                // its parent generator (e.g. a `generator` or `parent` field),
                // wire them now so any captured reference resolves.
                foreach (var refName in new[] { "generator", "terrainGenerator", "parent", "owner", "tg" })
                {
                    var rf = gt.GetType().GetField(refName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (rf != null && rf.FieldType.IsAssignableFrom(tgType) &&
                        rf.GetValue(gt) == null)
                    {
                        rf.SetValue(gt, __instance);
                        RiversRestoredMod.Log.Msg(
                            $"[RR] [Heal]   wired generatorThread.{refName} ← __instance");
                    }
                }

                gtField.SetValue(__instance, gt);
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Error($"[RR] HealGeneratorThread failed: {ex}");
            }
        }

        /// <summary>One-shot structure dump — every field of a type and its base.</summary>
        private static void DumpType(string label, Type t)
        {
            try
            {
                RiversRestoredMod.Log.Msg($"[RR] ===== [TypeDump] {label} = {t.FullName} =====");
                Type? walker = t;
                while (walker != null && walker != typeof(object) &&
                       walker != typeof(UnityEngine.MonoBehaviour) &&
                       walker != typeof(UnityEngine.Behaviour) &&
                       walker != typeof(UnityEngine.Component) &&
                       walker != typeof(UnityEngine.ScriptableObject) &&
                       walker != typeof(UnityEngine.Object))
                {
                    foreach (var f in walker.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                                        BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        RiversRestoredMod.Log.Msg(
                            $"[RR]   {walker.Name}::{f.Name} ({f.FieldType.Name})");
                    }
                    walker = walker.BaseType;
                }
                RiversRestoredMod.Log.Msg("[RR] ===== [TypeDump] end =====");
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Error($"[RR] DumpType({label}) failed: {ex}");
            }
        }

        /// <summary>
        /// One-shot diagnostic: enumerate every TerrainBiome ScriptableObject
        /// loaded into memory, with its name and every bool field. Helps us
        /// identify which biome should be used as waterBiome and why our
        /// heuristic missed.
        /// </summary>
        private static void DumpAllTerrainBiomes()
        {
            if (_dumpedBiomes) return;
            _dumpedBiomes = true;
            try
            {
                Type? biomeType = AccessTools.TypeByName("TerrainGen.TerrainBiome");
                if (biomeType == null)
                {
                    RiversRestoredMod.Log.Warning("[RR] [BiomeDump] TerrainGen.TerrainBiome type not found");
                    return;
                }
                var all = UnityEngine.Resources.FindObjectsOfTypeAll(biomeType);
                if (all == null)
                {
                    RiversRestoredMod.Log.Warning("[RR] [BiomeDump] Resources.FindObjectsOfTypeAll returned null");
                    return;
                }
                RiversRestoredMod.Log.Msg(
                    $"[RR] ===== [BiomeDump] {all.Length} TerrainBiome instances =====");

                // Find all bool fields on TerrainBiome (declared + inherited)
                var boolFields = new System.Collections.Generic.List<FieldInfo>();
                Type? walker = biomeType;
                while (walker != null && walker != typeof(object) &&
                       walker != typeof(UnityEngine.ScriptableObject) &&
                       walker != typeof(UnityEngine.Object))
                {
                    foreach (var f in walker.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                                        BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (f.FieldType == typeof(bool)) boolFields.Add(f);
                    }
                    walker = walker.BaseType;
                }

                foreach (var b in all)
                {
                    if (b == null) continue;
                    string name = (b as UnityEngine.Object)?.name ?? b.ToString() ?? "<no-name>";
                    var sb = new System.Text.StringBuilder();
                    sb.Append("[RR]   ");
                    sb.Append(name);
                    sb.Append(" :");
                    foreach (var bf in boolFields)
                    {
                        try
                        {
                            bool v = (bool)bf.GetValue(b);
                            if (v) { sb.Append(" "); sb.Append(bf.Name); sb.Append("=true"); }
                        }
                        catch { }
                    }
                    RiversRestoredMod.Log.Msg(sb.ToString());
                }
                RiversRestoredMod.Log.Msg("[RR] ===== [BiomeDump] end =====");
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Error($"[RR] DumpAllTerrainBiomes failed: {ex}");
            }
        }

        /// <summary>
        /// Find a TerrainBiome that represents water — first by inspecting
        /// the existing `biomes` collection on the generator, then by
        /// scanning loaded ScriptableObjects. Match heuristic: a `bool`
        /// field/property called `isWater` (or similar) being true, OR a
        /// name containing "Water" (case-insensitive).
        /// </summary>
        private static object? FindWaterBiome(TerrainGenerator __instance, Type tgType)
        {
            try
            {
                // First: walk __instance.biomes (TerrainBiomeList), looking for an entry
                var biomesField = WalkUpForField(tgType, "biomes");
                var biomes = biomesField?.GetValue(__instance);
                if (biomes != null)
                {
                    // TerrainBiomeList likely has a public IList field/prop with the entries.
                    // Try common shapes: a `list` or `biomes` IEnumerable.
                    foreach (var name in new[] { "list", "biomes", "items", "all" })
                    {
                        var listField = biomes.GetType().GetField(name,
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        var listVal = listField?.GetValue(biomes) as System.Collections.IEnumerable;
                        if (listVal == null) continue;
                        foreach (var entry in listVal)
                        {
                            if (LooksLikeWaterBiome(entry)) return entry;
                        }
                    }
                }

                // Second: scan all loaded TerrainBiome ScriptableObjects
                Type? biomeType = AccessTools.TypeByName("TerrainGen.TerrainBiome");
                if (biomeType != null)
                {
                    var all = UnityEngine.Resources.FindObjectsOfTypeAll(biomeType);
                    if (all != null)
                    {
                        foreach (var b in all)
                        {
                            if (LooksLikeWaterBiome(b)) return b;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Warning($"[RR] FindWaterBiome failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Heuristic: TerrainBiome is "water" if it has an isWater-style flag
        /// set, or its name/ToString mentions Water/River/Lake.
        /// </summary>
        private static bool LooksLikeWaterBiome(object? biome)
        {
            if (biome == null) return false;
            try
            {
                Type bt = biome.GetType();
                // Check bool fields named like isWater, water, riverBiome
                foreach (var fn in new[] { "isWater", "water", "isRiverBiome", "riverBiome", "isLakeBiome" })
                {
                    var f = bt.GetField(fn,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(biome) == true)
                        return true;
                }
                // Fall back to name match
                string n = (biome as UnityEngine.Object)?.name ?? biome.ToString() ?? "";
                if (string.IsNullOrEmpty(n)) return false;
                string lower = n.ToLowerInvariant();
                return lower.Contains("water") || lower.Contains("river") ||
                       lower.Contains("lake")  || lower.Contains("pond");
            }
            catch { return false; }
        }

        /// <summary>
        /// Walk the inheritance chain looking for a field, since some
        /// TerrainGenerator fields live on a base class.
        /// </summary>
        private static FieldInfo? WalkUpForField(Type t, string fieldName)
        {
            Type? walker = t;
            while (walker != null && walker != typeof(object))
            {
                var f = walker.GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                walker = walker.BaseType;
            }
            return null;
        }

        /// <summary>
        /// If `fieldName` is a List`1 currently null on the instance,
        /// allocate an empty list of the correct generic type and assign.
        /// </summary>
        private static void InitNullList(TerrainGenerator __instance, Type tgType, string fieldName)
        {
            try
            {
                var f = WalkUpForField(tgType, fieldName);
                if (f == null) return;
                if (f.GetValue(__instance) != null) return;
                if (!f.FieldType.IsGenericType) return;
                var inst = Activator.CreateInstance(f.FieldType);
                f.SetValue(__instance, inst);
                RiversRestoredMod.Log.Msg(
                    $"[RR] [Heal] {fieldName} ← new {f.FieldType.Name}");
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Warning(
                    $"[RR] InitNullList({fieldName}) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// PREFIX on Stage 60 (RiverGeometry). Dumps every instance field of
        /// TerrainGenerator so we can identify which field is null when
        /// GridTrace tries to dereference it. Looks especially for:
        ///   - Heightmap/buffer arrays (likely sized 0,0 or null)
        ///   - Unity Terrain references (_terrain, _terrainData)
        ///   - Delegate/Func fields (TraceFunction, etc.)
        ///   - Any List/Array that's null when a populated one is expected
        /// </summary>
        private static void Stage60PrefixDumper(TerrainGenerator __instance)
        {
            try
            {
                Type t = __instance.GetType();
                RiversRestoredMod.Log.Msg(
                    $"[RR] ===== [Stage60 PRE — instance fields] {t.FullName} =====");

                int fieldCount = 0;
                int nullCount = 0;
                Type? walker = t;
                while (walker != null && walker != typeof(object) &&
                       walker != typeof(UnityEngine.MonoBehaviour) &&
                       walker != typeof(UnityEngine.Behaviour) &&
                       walker != typeof(UnityEngine.Component) &&
                       walker != typeof(UnityEngine.Object))
                {
                    var fields = walker.GetFields(
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    foreach (var f in fields)
                    {
                        // Skip the big _generationData — we dump that separately
                        if (f.Name == "_generationData") continue;
                        fieldCount++;
                        object? v = null;
                        try { v = f.GetValue(__instance); } catch { }
                        string summary = SummarizeFieldValue(f.FieldType, v);
                        bool isNull = (v == null);
                        if (isNull) nullCount++;
                        // Highlight nulls with a marker so they're easy to scan for
                        string prefix = isNull ? "[RR] !!" : "[RR]   ";
                        RiversRestoredMod.Log.Msg(
                            $"{prefix} {f.Name} ({f.FieldType.Name}) = {summary}");
                    }
                    walker = walker.BaseType;
                }
                RiversRestoredMod.Log.Msg(
                    $"[RR] ===== [Stage60 PRE] {fieldCount} fields, {nullCount} NULL =====");
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Error($"[RR] Stage60PrefixDumper failed: {ex}");
            }
        }

        /// <summary>
        /// Reflective state dumper for `TerrainGenerator._generationData`.
        /// Logs every field's name, type, and a one-liner summary of its
        /// value (counts for collections, dimensions for arrays, ToString
        /// for primitives). The goal is to identify which prerequisite
        /// data structures (Voronoi points, heightmap, lake areas, etc.)
        /// are populated when Stage 38 enters — and which aren't.
        ///
        /// Called as a Stage38 PRE/POST snapshot pair so we can diff what
        /// Stage 38 actually consumed.
        /// </summary>
        private static void DumpGenerationDataState(TerrainGenerator __instance, string label)
        {
            try
            {
                var tgType = __instance.GetType();
                var gdField = tgType.GetField("_generationData",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (gdField == null)
                {
                    RiversRestoredMod.Log.Warning($"[RR] [{label}] _generationData field missing");
                    return;
                }
                var gd = gdField.GetValue(__instance);
                if (gd == null)
                {
                    RiversRestoredMod.Log.Warning($"[RR] [{label}] _generationData VALUE IS NULL");
                    return;
                }

                var gdType = gd.GetType();
                RiversRestoredMod.Log.Msg($"[RR] ===== [{label}] {gdType.FullName} =====");

                // Walk all fields (declared on this type and inherited).
                Type t = gdType;
                int totalFields = 0;
                while (t != null && t != typeof(object))
                {
                    var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    foreach (var f in fields)
                    {
                        totalFields++;
                        string fn = f.Name;
                        Type ft = f.FieldType;
                        object? fv = null;
                        try { fv = f.GetValue(gd); } catch { }

                        string summary = SummarizeFieldValue(ft, fv);
                        RiversRestoredMod.Log.Msg($"[RR]   {fn} ({ft.Name}) = {summary}");
                    }
                    t = t.BaseType;
                }
                RiversRestoredMod.Log.Msg(
                    $"[RR] ===== [{label}] {totalFields} fields total =====");
            }
            catch (Exception ex)
            {
                RiversRestoredMod.Log.Error($"[RR] DumpGenerationDataState({label}) failed: {ex}");
            }
        }

        /// <summary>
        /// Render a field's value as a short one-liner. For collections we
        /// report Count; for arrays we report Length and rank; for nulls
        /// "null"; otherwise ToString (truncated).
        /// </summary>
        private static string SummarizeFieldValue(Type ft, object? fv)
        {
            if (fv == null) return "null";

            // Arrays
            if (fv is Array arr)
            {
                if (arr.Rank == 1)
                    return $"Array[{arr.Length}]";
                var dims = new System.Text.StringBuilder("Array[");
                for (int d = 0; d < arr.Rank; d++)
                {
                    if (d > 0) dims.Append(",");
                    dims.Append(arr.GetLength(d));
                }
                dims.Append("]");
                return dims.ToString();
            }

            // Collections (List<T>, etc.) implementing ICollection
            if (fv is System.Collections.ICollection coll)
                return $"{ft.Name}(Count={coll.Count})";

            // Generic IEnumerable that isn't ICollection — count via foreach
            if (fv is System.Collections.IEnumerable en && !(fv is string))
            {
                int c = 0;
                foreach (var _ in en) c++;
                return $"{ft.Name}(enum-Count={c})";
            }

            // Primitive / value types — print the value
            string s = fv.ToString() ?? "null";
            if (s.Length > 80) s = s.Substring(0, 80) + "…";
            return s;
        }
    }
}
