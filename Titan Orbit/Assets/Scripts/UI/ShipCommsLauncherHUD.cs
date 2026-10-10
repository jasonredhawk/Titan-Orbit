using TitanOrbit.Core;
using TitanOrbit.Game;
using TitanOrbit.NetCode;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Top-left COMMS MATRIX keycap with a C hint chip, same corner treatment as
    /// BRAKES / CTRL. Sits under <see cref="SpaceBrakesHUD"/>
    /// (or the strip above it when brakes are hidden). Click opens the comms card
    /// and leaves it up until SEND or CLOSE. Hold C is a different path: the card
    /// stays only while the key is down and releasing C sends. This button does
    /// not query ship entities — <see cref="ShipCommsPanel"/> still decides whether
    /// a match is actually in progress.
    /// </summary>
    [DefaultExecutionOrder(66270)]
    public class ShipCommsLauncherHUD : MonoBehaviour
    {
        /// <summary>Inner tile width. Matches the CTRL brakes keycap.</summary>
        const float TileWidth = 108f;

        /// <summary>Two-line tile (COMMS + MATRIX).</summary>
        const float TileHeight = 38f;

        /// <summary>Inset from the dark panel edge to the tile.</summary>
        const float PanelPad = 6f;

        const float PanelWidth = TileWidth + PanelPad * 2f;
        const float PanelHeight = TileHeight + PanelPad * 2f;

        /// <summary>Air between the brakes tile and this keycap.</summary>
        const float DockGap = 8f;

        static readonly Color FillColor = new Color(0.012f, 0.016f, 0.028f, 0.92f);
        static readonly Color CaptionColor = new Color(0.62f, 0.78f, 0.95f, 0.92f);
        static readonly Color BodyColor = new Color(0.88f, 0.92f, 0.98f, 1f);
        static readonly Color RowIdle = new Color(1f, 1f, 1f, 0.03f);
        static readonly Color AccentColor = new Color(0.35f, 0.72f, 0.95f, 0.95f);
        /// <summary>Same ready green as the B / CTRL / weapon key hints on the other left-column tiles.</summary>
        static readonly Color ReadyColor = new Color(0.45f, 0.92f, 0.62f, 1f);
        /// <summary>Same dark key plate as the CTRL chip on brakes.</summary>
        static readonly Color KeycapFill = new Color(0.04f, 0.10f, 0.16f, 0.96f);
        static readonly Color LabelOutline = new Color(0.02f, 0.04f, 0.08f, 0.95f);

        Canvas _canvas;
        RectTransform _panel;
        GameObject _mainMenuPanel;

        /// <summary>[UNITY] Creates the keycap once after the first scene load.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            if (FindFirstObjectByType<ShipCommsLauncherHUD>() != null)
                return;

            var go = new GameObject(nameof(ShipCommsLauncherHUD));
            DontDestroyOnLoad(go);
            go.AddComponent<ShipCommsLauncherHUD>();
        }

        /// <summary>Builds the keycap hidden. LateUpdate shows it once a ship is flying.</summary>
        void Awake()
        {
            BuildUi();
            SetVisible(false);
        }

        /// <summary>
        /// Docks under brakes, then shows the keycap only in flight. Menus, death,
        /// the orbit station, and an already-open matrix hide it — the open card
        /// has its own SEND and CLOSE.
        /// </summary>
        void LateUpdate()
        {
            if (Application.isMobilePlatform ||
                ClientTeamFlowState.ShouldSuppressLocalPlayerControl() ||
                IsMainMenuShowing() ||
                !EcsGameBridge.HasLocalPlayerShip() ||
                HUDController.LocalPlayerDeathHidesHud ||
                HUDController.MinimapExpandedObscuresHud ||
                HUDController.CommsMatrixObscuresHud ||
                MoonOrbitClientState.IsOrbitMenuVisible)
            {
                SetVisible(false);
                return;
            }

            SetVisible(true);
            ApplyDock();
        }

        /// <summary>
        /// Parks under the CTRL tile when it is showing, otherwise under the same
        /// strip brakes would have used, so the column order stays stats → weapons → COMMS.
        /// </summary>
        void ApplyDock()
        {
            if (_panel == null)
                return;

            float dockBottom = 0f;
            bool stacked = false;

            // Execution order 66270 runs after SpaceBrakesHUD (66260), so this
            // bottom edge is already this frame's layout.
            if (SpaceBrakesHUD.TryGetOverlayDockBottomY(out dockBottom, out bool brakesVisible) &&
                brakesVisible)
            {
                stacked = true;
            }
            else if (ShipWeaponArmHUD.TryGetOverlayDockBottomY(out dockBottom, out bool arsenalVisible) &&
                     arsenalVisible)
            {
                stacked = true;
            }
            else if (BulletTypeHUD.TryGetOverlayDockBottomY(out dockBottom, out bool bulletsVisible) &&
                     bulletsVisible)
            {
                stacked = true;
            }
            else if (RocketLoadoutHUD.TryGetOverlayDockBottomY(out dockBottom, out bool rocketsVisible) &&
                     rocketsVisible)
            {
                stacked = true;
            }

            RocketLoadoutHUD.PlaceInLeftColumn(_panel, stacked, dockBottom, DockGap);
        }

        /// <summary>True while the scene Main Menu panel is up (Play / Join Game).</summary>
        bool IsMainMenuShowing()
        {
            if (_mainMenuPanel == null)
                _mainMenuPanel = GameObject.Find("MainMenuPanel");
            return _mainMenuPanel != null && _mainMenuPanel.activeInHierarchy;
        }

        /// <summary>
        /// Shows or hides the keycap only. The canvas stays enabled so other
        /// overlays that share this object later are not switched off.
        /// </summary>
        void SetVisible(bool visible)
        {
            if (_canvas != null)
                _canvas.enabled = true;
            if (_panel != null)
                _panel.gameObject.SetActive(visible);
        }

        /// <summary>
        /// Opens the sticky comms card. <see cref="ShipCommsPanel"/> ignores the
        /// click when the player is not allowed to talk (menus, death, jam is
        /// still allowed to open — SEND stays dark until they leave enemy fill).
        /// </summary>
        void OnClicked()
        {
            ShipCommsPanel.ToggleFromLauncher();
        }

        /// <summary>
        /// Dark glass tile: COMMS on the first line, MATRIX under it, and a C
        /// keycap in the top-right — the same chip as BRAKES / CTRL.
        /// </summary>
        void BuildUi()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 80;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();

            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panelGo.transform.SetParent(transform, false);
            _panel = panelGo.GetComponent<RectTransform>();
            RocketLoadoutHUD.PlaceInLeftColumn(_panel, false, 0f, 0f);
            _panel.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            var panelImage = panelGo.GetComponent<Image>();
            panelImage.color = FillColor;
            panelImage.raycastTarget = true;

            var accentGo = new GameObject("Accent", typeof(RectTransform), typeof(Image));
            accentGo.transform.SetParent(_panel, false);
            var accentRt = accentGo.GetComponent<RectTransform>();
            accentRt.anchorMin = new Vector2(0f, 0f);
            accentRt.anchorMax = new Vector2(0f, 1f);
            accentRt.pivot = new Vector2(0f, 0.5f);
            accentRt.sizeDelta = new Vector2(3f, 0f);
            accentRt.anchoredPosition = Vector2.zero;
            var accent = accentGo.GetComponent<Image>();
            accent.color = AccentColor;
            accent.raycastTarget = false;

            var tileGo = new GameObject("Tile", typeof(RectTransform), typeof(Image), typeof(Button));
            tileGo.transform.SetParent(_panel, false);
            var tileRt = tileGo.GetComponent<RectTransform>();
            tileRt.anchorMin = new Vector2(0f, 1f);
            tileRt.anchorMax = new Vector2(0f, 1f);
            tileRt.pivot = new Vector2(0f, 1f);
            tileRt.anchoredPosition = new Vector2(PanelPad, -PanelPad);
            tileRt.sizeDelta = new Vector2(TileWidth, TileHeight);
            var tileImage = tileGo.GetComponent<Image>();
            tileImage.color = RowIdle;
            var btn = tileGo.GetComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(OnClicked);

            var kind = CreateLabel(tileRt, "Kind", "COMMS", 9.5f, CaptionColor, TextAlignmentOptions.Left);
            var kindRt = kind.rectTransform;
            kindRt.anchorMin = new Vector2(0f, 0.46f);
            kindRt.anchorMax = new Vector2(1f, 1f);
            kindRt.offsetMin = new Vector2(8f, 0f);
            kindRt.offsetMax = new Vector2(-32f, -1f);
            kind.characterSpacing = 0.4f;

            // Same corner chip as SpaceBrakesHUD's CTRL hint. One letter, so the
            // plate is narrower, but it still sits in that top-right pocket.
            // The letter itself is the shared ready green; the left rail stays ice blue.
            var chipGo = new GameObject("HintChip", typeof(RectTransform), typeof(Image));
            chipGo.transform.SetParent(tileRt, false);
            var chipRt = chipGo.GetComponent<RectTransform>();
            chipRt.anchorMin = new Vector2(1f, 1f);
            chipRt.anchorMax = new Vector2(1f, 1f);
            chipRt.pivot = new Vector2(1f, 1f);
            chipRt.anchoredPosition = new Vector2(-3f, -3f);
            chipRt.sizeDelta = new Vector2(16f, 12f);
            var hintChip = chipGo.GetComponent<Image>();
            hintChip.color = KeycapFill;
            hintChip.raycastTarget = false;

            var hint = CreateLabel(chipRt, "Hint", "C", 7.5f, ReadyColor, TextAlignmentOptions.Center);
            var hintRt = hint.rectTransform;
            hintRt.anchorMin = Vector2.zero;
            hintRt.anchorMax = Vector2.one;
            hintRt.offsetMin = Vector2.zero;
            hintRt.offsetMax = Vector2.zero;
            hint.characterSpacing = 0.6f;

            var state = CreateLabel(tileRt, "State", "MATRIX", 7.5f, BodyColor, TextAlignmentOptions.Left);
            var stateRt = state.rectTransform;
            stateRt.anchorMin = new Vector2(0f, 0f);
            stateRt.anchorMax = new Vector2(1f, 0.48f);
            stateRt.offsetMin = new Vector2(8f, 2f);
            stateRt.offsetMax = new Vector2(-4f, 0f);
            state.characterSpacing = 0.8f;
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
