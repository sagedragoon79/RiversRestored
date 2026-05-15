using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RiversRestored.Patches
{
    /// <summary>
    /// In-game preview overlay rendered as a UGUI Canvas, themed to match
    /// FF's New Game dialog aesthetic. Hierarchy:
    ///
    ///   RR_PreviewCanvas              (Canvas + CanvasScaler + GraphicRaycaster)
    ///   └── Shadow                    (Image, IMG_BGShadowThickSoft01)
    ///       └── Border                (Image, IMG_BorderSimpleThickDark01B)
    ///           └── Backdrop          (Image, solid dark fill)
    ///               ├── PreviewImage  (RawImage, LatestPreview texture)
    ///               └── Caption       (TextMeshProUGUI, LatestCaption text)
    ///
    /// Sprites and font are looked up by name from FF's loaded assets via
    /// Resources.FindObjectsOfTypeAll. If any are missing, falls back to
    /// plain Unity defaults so the panel still functions (just looks plainer).
    ///
    /// Toggle via <see cref="RiversRestoredMod.ShowPreviewOverlay"/> pref or
    /// F8 hotkey. Texture handed off from <see cref="MapPreviewRenderer"/> via
    /// <see cref="LatestPreview"/>; caption via <see cref="LatestCaption"/>.
    /// </summary>
    public class PreviewOverlay : MonoBehaviour
    {
        public static Texture2D? LatestPreview;
        // Caption split across 3 TMP components to allow:
        //   - Left column: 2-line left-justified (seed/biome/size + river/water)
        //   - Mid-right:   stacked (Resources / Wildlife)
        //   - Far-right:   stacked (Maladies / Raiders)
        // Each holds a 2-line string ('\n' between rows).
        public static string LatestCaption = "";          // legacy fallback
        public static string LatestCaptionLeft = "";
        public static string LatestCaptionMid = "";
        public static string LatestCaptionRight = "";

        // Layout — values in Canvas-space pixels at the reference resolution
        // (1920×1080). CanvasScaler will scale on different displays.
        private const int PANEL_W = 425;
        // Sized so the preview-image rect inside the panel (panel minus
        // caption strip and 4px margins on top/bottom) ends up square,
        // matching the square 768×768 render texture without horizontal
        // stretching. Math: (PANEL_W - 8) == (PANEL_H - CAPTION_H - 8).
        private const int CAPTION_H = 48;  // 2 lines @ ~22px each
        private const int PANEL_H = PANEL_W + CAPTION_H;
        private const int RIGHT_MARGIN = 8;

        // Sprite/font asset names from KC's UI dump (FF's loaded assets).
        private const string SPRITE_SHADOW = "IMG_BGShadowThickSoft01";
        // BTN_Border02_UP has slice border 23,18,23,18 — all four edges
        // defined, so the sprite ships with proper corner ornaments baked
        // into its 9-slice corner regions. Same sprite FF uses for the
        // main menu Continue/Load/Exit buttons. No separate corner-ornament
        // overlay needed.
        private const string SPRITE_BORDER = "BTN_Border02_UP";
        private const string FONT_NAME = "Andada-Bold";  // substring match — covers "Andada-Bold SDF" variants

        // Canvas hierarchy refs we hold so update() can toggle visibility
        // and rebind the texture.
        private Canvas? _canvas;
        private RectTransform? _shadowRT;
        private Image? _shadowImg;
        private Image? _borderImg;
        private RawImage? _previewImage;
        private TextMeshProUGUI? _captionText;       // legacy/empty-state
        private TextMeshProUGUI? _captionLeftText;   // Seed/Biome/Size + river/water
        private TextMeshProUGUI? _captionMidText;    // Resources / Wildlife
        private TextMeshProUGUI? _captionRightText;  // Maladies / Raiders

        private bool _initialized = false;
        // Sprites/font may not be loaded when Start() runs (FF lazy-loads
        // the New Game UI assets). Track which ones we still need so we
        // can retry on each Update tick until they all resolve.
        private bool _spritesResolved = false;
        private bool _fontResolved = false;

        // Cached GraphicRaycaster ref so Update() doesn't GetComponent
        // every frame. The MonoBehaviour adds it once in BuildHierarchy.
        private GraphicRaycaster? _graphicRaycaster;

        // Pangu-style progress bar — shown when preview gen is in
        // flight and captions haven't populated yet. Replaces the
        // panel's "empty state" with something more informative.
        private RectTransform? _progressBarFill;
        private TextMeshProUGUI? _progressBarLabel;
        private GameObject? _progressBarRoot;
        // Smoothed progress (0..1). Lerps toward the target each frame
        // so the bar doesn't stutter between stage reads.
        private float _smoothedProgress = 0f;
        // Indeterminate-mode hop position (0..1) for cases where we
        // can't read the gen stage. Bar segment slides L→R→L.
        private float _indeterminatePhase = 0f;
        // Stall detection — if the determinate progress hasn't moved
        // for this long, switch to indeterminate animation so the bar
        // keeps moving and the user doesn't think the panel is frozen.
        // The most common cause is a soft-restart waiting for the
        // previous gen's coroutines to be torn down.
        private float _lastProgressMoveAt = 0f;
        private float _lastProgressValue = -1f;
        private const float STALL_TO_INDETERMINATE_SECONDS = 1.5f;

        // Frame counter for throttling the lazy rebind. Resources.
        // FindObjectsOfTypeAll<Sprite>() returns a fresh array of every
        // loaded Sprite (potentially thousands) — calling it every frame
        // allocates enough garbage to trigger GC sweeps every ~3 seconds,
        // which the user sees as periodic visual stutter. Throttle to
        // once per ~30 frames.
        private int _rebindThrottle = 0;
        private const int REBIND_INTERVAL_FRAMES = 30;

        // Pangu-style auto-regen state. The overlay polls FF's New Game
        // UI per frame for changes to map size / terrain type / seed and
        // the Advanced Settings panel's open/closed state. When the
        // panel opens (with the pref enabled) or any of the three
        // settings change while the panel is open, debounce briefly,
        // then trigger a fresh preview via PreviewGenWorker.
        // TriggerPreviewSoftRestart so an in-flight gen gets cancelled
        // and the latest state wins.
        private CanvasGroup? _advancedPanelGroup;
        // Cached seed input field ref. Looked up once via
        // Resources.FindObjectsOfTypeAll, then read .text per frame
        // (cheap accessor) for change detection. The earlier
        // approach scanned every frame — that allocated enough
        // garbage to stall Unity 5+ seconds on a reroll. NB: we
        // can't read SettingsManager.mapTerrainSeedValue directly
        // because the UI's RerollMap (Assembly-CSharp.cs:289327)
        // updates only the string field + input field text, NOT the
        // static int — that gets written at StartNewGame time.
        private TMP_InputField? _seedInputField;
        private int _seedFieldLookupThrottle = 0;
        private const int SEED_FIELD_LOOKUP_INTERVAL_FRAMES = 30;

        private object? _lastMapSize;
        private string _lastSeedText = "";
        private bool _wasPanelOpenAndEnabled = false;
        private float _changeDetectedAt = -1f;
        private const float DEBOUNCE_SECONDS = 0.30f;
        // Throttle for the panel-CanvasGroup lookup (Resources.FindObjectsOfTypeAll
        // is expensive). Once found, we cache and stop searching.
        private int _panelLookupThrottle = 0;
        private const int PANEL_LOOKUP_INTERVAL_FRAMES = 30;

        private void Start()
        {
            try
            {
                BuildHierarchy();
                _initialized = true;
                MelonLogger.Msg("[RR][PreviewOverlay] UGUI panel built and ready.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RR][PreviewOverlay] Build failed: {ex}");
            }
        }

        private void Update()
        {
            // F8 toggle — kept from the OnGUI version for consistent UX.
            try
            {
                if (Input.GetKeyDown(KeyCode.F8) && RiversRestoredMod.ShowPreviewOverlay != null)
                {
                    RiversRestoredMod.ShowPreviewOverlay.Value = !RiversRestoredMod.ShowPreviewOverlay.Value;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RR][PreviewOverlay] hotkey error: {ex.Message}");
            }

            if (!_initialized || _shadowRT == null) return;

            // Pangu-pattern Map-scene unload watchdog. The preview gen runs
            // tgc.GenSliced_Generate(false) which mutates the live TGC; if
            // we don't unload the Map scene before gameplay starts, the
            // load screen hangs. This tick check fires once per frame and
            // unloads the scene the moment GameManager.terrainManager
            // becomes available (= user clicked Start, gameplay loading).
            PreviewGenWorker.TickUnloadWatchdog(this);

            // Resolve scene FIRST so we can short-circuit work when not on
            // the Start scene. Cheap — single accessor, no allocation.
            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
            bool onNewGameScreen = sceneName.Equals("Start", StringComparison.OrdinalIgnoreCase);

            // Lazy retry of FF sprite/font lookup. Throttled to once per
            // REBIND_INTERVAL_FRAMES frames AND gated to the Start scene
            // because FF's New Game assets only exist there. Without
            // throttling, Resources.FindObjectsOfTypeAll<Sprite>() runs
            // per-frame, allocating large arrays that trigger periodic
            // (~3s cadence) GC stutter during gameplay.
            if (onNewGameScreen)
            {
                _rebindThrottle++;
                if (_rebindThrottle >= REBIND_INTERVAL_FRAMES)
                {
                    _rebindThrottle = 0;
                    if (!_spritesResolved) TryRebindSprites();
                    if (!_fontResolved) TryRebindFont();
                }
            }

            // Pangu-style visibility: only show when on Start scene AND
            // the EnableMapPreviewRender pref is on AND the user has
            // opened FF's Advanced Settings panel. The user can configure
            // size/terrain/seed without the overlay showing; the preview
            // appears the moment they expand Advanced Settings.
            bool prefOn = RiversRestoredMod.ShowPreviewOverlay?.Value ?? false;
            bool advancedOpen = onNewGameScreen && IsAdvancedSettingsPanelOpen();
            bool wantVisible = onNewGameScreen && prefOn && advancedOpen;

            // Auto-regen: detect changes to (map size, map type, seed)
            // while the panel is open, and trigger a fresh preview after
            // a short debounce. The first show (panel just opened with
            // pref enabled) also triggers an initial gen.
            //
            // Pass both signals separately so HandleAutoRegen can
            // distinguish "user left the new-game screen entirely"
            // (reset state) from "panel briefly invisible due to layout
            // flicker / CanvasGroup ref re-resolution" (don't reset).
            HandleAutoRegen(wantVisible, onNewGameScreen);
            if (_shadowRT.gameObject.activeSelf != wantVisible)
                _shadowRT.gameObject.SetActive(wantVisible);

            // Disable the Canvas + GraphicRaycaster on the root when not on
            // the Start scene. Hiding the children alone leaves the
            // raycaster active, which (at sortingOrder 1000) was beating
            // FF's UI to pointer events and swallowing clicks on the Town
            // Center confirm dialog. Disabling Canvas pulls us out of UI
            // event routing entirely. Cached graphicRaycaster lookup at
            // BuildHierarchy time so we don't GetComponent every frame.
            if (_canvas != null && _canvas.enabled != onNewGameScreen)
                _canvas.enabled = onNewGameScreen;
            if (_graphicRaycaster != null && _graphicRaycaster.enabled != onNewGameScreen)
                _graphicRaycaster.enabled = onNewGameScreen;

            if (!wantVisible) return;

            // Hold the preview content (image + captions) until the
            // gen has produced both a render AND populated captions.
            // Avoids the half-baked "empty caption + image" intermediate
            // state. Until ready, we show the progress bar instead.
            bool captionReady = PreviewGenWorker.CaptionReady;
            bool showContent = captionReady && LatestPreview != null;

            // Rebind texture if it changed (renderer hands us a new
            // Texture2D after each gen). Hide the image entirely until
            // showContent is true, so we don't flash a half-rendered
            // texture before its caption appears.
            if (_previewImage != null)
            {
                if (_previewImage.texture != LatestPreview)
                    _previewImage.texture = LatestPreview;
                if (showContent)
                {
                    _previewImage.color = Color.white;
                }
                else
                {
                    // Fully transparent so the progress bar is the
                    // visible element on the dark backdrop.
                    _previewImage.color = new Color(0f, 0f, 0f, 0f);
                }
            }

            // Drive the progress bar.
            UpdateProgressBar(showContent);

            // Caption refresh: split-column display when preview exists,
            // single centered hint when empty. Toggle the COMPONENT's
            // .enabled (not the GameObject's .activeSelf) — the legacy
            // TMP and the 3 split TMPs are all in the same GameObject
            // tree, so deactivating the parent would hide the children
            // too.
            // Caption visibility now follows showContent (= captionReady
            // AND LatestPreview != null). The empty-state legacy TMP
            // is no longer used for "Generating preview…" — the
            // progress bar handles that. Disable all caption TMPs
            // when content isn't ready.
            if (_captionText != null)
                _captionText.enabled = false;
            if (_captionLeftText != null)
                _captionLeftText.enabled = showContent;
            if (_captionMidText != null)
                _captionMidText.enabled = showContent;
            if (_captionRightText != null)
                _captionRightText.enabled = showContent;

            if (!showContent)
            {
                // No-op — progress bar is the visible element.
            }
            else
            {
                if (_captionLeftText != null && _captionLeftText.text != LatestCaptionLeft)
                    _captionLeftText.text = LatestCaptionLeft;
                if (_captionMidText != null && _captionMidText.text != LatestCaptionMid)
                    _captionMidText.text = LatestCaptionMid;
                if (_captionRightText != null && _captionRightText.text != LatestCaptionRight)
                    _captionRightText.text = LatestCaptionRight;
            }
        }

        /// <summary>Builds the Canvas → Shadow → Border → Backdrop → (image+caption)
        /// hierarchy with FF-themed sprites and font where available.</summary>
        private void BuildHierarchy()
        {
            // ── Canvas root (this MonoBehaviour's GameObject) ────────────
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 1000;  // above FF's dialogs (which are 0-1)

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;  // balance width and height

            _graphicRaycaster = gameObject.AddComponent<GraphicRaycaster>();  // not strictly needed
                                                          // but harmless

            // Lookup FF's sprite assets.
            Sprite? shadowSprite = FindSpriteByName(SPRITE_SHADOW);
            Sprite? borderSprite = FindSpriteByName(SPRITE_BORDER);
            TMP_FontAsset? font = FindFontByName(FONT_NAME);

            // ── Shadow layer (outermost, slightly larger than the border) ─
            var shadowGO = new GameObject("Shadow");
            shadowGO.transform.SetParent(transform, false);
            _shadowRT = shadowGO.AddComponent<RectTransform>();
            // Anchor to right-mid of canvas. anchoredPosition is offset
            // from that anchor toward the panel's pivot.
            _shadowRT.anchorMin = new Vector2(1f, 0.5f);
            _shadowRT.anchorMax = new Vector2(1f, 0.5f);
            _shadowRT.pivot = new Vector2(1f, 0.5f);
            _shadowRT.anchoredPosition = new Vector2(-RIGHT_MARGIN, 0f);
            _shadowRT.sizeDelta = new Vector2(PANEL_W + 24, PANEL_H + CAPTION_H + 24);

            _shadowImg = shadowGO.AddComponent<Image>();
            if (shadowSprite != null)
            {
                _shadowImg.sprite = shadowSprite;
                _shadowImg.type = Image.Type.Sliced;
                _shadowImg.color = new Color(0f, 0f, 0f, 0.85f);
            }
            else
            {
                // Fallback — flagged for lazy retry in Update.
                _shadowImg.color = new Color(0f, 0f, 0f, 0.7f);
            }

            // ── Border (FF's dark thick frame) ───────────────────────────
            var borderGO = new GameObject("Border");
            borderGO.transform.SetParent(_shadowRT, false);
            var borderRT = borderGO.AddComponent<RectTransform>();
            borderRT.anchorMin = Vector2.zero;
            borderRT.anchorMax = Vector2.one;
            borderRT.pivot = new Vector2(0.5f, 0.5f);
            borderRT.offsetMin = new Vector2(8f, 8f);   // inset 8px from shadow
            borderRT.offsetMax = new Vector2(-8f, -8f);

            _borderImg = borderGO.AddComponent<Image>();
            if (borderSprite != null)
            {
                _borderImg.sprite = borderSprite;
                _borderImg.type = Image.Type.Sliced;
                _borderImg.color = Color.white;
            }
            else
            {
                _borderImg.color = new Color(0.55f, 0.55f, 0.6f, 1f);
            }

            // No separate corner-ornament overlays needed — BTN_Border02_UP
            // has its corners baked into the sliced sprite (23x18 corner
            // regions). The MakeCornerOrnament helper is left in place
            // below for potential future use, but no longer called.

            // Mark resolved status for lazy retry in Update.
            _spritesResolved = (shadowSprite != null && borderSprite != null);
            _fontResolved = (font != null);

            // ── Backdrop fill (inside the border, dark + slightly transparent) ─
            var backdropGO = new GameObject("Backdrop");
            backdropGO.transform.SetParent(borderRT, false);
            var backdropRT = backdropGO.AddComponent<RectTransform>();
            backdropRT.anchorMin = Vector2.zero;
            backdropRT.anchorMax = Vector2.one;
            backdropRT.offsetMin = new Vector2(6f, 6f);   // inset slightly so border shows
            backdropRT.offsetMax = new Vector2(-6f, -6f);

            var backdropImg = backdropGO.AddComponent<Image>();
            backdropImg.color = new Color(0.08f, 0.08f, 0.10f, 0.92f);

            // ── Preview image (RawImage so it can take the rendered Texture2D) ─
            var previewGO = new GameObject("PreviewImage");
            previewGO.transform.SetParent(backdropRT, false);
            var previewRT = previewGO.AddComponent<RectTransform>();
            // Top portion of backdrop, leaving CAPTION_H at the bottom.
            previewRT.anchorMin = new Vector2(0f, 0f);
            previewRT.anchorMax = new Vector2(1f, 1f);
            previewRT.offsetMin = new Vector2(4f, CAPTION_H);
            previewRT.offsetMax = new Vector2(-4f, -4f);

            _previewImage = previewGO.AddComponent<RawImage>();
            _previewImage.color = Color.white;

            // ── Caption (TMP text bottom strip) ───────────────────────────
            var captionGO = new GameObject("Caption");
            captionGO.transform.SetParent(backdropRT, false);
            var captionRT = captionGO.AddComponent<RectTransform>();
            captionRT.anchorMin = new Vector2(0f, 0f);
            captionRT.anchorMax = new Vector2(1f, 0f);
            captionRT.pivot = new Vector2(0.5f, 0f);
            captionRT.anchoredPosition = new Vector2(0f, 4f);
            captionRT.sizeDelta = new Vector2(0f, CAPTION_H - 4f);

            // Legacy/empty-state TMP — kept centered; only shown when no
            // preview has been rendered yet (Update logic below).
            _captionText = captionGO.AddComponent<TextMeshProUGUI>();
            if (font != null) _captionText.font = font;
            _captionText.alignment = TextAlignmentOptions.Center;
            _captionText.fontSize = 13;
            _captionText.color = new Color(0.9f, 0.9f, 0.85f, 1f);
            _captionText.text = "Rivers Restored — preview";
            _captionText.enableWordWrapping = false;
            _captionText.overflowMode = TextOverflowModes.Truncate;
            _captionText.lineSpacing = -2;
            _captionText.raycastTarget = false;

            // 3 split caption columns — left full-width, mid-right and
            // far-right stacked. Each holds a 2-line string ('\n' between).
            // Width split: left ~50%, mid 25%, right 25% — adjust if labels
            // get truncated.
            // Width split: 60% / 20% / 20%. Left column is the longest
            // ("Seed XXXXXXXXX · IdyllicValley · Medium") so it gets the
            // most room. Mid (Resources/Wildlife) and far-right
            // (Maladies/Raiders) only need ~12 chars at fontSize 12.
            _captionLeftText = MakeCaptionColumn(captionRT, "CaptionLeft",
                font, anchorMin: new Vector2(0f, 0f), anchorMax: new Vector2(0.60f, 1f),
                align: TextAlignmentOptions.Left, sizePadding: 4);
            _captionMidText = MakeCaptionColumn(captionRT, "CaptionMid",
                font, anchorMin: new Vector2(0.60f, 0f), anchorMax: new Vector2(0.80f, 1f),
                align: TextAlignmentOptions.Left, sizePadding: 0);
            _captionRightText = MakeCaptionColumn(captionRT, "CaptionRight",
                font, anchorMin: new Vector2(0.80f, 0f), anchorMax: new Vector2(1f, 1f),
                align: TextAlignmentOptions.Left, sizePadding: 0);

            // No PREVIEW button — Pangu-style auto-regen drives the
            // preview instead (see HandleAutoRegen). The preview appears
            // automatically when the user opens the Advanced Settings
            // panel with EnableMapPreviewRender on, and refreshes
            // whenever map size / terrain type / seed changes.

            // Pangu-style progress bar — sits inside the preview-image
            // rect (centered), shown only when caption isn't ready
            // yet. Hides as soon as CaptionReady flips true.
            BuildProgressBar(previewRT, font);

            // Start hidden — Update() flips it on when conditions met.
            _shadowRT.gameObject.SetActive(false);
        }

        /// <summary>Pangu-style progress bar shown over the preview
        /// image area while a preview gen is in flight. Two-tone
        /// (background track + fill bar) with a centered label that
        /// reads either the percent (when stage is readable) or
        /// "Generating preview…" (indeterminate). Hides when
        /// PreviewGenWorker.CaptionReady flips true.</summary>
        private void BuildProgressBar(RectTransform parent, TMP_FontAsset? font)
        {
            _progressBarRoot = new GameObject("ProgressBar");
            _progressBarRoot.transform.SetParent(parent, false);
            var rt = _progressBarRoot.AddComponent<RectTransform>();
            // Centered, ~60% wide, 18px tall.
            rt.anchorMin = new Vector2(0.2f, 0.5f);
            rt.anchorMax = new Vector2(0.8f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, 18f);

            // Background track.
            var trackImg = _progressBarRoot.AddComponent<Image>();
            trackImg.color = new Color(0.05f, 0.05f, 0.07f, 0.85f);
            trackImg.raycastTarget = false;

            // Fill — child rect that we resize per frame in Update.
            var fillGO = new GameObject("Fill");
            fillGO.transform.SetParent(rt, false);
            _progressBarFill = fillGO.AddComponent<RectTransform>();
            _progressBarFill.anchorMin = new Vector2(0f, 0f);
            _progressBarFill.anchorMax = new Vector2(0f, 1f);  // width=0 initially
            _progressBarFill.pivot = new Vector2(0f, 0.5f);
            _progressBarFill.offsetMin = new Vector2(2f, 2f);
            _progressBarFill.offsetMax = new Vector2(2f, -2f);
            var fillImg = fillGO.AddComponent<Image>();
            // Warm amber — readable on the dark track and similar to
            // FF's resource-bar accent color.
            fillImg.color = new Color(0.95f, 0.78f, 0.32f, 1f);
            fillImg.raycastTarget = false;

            // Centered label.
            var lblGO = new GameObject("Label");
            lblGO.transform.SetParent(rt, false);
            var lblRT = lblGO.AddComponent<RectTransform>();
            lblRT.anchorMin = Vector2.zero;
            lblRT.anchorMax = Vector2.one;
            lblRT.offsetMin = Vector2.zero;
            lblRT.offsetMax = Vector2.zero;
            _progressBarLabel = lblGO.AddComponent<TextMeshProUGUI>();
            if (font != null) _progressBarLabel.font = font;
            _progressBarLabel.alignment = TextAlignmentOptions.Center;
            _progressBarLabel.fontSize = 12;
            _progressBarLabel.color = new Color(0.95f, 0.92f, 0.82f, 1f);
            _progressBarLabel.text = "Generating preview…";
            _progressBarLabel.raycastTarget = false;
            _progressBarLabel.enableWordWrapping = false;
            _progressBarLabel.overflowMode = TextOverflowModes.Truncate;

            _progressBarRoot.SetActive(false);
        }

        /// <summary>Drive the progress bar: show when content not ready,
        /// hide when it is. Reads PreviewGenWorker.TryGetGenProgress for
        /// the determinate value; falls back to a sliding-segment
        /// indeterminate animation when the gen stage isn't readable.</summary>
        private void UpdateProgressBar(bool showContent)
        {
            if (_progressBarRoot == null) return;
            bool wantBar = !showContent;
            if (_progressBarRoot.activeSelf != wantBar)
                _progressBarRoot.SetActive(wantBar);
            if (!wantBar)
            {
                _smoothedProgress = 0f;  // reset for next gen
                return;
            }

            float p = PreviewGenWorker.TryGetGenProgress();

            // Stall detection: if the determinate value hasn't advanced
            // for STALL_TO_INDETERMINATE_SECONDS, fall through to the
            // indeterminate animation. Most common case: soft-restart
            // tearing down the previous gen's coroutines, where stage
            // freezes at whatever value it had when StopAllCoroutines
            // fired. The user sees a moving bar instead of a frozen
            // determinate bar at e.g. 62%.
            bool stalled = false;
            if (p >= 0f)
            {
                if (Mathf.Abs(p - _lastProgressValue) > 0.001f)
                {
                    _lastProgressValue = p;
                    _lastProgressMoveAt = Time.unscaledTime;
                }
                else if (Time.unscaledTime - _lastProgressMoveAt > STALL_TO_INDETERMINATE_SECONDS)
                {
                    stalled = true;
                }
            }
            else
            {
                _lastProgressValue = -1f;
            }

            if (p >= 0f && !stalled)
            {
                // Determinate: smooth toward target so the bar doesn't
                // pop between stage reads. Lerp factor ~6/sec means
                // ~6% per second per 0.1 of delta — feels responsive
                // but not jittery.
                _smoothedProgress = Mathf.Lerp(_smoothedProgress, p, Time.unscaledDeltaTime * 6f);
                if (_progressBarFill != null)
                {
                    // Reset min in case we were in indeterminate mode.
                    var min = _progressBarFill.anchorMin;
                    min.x = 0f;
                    _progressBarFill.anchorMin = min;
                    var max = _progressBarFill.anchorMax;
                    max.x = Mathf.Clamp01(_smoothedProgress);
                    _progressBarFill.anchorMax = max;
                }
                if (_progressBarLabel != null)
                {
                    int pct = Mathf.RoundToInt(_smoothedProgress * 100f);
                    _progressBarLabel.text = $"Generating preview… {pct}%";
                }
            }
            else
            {
                // Indeterminate: a 25%-wide segment slides L→R→L
                // across the track. Phase advances at ~0.6/sec so a
                // full sweep takes ~3.3 s.
                _indeterminatePhase += Time.unscaledDeltaTime * 0.6f;
                if (_indeterminatePhase > 1f) _indeterminatePhase -= 2f;  // wrap to -1..1
                float ping = Mathf.Abs(_indeterminatePhase);  // 0..1..0 triangular
                if (_progressBarFill != null)
                {
                    const float segWidth = 0.25f;
                    float min = ping * (1f - segWidth);
                    float max = min + segWidth;
                    var aMin = _progressBarFill.anchorMin;
                    var aMax = _progressBarFill.anchorMax;
                    aMin.x = min;
                    aMax.x = max;
                    _progressBarFill.anchorMin = aMin;
                    _progressBarFill.anchorMax = aMax;
                }
                if (_progressBarLabel != null && _progressBarLabel.text != "Generating preview…")
                    _progressBarLabel.text = "Generating preview…";
            }
        }

        /// <summary>True when FF's Advanced Settings panel is currently
        /// open. The panel sits at
        ///   Canvas/Main Panel/StartMenu_DifficultySelect/.../Main Panel/Advanced Settings Panel
        /// and has a CanvasGroup whose alpha lerps to 1 + interactable=true
        /// when expanded (FF's UIStartMenu_DifficultySelect.OnAdvancedSettings
        /// at Assembly-CSharp.cs:288663). We cache the CanvasGroup ref
        /// and just read alpha + interactable per frame.</summary>
        private bool IsAdvancedSettingsPanelOpen()
        {
            // Gate the lookup to the Start scene — the panel only
            // exists there. Without this, clicking Start before the
            // panel binds would leave us doing
            // Resources.FindObjectsOfTypeAll<CanvasGroup>() every 30
            // frames forever in gameplay, allocating large arrays
            // and triggering periodic GC stutter.
            if (_advancedPanelGroup == null)
            {
                string sn = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
                if (!sn.Equals("Start", StringComparison.OrdinalIgnoreCase)) return false;
                _panelLookupThrottle++;
                if (_panelLookupThrottle < PANEL_LOOKUP_INTERVAL_FRAMES) return false;
                _panelLookupThrottle = 0;
                _advancedPanelGroup = FindAdvancedPanelGroup();
                if (_advancedPanelGroup == null) return false;
            }
            try
            {
                return _advancedPanelGroup.alpha > 0.5f && _advancedPanelGroup.interactable;
            }
            catch
            {
                _advancedPanelGroup = null;
                return false;
            }
        }

        /// <summary>One-time scene-walk lookup for the Advanced Settings
        /// panel's CanvasGroup. Match by GameObject name "Advanced
        /// Settings Panel" with a parent path that includes
        /// StartMenu_DifficultySelect (so we don't grab some other
        /// "Advanced Settings Panel" elsewhere in FF's UI).</summary>
        private static CanvasGroup? FindAdvancedPanelGroup()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<CanvasGroup>();
                foreach (var cg in all)
                {
                    if (cg == null || cg.gameObject == null) continue;
                    if (cg.gameObject.name != "Advanced Settings Panel") continue;
                    // Confirm it's under the New Game UI (StartMenu_DifficultySelect)
                    // by walking up the parent chain.
                    Transform? t = cg.transform.parent;
                    bool underStartMenu = false;
                    int hops = 0;
                    while (t != null && hops < 10)
                    {
                        if (t.name == "StartMenu_DifficultySelect") { underStartMenu = true; break; }
                        t = t.parent;
                        hops++;
                    }
                    if (underStartMenu) return cg;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Pangu-style auto-regen. Tracks (mapSize, mapType,
        /// seed) and the panel's open state. Triggers a fresh preview
        /// when:
        ///   • The panel transitions closed→open with the pref enabled
        ///     (initial preview)
        ///   • Any of the three settings changes while the panel is open
        /// Each detected change resets a debounce timer (300 ms); once
        /// the timer expires, fires PreviewGenWorker.TriggerPreviewSoftRestart
        /// which cancel-and-restarts any in-flight gen so the latest
        /// state always wins.</summary>
        private void HandleAutoRegen(bool wantVisible, bool onNewGameScreen)
        {
            try
            {
                // If we left the Start (new-game) scene, the panel session
                // is over: reset state so the next time the user comes back
                // to the new-game screen, the First-show path correctly
                // kicks an initial gen for that fresh session.
                if (!onNewGameScreen)
                {
                    if (_wasPanelOpenAndEnabled)
                    {
                        MelonLogger.Msg("[RR][PreviewOverlay] AutoRegen: left Start scene — resetting panel-session state.");
                    }
                    _wasPanelOpenAndEnabled = false;
                    _changeDetectedAt = -1f;
                    return;
                }

                // We're on the Start scene but the panel/pref isn't currently
                // showing the overlay (CanvasGroup.alpha dipped from a layout
                // reflow when our preview image lands, or
                // IsAdvancedSettingsPanelOpen's cached CanvasGroup ref is
                // being re-resolved — up to PANEL_LOOKUP_INTERVAL_FRAMES of
                // false readings during re-resolution). Just suspend trigger
                // evaluation; do NOT tear down state. Resetting on those
                // false readings was the cause of the "three previews per
                // change" bug: brief invisibility → state reset → next frame
                // re-enters First-show → silent debounce → soft-restart fires
                // while gen 1 was still doing post-render bookkeeping → gen 2
                // runs on a dirty TerrainGenerator (state leakage produces
                // different output for the same seed) → HardCancel → gen 3
                // on a fresh scene matches gen 1.
                if (!wantVisible) return;

                // Read current settings:
                //   mapSizeValue — static, cheap read.
                //   seed text    — cached input field, .text accessor
                //                  per frame (cheap; the expensive part
                //                  was the FindObjectsOfTypeAll scan,
                //                  which is now once-and-cached).
                //
                // The seed input field text covers all UI sources:
                //   • User typing → input field text changes
                //   • Randomize Button → UI's RerollMap writes new
                //     SettingsToSeed string, updates input field
                //   • Non-Custom map type click → triggers UI's
                //     RerollMap (same path)
                object? curSize = ReadStaticProp("SettingsManager", "mapSizeValue", "_mapSizeValue");
                // Skip seed change detection while the user is actively
                // typing in the input field — otherwise every keystroke
                // re-fires a gen. Reroll button writes via
                // SetTextWithoutNotify() which doesn't focus the field,
                // so reroll detection works even with this gate. The
                // typing case is caught when the field unfocuses (Enter
                // or click elsewhere).
                bool seedFieldIsTyping = _seedInputField != null && _seedInputField.isFocused;
                string curSeedText = seedFieldIsTyping ? _lastSeedText : ReadCachedSeedFieldText();

                // First show (panel just opened) — kick an initial gen.
                // Also flip CaptionReady=false here so the panel shows
                // the progress bar immediately instead of flashing the
                // previous gen's image during the 300ms debounce window.
                //
                // BUT: defer first-show until the seed input field has
                // actually populated. The panel becomes visible a few frames
                // before FF finishes writing the initial seed string into
                // the input. If first-show fires with seed='', it kicks gen
                // #1 against an empty seed; the input then populates and
                // change-detect fires gen #2 against the real seed. Two
                // gens per panel-open. The user sees gen #2's preview, but
                // gameplay matches gen #1's RNG state — preview-vs-gameplay
                // divergence on the same seed, even though both gens
                // *technically* ran with the same final seed value. By
                // waiting for the seed to exist before first-show, we
                // collapse to a single trigger and the seed gen state at
                // Stage 38 matches what gameplay will reproduce.
                if (!_wasPanelOpenAndEnabled)
                {
                    if (string.IsNullOrEmpty(curSeedText))
                    {
                        // Seed not ready yet — wait. _wasPanelOpenAndEnabled
                        // stays false so we re-enter this branch next frame.
                        return;
                    }
                    MelonLogger.Msg(
                        $"[RR][PreviewOverlay] AutoRegen first-show: size='{curSize}' seed='{curSeedText}' — debounce scheduled.");
                    _wasPanelOpenAndEnabled = true;
                    _lastMapSize = curSize;
                    _lastSeedText = curSeedText;
                    _changeDetectedAt = Time.realtimeSinceStartup;
                    PreviewGenWorker.ResetCaptionReady();
                    return;
                }

                // Subsequent changes — reset debounce on each delta.
                bool sizeChanged = !object.Equals(curSize, _lastMapSize);
                bool seedChanged = curSeedText != _lastSeedText;
                if (sizeChanged || seedChanged)
                {
                    MelonLogger.Msg(
                        $"[RR][PreviewOverlay] AutoRegen change detected: " +
                        $"size {(sizeChanged ? $"'{_lastMapSize}' → '{curSize}'" : "(same)")}, " +
                        $"seed {(seedChanged ? $"'{_lastSeedText}' → '{curSeedText}'" : "(same)")}");
                    _lastMapSize = curSize;
                    _lastSeedText = curSeedText;
                    _changeDetectedAt = Time.realtimeSinceStartup;
                    return;
                }

                if (_changeDetectedAt > 0f
                    && (Time.realtimeSinceStartup - _changeDetectedAt) >= DEBOUNCE_SECONDS)
                {
                    MelonLogger.Msg(
                        $"[RR][PreviewOverlay] AutoRegen debounce fired — calling TriggerPreviewSoftRestart.");
                    _changeDetectedAt = -1f;
                    PreviewGenWorker.TriggerPreviewSoftRestart();
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RR][PreviewOverlay] HandleAutoRegen error: {ex.Message}");
            }
        }

        /// <summary>Read a static property (or its underscored backing
        /// field) on a type discovered via AccessTools.TypeByName.
        /// Returns null on any failure — auto-regen tolerates nulls.</summary>
        private static object? ReadStaticProp(string typeName, string propName, string backingFieldName)
        {
            try
            {
                var t = AccessTools.TypeByName(typeName);
                if (t == null) return null;
                var p = t.GetProperty(propName, BindingFlags.Public | BindingFlags.Static);
                if (p != null)
                {
                    try { var v = p.GetValue(null); if (v != null) return v; } catch { }
                }
                var f = t.GetField(backingFieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f != null)
                {
                    try { return f.GetValue(null); } catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Read an instance property on a singleton type:
        /// looks up TypeByName(typeName), reads its static instanceProp,
        /// then reads instance.propName (or backing field).</summary>
        private static object? ReadInstanceProp(string typeName, string instanceProp, string propName, string backingFieldName)
        {
            try
            {
                var t = AccessTools.TypeByName(typeName);
                if (t == null) return null;
                var instProp = t.GetProperty(instanceProp, BindingFlags.Public | BindingFlags.Static);
                var inst = instProp?.GetValue(null);
                if (inst == null) return null;
                var p = t.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (p != null)
                {
                    try { var v = p.GetValue(inst); if (v != null) return v; } catch { }
                }
                var f = t.GetField(backingFieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null)
                {
                    try { return f.GetValue(inst); } catch { }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Read the current seed text from FF's "Map Seed
        /// InputField," using a cached ref to avoid per-frame
        /// Resources.FindObjectsOfTypeAll allocation. The lookup is
        /// throttled to once per 30 frames; if the cached ref goes
        /// stale (destroyed), next lookup re-scans. This keeps the
        /// hot path to a single property accessor per frame.</summary>
        private string ReadCachedSeedFieldText()
        {
            // Cache hit — fast path.
            if (_seedInputField != null)
            {
                try
                {
                    // Touch a trivial property to check if the underlying
                    // GameObject was destroyed. If it has been, Unity's
                    // null-overload returns true on the wrapper.
                    if (_seedInputField.gameObject == null)
                    {
                        _seedInputField = null;
                    }
                    else
                    {
                        return _seedInputField.text ?? "";
                    }
                }
                catch
                {
                    _seedInputField = null;
                }
            }

            // Throttled scan path. Only fires when cache is cold.
            _seedFieldLookupThrottle++;
            if (_seedFieldLookupThrottle < SEED_FIELD_LOOKUP_INTERVAL_FRAMES) return "";
            _seedFieldLookupThrottle = 0;

            try
            {
                var all = Resources.FindObjectsOfTypeAll<TMP_InputField>();
                foreach (var f in all)
                {
                    if (f == null || f.gameObject == null) continue;
                    var n = f.gameObject.name;
                    if (string.IsNullOrEmpty(n)) continue;
                    if (n.IndexOf("seed", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    // Confirm it's the New Game seed field by walking
                    // the parent chain — defensive against other
                    // "seed"-named inputs that might exist elsewhere.
                    Transform? t = f.transform;
                    int hops = 0;
                    while (t != null && hops < 12)
                    {
                        if (t.name == "StartMenu_DifficultySelect") { _seedInputField = f; break; }
                        t = t.parent;
                        hops++;
                    }
                    if (_seedInputField != null) break;
                }
            }
            catch { }

            return _seedInputField?.text ?? "";
        }

        /// <summary>Create one of the three caption-column TMP components.
        /// Each column holds a 2-line string and is anchored to a slice
        /// of the caption strip's width.</summary>
        private TextMeshProUGUI MakeCaptionColumn(RectTransform parent, string name,
            TMP_FontAsset? font, Vector2 anchorMin, Vector2 anchorMax,
            TextAlignmentOptions align, int sizePadding)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = new Vector2(sizePadding, 0f);
            rt.offsetMax = new Vector2(-sizePadding, 0f);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            // Combine horizontal alignment with TOP vertical so the first
            // line sits at the top of the strip and the second line follows
            // — without this the default Middle alignment can clip the
            // second line when the strip is just-barely-tall-enough.
            TextAlignmentOptions vAlign = align switch
            {
                TextAlignmentOptions.Left => TextAlignmentOptions.TopLeft,
                TextAlignmentOptions.Right => TextAlignmentOptions.TopRight,
                TextAlignmentOptions.Center => TextAlignmentOptions.Top,
                _ => align,
            };
            tmp.alignment = vAlign;
            tmp.fontSize = 12;
            tmp.color = new Color(0.9f, 0.9f, 0.85f, 1f);
            tmp.text = "";
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;  // let 2-line text spill; we sized strip for it
            tmp.lineSpacing = -2;
            tmp.raycastTarget = false;
            return tmp;
        }

        /// <summary>Create a single corner ornament Image at a specified
        /// anchor of the parent. Anchor is a unit-square corner (0,0=BL,
        /// 1,1=TR, etc.). Pivot is set to match the anchor so the rect
        /// extends INWARD from that corner. Offset is added on top.
        /// xFlip/yFlip apply localScale=-1 on the respective axes.
        /// Sets raycastTarget=false and SetAsLastSibling so the ornament
        /// renders on top of the panel content.</summary>
        private static Image MakeCornerOrnament(RectTransform parent, string name,
            Sprite? sprite, int w, int h,
            Vector2 anchor, Vector2 offset, bool xFlip, bool yFlip)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = offset;
            rt.sizeDelta = new Vector2(w, h);
            rt.localScale = new Vector3(xFlip ? -1f : 1f, yFlip ? -1f : 1f, 1f);

            var img = go.AddComponent<Image>();
            if (sprite != null) { img.sprite = sprite; img.type = Image.Type.Sliced; }
            img.color = Color.white;
            img.raycastTarget = false;
            go.transform.SetAsLastSibling();
            return img;
        }

        /// <summary>Re-attempt sprite lookup if Start() couldn't find them.
        /// Called once per frame from Update until both shadow and border
        /// sprites are bound. Cheap — Resources.FindObjectsOfTypeAll is
        /// O(loaded objects) and we stop after success.</summary>
        private void TryRebindSprites()
        {
            if (_shadowImg == null || _borderImg == null) return;

            bool changed = false;
            if (_shadowImg.sprite == null)
            {
                var s = FindSpriteByName(SPRITE_SHADOW);
                if (s != null)
                {
                    _shadowImg.sprite = s;
                    _shadowImg.type = Image.Type.Sliced;
                    _shadowImg.color = new Color(0f, 0f, 0f, 0.85f);
                    changed = true;
                }
            }
            if (_borderImg.sprite == null)
            {
                var s = FindSpriteByName(SPRITE_BORDER);
                if (s != null)
                {
                    _borderImg.sprite = s;
                    _borderImg.type = Image.Type.Sliced;
                    _borderImg.color = Color.white;
                    changed = true;
                }
            }
            if (changed)
                MelonLogger.Msg("[RR][PreviewOverlay] Late-bound FF sprites — panel theme applied.");
            _spritesResolved = (_shadowImg.sprite != null && _borderImg.sprite != null);
        }

        /// <summary>Re-attempt font lookup if Start() couldn't find it.</summary>
        private void TryRebindFont()
        {
            if (_captionText == null) return;
            if (_captionText.font == null
                || _captionText.font.name.IndexOf(FONT_NAME, StringComparison.OrdinalIgnoreCase) < 0)
            {
                var f = FindFontByName(FONT_NAME);
                if (f != null)
                {
                    _captionText.font = f;
                    MelonLogger.Msg("[RR][PreviewOverlay] Late-bound FF font.");
                }
            }
            if (_captionText.font != null
                && _captionText.font.name.IndexOf(FONT_NAME, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _fontResolved = true;
            }
        }

        /// <summary>Find a Sprite by name (case-insensitive substring match)
        /// from all loaded sprite assets. Returns null if not found.</summary>
        private static Sprite? FindSpriteByName(string substring)
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<Sprite>();
                foreach (var s in all)
                {
                    if (s == null || string.IsNullOrEmpty(s.name)) continue;
                    if (s.name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
                        return s;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Find a TMP_FontAsset by name (substring match).</summary>
        private static TMP_FontAsset? FindFontByName(string substring)
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                foreach (var f in all)
                {
                    if (f == null || string.IsNullOrEmpty(f.name)) continue;
                    if (f.name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
                        return f;
                }
            }
            catch { }
            return null;
        }
    }
}
