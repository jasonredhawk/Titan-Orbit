using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.NetCode;
using TitanOrbit.Services;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// In-match gift card for Orbit Unlocked. The admin opens it from the Escape
    /// command card, not from a button on the flight HUD. Opening it asks once for
    /// who is online. Gift writes nothing on this machine — the server asks the
    /// checkout Worker to set the same Cloud Save flag a purchase sets.
    /// A gifted player, admin or not, sees a short line when their own unlock lands.
    /// Client presentation only. The dedicated server does not create this object.
    /// </summary>
    [DefaultExecutionOrder(66310)]
    public class OrbitUnlockedGiftPanel : MonoBehaviour
    {
        const float CardWidth = 400f;
        const float RowHeight = 36f;
        const float HeaderHeight = 44f;

        static readonly Color FillColor = new Color(0.012f, 0.016f, 0.028f, 0.96f);
        static readonly Color CaptionColor = new Color(0.62f, 0.78f, 0.95f, 0.92f);
        static readonly Color BodyColor = new Color(0.88f, 0.92f, 0.98f, 1f);
        static readonly Color AccentColor = new Color(0.35f, 0.72f, 0.95f, 0.95f);
        static readonly Color RowIdle = new Color(1f, 1f, 1f, 0.04f);
        static readonly Color ReadyColor = new Color(0.45f, 0.92f, 0.62f, 1f);
        static readonly Color MutedColor = new Color(0.45f, 0.52f, 0.62f, 0.9f);
        static readonly Color LabelOutline = new Color(0.02f, 0.04f, 0.08f, 0.95f);

        const int ToastSort = 120;
        const int RosterSort = 9700;

        Canvas _canvas;
        RectTransform _card;
        RectTransform _rows;
        TextMeshProUGUI _toast;
        TextMeshProUGUI _note;
        GameObject _mainMenuPanel;
        int _builtVersion = -1;
        float _leftMatchAt = -1f;

        /// <summary>[UNITY] One overlay for the whole app. Headless builds skip it.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            if (FindFirstObjectByType<OrbitUnlockedGiftPanel>() != null)
                return;

            var go = new GameObject(nameof(OrbitUnlockedGiftPanel));
            DontDestroyOnLoad(go);
            go.AddComponent<OrbitUnlockedGiftPanel>();
        }

        /// <summary>Builds the roster card hidden. Escape opens it. LateUpdate decides what to show.</summary>
        void Awake()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = ToastSort;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();

            _toast = CreateLabel(transform, "Toast", "", 16f, ReadyColor, TextAlignmentOptions.Center);
            var toastRt = _toast.rectTransform;
            toastRt.anchorMin = new Vector2(0.5f, 1f);
            toastRt.anchorMax = new Vector2(0.5f, 1f);
            toastRt.pivot = new Vector2(0.5f, 1f);
            toastRt.anchoredPosition = new Vector2(0f, -12f);
            toastRt.sizeDelta = new Vector2(420f, 28f);

            BuildCard();
            _card.gameObject.SetActive(false);
            _toast.gameObject.SetActive(false);
        }

        /// <summary>The Escape card asks this panel to open the roster.</summary>
        void OnEnable()
        {
            InGameEscapeMenuController.GiftChosen += OnOpenFromEscapeMenu;
        }

        /// <summary>Drops the Escape subscription if this overlay is destroyed.</summary>
        void OnDisable()
        {
            InGameEscapeMenuController.GiftChosen -= OnOpenFromEscapeMenu;
        }

        /// <summary>
        /// Keeps the roster on screen only while the Escape card is open.
        /// The toast is separate: the person who received the gift may not be an admin.
        /// </summary>
        void LateUpdate()
        {
            if (!EcsGameBridge.IsNetworkInGame())
            {
                if (_leftMatchAt < 0f)
                    _leftMatchAt = Time.unscaledTime;
                // A one-frame gap during join used to clear the admin flag
                // before the server notice arrived. Wait briefly.
                if (Time.unscaledTime - _leftMatchAt > 0.5f)
                    OrbitUnlockedGiftClientState.ResetSession();
                SetShown(false, false);
                return;
            }

            _leftMatchAt = -1f;
            // The simulation system only sets a flag. This panel owns the
            // Cloud Save read, because the account code lives outside that assembly.
            if (OrbitUnlockedGiftClientState.ConsumeBetaGift())
                TitanOrbitEntitlements.SetBetaTesterOwned(true);
            if (OrbitUnlockedGiftClientState.ConsumeCloudRefresh())
                TitanOrbitAccountEntitlementSync.RefreshForCurrentPlayerAsync();
            bool menu = IsMainMenuShowing();
            bool escapeOpen = InGameEscapeMenuController.IsOpen;
            if (!escapeOpen && _card.gameObject.activeSelf)
                _card.gameObject.SetActive(false);
            bool toast = OrbitUnlockedGiftClientState.ToastVisible() && !menu && !escapeOpen;
            bool cardOpen = escapeOpen && !menu && OrbitUnlockedGiftClientState.IsAdmin && _card.gameObject.activeSelf;
            SetShown(cardOpen, toast);
            if (toast)
                _toast.text = OrbitUnlockedGiftClientState.Toast;
            if (cardOpen && _builtVersion != OrbitUnlockedGiftClientState.Version)
                RebuildRows();
            if (_note != null)
                _note.text = OrbitUnlockedGiftClientState.LastNote ?? "";
        }

        /// <summary>
        /// Opens the roster beside the Escape command card and asks for names once.
        /// </summary>
        void OnOpenFromEscapeMenu()
        {
            if (!OrbitUnlockedGiftClientState.IsAdmin)
                return;

            _card.anchorMin = new Vector2(0.5f, 0.5f);
            _card.anchorMax = new Vector2(0.5f, 0.5f);
            _card.pivot = new Vector2(0f, 0.5f);
            _card.anchoredPosition = new Vector2(196f, 0f);
            _card.gameObject.SetActive(true);
            if (_canvas != null)
                _canvas.sortingOrder = RosterSort;
            OrbitUnlockedGiftRpcClient.RequestRoster();
            _builtVersion = -1;
        }

        void OnClose()
        {
            _card.gameObject.SetActive(false);
        }

        void OnRefresh()
        {
            OrbitUnlockedGiftRpcClient.RequestRoster();
            _builtVersion = -1;
        }

        /// <summary>Rebuilds the name rows from the latest server snapshot. Not every frame.</summary>
        void RebuildRows()
        {
            _builtVersion = OrbitUnlockedGiftClientState.Version;
            for (int i = _rows.childCount - 1; i >= 0; i--)
                DestroyImmediate(_rows.GetChild(i).gameObject);

            int count = OrbitUnlockedGiftClientState.RowCount;
            float height = HeaderHeight + 8f + Mathf.Max(1, count) * (RowHeight + 4f) + 8f;
            _card.sizeDelta = new Vector2(CardWidth, Mathf.Min(height, 420f));

            if (count == 0)
            {
                var empty = CreateLabel(_rows, "Empty", "NOBODY IN THIS MATCH", 12f, MutedColor, TextAlignmentOptions.Left);
                var emptyRt = empty.rectTransform;
                emptyRt.sizeDelta = new Vector2(CardWidth - 24f, RowHeight);
                return;
            }

            for (int i = 0; i < count; i++)
            {
                OrbitUnlockedGiftClientState.Row row = OrbitUnlockedGiftClientState.GetRow(i);
                BuildRow(row, i);
            }
        }

        /// <summary>One name plus Gift, or a sign-in note when that player is a guest.</summary>
        void BuildRow(OrbitUnlockedGiftClientState.Row row, int index)
        {
            var go = new GameObject("Row", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(_rows, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, RowHeight);
            rt.anchoredPosition = new Vector2(0f, -index * (RowHeight + 4f));
            var image = go.GetComponent<Image>();
            image.color = RowIdle;
            image.raycastTarget = false;

            var name = CreateLabel(rt, "Name", row.Name, 14f, BodyColor, TextAlignmentOptions.Left);
            var nameRt = name.rectTransform;
            nameRt.anchorMin = new Vector2(0f, 0f);
            nameRt.anchorMax = new Vector2(1f, 1f);
            nameRt.offsetMin = new Vector2(8f, 0f);
            nameRt.offsetMax = new Vector2(-156f, 0f);

            if (!row.SignedIn)
            {
                var state = CreateLabel(rt, "State", "SIGN IN", 11f, MutedColor, TextAlignmentOptions.Right);
                var stateRt = state.rectTransform;
                stateRt.anchorMin = new Vector2(1f, 0f);
                stateRt.anchorMax = new Vector2(1f, 1f);
                stateRt.pivot = new Vector2(1f, 0.5f);
                stateRt.sizeDelta = new Vector2(120f, RowHeight);
                stateRt.anchoredPosition = new Vector2(-8f, 0f);
                return;
            }

            int networkId = row.NetworkId;
            var gift = CreateButton(rt, "Gift", GiftLabel(row.Status), () => OrbitUnlockedGiftRpcClient.Gift(networkId));
            gift.anchorMin = new Vector2(1f, 0.5f);
            gift.anchorMax = new Vector2(1f, 0.5f);
            gift.pivot = new Vector2(1f, 0.5f);
            gift.sizeDelta = new Vector2(68f, 24f);
            gift.anchoredPosition = new Vector2(-78f, 0f);

            var beta = CreateButton(rt, "Beta", BetaLabel(row.BetaStatus), () => OrbitUnlockedGiftRpcClient.GiftBeta(networkId));
            beta.anchorMin = new Vector2(1f, 0.5f);
            beta.anchorMax = new Vector2(1f, 0.5f);
            beta.pivot = new Vector2(1f, 0.5f);
            beta.sizeDelta = new Vector2(68f, 24f);
            beta.anchoredPosition = new Vector2(-6f, 0f);
        }

        /// <summary>Short caption on the Orbit Unlocked button after the Worker answers.</summary>
        static string GiftLabel(byte status)
        {
            switch (status)
            {
                case OrbitUnlockedGiftServerSystem.StatusGranted:
                    return "SENT";
                case OrbitUnlockedGiftServerSystem.StatusAlready:
                    return "OWNED";
                case OrbitUnlockedGiftServerSystem.StatusBusy:
                    return "WAIT";
                case OrbitUnlockedGiftServerSystem.StatusFailed:
                    return "FAILED";
                default:
                    return "GIFT";
            }
        }

        /// <summary>Short caption on the beta-tester button after the Worker answers.</summary>
        static string BetaLabel(byte status)
        {
            switch (status)
            {
                case OrbitUnlockedGiftServerSystem.StatusGranted:
                    return "SENT";
                case OrbitUnlockedGiftServerSystem.StatusAlready:
                    return "OWNED";
                case OrbitUnlockedGiftServerSystem.StatusBusy:
                    return "WAIT";
                case OrbitUnlockedGiftServerSystem.StatusFailed:
                    return "FAILED";
                default:
                    return "BETA";
            }
        }

        void BuildCard()
        {
            var cardGo = new GameObject("Card", typeof(RectTransform), typeof(Image));
            cardGo.transform.SetParent(transform, false);
            _card = cardGo.GetComponent<RectTransform>();
            _card.anchorMin = new Vector2(0.5f, 1f);
            _card.anchorMax = new Vector2(0.5f, 1f);
            _card.pivot = new Vector2(0.5f, 1f);
            _card.anchoredPosition = new Vector2(0f, -80f);
            _card.sizeDelta = new Vector2(CardWidth, 160f);
            var cardImage = cardGo.GetComponent<Image>();
            cardImage.color = FillColor;
            cardImage.raycastTarget = true;

            var accentGo = new GameObject("Accent", typeof(RectTransform), typeof(Image));
            accentGo.transform.SetParent(_card, false);
            var accentRt = accentGo.GetComponent<RectTransform>();
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot = new Vector2(0.5f, 1f);
            accentRt.sizeDelta = new Vector2(0f, 3f);
            accentGo.GetComponent<Image>().color = AccentColor;
            accentGo.GetComponent<Image>().raycastTarget = false;

            var title = CreateLabel(_card, "Title", "ORBIT GIFT", 13f, CaptionColor, TextAlignmentOptions.Left);
            var titleRt = title.rectTransform;
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0f, 1f);
            titleRt.anchoredPosition = new Vector2(12f, -6f);
            titleRt.sizeDelta = new Vector2(140f, 16f);

            _note = CreateLabel(_card, "Note", "", 11f, ReadyColor, TextAlignmentOptions.Left);
            var noteRt = _note.rectTransform;
            noteRt.anchorMin = new Vector2(0f, 1f);
            noteRt.anchorMax = new Vector2(0f, 1f);
            noteRt.pivot = new Vector2(0f, 1f);
            noteRt.anchoredPosition = new Vector2(12f, -22f);
            noteRt.sizeDelta = new Vector2(150f, 14f);

            var close = CreateButton(_card, "Close", "CLOSE", OnClose);
            close.anchorMin = new Vector2(1f, 1f);
            close.anchorMax = new Vector2(1f, 1f);
            close.pivot = new Vector2(1f, 1f);
            close.anchoredPosition = new Vector2(-8f, -6f);
            close.sizeDelta = new Vector2(64f, 22f);

            var refresh = CreateButton(_card, "Refresh", "REFRESH", OnRefresh);
            refresh.anchorMin = new Vector2(1f, 1f);
            refresh.anchorMax = new Vector2(1f, 1f);
            refresh.pivot = new Vector2(1f, 1f);
            refresh.anchoredPosition = new Vector2(-78f, -6f);
            refresh.sizeDelta = new Vector2(78f, 22f);

            var rowsGo = new GameObject("Rows", typeof(RectTransform));
            rowsGo.transform.SetParent(_card, false);
            _rows = rowsGo.GetComponent<RectTransform>();
            _rows.anchorMin = new Vector2(0f, 0f);
            _rows.anchorMax = new Vector2(1f, 1f);
            _rows.offsetMin = new Vector2(8f, 8f);
            _rows.offsetMax = new Vector2(-8f, -HeaderHeight);
        }

        void SetShown(bool card, bool toast)
        {
            if (_canvas != null)
            {
                _canvas.enabled = card || toast;
                if (!card)
                    _canvas.sortingOrder = ToastSort;
            }
            if (_card != null && !card)
                _card.gameObject.SetActive(false);
            if (_toast != null)
                _toast.gameObject.SetActive(toast);
        }

        bool IsMainMenuShowing()
        {
            if (_mainMenuPanel == null)
                _mainMenuPanel = GameObject.Find("MainMenuPanel");
            return _mainMenuPanel != null && _mainMenuPanel.activeInHierarchy;
        }

        static RectTransform CreateButton(Transform parent, string name, string text, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = new Color(0.04f, 0.10f, 0.16f, 0.96f);
            var button = go.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(onClick);
            var label = CreateLabel(go.transform, "Label", text, 12f, BodyColor, TextAlignmentOptions.Center);
            var labelRt = label.rectTransform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;
            return go.GetComponent<RectTransform>();
        }

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
