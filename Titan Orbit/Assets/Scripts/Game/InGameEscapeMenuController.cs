using TitanOrbit.NetCode;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace TitanOrbit.Game
{
    /// <summary>
    /// Small in-match command overlay opened with Escape. Lets the player resume or leave
    /// the current session and return to the Main Menu. Client presentation only — the
    /// dedicated server has no canvas, and this component is never added on that process.
    /// <para>
    /// [TITAN-ORBIT] Multiplayer matches do not pause. The overlay is a HUD card on top of
    /// live sim; the server keeps simulating other players. Leaving calls
    /// <see cref="TitanOrbitSessionManager.ReturnToMainMenuAsync"/>, which disconnects this
    /// client (and parks a local host ServerWorld) so <see cref="NceGameFlowController"/>
    /// can show MainMenuPanel again. Paired with <c>DeathScreenController</c> for overlay
    /// chrome and <c>OrbitStationUI</c> (Escape is no longer the moon-dock toggle).
    /// </para>
    /// </summary>
    public class InGameEscapeMenuController : MonoBehaviour
    {
        /// <summary>
        /// True while any instance has the command card visible. Other HUD (orbit suppressor)
        /// reads this so it does not alpha-zero this overlay.
        /// </summary>
        public static bool IsOpen { get; private set; }

        /// <summary>
        /// True after Awake succeeded on a client-presentation process.
        /// OrbitStationUI uses this to yield Escape instead of toggling the dock.
        /// </summary>
        public static bool IsAvailable { get; private set; }

        /// <summary>Full-screen canvas we show/hide. Built at runtime if the Inspector is empty.</summary>
        [SerializeField] GameObject overlayRoot;

        /// <summary>True while a Main Menu leave is in flight — blocks a second click / Escape spam.</summary>
        bool _leaving;

        /// <summary>Cached Join Game overlay on the same NceGameRoot (null if missing).</summary>
        JoinGameBrowserController _joinBrowser;

        /// <summary>Cached flow controller on the same NceGameRoot (null if missing).</summary>
        NceGameFlowController _flow;

        /// <summary>Last frame we consumed Escape so we do not reopen on the same press that closed us.</summary>
        int _ignoreEscapeUntilFrame = -1;

        // --- Palette: same void glass as DeathScreen / ShipStatTooltipChrome ---
        static readonly Color DimColor = new Color(0.01f, 0.015f, 0.03f, 0.55f);
        static readonly Color FillColor = new Color(0.012f, 0.016f, 0.028f, 0.96f);
        static readonly Color CaptionPlateColor = new Color(0.018f, 0.028f, 0.045f, 1f);
        static readonly Color CaptionTextColor = new Color(0.62f, 0.78f, 0.95f, 0.92f);
        static readonly Color BodyTextColor = new Color(0.88f, 0.92f, 0.98f, 1f);
        static readonly Color AccentColor = new Color(0.42f, 0.78f, 0.98f, 0.95f);
        static readonly Color AccentDim = new Color(0.42f, 0.78f, 0.98f, 0.40f);
        static readonly Color BracketColor = new Color(0.38f, 0.55f, 0.70f, 0.70f);
        static readonly Color FrameTint = new Color(0.18f, 0.28f, 0.40f, 0.45f);

        /// <summary>
        /// [UNITY] Runs when the component wakes on NceGameRoot. Dedicated-server processes
        /// disable immediately. Client builds the card once and keep it hidden until Escape.
        /// </summary>
        void Awake()
        {
#if UNITY_SERVER
            enabled = false;
            return;
#endif
            if (TitanOrbitDedicatedServerAutoBoot.IsDedicatedServerProcess())
            {
                enabled = false;
                return;
            }

            IsAvailable = true;
            _joinBrowser = GetComponent<JoinGameBrowserController>();
            _flow = GetComponent<NceGameFlowController>();
            EnsureUi();
            Hide();
        }

        /// <summary>[UNITY] Clears the static flags if this is the live instance.</summary>
        void OnDestroy()
        {
            if (IsAvailable)
                IsAvailable = false;
            if (IsOpen)
                IsOpen = false;
        }

        /// <summary>
        /// Per-frame presentation tick. Escape toggles the card while we are in a match
        /// (or connecting / team-pick). Hidden automatically once the session is gone so a
        /// leftover overlay cannot sit on the Main Menu.
        /// </summary>
        void Update()
        {
            // --- Session guard ---
            // [HYBRID] EcsGameBridge / session flags are the GameObject window into NetCode.
            // Leave stays latched until NetworkId / InGame are actually gone, otherwise
            // Escape could reopen the card on the first frame of the disconnect flush.
            if (_leaving)
            {
                if (IsOpen)
                    Hide();
                if (!EcsGameBridge.IsNetworkInGame() &&
                    !TitanOrbitSessionManager.IsJoinConnecting &&
                    !EcsGameBridge.HasClientNetworkId())
                    _leaving = false;
                return;
            }

            if (!CanShowMenu())
            {
                if (IsOpen)
                    Hide();
                return;
            }

            if (!WasEscapePressedThisFrame())
                return;

            // Same-frame close from Resume / backdrop must not reopen.
            if (Time.frameCount <= _ignoreEscapeUntilFrame)
                return;

            if (IsOpen)
                Hide();
            else
                Show();
        }

        /// <summary>
        /// True when this client is past the Main Menu / Join Game browser and Escape
        /// should open the command card (loading, team pick, flight, death, match end).
        /// </summary>
        bool CanShowMenu()
        {
            if (_leaving)
                return false;

            // Join Game has its own Back control — do not steal Escape there.
            if (_joinBrowser != null && _joinBrowser.IsVisible)
                return false;

            // Main Menu: no NetworkStreamInGame, no join-in-flight, no NetworkId yet.
            return EcsGameBridge.IsNetworkInGame() ||
                   TitanOrbitSessionManager.IsJoinConnecting ||
                   EcsGameBridge.HasClientNetworkId();
        }

        /// <summary>Makes the dim + card visible. Builds UI on first show if Awake was skipped.</summary>
        void Show()
        {
            EnsureUi();
            if (overlayRoot != null)
                overlayRoot.SetActive(true);
            IsOpen = true;
        }

        /// <summary>Hides the overlay and publishes <see cref="IsOpen"/> false.</summary>
        void Hide()
        {
            if (overlayRoot != null)
                overlayRoot.SetActive(false);
            IsOpen = false;
            _ignoreEscapeUntilFrame = Time.frameCount;
        }

        /// <summary>
        /// Resume button / dim-backdrop click. Closes the card so the player keeps flying.
        /// Does not send RPCs and does not change session state.
        /// </summary>
        void OnResumeClicked()
        {
            Hide();
        }

        /// <summary>
        /// Main Menu button. Disconnects this client (and parks a local host) then lets
        /// <see cref="NceGameFlowController"/> show the menu once <c>NetworkStreamInGame</c> drops.
        /// </summary>
        async void OnMainMenuClicked()
        {
            if (_leaving)
                return;

            _leaving = true;
            Hide();

            // --- Reset flow latches so Play / Join work on the next menu visit ---
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
                    Debug.LogError("[InGameEscapeMenu] Leave failed: " + ex.Message);
                    _leaving = false;
                }
            }
            else
                _leaving = false;
        }

        /// <summary>
        /// Builds the centred command card once. Recreates if a hot-reload leftover is found.
        /// Screen Space Overlay + GraphicRaycaster so the two buttons receive clicks.
        /// </summary>
        void EnsureUi()
        {
            if (overlayRoot != null)
                return;

            Transform existing = transform.Find("EscapeMenuOverlay");
            if (existing != null)
                Destroy(existing.gameObject);

            // [UNITY] Overlay canvas — sorting above death (8500) and match-end (9000).
            var canvasGo = new GameObject("EscapeMenuOverlay");
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9600;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();
            overlayRoot = canvasGo;

            // --- Dim backdrop (click = resume) ---
            Image dim = CreateChildImage(canvasGo.transform, "Dim", stretch: true);
            dim.color = DimColor;
            dim.raycastTarget = true;
            var dimButton = dim.gameObject.AddComponent<Button>();
            dimButton.transition = Selectable.Transition.None;
            dimButton.onClick.AddListener(OnResumeClicked);

            // --- Compact command card ---
            const float cardW = 360f;
            const float cardH = 236f;
            var cardGo = new GameObject("CommandCard");
            cardGo.transform.SetParent(canvasGo.transform, false);
            var cardRt = cardGo.AddComponent<RectTransform>();
            cardRt.anchorMin = new Vector2(0.5f, 0.5f);
            cardRt.anchorMax = new Vector2(0.5f, 0.5f);
            cardRt.pivot = new Vector2(0.5f, 0.5f);
            cardRt.anchoredPosition = Vector2.zero;
            cardRt.sizeDelta = new Vector2(cardW, cardH);

            Image fill = CreateChildImage(cardGo.transform, "Fill", stretch: true);
            fill.color = FillColor;
            fill.raycastTarget = true;

            var outline = cardGo.AddComponent<Outline>();
            outline.effectColor = FrameTint;
            outline.effectDistance = new Vector2(1.2f, -1.2f);

            Image accent = CreateChildImage(cardGo.transform, "Accent", stretch: false);
            RectTransform accentRt = accent.rectTransform;
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot = new Vector2(0.5f, 1f);
            accentRt.anchoredPosition = Vector2.zero;
            accentRt.sizeDelta = new Vector2(-16f, 2.5f);
            accent.color = AccentColor;
            accent.raycastTarget = false;

            // Caption rail — COMMAND (left) + ESC (right) so the keybinding is visible.
            GameObject captionGo = new GameObject("CaptionBar");
            captionGo.transform.SetParent(cardGo.transform, false);
            RectTransform captionRt = captionGo.AddComponent<RectTransform>();
            captionRt.anchorMin = new Vector2(0f, 1f);
            captionRt.anchorMax = new Vector2(1f, 1f);
            captionRt.pivot = new Vector2(0.5f, 1f);
            captionRt.anchoredPosition = new Vector2(0f, -10f);
            captionRt.sizeDelta = new Vector2(-20f, 18f);

            Image captionBg = captionGo.AddComponent<Image>();
            captionBg.color = CaptionPlateColor;
            captionBg.raycastTarget = false;

            TextMeshProUGUI caption = CreateLabel(
                captionGo.transform, "Caption", "COMMAND", 11f, CaptionTextColor, TextAlignmentOptions.MidlineLeft);
            caption.fontStyle = FontStyles.Bold;
            caption.characterSpacing = 3f;
            Stretch(caption.rectTransform, 10f, 1f, 80f, 1f);

            TextMeshProUGUI escHint = CreateLabel(
                captionGo.transform, "EscHint", "ESC", 10f, CaptionTextColor, TextAlignmentOptions.MidlineRight);
            escHint.characterSpacing = 2.2f;
            Stretch(escHint.rectTransform, 200f, 1f, 8f, 1f);

            Image sep = CreateChildImage(cardGo.transform, "CaptionSeparator", stretch: false);
            RectTransform sepRt = sep.rectTransform;
            sepRt.anchorMin = new Vector2(0f, 1f);
            sepRt.anchorMax = new Vector2(1f, 1f);
            sepRt.pivot = new Vector2(0.5f, 1f);
            sepRt.anchoredPosition = new Vector2(0f, -30f);
            sepRt.sizeDelta = new Vector2(-28f, 1f);
            sep.color = AccentDim;
            sep.raycastTarget = false;

            TextMeshProUGUI title = CreateLabel(
                cardGo.transform, "Title", "SESSION", 22f, BodyTextColor, TextAlignmentOptions.Center);
            title.fontStyle = FontStyles.Bold;
            title.characterSpacing = 2.4f;
            RectTransform titleRt = title.rectTransform;
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.anchoredPosition = new Vector2(0f, -48f);
            titleRt.sizeDelta = new Vector2(-24f, 28f);

            TextMeshProUGUI body = CreateLabel(
                cardGo.transform,
                "Body",
                "Leave this match and return to the Main Menu.",
                13f,
                CaptionTextColor,
                TextAlignmentOptions.Center);
            body.enableWordWrapping = true;
            body.overflowMode = TextOverflowModes.Overflow;
            RectTransform bodyRt = body.rectTransform;
            bodyRt.anchorMin = new Vector2(0f, 1f);
            bodyRt.anchorMax = new Vector2(1f, 1f);
            bodyRt.pivot = new Vector2(0.5f, 1f);
            bodyRt.anchoredPosition = new Vector2(0f, -78f);
            bodyRt.sizeDelta = new Vector2(-36f, 36f);

            // --- Actions ---
            CreateMenuButton(cardGo.transform, "ResumeButton", "RESUME", new Vector2(0f, -128f), OnResumeClicked);
            CreateMenuButton(cardGo.transform, "MainMenuButton", "MAIN MENU", new Vector2(0f, -184f), OnMainMenuClicked);

            const float bracket = 10f;
            const float thick = 1.4f;
            const float inset = 4f;
            CreateCornerBracket(cardGo.transform, "BracketTL", new Vector2(0f, 1f),
                new Vector2(inset, -inset), bracket, thick, openRight: true, openDown: true);
            CreateCornerBracket(cardGo.transform, "BracketTR", new Vector2(1f, 1f),
                new Vector2(-inset, -inset), bracket, thick, openRight: false, openDown: true);
            CreateCornerBracket(cardGo.transform, "BracketBL", new Vector2(0f, 0f),
                new Vector2(inset, inset), bracket, thick, openRight: true, openDown: false);
            CreateCornerBracket(cardGo.transform, "BracketBR", new Vector2(1f, 0f),
                new Vector2(-inset, inset), bracket, thick, openRight: false, openDown: false);
        }

        /// <summary>
        /// Builds a Cut-Frame menu button matching Main Menu Play, then wires the click.
        /// </summary>
        /// <param name="parent">Command card transform.</param>
        /// <param name="name">Child GameObject name.</param>
        /// <param name="label">Uppercase caption the player reads.</param>
        /// <param name="anchoredPos">Centre-card offset (Y is negative downward).</param>
        /// <param name="onClick">Handler for the UGUI Button.</param>
        static void CreateMenuButton(
            Transform parent,
            string name,
            string label,
            Vector2 anchoredPos,
            UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = new Vector2(260f, 44f);

            MainMenuPresenter.StyleGameObjectAsMenuButton(go, label, 44f, 260f);

            var button = go.GetComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(onClick);
        }

        /// <summary>True on the frame the player presses Escape (new Input System, else legacy).</summary>
        static bool WasEscapePressedThisFrame()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
