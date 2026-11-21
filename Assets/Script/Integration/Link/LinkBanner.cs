using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Menu.Persistent;

namespace YARG.Integration.Link
{
    /// <summary>
    /// The online banner at the top of the screen during a song: what a synced pause is waiting on, or the
    /// countdown of a song that lost its host. A uGUI canvas of its own, so it needs no prefab and shows over
    /// the pause menu.
    /// </summary>
    internal sealed class LinkBanner : MonoBehaviour
    {
        public enum Style
        {
            /// <summary>A synced pause.</summary>
            Info,
            /// <summary>The song lost its host.</summary>
            Warning,
        }

        /// <summary>Over the gameplay HUD and the pause menu, under the toasts (<c>ToastManager</c>, 1000).</summary>
        private const int SORTING_ORDER = 900;

        /// <summary>The layout is in pixels of a 1080p screen, and scales with the screen's height.</summary>
        private static readonly Vector2 REFERENCE_RESOLUTION = new(1920, 1080);

        private const float TOP = 40;
        private const int   PADDING = 14;
        private const float FONT_SIZE = 24;

        private static readonly Color InfoBackground    = new(0f, 0f, 0f, 0.7f);
        private static readonly Color WarningBackground = new(0.25f, 0f, 0f, 0.8f);

        private Canvas          _canvas;
        private Image           _background;
        private TextMeshProUGUI _text;
        private string          _shown;
        private bool            _fontSet;

        public static LinkBanner Create(Transform parent)
        {
            var go = new GameObject(nameof(LinkBanner), typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var banner = go.AddComponent<LinkBanner>();
            banner.Build();
            return banner;
        }

        private void Build()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = SORTING_ORDER;
            _canvas.enabled = false;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = REFERENCE_RESOLUTION;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;

            // A box at the top center that fits its text
            var box = new GameObject("Box", typeof(RectTransform));
            box.transform.SetParent(transform, false);
            var rect = (RectTransform) box.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0, -TOP);

            _background = box.AddComponent<Image>();
            _background.raycastTarget = false;
            _background.color = InfoBackground;

            var layout = box.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(PADDING, PADDING, PADDING, PADDING);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var fitter = box.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var label = new GameObject("Text", typeof(RectTransform));
            label.transform.SetParent(box.transform, false);
            _text = label.AddComponent<TextMeshProUGUI>();
            _text.richText = true;
            _text.textWrappingMode = TextWrappingModes.NoWrap;
            _text.alignment = TextAlignmentOptions.Center;
            _text.fontSize = FONT_SIZE;
            _text.color = Color.white;
            _text.raycastTarget = false;
        }

        /// <summary>Show <paramref name="text"/> (rich text), or hide the banner for <c>null</c>.</summary>
        public void Show(string text, Style style)
        {
            if (text == null)
            {
                Hide();
                return;
            }

            // The toasts' font, so the banner looks like the rest of the game. The toasts may not exist yet
            // when the banner is made.
            if (!_fontSet && ToastManager.Instance != null && ToastManager.Instance.MessageFont is { } font)
            {
                _text.font = font;
                _fontSet = true;
            }

            if (text != _shown)
            {
                _text.text = text;
                _shown = text;
            }

            _background.color = style == Style.Warning ? WarningBackground : InfoBackground;
            if (!_canvas.enabled) _canvas.enabled = true;
        }

        public void Hide()
        {
            if (_canvas.enabled) _canvas.enabled = false;
            _shown = null;
        }
    }
}
