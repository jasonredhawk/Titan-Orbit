using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Full-screen map build overlay. Progress tracks local GameObject Instantiates only.
    /// Server meta gives the stable "/ N"; the bar does not advance on network/ECS Instantiates.
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

        public bool IsVisible => _panelRoot != null && _panelRoot.gameObject.activeSelf;

        void Awake()
        {
            BuildUi();
            Hide();
        }

        void Update()
        {
            // --- Per-frame refresh ---
            if (!IsVisible)
                return;

            if (EcsGameBridge.TryGetMapLoadingStepCounts(out int completedSteps, out int totalSteps) && totalSteps > 0)
            {
                // [TITAN-ORBIT] completedSteps = planet/asteroid GameObjects built locally.
                // totalSteps = MapSessionMetaRpc once from server. Cap 99% until complete.
                float fraction = (float)completedSteps / totalSteps;
                if (!EcsGameBridge.IsMapLoadingComplete())
                    fraction = Mathf.Min(fraction, 0.99f);
                UpdateStatusForSteps(completedSteps, totalSteps);
                ApplyProgress(fraction);
                return;
            }

            if (EcsGameBridge.TryGetMapLoadingProgress(out float progress))
            {
                if (progress >= 1f && !EcsGameBridge.IsMapLoadingComplete())
                    progress = 0.99f;
                ApplyProgress(progress);
            }
        }

        public void Show()
        {
            // --- Show ---
            BuildUi();
            ApplyProgress(0f);
            if (_statusText != null)
                _statusText.text = "Waiting for map totals...";
            if (_panelRoot != null)
            {
                _panelRoot.SetAsLastSibling();
                _panelRoot.gameObject.SetActive(true);
            }
        }

        public void Hide()
        {
            if (_panelRoot != null)
                _panelRoot.gameObject.SetActive(false);
        }

        void UpdateStatusForSteps(int completedSteps, int totalSteps)
        {
            // --- Per-frame refresh ---
            if (_statusText == null || totalSteps <= 0)
                return;

            float fraction = (float)completedSteps / totalSteps;
            // Phases describe local GO build only (server already told us N via meta).
            string phase = fraction switch
            {
                <= 0f => "Building map visuals",
                < 0.08f => "Placing home worlds and moons",
                < 0.2f => "Seeding neutral planets and moons",
                < 0.45f => "Scattering asteroid fields",
                _ => "Finishing map visuals",
            };

            _statusText.text = $"{phase}... {completedSteps} / {totalSteps}";
        }

        void ApplyProgress(float progress)
        {
            // --- Apply changes ---
            progress = Mathf.Clamp01(progress);

            if (_fillImage != null)
                _fillImage.fillAmount = progress;

            if (_percentText != null)
                _percentText.text = Mathf.RoundToInt(progress * 100f) + "%";
        }

        void BuildUi()
        {
            // --- Build data ---
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

            _statusText = CreateText(content, "Status", "Preparing map...", 18, FontStyles.Normal);
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
            percentRect.sizeDelta = new Vector2(0f, 24f);
            _percentText.alignment = TextAlignmentOptions.Center;
            _percentText.color = new Color(0.75f, 0.85f, 0.98f, 1f);

            _panelRoot.gameObject.SetActive(false);
            _uiBuilt = true;
        }

        static RectTransform CreateRect(string name, Transform parent)
        {
            // --- Create instance ---
            var go = new GameObject(name, typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        static void StretchFill(RectTransform rect, float left = 0f, float bottom = 0f, float right = 0f, float top = 0f)
        {
            // --- StretchFill ---
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        static TextMeshProUGUI CreateText(RectTransform parent, string name, string text, float fontSize, FontStyles style)
        {
            // --- Create instance ---
            var label = CreateRect(name, parent);
            var tmp = label.gameObject.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.raycastTarget = false;
            return tmp;
        }
    }
}
