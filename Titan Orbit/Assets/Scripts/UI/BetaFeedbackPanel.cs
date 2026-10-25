using TitanOrbit.Diagnostics;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Field-notes card for a beta tester. The Escape BETA menu opens it. There is
    /// no always-on NOTES chip. Opening stamps the local ship, takes a typed note,
    /// and SENDs both to the server log. Saved rows list underneath, each with DELETE.
    /// The server stores a note only when that connection owns the beta-tester item.
    /// <para>
    /// The stamp is taken when the card opens, not on a clock. Closing and opening again
    /// takes a fresh stamp. Client presentation only — storage is
    /// <see cref="BetaFeedbackRpcClient"/> / the server log.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(66300)]
    public class BetaFeedbackPanel : MonoBehaviour
    {
        static readonly Color FillColor = new Color(0.012f, 0.016f, 0.028f, 0.96f);
        static readonly Color CaptionColor = new Color(0.62f, 0.78f, 0.95f, 0.92f);
        static readonly Color BodyColor = new Color(0.88f, 0.92f, 0.98f, 1f);
        static readonly Color AccentColor = new Color(0.35f, 0.72f, 0.95f, 0.95f);
        static readonly Color RowColor = new Color(1f, 1f, 1f, 0.04f);
        static readonly Color InputColor = new Color(0.03f, 0.05f, 0.09f, 1f);
        static readonly Color MutedColor = new Color(0.55f, 0.68f, 0.82f, 0.9f);
        static readonly Color DeleteColor = new Color(0.95f, 0.45f, 0.42f, 1f);

        /// <summary>NOTES launcher size. A short chip, not a panel — it only opens the card.</summary>
        const float LauncherWidth = 120f;

        /// <summary>Chip height. Short enough to share the top band with the speedometer.</summary>
        const float LauncherHeight = 32f;

        /// <summary>
        /// Half of the speedometer's slim width (258px in <see cref="ShipSpeedometerHUD"/>).
        /// Both widgets use a top-center anchor, so this is the meter's right edge in the
        /// same pixel space as the chip.
        /// </summary>
        const float SpeedometerHalfWidth = 129f;

        /// <summary>Clear air between the speedometer's right edge and the NOTES chip.</summary>
        const float GapRightOfSpeedometer = 16f;

        /// <summary>
        /// Same top inset as the speedometer's vertical margin, so the chip's top lines
        /// up with the meter instead of sitting on the bars.
        /// </summary>
        const float LauncherTopInset = 6f;

        /// <summary>The live panel, so the beta menu can open the card.</summary>
        public static BetaFeedbackPanel Instance { get; private set; }

        RectTransform _launcher;
        GameObject _card;
        TextMeshProUGUI _snapshot;
        TMP_InputField _note;
        TextMeshProUGUI _status;
        TextMeshProUGUI _logCaption;
        RectTransform _logContent;
        bool _visualsBuilt;
        bool _cardOpen;
        bool _sendBusy;
        int _seenListSerial = -1;
        int _seenStatusSerial = -1;
        BetaFeedbackStamp _stamp;

        /// <summary>
        /// Opens the field-notes card from the beta-tester menu.
        /// No-op on a headless server, where this panel is not created.
        /// </summary>
        public static void OpenFromBetaMenu()
        {
            if (Instance == null)
                return;
            Instance.OpenCard();
        }

        /// <summary>
        /// [UNITY] Creates the hidden driver after the first scene load. The card
        /// is built the first time a beta tester opens NOTES.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#else
            if (FindFirstObjectByType<BetaFeedbackPanel>() != null)
                return;

            var go = new GameObject(nameof(BetaFeedbackPanel));
            DontDestroyOnLoad(go);
            go.AddComponent<BetaFeedbackPanel>();
#endif
        }

        /// <summary>Clears the Escape gate so a leftover open flag cannot swallow pause next Play.</summary>
        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            BetaFeedbackHotkeyGate.NotesCardOpen = false;
            BetaFeedbackHotkeyGate.CloseRequested = false;
        }

        /// <summary>Remembers this panel so the beta menu can open the card.</summary>
        void Awake()
        {
            Instance = this;
        }

        /// <summary>
        /// Keeps an open card in sync with the server log. The top-edge chip stays
        /// hidden — NOTES is a row on the beta-tester menu.
        /// </summary>
        void LateUpdate()
        {
            if (BetaFeedbackHotkeyGate.CloseRequested)
            {
                BetaFeedbackHotkeyGate.CloseRequested = false;
                if (_cardOpen)
                    CloseCard();
            }

            if (!_visualsBuilt)
                return;

            if (_cardOpen)
                PollInbox();

            if (_launcher != null)
                _launcher.gameObject.SetActive(false);
        }

        /// <summary>Overlay canvas, the NOTES chip, and the right-side card (card starts hidden).</summary>
        void Build()
        {
            _visualsBuilt = true;

            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 160;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();

            BuildLauncher();
            BuildCard();
            _card.SetActive(false);
        }

        /// <summary>
        /// Small chip on the top edge, just right of the speedometer.
        /// Hidden while the card is open, and while the checkbox is off.
        /// Top-center used to cover the SPD / ACC bars.
        /// </summary>
        void BuildLauncher()
        {
            var go = new GameObject("NotesChip", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(transform, false);
            _launcher = go.GetComponent<RectTransform>();
            // Top-center anchor matches the speedometer, so X is pixels from screen center.
            // Pivot is the chip's own top-center, so we step half the chip past the gap.
            _launcher.anchorMin = new Vector2(0.5f, 1f);
            _launcher.anchorMax = new Vector2(0.5f, 1f);
            _launcher.pivot = new Vector2(0.5f, 1f);
            float x = SpeedometerHalfWidth + GapRightOfSpeedometer + LauncherWidth * 0.5f;
            _launcher.anchoredPosition = new Vector2(x, -LauncherTopInset);
            _launcher.sizeDelta = new Vector2(LauncherWidth, LauncherHeight);
            go.GetComponent<Image>().color = FillColor;

            var button = go.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(OpenCard);

            var accent = CreateImage(_launcher, "Accent", AccentColor, false);
            var accentRt = accent.rectTransform;
            accentRt.anchorMin = new Vector2(0f, 0f);
            accentRt.anchorMax = new Vector2(0f, 1f);
            accentRt.pivot = new Vector2(0f, 0.5f);
            accentRt.sizeDelta = new Vector2(3f, 0f);

            var label = CreateLabel(_launcher, "Label", "NOTES", 16f, CaptionColor, TextAlignmentOptions.Center);
            Stretch(label.rectTransform, 8f, 2f);
        }

        /// <summary>Right-side cockpit card: stamp, note field, SEND, and the saved log.</summary>
        void BuildCard()
        {
            _card = new GameObject("FieldNotes", typeof(RectTransform), typeof(Image));
            _card.transform.SetParent(transform, false);
            var cardRt = _card.GetComponent<RectTransform>();
            cardRt.anchorMin = new Vector2(1f, 0f);
            cardRt.anchorMax = new Vector2(1f, 1f);
            cardRt.pivot = new Vector2(1f, 0.5f);
            cardRt.anchoredPosition = new Vector2(-12f, 0f);
            cardRt.sizeDelta = new Vector2(440f, -28f);
            _card.GetComponent<Image>().color = FillColor;

            var accent = CreateImage(cardRt, "Accent", AccentColor, false);
            var accentRt = accent.rectTransform;
            accentRt.anchorMin = new Vector2(0f, 0f);
            accentRt.anchorMax = new Vector2(0f, 1f);
            accentRt.pivot = new Vector2(0f, 0.5f);
            accentRt.sizeDelta = new Vector2(3f, 0f);

            // Layout lives on an inner body so the accent rail is not stacked as a row.
            var bodyGo = new GameObject("Body", typeof(RectTransform), typeof(VerticalLayoutGroup));
            bodyGo.transform.SetParent(cardRt, false);
            var body = bodyGo.GetComponent<RectTransform>();
            Stretch(body, 0f, 0f);
            body.offsetMin = new Vector2(16f, 12f);
            body.offsetMax = new Vector2(-14f, -12f);
            var layout = bodyGo.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var header = CreateRow(body, "Header", 32f);
            var title = CreateLabel(header, "Title", "FIELD NOTES", 20f, BodyColor, TextAlignmentOptions.MidlineLeft);
            Stretch(title.rectTransform, 8f, 0f);
            title.rectTransform.offsetMax = new Vector2(-80f, 0f);
            var close = CreateButton(header, "Close", "CLOSE", CaptionColor);
            var closeRt = close.GetComponent<RectTransform>();
            closeRt.anchorMin = new Vector2(1f, 0.5f);
            closeRt.anchorMax = new Vector2(1f, 0.5f);
            closeRt.pivot = new Vector2(1f, 0.5f);
            closeRt.anchoredPosition = Vector2.zero;
            closeRt.sizeDelta = new Vector2(72f, 26f);
            close.onClick.AddListener(CloseCard);

            CreateFixedLabel(body, "StampCaption", "HULL SNAPSHOT", 13f, CaptionColor, 16f);
            _snapshot = CreateFixedLabel(body, "Stamp", "No hull yet.", 15f, BodyColor, 88f);
            _snapshot.alignment = TextAlignmentOptions.TopLeft;

            CreateFixedLabel(body, "NoteCaption", "NOTE", 13f, CaptionColor, 16f);
            _note = CreateNoteField(body);

            var send = CreateButton(CreateRow(body, "SendRow", 32f), "Send", "SEND", AccentColor);
            Stretch(send.GetComponent<RectTransform>(), 0f, 2f);
            send.onClick.AddListener(OnSend);

            _status = CreateFixedLabel(body, "Status", "Opens with a stamp of the ship you have right now.", 13f, MutedColor, 32f);
            _status.alignment = TextAlignmentOptions.TopLeft;

            _logCaption = CreateFixedLabel(body, "LogCaption", "LOG", 13f, CaptionColor, 16f);
            BuildLogScroll(body);
        }

        /// <summary>Scroll of saved notes. Flexible height so it fills the card under the form.</summary>
        void BuildLogScroll(RectTransform cardRt)
        {
            var scrollGo = new GameObject("Log", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
            scrollGo.transform.SetParent(cardRt, false);
            scrollGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);
            var scrollLe = scrollGo.AddComponent<LayoutElement>();
            scrollLe.flexibleHeight = 1f;
            scrollLe.minHeight = 120f;

            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D), typeof(Image));
            viewportGo.transform.SetParent(scrollGo.transform, false);
            Stretch(viewportGo.GetComponent<RectTransform>(), 0f, 0f);
            var viewportImage = viewportGo.GetComponent<Image>();
            viewportImage.color = new Color(1f, 1f, 1f, 0.01f);
            viewportImage.raycastTarget = true;

            var contentGo = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentGo.transform.SetParent(viewportGo.transform, false);
            _logContent = contentGo.GetComponent<RectTransform>();
            _logContent.anchorMin = new Vector2(0f, 1f);
            _logContent.anchorMax = new Vector2(1f, 1f);
            _logContent.pivot = new Vector2(0.5f, 1f);
            _logContent.anchoredPosition = Vector2.zero;
            _logContent.sizeDelta = new Vector2(0f, 0f);
            var rows = contentGo.GetComponent<VerticalLayoutGroup>();
            rows.spacing = 6f;
            rows.padding = new RectOffset(6, 6, 6, 6);
            rows.childControlWidth = true;
            rows.childControlHeight = true;
            rows.childForceExpandWidth = true;
            rows.childForceExpandHeight = false;
            var fitter = contentGo.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.viewport = viewportGo.GetComponent<RectTransform>();
            scroll.content = _logContent;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 28f;
        }

        /// <summary>
        /// Opens the card and stamps the local ship once. The typed draft is kept if they
        /// only closed the card. SEND uses this stamp, not a later hull.
        /// </summary>
        void OpenCard()
        {
            if (!_visualsBuilt)
                Build();

            _stamp = BetaFeedbackSnapshot.Capture();
            if (_snapshot != null)
                _snapshot.text = _stamp.Summary;
            _cardOpen = true;
            BetaFeedbackHotkeyGate.NotesCardOpen = true;
            _card.SetActive(true);
            _sendBusy = false;
            _seenListSerial = BetaFeedbackClientInbox.ListSerial;
            _seenStatusSerial = BetaFeedbackClientInbox.StatusSerial;
            if (_status != null)
            {
                _status.text = _stamp.HasHull
                    ? "Stamp taken from the ship you have now."
                    : "No hull yet — the stamp says so. Join a match to send.";
            }

            if (!BetaFeedbackRpcClient.RequestList() && _status != null)
                _status.text = "Join a match to load or send notes.";
        }

        /// <summary>Hides the card. The next open takes a new stamp.</summary>
        void CloseCard()
        {
            _cardOpen = false;
            BetaFeedbackHotkeyGate.NotesCardOpen = false;
            BetaFeedbackHotkeyGate.CloseRequested = false;
            _sendBusy = false;
            if (_note != null)
                _note.DeactivateInputField();
            if (_card != null)
                _card.SetActive(false);
        }

        /// <summary>SEND. The server appends the open-time stamp plus the current text.</summary>
        void OnSend()
        {
            if (_sendBusy)
                return;
            if (!BetaFeedbackRpcClient.Submit(in _stamp, _note != null ? _note.text : string.Empty))
            {
                if (_status != null)
                    _status.text = "Join a match first. Notes are stored on the server.";
                return;
            }

            _sendBusy = true;
            if (_status != null)
                _status.text = "Sending…";
        }

        /// <summary>Applies a save/delete result, then rebuilds the log when a list finishes.</summary>
        void PollInbox()
        {
            if (_seenStatusSerial != BetaFeedbackClientInbox.StatusSerial)
            {
                _seenStatusSerial = BetaFeedbackClientInbox.StatusSerial;
                _sendBusy = false;
                if (_status != null && !string.IsNullOrEmpty(BetaFeedbackClientInbox.StatusMessage))
                    _status.text = BetaFeedbackClientInbox.StatusMessage;

                byte status = BetaFeedbackClientInbox.LastStatus;
                if (status == BetaFeedbackStatus.Saved && _note != null)
                    _note.text = string.Empty;
                if (status == BetaFeedbackStatus.Saved
                    || status == BetaFeedbackStatus.Deleted
                    || status == BetaFeedbackStatus.Missing)
                    BetaFeedbackRpcClient.RequestList();
            }

            if (_seenListSerial != BetaFeedbackClientInbox.ListSerial)
            {
                _seenListSerial = BetaFeedbackClientInbox.ListSerial;
                RebuildLog();
            }
        }

        /// <summary>Rebuilds the scroll from the latest server batch. Not called every frame.</summary>
        void RebuildLog()
        {
            if (_logContent == null)
                return;

            for (int i = _logContent.childCount - 1; i >= 0; i--)
                DestroyImmediate(_logContent.GetChild(i).gameObject);

            int total = BetaFeedbackClientInbox.Total;
            int shown = BetaFeedbackClientInbox.Shown;
            if (_logCaption != null)
            {
                if (total < 0)
                    _logCaption.text = "LOG";
                else if (total > shown && shown > 0)
                    _logCaption.text = "LOG    NEWEST " + shown + " OF " + total;
                else
                    _logCaption.text = "LOG    " + total;
            }

            var rows = BetaFeedbackClientInbox.Rows;
            if (rows.Count == 0)
            {
                var empty = CreateLabel(_logContent, "Empty", "No notes yet.", 14f, MutedColor, TextAlignmentOptions.TopLeft);
                var emptyLe = empty.gameObject.AddComponent<LayoutElement>();
                emptyLe.minHeight = 28f;
                emptyLe.preferredHeight = 28f;
                return;
            }

            for (int i = 0; i < rows.Count; i++)
                CreateLogRow(rows[i]);
        }

        /// <summary>One saved note: who, hull, gun, added abilities, the text, and DELETE.</summary>
        void CreateLogRow(BetaFeedbackClientInbox.Row row)
        {
            var go = new GameObject("Note" + row.Id, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            go.transform.SetParent(_logContent, false);
            go.GetComponent<Image>().color = RowColor;
            var le = go.GetComponent<LayoutElement>();
            int extraLines = string.IsNullOrEmpty(row.Note) ? 0 : Mathf.CeilToInt(row.Note.Length / 42f);
            le.minHeight = 96f;
            le.preferredHeight = 96f + extraLines * 16f;

            var rt = go.GetComponent<RectTransform>();
            string body = "#" + row.Id + "   " + row.Player
                + "\n" + row.Ship + "    LV " + row.ShipLevel + "    BRANCH " + row.Branch
                + "\nGUN  " + row.Gun
                + "\nADDED  " + row.Abilities;
            if (!string.IsNullOrEmpty(row.Note))
                body += "\n" + row.Note;

            var label = CreateLabel(rt, "Body", body, 14f, BodyColor, TextAlignmentOptions.TopLeft);
            Stretch(label.rectTransform, 8f, 8f);
            label.rectTransform.offsetMax = new Vector2(-78f, -6f);
            label.rectTransform.offsetMin = new Vector2(8f, 6f);

            int id = row.Id;
            var delete = CreateButton(rt, "Delete", "DELETE", DeleteColor);
            var deleteRt = delete.GetComponent<RectTransform>();
            deleteRt.anchorMin = new Vector2(1f, 1f);
            deleteRt.anchorMax = new Vector2(1f, 1f);
            deleteRt.pivot = new Vector2(1f, 1f);
            deleteRt.anchoredPosition = new Vector2(-6f, -6f);
            deleteRt.sizeDelta = new Vector2(64f, 22f);
            delete.onClick.AddListener(() => OnDelete(id));
        }

        /// <summary>DELETE for one id. The server rewrites the log without that line.</summary>
        void OnDelete(int id)
        {
            if (!BetaFeedbackRpcClient.Delete(id))
            {
                if (_status != null)
                    _status.text = "Join a match first.";
                return;
            }

            if (_status != null)
                _status.text = "Removing…";
        }

        /// <summary>Multiline note field. Gameplay hotkeys ignore keys while this is focused.</summary>
        TMP_InputField CreateNoteField(RectTransform parent)
        {
            var go = new GameObject("NoteField", typeof(RectTransform), typeof(Image), typeof(TMP_InputField), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var le = go.GetComponent<LayoutElement>();
            le.minHeight = 88f;
            le.preferredHeight = 88f;
            go.GetComponent<Image>().color = InputColor;

            var input = go.GetComponent<TMP_InputField>();
            var text = CreateLabel(go.GetComponent<RectTransform>(), "Text", string.Empty, 16f, BodyColor, TextAlignmentOptions.TopLeft);
            text.richText = false;
            text.raycastTarget = false;
            Stretch(text.rectTransform, 10f, 8f);
            var placeholder = CreateLabel(go.GetComponent<RectTransform>(), "Placeholder", "What should we know?", 16f, MutedColor, TextAlignmentOptions.TopLeft);
            placeholder.fontStyle = FontStyles.Italic;
            placeholder.raycastTarget = false;
            Stretch(placeholder.rectTransform, 10f, 8f);

            input.textViewport = go.GetComponent<RectTransform>();
            input.textComponent = text;
            input.placeholder = placeholder;
            input.lineType = TMP_InputField.LineType.MultiLineNewline;
            input.characterLimit = BetaFeedbackLog.MaxNoteLength;
            input.richText = false;
            TmpInputFieldFocus.Attach(input);
            return input;
        }

        /// <summary>A fixed-height label row inside the card layout.</summary>
        TextMeshProUGUI CreateFixedLabel(RectTransform parent, string name, string text, float size, Color color, float height)
        {
            var label = CreateLabel(parent, name, text, size, color, TextAlignmentOptions.MidlineLeft);
            var le = label.gameObject.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            return label;
        }

        /// <summary>Empty layout row used for the header and the SEND button.</summary>
        static RectTransform CreateRow(RectTransform parent, string name, float height)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var le = go.GetComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            return go.GetComponent<RectTransform>();
        }

        /// <summary>Dark button with a caption. Click does not fire the ship — the image raycasts.</summary>
        static Button CreateButton(RectTransform parent, string name, string caption, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.04f, 0.08f, 0.12f, 0.95f);
            var button = go.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            var label = CreateLabel(go.GetComponent<RectTransform>(), "Caption", caption, 14f, color, TextAlignmentOptions.Center);
            Stretch(label.rectTransform, 4f, 2f);
            return button;
        }

        /// <summary>TMP label in the shared HUD font when that asset is present.</summary>
        static TextMeshProUGUI CreateLabel(
            RectTransform parent,
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
            tmp.enableWordWrapping = true;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.richText = false;
            TMP_FontAsset font = Resources.Load<TMP_FontAsset>("Rajdhani-SemiBold SDF");
            if (font != null)
                tmp.font = font;
            else if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
            return tmp;
        }

        /// <summary>Solid image child. <paramref name="raycast"/> is false for rails that must not block clicks.</summary>
        static Image CreateImage(RectTransform parent, string name, Color color, bool raycast)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = raycast;
            return image;
        }

        /// <summary>Stretches a rect to its parent with an even inset.</summary>
        static void Stretch(RectTransform rt, float x, float y)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(x, y);
            rt.offsetMax = new Vector2(-x, -y);
        }
    }
}
