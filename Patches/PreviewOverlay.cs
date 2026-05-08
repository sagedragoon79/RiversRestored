using System;
using MelonLoader;
using UnityEngine;

namespace RiversRestored.Patches
{
    /// <summary>
    /// Minimal in-game overlay that displays the most-recently-rendered map
    /// preview texture in a fixed-position panel on the right side of the
    /// screen. Toggleable via hotkey or pref. Uses OnGUI (immediate-mode
    /// Unity UI) for simplicity — full UGUI Canvas integration into FF's
    /// New Game screen would be 10× the code for marginal polish gain.
    ///
    /// Lifecycle: instance attached as a global GameObject at MelonMod
    /// initialization. Reads <see cref="LatestPreview"/> set by
    /// <see cref="MapPreviewRenderer"/> at end of gen.
    ///
    /// Toggle: <see cref="RiversRestoredMod.ShowPreviewOverlay"/> pref +
    /// hotkey (F8 default, configurable later).
    /// </summary>
    public class PreviewOverlay : MonoBehaviour
    {
        /// <summary>Texture2D of the most recently rendered preview.
        /// Set by <see cref="MapPreviewRenderer.TryRender"/>; read here.
        /// Null means no preview has been rendered this session yet.</summary>
        public static Texture2D? LatestPreview;

        /// <summary>Most recent preview's metadata for the caption strip.</summary>
        public static string LatestCaption = "";

        // Panel layout (anchored to right edge, vertically centered).
        // Sized to fit between FF's New Game settings dialog and the
        // right edge of screen at common 1920×1080 / 1920×900 layouts
        // without overlapping the settings panel.
        private const int PANEL_W = 360;
        private const int PANEL_H = 360;
        private const int CAPTION_H = 24;
        private const int MARGIN = 16;

        private static GUIStyle? _captionStyle;

        // Diagnostic counters — log first OnGUI call and gating reasons.
        private static bool _loggedFirstOnGUI = false;
        private static bool _loggedGateReason = false;

        private void Start()
        {
            MelonLogger.Msg("[RR][PreviewOverlay] MonoBehaviour Start — overlay is alive.");
        }

        private void OnGUI()
        {
            if (!_loggedFirstOnGUI)
            {
                _loggedFirstOnGUI = true;
                MelonLogger.Msg("[RR][PreviewOverlay] OnGUI firing. " +
                    $"showPref={(RiversRestoredMod.ShowPreviewOverlay?.Value ?? false)} " +
                    $"latestTex={(LatestPreview != null ? "OK" : "null")}");
            }

            if (!(RiversRestoredMod.ShowPreviewOverlay?.Value ?? false))
            {
                if (!_loggedGateReason)
                {
                    _loggedGateReason = true;
                    MelonLogger.Msg("[RR][PreviewOverlay] gated: pref OFF.");
                }
                return;
            }
            if (LatestPreview == null)
            {
                if (!_loggedGateReason)
                {
                    _loggedGateReason = true;
                    MelonLogger.Msg("[RR][PreviewOverlay] gated: no preview rendered yet (run a gen).");
                }
                return;
            }
            // Reset gate-reason log so we re-log if conditions change.
            _loggedGateReason = false;

            int w = PANEL_W;
            int h = PANEL_H;
            int x = Screen.width - w - MARGIN;
            int y = (Screen.height - h - CAPTION_H) / 2;

            // Backdrop (slightly darker than panel, for readability).
            var prevColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(x - 6, y - 6, w + 12, h + CAPTION_H + 12), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // The preview itself.
            GUI.DrawTexture(new Rect(x, y, w, h), LatestPreview, ScaleMode.StretchToFill);

            // Caption strip below the image.
            if (_captionStyle == null)
            {
                _captionStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.white },
                };
            }
            GUI.Label(new Rect(x, y + h + 4, w, CAPTION_H - 4),
                      string.IsNullOrEmpty(LatestCaption) ? "Rivers Restored — preview" : LatestCaption,
                      _captionStyle);

            GUI.color = prevColor;
        }

        private void Update()
        {
            // F8 quick-toggle. Avoids users having to dig into KC settings
            // every time they want to peek at the preview.
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
        }
    }
}
