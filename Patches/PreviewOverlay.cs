using System;
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
        private const int PANEL_H = 425;
        private const int CAPTION_H = 48;  // 2 lines @ ~22px each
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

            // Lazy retry: if Start() couldn't find FF's sprites/font (because
            // the New Game UI hadn't loaded yet), try again every frame
            // until they resolve. Cheap — Resources.FindObjectsOfTypeAll
            // is a single scan and we stop calling once both flags flip.
            if (!_spritesResolved) TryRebindSprites();
            if (!_fontResolved) TryRebindFont();

            // Visibility — show whenever the pref is on. If no preview
            // texture exists yet, we show a placeholder + the PREVIEW
            // button so the user has a way to trigger one. (Previously
            // we required LatestPreview != null too, which caused a
            // chicken-and-egg problem when Pangu wasn't running: no
            // texture → panel hidden → button unreachable → no way to
            // generate a texture.)
            bool wantVisible = RiversRestoredMod.ShowPreviewOverlay?.Value ?? false;
            if (_shadowRT.gameObject.activeSelf != wantVisible)
                _shadowRT.gameObject.SetActive(wantVisible);

            if (!wantVisible) return;

            // Rebind texture if it changed (renderer hands us a new
            // Texture2D after each gen). When no texture exists, dim the
            // RawImage to a near-black tint so the empty state reads
            // intentionally rather than as a glitch.
            if (_previewImage != null)
            {
                if (_previewImage.texture != LatestPreview)
                    _previewImage.texture = LatestPreview;
                _previewImage.color = LatestPreview != null
                    ? Color.white
                    : new Color(0.10f, 0.10f, 0.12f, 1f);
            }

            // Caption refresh: split-column display when preview exists,
            // single centered hint when empty.
            bool hasPreview = LatestPreview != null;
            if (_captionText != null)
                _captionText.gameObject.SetActive(!hasPreview);
            if (_captionLeftText != null)
                _captionLeftText.gameObject.SetActive(hasPreview);
            if (_captionMidText != null)
                _captionMidText.gameObject.SetActive(hasPreview);
            if (_captionRightText != null)
                _captionRightText.gameObject.SetActive(hasPreview);

            if (!hasPreview)
            {
                if (_captionText != null && _captionText.text != "Click PREVIEW to generate")
                    _captionText.text = "Click PREVIEW to generate";
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

            gameObject.AddComponent<GraphicRaycaster>();  // not strictly needed
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
            _captionLeftText = MakeCaptionColumn(captionRT, "CaptionLeft",
                font, anchorMin: new Vector2(0f, 0f), anchorMax: new Vector2(0.5f, 1f),
                align: TextAlignmentOptions.Left, sizePadding: 4);
            _captionMidText = MakeCaptionColumn(captionRT, "CaptionMid",
                font, anchorMin: new Vector2(0.5f, 0f), anchorMax: new Vector2(0.75f, 1f),
                align: TextAlignmentOptions.Left, sizePadding: 0);
            _captionRightText = MakeCaptionColumn(captionRT, "CaptionRight",
                font, anchorMin: new Vector2(0.75f, 0f), anchorMax: new Vector2(1f, 1f),
                align: TextAlignmentOptions.Left, sizePadding: 0);

            // ── Generate-Preview button (top-right of panel) ──────────────
            // Triggers an on-demand preview gen so the panel populates
            // even when Pangu's preview-gen path isn't running. Sits as
            // a small icon in the top-right corner of the backdrop.
            BuildPreviewButton(backdropRT, font, borderSprite);

            // Start hidden — Update() flips it on when conditions met.
            _shadowRT.gameObject.SetActive(false);
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
            tmp.alignment = align;
            tmp.fontSize = 12;
            tmp.color = new Color(0.9f, 0.9f, 0.85f, 1f);
            tmp.text = "";
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Truncate;
            tmp.lineSpacing = -2;
            tmp.raycastTarget = false;
            return tmp;
        }

        /// <summary>Add a small "Generate Preview" button anchored to the
        /// top-right corner of the panel. Clicking it triggers an on-demand
        /// preview gen via <see cref="PreviewGenWorker.TriggerPreview"/>,
        /// breaking the dependency on Pangu's preview-gen running.</summary>
        private void BuildPreviewButton(RectTransform parent, TMP_FontAsset? font, Sprite? buttonSprite)
        {
            var btnGO = new GameObject("PreviewButton");
            btnGO.transform.SetParent(parent, false);
            var rt = btnGO.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-8f, -8f);
            rt.sizeDelta = new Vector2(110f, 30f);

            var bgImg = btnGO.AddComponent<Image>();
            if (buttonSprite != null) { bgImg.sprite = buttonSprite; bgImg.type = Image.Type.Sliced; }
            bgImg.color = new Color(0.18f, 0.18f, 0.22f, 0.95f);
            bgImg.raycastTarget = true;

            var btn = btnGO.AddComponent<Button>();
            btn.targetGraphic = bgImg;

            // Hover/press color states
            var colors = btn.colors;
            colors.normalColor = new Color(1f, 1f, 1f, 1f);
            colors.highlightedColor = new Color(1.2f, 1.2f, 1.0f, 1f);
            colors.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            btn.colors = colors;

            // Button label
            var lblGO = new GameObject("Label");
            lblGO.transform.SetParent(rt, false);
            var lblRT = lblGO.AddComponent<RectTransform>();
            lblRT.anchorMin = Vector2.zero;
            lblRT.anchorMax = Vector2.one;
            lblRT.offsetMin = Vector2.zero;
            lblRT.offsetMax = Vector2.zero;
            var lbl = lblGO.AddComponent<TextMeshProUGUI>();
            if (font != null) lbl.font = font;
            lbl.alignment = TextAlignmentOptions.Center;
            lbl.fontSize = 13;
            lbl.color = new Color(0.95f, 0.92f, 0.82f, 1f);
            lbl.text = "PREVIEW";
            lbl.raycastTarget = false;

            btn.onClick.AddListener(() =>
            {
                try { PreviewGenWorker.TriggerPreview(); }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[RR][PreviewOverlay] Preview button error: {ex.Message}");
                }
            });
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
