using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Personal game-over overlay when the local player dies and their team owns no planets.
    /// They cannot respawn and they do not spectate — Continue disconnects back to Main Menu.
    /// <para>
    /// Client presentation only. Server sets ghosted <see cref="ShipState.IsEliminated"/>.
    /// We also treat "dead + planet cache ready + zero friendly worlds" as eliminated so
    /// Local Host does not wait a tick for the ghost field. Visual language matches
    /// <see cref="MatchEndScreenController"/> (dark void glass, uppercase telemetry).
    /// </para>
    /// Paired with <see cref="ShipRespawnSystem"/> (server) and
    /// <see cref="DeathScreenController"/> (plaque hides while this overlay is up).
    /// </summary>
    public class PlayerEliminatedScreenController : MonoBehaviour
    {
        /// <summary>
        /// True while the eliminated overlay is visible. <see cref="GameplayCursorController"/>
        /// reads this to restore the OS arrow over Continue.
        /// </summary>
        public static bool IsShowing { get; private set; }

        /// <summary>
        /// [UNITY] Domain Reload off leaves this static hot. Called from
        /// <see cref="GameplayCursorController"/> before scene load.
        /// </summary>
        public static void ClearShowingFlag() => IsShowing = false;

        [SerializeField] GameObject overlayRoot;
        [SerializeField] TextMeshProUGUI titleText;
        [SerializeField] TextMeshProUGUI subtitleText;
        [SerializeField] Button continueButton;

        /// <summary>True after we have shown this death so we do not rebuild every frame.</summary>
        bool _shownThisLife;

        /// <summary>True while ReturnToMainMenuAsync is in flight (double-click guard).</summary>
        bool _leaving;

        NceGameFlowController _flow;

        /// <summary>Build the overlay once and keep it hidden until the local ship is eliminated.</summary>
        void Awake()
        {
            _flow = GetComponent<NceGameFlowController>();
            EnsureUi();
            Hide();
        }

        /// <summary>
        /// Per-frame: if the local ship is eliminated (or dead with no friendly worlds),
        /// show the overlay. Hide when alive again or when we leave the match.
        /// </summary>
        void Update()
        {
            if (!EcsGameBridge.IsNetworkInGame() || !EcsGameBridge.HasLocalPlayerShip())
            {
                if (_shownThisLife)
                    Hide();
                _shownThisLife = false;
                return;
            }

            if (!EcsGameBridge.TryGetLocalShipState(out var ship))
            {
                if (_shownThisLife)
                    Hide();
                _shownThisLife = false;
                return;
            }

            if (!ship.IsDead && !ship.IsEliminated)
            {
                if (_shownThisLife)
                    Hide();
                _shownThisLife = false;
                return;
            }

            if (!IsLocalPlayerEliminated(ship))
                return;

            if (_shownThisLife)
                return;

            _shownThisLife = true;
            Show();
        }

        /// <summary>
        /// Server ghost flag, or a ready planet cache that shows this team owns nothing.
        /// Empty cache is "not ready" — we wait so join settle cannot flash this overlay.
        /// </summary>
        public static bool IsLocalPlayerEliminated(in ShipState ship)
        {
            if (ship.IsEliminated)
                return true;
            if (!ship.IsDead || ship.Team == TeamId.None)
                return false;
            if (EcsGameBridge.GetCachedPlanetCount() <= 0)
                return false;
            return !EcsGameBridge.TeamOwnsAnyPlanet(ship.Team);
        }

        /// <summary>Builds (if needed) and shows the eliminated card.</summary>
        void Show()
        {
            EnsureUi();
            if (overlayRoot != null)
                overlayRoot.SetActive(true);
            IsShowing = true;

            if (titleText != null)
                titleText.text = "NO WORLDS REMAINING";

            if (subtitleText != null)
                subtitleText.text = "YOUR TEAM HOLDS NO PLANETS\nTHIS LIFE IS OVER";

            if (continueButton != null)
            {
                continueButton.onClick.RemoveAllListeners();
                continueButton.onClick.AddListener(OnContinueClicked);
            }
        }

        /// <summary>Hides the overlay and clears <see cref="IsShowing"/>.</summary>
        void Hide()
        {
            if (overlayRoot != null)
                overlayRoot.SetActive(false);
            IsShowing = false;
        }

        /// <summary>
        /// Continue: leave gameplay the same way the Escape menu does — disconnect and
        /// return to Main Menu. We do not spectate the wreck.
        /// </summary>
        async void OnContinueClicked()
        {
            if (_leaving)
                return;

            _leaving = true;
            Hide();

            if (_flow != null)
                _flow.NotifyReturningToMainMenu();

            var session = TitanOrbitSessionManager.Instance;
            if (session != null)
            {
                try
                {
                    await session.ReturnToMainMenuAsync();
                }
                catch (System.Exception ex)
                {
                    Debug.LogError("[PlayerEliminated] Leave failed: " + ex.Message);
                    _leaving = false;
                }
            }
            else
            {
                _leaving = false;
            }
        }

        /// <summary>[UNITY] Clears the static flag if this instance was the one showing.</summary>
        void OnDestroy()
        {
            if (IsShowing)
                IsShowing = false;
        }

        /// <summary>
        /// Builds a full-screen void-glass card (same family as match-end). Recreates if a
        /// hot-reload leftover is found.
        /// </summary>
        void EnsureUi()
        {
            if (overlayRoot != null && titleText != null && continueButton != null)
                return;

            Transform existing = transform.Find("PlayerEliminatedOverlay");
            if (existing != null)
                Destroy(existing.gameObject);

            var canvasGo = new GameObject("PlayerEliminatedOverlay");
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9100;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            overlayRoot = canvasGo;

            var panel = new GameObject("Panel");
            panel.transform.SetParent(canvasGo.transform, false);
            var panelImage = panel.AddComponent<Image>();
            panelImage.color = new Color(0.012f, 0.016f, 0.028f, 0.94f);
            var panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = Vector2.zero;
            panelRt.anchorMax = Vector2.one;
            panelRt.offsetMin = Vector2.zero;
            panelRt.offsetMax = Vector2.zero;

            titleText = CreateLabel(panel.transform, "Title", new Vector2(0f, 80f), 42f, FontStyles.Bold);
            titleText.color = new Color(0.95f, 0.48f, 0.28f, 0.95f);
            titleText.characterSpacing = 2.4f;

            subtitleText = CreateLabel(panel.transform, "Subtitle", new Vector2(0f, 8f), 22f, FontStyles.Normal);
            subtitleText.color = new Color(0.88f, 0.92f, 0.98f, 1f);

            var buttonGo = new GameObject("ContinueButton");
            buttonGo.transform.SetParent(panel.transform, false);
            var buttonRt = buttonGo.AddComponent<RectTransform>();
            buttonRt.anchorMin = buttonRt.anchorMax = new Vector2(0.5f, 0.5f);
            buttonRt.pivot = new Vector2(0.5f, 0.5f);
            buttonRt.anchoredPosition = new Vector2(0f, -90f);
            buttonRt.sizeDelta = new Vector2(260f, 48f);

            var buttonImage = buttonGo.AddComponent<Image>();
            buttonImage.color = new Color(0.10f, 0.14f, 0.20f, 0.95f);
            continueButton = buttonGo.AddComponent<Button>();

            var buttonLabel = CreateLabel(buttonGo.transform, "Label", Vector2.zero, 22f, FontStyles.Bold);
            buttonLabel.text = "MAIN MENU";
            buttonLabel.color = new Color(0.62f, 0.78f, 0.95f, 0.92f);
            var labelRt = buttonLabel.rectTransform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;
        }

        /// <summary>Creates a centred TMP label with the HUD Rajdhani face when available.</summary>
        static TextMeshProUGUI CreateLabel(Transform parent, string name, Vector2 anchoredPos, float fontSize, FontStyles style)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = new Vector2(900f, 80f);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.color = Color.white;
            tmp.enableWordWrapping = true;
            TMP_FontAsset font = Resources.Load<TMP_FontAsset>("Rajdhani-SemiBold SDF");
            if (font != null)
                tmp.font = font;
            else if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
            return tmp;
        }
    }
}
