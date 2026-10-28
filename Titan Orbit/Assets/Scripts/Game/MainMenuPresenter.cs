using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.NetCode;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Builds the Command Deck: one space-HUD screen that keeps sign-in, callsign,
    /// the ship studio, live matches, and Orbit Unlocked visible together.
    /// Client-only [HYBRID] presentation — does not touch ECS ship sim.
    /// Called from <see cref="NceGameFlowController"/> and <see cref="MainMenuUiBootstrap"/>.
    ///
    /// Layout contract:
    ///   Header — logo, callsign, Sign in
    ///   Left — embedded Customize Ship studio
    ///   Right — match list, Orbit Unlocked card, Quick join / Local client, status
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

        /// <summary>Child name for the retired vertical button column. Hidden once the deck exists.</summary>
        public const string ButtonStackObjectName = "ButtonStack";

        /// <summary>Root of the combined menu. Direct child of MainMenuPanel.</summary>
        public const string CommandDeckObjectName = "CommandDeck";

        /// <summary>Child name for the player-name label.</summary>
        public const string PlayerNameLabelObjectName = "PlayerNameLabel";

        /// <summary>Child name for the player-name TMP_InputField.</summary>
        public const string PlayerNameInputObjectName = "PlayerNameInput";

        /// <summary>Child name for the collapsed profile-badge chip.</summary>
        public const string PlayerBadgePickerObjectName = MainMenuBadgePicker.RootObjectName;

        /// <summary>Legacy child name from when hull paint lived on this panel. Destroyed on refresh.</summary>
        public const string PlayerAccentPickerObjectName = "PlayerAccentPicker";

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

            // Join is the match column now. The callback stays on the signature so older
            // call sites compile; the deck does not open a second screen.
            _ = onJoinGame;

            // --- Panel backdrop ---
            // [TITAN-ORBIT] Clear Placeholder HUD BG so SpaceBackground shows through.
            EnsureTransparentPanelBackdrop(panel);

            DestroyChildIfPresent(panel.transform, "LocalHostButton");

            var title = panel.transform.Find("Title");
            if (title != null)
                title.gameObject.SetActive(false);

            MenuButtonStyle buttonStyle = CaptureButtonStyle(playButton);

            DestroyChildIfPresent(panel.transform, PlayerBadgePickerObjectName);
            DestroyChildIfPresent(panel.transform, MainMenuBadgePicker.OverlayObjectName);
            DestroyChildIfPresent(panel.transform, PlayerAccentPickerObjectName);

            Transform deck = EnsureCommandDeck(panel.transform);
            Transform header = deck.Find("Header");
            Transform shipSlot = deck.Find("Body/ShipSlot");
            Transform right = deck.Find("Body/RightColumn");
            Transform matchSlot = right != null ? right.Find("MatchSlot") : null;
            Transform unlockSlot = right != null ? right.Find("UnlockSlot") : null;
            Transform actionRow = right != null ? right.Find("ActionRow") : null;

            EnsureHeaderLogo(header);
            EnsureAccountBar(header, buttonStyle);
            EnsurePlayerNameField(header);

            RetireStackButton(panel.transform, "BrowseGamesButton");
            RetireStackButton(panel.transform, "CustomizeShipButton");
            RetireStackButton(panel.transform, "UnlockOrbitButton");
            RetireStackButton(panel.transform, "LocalHostButton");

            if (playButton != null && actionRow != null)
            {
                playButton.transform.SetParent(actionRow, false);
                playButton.transform.SetAsFirstSibling();
                var playFitter = playButton.GetComponent<ContentSizeFitter>();
                if (playFitter != null)
                    playFitter.enabled = false;
                StyleButton(playButton.gameObject, GetPlayLabel(), buttonStyle, 48f, 280f);
                var playLe = playButton.GetComponent<LayoutElement>();
                if (playLe != null)
                    playLe.flexibleWidth = 1f;
                playButton.gameObject.SetActive(true);
            }

            if (TitanOrbitMultiplayerConfig.ShowLocalPlayOptions && actionRow != null)
            {
                CreateDeckSideButton(panel.transform, actionRow, "LocalClientButton", "Local client", buttonStyle, onLocalClient);
            }
            else
            {
                RetireStackButton(panel.transform, "LocalClientButton");
                if (actionRow != null)
                    DestroyChildIfPresent(actionRow, "LocalClientButton");
            }

            var stack = panel.transform.Find(ButtonStackObjectName);
            if (stack != null)
                stack.gameObject.SetActive(false);

            statusText = right != null ? EnsureDeckStatus(panel.transform, right) : null;

            var deckRt = deck as RectTransform;
            if (deckRt != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(deckRt);

            if (shipSlot != null)
                ShipCustomizeScreen.Embed(shipSlot, TeamId.TeamA);
            if (unlockSlot != null)
                OrbitUnlockedPurchaseScreen.Embed(unlockSlot);

            var browser = UnityEngine.Object.FindFirstObjectByType<JoinGameBrowserController>();
            if (browser != null && matchSlot != null)
                browser.Embed(matchSlot, panel);
        }

        /// <summary>
        /// Puts the studio, match list, and Orbit Unlocked card back in their slots
        /// after a match (the dock studio can steal the customize instance).
        /// </summary>
        public static void RefreshCommandDeck(GameObject panel)
        {
            if (panel == null)
                return;

            Transform deck = panel.transform.Find(CommandDeckObjectName);
            if (deck == null)
                return;

            Transform shipSlot = deck.Find("Body/ShipSlot");
            Transform matchSlot = deck.Find("Body/RightColumn/MatchSlot");
            Transform unlockSlot = deck.Find("Body/RightColumn/UnlockSlot");
            if (shipSlot != null)
                ShipCustomizeScreen.Embed(shipSlot, TeamId.TeamA);
            if (unlockSlot != null)
                OrbitUnlockedPurchaseScreen.Embed(unlockSlot);
            if (matchSlot != null)
            {
                var browser = UnityEngine.Object.FindFirstObjectByType<JoinGameBrowserController>();
                if (browser != null)
                    browser.Embed(matchSlot, panel);
            }
        }

        /// <summary>Depth-first child lookup. <see cref="Transform.Find"/> only walks one path.</summary>
        public static Transform FindDeep(Transform root, string childName)
        {
            if (root == null || string.IsNullOrEmpty(childName))
                return null;
            if (root.name == childName)
                return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), childName);
                if (found != null)
                    return found;
            }

            return null;
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
        /// Command Deck chrome: header, ship slot, match column, unlock card, action row.
        /// Idempotent — safe on the bootstrap retry loop.
        /// </summary>
        static Transform EnsureCommandDeck(Transform panel)
        {
            Transform existing = panel.Find(CommandDeckObjectName);
            GameObject deckGo = existing != null
                ? existing.gameObject
                : new GameObject(CommandDeckObjectName, typeof(RectTransform));
            if (existing == null)
                deckGo.transform.SetParent(panel, false);

            var deckRt = deckGo.GetComponent<RectTransform>();
            deckRt.anchorMin = Vector2.zero;
            deckRt.anchorMax = Vector2.one;
            deckRt.offsetMin = new Vector2(16f, 14f);
            deckRt.offsetMax = new Vector2(-16f, -14f);
            deckRt.localScale = Vector3.one;

            var deckImage = deckGo.GetComponent<Image>();
            if (deckImage == null)
                deckImage = deckGo.AddComponent<Image>();
            deckImage.sprite = null;
            deckImage.color = new Color(0.01f, 0.02f, 0.04f, 0.18f);
            deckImage.raycastTarget = false;

            EnsureEdgeRail(deckGo.transform, "TopRail", new Color(0.42f, 0.78f, 0.98f, 0.9f), top: true);
            EnsureEdgeRail(deckGo.transform, "BottomRail", new Color(0.95f, 0.78f, 0.22f, 0.85f), top: false);
            EnsureCornerTicks(deckGo.transform);

            Transform header = EnsureStretchChild(deckGo.transform, "Header");
            var headerRt = header.GetComponent<RectTransform>();
            headerRt.anchorMin = new Vector2(0f, 1f);
            headerRt.anchorMax = new Vector2(1f, 1f);
            headerRt.pivot = new Vector2(0.5f, 1f);
            headerRt.sizeDelta = new Vector2(0f, 92f);
            headerRt.anchoredPosition = new Vector2(0f, -8f);

            Transform body = EnsureStretchChild(deckGo.transform, "Body");
            var bodyRt = body.GetComponent<RectTransform>();
            bodyRt.anchorMin = Vector2.zero;
            bodyRt.anchorMax = Vector2.one;
            bodyRt.offsetMin = new Vector2(8f, 10f);
            bodyRt.offsetMax = new Vector2(-8f, -108f);

            Transform ship = EnsureSlot(body, "ShipSlot", new Color(0.42f, 0.78f, 0.98f, 0.45f));
            var shipRt = ship.GetComponent<RectTransform>();
            shipRt.anchorMin = Vector2.zero;
            shipRt.anchorMax = new Vector2(0.56f, 1f);
            shipRt.offsetMin = Vector2.zero;
            shipRt.offsetMax = new Vector2(-8f, 0f);
            shipRt.pivot = new Vector2(0.5f, 0.5f);

            Transform right = EnsureStretchChild(body, "RightColumn", typeof(VerticalLayoutGroup));
            var rightRt = right.GetComponent<RectTransform>();
            rightRt.anchorMin = new Vector2(0.56f, 0f);
            rightRt.anchorMax = Vector2.one;
            rightRt.offsetMin = new Vector2(8f, 0f);
            rightRt.offsetMax = Vector2.zero;
            rightRt.pivot = new Vector2(0.5f, 0.5f);
            var rightLayout = right.GetComponent<VerticalLayoutGroup>();
            if (rightLayout == null)
                rightLayout = right.gameObject.AddComponent<VerticalLayoutGroup>();
            rightLayout.spacing = 10f;
            rightLayout.padding = new RectOffset(0, 0, 0, 0);
            rightLayout.childAlignment = TextAnchor.UpperCenter;
            rightLayout.childControlWidth = true;
            rightLayout.childControlHeight = true;
            rightLayout.childForceExpandWidth = true;
            rightLayout.childForceExpandHeight = false;

            Transform match = EnsureSlot(right, "MatchSlot", new Color(0.42f, 0.78f, 0.98f, 0.28f));
            SetColumnSlot(match, flexibleHeight: 1f, preferredHeight: -1f, minHeight: 180f);

            Transform unlock = EnsureSlot(right, "UnlockSlot", new Color(0.95f, 0.78f, 0.22f, 0.4f));
            SetColumnSlot(unlock, flexibleHeight: 0f, preferredHeight: 206f, minHeight: 188f);

            Transform action = EnsureStretchChild(right, "ActionRow", typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            var actionLayout = action.GetComponent<HorizontalLayoutGroup>();
            if (actionLayout == null)
                actionLayout = action.gameObject.AddComponent<HorizontalLayoutGroup>();
            actionLayout.spacing = 10f;
            actionLayout.padding = new RectOffset(0, 0, 0, 0);
            actionLayout.childAlignment = TextAnchor.MiddleCenter;
            actionLayout.childControlWidth = true;
            actionLayout.childControlHeight = true;
            actionLayout.childForceExpandWidth = false;
            actionLayout.childForceExpandHeight = true;
            SetColumnSlot(action, flexibleHeight: 0f, preferredHeight: 52f, minHeight: 48f);

            return deckGo.transform;
        }

        /// <summary>Finds or creates a direct child with a RectTransform.</summary>
        static Transform EnsureStretchChild(Transform parent, string name, params System.Type[] extras)
        {
            return EnsureChild(parent, name, extras).transform;
        }

        /// <summary>Glass slot with a thin accent outline.</summary>
        static Transform EnsureSlot(Transform parent, string name, Color outlineColor)
        {
            Transform slot = EnsureStretchChild(parent, name, typeof(Image), typeof(Outline));
            var image = slot.GetComponent<Image>();
            image.sprite = null;
            image.color = new Color(0.012f, 0.02f, 0.04f, 0.42f);
            image.raycastTarget = false;
            var outline = slot.GetComponent<Outline>();
            outline.effectColor = outlineColor;
            outline.effectDistance = new Vector2(1.25f, -1.25f);
            return slot;
        }

        /// <summary>Vertical-layout height for a right-column region.</summary>
        static void SetColumnSlot(Transform slot, float flexibleHeight, float preferredHeight, float minHeight)
        {
            var layout = slot.GetComponent<LayoutElement>();
            if (layout == null)
                layout = slot.gameObject.AddComponent<LayoutElement>();
            layout.flexibleWidth = 1f;
            layout.flexibleHeight = flexibleHeight;
            layout.preferredHeight = preferredHeight;
            layout.minHeight = minHeight;
        }

        /// <summary>Cyan / gold hairline along the deck edge, inset so the corner ticks stay visible.</summary>
        static void EnsureEdgeRail(Transform parent, string name, Color color, bool top)
        {
            Transform existing = parent.Find(name);
            GameObject go = existing != null
                ? existing.gameObject
                : new GameObject(name, typeof(RectTransform), typeof(Image));
            if (existing == null)
                go.transform.SetParent(parent, false);

            var rt = go.GetComponent<RectTransform>();
            if (top)
            {
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, -4f);
            }
            else
            {
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.anchoredPosition = new Vector2(0f, 4f);
            }

            rt.sizeDelta = new Vector2(-36f, 2f);
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
        }

        /// <summary>Four HUD corner brackets.</summary>
        static void EnsureCornerTicks(Transform deck)
        {
            EnsureCornerTick(deck, "TickTL", new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(8f, -8f), left: true, top: true);
            EnsureCornerTick(deck, "TickTR", new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-8f, -8f), left: false, top: true);
            EnsureCornerTick(deck, "TickBL", new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(8f, 8f), left: true, top: false);
            EnsureCornerTick(deck, "TickBR", new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-8f, 8f), left: false, top: false);
        }

        /// <summary>One L-shaped bracket.</summary>
        static void EnsureCornerTick(
            Transform parent,
            string name,
            Vector2 anchor,
            Vector2 pivot,
            Vector2 position,
            bool left,
            bool top)
        {
            if (parent.Find(name) != null)
                return;

            var root = new GameObject(name, typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var rt = root.GetComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.sizeDelta = new Vector2(26f, 26f);
            rt.anchoredPosition = position;

            Color color = new Color(0.42f, 0.78f, 0.98f, 0.95f);
            var horizontal = new GameObject("H", typeof(RectTransform), typeof(Image));
            horizontal.transform.SetParent(root.transform, false);
            var horizontalRt = horizontal.GetComponent<RectTransform>();
            horizontalRt.anchorMin = new Vector2(0f, top ? 1f : 0f);
            horizontalRt.anchorMax = new Vector2(1f, top ? 1f : 0f);
            horizontalRt.pivot = new Vector2(0.5f, top ? 1f : 0f);
            horizontalRt.sizeDelta = new Vector2(0f, 2f);
            horizontalRt.anchoredPosition = Vector2.zero;
            var horizontalImage = horizontal.GetComponent<Image>();
            horizontalImage.color = color;
            horizontalImage.raycastTarget = false;

            var vertical = new GameObject("V", typeof(RectTransform), typeof(Image));
            vertical.transform.SetParent(root.transform, false);
            var verticalRt = vertical.GetComponent<RectTransform>();
            verticalRt.anchorMin = new Vector2(left ? 0f : 1f, 0f);
            verticalRt.anchorMax = new Vector2(left ? 0f : 1f, 1f);
            verticalRt.pivot = new Vector2(left ? 0f : 1f, 0.5f);
            verticalRt.sizeDelta = new Vector2(2f, 0f);
            verticalRt.anchoredPosition = Vector2.zero;
            var verticalImage = vertical.GetComponent<Image>();
            verticalImage.color = color;
            verticalImage.raycastTarget = false;
        }

        /// <summary>
        /// Brand mark in the deck header. Adopts a leftover centered Logo from the old menu.
        /// </summary>
        static void EnsureHeaderLogo(Transform header)
        {
            if (header == null)
                return;

            Transform panel = header.parent != null ? header.parent.parent : null;
            Transform existing = header.Find(LogoObjectName);
            if (existing == null && panel != null)
                existing = panel.Find(LogoObjectName);

            GameObject go = existing != null
                ? existing.gameObject
                : new GameObject(LogoObjectName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));

            if (go.transform.parent != header)
                go.transform.SetParent(header, false);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(300f, 72f);
            rt.anchoredPosition = new Vector2(12f, 0f);
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
        /// Compact Sign in / Sign out button on the right of the deck header.
        /// </summary>
        static void EnsureAccountBar(Transform header, MenuButtonStyle buttonStyle)
        {
            if (header == null)
                return;

            Transform panel = header.parent != null ? header.parent.parent : null;
            Transform existing = header.Find(AccountBarObjectName);
            if (existing == null && panel != null)
                existing = panel.Find(AccountBarObjectName);

            GameObject barGo = existing != null
                ? existing.gameObject
                : new GameObject(AccountBarObjectName, typeof(RectTransform));

            if (barGo.transform.parent != header)
                barGo.transform.SetParent(header, false);

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
            barRt.anchorMin = new Vector2(1f, 0.5f);
            barRt.anchorMax = new Vector2(1f, 0.5f);
            barRt.pivot = new Vector2(1f, 0.5f);
            barRt.sizeDelta = new Vector2(200f, 44f);
            barRt.anchoredPosition = new Vector2(-12f, 0f);

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
            StyleButton(btnGo, "Sign in", buttonStyle, 44f, 200f);

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
        /// Callsign plate in the deck header. Restores scene orphans when present.
        /// Saves to <see cref="LocalPlayerDisplayName"/> on every edit.
        /// </summary>
        static void EnsurePlayerNameField(Transform header)
        {
            if (header == null)
                return;

            Transform panel = header.parent != null ? header.parent.parent : null;

            Transform labelTf = header.Find(PlayerNameLabelObjectName);
            if (labelTf == null && panel != null)
                labelTf = panel.Find(PlayerNameLabelObjectName);
            GameObject labelGo = labelTf != null
                ? labelTf.gameObject
                : new GameObject(PlayerNameLabelObjectName, typeof(RectTransform), typeof(TextMeshProUGUI));
            if (labelGo.transform.parent != header)
                labelGo.transform.SetParent(header, false);

            var labelRt = labelGo.GetComponent<RectTransform>();
            labelRt.anchorMin = new Vector2(0.5f, 0.5f);
            labelRt.anchorMax = new Vector2(0.5f, 0.5f);
            labelRt.pivot = new Vector2(0.5f, 0.5f);
            labelRt.sizeDelta = new Vector2(420f, 18f);
            labelRt.anchoredPosition = new Vector2(0f, 22f);

            var labelTmp = labelGo.GetComponent<TextMeshProUGUI>();
            labelTmp.text = "CALLSIGN";
            labelTmp.fontSize = 13f;
            labelTmp.fontStyle = FontStyles.Bold;
            labelTmp.characterSpacing = 2.4f;
            labelTmp.alignment = TextAlignmentOptions.Center;
            labelTmp.color = new Color(0.42f, 0.78f, 0.98f, 0.92f);
            labelTmp.raycastTarget = false;
            labelGo.SetActive(true);

            Transform inputTf = header.Find(PlayerNameInputObjectName);
            if (inputTf == null && panel != null)
                inputTf = panel.Find(PlayerNameInputObjectName);
            GameObject inputGo = inputTf != null
                ? inputTf.gameObject
                : new GameObject(
                    PlayerNameInputObjectName,
                    typeof(RectTransform),
                    typeof(Image),
                    typeof(TMP_InputField));
            if (inputGo.transform.parent != header)
                inputGo.transform.SetParent(header, false);

            var inputRt = inputGo.GetComponent<RectTransform>();
            inputRt.anchorMin = new Vector2(0.5f, 0.5f);
            inputRt.anchorMax = new Vector2(0.5f, 0.5f);
            inputRt.pivot = new Vector2(0.5f, 0.5f);
            inputRt.sizeDelta = new Vector2(420f, 46f);
            inputRt.anchoredPosition = new Vector2(0f, -12f);

            var nameOutline = inputGo.GetComponent<Outline>();
            if (nameOutline == null)
                nameOutline = inputGo.AddComponent<Outline>();
            nameOutline.effectColor = new Color(0.42f, 0.78f, 0.98f, 0.55f);
            nameOutline.effectDistance = new Vector2(1f, -1f);

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
            textRt.offsetMin = new Vector2(16f, 6f);
            textRt.offsetMax = new Vector2(-16f, -6f);
            textTmp.enableAutoSizing = true;
            textTmp.fontSizeMin = 18f;
            textTmp.fontSizeMax = 28f;
            textTmp.fontSize = 24f;
            textTmp.alignment = TextAlignmentOptions.Center;
            textTmp.color = Color.white;
            textTmp.raycastTarget = false;

            // Placeholder — same size / center alignment so it matches typed text.
            var placeholderTmp = EnsureChildTmp(inputGo.transform, "Placeholder", TextAlignmentOptions.Center);
            var phRt = placeholderTmp.rectTransform;
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(16f, 6f);
            phRt.offsetMax = new Vector2(-16f, -6f);
            placeholderTmp.enableAutoSizing = true;
            placeholderTmp.fontSizeMin = 18f;
            placeholderTmp.fontSizeMax = 28f;
            placeholderTmp.fontSize = 24f;
            placeholderTmp.alignment = TextAlignmentOptions.Center;
            placeholderTmp.fontStyle = FontStyles.Italic;
            placeholderTmp.color = new Color(0.65f, 0.72f, 0.82f, 0.55f);
            placeholderTmp.text = "Enter callsign";
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

            // Blinking caret + lit plate. Must run after textComponent is assigned:
            // TMP only creates the Caret child during OnEnable when that reference exists.
            TmpInputFieldFocus.Attach(input);

            inputGo.SetActive(true);
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

        /// <summary>Removes a leftover stack button from the panel or the retired ButtonStack.</summary>
        static void RetireStackButton(Transform panel, string name)
        {
            DestroyChildIfPresent(panel, name);
            var stack = panel.Find(ButtonStackObjectName);
            if (stack != null)
                DestroyChildIfPresent(stack, name);
        }

        /// <summary>Secondary deck action (Local client). Play stays the flexible primary.</summary>
        static void CreateDeckSideButton(
            Transform panel,
            Transform actionRow,
            string name,
            string label,
            MenuButtonStyle style,
            UnityEngine.Events.UnityAction onClick)
        {
            Transform existing = actionRow.Find(name);
            if (existing == null)
                existing = panel.Find(name);
            if (existing == null)
            {
                var stack = panel.Find(ButtonStackObjectName);
                if (stack != null)
                    existing = stack.Find(name);
            }

            GameObject go = existing != null
                ? existing.gameObject
                : new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            if (go.transform.parent != actionRow)
                go.transform.SetParent(actionRow, false);

            StyleButton(go, label, style, 48f, 180f);
            var layout = go.GetComponent<LayoutElement>();
            if (layout != null)
                layout.flexibleWidth = 0f;

            var button = go.GetComponent<Button>();
            button.onClick.RemoveAllListeners();
            if (onClick != null)
                button.onClick.AddListener(onClick);
            button.interactable = true;
            go.SetActive(true);
        }

        /// <summary>Connection status under Quick join. Found by name anywhere under the panel.</summary>
        static TextMeshProUGUI EnsureDeckStatus(Transform panel, Transform rightColumn)
        {
            Transform existing = FindDeep(panel, "MainMenuStatus");
            GameObject go = existing != null
                ? existing.gameObject
                : new GameObject("MainMenuStatus", typeof(RectTransform), typeof(TextMeshProUGUI));
            if (go.transform.parent != rightColumn)
                go.transform.SetParent(rightColumn, false);
            go.transform.SetAsLastSibling();

            var layout = go.GetComponent<LayoutElement>();
            if (layout == null)
                layout = go.AddComponent<LayoutElement>();
            layout.preferredHeight = 36f;
            layout.minHeight = 28f;
            layout.flexibleHeight = 0f;
            layout.flexibleWidth = 1f;

            var rt = go.GetComponent<RectTransform>();
            rt.localScale = Vector3.one;

            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.fontSize = 14f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = StatusColor;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = true;
            if (string.IsNullOrEmpty(tmp.text))
                tmp.text = "Pick a match, or quick join.";
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
            var logo = FindDeep(panel, LogoObjectName);
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
