using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Full-screen join overlay. One progress bar that always crawls while the client is
    /// in-game and map load is not complete, then fills to 100% and yields to Join Team.
    /// Does not count GameObject proxies or asteroid entities (those paths Crash!!! on Windows).
    /// </summary>
    public class LoadingScreenControllerNce : MonoBehaviour
    {
        const float BarPadding = 2f;

        RectTransform _panelRoot;
        RectTransform _barTrackRect;
        RectTransform _fillRect;
        Image _fillImage;
        TextMeshProUGUI _titleText;
        TextMeshProUGUI _statusText;
        TextMeshProUGUI _percentText;
        bool _uiBuilt;
        static Sprite _whiteSprite;

        static Sprite WhiteSprite
        {
            get
            {
                if (_whiteSprite == null)
                {
                    _whiteSprite = Sprite.Create(
                        Texture2D.whiteTexture,
                        new Rect(0f, 0f, 4f, 4f),
                        new Vector2(0.5f, 0.5f),
                        100f);
                }

                return _whiteSprite;
            }
        }

        /// <summary>True when the loading panel GameObject is active.</summary>
        public bool IsVisible => _panelRoot != null && _panelRoot.gameObject.activeSelf;

        /// <summary>[UNITY] Build UI once, start hidden.</summary>
        void Awake()
        {
            BuildUi();
            Hide();
        }

        /// <summary>
        /// Per-frame: fill from <see cref="EcsGameBridge.TryGetJoinLoadProgress"/>
        /// (planet/asteroid GameObject proxies vs server meta N).
        /// </summary>
        void Update()
        {
            if (!IsVisible)
                return;

            // --- Map GO build progress ---
            // [TITAN-ORBIT] Bar covers hybrid Instantiates cost — complete only when proxies ≈ N.
            if (!EcsGameBridge.TryGetJoinLoadProgress(out float progress))
                return;

            if (_statusText != null)
            {
                if (EcsGameBridge.TryGetMapLoadingStepCounts(out int done, out int total) && total > 0)
                    _statusText.text = "Loading map... " + done + " / " + total;
                else
                    _statusText.text = "Loading map...";
            }

            ApplyProgress(progress);
        }

        /// <summary>Shows the overlay and resets the fill to empty.</summary>
        public void Show()
        {
            BuildUi();
            ApplyProgress(0f);
            if (_statusText != null)
                _statusText.text = "Loading map...";
            if (_panelRoot != null)
            {
                _panelRoot.SetAsLastSibling();
                _panelRoot.gameObject.SetActive(true);
            }
        }

        /// <summary>Hides the overlay without destroying it.</summary>
        public void Hide()
        {
            if (_panelRoot != null)
                _panelRoot.gameObject.SetActive(false);
        }

        /// <summary>Applies 0–1 fill amount and percent label.</summary>
        void ApplyProgress(float progress)
        {
            progress = Mathf.Clamp01(progress);

            if (_fillImage != null)
                _fillImage.fillAmount = progress;

            if (_percentText != null)
                _percentText.text = Mathf.RoundToInt(progress * 100f) + "%";
        }

        /// <summary>[UNITY] Builds the full-screen panel once under the parent Canvas.</summary>
        void BuildUi()
        {
            if (_uiBuilt)
                return;

            var canvasRect = GetComponentInParent<Canvas>()?.GetComponent<RectTransform>();
            if (canvasRect == null)
                return;

            var existingPanel = canvasRect.Find("LoadingPanel");
            if (existingPanel != null)
                Destroy(existingPanel.gameObject);

            _panelRoot = CreateRect("LoadingPanel", canvasRect);
            StretchFill(_panelRoot);

            // [TITAN-ORBIT] Slightly transparent so players can watch asteroids/planets Instantiates
            // during join. Join Game is dismissed before this shows — so the lobby list does not
            // bleed through (see JoinGameBrowserController.DismissForLoading).
            var backdrop = _panelRoot.gameObject.AddComponent<Image>();
            backdrop.color = new Color(0.02f, 0.04f, 0.1f, 0.97f);
            backdrop.raycastTarget = true;

            var content = CreateRect("Content", _panelRoot);
            content.anchorMin = new Vector2(0.5f, 0.5f);
            content.anchorMax = new Vector2(0.5f, 0.5f);
            content.pivot = new Vector2(0.5f, 0.5f);
            content.sizeDelta = new Vector2(520f, 180f);

            _titleText = CreateText(content, "Title", "BUILDING GALAXY", 34, FontStyles.Bold);
            var titleRect = _titleText.rectTransform;
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(0f, 0f);
            titleRect.sizeDelta = new Vector2(0f, 44f);
            _titleText.alignment = TextAlignmentOptions.Center;
            _titleText.color = new Color(0.85f, 0.92f, 1f, 1f);

            _statusText = CreateText(content, "Status", "Loading map...", 18, FontStyles.Normal);
            var statusRect = _statusText.rectTransform;
            statusRect.anchorMin = new Vector2(0f, 1f);
            statusRect.anchorMax = new Vector2(1f, 1f);
            statusRect.pivot = new Vector2(0.5f, 1f);
            statusRect.anchoredPosition = new Vector2(0f, -52f);
            statusRect.sizeDelta = new Vector2(0f, 28f);
            _statusText.alignment = TextAlignmentOptions.Center;
            _statusText.color = new Color(0.62f, 0.76f, 0.92f, 0.95f);

            var barBackground = CreateRect("ProgressBarBackground", content);
            _barTrackRect = barBackground;
            barBackground.anchorMin = new Vector2(0f, 0f);
            barBackground.anchorMax = new Vector2(1f, 0f);
            barBackground.pivot = new Vector2(0.5f, 0f);
            barBackground.anchoredPosition = new Vector2(0f, 36f);
            barBackground.sizeDelta = new Vector2(0f, 18f);
            var barBgImage = barBackground.gameObject.AddComponent<Image>();
            barBgImage.sprite = WhiteSprite;
            barBgImage.color = new Color(0.08f, 0.12f, 0.2f, 0.95f);
            barBgImage.raycastTarget = false;

            _fillRect = CreateRect("Fill", barBackground);
            StretchFill(_fillRect, BarPadding, BarPadding, BarPadding, BarPadding);
            _fillImage = _fillRect.gameObject.AddComponent<Image>();
            _fillImage.sprite = WhiteSprite;
            _fillImage.type = Image.Type.Filled;
            _fillImage.fillMethod = Image.FillMethod.Horizontal;
            _fillImage.fillOrigin = (int)Image.OriginHorizontal.Left;
            _fillImage.fillAmount = 0f;
            _fillImage.color = new Color(0.28f, 0.62f, 0.98f, 1f);
            _fillImage.raycastTarget = false;

            _percentText = CreateText(content, "Percent", "0%", 16, FontStyles.Bold);
            var percentRect = _percentText.rectTransform;
            percentRect.anchorMin = new Vector2(0f, 0f);
            percentRect.anchorMax = new Vector2(1f, 0f);
            percentRect.pivot = new Vector2(0.5f, 0f);
            percentRect.anchoredPosition = new Vector2(0f, 8f);
            percentRect.sizeDelta = new Vector2(0f, 22f);
            _percentText.alignment = TextAlignmentOptions.Center;
            _percentText.color = new Color(0.75f, 0.85f, 1f, 0.95f);

            _uiBuilt = true;
        }

        /// <summary>Creates an empty RectTransform child.</summary>
        static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        /// <summary>Creates a TMP label under <paramref name="parent"/>.</summary>
        static TextMeshProUGUI CreateText(Transform parent, string name, string text, float size, FontStyles style)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.fontStyle = style;
            tmp.raycastTarget = false;
            return tmp;
        }

        /// <summary>Stretches a rect to its parent with optional padding.</summary>
        static void StretchFill(RectTransform rect, float left = 0f, float right = 0f, float top = 0f, float bottom = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }
    }
}
