using TitanOrbit;
using TitanOrbit.Core;
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
    /// In-match card for a beta tester. Escape → BETA opens it. Two switches
    /// put ship upgrades and gear back to free for this player only, and NOTES
    /// opens the existing field-notes card. The switch positions are saved on
    /// this device and sent again each match. Prices stay normal until the server
    /// confirms them. Client presentation only.
    /// </summary>
    [DefaultExecutionOrder(66320)]
    public class BetaTesterMenuPanel : MonoBehaviour
    {
        /// <summary>
        /// Device copy. Awake runs before Unity sign-in, so this key is what the
        /// next launch can read immediately. The account key wins once the player id exists.
        /// </summary>
        const string DeviceToggleKey = "TitanOrbit_BetaTesterToggles_v1";

        const string ToggleKeyPrefix = "TitanOrbit_BetaTesterToggles_v1_";

        static readonly Color FillColor = new Color(0.012f, 0.016f, 0.028f, 0.96f);
        static readonly Color CaptionColor = new Color(0.62f, 0.78f, 0.95f, 0.92f);
        static readonly Color BodyColor = new Color(0.88f, 0.92f, 0.98f, 1f);
        static readonly Color AccentColor = new Color(0.35f, 0.72f, 0.95f, 0.95f);
        static readonly Color ReadyColor = new Color(0.45f, 0.92f, 0.62f, 1f);
        static readonly Color MutedColor = new Color(0.55f, 0.68f, 0.82f, 0.9f);

        GameObject _card;
        TextMeshProUGUI _shipLabel;
        TextMeshProUGUI _gearLabel;
        TextMeshProUGUI _status;
        bool _open;
        int _sentNetworkId = -1;
        byte _sentFlags = 255;
        string _loadedAccountKey;

        /// <summary>[UNITY] One overlay for the whole app. Headless builds skip it.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            if (FindFirstObjectByType<BetaTesterMenuPanel>() != null)
                return;

            var go = new GameObject(nameof(BetaTesterMenuPanel));
            DontDestroyOnLoad(go);
            go.AddComponent<BetaTesterMenuPanel>();
        }

        /// <summary>Builds the card hidden and loads the saved switches.</summary>
        void Awake()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9800;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();

            LoadDeviceFlags();
            BuildCard();
            _card.SetActive(false);
        }

        void OnEnable()
        {
            InGameEscapeMenuController.BetaTesterChosen += OnOpenFromEscapeMenu;
        }

        void OnDisable()
        {
            InGameEscapeMenuController.BetaTesterChosen -= OnOpenFromEscapeMenu;
        }

        /// <summary>
        /// Sends saved switches once per match, and hides the card when Escape closes.
        /// One flag check. No ship scan.
        /// </summary>
        void LateUpdate()
        {
            // Sign-in often finishes after Awake. Pull the account's saved switches
            // before the match send, or a guest "off" would overwrite them.
            RefreshAccountFlags();

            if (!EcsGameBridge.IsNetworkInGame())
            {
                _sentNetworkId = -1;
                _sentFlags = 255;
                if (_open)
                    SetOpen(false);
                return;
            }

            MaybeSend();

            if (_open && !InGameEscapeMenuController.IsOpen)
                SetOpen(false);
            if (_open)
                RefreshLabels();
        }

        /// <summary>Escape BETA. Ignored when this account is not a beta tester.</summary>
        void OnOpenFromEscapeMenu()
        {
            if (!CanUseMenu())
                return;
            SetOpen(true);
            RefreshLabels();
        }

        void SetOpen(bool open)
        {
            _open = open;
            if (_card != null)
                _card.SetActive(open);
        }

        /// <summary>Sends the saved bits when the match id or the bits change.</summary>
        void MaybeSend()
        {
            if (!CanUseMenu())
                return;
            int networkId = EcsGameBridge.GetLocalNetworkId();
            if (networkId <= 0)
                return;

            byte flags = (byte)(BetaTesterClientFlags.RequestedFlags & BetaTesterClientFlags.Mask);
            if (_sentNetworkId == networkId && _sentFlags == flags)
                return;
            if (!BetaTesterRpcClient.SendToggles(flags))
                return;

            _sentNetworkId = networkId;
            _sentFlags = flags;
        }

        static bool CanUseMenu()
        {
            if (TitanOrbitEntitlements.IsBetaTesterOwned || BetaTesterClientFlags.VerifiedOwned)
                return true;
            return EcsGameBridge.IsLocalHost()
                && GameManager.Instance != null
                && GameManager.Instance.EnableBetaFeedback;
        }

        void ToggleShip()
        {
            Flip(BetaTesterClientFlags.FreeShipTree);
        }

        void ToggleGear()
        {
            Flip(BetaTesterClientFlags.FreeGear);
        }

        void Flip(byte bit)
        {
            byte flags = (byte)(BetaTesterClientFlags.RequestedFlags & BetaTesterClientFlags.Mask);
            if ((flags & bit) != 0)
                flags = (byte)(flags & ~bit);
            else
                flags = (byte)(flags | bit);
            BetaTesterClientFlags.RequestedFlags = flags;
            SaveFlags(flags);
            _sentFlags = 255;
            RefreshLabels();
        }

        void OpenNotes()
        {
            BetaFeedbackPanel.OpenFromBetaMenu();
        }

        void RefreshLabels()
        {
            byte flags = (byte)(BetaTesterClientFlags.RequestedFlags & BetaTesterClientFlags.Mask);
            if (_shipLabel != null)
                _shipLabel.text = (flags & BetaTesterClientFlags.FreeShipTree) != 0 ? "ON" : "OFF";
            if (_gearLabel != null)
                _gearLabel.text = (flags & BetaTesterClientFlags.FreeGear) != 0 ? "ON" : "OFF";
            if (_status == null)
                return;

            if (!BetaTesterClientFlags.SessionKnown)
                _status.text = "CONFIRMING";
            else if (!BetaTesterClientFlags.VerifiedOwned)
                _status.text = "NOT CONFIRMED";
            else if (!BetaTesterClientFlags.IsConfirmed)
                _status.text = "CONFIRMING";
            else
                _status.text = "LIVE";

            _status.color = BetaTesterClientFlags.IsConfirmed ? ReadyColor : MutedColor;
        }

        /// <summary>
        /// Restores the last switches from this device. Also checks the old
        /// guest key so a choice saved before the device key existed still returns.
        /// </summary>
        void LoadDeviceFlags()
        {
            if (PlayerPrefs.HasKey(DeviceToggleKey))
            {
                ApplyStoredFlags(ReadFlags(DeviceToggleKey));
                return;
            }

            string legacy = ToggleKeyPrefix + "local";
            if (PlayerPrefs.HasKey(legacy))
                ApplyStoredFlags(ReadFlags(legacy));
        }

        /// <summary>
        /// Once the Unity account is known, its own saved bits replace the device copy.
        /// A first sign-in with no account key keeps what this device already restored.
        /// </summary>
        void RefreshAccountFlags()
        {
            if (!UnityGameServicesBootstrap.HasDurablePlayerAccount())
                return;

            string playerId = UnityGameServicesBootstrap.PlayerId;
            if (string.IsNullOrEmpty(playerId))
                return;

            string accountKey = ToggleKeyPrefix + playerId;
            if (accountKey == _loadedAccountKey)
                return;

            _loadedAccountKey = accountKey;
            if (!PlayerPrefs.HasKey(accountKey))
            {
                SaveFlags(BetaTesterClientFlags.RequestedFlags);
                return;
            }

            byte flags = ReadFlags(accountKey);
            ApplyStoredFlags(flags);
            // Next launch reads this before sign-in, so it must match the account.
            if (!PlayerPrefs.HasKey(DeviceToggleKey) || ReadFlags(DeviceToggleKey) != flags)
            {
                PlayerPrefs.SetInt(DeviceToggleKey, flags);
                PlayerPrefs.Save();
            }
        }

        void ApplyStoredFlags(byte flags)
        {
            flags = (byte)(flags & BetaTesterClientFlags.Mask);
            if (BetaTesterClientFlags.RequestedFlags == flags)
                return;

            BetaTesterClientFlags.RequestedFlags = flags;
            _sentFlags = 255;
        }

        static byte ReadFlags(string key)
        {
            return (byte)(PlayerPrefs.GetInt(key, 0) & BetaTesterClientFlags.Mask);
        }

        /// <summary>Writes the device key, and the account key when sign-in has finished.</summary>
        void SaveFlags(byte flags)
        {
            PlayerPrefs.SetInt(DeviceToggleKey, flags);
            if (!string.IsNullOrEmpty(_loadedAccountKey))
                PlayerPrefs.SetInt(_loadedAccountKey, flags);
            PlayerPrefs.Save();
        }

        void BuildCard()
        {
            var cardGo = new GameObject("Card", typeof(RectTransform), typeof(Image));
            cardGo.transform.SetParent(transform, false);
            var card = cardGo.GetComponent<RectTransform>();
            card.anchorMin = new Vector2(0.5f, 0.5f);
            card.anchorMax = new Vector2(0.5f, 0.5f);
            card.pivot = new Vector2(1f, 0.5f);
            card.anchoredPosition = new Vector2(-196f, 0f);
            card.sizeDelta = new Vector2(320f, 248f);
            cardGo.GetComponent<Image>().color = FillColor;
            _card = cardGo;

            var accentGo = new GameObject("Accent", typeof(RectTransform), typeof(Image));
            accentGo.transform.SetParent(card, false);
            var accentRt = accentGo.GetComponent<RectTransform>();
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot = new Vector2(0.5f, 1f);
            accentRt.sizeDelta = new Vector2(0f, 3f);
            accentGo.GetComponent<Image>().color = AccentColor;
            accentGo.GetComponent<Image>().raycastTarget = false;

            var title = CreateLabel(card, "Title", "BETA TESTER", 13f, CaptionColor, TextAlignmentOptions.Left);
            var titleRt = title.rectTransform;
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0f, 1f);
            titleRt.anchoredPosition = new Vector2(12f, -10f);
            titleRt.sizeDelta = new Vector2(200f, 18f);

            _status = CreateLabel(card, "Status", "CONFIRMING", 12f, MutedColor, TextAlignmentOptions.Right);
            var statusRt = _status.rectTransform;
            statusRt.anchorMin = new Vector2(1f, 1f);
            statusRt.anchorMax = new Vector2(1f, 1f);
            statusRt.pivot = new Vector2(1f, 1f);
            statusRt.anchoredPosition = new Vector2(-12f, -10f);
            statusRt.sizeDelta = new Vector2(140f, 18f);

            CreateRow(card, "Ship", "FREE SHIP UPGRADES", -48f, out _shipLabel, ToggleShip);
            CreateRow(card, "Gear", "FREE GEAR", -100f, out _gearLabel, ToggleGear);

            var notes = CreateButton(card, "Notes", "NOTES", OpenNotes);
            notes.anchorMin = new Vector2(0.5f, 1f);
            notes.anchorMax = new Vector2(0.5f, 1f);
            notes.pivot = new Vector2(0.5f, 1f);
            notes.anchoredPosition = new Vector2(0f, -156f);
            notes.sizeDelta = new Vector2(280f, 36f);

            var hint = CreateLabel(card, "Hint", "Prices change after the server confirms.", 11f, MutedColor, TextAlignmentOptions.Center);
            var hintRt = hint.rectTransform;
            hintRt.anchorMin = new Vector2(0f, 0f);
            hintRt.anchorMax = new Vector2(1f, 0f);
            hintRt.pivot = new Vector2(0.5f, 0f);
            hintRt.anchoredPosition = new Vector2(0f, 12f);
            hintRt.sizeDelta = new Vector2(-24f, 32f);
        }

        void CreateRow(RectTransform parent, string name, string caption, float y, out TextMeshProUGUI value, UnityEngine.Events.UnityAction onClick)
        {
            var row = CreateButton(parent, name, "", onClick);
            row.anchorMin = new Vector2(0.5f, 1f);
            row.anchorMax = new Vector2(0.5f, 1f);
            row.pivot = new Vector2(0.5f, 1f);
            row.anchoredPosition = new Vector2(0f, y);
            row.sizeDelta = new Vector2(280f, 44f);

            var label = CreateLabel(row, "Caption", caption, 13f, BodyColor, TextAlignmentOptions.Left);
            var labelRt = label.rectTransform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(12f, 0f);
            labelRt.offsetMax = new Vector2(-64f, 0f);

            value = CreateLabel(row, "Value", "OFF", 13f, ReadyColor, TextAlignmentOptions.Right);
            var valueRt = value.rectTransform;
            valueRt.anchorMin = Vector2.zero;
            valueRt.anchorMax = Vector2.one;
            valueRt.offsetMin = new Vector2(12f, 0f);
            valueRt.offsetMax = new Vector2(-12f, 0f);
        }

        static RectTransform CreateButton(Transform parent, string name, string text, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.04f, 0.10f, 0.16f, 0.96f);
            var button = go.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(onClick);
            if (!string.IsNullOrEmpty(text))
            {
                var label = CreateLabel(go.transform, "Label", text, 13f, BodyColor, TextAlignmentOptions.Center);
                var labelRt = label.rectTransform;
                labelRt.anchorMin = Vector2.zero;
                labelRt.anchorMax = Vector2.one;
                labelRt.offsetMin = Vector2.zero;
                labelRt.offsetMax = Vector2.zero;
            }

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
            if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
            return tmp;
        }
    }
}
