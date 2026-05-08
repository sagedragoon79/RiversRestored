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
        public static string LatestCaption = "";

        // Layout — values in Canvas-space pixels at the reference resolution
        // (1920×1080). CanvasScaler will scale on different displays.
        private const int PANEL_W = 425;
        private const int PANEL_H = 425;
        private const int CAPTION_H = 30;
        private const int RIGHT_MARGIN = 16;

        // Sprite/font asset names from KC's UI dump (FF's loaded assets).
        private const string SPRITE_SHADOW = "IMG_BGShadowThickSoft01";
        private const string SPRITE_BORDER = "IMG_BorderSimpleThickDark01B";
        private const string FONT_NAME = "Andada-Bold";  // substring match — covers "Andada-Bold SDF" variants

        // Canvas hierarchy refs we hold so update() can toggle visibility
        // and rebind the texture.
        private Canvas? _canvas;
        private RectTransform? _shadowRT;
        private RawImage? _previewImage;
        private TextMeshProUGUI? _captionText;

        private bool _initialized = false;

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

            // Visibility — show only when the pref is on AND we have a preview.
            bool wantVisible = (RiversRestoredMod.ShowPreviewOverlay?.Value ?? false)
                              && LatestPreview != null;
            if (_shadowRT.gameObject.activeSelf != wantVisible)
                _shadowRT.gameObject.SetActive(wantVisible);

            if (!wantVisible) return;

            // Rebind texture if it changed (renderer hands us a new
            // Texture2D after each gen).
            if (_previewImage != null && _previewImage.texture != LatestPreview)
                _previewImage.texture = LatestPreview;

            // Refresh caption.
            if (_captionText != null && _captionText.text != LatestCaption)
                _captionText.text = string.IsNullOrEmpty(LatestCaption)
                    ? "Rivers Restored — preview"
                    : LatestCaption;
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

            var shadowImg = shadowGO.AddComponent<Image>();
            if (shadowSprite != null)
            {
                shadowImg.sprite = shadowSprite;
                shadowImg.type = Image.Type.Sliced;
                shadowImg.color = new Color(0f, 0f, 0f, 0.85f);
            }
            else
            {
                // Fallback: plain dark rectangle.
                shadowImg.color = new Color(0f, 0f, 0f, 0.7f);
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

            var borderImg = borderGO.AddComponent<Image>();
            if (borderSprite != null)
            {
                borderImg.sprite = borderSprite;
                borderImg.type = Image.Type.Sliced;
                borderImg.color = Color.white;
            }
            else
            {
                // Fallback: thin gray outline simulated via a colored rect.
                borderImg.color = new Color(0.55f, 0.55f, 0.6f, 1f);
            }

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

            _captionText = captionGO.AddComponent<TextMeshProUGUI>();
            if (font != null) _captionText.font = font;
            _captionText.alignment = TextAlignmentOptions.Center;
            _captionText.fontSize = 14;
            _captionText.color = new Color(0.9f, 0.9f, 0.85f, 1f);
            _captionText.text = "Rivers Restored — preview";
            _captionText.enableWordWrapping = false;
            _captionText.overflowMode = TextOverflowModes.Ellipsis;

            // Start hidden — Update() flips it on when conditions met.
            _shadowRT.gameObject.SetActive(false);
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
