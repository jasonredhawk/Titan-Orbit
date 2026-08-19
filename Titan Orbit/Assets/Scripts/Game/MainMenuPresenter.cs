using TitanOrbit.Data;
using TitanOrbit.NetCode;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Builds and refreshes the Main Menu visual layout at runtime: logo hero, account strip,
    /// player name field, stacked action buttons, and status line. Client-only [HYBRID]
    /// presentation — does not touch ECS ship sim. Called from <see cref="NceGameFlowController"/>
    /// and <see cref="MainMenuUiBootstrap"/>.
    ///
    /// Layout contract:
    ///   Sign in (top-right) · Logo (centered, below top edge) · Player name label+input (tight) ·
    ///   Play / Join / Local client (lower) · Status
    /// Local host is omitted because Play already starts local host when local options are enabled.
    /// </summary>
    public static class MainMenuPresenter
    {
        /// <summary>Resources path (no extension) for the brand sprite with true alpha.</summary>
        public const string LogoResourcesPath = "UI/Branding/TitanOrbitLogo";

        /// <summary>Child name for the hero logo Image under MainMenuPanel.</summary>
        public const string LogoObjectName = "Logo";

        /// <summary>Child name for the Unity account strip (Sign in / Sign out only).</summary>
        public const string AccountBarObjectName = "AccountBar";

        /// <summary>Child name for the vertical button column (Play is reparented here).</summary>
        public const string ButtonStackObjectName = "ButtonStack";

        /// <summary>Child name for the player-name label.</summary>
        public const string PlayerNameLabelObjectName = "PlayerNameLabel";

        /// <summary>Child name for the player-name TMP_InputField.</summary>
        public const string PlayerNameInputObjectName = "PlayerNameInput";

        /// <summary>Child name for the collapsed profile-badge chip.</summary>
        public const string PlayerBadgePickerObjectName = MainMenuBadgePicker.RootObjectName;

        /// <summary>Soft status text color.</summary>
        static readonly Color StatusColor = new Color(0.72f, 0.84f, 0.96f, 0.92f);

        /// <summary>
        /// Player-name field fill — semi-transparent black so it reads as a text box, not a button.
        /// </summary>
        static readonly Color InputFill = new Color(0f, 0f, 0f, 0.55f);

        /// <summary>
        /// Visual template copied from the scene Play button (Shift Cut Frame) and applied to
        /// Join / Local client / Sign in so the whole menu shares one button look.
        /// </summary>
        struct MenuButtonStyle
        {
            public Sprite Sprite;
            public Image.Type ImageType;
            public float PixelsPerUnitMultiplier;
            public Color ImageColor;
            public ColorBlock Colors;
            public bool HasSprite;
        }

        /// <summary>
        /// Applies the full main-menu visual refresh once the panel exists.
        /// Safe to call multiple times — finds-or-creates children and rewires listeners.
        /// </summary>
        /// <param name="panel">MainMenuPanel root (full-screen).</param>
        /// <param name="playButton">Primary Play / Local play / Quick join button (scene-authored).</param>
        /// <param name="onJoinGame">Handler for Join game.</param>
        /// <param name="onLocalClient">Handler for Local client (dev / MPPM only).</param>
        /// <param name="statusText">Optional out: status TMP created or found.</param>
        public static void Apply(
            GameObject panel,
            Button playButton,
            UnityEngine.Events.UnityAction onJoinGame,
            UnityEngine.Events.UnityAction onLocalClient,
            out TextMeshProUGUI statusText)
        {
            statusText = null;
            if (panel == null)
                return;

            // --- Panel backdrop ---
            // [TITAN-ORBIT] Clear Placeholder HUD BG so SpaceBackground shows through.
            // Sprite left null — designer can assign a new space art later.
            EnsureTransparentPanelBackdrop(panel);

            // Play already covers "Local host" when ShowLocalPlayOptions is on.
            DestroyChildIfPresent(panel.transform, "LocalHostButton");

            // --- Hide text title (logo already says TITAN ORBIT) ---
            var title = panel.transform.Find("Title");
            if (title != null)
                title.gameObject.SetActive(false);

            // Capture Play's Shift Cut Frame look before we rebuild the rest of the menu.
            MenuButtonStyle buttonStyle = CaptureButtonStyle(playButton);

            // --- Hero logo (transparent PNG from Resources) ---
            EnsureLogo(panel.transform);

            // --- Account strip: Sign in / Sign out (same style as Play) ---
            EnsureAccountBar(panel.transform, buttonStyle);

            // --- Player display name (persisted via LocalPlayerDisplayName) ---
            EnsurePlayerNameField(panel.transform);

            // --- Profile badge chip under the name field (pixel gap, not a second screen-fraction) ---
            EnsurePlayerBadgePicker(panel.transform);

            // --- Button column ---
            var stack = EnsureButtonStack(panel.transform);

            if (playButton != null)
            {
                // Reparent Play into the stack so spacing is consistent with code-built buttons.
                EnsureInStack(playButton.transform, stack, 0);
                StyleButton(playButton.gameObject, GetPlayLabel(), buttonStyle, 48f, 340f);
            }

            CreateOrWireStackButton(
                stack,
                "BrowseGamesButton",
                "Join game",
                buttonStyle,
                onJoinGame,
                playButton != null ? 1 : 0);

            if (TitanOrbitMultiplayerConfig.ShowLocalPlayOptions)
            {
                // Local client is for MPPM / LAN second window — keep it; Local host is redundant with Play.
                CreateOrWireStackButton(
                    stack,
                    "LocalClientButton",
                    "Local client",
                    buttonStyle,
                    onLocalClient,
                    -1);
            }
            else
            {
                DestroyChildIfPresent(panel.transform, "LocalClientButton");
                DestroyChildIfPresent(stack, "LocalClientButton");
            }

            LayoutStack(stack, ComputeStackYBelowBadge(panel.transform));

            // --- Status line under the stack (must run after LayoutStack so height is known) ---
            statusText = EnsureStatusTextBelowStack(panel.transform, stack);
        }

        /// <summary>
        /// Reads sprite / sliced type / tint / ColorBlock from the scene Play button.
        /// Falls back to the known Cut Frame colors when Play is missing.
        /// </summary>
        static MenuButtonStyle CaptureButtonStyle(Button playButton)
        {
            // --- Default matches SampleScene PlayButton (Shift Cut Frame Filled) ---
            var style = new MenuButtonStyle
            {
                Sprite = null,
                ImageType = Image.Type.Sliced,
                PixelsPerUnitMultiplier = 1f,
                ImageColor = new Color(0.22f, 0.33f, 0.42f, 0.75f),
                HasSprite = false,
                Colors = new ColorBlock
                {
                    normalColor = new Color(0.22f, 0.33f, 0.42f, 0.75f),
                    highlightedColor = new Color(0.28f, 0.40f, 0.50f, 0.85f),
                    pressedColor = new Color(0.17f, 0.27f, 0.36f, 0.90f),
                    selectedColor = new Color(0.22f, 0.33f, 0.42f, 0.75f),
                    disabledColor = new Color(0.20f, 0.25f, 0.35f, 0.70f),
                    colorMultiplier = 1f,
                    fadeDuration = 0.1f,
                },
            };

            if (playButton == null)
                return style;

            var image = playButton.GetComponent<Image>();
            if (image != null)
            {
                style.Sprite = image.sprite;
                style.ImageType = image.type;
                style.PixelsPerUnitMultiplier = image.pixelsPerUnitMultiplier;
                style.ImageColor = image.color;
                style.HasSprite = image.sprite != null;
            }

            style.Colors = playButton.colors;
            return style;
        }

        /// <summary>Play button caption depends on local-dev vs dedicated Quick join mode.</summary>
        static string GetPlayLabel()
        {
            if (TitanOrbitPlayModeUtility.IsMppmAdditionalEditorInstance())
                return "Local client";
            return TitanOrbitMultiplayerConfig.ShowLocalPlayOptions ? "Local play" : "Quick join";
        }

        /// <summary>
        /// Clears the panel Image sprite and drops opacity so the scrolling nebula reads through.
        /// </summary>
        static void EnsureTransparentPanelBackdrop(GameObject panel)
        {
            var image = panel.GetComponent<Image>();
            if (image == null)
                return;

            // [UNITY] Null sprite = empty placeholder; tint nearly clear so SpaceBackground is visible.
            image.sprite = null;
            image.color = new Color(0.02f, 0.04f, 0.08f, 0.35f);
            image.raycastTarget = true; // keep blocking world clicks under the menu
        }

        /// <summary>
        /// Creates or refreshes the brand logo Image. Always loads the Resources sprite so we use
        /// the alpha-keyed PNG (the Art copy can stay locked by the Editor importer).
        /// </summary>
        static void EnsureLogo(Transform panel)
        {
            Transform existing = panel.Find(LogoObjectName);
            GameObject go = existing != null
                ? existing.gameObject
                : new GameObject(LogoObjectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));

            if (existing == null)
                go.transform.SetParent(panel, false);

            go.transform.SetSiblingIndex(0);

            var rt = go.GetComponent<RectTransform>();
            // Hero brand — nudged down from the top so it is not flush with the screen edge.
            rt.anchorMin = new Vector2(0.5f, 0.58f);
            rt.anchorMax = new Vector2(0.5f, 0.88f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(720f, 300f);
            rt.anchoredPosition = Vector2.zero;
            rt.localScale = Vector3.one;

            var image = go.GetComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;
            // [UNITY] White tint + null material = Default UI shader multiplies sprite RGBA as-is.
            image.color = Color.white;
            image.material = null;

            // [TITAN-ORBIT] Prefer Resources copy — it has true transparency after black key-out.
            var fromResources = Resources.Load<Sprite>(LogoResourcesPath);
            if (fromResources != null)
                image.sprite = fromResources;

            go.SetActive(true);
        }

        /// <summary>
        /// Compact Sign in / Sign out button in the top-right — uses the same Cut Frame style as Play.
        /// </summary>
        static void EnsureAccountBar(Transform panel, MenuButtonStyle buttonStyle)
        {
            Transform existing = panel.Find(AccountBarObjectName);
            GameObject barGo = existing != null
                ? existing.gameObject
                : new GameObject(AccountBarObjectName, typeof(RectTransform));

            if (existing == null)
                barGo.transform.SetParent(panel, false);

            // Remove old navy strip background + Guest status label from earlier layout.
            var oldBg = barGo.GetComponent<Image>();
            if (oldBg != null)
            {
                if (Application.isPlaying)
                    Object.Destroy(oldBg);
                else
                    Object.DestroyImmediate(oldBg);
            }

            DestroyChildIfPresent(barGo.transform, "AccountStatus");

            var barRt = barGo.GetComponent<RectTransform>();
            // Top-right corner — Sign in / Sign out stays out of the logo / name column.
            barRt.anchorMin = new Vector2(1f, 1f);
            barRt.anchorMax = new Vector2(1f, 1f);
            barRt.pivot = new Vector2(1f, 1f);
            barRt.sizeDelta = new Vector2(240f, 44f);
            barRt.anchoredPosition = new Vector2(-24f, -24f);

            // --- Single action button filling the bar ---
            Transform btnTf = barGo.transform.Find("AccountActionButton");
            GameObject btnGo = btnTf != null
                ? btnTf.gameObject
                : new GameObject("AccountActionButton", typeof(RectTransform), typeof(Image), typeof(Button));
            if (btnTf == null)
                btnGo.transform.SetParent(barGo.transform, false);

            var btnRt = btnGo.GetComponent<RectTransform>();
            btnRt.anchorMin = Vector2.zero;
            btnRt.anchorMax = Vector2.one;
            btnRt.offsetMin = Vector2.zero;
            btnRt.offsetMax = Vector2.zero;
            btnRt.pivot = new Vector2(0.5f, 0.5f);

            // Reuse Play's sliced Cut Frame sprite + ColorBlock (not a flat navy fill).
            StyleButton(btnGo, "Sign in with Unity", buttonStyle, 44f, 240f);

            var btnLabel = btnGo.GetComponentInChildren<TextMeshProUGUI>(true);
            if (btnLabel != null)
                btnLabel.fontSize = 17f;

            var accountBar = barGo.GetComponent<MainMenuAccountBar>();
            if (accountBar == null)
                accountBar = barGo.AddComponent<MainMenuAccountBar>();

            // statusLabel omitted on purpose — Configure(null, …) keeps the button-only UI.
            accountBar.Configure(null, btnGo.GetComponent<Button>(), btnLabel);
            barGo.SetActive(true);
        }

        /// <summary>
        /// Player name label + TMP input. Restores scene orphans when present; otherwise builds them.
        /// Saves to <see cref="LocalPlayerDisplayName"/> on every edit.
        /// </summary>
        static void EnsurePlayerNameField(Transform panel)
        {
            // --- Label ---
            Transform labelTf = panel.Find(PlayerNameLabelObjectName);
            GameObject labelGo = labelTf != null
                ? labelTf.gameObject
                : new GameObject(PlayerNameLabelObjectName, typeof(RectTransform), typeof(TextMeshProUGUI));
            if (labelTf == null)
                labelGo.transform.SetParent(panel, false);

            var labelRt = labelGo.GetComponent<RectTransform>();
            // Tight stack with the input — small gap under the label.
            labelRt.anchorMin = new Vector2(0.5f, 0.50f);
            labelRt.anchorMax = new Vector2(0.5f, 0.50f);
            labelRt.pivot = new Vector2(0.5f, 0.5f);
            labelRt.sizeDelta = new Vector2(480f, 28f);
            labelRt.anchoredPosition = Vector2.zero;

            var labelTmp = labelGo.GetComponent<TextMeshProUGUI>();
            labelTmp.text = "Player name";
            labelTmp.fontSize = 20f;
            labelTmp.alignment = TextAlignmentOptions.Center;
            labelTmp.color = StatusColor;
            labelTmp.raycastTarget = false;
            labelGo.SetActive(true);

            // --- Input field ---
            Transform inputTf = panel.Find(PlayerNameInputObjectName);
            GameObject inputGo = inputTf != null
                ? inputTf.gameObject
                : new GameObject(
                    PlayerNameInputObjectName,
                    typeof(RectTransform),
                    typeof(Image),
                    typeof(TMP_InputField));
            if (inputTf == null)
                inputGo.transform.SetParent(panel, false);

            var inputRt = inputGo.GetComponent<RectTransform>();
            // Just below the label (anchors ~0.50 → 0.42) for a tight vertical gap.
            inputRt.anchorMin = new Vector2(0.5f, 0.42f);
            inputRt.anchorMax = new Vector2(0.5f, 0.42f);
            inputRt.pivot = new Vector2(0.5f, 0.5f);
            // Twice the previous field size (was 420×56).
            inputRt.sizeDelta = new Vector2(840f, 112f);
            inputRt.anchoredPosition = Vector2.zero;

            var inputBg = inputGo.GetComponent<Image>();
            // Semi-transparent black plate — clearly an input, not a Cut Frame button.
            inputBg.sprite = null;
            inputBg.type = Image.Type.Simple;
            inputBg.color = InputFill;
            inputBg.raycastTarget = true;

            var input = inputGo.GetComponent<TMP_InputField>();
            if (input == null)
                input = inputGo.AddComponent<TMP_InputField>();

            // Text area (typed characters) — middle-center so the name sits in the field.
            var textTmp = EnsureChildTmp(inputGo.transform, "Text", TextAlignmentOptions.Center);
            var textRt = textTmp.rectTransform;
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(28f, 16f);
            textRt.offsetMax = new Vector2(-28f, -16f);
            // Fill most of the 112px-tall field — auto-size so typed text matches the box.
            textTmp.enableAutoSizing = true;
            textTmp.fontSizeMin = 40f;
            textTmp.fontSizeMax = 56f;
            textTmp.fontSize = 52f;
            textTmp.alignment = TextAlignmentOptions.Center;
            textTmp.color = Color.white;
            textTmp.raycastTarget = false;

            // Placeholder — same size / center alignment so it matches typed text.
            var placeholderTmp = EnsureChildTmp(inputGo.transform, "Placeholder", TextAlignmentOptions.Center);
            var phRt = placeholderTmp.rectTransform;
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(28f, 16f);
            phRt.offsetMax = new Vector2(-28f, -16f);
            placeholderTmp.enableAutoSizing = true;
            placeholderTmp.fontSizeMin = 40f;
            placeholderTmp.fontSizeMax = 56f;
            placeholderTmp.fontSize = 52f;
            placeholderTmp.alignment = TextAlignmentOptions.Center;
            placeholderTmp.fontStyle = FontStyles.Italic;
            placeholderTmp.color = new Color(0.65f, 0.72f, 0.82f, 0.55f);
            placeholderTmp.text = "Enter your name";
            placeholderTmp.raycastTarget = false;

            input.textViewport = inputRt;
            input.textComponent = textTmp;
            input.placeholder = placeholderTmp;
            input.characterLimit = LocalPlayerDisplayName.MaxLength;
            input.contentType = TMP_InputField.ContentType.Standard;
            input.lineType = TMP_InputField.LineType.SingleLine;

            // Restore saved name (or Pilot default).
            string saved = LocalPlayerDisplayName.Get();
            input.SetTextWithoutNotify(saved);
            textTmp.text = saved;

            // Persist on every change so Play / Join always sees the latest name.
            input.onValueChanged.RemoveAllListeners();
            input.onValueChanged.AddListener(LocalPlayerDisplayName.Set);
            input.onEndEdit.RemoveAllListeners();
            input.onEndEdit.AddListener(LocalPlayerDisplayName.Set);

            inputGo.SetActive(true);
        }

        /// <summary>
        /// Collapsed badge chip under the name field. Click opens the full-grid overlay.
        /// Restores the saved pick from <see cref="LocalPlayerBadge"/>.
        /// </summary>
        static void EnsurePlayerBadgePicker(Transform panel)
        {
            Transform existing = panel.Find(PlayerBadgePickerObjectName);
            GameObject rootGo = existing != null
                ? existing.gameObject
                : new GameObject(PlayerBadgePickerObjectName, typeof(RectTransform));
            if (existing == null)
                rootGo.transform.SetParent(panel, false);

            var rootRt = rootGo.GetComponent<RectTransform>();
            // Same screen-fraction as the name box; hang below it by a fixed pixel gap
            // so the two never overlap when the window height changes.
            var inputRt = panel.Find(PlayerNameInputObjectName) as RectTransform;
            const float gapBelowName = 22f;
            float inputHalfH = inputRt != null ? inputRt.rect.height * 0.5f : 56f;
            Vector2 nameAnchor = inputRt != null ? inputRt.anchorMin : new Vector2(0.5f, 0.42f);
            rootRt.anchorMin = nameAnchor;
            rootRt.anchorMax = nameAnchor;
            rootRt.pivot = new Vector2(0.5f, 1f);
            rootRt.sizeDelta = new Vector2(280f, 118f);
            rootRt.anchoredPosition = new Vector2(0f, -inputHalfH - gapBelowName);

            Transform chipTf = rootGo.transform.Find("Chip");
            GameObject chipGo = chipTf != null
                ? chipTf.gameObject
                : new GameObject("Chip", typeof(RectTransform), typeof(Image), typeof(Button));
            if (chipTf == null)
                chipGo.transform.SetParent(rootGo.transform, false);

            var chipRt = chipGo.GetComponent<RectTransform>();
            chipRt.anchorMin = new Vector2(0.5f, 1f);
            chipRt.anchorMax = new Vector2(0.5f, 1f);
            chipRt.pivot = new Vector2(0.5f, 1f);
            chipRt.sizeDelta = new Vector2(88f, 88f);
            chipRt.anchoredPosition = Vector2.zero;

            var chipFill = chipGo.GetComponent<Image>();
            chipFill.color = new Color(0.45f, 0.78f, 0.95f, 0.85f);
            chipFill.raycastTarget = true;

            DestroyChildIfPresent(chipGo.transform, "Ring");

            var badgeGo = EnsureChild(chipGo.transform, "Badge", typeof(Image));
            var badgeRt = badgeGo.GetComponent<RectTransform>();
            badgeRt.anchorMin = Vector2.zero;
            badgeRt.anchorMax = Vector2.one;
            badgeRt.offsetMin = new Vector2(6f, 6f);
            badgeRt.offsetMax = new Vector2(-6f, -6f);
            var badgeImage = badgeGo.GetComponent<Image>();
            badgeImage.preserveAspect = true;
            badgeImage.raycastTarget = false;
            badgeImage.color = Color.white;

            var emptyGo = EnsureChild(chipGo.transform, "EmptyMark", typeof(TextMeshProUGUI));
            var emptyRt = emptyGo.GetComponent<RectTransform>();
            emptyRt.anchorMin = Vector2.zero;
            emptyRt.anchorMax = Vector2.one;
            emptyRt.offsetMin = Vector2.zero;
            emptyRt.offsetMax = Vector2.zero;
            var emptyTmp = emptyGo.GetComponent<TextMeshProUGUI>();
            emptyTmp.text = "+";
            emptyTmp.fontSize = 42f;
            emptyTmp.alignment = TextAlignmentOptions.Center;
            emptyTmp.color = StatusColor;
            emptyTmp.raycastTarget = false;

            var captionGo = EnsureChild(rootGo.transform, "Caption", typeof(TextMeshProUGUI));
            var captionRt = captionGo.GetComponent<RectTransform>();
            captionRt.anchorMin = new Vector2(0f, 0f);
            captionRt.anchorMax = new Vector2(1f, 0f);
            captionRt.pivot = new Vector2(0.5f, 0f);
            captionRt.sizeDelta = new Vector2(0f, 26f);
            captionRt.anchoredPosition = Vector2.zero;
            var captionTmp = captionGo.GetComponent<TextMeshProUGUI>();
            captionTmp.fontSize = 18f;
            captionTmp.alignment = TextAlignmentOptions.Center;
            captionTmp.color = StatusColor;
            captionTmp.raycastTarget = false;

            var picker = rootGo.GetComponent<MainMenuBadgePicker>();
            if (picker == null)
                picker = rootGo.AddComponent<MainMenuBadgePicker>();
            picker.Configure(badgeImage, chipFill, emptyTmp, captionTmp);

            var chipBtn = chipGo.GetComponent<Button>();
            chipBtn.transition = Selectable.Transition.ColorTint;
            chipBtn.targetGraphic = chipFill;
            chipBtn.onClick.RemoveAllListeners();
            chipBtn.onClick.AddListener(picker.OpenOverlay);

            rootGo.SetActive(true);
        }

        /// <summary>Finds or creates a named child with the given extra components.</summary>
        static GameObject EnsureChild(Transform parent, string name, params System.Type[] extras)
        {
            Transform existing = parent.Find(name);
            if (existing != null)
                return existing.gameObject;

            var types = new System.Type[extras.Length + 1];
            types[0] = typeof(RectTransform);
            for (int i = 0; i < extras.Length; i++)
                types[i + 1] = extras[i];

            var go = new GameObject(name, types);
            go.transform.SetParent(parent, false);
            return go;
        }

        /// <summary>Creates the centered vertical column that holds Play / Join / Local client.</summary>
        static Transform EnsureButtonStack(Transform panel)
        {
            Transform existing = panel.Find(ButtonStackObjectName);
            GameObject go = existing != null
                ? existing.gameObject
                : new GameObject(ButtonStackObjectName, typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));

            if (existing == null)
                go.transform.SetParent(panel, false);

            var rt = go.GetComponent<RectTransform>();
            // Same anchor as the name/badge column — Y is set in LayoutStack from the chip.
            var badgeRt = panel.Find(PlayerBadgePickerObjectName) as RectTransform;
            Vector2 columnAnchor = badgeRt != null ? badgeRt.anchorMin : new Vector2(0.5f, 0.42f);
            rt.anchorMin = columnAnchor;
            rt.anchorMax = columnAnchor;
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(360f, 240f);
            rt.anchoredPosition = Vector2.zero;
            rt.localScale = Vector3.one;

            var vlg = go.GetComponent<VerticalLayoutGroup>();
            if (vlg == null)
                vlg = go.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 12f;
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.padding = new RectOffset(0, 0, 0, 0);

            var fitter = go.GetComponent<ContentSizeFitter>();
            if (fitter == null)
                fitter = go.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            return go.transform;
        }

        /// <summary>Places <paramref name="child"/> under the stack at a stable sibling index.</summary>
        static void EnsureInStack(Transform child, Transform stack, int siblingIndex)
        {
            if (child.parent != stack)
                child.SetParent(stack, false);
            if (siblingIndex >= 0)
                child.SetSiblingIndex(Mathf.Clamp(siblingIndex, 0, stack.childCount - 1));
        }

        /// <summary>Creates or updates a stack button using the shared Play / Cut Frame style.</summary>
        static void CreateOrWireStackButton(
            Transform stack,
            string name,
            string label,
            MenuButtonStyle style,
            UnityEngine.Events.UnityAction onClick,
            int siblingIndex)
        {
            Transform existing = stack.Find(name);
            // Also search panel root for buttons created by older bootstraps.
            if (existing == null && stack.parent != null)
            {
                var orphan = stack.parent.Find(name);
                if (orphan != null)
                {
                    orphan.SetParent(stack, false);
                    existing = orphan;
                }
            }

            GameObject go = existing != null
                ? existing.gameObject
                : new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));

            if (existing == null)
                go.transform.SetParent(stack, false);

            if (siblingIndex >= 0)
                go.transform.SetSiblingIndex(Mathf.Clamp(siblingIndex, 0, stack.childCount - 1));

            StyleButton(go, label, style, 46f, 340f);

            var button = go.GetComponent<Button>();
            button.onClick.RemoveAllListeners();
            if (onClick != null)
                button.onClick.AddListener(onClick);
            button.interactable = true;
            go.SetActive(true);
        }

        /// <summary>
        /// Applies Play-button visuals (Cut Frame sprite, sliced type, ColorBlock) plus label size.
        /// </summary>
        static void StyleButton(GameObject go, string label, MenuButtonStyle style, float height, float width)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.localScale = Vector3.one;

            var layout = go.GetComponent<LayoutElement>();
            if (layout == null)
                layout = go.AddComponent<LayoutElement>();
            layout.preferredHeight = height;
            layout.minHeight = height;
            layout.preferredWidth = width;

            var image = go.GetComponent<Image>();
            if (image == null)
                image = go.AddComponent<Image>();

            // [UNITY] Sliced Cut Frame needs a border sprite — copy from Play when available.
            if (style.HasSprite)
            {
                image.sprite = style.Sprite;
                image.type = style.ImageType;
                image.pixelsPerUnitMultiplier = style.PixelsPerUnitMultiplier;
            }
            else
            {
                image.sprite = null;
                image.type = Image.Type.Simple;
            }

            image.color = style.ImageColor;
            image.raycastTarget = true;

            var button = go.GetComponent<Button>();
            if (button == null)
                button = go.AddComponent<Button>();
            button.transition = Selectable.Transition.ColorTint;
            button.colors = style.Colors;
            button.targetGraphic = image;

            var textGo = go.transform.Find("Text")?.gameObject;
            TextMeshProUGUI tmp = null;
            if (textGo == null)
            {
                // Scene PlayButton may use a differently named child — take first TMP.
                tmp = go.GetComponentInChildren<TextMeshProUGUI>(true);
                if (tmp == null)
                {
                    textGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                    textGo.transform.SetParent(go.transform, false);
                    tmp = textGo.GetComponent<TextMeshProUGUI>();
                }
            }
            else
            {
                tmp = textGo.GetComponent<TextMeshProUGUI>();
            }

            if (tmp != null)
            {
                var textRt = tmp.rectTransform;
                textRt.anchorMin = Vector2.zero;
                textRt.anchorMax = Vector2.one;
                textRt.offsetMin = Vector2.zero;
                textRt.offsetMax = Vector2.zero;
                tmp.text = label;
                tmp.fontSize = 22f;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color = Color.white;
                tmp.raycastTarget = false;
            }
        }

        /// <summary>
        /// Pixel Y for the button stack: just under the badge chip, same name-column anchor.
        /// </summary>
        static float ComputeStackYBelowBadge(Transform panel)
        {
            const float gapBelowBadge = 20f;
            var badgeRt = panel.Find(PlayerBadgePickerObjectName) as RectTransform;
            if (badgeRt == null)
                return -208f;

            // Badge pivot is top, so its bottom is anchoredPosition.y - height.
            return badgeRt.anchoredPosition.y - badgeRt.sizeDelta.y - gapBelowBadge;
        }

        /// <summary>Forces layout rebuild after children change.</summary>
        static void LayoutStack(Transform stack, float anchoredY)
        {
            var rt = stack.GetComponent<RectTransform>();
            rt.anchoredPosition = new Vector2(0f, anchoredY);
            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        }

        /// <summary>
        /// Status TMP placed directly under the button stack so it never overlaps Play / Join.
        /// Stays a direct child of the panel (so <c>panel.Find("MainMenuStatus")</c> still works).
        /// </summary>
        /// <param name="panel">MainMenuPanel root.</param>
        /// <param name="stack">ButtonStack whose laid-out height drives the Y offset.</param>
        static TextMeshProUGUI EnsureStatusTextBelowStack(Transform panel, Transform stack)
        {
            var statusGo = panel.Find("MainMenuStatus");
            GameObject go = statusGo != null
                ? statusGo.gameObject
                : new GameObject("MainMenuStatus", typeof(RectTransform), typeof(TextMeshProUGUI));

            if (statusGo == null)
                go.transform.SetParent(panel, false);
            else if (statusGo.parent != panel)
                statusGo.SetParent(panel, false);

            // Ensure stack height is current before we measure it.
            var stackRt = stack.GetComponent<RectTransform>();
            LayoutRebuilder.ForceRebuildLayoutImmediate(stackRt);

            const float gapBelowStack = 18f;
            float stackHeight = Mathf.Max(stackRt.rect.height, 1f);

            var rt = go.GetComponent<RectTransform>();
            // Same anchor as the stack; pivot at top so we hang just under the last button.
            rt.anchorMin = stackRt.anchorMin;
            rt.anchorMax = stackRt.anchorMax;
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(680f, 72f);
            rt.anchoredPosition = new Vector2(
                0f,
                stackRt.anchoredPosition.y - stackHeight - gapBelowStack);
            rt.localScale = Vector3.one;

            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.fontSize = 16f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = StatusColor;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = true;
            if (string.IsNullOrEmpty(tmp.text))
                tmp.text = "Join a match or start local play.";
            return tmp;
        }

        /// <summary>Finds or creates a child TMP under <paramref name="parent"/>.</summary>
        static TextMeshProUGUI EnsureChildTmp(Transform parent, string name, TextAlignmentOptions align)
        {
            Transform existing = parent.Find(name);
            GameObject go = existing != null
                ? existing.gameObject
                : new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            if (existing == null)
                go.transform.SetParent(parent, false);

            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.alignment = align;
            tmp.raycastTarget = false;
            return tmp;
        }

        /// <summary>Destroys a direct child by name if present (legacy / redundant UI).</summary>
        static void DestroyChildIfPresent(Transform parent, string name)
        {
            if (parent == null)
                return;
            var child = parent.Find(name);
            if (child == null)
                return;
            if (Application.isPlaying)
                Object.Destroy(child.gameObject);
            else
                Object.DestroyImmediate(child.gameObject);
        }

        /// <summary>
        /// Editor / bootstrap helper: assign the logo sprite onto the Logo Image if present.
        /// Called from Editor setup command after import — kept here so runtime path stays simple.
        /// </summary>
        public static void AssignLogoSprite(Transform panel, Sprite logoSprite)
        {
            if (panel == null || logoSprite == null)
                return;
            var logo = panel.Find(LogoObjectName);
            if (logo == null)
                return;
            var image = logo.GetComponent<Image>();
            if (image != null)
            {
                image.sprite = logoSprite;
                image.preserveAspect = true;
            }
        }

        /// <summary>
        /// Applies the scene Play button's Cut Frame look to any button GameObject.
        /// Used by Join Game and other menus so all primary actions share one style.
        /// </summary>
        /// <param name="go">Button root with Image + Button (created if missing).</param>
        /// <param name="label">TMP caption.</param>
        /// <param name="height">Preferred height for layout.</param>
        /// <param name="width">Preferred width for layout.</param>
        /// <param name="styleSource">Optional Play button; when null, finds scene PlayButton.</param>
        public static void StyleGameObjectAsMenuButton(
            GameObject go,
            string label,
            float height,
            float width,
            Button styleSource = null)
        {
            if (go == null)
                return;
            if (styleSource == null)
                styleSource = FindScenePlayButton();
            StyleButton(go, label, CaptureButtonStyle(styleSource), height, width);
        }

        /// <summary>Finds the scene-authored PlayButton used as the visual style source.</summary>
        public static Button FindScenePlayButton()
        {
            var transforms = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i].name != "PlayButton")
                    continue;
                var scene = transforms[i].gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded)
                    continue;
                return transforms[i].GetComponent<Button>();
            }

            return null;
        }

        /// <summary>
        /// Places a Titan Orbit logo sized for secondary screens (Join Game).
        /// When <paramref name="parent"/> uses a vertical layout group, the logo participates in
        /// that flow near the top; otherwise it anchors high on the parent.
        /// </summary>
        /// <param name="parent">Usually the Join Game content column or screen root.</param>
        /// <param name="objectName">Child name so rebuild detection can Find it.</param>
        /// <returns>The logo GameObject (new or existing).</returns>
        public static GameObject PlaceCompactTopLogo(Transform parent, string objectName = "JoinGameLogo")
        {
            if (parent == null)
                return null;

            Transform existing = parent.Find(objectName);
            GameObject go = existing != null
                ? existing.gameObject
                : new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));

            if (existing == null)
                go.transform.SetParent(parent, false);

            // Join Game logo width (~50% larger than the older 504px compact size).
            // Height follows sprite aspect, then trimmed ~15% so transparent PNG padding does not
            // read as a large empty gap above/below the art (layout of the rest of the screen is unchanged).
            const float logoW = 756f;
            var fromResources = Resources.Load<Sprite>(LogoResourcesPath);
            float aspect = fromResources != null && fromResources.rect.height > 0.01f
                ? fromResources.rect.width / fromResources.rect.height
                : (756f / 270f);
            float logoH = Mathf.Clamp(logoW / aspect, 140f, 300f) * 0.85f;

            var rt = go.GetComponent<RectTransform>();
            rt.localScale = Vector3.one;

            bool inVerticalLayout = parent.GetComponent<VerticalLayoutGroup>() != null;
            if (inVerticalLayout)
            {
                // [UNITY] LayoutElement drives size inside VerticalLayoutGroup (not stretch anchors).
                rt.anchorMin = new Vector2(0.5f, 1f);
                rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.sizeDelta = new Vector2(logoW, logoH);
                rt.anchoredPosition = Vector2.zero;

                var le = go.GetComponent<LayoutElement>();
                if (le == null)
                    le = go.AddComponent<LayoutElement>();
                le.preferredWidth = logoW;
                le.preferredHeight = logoH;
                le.minWidth = logoW * 0.5f;
                le.minHeight = logoH;
                le.flexibleWidth = 0f;
                le.flexibleHeight = 0f;
            }
            else
            {
                // Absolute: higher than main-menu logo band (main uses ~0.58–0.88).
                rt.anchorMin = new Vector2(0.5f, 0.80f);
                rt.anchorMax = new Vector2(0.5f, 0.96f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(logoW, logoH);
                rt.anchoredPosition = Vector2.zero;
            }

            var image = go.GetComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.color = Color.white;
            image.material = null;
            if (fromResources != null)
                image.sprite = fromResources;

            go.SetActive(true);
            return go;
        }

        /// <summary>
        /// Soft navy panel tint matching the main menu — SpaceBackground shows through.
        /// </summary>
        public static void ApplyTransparentMenuBackdrop(Image image)
        {
            if (image == null)
                return;
            image.sprite = null;
            image.color = new Color(0.02f, 0.04f, 0.08f, 0.35f);
            image.raycastTarget = true;
        }
    }
}