#else
            return UnityEngine.Input.GetKeyDown(KeyCode.Escape);
#endif
        }

        /// <summary>Creates a full-stretch or empty-rect child <see cref="Image"/>.</summary>
        static Image CreateChildImage(Transform parent, string name, bool stretch)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            if (stretch)
            {
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
            }

            return go.AddComponent<Image>();
        }

        /// <summary>Builds a TMP label with the HUD Rajdhani face when available.</summary>
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
            ApplyHudFont(tmp);
            return tmp;
        }

        /// <summary>Prefers Shift Rajdhani so this card matches other HUD chrome.</summary>
        static void ApplyHudFont(TextMeshProUGUI tmp)
        {
            if (tmp == null)
                return;

            TMP_FontAsset font = Resources.Load<TMP_FontAsset>("Rajdhani-SemiBold SDF");
            if (font != null)
                tmp.font = font;
            else if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
        }

        /// <summary>Insets a stretched rect (left, bottom, right, top) in canvas units.</summary>
        static void Stretch(RectTransform rt, float left, float bottom, float right, float top)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>
        /// Two thin bars forming an L at a corner — targeting-HUD motif shared with death plaque.
        /// </summary>
        static void CreateCornerBracket(
            Transform parent,
            string name,
            Vector2 corner,
            Vector2 anchoredPos,
            float armLength,
            float thickness,
            bool openRight,
            bool openDown)
        {
            var holder = new GameObject(name);
            holder.transform.SetParent(parent, false);
            var holderRt = holder.AddComponent<RectTransform>();
            holderRt.anchorMin = corner;
            holderRt.anchorMax = corner;
            holderRt.pivot = corner;
            holderRt.anchoredPosition = anchoredPos;
            holderRt.sizeDelta = new Vector2(armLength, armLength);

            Image h = CreateChildImage(holder.transform, "H", stretch: false);
            RectTransform hRt = h.rectTransform;
            hRt.pivot = new Vector2(openRight ? 0f : 1f, openDown ? 1f : 0f);
            hRt.anchorMin = hRt.pivot;
            hRt.anchorMax = hRt.pivot;
            hRt.anchoredPosition = Vector2.zero;
            hRt.sizeDelta = new Vector2(armLength, thickness);
            h.color = BracketColor;
            h.raycastTarget = false;

            Image v = CreateChildImage(holder.transform, "V", stretch: false);
            RectTransform vRt = v.rectTransform;
            vRt.pivot = new Vector2(openRight ? 0f : 1f, openDown ? 1f : 0f);
            vRt.anchorMin = vRt.pivot;
            vRt.anchorMax = vRt.pivot;
            vRt.anchoredPosition = Vector2.zero;
            vRt.sizeDelta = new Vector2(thickness, armLength);
            v.color = BracketColor;
            v.raycastTarget = false;
        }
    }
}
