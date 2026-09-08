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
    /// Left-column CTRL keycap for space brakes. Shows ON / OFF and toggles the same
    /// flag as Left Ctrl. Sits under <see cref="BulletTypeHUD"/> (or the rocket column
    /// when no fire types are showing) so the left HUDs do not overlap.
    /// Hidden on the main menu, Join Team, and Orbit Menu. Does not query ship entities.
    /// </summary>
    [DefaultExecutionOrder(66230)]
    public class SpaceBrakesHUD : MonoBehaviour
    {
        /// <summary>Inner tile width. Matches rocket / bullet buttons.</summary>
        const float TileWidth = 108f;

        /// <summary>Two-line tile (BRAKES + ON/OFF). Matches the fire-type row height.</summary>
        const float TileHeight = 38f;

        /// <summary>Inset from the dark panel edge to the tile.</summary>
        const float PanelPad = 6f;

        /// <summary>Panel width = tile + left/right pad.</summary>
        const float PanelWidth = TileWidth + PanelPad * 2f;

        /// <summary>Panel height = tile + top/bottom pad.</summary>
        const float PanelHeight = TileHeight + PanelPad * 2f;

        /// <summary>Left inset shared with rockets and fire types on the 1920×1080 overlay.</summary>
        const float OverlayLeft = 14f;

        /// <summary>Air between the strip above and this CTRL tile.</summary>
        const float DockGap = 8f;

        static readonly Color FillColor = new Color(0.012f, 0.016f, 0.028f, 0.92f);
        static readonly Color CaptionSelected = new Color(0.95f, 0.98f, 1f, 1f);
        static readonly Color CaptionDim = new Color(0.62f, 0.78f, 0.95f, 0.55f);
        static readonly Color BodyColor = new Color(0.94f, 0.97f, 1f, 1f);
        static readonly Color BodyDim = new Color(0.88f, 0.92f, 0.98f, 0.5f);
        static readonly Color ReadyColor = new Color(0.45f, 0.92f, 0.62f, 1f);
        static readonly Color OffColor = new Color(0.95f, 0.55f, 0.32f, 1f);
        static readonly Color RowIdle = new Color(1f, 1f, 1f, 0.03f);
        static readonly Color RowSelected = new Color(0.04f, 0.10f, 0.20f, 0.94f);
        static readonly Color CaretColor = new Color(0.45f, 0.95f, 1f, 1f);
        static readonly Color LabelOutline = new Color(0.02f, 0.04f, 0.08f, 0.95f);
        static readonly Color KeycapFill = new Color(0.04f, 0.10f, 0.16f, 0.96f);

        Canvas _canvas;
        RectTransform _panel;
        Image _panelImage;
        Image _tileImage;
        Image _caret;
        Image _accent;
        Image _hintChip;
        Outline _outline;
        TextMeshProUGUI _kindLabel;
        TextMeshProUGUI _hintLabel;
        TextMeshProUGUI _stateLabel;
        GameObject _mainMenuPanel;
        PlayerInputHandler _input;
        bool _lastOn = true;
        bool _hasPainted;

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

        /// <summary>Builds the left-column CTRL tile.</summary>
        void Awake()
        {
            BuildUi();
            SetVisible(false);
        }

        /// <summary>
        /// Docks under bullets / rockets, then paints ON/OFF from
        /// <see cref="PlayerInputHandler"/>. Hides on menus and while
        /// <see cref="HUDController.LocalPlayerDeathHidesHud"/>.
        /// </summary>
        void LateUpdate()
        {
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl() ||
                IsMainMenuShowing() ||
                !EcsGameBridge.HasLocalPlayerShip() ||
                HUDController.LocalPlayerDeathHidesHud ||
                HUDController.MinimapExpandedObscuresHud)
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
            ApplyDock();
            Paint(_input == null || _input.SpaceBrakesEnabled);
        }

        /// <summary>
        /// Parks this tile under the fire-type strip, or under rockets when that strip
        /// is hidden, or in the mid-left slot when both are hidden.
        /// </summary>
        void ApplyDock()
        {
            if (_panel == null)
                return;

            float dockBottom = 0f;
            bool stacked = false;

            // Execution order 66230 runs after BulletTypeHUD (66220) and rockets (66200).
            if (BulletTypeHUD.TryGetOverlayDockBottomY(out dockBottom, out bool bulletsVisible) &&
                bulletsVisible)
            {
                stacked = true;
            }
            else if (RocketLoadoutHUD.TryGetOverlayDockBottomY(out dockBottom, out bool rocketsVisible) &&
                     rocketsVisible)
            {
                stacked = true;
            }

            if (stacked)
            {
                _panel.pivot = new Vector2(0f, 1f);
                _panel.anchoredPosition = new Vector2(OverlayLeft, dockBottom - DockGap);
            }
            else
            {
                _panel.pivot = new Vector2(0f, 0.5f);
                _panel.anchoredPosition = new Vector2(OverlayLeft, 0f);
            }
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

        /// <summary>
        /// Updates fill and labels when the toggle changes. ON uses the same caret /
        /// navy fill as a focused rocket or fire-type row.
        /// </summary>
        void Paint(bool brakesOn)
        {
            if (_hasPainted && brakesOn == _lastOn)
                return;
            _hasPainted = true;
            _lastOn = brakesOn;

            Color state = brakesOn ? ReadyColor : OffColor;
            if (_panelImage != null)
                _panelImage.color = FillColor;
            if (_tileImage != null)
                _tileImage.color = brakesOn ? RowSelected : RowIdle;
            if (_accent != null)
                _accent.color = state;
            if (_caret != null)
                _caret.enabled = brakesOn;
            if (_outline != null)
                _outline.enabled = brakesOn;
            if (_kindLabel != null)
            {
                _kindLabel.text = "BRAKES";
                _kindLabel.color = brakesOn ? CaptionSelected : CaptionDim;
            }

            if (_hintLabel != null)
            {
                _hintLabel.text = "CTRL";
                _hintLabel.color = ReadyColor;
            }

            if (_stateLabel != null)
            {
                _stateLabel.text = brakesOn ? "ON" : "OFF";
                _stateLabel.color = brakesOn ? BodyColor : BodyDim;
            }
        }

        /// <summary>Builds a rocket-sized CTRL tile in the left column.</summary>
        void BuildUi()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 80;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            gameObject.AddComponent<GraphicRaycaster>();

            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panelGo.transform.SetParent(transform, false);
            _panel = panelGo.GetComponent<RectTransform>();
            _panel.anchorMin = new Vector2(0f, 0.5f);
            _panel.anchorMax = new Vector2(0f, 0.5f);
            _panel.pivot = new Vector2(0f, 0.5f);
            _panel.anchoredPosition = new Vector2(OverlayLeft, 0f);
            _panel.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            _panelImage = panelGo.GetComponent<Image>();
            _panelImage.color = FillColor;
            _panelImage.raycastTarget = true;

            var accentGo = new GameObject("Accent", typeof(RectTransform), typeof(Image));
            accentGo.transform.SetParent(_panel, false);
            var accentRt = accentGo.GetComponent<RectTransform>();
            accentRt.anchorMin = new Vector2(0f, 0f);
            accentRt.anchorMax = new Vector2(0f, 1f);
            accentRt.pivot = new Vector2(0f, 0.5f);
            accentRt.sizeDelta = new Vector2(3f, 0f);
            accentRt.anchoredPosition = Vector2.zero;
            _accent = accentGo.GetComponent<Image>();
            _accent.color = ReadyColor;
            _accent.raycastTarget = false;

            var tileGo = new GameObject("Tile", typeof(RectTransform), typeof(Image), typeof(Button));
            tileGo.transform.SetParent(_panel, false);
            var tileRt = tileGo.GetComponent<RectTransform>();
            tileRt.anchorMin = new Vector2(0f, 1f);
            tileRt.anchorMax = new Vector2(0f, 1f);
            tileRt.pivot = new Vector2(0f, 1f);
            tileRt.anchoredPosition = new Vector2(PanelPad, -PanelPad);
            tileRt.sizeDelta = new Vector2(TileWidth, TileHeight);
            _tileImage = tileGo.GetComponent<Image>();
            _tileImage.color = RowIdle;
            var btn = tileGo.GetComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(OnClicked);

            var outline = tileGo.AddComponent<Outline>();
            outline.effectColor = CaretColor;
            outline.effectDistance = new Vector2(1f, -1f);
            outline.useGraphicAlpha = false;
            outline.enabled = false;
            _outline = outline;

            var caretGo = new GameObject("Caret", typeof(RectTransform), typeof(Image));
            caretGo.transform.SetParent(tileRt, false);
            var caretRt = caretGo.GetComponent<RectTransform>();
            caretRt.anchorMin = new Vector2(0f, 0f);
            caretRt.anchorMax = new Vector2(0f, 1f);
            caretRt.pivot = new Vector2(0f, 0.5f);
            caretRt.sizeDelta = new Vector2(3f, 0f);
            caretRt.anchoredPosition = Vector2.zero;
            _caret = caretGo.GetComponent<Image>();
            _caret.color = CaretColor;
            _caret.raycastTarget = false;
            _caret.enabled = false;

            _kindLabel = CreateLabel(tileRt, "Kind", "BRAKES", 9.5f, CaptionSelected, TextAlignmentOptions.Left);
            var kindRt = _kindLabel.rectTransform;
            kindRt.anchorMin = new Vector2(0f, 0.46f);
            kindRt.anchorMax = new Vector2(1f, 1f);
            kindRt.offsetMin = new Vector2(8f, 0f);
            kindRt.offsetMax = new Vector2(-32f, -1f);
            _kindLabel.characterSpacing = 0.4f;

            var chipGo = new GameObject("HintChip", typeof(RectTransform), typeof(Image));
            chipGo.transform.SetParent(tileRt, false);
            var chipRt = chipGo.GetComponent<RectTransform>();
            chipRt.anchorMin = new Vector2(1f, 1f);
            chipRt.anchorMax = new Vector2(1f, 1f);
            chipRt.pivot = new Vector2(1f, 1f);
            chipRt.anchoredPosition = new Vector2(-3f, -3f);
            chipRt.sizeDelta = new Vector2(28f, 12f);
            _hintChip = chipGo.GetComponent<Image>();
            _hintChip.color = KeycapFill;
            _hintChip.raycastTarget = false;

            _hintLabel = CreateLabel(chipRt, "Hint", "CTRL", 7.5f, ReadyColor, TextAlignmentOptions.Center);
            var hintRt = _hintLabel.rectTransform;
            hintRt.anchorMin = Vector2.zero;
            hintRt.anchorMax = Vector2.one;
            hintRt.offsetMin = Vector2.zero;
            hintRt.offsetMax = Vector2.zero;
            _hintLabel.characterSpacing = 0.6f;

            _stateLabel = CreateLabel(tileRt, "State", "ON", 7.5f, BodyColor, TextAlignmentOptions.Left);
            var stateRt = _stateLabel.rectTransform;
            stateRt.anchorMin = new Vector2(0f, 0f);
            stateRt.anchorMax = new Vector2(1f, 0.48f);
            stateRt.offsetMin = new Vector2(8f, 2f);
            stateRt.offsetMax = new Vector2(-4f, 0f);
            _stateLabel.characterSpacing = 0.8f;
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
            tmp.outlineWidth = 0.18f;
            tmp.outlineColor = LabelOutline;
            TMP_FontAsset font = Resources.Load<TMP_FontAsset>("Rajdhani-SemiBold SDF");
            if (font != null)
                tmp.font = font;
            return tmp;
        }
    }
}
