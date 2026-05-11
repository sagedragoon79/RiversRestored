using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using TerrainGen;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RiversRestored.Patches
{
    /// <summary>
    /// On-demand preview-gen trigger. Adopts Pangu's approach:
    ///
    /// FF's "Map" scene contains a properly-configured prefab with the
    /// TerrainGenerator and TerrainGeneratorController. AddComponent on a
    /// fresh GameObject doesn't carry over the Editor-configured settings
    /// (mapSettings, baseSettings, asset references etc.), so worker gen
    /// against such an instance produces NaN heightnoise. The fix is to
    /// load FF's "Map" scene additively and use the TerrainGenerator that
    /// scene already has — the same trick Pangu uses internally.
    ///
    /// V3 strategy:
    ///   1. Try cached/scene-search for any TG already present (Pangu, etc.)
    ///   2. If none, load "Map" scene additively
    ///   3. Find the TG in the loaded scene
    ///   4. Run gen stage sequence on it
    ///   5. Render via MapPreviewRenderer
    ///   6. (Optionally) unload Map scene to free resources
    ///
    /// Async work runs on a coroutine hosted by PreviewOverlay.
    /// </summary>
    internal static class PreviewGenWorker
    {
        private static bool _busy = false;
        // Pangu-pattern: tracks whether RR triggered the Map scene load.
        // When true, we MUST unload the scene before gameplay starts —
        // otherwise the preview's mutated TGC pollutes the actual game's
        // gen and hangs the load screen. Pangu does the same:
        // _seedPreviewLoadedMapSceneForTemplate (line 3035) +
        // StartSeedPreviewTemplateSceneUnloadIfNeeded (line 3204).
        public static bool _mapSceneLoadedByUs = false;
        // Pangu-pattern: set true when WE initiate a Map scene load,
        // cleared by Plugin.OnSceneWasInitialized's discriminator. When
        // FF re-initializes the Map scene with this flag false, that
        // means FF is loading it for gameplay → tear down the worker.
        // Mirrors Pangu's _seedPreviewTemplateSceneInitPending
        // (Pangu_FF.decompiled.cs:929).
        public static bool _sceneInitPending = false;
        // Set by HardCancel — checked by the live coroutine so it bails
        // out cleanly mid-gen instead of finishing and rendering against
        // a worker that's about to be torn down.
        private static bool _cancelled = false;
        // True while tgc.GenSliced_Generate is executing for our preview.
        // Read by RR's own Harmony patches so they can skip
        // SCENE-MUTATING work (carving Unity Terrain, rebuilding the
        // live WaterPlane) while still letting DATA-ONLY work proceed
        // (adding river polygons to _generationData.waterAreas — the
        // preview renderer needs that). Without this gate, the full
        // pipeline run pollutes the Map scene with RR's modifications,
        // and FF's gameplay flow then chokes when it adopts the
        // mutated scene. Pangu doesn't have this problem because it
        // doesn't inject into the gen pipeline; RR does.
        public static bool IsPreviewActive { get; private set; } = false;

        // Worker TG ref + stage field, exposed for the overlay's
        // progress bar. Set when PreviewCoroutine acquires a worker;
        // cleared by HardCancel + on graceful gen end. The stage field
        // is the int 1..97 inside _generationData; reading it gives
        // the gen pipeline's current stage for a Pangu-style progress
        // bar (0→100%).
        private static TerrainGenerator? _activeWorkerTG;
        // Set by PreviewCoroutine the moment the LateCarvePostfix-time
        // render fires AND BuildRichCaption populates the caption
        // strings. The overlay gates panel content visibility on this
        // so the user doesn't see a half-finished render with empty
        // captions. Cleared at the start of every TriggerPreview.
        public static bool CaptionReady { get; private set; } = false;

        /// <summary>Flip the caption-ready flag back to false. Used by
        /// the overlay on panel re-open to suppress the old preview's
        /// image from flashing before the new gen starts.</summary>
        public static void ResetCaptionReady() => CaptionReady = false;

        /// <summary>Read the gen pipeline's current stage [0,1] for a
        /// progress bar. Returns -1 if no preview is active or stage
        /// can't be resolved (caller can render an indeterminate state
        /// instead).</summary>
        public static float TryGetGenProgress()
        {
            try
            {
                var tg = _activeWorkerTG;
                if (tg == null) return -1f;
                var gdField = AccessTools.Field(tg.GetType(), "_generationData");
                var gd = gdField?.GetValue(tg);
                if (gd == null) return -1f;
                var stageField = gd.GetType().GetField("stage",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (stageField?.GetValue(gd) is int stage)
                {
                    // Pipeline is 1..97 stages. Clamp + normalize.
                    return Mathf.Clamp01(stage / 97f);
                }
            }
            catch { }
            return -1f;
        }

        /// <summary>Entry point — invoked from the PREVIEW button click.
        /// Kicks off a coroutine that handles the async scene load and
        /// gen stage sequence.</summary>
        // Set by TriggerPreviewSoftRestart while a gen is in flight —
        // the running coroutine's finally checks this and re-triggers
        // a fresh preview. Used by the auto-regen UI loop so rapid
        // slider changes always settle to the latest state without
        // starting multiple gens on top of each other.
        private static bool _pendingRestart = false;

        public static void TriggerPreview()
        {
            if (_busy)
            {
                Log("Already running — ignoring click.");
                return;
            }

            // Find a MonoBehaviour to host the coroutine. The PreviewOverlay
            // is already DontDestroyOnLoad and present whenever we'd want
            // to trigger a preview.
            var host = UnityEngine.Object.FindObjectOfType<PreviewOverlay>();
            if (host == null)
            {
                Log("No PreviewOverlay host MonoBehaviour found — can't start coroutine.");
                return;
            }

            _busy = true;
            _cancelled = false;
            // Reset the gate flag explicitly. The previous gen may have
            // left it set if its polling loop hard-timed-out before the
            // inner gen finished. Either way, this new gen takes
            // ownership of it from here on.
            IsPreviewActive = false;
            // Reset the caption-ready signal so the overlay shows the
            // "Generating preview…" empty-state until this gen's
            // captions populate.
            CaptionReady = false;
            host.StartCoroutine(PreviewCoroutine());
        }

        /// <summary>Trigger a preview, cancel-and-restart-style. If a
        /// preview gen is currently running, mark a pending restart;
        /// the running coroutine's finally re-fires once it cleans up.
        /// If no gen is running, fire immediately. Used by the auto-
        /// regen UI loop where the user is dragging a slider — we want
        /// to always settle to the *latest* state without queuing a
        /// pile of stale gens.</summary>
        public static void TriggerPreviewSoftRestart()
        {
            // Revert the overlay to the progress-bar empty state
            // immediately. Without this, the user sees the previous
            // preview lingering until the new one renders, which reads
            // as "the app froze." Flipping CaptionReady here means the
            // overlay's gate (showContent = CaptionReady && LatestPreview)
            // becomes false next frame and the progress bar takes over.
            CaptionReady = false;

            if (_busy)
            {
                _pendingRestart = true;
                _cancelled = true;
                StopWorkerCoroutines();
                Log("Soft restart requested mid-gen.");
                return;
            }
            TriggerPreview();
        }

        /// <summary>True if there's still preview state that warrants a
        /// HardCancel. Used by OnSceneWasInitialized to avoid running
        /// HardCancel (and its StopWorkerCoroutines side-effect) when
        /// there's nothing to clean up — otherwise we'd walk FF's fresh
        /// gameplay Map scene and stop all of its gen coroutines.</summary>
        public static bool HasActivePreviewState()
        {
            return _busy || _mapSceneLoadedByUs || _sceneInitPending || IsPreviewActive;
        }

        /// <summary>Force teardown of the preview worker. Mirrors Pangu's
        /// CancelSeedPreviewBuild + DisposeSeedPreviewWorker
        /// (Pangu_FF.decompiled.cs:2197, 3183). Called from:
        ///   • StartNewGamePatch.Prefix — when user clicks Start
        ///   • RiversRestoredMod.OnSceneWasInitialized — when FF (not us)
        ///     re-initializes the Map scene for gameplay
        ///   • TickUnloadWatchdog — belt-and-braces fallback
        ///
        /// Stops worker coroutines, sets the cancel flag so an in-flight
        /// PreviewCoroutine bails out before rendering, optionally
        /// unloads the Map scene we loaded.
        ///
        /// unload=false: caller (FF) is about to take ownership of the
        ///   scene transition. Don't fight it — just stop our coroutines.
        /// unload=true: caller wants the scene gone (e.g. UI hidden).</summary>
        public static void HardCancel(bool unload)
        {
            try
            {
                _cancelled = true;
                // Stop coroutines FIRST so the still-running inner gen
                // (started by StartCoroutine inside tgc.GenSliced_Generate)
                // can't fire any more late-stage hooks against the
                // soon-to-be-unloaded Map scene.
                StopWorkerCoroutines();
                _busy = false;
                _activeWorkerTG = null;
                CaptionReady = false;
                // CRITICAL: do NOT clear IsPreviewActive here when
                // unloading. StopAllCoroutines doesn't preempt code
                // that's mid-execution — a hook currently inside
                // CarveAllRivers / ForceWaterPlaneRebuild will finish
                // its current frame's work after the Stop call. If we
                // cleared the gate now, those hooks would mutate the
                // Map scene we're about to unload, leaving Unity to
                // clean up 1.5M-cell heightmap modifications and dozens
                // of orphan terrain/water assets — manifests as a 3+
                // minute UnloadUnusedAssets stall during gameplay load.
                // Instead, the UnloadMapScene coroutine clears the gate
                // AFTER the unload completes (see below).
                if (!unload)
                {
                    IsPreviewActive = false;
                }
                if (unload)
                {
                    // Unload regardless of _mapSceneLoadedByUs — if a "Map"
                    // scene is currently loaded (whether we loaded it or
                    // someone else did), it carries our preview's mutated
                    // state and FF must NOT inherit it. The unload-async
                    // op kicked off here completes well before FF's
                    // gameplay flow reaches its own LoadSceneAsync("Map").
                    var sc = SceneManager.GetSceneByName("Map");
                    if (sc.IsValid() && sc.isLoaded)
                    {
                        var host = UnityEngine.Object.FindObjectOfType<PreviewOverlay>();
                        if (host != null)
                        {
                            host.StartCoroutine(UnloadMapScene());
                            Log("HardCancel: Map scene unload coroutine started.");
                        }
                        else
                        {
                            // Fallback — unload without coroutine driver.
                            try { SceneManager.UnloadSceneAsync(sc); }
                            catch (Exception ex) { Log($"HardCancel direct unload failed: {ex.Message}"); }
                        }
                    }
                    else
                    {
                        _mapSceneLoadedByUs = false;
                        _sceneInitPending = false;
                    }
                }
                else
                {
                    _mapSceneLoadedByUs = false;
                    _sceneInitPending = false;
                }
            }
            catch (Exception ex) { Log($"HardCancel failed: {ex.Message}"); }
        }

        /// <summary>Hard-ceiling timeout per map size. Pangu's
        /// GetSeedPreviewAttemptTimeoutSeconds (Pangu_FF.decompiled.cs:2606)
        /// uses 8/12/16s for S/M/L, sized for Pangu's lighter gen. RR's
        /// preview triggers RR's own Stage 38 RiverPaths injection,
        /// RiverWaterAreaBuilder polygon stamping, etc. — significantly
        /// heavier than vanilla. Empirically a Large map's full pipeline
        /// runs ~25-35s with RR's hooks active. Use a generous 60s
        /// ceiling so the flag-gate stays correct through completion.
        /// If the gen is still running at the ceiling, IsPreviewActive
        /// will deliberately remain set — see the polling loop in
        /// PreviewCoroutine.</summary>
        private static float PanguTimeoutFor(int mapSizeIdx) => 60f;

        private static IEnumerator PreviewCoroutine()
        {
            try
            {
                // ── Step 1: try existing TG ──────────────────────────
                var tg = FindUsableTerrainGenerator();
                if (tg != null)
                {
                    Log($"Using existing TerrainGenerator on '{tg.gameObject.name}'.");
                }
                else
                {
                    // ── Step 2: load FF's "Map" scene additively ──────
                    Log("No existing TerrainGenerator — loading 'Map' scene additively...");
                    yield return LoadMapScene();
                    tg = FindTerrainGeneratorInMapScene();
                    if (tg == null)
                    {
                        Log("Map scene loaded but no TerrainGenerator found in its roots.");
                        yield break;
                    }
                    Log($"Loaded 'Map' scene; using its TerrainGenerator on '{tg.gameObject.name}'.");
                }

                // Expose worker TG for the overlay's progress bar reader.
                _activeWorkerTG = tg;

                // ── Step 3: find/configure TerrainGeneratorController ──
                var tgc = tg.GetComponent<TerrainGeneratorController>()
                          ?? tg.GetComponentInParent<TerrainGeneratorController>()
                          ?? UnityEngine.Object.FindObjectOfType<TerrainGeneratorController>();
                if (tgc == null)
                {
                    Log("No TerrainGeneratorController found — can't run native gen path.");
                    yield break;
                }
                Log($"Using TerrainGeneratorController on '{tgc.gameObject.name}'.");

                // ── Step 4: read seed from UI, decode all encoded values ──
                // Pangu's insight: the seed string encodes terrainSeed +
                // themeId + mountainValue + waterValue. SettingsManager
                // .SeedToSettings(seedStr, ref ts, ref t, ref m, ref w)
                // decodes them in one call. The user's biome/water/mountain
                // sliders write into the seed; we just read the seed and
                // get all the values for free.
                string rawSeed = TryReadSeedFromUI();
                if (string.IsNullOrEmpty(rawSeed))
                {
                    Log("No seed in UI input — can't generate preview.");
                    yield break;
                }
                Log($"Seed from UI: '{rawSeed}'");

                if (!TryDecodeSeedString(rawSeed, out int terrainSeed,
                    out int themeId, out int mountainValue, out int waterValue))
                {
                    Log("Failed to decode seed string — can't generate preview.");
                    yield break;
                }
                Log($"Decoded: terrainSeed={terrainSeed} themeId={themeId} " +
                    $"mountains={mountainValue} water={waterValue}");

                // Read map size as TGC.Size enum — Pangu's exact source.
                int mapSizeIdx;
                object mapSizeEnum = TryReadMapSizeEnum(out mapSizeIdx);
                Log($"MapSize: enum={mapSizeEnum} (caption idx {mapSizeIdx})");

                // Resolve theme from themeId via GlobalAssets.mapTypeData.
                object? theme = ResolveTheme(themeId);
                if (theme == null)
                {
                    Log($"Could not resolve theme for themeId={themeId} — gen may fail.");
                }

                // ── Preflight: ensure RuntimeScriptableObjectManager is
                // initialized. GenerateInternal calls
                // RuntimeScriptableObjectManager.CreateInstance<TerrainBiome>()
                // on each biome container; without prior Init() it NREs at
                // first call, which surfaces as "Object reference not set to
                // an instance of an object" thrown from GenSliced_Generate's
                // first MoveNext. Pangu does the same preflight (line 2412 →
                // EnsureRuntimeScriptableObjectManagerInitialized).
                EnsureRuntimeScriptableObjectManagerInitialized();

                // Apply all the gen parameters to TGC + TG.
                ApplyTgcGenParameters(tgc, terrainSeed, mapSizeEnum, theme,
                    mountainValue, waterValue);
                TrySetIsLoadedFalse(tgc);

                // Configure TG.debugOptions — skip expensive stages we don't
                // need for the preview (roads/trees/details/objects). Pangu
                // does this exact same set (Pangu line 2453-2461).
                ConfigureDebugOptions(tg);

                // Reset our renderer's per-gen flag.
                MapPreviewRenderer.RenderedThisGen = false;

                // ── Run the FULL Pangu-canonical pipeline ─────────────
                //
                // Pangu (Pangu_FF.decompiled.cs:2462) calls
                // tgc.GenSliced_Generate(false) and drives the resulting
                // coroutine to completion — including Sliced_OnGenerated
                // and its Terrain2Builder + WaterPlane.Sliced_Rebuild
                // calls. Pangu does NOT skip OnGenerated; the v3 belief
                // that OnGenerated itself causes the gameplay-load hang
                // was a misdiagnosis.
                //
                // The actual cause of the v2 hang was that the polluted
                // Map scene stayed loaded when FF began its gameplay
                // scene transition, and FF adopted the mutated
                // TerrainGeneratorController. Pangu avoids this with
                //   (a) StartSceneManager.StartNewGame prefix → HardCancel
                //   (b) OnSceneWasInitialized("Map") discriminator
                //       → HardCancel when FF (not us) re-inits the scene
                // Both of those are now wired (StartNewGamePatch +
                // RiversRestoredMod.OnSceneWasInitialized). Running the
                // full pipeline here gives us pixel-parity with the
                // actual game gen (no RNG drift, fully populated
                // _generationData.areas / waterAreas / treeDensity).
                //
                // _cancelled is set by HardCancel — abort the drive
                // instead of finishing on a worker that's being torn
                // down.
                Log("Starting full Pangu-canonical gen (tgc.GenSliced_Generate)…");
                IEnumerator? official = null;
                try
                {
                    var miFull = tgc.GetType().GetMethod("GenSliced_Generate",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new[] { typeof(bool) }, null);
                    if (miFull == null)
                    {
                        Log("TGC.GenSliced_Generate(bool) not found — cannot run preview gen.");
                    }
                    else
                    {
                        official = miFull.Invoke(tgc, new object[] { false }) as IEnumerator;
                        if (official == null)
                            Log("GenSliced_Generate returned null enumerator.");
                    }
                }
                catch (Exception ex) { Log($"GenSliced_Generate invoke failed: {ex.Message}"); }

                if (official != null)
                {
                    float timeout = PanguTimeoutFor(mapSizeIdx);
                    IsPreviewActive = true;
                    bool genCompletedGracefully = false;
                    bool captionFired = false;
                    try
                    {
                        // Drive the outer tgc.GenSliced_Generate enumerator
                        // first. NOTE: this finishes after ~1 step because
                        // the outer's body does
                        //   GenerateInternal(game);
                        //   yield return StartCoroutine(tg.GenSliced_Generate(game));
                        // — DriveCoroutineWithTimeout doesn't actually wait
                        // on Unity Coroutine objects (it yields null and
                        // advances), so the outer reports done once it has
                        // kicked off the inner StartCoroutine. The real gen
                        // work continues on that nested coroutine
                        // independently. We then poll tg.generating to wait
                        // for the inner to finish, keeping IsPreviewActive
                        // true for the duration so RR's mutation patches
                        // correctly stand down.
                        yield return DriveCoroutineWithTimeout(official, "tgc.GenSliced_Generate (outer)", 2f);

                        float waitStart = Time.realtimeSinceStartup;
                        var generatingField = AccessTools.Field(tg.GetType(), "generating");
                        int idleFrames = 0;
                        while (true)
                        {
                            bool stillGenerating = false;
                            try
                            {
                                if (generatingField != null)
                                    stillGenerating = (bool)(generatingField.GetValue(tg) ?? false);
                            }
                            catch { stillGenerating = false; }

                            // Soft-cancel exit: TriggerPreviewSoftRestart
                            // (called by auto-regen on user input) sets
                            // _cancelled and StopWorkerCoroutines. The
                            // gen pipeline's coroutines are now dead but
                            // tg.generating stays true forever (the line
                            // that sets it false never ran). Without
                            // this check we'd wait the full 60s timeout
                            // every reroll — a visible 30-60s frozen-
                            // looking bar.
                            if (_cancelled)
                            {
                                Log("Polling loop saw _cancelled — bailing immediately.");
                                break;
                            }

                            if (!stillGenerating)
                            {
                                // Allow a few idle frames for any final
                                // post-Sliced_OnGenerated work that runs
                                // synchronously after the flag flips.
                                if (++idleFrames >= 3)
                                {
                                    genCompletedGracefully = true;
                                    break;
                                }
                            }
                            else
                            {
                                idleFrames = 0;
                            }

                            // Fire the caption build as soon as the
                            // mid-pipeline render has populated
                            // LastRiverCount / LastWaterPct — no need
                            // to wait the full 15-30s for gen to finish.
                            if (!captionFired && MapPreviewRenderer.RenderedThisGen)
                            {
                                captionFired = true;
                                BuildRichCaption(rawSeed, mapSizeIdx, tg);
                                CaptionReady = true;
                                Log("Caption built early (right after preview render).");
                            }

                            float elapsed = Time.realtimeSinceStartup - waitStart;
                            if (elapsed > timeout)
                            {
                                Log($"Inner gen wait HARD-timed out at {elapsed:0.0}s (tg.generating still {stillGenerating}). " +
                                    "Leaving IsPreviewActive set — gen is still mutating the worker, RR patches must keep gating. " +
                                    "Flag will clear on next preview start or HardCancel.");
                                break;
                            }
                            yield return null;
                        }
                        Log($"Inner gen wait done in {(Time.realtimeSinceStartup - waitStart):0.00}s (graceful={genCompletedGracefully}).");
                    }
                    finally
                    {
                        // Only clear if gen completed cleanly. If we
                        // bailed via timeout, the inner gen is STILL
                        // running on a sibling coroutine — RR's stage
                        // hooks will still fire. Clearing the gate now
                        // would let those hooks (RiverCarver,
                        // ForceWaterPlaneRebuild) mutate the live Map
                        // scene, which then gets adopted by FF's
                        // gameplay flow → 3-minute UnloadUnusedAssets
                        // stall on Start click. Keep the gate set;
                        // HardCancel + the next TriggerPreview reset it.
                        if (genCompletedGracefully)
                            IsPreviewActive = false;
                    }
                }

                if (_cancelled)
                {
                    Log("Preview cancelled mid-gen — skipping render.");
                    yield break;
                }

                DumpGeneratorState(tg);

                // INTENTIONALLY no post-completion render here. The
                // earlier mid-gen render fired by LateCarvePostfix
                // (RiverSettingsPatch.cs:196) captures _generationData
                // at the SAME pipeline phase as in-game gen's
                // LateCarvePostfix, giving 1:1 preview-vs-gameplay
                // parity. Re-rendering after Sliced_OnGenerated produced
                // a slightly different image (post-smoothing, post-lake-
                // bed adjustments) — visually striking ("preview went
                // black and reloaded") and confusing because the
                // resulting preview no longer matched what the user saw
                // in-game. BuildRichCaption still runs below.

                // Final caption build for any data that wasn't ready
                // mid-gen. The early-fire above already populated the
                // caption when the preview rendered — this is a
                // belt-and-braces re-fire in case BuildRichCaption
                // didn't get to run (e.g., gen hard-timed-out before
                // RenderedThisGen flipped). Cheap to re-run.
                BuildRichCaption(rawSeed, mapSizeIdx, tg);
                CaptionReady = true;

                Log("Preview gen complete.");

                // Optional: unload Map scene to free resources. Skipped for
                // V3 — keeping it loaded means subsequent previews don't pay
                // the load cost again. Trade-off: extra memory while the
                // user is on the New Game screen.
                // if (_mapSceneLoadedByUs) yield return UnloadMapScene();
            }
            finally
            {
                _busy = false;
                // NOTE: deliberately NOT clearing IsPreviewActive here.
                // The inner-block conditional finally only clears it on
                // graceful gen completion. If we bailed (cancel, error,
                // hard timeout), the inner gen may still be running and
                // we MUST keep RR's mutation patches gated. The flag is
                // reset in three places: TriggerPreview start, HardCancel,
                // and the inner conditional finally.
                if (_pendingRestart)
                {
                    _pendingRestart = false;
                    _cancelled = false;  // clear so next gen runs
                    Log("Soft-restart firing.");
                    var host2 = UnityEngine.Object.FindObjectOfType<PreviewOverlay>();
                    if (host2 != null)
                    {
                        _busy = true;
                        host2.StartCoroutine(PreviewCoroutine());
                    }
                }
            }
        }

        /// <summary>Load FF's "Map" scene additively. Returns when the
        /// load completes (isDone == true). If the scene is already
        /// loaded, returns immediately without re-loading.</summary>
        private static IEnumerator LoadMapScene()
        {
            var existing = SceneManager.GetSceneByName("Map");
            if (existing.IsValid() && existing.isLoaded)
            {
                Log("'Map' scene already loaded.");
                yield break;
            }

            AsyncOperation? op = null;
            try { op = SceneManager.LoadSceneAsync("Map", LoadSceneMode.Additive); }
            catch (Exception ex) { Log($"LoadSceneAsync failed: {ex.Message}"); yield break; }
            if (op == null) { Log("LoadSceneAsync returned null."); yield break; }
            _mapSceneLoadedByUs = true;
            // Mirrors Pangu's _seedPreviewTemplateSceneInitPending — set
            // before the scene's OnSceneWasInitialized hook fires so the
            // discriminator in RiversRestoredMod knows the load is ours.
            _sceneInitPending = true;
            while (!op.isDone) yield return null;
            Log("'Map' scene load complete.");
        }

        private static IEnumerator UnloadMapScene()
        {
            var sc = SceneManager.GetSceneByName("Map");
            if (!sc.IsValid() || !sc.isLoaded)
            {
                // Nothing to unload — clear gate and exit.
                IsPreviewActive = false;
                _mapSceneLoadedByUs = false;
                _sceneInitPending = false;
                yield break;
            }
            AsyncOperation? op = null;
            try { op = SceneManager.UnloadSceneAsync(sc); }
            catch (Exception ex)
            {
                Log($"UnloadSceneAsync failed: {ex.Message}");
                IsPreviewActive = false;
                yield break;
            }
            if (op == null) { IsPreviewActive = false; yield break; }
            while (!op.isDone) yield return null;
            _mapSceneLoadedByUs = false;
            _sceneInitPending = false;
            // Clear the gate AFTER unload completes. Any in-flight hook
            // that races to fire between StopWorkerCoroutines and this
            // line still sees IsPreviewActive=true and bails. Once the
            // scene is gone, the RR's hooks have nothing to run against
            // anyway — clearing the gate is safe so FF's gameplay gen
            // (which fires next on a fresh Map scene) can run normally.
            IsPreviewActive = false;
            Log("'Map' scene unloaded; preview gate cleared.");
        }

        /// <summary>Drive an inner gen coroutine with a hard wall-clock
        /// timeout. Yields `null` between MoveNext calls instead of
        /// forwarding the inner Coroutine wait — that lets us check the
        /// timeout every frame even if the inner coroutine is stuck
        /// waiting on a nested Unity Coroutine that faulted/orphaned.
        /// Trade-off: nested StartCoroutine results aren't awaited
        /// properly, so the inner gen may advance one frame faster than
        /// it expected. For our sliced gen pipeline this is fine —
        /// stages don't actually depend on cross-frame waits, they just
        /// yield to amortize CPU across frames.</summary>
        private static IEnumerator DriveCoroutineWithTimeout(IEnumerator inner, string label, float timeoutSeconds)
        {
            float startedAt = Time.realtimeSinceStartup;
            int steps = 0;
            while (true)
            {
                bool moved;
                try { moved = inner.MoveNext(); }
                catch (Exception ex)
                {
                    Log($"{label} threw at step {steps}: {ex.GetType().Name}: {ex.Message}");
                    break;
                }
                if (!moved) break;
                steps++;
                if (Time.realtimeSinceStartup - startedAt > timeoutSeconds)
                {
                    Log($"{label} timeout at step {steps} after {timeoutSeconds:0.0}s.");
                    break;
                }
                yield return null;
            }
            Log($"{label} done: {steps} steps in {(Time.realtimeSinceStartup - startedAt):0.00}s.");
        }

        /// <summary>Pangu-pattern unload trigger. Called every frame from
        /// PreviewOverlay.Update. When the user clicks Start (gameplay
        /// context activates — GameManager.terrainManager becomes non-null),
        /// we stop our worker's coroutines and unload the Map scene we
        /// loaded for previews. This prevents the preview's mutated TGC
        /// from polluting the actual game's gen and hanging the load
        /// screen. Mirrors Pangu's StartSeedPreviewTemplateSceneUnloadIfNeeded
        /// (Pangu_FF.cs:3204).</summary>
        public static void TickUnloadWatchdog(MonoBehaviour host)
        {
            // BELT-AND-BRACES fallback only. The primary cleanup paths
            // are now StartNewGamePatch.Prefix (fires when user clicks
            // Start) and RiversRestoredMod.OnSceneWasInitialized
            // (fires when FF re-initializes the Map scene). This
            // watchdog only triggers if both of those somehow missed —
            // by then GameManager.terrainManager has been wired up,
            // which means FF is mid-initialization. Don't try to
            // unload at this point (the scene is now FF-owned), just
            // stop any rogue coroutines and clear our flags.
            if (!_mapSceneLoadedByUs) return;
            if (_busy) return;  // mid-preview, don't yank the rug
            if (!IsGameplayContextActive()) return;

            Log("Watchdog: gameplay context active without prior cleanup — stopping coroutines and releasing scene flag (FF owns it now).");
            StopWorkerCoroutines();
            _mapSceneLoadedByUs = false;
            _sceneInitPending = false;
        }

        /// <summary>Pangu's IsGameplayContextActive (line 1917): returns
        /// true once GameManager.Instance has a terrainManager wired up,
        /// i.e. the actual game's terrain is being initialized. This is
        /// the signal that gameplay is starting and we must release the
        /// preview's hold on the Map scene.</summary>
        private static bool IsGameplayContextActive()
        {
            try
            {
                Type? gmType = AccessTools.TypeByName("GameManager");
                if (gmType == null) return false;
                var instProp = gmType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static);
                var inst = instProp?.GetValue(null);
                if (inst == null) return false;
                var tmField = gmType.GetField("terrainManager",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var tmProp = tmField == null
                    ? gmType.GetProperty("terrainManager",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    : null;
                var tm = tmField?.GetValue(inst) ?? tmProp?.GetValue(inst);
                return tm is UnityEngine.Object o && o != null;
            }
            catch { return false; }
        }

        /// <summary>Stop all coroutines on the worker TGC + TG before
        /// scene unload. Pangu does the same (line 3159 StopSeedPreviewWorkerCoroutines).
        /// Without this, in-flight Sliced_Rebuild coroutines can crash
        /// when their target GameObjects get destroyed by the unload.</summary>
        private static void StopWorkerCoroutines()
        {
            try
            {
                var sc = SceneManager.GetSceneByName("Map");
                if (!sc.IsValid() || !sc.isLoaded) return;
                var roots = sc.GetRootGameObjects();
                if (roots == null) return;
                foreach (var root in roots)
                {
                    if (root == null) continue;
                    foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        try { if (mb != null) mb.StopAllCoroutines(); }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { Log($"StopWorkerCoroutines failed: {ex.Message}"); }
        }

        /// <summary>Find a TerrainGenerator inside the loaded "Map" scene.
        /// Mirrors Pangu's TryCreateSeedPreviewWorkerFromLoadedObjects
        /// — walks scene root GameObjects and looks for the component.</summary>
        private static TerrainGenerator? FindTerrainGeneratorInMapScene()
        {
            try
            {
                var sc = SceneManager.GetSceneByName("Map");
                if (!sc.IsValid() || !sc.isLoaded) return null;
                var roots = sc.GetRootGameObjects();
                if (roots == null) return null;
                foreach (var root in roots)
                {
                    if (root == null) continue;
                    var tg = root.GetComponent<TerrainGenerator>()
                             ?? root.GetComponentInChildren<TerrainGenerator>(true);
                    if (tg != null) return tg;
                }
            }
            catch (Exception ex)
            {
                Log($"FindTerrainGeneratorInMapScene failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>Find any TerrainGenerator instance currently in the
        /// scene — RR's cached one (gameplay or Pangu worker) or any other.</summary>
        private static TerrainGenerator? FindUsableTerrainGenerator()
        {
            if (RiverSettingsPatch.CachedGenerator != null)
                return RiverSettingsPatch.CachedGenerator;
            return UnityEngine.Object.FindObjectOfType<TerrainGenerator>();
        }

        private static string TryReadSeedFromUI()
        {
            try
            {
                var allInputs = Resources.FindObjectsOfTypeAll<TMPro.TMP_InputField>();
                foreach (var f in allInputs)
                {
                    if (f == null || string.IsNullOrEmpty(f.gameObject.name)) continue;
                    var n = f.gameObject.name.ToLowerInvariant();
                    if (n.Contains("seed") || n.Contains("map seed"))
                    {
                        var txt = f.text;
                        if (!string.IsNullOrEmpty(txt)) return txt;
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        /// <summary>Decode the user's seed string into its component values
        /// using FF's static <c>SettingsManager.SeedToSettings</c>. The
        /// seed string encodes terrainSeed (int) + themeId (byte) +
        /// mountainValue (byte) + waterValue (byte) in one string —
        /// reading the seed gets all four UI values for free without
        /// having to find/read each individual UI control.</summary>
        private static bool TryDecodeSeedString(string rawSeed, out int terrainSeed,
            out int themeId, out int mountainValue, out int waterValue)
        {
            terrainSeed = 1; themeId = 0; mountainValue = 0; waterValue = 0;
            try
            {
                var smType = AccessTools.TypeByName("SettingsManager");
                if (smType == null) { Log("SettingsManager type not found."); return false; }

                // SeedToSettings(string, ref int, ref byte, ref byte, ref byte)
                var m = smType.GetMethod("SeedToSettings",
                    BindingFlags.Public | BindingFlags.Static);
                if (m == null) { Log("SettingsManager.SeedToSettings not found."); return false; }

                // Build args: string + 4 ref values (boxed)
                int ts = 1; byte b1 = 0, b2 = 0, b3 = 0;
                object[] args = new object[] { rawSeed, ts, b1, b2, b3 };
                m.Invoke(null, args);
                terrainSeed = (int)args[1];
                themeId = (byte)args[2];
                mountainValue = (byte)args[3];
                waterValue = (byte)args[4];
                if (terrainSeed <= 0)
                {
                    Log("SeedToSettings returned terrainSeed <= 0 — invalid seed.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log($"TryDecodeSeedString failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Ensure FF's RuntimeScriptableObjectManager is initialized
        /// so GenerateInternal can call CreateInstance&lt;TerrainBiome&gt;()
        /// when duplicating biome assets. Mirrors Pangu line 3231 logic:
        /// only call Init() when the manager's static list is null.</summary>
        private static void EnsureRuntimeScriptableObjectManagerInitialized()
        {
            try
            {
                Type? rsmType = AccessTools.TypeByName("RuntimeScriptableObjectManager");
                if (rsmType == null)
                {
                    Log("RuntimeScriptableObjectManager type not found — skipping preflight.");
                    return;
                }

                // Check if already initialized by inspecting the static list
                // field (any field of List<...> type will do — Pangu probes
                // the same field). If null, call Init() to populate.
                bool needsInit = true;
                FieldInfo? listField = null;
                foreach (var f in rsmType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (typeof(System.Collections.IList).IsAssignableFrom(f.FieldType))
                    {
                        listField = f;
                        break;
                    }
                }
                if (listField != null)
                {
                    try
                    {
                        var v = listField.GetValue(null);
                        if (v != null) needsInit = false;
                    }
                    catch { needsInit = true; }
                }

                if (needsInit)
                {
                    var initMi = rsmType.GetMethod("Init",
                        BindingFlags.Public | BindingFlags.Static);
                    if (initMi != null)
                    {
                        initMi.Invoke(null, null);
                        Log("RuntimeScriptableObjectManager.Init() called.");
                    }
                    else
                    {
                        Log("RuntimeScriptableObjectManager.Init() not found.");
                    }
                }
                else
                {
                    Log("RuntimeScriptableObjectManager already initialized.");
                }
            }
            catch (Exception ex)
            {
                Log($"EnsureRuntimeScriptableObjectManagerInitialized failed: {ex.Message}");
            }
        }

        /// <summary>Read map size as the TGC.Size enum — IDENTICAL to
        /// Pangu's source-of-truth: <c>(Size)(int)SettingsManager.mapSizeValue</c>.
        ///
        /// SettingsManager.mapSizeValue is updated LIVE by the New Game UI's
        /// slider callback (Assembly-CSharp.cs:288782 OnMapSizeChanged →
        /// `mapSizeValue = (Size)(2 - num)`), so it reflects the user's
        /// current selection without needing to read the slider directly.
        /// The enum-aligned value (Large=0, Medium=1, Small=2) is stored,
        /// so we can pass it straight to TGC.size with no inversion.
        ///
        /// Returns the enum boxed as object so callers can pass it to
        /// reflection-set TGC.size without re-converting. Falls back to
        /// Size.Medium (enum value 1) on any failure.</summary>
        private static object TryReadMapSizeEnum(out int idxForCaption)
        {
            // FF's enum order is Large=0, Medium=1, Small=2. Caption mapping
            // (0=Small..2=Large) is the slider/UI order, so we invert:
            //   enum 0 (Large)  → caption idx 2
            //   enum 1 (Medium) → caption idx 1
            //   enum 2 (Small)  → caption idx 0
            try
            {
                var smType = AccessTools.TypeByName("SettingsManager");
                if (smType != null)
                {
                    // mapSizeValue is a STATIC PROPERTY in
                    // SettingsManager (Assembly-CSharp.cs:100845), not a
                    // field. Backing field is _mapSizeValue
                    // (Assembly-CSharp.cs:100401). Earlier code tried
                    // GetField("mapSizeValue") which returns null, falling
                    // through to the default-Medium return at the bottom
                    // — meaning the preview ALWAYS rendered at Medium
                    // regardless of slider position. Read the property
                    // first; fall back to the backing field as belt-and-
                    // braces in case FF renames in a future patch.
                    object? v = null;
                    try
                    {
                        var prop = smType.GetProperty("mapSizeValue",
                            BindingFlags.Public | BindingFlags.Static);
                        v = prop?.GetValue(null);
                    }
                    catch { }
                    if (v == null)
                    {
                        try
                        {
                            var f = smType.GetField("_mapSizeValue",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                            v = f?.GetValue(null);
                        }
                        catch { }
                    }
                    if (v != null)
                    {
                        int enumVal = v is Enum e ? Convert.ToInt32(e)
                                    : v is byte b ? b
                                    : v is int i  ? i
                                    : 1;
                        idxForCaption = Mathf.Clamp(2 - enumVal, 0, 2);
                        return v;  // already a Size enum boxed
                    }
                }
            }
            catch { }
            idxForCaption = 1;
            // Default to Medium = enum value 1.
            var sizeType = AccessTools.TypeByName("TerrainGeneratorController+Size")
                          ?? AccessTools.TypeByName("TerrainGeneratorController.Size");
            return sizeType != null
                ? Enum.ToObject(sizeType, 1)
                : (object)1;
        }

        /// <summary>Resolve a Terrain2Theme from a theme ID via
        /// <c>GlobalAssets.mapTypeData.GetMapThemeFromID(themeId, false)</c>.
        /// Required input to TGC.theme — without it, GenerateInternal NREs.</summary>
        private static object? ResolveTheme(int themeId)
        {
            try
            {
                var gaType = AccessTools.TypeByName("GlobalAssets");
                if (gaType == null) { Log("GlobalAssets type not found."); return null; }
                var mtdField = gaType.GetField("mapTypeData",
                    BindingFlags.Public | BindingFlags.Static);
                var mtdProp = gaType.GetProperty("mapTypeData",
                    BindingFlags.Public | BindingFlags.Static);
                var mtd = mtdField?.GetValue(null) ?? mtdProp?.GetValue(null);
                if (mtd == null) { Log("GlobalAssets.mapTypeData is null."); return null; }

                var m = mtd.GetType().GetMethod("GetMapThemeFromID",
                    new[] { typeof(byte), typeof(bool) });
                if (m == null)
                {
                    Log("GetMapThemeFromID(byte, bool) not found on mapTypeData.");
                    return null;
                }
                var theme = m.Invoke(mtd, new object[] { (byte)themeId, false });
                if (theme == null) { Log($"GetMapThemeFromID({themeId}) returned null."); }
                return theme;
            }
            catch (Exception ex)
            {
                Log($"ResolveTheme failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Apply all the gen parameters to the worker TGC. Mirrors
        /// the field set Pangu writes before calling GenSliced_Generate
        /// (Pangu_FF.decompiled.cs:2438-2449): terrainType (from
        /// SettingsManager.Instance.mapType), seed, size (already a Size
        /// enum, no conversion needed), theme, mountains, water, lakes
        /// (from SettingsManager.mapLakeValue).</summary>
        private static void ApplyTgcGenParameters(TerrainGeneratorController tgc,
            int terrainSeed, object mapSizeEnum, object? theme,
            int mountainValue, int waterValue)
        {
            var tgcType = tgc.GetType();
            try
            {
                // terrainType from SettingsManager.Instance.mapType.
                // mapType is a property (Assembly-CSharp.cs:100701), not
                // a field — same property-vs-field gotcha that hit
                // mapSizeValue. Read property first, fall back to the
                // _mapType backing field.
                var smType = AccessTools.TypeByName("SettingsManager");
                if (smType != null)
                {
                    var instProp = smType.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static);
                    var inst = instProp?.GetValue(null);
                    if (inst != null)
                    {
                        object? mtVal = null;
                        try
                        {
                            var mtProp = smType.GetProperty("mapType",
                                BindingFlags.Public | BindingFlags.Instance);
                            mtVal = mtProp?.GetValue(inst);
                        }
                        catch { }
                        if (mtVal == null)
                        {
                            try
                            {
                                var mtField = smType.GetField("_mapType",
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                mtVal = mtField?.GetValue(inst);
                            }
                            catch { }
                        }
                        if (mtVal != null)
                        {
                            var ttField = AccessTools.Field(tgcType, "terrainType");
                            ttField?.SetValue(tgc, mtVal);
                        }
                    }
                }

                SetTgcField(tgc, tgcType, "seed", terrainSeed);
                // mapSizeEnum is already a TGC.Size enum value boxed —
                // assign directly. TryReadMapSizeEnum returns
                // SettingsManager.mapSizeValue verbatim, which is the
                // enum-aligned value the slider's OnMapSizeChanged
                // callback writes (Assembly-CSharp.cs:288785).
                SetTgcField(tgc, tgcType, "size", mapSizeEnum);
                if (theme != null) SetTgcField(tgc, tgcType, "theme", theme);
                SetTgcField(tgc, tgcType, "mountains", Mathf.Clamp01(mountainValue / 255f));
                SetTgcField(tgc, tgcType, "water", Mathf.Clamp01(waterValue / 255f));

                // lakes from static SettingsManager.mapLakeValue.
                // Property (Assembly-CSharp.cs:100785), not field.
                // Same bug as mapSizeValue/mapType.
                if (smType != null)
                {
                    object? lakesV = null;
                    try
                    {
                        var lakesProp = smType.GetProperty("mapLakeValue",
                            BindingFlags.Public | BindingFlags.Static);
                        lakesV = lakesProp?.GetValue(null);
                    }
                    catch { }
                    if (lakesV == null)
                    {
                        try
                        {
                            var lakesF = smType.GetField("_mapLakeValue",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                            lakesV = lakesF?.GetValue(null);
                        }
                        catch { }
                    }
                    if (lakesV != null) SetTgcField(tgc, tgcType, "lakes", lakesV);
                }

                Log($"Applied TGC params: seed={terrainSeed} size={mapSizeEnum} " +
                    $"theme={(theme != null ? theme.GetType().Name : "null")} " +
                    $"mountains={mountainValue} water={waterValue}");
            }
            catch (Exception ex) { Log($"ApplyTgcGenParameters failed: {ex.Message}"); }
        }

        private static void SetTgcField(object tgc, Type tgcType, string fieldName, object value)
        {
            try
            {
                var f = AccessTools.Field(tgcType, fieldName);
                if (f != null) { f.SetValue(tgc, value); return; }
                var p = tgcType.GetProperty(fieldName);
                if (p != null && p.CanWrite) p.SetValue(tgc, value);
            }
            catch { }
        }

        /// <summary>Force isLoaded=false on the controller so a fresh gen runs.</summary>
        private static void TrySetIsLoadedFalse(TerrainGeneratorController tgc)
        {
            try
            {
                var f = AccessTools.Field(tgc.GetType(), "isLoaded")
                       ?? AccessTools.Field(tgc.GetType(), "IsLoaded");
                if (f != null) f.SetValue(tgc, false);
            }
            catch { }
        }

        /// <summary>Configure TG.debugOptions to skip expensive stages we
        /// don't need for preview rendering. Mirrors Pangu's settings.</summary>
        private static void ConfigureDebugOptions(TerrainGenerator tg)
        {
            try
            {
                var doField = AccessTools.Field(tg.GetType(), "debugOptions")
                              ?? AccessTools.Field(tg.GetType(), "_debugOptions");
                var debugOpts = doField?.GetValue(tg);
                if (debugOpts == null) return;
                var t = debugOpts.GetType();
                void Set(string n, bool v)
                {
                    var f = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null) f.SetValue(debugOpts, v);
                }
                Set("generateFeatures", true);
                // Honor the master rivers toggle. If RiversEnabled is false,
                // RR's river patches no-op (RiverCarver/RiverSettingsPatch
                // early-return), so requesting generateRivers=true here makes
                // FF's terrain gen wait for state RR's patches won't produce
                // — preview hangs indefinitely. Match the toggle so the
                // generator skips river stages entirely.
                Set("generateRivers", RiversRestoredMod.RiversEnabled?.Value ?? true);
                Set("paintTerrain", true);
                Set("paintBiomes", true);
                Set("generateRoads", false);
                Set("generateTrees", false);
                Set("generateDetails", false);
                Set("generateObjects", false);
            }
            catch (Exception ex) { Log($"ConfigureDebugOptions failed: {ex.Message}"); }
        }

        /// <summary>Invoke TGC.GenerateInternal(false) — the synchronous
        /// version of FF's gen pipeline. Returns true on success.</summary>
        private static bool InvokeGenerateInternal(TerrainGeneratorController tgc)
        {
            try
            {
                var m = AccessTools.Method(tgc.GetType(), "GenerateInternal", new[] { typeof(bool) });
                if (m == null)
                {
                    Log("GenerateInternal(bool) method not found on TGC.");
                    return false;
                }
                m.Invoke(tgc, new object[] { false });
                return true;
            }
            catch (TargetInvocationException tex)
            {
                var inner = tex.InnerException;
                Log($"GenerateInternal threw: {(inner != null ? inner.GetType().Name + ": " + inner.Message : tex.Message)}");
                return false;
            }
            catch (Exception ex)
            {
                Log($"GenerateInternal threw: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Override PreviewOverlay's caption with a comprehensive
        /// metadata string: seed, biome, map size, 4 difficulty selections,
        /// river count, water %. Reads the difficulty values from
        /// SettingsManager's static fields:
        ///   startingResourcesDifficultyValue (Resources)
        ///   diseaseDifficultyValue           (Maladies)
        ///   animalDifficultyValue            (Wildlife)
        ///   raiderDifficultyValue            (Raiders)
        /// FF's Difficulty enum maps Easy/Normal/Hard → Pioneer/Trailblazer/Vanquisher
        /// in the UI.</summary>
        private static void BuildRichCaption(string rawSeed, int mapSizeIdx, TerrainGenerator tg)
        {
            try
            {
                string biome = (RiversRestoredMod.RiverPreset?.Value ?? RiverPresetMode.IdyllicValley).ToString();
                string size = mapSizeIdx switch
                {
                    0 => "Small",
                    1 => "Medium",
                    2 => "Large",
                    _ => $"Size{mapSizeIdx}",
                };

                // River + water % — read from MapPreviewRenderer which
                // already computed these accurately from the polygon-raster
                // pixel count. Avoids the bbox-overlap double-counting bug
                // we had earlier (rivers crossing the map produce huge
                // bboxes that inflate water%).
                int riverCount = MapPreviewRenderer.LastRiverCount;
                int waterPct = MapPreviewRenderer.LastWaterPct;

                // Read 4 difficulty values from SettingsManager (static).
                // Diagnostic dump so we can see the RAW enum values and
                // map them correctly per category. Some categories may
                // have non-obvious mappings between UI button labels and
                // stored enum values.
                string res = ReadDifficulty("startingResourcesDifficultyValue");
                string mal = ReadDifficulty("diseaseDifficultyValue");
                // Wildlife/Raiders FF UI buttons store enum value one tier
                // higher than the UI label suggests (Pioneer button writes
                // Difficulty.Normal, not Easy). Empirically verified by
                // user — offset -1 maps stored values back to UI labels.
                string wld = ReadDifficulty("animalDifficultyValue", offset: -1);
                string raid = ReadDifficulty("raiderDifficultyValue", offset: -1);
                Log($"  Difficulty raw values: " +
                    $"resources={ReadDifficultyRaw("startingResourcesDifficultyValue")} " +
                    $"disease={ReadDifficultyRaw("diseaseDifficultyValue")} " +
                    $"animal={ReadDifficultyRaw("animalDifficultyValue")} " +
                    $"raider={ReadDifficultyRaw("raiderDifficultyValue")}");

                // 3 columns × 2 rows:
                //   Left:    "Seed X · Biome · Size"     | "N river(s) · N% water"
                //   Mid:     "Resources: T"              | "Wildlife: T"
                //   Right:   "Maladies: T"               | "Raiders: T"
                PreviewOverlay.LatestCaptionLeft =
                    $"Seed {rawSeed} · {biome} · {size}\n" +
                    $"{riverCount} river(s) · {waterPct}% water";
                PreviewOverlay.LatestCaptionMid =
                    $"Resources: {res}\nWildlife: {wld}";
                PreviewOverlay.LatestCaptionRight =
                    $"Maladies: {mal}\nRaiders: {raid}";
                PreviewOverlay.LatestCaption = ""; // unused with split columns
            }
            catch (Exception ex)
            {
                Log($"BuildRichCaption failed: {ex.Message}");
            }
        }

        /// <summary>Read a Difficulty-typed static field/property from
        /// SettingsManager and convert to its UI label (P/T/V/X). Some
        /// categories (Wildlife/Raiders) store the enum value one tier
        /// higher than the UI button label — pass offset=-1 for those
        /// to subtract one tier before mapping.</summary>
        private static string ReadDifficulty(string fieldName, int offset = 0)
        {
            try
            {
                var smType = AccessTools.TypeByName("SettingsManager");
                if (smType == null) return "?";
                var prop = smType.GetProperty(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                object? val = prop?.GetValue(null);
                if (val == null)
                {
                    var fld = smType.GetField(fieldName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                              ?? smType.GetField("_" + fieldName,
                                  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    val = fld?.GetValue(null);
                }
                if (val == null) return "?";

                // Apply offset by converting to int, adjusting, clamping
                // to enum range, then converting back to enum value name.
                if (offset != 0 && val is Enum e)
                {
                    int raw = Convert.ToInt32(e);
                    int adjusted = Math.Max(0, Math.Min(3, raw + offset));  // Difficulty: 0..3
                    val = Enum.ToObject(val.GetType(), adjusted);
                }

                return DifficultyToUiLabel(val.ToString());
            }
            catch { return "?"; }
        }

        /// <summary>Read the raw enum value name + numeric for a difficulty
        /// field. Diagnostic only — used to verify what FF actually stores
        /// when the user clicks UI difficulty buttons.</summary>
        private static string ReadDifficultyRaw(string fieldName)
        {
            try
            {
                var smType = AccessTools.TypeByName("SettingsManager");
                if (smType == null) return "?";
                var prop = smType.GetProperty(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                object? val = prop?.GetValue(null);
                if (val == null)
                {
                    var fld = smType.GetField(fieldName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                              ?? smType.GetField("_" + fieldName,
                                  BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    val = fld?.GetValue(null);
                }
                if (val == null) return "null";
                if (val is Enum e) return $"{val}({Convert.ToInt32(e)})";
                return val.ToString();
            }
            catch (Exception ex) { return $"err:{ex.Message}"; }
        }

        /// <summary>FF's Difficulty enum (Easy/Normal/Hard/VeryHard) maps to
        /// the UI labels (Pioneer/Trailblazer/Vanquisher/VeryHard) — but for
        /// the caption we use single-letter abbreviations to fit narrow
        /// columns.</summary>
        private static string DifficultyToUiLabel(string difficultyName) => difficultyName switch
        {
            "Easy"     => "P",   // Pioneer
            "Normal"   => "T",   // Trailblazer
            "Hard"     => "V",   // Vanquisher
            "VeryHard" => "X",   // (UI-hidden tier)
            _ => difficultyName,
        };

        /// <summary>Call TG.ResetGeneratedData() to clear any prior gen state.</summary>
        private static void TryResetGeneratedData(TerrainGenerator tg)
        {
            try
            {
                var m = AccessTools.Method(tg.GetType(), "ResetGeneratedData", new Type[0]);
                m?.Invoke(tg, null);
            }
            catch (Exception ex) { Log($"ResetGeneratedData threw: {ex.Message}"); }
        }

        /// <summary>Set a public/non-public bool field on the worker TG.</summary>
        private static void SetWorkerFlag(TerrainGenerator tg, string fieldName, bool value)
        {
            try
            {
                var f = AccessTools.Field(tg.GetType(), fieldName);
                if (f != null && f.FieldType == typeof(bool)) f.SetValue(tg, value);
            }
            catch { }
        }

        /// <summary>Set the worker's RNG seed from terrainSettings.seed —
        /// makes the gen deterministic per seed.</summary>
        private static void SetRngSeed(TerrainGenerator tg)
        {
            try
            {
                var rngField = AccessTools.Field(tg.GetType(), "rng")
                              ?? AccessTools.Field(tg.GetType(), "_rng");
                var rng = rngField?.GetValue(tg);
                if (rng == null) return;

                var tsField = AccessTools.Field(tg.GetType(), "terrainSettings");
                var ts = tsField?.GetValue(tg);
                int seed = 1;
                if (ts != null)
                {
                    var sField = ts.GetType().GetField("seed",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (sField != null) seed = Math.Max(1, (int)(sField.GetValue(ts) ?? 1));
                }

                var seedProp = rng.GetType().GetProperty("Seed");
                if (seedProp != null && seedProp.CanWrite)
                {
                    seedProp.SetValue(rng, (uint)seed);
                }
                else
                {
                    var seedF = rng.GetType().GetField("Seed",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (seedF != null) seedF.SetValue(rng, (uint)seed);
                }
            }
            catch (Exception ex) { Log($"SetRngSeed failed: {ex.Message}"); }
        }

        /// <summary>Allocate a fresh _generationData and assign to TG.
        /// Pangu does this between PreGenerate and GenerateAsync.</summary>
        private static void CreateFreshGenerationData(TerrainGenerator tg)
        {
            try
            {
                var gdField = AccessTools.Field(typeof(TerrainGenerator), "_generationData");
                if (gdField == null) { Log("_generationData field not found."); return; }
                var gdType = gdField.FieldType;
                var inst = Activator.CreateInstance(gdType);
                gdField.SetValue(tg, inst);
            }
            catch (Exception ex) { Log($"CreateFreshGenerationData failed: {ex.Message}"); }
        }

        /// <summary>Invoke a no-arg method on the TG. Returns true on success.</summary>
        private static bool InvokeMethodIfPresent(TerrainGenerator tg, string methodName)
        {
            try
            {
                var m = AccessTools.Method(tg.GetType(), methodName, new Type[0]);
                if (m == null) { Log($"{methodName} method not found."); return false; }
                m.Invoke(tg, null);
                return true;
            }
            catch (TargetInvocationException tex)
            {
                var inner = tex.InnerException;
                Log($"  {methodName} threw: {(inner != null ? inner.GetType().Name + ": " + inner.Message : tex.Message)}");
                return false;
            }
            catch (Exception ex)
            {
                Log($"  {methodName} threw: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static void TrySetSeedOnGenerator(TerrainGenerator tg, string seedText)
        {
            try
            {
                foreach (var name in new[] { "currentMapSeed", "_seed", "seed", "mapSeed" })
                {
                    var f = AccessTools.Field(typeof(TerrainGenerator), name);
                    if (f == null) continue;
                    if (f.FieldType == typeof(string)) { f.SetValue(tg, seedText); return; }
                    if (f.FieldType == typeof(int) && int.TryParse(seedText, out var iv))
                    { f.SetValue(tg, iv); return; }
                }
                var gdField = AccessTools.Field(typeof(TerrainGenerator), "_generationData");
                var gd = gdField?.GetValue(tg);
                if (gd != null)
                {
                    var sf = gd.GetType().GetField("seed",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (sf != null && sf.FieldType == typeof(string))
                        sf.SetValue(gd, seedText);
                }
            }
            catch (Exception ex) { Log($"Seed set failed: {ex.Message}"); }
        }

        private static void InvokeStageIfPresent(TerrainGenerator tg, string methodName)
        {
            try
            {
                var m = AccessTools.Method(typeof(TerrainGenerator), methodName, new Type[0]);
                if (m == null) return;
                m.Invoke(tg, null);
            }
            catch (TargetInvocationException tex)
            {
                var inner = tex.InnerException;
                Log($"  {methodName} threw: {(inner != null ? inner.GetType().Name + ": " + inner.Message : tex.Message)}");
            }
            catch (Exception ex)
            {
                Log($"  {methodName} threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void DumpGeneratorState(TerrainGenerator tg)
        {
            try
            {
                var t = typeof(TerrainGenerator);
                foreach (var name in new[] { "mapSettings", "baseSettings", "riverSettings",
                                              "waterSettings", "biomeSettings", "_generationData" })
                {
                    var f = AccessTools.Field(t, name);
                    if (f == null) { Log($"  state: {name} = <field missing>"); continue; }
                    var v = f.GetValue(tg);
                    Log($"  state: {name} = {(v == null ? "<null>" : v.GetType().Name)}");
                }

                var gdField = AccessTools.Field(t, "_generationData");
                var gd = gdField?.GetValue(tg);
                if (gd != null)
                {
                    var hnField = gd.GetType().GetField("heightNoise",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (hnField?.GetValue(gd) is float[,] hn)
                    {
                        float min = float.MaxValue, max = float.MinValue;
                        for (int x = 0; x < hn.GetLength(0); x++)
                            for (int z = 0; z < hn.GetLength(1); z++)
                            {
                                if (hn[x, z] < min) min = hn[x, z];
                                if (hn[x, z] > max) max = hn[x, z];
                            }
                        Log($"  state: heightNoise[{hn.GetLength(0)}x{hn.GetLength(1)}] min={min:F3} max={max:F3}");
                    }
                    else
                    {
                        Log($"  state: heightNoise = <null>");
                    }
                    var waField = gd.GetType().GetField("waterAreas",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var wa = waField?.GetValue(gd) as System.Collections.IList;
                    Log($"  state: waterAreas count={(wa?.Count ?? -1)}");
                }
            }
            catch (Exception ex) { Log($"  state dump failed: {ex.Message}"); }
        }

        private static void Log(string msg) =>
            MelonLogger.Msg($"[RR][PreviewGen] {msg}");
    }
}
