using TitanOrbit.Core;
using TitanOrbit.Game;
using TitanOrbit.Input;
using TitanOrbit.Shared;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Left-side keycap for space brakes. Shows ON / OFF and toggles the same flag as Left Ctrl.
    /// Hidden on the main menu, Join Team, and Orbit Menu. Does not query ship entities.
    /// </summary>
    [DefaultExecutionOrder(66210)]
    public class SpaceBrakesHUD : MonoBehaviour
    {
        const float TileSize = 96f;

        static readonly Color FillColor = new Color(0.012f, 0.016f, 0.028f, 0.92f);
        static readonly Color KeyFillOn = new Color(0.06f, 0.12f, 0.16f, 0.96f);
        static readonly Color KeyFillOff = new Color(0.08f, 0.07f, 0.06f, 0.96f);
        static readonly Color CaptionColor = new Color(0.62f, 0.78f, 0.95f, 0.92f);
        static readonly Color OnColor = new Color(0.45f, 0.92f, 0.62f, 1f);
        static readonly Color OffColor = new Color(0.95f, 0.55f, 0.32f, 1f);
        static readonly Color KeycapColor = new Color(0.88f, 0.92f, 0.98f, 1f);

        Canvas _canvas;
        RectTransform _panel;
        Image _panelImage;
        Image _keyFill;
        Image _accent;
        TextMeshProUGUI _keyLabel;
        TextMeshProUGUI _nameLabel;
        TextMeshProUGUI _stateLabel;
        GameObject _mainMenuPanel;
        PlayerInputHandler _input;
        bool _lastOn = true;

        /// <summary>[UNITY] Creates the HUD once after the first scene load.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (FindFirstObjectByType<SpaceBrakesHUD>() != null)
                return;

            var go = new GameObject(nameof(SpaceBrakesHUD));
            DontDestroyOnLoad(go);
            go.AddComponent<SpaceBrakesHUD>();
        }

        /// <summary>Builds the square keycap overlay.</summary>
        void Awake()
        {
            BuildUi();
            SetVisible(false);
        }

        /// <summary>Paints ON/OFF from <see cref="PlayerInputHandler"/>; no ship queries.</summary>
        void LateUpdate()
        {
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl() ||
                IsMainMenuShowing() ||
                !EcsGameBridge.HasLocalPlayerShip())
            {
                SetVisible(false);
                return;
            }

            if (MoonOrbitClientState.IsOrbitMenuVisible)
            {
                SetVisible(false);
                return;
            }

            if (_input == null)
                _input = FindFirstObjectByType<PlayerInputHandler>();

            SetVisible(true);
            Paint(_input == null || _input.SpaceBrakesEnabled);
        }

        /// <summary>True while the scene Main Menu panel is up (Play / Join Game).</summary>
        bool IsMainMenuShowing()
        {
            if (_mainMenuPanel == null)
                _mainMenuPanel = GameObject.Find("MainMenuPanel");
            return _mainMenuPanel != null && _mainMenuPanel.activeInHierarchy;
        }

        /// <summary>
        /// Shows or hides the keycap only. Never disables the Canvas — Orbit Menu
        /// must not share a disabled overlay.
        /// </summary>
        void SetVisible(bool visible)
        {
            if (_canvas != null)
                _canvas.enabled = true;
            if (_panel != null)
                _panel.gameObject.SetActive(visible);
        }

        /// <summary>Same toggle as Left Ctrl.</summary>
        void OnClicked()
        {
            if (MoonOrbitClientState.IsOrbitMenuVisible)
                return;
            if (_input == null)
                _input = FindFirstObjectByType<PlayerInputHandler>();
            _input?.ToggleSpaceBrakes();
        }

        /// <summary>Updates fill and labels when the toggle changes.</summary>
        void Paint(bool brakesOn)
        {
            if (brakesOn == _lastOn && _stateLabel != null && _stateLabel.text.Length > 0)
                return;
            _lastOn = brakesOn;

            Color state = brakesOn ? OnColor : OffColor;
            if (_panelImage != null)
                _panelImage.color = FillColor;
            if (_keyFill != null)
                _keyFill.color = brakesOn ? KeyFillOn : KeyFillOff;
            if (_accent != null)
                _accent.color = state;
            if (_keyLabel != null)
                _keyLabel.color = KeycapColor;
            if (_nameLabel != null)
                _nameLabel.color = CaptionColor;
            if (_stateLabel != null)
            {
                _stateLabel.text = brakesOn ? "ON" : "OFF";
                _stateLabel.color = state;
            }
        }

        /// <summary>Builds a square CTRL keycap with status text.</summary>
        void BuildUi()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 80;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            gameObject.AddComponent<GraphicRaycaster>();

            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(Image), typeof(Button));
            panelGo.transform.SetParent(transform, false);
            _panel = panelGo.GetComponent<RectTransform>();
            _panel.anchorMin = new Vector2(0f, 0f);
            _panel.anchorMax = new Vector2(0f, 0f);
            _panel.pivot = new Vector2(0f, 0f);
            _panel.anchoredPosition = new Vector2(14f, 168f);
            _panel.sizeDelta = new Vector2(TileSize, TileSize);
            _panelImage = panelGo.GetComponent<Image>();
            _panelImage.color = FillColor;
            _panelImage.raycastTarget = true;
            var btn = panelGo.GetComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(OnClicked);

            var accentGo = new GameObject("Accent", typeof(RectTransform), typeof(Image));
            accentGo.transform.SetParent(_panel, false);
            var accentRt = accentGo.GetComponent<RectTransform>();
            accentRt.anchorMin = new Vector2(0f, 0f);
            accentRt.anchorMax = new Vector2(0f, 1f);
            accentRt.pivot = new Vector2(0f, 0.5f);
            accentRt.sizeDelta = new Vector2(3f, 0f);
            accentRt.anchoredPosition = Vector2.zero;
            _accent = accentGo.GetComponent<Image>();
            _accent.color = OnColor;
            _accent.raycastTarget = false;

            var keyGo = new GameObject("Keycap", typeof(RectTransform), typeof(Image));
            keyGo.transform.SetParent(_panel, false);
            var keyRt = keyGo.GetComponent<RectTransform>();
            keyRt.anchorMin = new Vector2(0.5f, 1f);
            keyRt.anchorMax = new Vector2(0.5f, 1f);
            keyRt.pivot = new Vector2(0.5f, 1f);
            keyRt.anchoredPosition = new Vector2(0f, -8f);
            keyRt.sizeDelta = new Vector2(72f, 28f);
            _keyFill = keyGo.GetComponent<Image>();
            _keyFill.color = KeyFillOn;
            _keyFill.raycastTarget = false;

            _keyLabel = CreateLabel(keyRt, "Key", "CTRL", 15f, KeycapColor, TextAlignmentOptions.Center);
            Stretch(_keyLabel.rectTransform);

            _nameLabel = CreateLabel(_panel, "Name", "BRAKES", 11f, CaptionColor, TextAlignmentOptions.Center);
            var nameRt = _nameLabel.rectTransform;
            nameRt.anchorMin = new Vector2(0f, 0.28f);
            nameRt.anchorMax = new Vector2(1f, 0.52f);
            nameRt.offsetMin = new Vector2(8f, 0f);
            nameRt.offsetMax = new Vector2(-6f, 0f);

            _stateLabel = CreateLabel(_panel, "State", "ON", 18f, OnColor, TextAlignmentOptions.Center);
            var stateRt = _stateLabel.rectTransform;
            stateRt.anchorMin = new Vector2(0f, 0.04f);
            stateRt.anchorMax = new Vector2(1f, 0.30f);
            stateRt.offsetMin = new Vector2(8f, 2f);
            stateRt.offsetMax = new Vector2(-6f, 0f);
        }

        /// <summary>Creates a TMP label under <paramref name="parent"/>.</summary>
        static TextMeshProUGUI CreateLabel(
            Transform parent,
            string name,
            string text,
            float size,
            Color color,
            TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            return tmp;
        }

        /// <summary>Fills the parent rect.</summary>
        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
