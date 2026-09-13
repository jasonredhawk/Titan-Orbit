using System;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Full-screen Customize Ship studio: hull paint, profile badge, and thrusters.
    /// Color1 stays the team Colorize material. Color2 / Color3 / Glow 1–3 are
    /// color-well buttons — tap opens an HSV picker, not RGB sliders.
    /// CLOSE on the picker (or a tap on the preview / empty panel) dismisses it.
    /// <para>
    /// The world-space preview hull lives on the unused ShipPreview layer. Close()
    /// disables that camera and hides the root so a leftover ship cannot composite
    /// into the game view.
    /// </para>
    /// Writes <see cref="LocalPlayerShipAccents"/> / <see cref="LocalPlayerThrusterStyle"/>
    /// and pings <see cref="ShipAccentColorsRpcClient"/> so remotes see paint and jets.
    /// </summary>
    public sealed class ShipCustomizeScreen : MonoBehaviour
    {
        public const string OverlayObjectName = "ShipCustomizeScreen";

        const int OverlaySortingOrder = 540;
        const int SvSize = 160;
        const int HueBarW = 22;
        const int HueBarH = 160;

        static readonly Color PanelFill = new Color(0.012f, 0.016f, 0.028f, 0.97f);
        static readonly Color CaptionColor = new Color(0.62f, 0.78f, 0.95f, 0.92f);
        static readonly Color BodyColor = new Color(0.88f, 0.92f, 0.98f, 0.95f);

        enum AccentSlot
        {
            Color2 = 0,
            Color3 = 1,
            Emission1 = 2,
            Emission2 = 3,
            Emission3 = 4,
            Thruster = 5,
        }

        TeamId _team = TeamId.TeamA;
        Color32 _color2;
        Color32 _color3;
        Color32 _emission1;
        Color32 _emission2;
        Color32 _emission3;
        bool _hasCustom;

        Image _teamSwatch;
        Image _color2Well;
        Image _color3Well;
        Image _glow1Well;
        Image _glow2Well;
        Image _glow3Well;
        Image _badgeChip;
        TextMeshProUGUI _badgeEmpty;
        TextMeshProUGUI _badgeCaption;
        TextMeshProUGUI _paintStateLabel;
        TextMeshProUGUI _thrusterStyleLabel;
        TextMeshProUGUI _thrusterColorLabel;
        TextMeshProUGUI _thrusterModeLabel;
        Image _thrusterColorWell;
        MainMenuBadgePicker _badgePicker;
        readonly Image[] _teamChips = new Image[5];

        GameObject _previewRoot;
        Camera _previewCam;
        RenderTexture _previewRt;
        RawImage _previewImage;
        GameObject _previewHull;
        ShipFamilyDefinition _previewFamily;

        RectTransform _pickerPanel;
        RawImage _svImage;
        RawImage _hueImage;
        RectTransform _svCursor;
        RectTransform _hueCursor;
        Texture2D _svTex;
        Texture2D _hueTex;
        Texture2D _whiteTex;
        Texture2D _ringTex;
        Sprite _whiteSprite;
        Sprite _ringSprite;
        AccentSlot _pickerSlot;
        float _hue;
        float _sat;
        float _val;

        /// <summary>
        /// Finds or creates the overlay under <paramref name="canvasRoot"/> and shows it.
        /// </summary>
        public static void Open(Transform canvasRoot, TeamId team)
        {
            if (canvasRoot == null)
                return;

            Transform existing = canvasRoot.Find(OverlayObjectName);
            ShipCustomizeScreen screen;
            if (existing != null)
            {
                screen = existing.GetComponent<ShipCustomizeScreen>();
                if (screen == null)
                    screen = existing.gameObject.AddComponent<ShipCustomizeScreen>();
            }
            else
            {
                var go = new GameObject(
                    OverlayObjectName,
                    typeof(RectTransform),
                    typeof(Canvas),
                    typeof(GraphicRaycaster),
                    typeof(ShipCustomizeScreen));
                go.layer = canvasRoot.gameObject.layer;
                go.transform.SetParent(canvasRoot, false);
                screen = go.GetComponent<ShipCustomizeScreen>();
            }

            screen.Show(team);
        }

        /// <summary>Builds chrome once, then paints from prefs and the requested team Color1.</summary>
        public void Show(TeamId team)
        {
            _team = team == TeamId.None ? TeamId.TeamA : team;
            EnsureChrome();
            SetPreviewWorldActive(true);
            ApplyTeamToPreview();
            RefreshFromStore();
            RefreshThrusterSection();
            HidePicker();
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
        }

        /// <summary>Hides the studio and tears down the leftover preview camera / hull.</summary>
        public void Close()
        {
            HidePicker();
            FlushPersist();
            SetPreviewWorldActive(false);
            gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            if (_previewRt != null)
            {
                _previewRt.Release();
                Destroy(_previewRt);
            }

            if (_svTex != null)
                Destroy(_svTex);
            if (_hueTex != null)
                Destroy(_hueTex);
            if (_whiteTex != null)
                Destroy(_whiteTex);
            if (_ringTex != null)
                Destroy(_ringTex);
            if (_previewRoot != null)
                Destroy(_previewRoot);
        }

        void EnsureChrome()
        {
            EnsureSprites();
            if (transform.Find("Panel/Content") == null)
            {
                if (transform.Find("Backdrop") != null)
                    TearDownChrome();
                BuildFullChrome();
            }

            BindChromeRefs();
            EnsureTeamStrip();
            EnsureBadgeSection();
            EnsureThrusterSection();
            EnsurePickerCloseButton();
            EnsurePanelDismissesPicker();
            PaintTeamStrip();
        }

        /// <summary>Same-frame rebuild so leftover well-row badge chrome cannot sit under the new sections.</summary>
        void TearDownChrome()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
                DestroyImmediate(transform.GetChild(i).gameObject);

            _previewImage = null;
            _teamSwatch = null;
            _color2Well = null;
            _color3Well = null;
            _glow1Well = null;
            _glow2Well = null;
            _glow3Well = null;
            _paintStateLabel = null;
            _pickerPanel = null;
            _svImage = null;
            _hueImage = null;
            _svCursor = null;
            _hueCursor = null;
            _badgeChip = null;
            _badgeEmpty = null;
            _badgeCaption = null;
            _badgePicker = null;
            _thrusterStyleLabel = null;
            _thrusterColorLabel = null;
            _thrusterModeLabel = null;
            _thrusterColorWell = null;
            for (int i = 0; i < _teamChips.Length; i++)
                _teamChips[i] = null;

            if (_svTex != null)
            {
                Destroy(_svTex);
                _svTex = null;
            }

            if (_hueTex != null)
            {
                Destroy(_hueTex);
                _hueTex = null;
            }
        }

        Transform ContentRoot()
        {
            Transform content = transform.Find("Panel/Content/Viewport/Content");
            return content != null ? content : transform.Find("Panel");
        }

        void BuildFullChrome()
        {

            var overlayCanvas = GetComponent<Canvas>();
            overlayCanvas.overrideSorting = true;
            overlayCanvas.sortingOrder = OverlaySortingOrder;

            var rootRt = GetComponent<RectTransform>();
            StretchFull(rootRt);

            var backdrop = CreateUi("Backdrop", transform, typeof(Image), typeof(Button));
            StretchFull(backdrop.GetComponent<RectTransform>());
            var backdropImage = backdrop.GetComponent<Image>();
            backdropImage.color = new Color(0.01f, 0.02f, 0.04f, 0.88f);
            backdropImage.raycastTarget = true;
            var backdropBtn = backdrop.GetComponent<Button>();
            backdropBtn.transition = Selectable.Transition.None;
            backdropBtn.onClick.AddListener(Close);

            var panel = CreateUi("Panel", transform, typeof(Image));
            var panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(720f, 780f);
            var panelImage = panel.GetComponent<Image>();
            panelImage.color = PanelFill;
            panelImage.raycastTarget = true;

            var title = CreateTmp(panel.transform, "Title", "CUSTOMIZE SHIP", 26f, FontStyles.Bold);
            PlaceTop(title.rectTransform, 16f, 36f);
            title.alignment = TextAlignmentOptions.Center;
            title.color = CaptionColor;
            title.characterSpacing = 1.6f;

            var subtitle = CreateTmp(
                panel.transform,
                "Subtitle",
                "Hull paint, profile badge, and thrusters. Color1 stays the team preset.",
                14f,
                FontStyles.Normal);
            PlaceTop(subtitle.rectTransform, 50f, 22f);
            subtitle.alignment = TextAlignmentOptions.Center;
            subtitle.color = CaptionColor;

            Transform content = BuildScrollContent(panel.transform);

            _previewImage = CreateUi("Preview", content, typeof(RawImage))
                .GetComponent<RawImage>();
            var previewRt = _previewImage.rectTransform;
            previewRt.anchorMin = new Vector2(0.5f, 1f);
            previewRt.anchorMax = new Vector2(0.5f, 1f);
            previewRt.pivot = new Vector2(0.5f, 1f);
            previewRt.sizeDelta = new Vector2(420f, 176f);
            previewRt.anchoredPosition = new Vector2(0f, -8f);
            if (_previewRt == null)
            {
                _previewRt = new RenderTexture(512, 256, 16)
                {
                    name = "ShipCustomizePreview",
                    antiAliasing = 1,
                };
            }

            _previewImage.texture = _previewRt;
            _previewImage.color = Color.white;

            BuildPreviewWorld();

            var hullHeader = CreateTmp(content, "HullHeader", "HULL PAINT", 15f, FontStyles.Bold);
            PlaceTop(hullHeader.rectTransform, 196f, 22f);
            hullHeader.alignment = TextAlignmentOptions.MidlineLeft;
            hullHeader.color = CaptionColor;

            const float leftX = -132f;
            const float rightX = 132f;
            float wellY = -266f;
            _teamSwatch = CreateLockedWell(content, "COLOR 1  LOCKED", leftX, wellY);
            _color2Well = CreatePickWell(content, "COLOR 2", rightX, wellY, AccentSlot.Color2);
            wellY -= 56f;
            _color3Well = CreatePickWell(content, "COLOR 3", leftX, wellY, AccentSlot.Color3);
            _glow1Well = CreatePickWell(content, "GLOW 1", rightX, wellY, AccentSlot.Emission1);
            wellY -= 56f;
            _glow2Well = CreatePickWell(content, "GLOW 2", leftX, wellY, AccentSlot.Emission2);
            _glow3Well = CreatePickWell(content, "GLOW 3", rightX, wellY, AccentSlot.Emission3);

            _paintStateLabel = CreateTmp(content, "PaintState", "TEAM DEFAULT COLORS", 13f, FontStyles.Bold);
            PlaceTop(_paintStateLabel.rectTransform, 440f, 20f);
            _paintStateLabel.alignment = TextAlignmentOptions.Center;
            _paintStateLabel.color = CaptionColor;

            CreateContentButton(
                content,
                "Reset",
                "RESET TO DEFAULT COLORS",
                -466f,
                360f,
                new Color(0.32f, 0.16f, 0.12f, 0.96f),
                OnResetClicked);

            CreateFooterButton(
                panel.transform,
                "Done",
                "DONE",
                new Vector2(0f, 16f),
                new Vector2(300f, 44f),
                new Color(0.14f, 0.18f, 0.28f, 0.95f),
                Close);

            BuildPickerPanel(panel.transform);
        }

        Transform BuildScrollContent(Transform panel)
        {
            var scrollGo = CreateUi("Content", panel, typeof(Image), typeof(ScrollRect), typeof(RectMask2D));
            var scrollRt = scrollGo.GetComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.offsetMin = new Vector2(8f, 68f);
            scrollRt.offsetMax = new Vector2(-8f, -74f);
            var scrollImage = scrollGo.GetComponent<Image>();
            scrollImage.color = new Color(0f, 0f, 0f, 0.01f);
            scrollImage.raycastTarget = true;

            var viewportGo = CreateUi("Viewport", scrollGo.transform, typeof(Image), typeof(RectMask2D));
            StretchFull(viewportGo.GetComponent<RectTransform>());
            var viewportImage = viewportGo.GetComponent<Image>();
            viewportImage.color = Color.clear;
            viewportImage.raycastTarget = true;

            var contentGo = CreateUi("Content", viewportGo.transform);
            var contentRt = contentGo.GetComponent<RectTransform>();
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.sizeDelta = new Vector2(0f, 980f);
            contentRt.anchoredPosition = Vector2.zero;

            var scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.viewport = viewportGo.GetComponent<RectTransform>();
            scroll.content = contentRt;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 28f;
            return contentGo.transform;
        }

        void BindChromeRefs()
        {
            Transform content = ContentRoot();
            Transform panel = transform.Find("Panel");
            if (content == null)
                return;

            if (_previewImage == null)
                _previewImage = content.Find("Preview")?.GetComponent<RawImage>();
            if (_teamSwatch == null)
                _teamSwatch = content.Find("TeamWell/Well")?.GetComponent<Image>();
            if (_color2Well == null)
                _color2Well = content.Find("COLOR 2Well/Well")?.GetComponent<Image>();
            if (_color3Well == null)
                _color3Well = content.Find("COLOR 3Well/Well")?.GetComponent<Image>();
            if (_glow1Well == null)
                _glow1Well = content.Find("GLOW 1Well/Well")?.GetComponent<Image>();
            if (_glow2Well == null)
                _glow2Well = content.Find("GLOW 2Well/Well")?.GetComponent<Image>();
            if (_glow3Well == null)
                _glow3Well = content.Find("GLOW 3Well/Well")?.GetComponent<Image>();
            if (_paintStateLabel == null)
                _paintStateLabel = content.Find("PaintState")?.GetComponent<TextMeshProUGUI>();
            if (_pickerPanel == null && panel != null)
                _pickerPanel = panel.Find("ColorPicker") as RectTransform;
        }

        /// <summary>
        /// Team A–E chips so the player can preview Color1 presets. This does not
        /// change their match team — Color1 is still locked to the palette asset.
        /// </summary>
        void EnsureTeamStrip()
        {
            Transform content = ContentRoot();
            if (content == null)
                return;

            Transform existing = content.Find("TeamStrip");
            GameObject row = existing != null
                ? existing.gameObject
                : CreateUi("TeamStrip", content, typeof(Image));
            var rt = row.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(520f, 40f);
            rt.anchoredPosition = new Vector2(0f, -220f);
            row.GetComponent<Image>().color = new Color(0.07f, 0.09f, 0.14f, 0.96f);

            Transform labelTf = row.transform.Find("Label");
            TextMeshProUGUI label = labelTf != null
                ? labelTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(row.transform, "Label", "PREVIEW TEAM", 12f, FontStyles.Bold);
            var labelRt = label.rectTransform;
            labelRt.anchorMin = new Vector2(0f, 0f);
            labelRt.anchorMax = new Vector2(0.32f, 1f);
            labelRt.offsetMin = new Vector2(10f, 0f);
            labelRt.offsetMax = Vector2.zero;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.color = CaptionColor;

            for (int i = 0; i < 5; i++)
            {
                TeamId team = (TeamId)(i + 1);
                string name = "Team" + team.ToLetter();
                Transform chipTf = row.transform.Find(name);
                GameObject chipGo = chipTf != null
                    ? chipTf.gameObject
                    : CreateUi(name, row.transform, typeof(Image), typeof(Button));
                var chipRt = chipGo.GetComponent<RectTransform>();
                chipRt.anchorMin = new Vector2(0.34f, 0.15f);
                chipRt.anchorMax = new Vector2(0.34f, 0.15f);
                chipRt.pivot = new Vector2(0f, 0f);
                chipRt.sizeDelta = new Vector2(32f, 28f);
                chipRt.anchoredPosition = new Vector2(i * 36f, 0f);
                var chipImg = chipGo.GetComponent<Image>();
                chipImg.sprite = _whiteSprite;
                chipImg.color = ShipColorizeAccentApplier.Opaque(TeamColor1Palette.GetColor1(team));
                var button = chipGo.GetComponent<Button>();
                button.targetGraphic = chipImg;
                button.transition = Selectable.Transition.None;
                button.onClick.RemoveAllListeners();
                TeamId captured = team;
                button.onClick.AddListener(() => SelectPreviewTeam(captured));

                Transform letterTf = chipGo.transform.Find("Letter");
                TextMeshProUGUI letter = letterTf != null
                    ? letterTf.GetComponent<TextMeshProUGUI>()
                    : CreateTmp(chipGo.transform, "Letter", team.ToLetter(), 13f, FontStyles.Bold);
                StretchFull(letter.rectTransform);
                letter.alignment = TextAlignmentOptions.Center;
                letter.color = BodyColor;
                letter.raycastTarget = false;
                _teamChips[i] = chipImg;
            }
        }

        void SelectPreviewTeam(TeamId team)
        {
            if (team == TeamId.None)
                team = TeamId.TeamA;
            _team = team;
            HidePicker();
            PaintWells();
            PaintTeamStrip();
            PaintPreview();
            RefreshThrusterSection();
        }

        void PaintTeamStrip()
        {
            for (int i = 0; i < _teamChips.Length; i++)
            {
                Image chip = _teamChips[i];
                if (chip == null)
                    continue;
                TeamId team = (TeamId)(i + 1);
                Color fill = TeamColor1Palette.GetColor1(team);
                bool selected = team == _team;
                chip.color = selected
                    ? ShipColorizeAccentApplier.Opaque(fill)
                    : Color.Lerp(ShipColorizeAccentApplier.Opaque(fill), Color.black, 0.35f);
            }
        }

        /// <summary>
        /// Own PROFILE BADGE block — not a color-well lookalike. Opens the same grid overlay.
        /// </summary>
        void EnsureBadgeSection()
        {
            Transform content = ContentRoot();
            if (content == null)
                return;

            Transform leftover = content.Find("BadgeRow");
            if (leftover != null)
                DestroyImmediate(leftover.gameObject);

            Transform existing = content.Find("BadgeSection");
            GameObject section = existing != null
                ? existing.gameObject
                : CreateUi("BadgeSection", content, typeof(Image));
            var rt = section.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(560f, 118f);
            rt.anchoredPosition = new Vector2(0f, -524f);
            section.GetComponent<Image>().color = new Color(0.06f, 0.08f, 0.13f, 0.96f);

            Transform headerTf = section.transform.Find("Header");
            TextMeshProUGUI header = headerTf != null
                ? headerTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(section.transform, "Header", "PROFILE BADGE", 15f, FontStyles.Bold);
            var headerRt = header.rectTransform;
            headerRt.anchorMin = new Vector2(0f, 1f);
            headerRt.anchorMax = new Vector2(1f, 1f);
            headerRt.pivot = new Vector2(0.5f, 1f);
            headerRt.sizeDelta = new Vector2(-24f, 24f);
            headerRt.anchoredPosition = new Vector2(0f, -8f);
            header.alignment = TextAlignmentOptions.MidlineLeft;
            header.color = CaptionColor;

            Transform chipTf = section.transform.Find("Chip");
            GameObject chipGo = chipTf != null
                ? chipTf.gameObject
                : CreateUi("Chip", section.transform, typeof(Image), typeof(Button));
            var chipRt = chipGo.GetComponent<RectTransform>();
            chipRt.anchorMin = new Vector2(0f, 0f);
            chipRt.anchorMax = new Vector2(0f, 0f);
            chipRt.pivot = new Vector2(0f, 0f);
            chipRt.sizeDelta = new Vector2(72f, 72f);
            chipRt.anchoredPosition = new Vector2(16f, 12f);
            var chipFill = chipGo.GetComponent<Image>();
            chipFill.sprite = _whiteSprite;
            chipFill.color = new Color(0.12f, 0.16f, 0.22f, 0.95f);
            chipFill.raycastTarget = true;

            Transform iconTf = chipGo.transform.Find("Icon");
            GameObject iconGo = iconTf != null
                ? iconTf.gameObject
                : CreateUi("Icon", chipGo.transform, typeof(Image));
            StretchFull(iconGo.GetComponent<RectTransform>());
            iconGo.GetComponent<RectTransform>().offsetMin = new Vector2(8f, 8f);
            iconGo.GetComponent<RectTransform>().offsetMax = new Vector2(-8f, -8f);
            _badgeChip = iconGo.GetComponent<Image>();
            _badgeChip.preserveAspect = true;
            _badgeChip.raycastTarget = false;

            Transform emptyTf = chipGo.transform.Find("EmptyMark");
            GameObject emptyGo = emptyTf != null
                ? emptyTf.gameObject
                : CreateUi("EmptyMark", chipGo.transform, typeof(TextMeshProUGUI));
            StretchFull(emptyGo.GetComponent<RectTransform>());
            _badgeEmpty = emptyGo.GetComponent<TextMeshProUGUI>();
            _badgeEmpty.text = "+";
            _badgeEmpty.fontSize = 28f;
            _badgeEmpty.alignment = TextAlignmentOptions.Center;
            _badgeEmpty.color = CaptionColor;
            _badgeEmpty.raycastTarget = false;

            Transform hintTf = section.transform.Find("Status");
            TextMeshProUGUI hint = hintTf != null
                ? hintTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(section.transform, "Status", "No badge", 14f, FontStyles.Normal);
            var hintRt = hint.rectTransform;
            hintRt.anchorMin = new Vector2(0f, 1f);
            hintRt.anchorMax = new Vector2(1f, 1f);
            hintRt.pivot = new Vector2(0f, 1f);
            hintRt.sizeDelta = new Vector2(-120f, 22f);
            hintRt.anchoredPosition = new Vector2(104f, -36f);
            hint.alignment = TextAlignmentOptions.MidlineLeft;
            hint.color = BodyColor;
            _badgeCaption = hint;

            Transform btnTf = section.transform.Find("Choose");
            GameObject btnGo = btnTf != null
                ? btnTf.gameObject
                : CreateUi("Choose", section.transform, typeof(Image), typeof(Button));
            var btnRt = btnGo.GetComponent<RectTransform>();
            btnRt.anchorMin = new Vector2(0f, 0f);
            btnRt.anchorMax = new Vector2(0f, 0f);
            btnRt.pivot = new Vector2(0f, 0f);
            btnRt.sizeDelta = new Vector2(180f, 36f);
            btnRt.anchoredPosition = new Vector2(104f, 14f);
            var btnBg = btnGo.GetComponent<Image>();
            btnBg.sprite = _whiteSprite;
            btnBg.color = new Color(0.16f, 0.22f, 0.34f, 0.96f);
            var chooseBtn = btnGo.GetComponent<Button>();
            chooseBtn.targetGraphic = btnBg;
            chooseBtn.transition = Selectable.Transition.None;
            chooseBtn.onClick.RemoveAllListeners();
            chooseBtn.onClick.AddListener(OpenBadgePicker);

            Transform btnLabelTf = btnGo.transform.Find("Label");
            TextMeshProUGUI btnLabel = btnLabelTf != null
                ? btnLabelTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(btnGo.transform, "Label", "CHOOSE BADGE", 13f, FontStyles.Bold);
            StretchFull(btnLabel.rectTransform);
            btnLabel.alignment = TextAlignmentOptions.Center;
            btnLabel.color = BodyColor;
            btnLabel.raycastTarget = false;

            _badgePicker = section.GetComponent<MainMenuBadgePicker>();
            if (_badgePicker == null)
                _badgePicker = section.AddComponent<MainMenuBadgePicker>();
            _badgePicker.Configure(_badgeChip, chipFill, _badgeEmpty, _badgeCaption);

            var chipBtn = chipGo.GetComponent<Button>();
            chipBtn.targetGraphic = chipFill;
            chipBtn.transition = Selectable.Transition.None;
            chipBtn.onClick.RemoveAllListeners();
            chipBtn.onClick.AddListener(OpenBadgePicker);
        }

        void OpenBadgePicker()
        {
            HidePicker();
            if (_badgePicker != null)
                _badgePicker.OpenOverlay();
        }

        /// <summary>Player-chosen flame type and team-follow vs locked color.</summary>
        void EnsureThrusterSection()
        {
            Transform content = ContentRoot();
            if (content == null)
                return;

            Transform existing = content.Find("ThrusterSection");
            GameObject section = existing != null
                ? existing.gameObject
                : CreateUi("ThrusterSection", content, typeof(Image));
            var rt = section.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(560f, 210f);
            rt.anchoredPosition = new Vector2(0f, -656f);
            section.GetComponent<Image>().color = new Color(0.06f, 0.08f, 0.13f, 0.96f);

            Transform headerTf = section.transform.Find("Header");
            TextMeshProUGUI header = headerTf != null
                ? headerTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(section.transform, "Header", "THRUSTERS", 15f, FontStyles.Bold);
            var headerRt = header.rectTransform;
            headerRt.anchorMin = new Vector2(0f, 1f);
            headerRt.anchorMax = new Vector2(1f, 1f);
            headerRt.pivot = new Vector2(0.5f, 1f);
            headerRt.sizeDelta = new Vector2(-24f, 24f);
            headerRt.anchoredPosition = new Vector2(0f, -8f);
            header.alignment = TextAlignmentOptions.MidlineLeft;
            header.color = CaptionColor;

            Transform blurbTf = section.transform.Find("Blurb");
            TextMeshProUGUI blurb = blurbTf != null
                ? blurbTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(
                    section.transform,
                    "Blurb",
                    "Four jet types. Follow team uses Color1, or lock a color with the picker.",
                    12f,
                    FontStyles.Normal);
            var blurbRt = blurb.rectTransform;
            blurbRt.anchorMin = new Vector2(0f, 1f);
            blurbRt.anchorMax = new Vector2(1f, 1f);
            blurbRt.pivot = new Vector2(0.5f, 1f);
            blurbRt.sizeDelta = new Vector2(-24f, 34f);
            blurbRt.anchoredPosition = new Vector2(0f, -30f);
            blurb.text = "Four jet types. Follow team uses Color1, or lock a color with the picker.";
            blurb.alignment = TextAlignmentOptions.TopLeft;
            blurb.color = CaptionColor;

            _thrusterStyleLabel = EnsureCycleRow(
                section.transform,
                "StyleRow",
                "TYPE",
                -68f,
                () => CycleThrusterStyle(-1),
                () => CycleThrusterStyle(1));

            Transform leftoverColor = section.transform.Find("ColorRow");
            if (leftoverColor != null)
                DestroyImmediate(leftoverColor.gameObject);

            Transform colorRowTf = section.transform.Find("ThrusterColorRow");
            GameObject colorRow = colorRowTf != null
                ? colorRowTf.gameObject
                : CreateUi("ThrusterColorRow", section.transform, typeof(Image));
            var colorRt = colorRow.GetComponent<RectTransform>();
            colorRt.anchorMin = new Vector2(0.5f, 1f);
            colorRt.anchorMax = new Vector2(0.5f, 1f);
            colorRt.pivot = new Vector2(0.5f, 1f);
            colorRt.sizeDelta = new Vector2(528f, 44f);
            colorRt.anchoredPosition = new Vector2(0f, -112f);
            colorRow.GetComponent<Image>().color = new Color(0.08f, 0.1f, 0.15f, 0.96f);

            Transform colorCapTf = colorRow.transform.Find("Caption");
            TextMeshProUGUI colorCap = colorCapTf != null
                ? colorCapTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(colorRow.transform, "Caption", "COLOR", 12f, FontStyles.Bold);
            var colorCapRt = colorCap.rectTransform;
            colorCapRt.anchorMin = new Vector2(0f, 0f);
            colorCapRt.anchorMax = new Vector2(0.22f, 1f);
            colorCapRt.offsetMin = new Vector2(10f, 0f);
            colorCapRt.offsetMax = Vector2.zero;
            colorCap.alignment = TextAlignmentOptions.MidlineLeft;
            colorCap.color = CaptionColor;

            Transform colorValueTf = colorRow.transform.Find("Value");
            _thrusterColorLabel = colorValueTf != null
                ? colorValueTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(colorRow.transform, "Value", "TEAM COLOR", 13f, FontStyles.Bold);
            var colorValueRt = _thrusterColorLabel.rectTransform;
            colorValueRt.anchorMin = new Vector2(0.22f, 0f);
            colorValueRt.anchorMax = new Vector2(0.62f, 1f);
            colorValueRt.offsetMin = Vector2.zero;
            colorValueRt.offsetMax = Vector2.zero;
            _thrusterColorLabel.alignment = TextAlignmentOptions.MidlineLeft;
            _thrusterColorLabel.color = BodyColor;

            Transform wellTf = colorRow.transform.Find("Well");
            GameObject wellGo = wellTf != null
                ? wellTf.gameObject
                : CreateUi("Well", colorRow.transform, typeof(Image), typeof(Button));
            var wellRt = wellGo.GetComponent<RectTransform>();
            wellRt.anchorMin = new Vector2(1f, 0.5f);
            wellRt.anchorMax = new Vector2(1f, 0.5f);
            wellRt.pivot = new Vector2(1f, 0.5f);
            wellRt.sizeDelta = new Vector2(72f, 30f);
            wellRt.anchoredPosition = new Vector2(-16f, 0f);
            _thrusterColorWell = wellGo.GetComponent<Image>();
            _thrusterColorWell.sprite = _whiteSprite;
            _thrusterColorWell.raycastTarget = true;
            var wellBtn = wellGo.GetComponent<Button>();
            if (wellBtn == null)
                wellBtn = wellGo.AddComponent<Button>();
            wellBtn.targetGraphic = _thrusterColorWell;
            wellBtn.transition = Selectable.Transition.None;
            wellBtn.onClick.RemoveAllListeners();
            wellBtn.onClick.AddListener(OpenThrusterColorPicker);

            Transform modeTf = section.transform.Find("Mode");
            GameObject modeGo = modeTf != null
                ? modeTf.gameObject
                : CreateUi("Mode", section.transform, typeof(Image), typeof(Button));
            var modeRt = modeGo.GetComponent<RectTransform>();
            modeRt.anchorMin = new Vector2(0.5f, 1f);
            modeRt.anchorMax = new Vector2(0.5f, 1f);
            modeRt.pivot = new Vector2(0.5f, 1f);
            modeRt.sizeDelta = new Vector2(320f, 34f);
            modeRt.anchoredPosition = new Vector2(-90f, -156f);
            var modeBg = modeGo.GetComponent<Image>();
            modeBg.sprite = _whiteSprite;
            modeBg.color = new Color(0.16f, 0.22f, 0.34f, 0.96f);
            var modeBtn = modeGo.GetComponent<Button>();
            modeBtn.targetGraphic = modeBg;
            modeBtn.transition = Selectable.Transition.None;
            modeBtn.onClick.RemoveAllListeners();
            modeBtn.onClick.AddListener(ToggleThrusterFollowTeam);

            Transform modeLabelTf = modeGo.transform.Find("Label");
            _thrusterModeLabel = modeLabelTf != null
                ? modeLabelTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(modeGo.transform, "Label", "FOLLOW TEAM COLOR", 12f, FontStyles.Bold);
            StretchFull(_thrusterModeLabel.rectTransform);
            _thrusterModeLabel.alignment = TextAlignmentOptions.Center;
            _thrusterModeLabel.color = BodyColor;
            _thrusterModeLabel.raycastTarget = false;

            Transform resetTf = section.transform.Find("ResetThrusters");
            GameObject resetGo = resetTf != null
                ? resetTf.gameObject
                : CreateUi("ResetThrusters", section.transform, typeof(Image), typeof(Button));
            var resetRt = resetGo.GetComponent<RectTransform>();
            resetRt.anchorMin = new Vector2(0.5f, 1f);
            resetRt.anchorMax = new Vector2(0.5f, 1f);
            resetRt.pivot = new Vector2(0.5f, 1f);
            resetRt.sizeDelta = new Vector2(150f, 34f);
            resetRt.anchoredPosition = new Vector2(186f, -156f);
            var resetBg = resetGo.GetComponent<Image>();
            resetBg.sprite = _whiteSprite;
            resetBg.color = new Color(0.32f, 0.16f, 0.12f, 0.96f);
            var resetBtn = resetGo.GetComponent<Button>();
            resetBtn.targetGraphic = resetBg;
            resetBtn.transition = Selectable.Transition.None;
            resetBtn.onClick.RemoveAllListeners();
            resetBtn.onClick.AddListener(OnResetThrustersClicked);

            Transform resetLabelTf = resetGo.transform.Find("Label");
            TextMeshProUGUI resetLabel = resetLabelTf != null
                ? resetLabelTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(resetGo.transform, "Label", "RESET JETS", 12f, FontStyles.Bold);
            StretchFull(resetLabel.rectTransform);
            resetLabel.alignment = TextAlignmentOptions.Center;
            resetLabel.color = BodyColor;
            resetLabel.raycastTarget = false;
        }

        TextMeshProUGUI EnsureCycleRow(
            Transform parent,
            string name,
            string caption,
            float y,
            Action onPrev,
            Action onNext)
        {
            Transform existing = parent.Find(name);
            GameObject row = existing != null
                ? existing.gameObject
                : CreateUi(name, parent, typeof(Image));
            var rt = row.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(528f, 38f);
            rt.anchoredPosition = new Vector2(0f, y);
            row.GetComponent<Image>().color = new Color(0.08f, 0.1f, 0.15f, 0.96f);

            Transform capTf = row.transform.Find("Caption");
            TextMeshProUGUI cap = capTf != null
                ? capTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(row.transform, "Caption", caption, 12f, FontStyles.Bold);
            var capRt = cap.rectTransform;
            capRt.anchorMin = new Vector2(0f, 0f);
            capRt.anchorMax = new Vector2(0.22f, 1f);
            capRt.offsetMin = new Vector2(10f, 0f);
            capRt.offsetMax = Vector2.zero;
            cap.text = caption;
            cap.alignment = TextAlignmentOptions.MidlineLeft;
            cap.color = CaptionColor;

            EnsureArrow(row.transform, "Prev", new Vector2(0.24f, 0.5f), "<", onPrev);
            EnsureArrow(row.transform, "Next", new Vector2(0.94f, 0.5f), ">", onNext);

            Transform valueTf = row.transform.Find("Value");
            TextMeshProUGUI value = valueTf != null
                ? valueTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(row.transform, "Value", caption, 14f, FontStyles.Bold);
            var valueRt = value.rectTransform;
            valueRt.anchorMin = new Vector2(0.32f, 0f);
            valueRt.anchorMax = new Vector2(0.80f, 1f);
            valueRt.offsetMin = Vector2.zero;
            valueRt.offsetMax = Vector2.zero;
            value.alignment = TextAlignmentOptions.Center;
            value.color = BodyColor;
            return value;
        }

        void EnsureArrow(Transform parent, string name, Vector2 anchor, string glyph, Action onClick)
        {
            Transform existing = parent.Find(name);
            GameObject go = existing != null
                ? existing.gameObject
                : CreateUi(name, parent, typeof(Image), typeof(Button));
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(34f, 28f);
            rt.anchoredPosition = Vector2.zero;
            var bg = go.GetComponent<Image>();
            bg.sprite = _whiteSprite;
            bg.color = new Color(0.18f, 0.24f, 0.36f, 0.96f);
            var button = go.GetComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => onClick());

            Transform labelTf = go.transform.Find("Label");
            TextMeshProUGUI label = labelTf != null
                ? labelTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(go.transform, "Label", glyph, 16f, FontStyles.Bold);
            StretchFull(label.rectTransform);
            label.text = glyph;
            label.alignment = TextAlignmentOptions.Center;
            label.color = BodyColor;
            label.raycastTarget = false;
        }

        void RefreshThrusterSection()
        {
            var style = LocalPlayerThrusterStyle.Get();
            int styleIndex = LocalPlayerThrusterStyle.ResolveStyleIndex(style);
            if (_thrusterStyleLabel != null)
                _thrusterStyleLabel.text = ThrusterVfxBank.GetStyleDisplayName(styleIndex).ToUpperInvariant();

            Color32 tint = LocalPlayerThrusterStyle.ResolveTint(style, _team);
            if (_thrusterColorWell != null)
                _thrusterColorWell.color = (Color)tint;

            bool followTeam = style.UseTeamColor;
            if (_thrusterColorLabel != null)
                _thrusterColorLabel.text = followTeam ? "TEAM COLOR" : "CUSTOM";
            if (_thrusterModeLabel != null)
                _thrusterModeLabel.text = followTeam ? "FOLLOW TEAM COLOR" : "LOCKED CHOSEN COLOR";
        }

        LocalPlayerThrusterStyle.Style BeginCustomThruster()
        {
            var style = LocalPlayerThrusterStyle.Get();
            if (style.IsCustom)
                return style;

            style.HasCustom = 1;
            style.FollowTeam = 1;
            style.StyleIndex = (byte)ThrusterVfxBank.DefaultStyleIndex;
            style.ColorPacked = ShipAccentColors.Pack(
                ShipColorizeAccentApplier.Opaque(TeamColor1Palette.GetColor1(_team)));
            return style;
        }

        void PersistThruster(LocalPlayerThrusterStyle.Style style, bool rebuildJets)
        {
            LocalPlayerThrusterStyle.Set(style);
            ShipAccentColorsRpcClient.NotifyChanged();
            if (rebuildJets)
                ShipPropulsionVisualApplier.RebuildAllLive();
            else
                ShipPropulsionVisualApplier.ApplyTintToAllLive();
            RefreshThrusterSection();
        }

        void CycleThrusterStyle(int delta)
        {
            var style = BeginCustomThruster();
            style.StyleIndex = (byte)ThrusterVfxBank.WrapStyleIndex(style.StyleIndex + delta);
            PersistThruster(style, rebuildJets: true);
        }

        void OpenThrusterColorPicker()
        {
            var style = BeginCustomThruster();
            style.FollowTeam = 0;
            if (style.ColorPacked == 0)
            {
                style.ColorPacked = ShipAccentColors.Pack(
                    ShipColorizeAccentApplier.Opaque(TeamColor1Palette.GetColor1(_team)));
            }

            LocalPlayerThrusterStyle.Set(style);
            RefreshThrusterSection();
            OpenPicker(AccentSlot.Thruster);
        }

        void ToggleThrusterFollowTeam()
        {
            var style = BeginCustomThruster();
            bool follow = style.FollowTeam != 0;
            style.FollowTeam = (byte)(follow ? 0 : 1);
            if (style.FollowTeam == 0 && style.ColorPacked == 0)
            {
                style.ColorPacked = ShipAccentColors.Pack(
                    ShipColorizeAccentApplier.Opaque(TeamColor1Palette.GetColor1(_team)));
            }

            PersistThruster(style, rebuildJets: false);
            if (style.FollowTeam == 0)
                OpenPicker(AccentSlot.Thruster);
        }

        void OnResetThrustersClicked()
        {
            LocalPlayerThrusterStyle.Clear();
            ShipAccentColorsRpcClient.NotifyChanged();
            ShipPropulsionVisualApplier.RebuildAllLive();
            RefreshThrusterSection();
        }

        /// <summary>X on the HSV popup so the palette can leave without closing the studio.</summary>
        void EnsurePickerCloseButton()
        {
            if (_pickerPanel == null)
                return;

            Transform existing = _pickerPanel.Find("Close");
            GameObject go = existing != null
                ? existing.gameObject
                : CreateUi("Close", _pickerPanel, typeof(Image), typeof(Button));
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(56f, 22f);
            rt.anchoredPosition = new Vector2(-6f, -6f);
            var bg = go.GetComponent<Image>();
            bg.sprite = _whiteSprite;
            bg.color = new Color(0.22f, 0.14f, 0.16f, 0.96f);
            var button = go.GetComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(HidePicker);

            Transform labelTf = go.transform.Find("Label");
            TextMeshProUGUI tmp = labelTf != null
                ? labelTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(go.transform, "Label", "CLOSE", 11f, FontStyles.Bold);
            StretchFull(tmp.rectTransform);
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = BodyColor;
            tmp.raycastTarget = false;
        }

        /// <summary>Empty glass on the studio panel hides the HSV popup; wells stay on top.</summary>
        void EnsurePanelDismissesPicker()
        {
            Transform panel = transform.Find("Panel");
            if (panel == null)
                return;

            var button = panel.GetComponent<Button>();
            if (button == null)
                button = panel.gameObject.AddComponent<Button>();
            button.targetGraphic = panel.GetComponent<Image>();
            button.transition = Selectable.Transition.None;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(HidePicker);

            if (_previewImage == null)
                return;
            var previewBtn = _previewImage.GetComponent<Button>();
            if (previewBtn == null)
                previewBtn = _previewImage.gameObject.AddComponent<Button>();
            previewBtn.targetGraphic = _previewImage;
            previewBtn.transition = Selectable.Transition.None;
            previewBtn.onClick.RemoveAllListeners();
            previewBtn.onClick.AddListener(HidePicker);
        }

        void EnsureSprites()
        {
            if (_whiteSprite == null)
            {
                _whiteTex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _whiteTex.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
                _whiteTex.Apply(false, true);
                _whiteSprite = Sprite.Create(_whiteTex, new Rect(0f, 0f, 2f, 2f), new Vector2(0.5f, 0.5f), 2f);
            }

            if (_ringSprite == null)
            {
                const int n = 24;
                _ringTex = new Texture2D(n, n, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                float cx = (n - 1) * 0.5f;
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cx) * (y - cx));
                        float ring = 1f - Mathf.Abs(d - 8.5f) / 1.8f;
                        _ringTex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(ring)));
                    }
                }

                _ringTex.Apply(false, true);
                _ringSprite = Sprite.Create(_ringTex, new Rect(0f, 0f, n, n), new Vector2(0.5f, 0.5f), n);
            }
        }

        Image CreateLockedWell(Transform parent, string caption, float x, float y)
        {
            var row = CreateWellRow(parent, "TeamWell", caption, x, y);
            var well = row.transform.Find("Well").GetComponent<Image>();
            well.raycastTarget = false;
            var button = well.GetComponent<Button>();
            if (button != null)
                button.enabled = false;
            return well;
        }

        Image CreatePickWell(Transform parent, string caption, float x, float y, AccentSlot slot)
        {
            var row = CreateWellRow(parent, caption + "Well", caption, x, y);
            var wellGo = row.transform.Find("Well").gameObject;
            var well = wellGo.GetComponent<Image>();
            well.raycastTarget = true;
            var button = wellGo.GetComponent<Button>();
            if (button == null)
                button = wellGo.AddComponent<Button>();
            button.targetGraphic = well;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => OpenPicker(slot));
            return well;
        }

        GameObject CreateWellRow(Transform parent, string name, string caption, float x, float y)
        {
            var row = CreateUi(name, parent, typeof(Image));
            var rt = row.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(248f, 52f);
            rt.anchoredPosition = new Vector2(x, y);
            row.GetComponent<Image>().color = new Color(0.07f, 0.09f, 0.14f, 0.96f);

            var label = CreateTmp(row.transform, "Label", caption, 13f, FontStyles.Bold);
            var labelRt = label.rectTransform;
            labelRt.anchorMin = new Vector2(0f, 0f);
            labelRt.anchorMax = new Vector2(0.58f, 1f);
            labelRt.offsetMin = new Vector2(10f, 0f);
            labelRt.offsetMax = Vector2.zero;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.color = CaptionColor;

            var wellGo = CreateUi("Well", row.transform, typeof(Image), typeof(Button));
            var wellRt = wellGo.GetComponent<RectTransform>();
            wellRt.anchorMin = new Vector2(0.62f, 0.18f);
            wellRt.anchorMax = new Vector2(0.94f, 0.82f);
            wellRt.offsetMin = Vector2.zero;
            wellRt.offsetMax = Vector2.zero;
            var wellImg = wellGo.GetComponent<Image>();
            wellImg.sprite = _whiteSprite;
            wellImg.type = Image.Type.Simple;
            wellImg.color = Color.white;
            return row;
        }

        void CreateContentButton(
            Transform parent,
            string name,
            string label,
            float y,
            float width,
            Color fill,
            Action onClick)
        {
            var go = CreateUi(name, parent, typeof(Image), typeof(Button));
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(width, 44f);
            rt.anchoredPosition = new Vector2(0f, y);
            var bg = go.GetComponent<Image>();
            bg.color = fill;
            var button = go.GetComponent<Button>();
            button.targetGraphic = bg;
            button.onClick.AddListener(() => onClick());
            var tmp = CreateTmp(go.transform, "Label", label, 14f, FontStyles.Bold);
            StretchFull(tmp.rectTransform);
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = BodyColor;
            tmp.raycastTarget = false;
        }

        void CreateFooterButton(
            Transform parent,
            string name,
            string label,
            Vector2 anchored,
            Vector2 size,
            Color fill,
            Action onClick)
        {
            var go = CreateUi(name, parent, typeof(Image), typeof(Button));
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = size;
            rt.anchoredPosition = anchored;
            var bg = go.GetComponent<Image>();
            bg.color = fill;
            var button = go.GetComponent<Button>();
            button.targetGraphic = bg;
            button.onClick.AddListener(() => onClick());
            var tmp = CreateTmp(go.transform, "Label", label, 14f, FontStyles.Bold);
            StretchFull(tmp.rectTransform);
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = BodyColor;
            tmp.raycastTarget = false;
        }

        void BuildPickerPanel(Transform parent)
        {
            _pickerPanel = CreateUi("ColorPicker", parent, typeof(Image)).GetComponent<RectTransform>();
            _pickerPanel.anchorMin = new Vector2(0.5f, 1f);
            _pickerPanel.anchorMax = new Vector2(0.5f, 1f);
            _pickerPanel.pivot = new Vector2(0.5f, 1f);
            _pickerPanel.sizeDelta = new Vector2(248f, 222f);
            _pickerPanel.anchoredPosition = new Vector2(0f, -84f);
            _pickerPanel.GetComponent<Image>().color = new Color(0.04f, 0.06f, 0.1f, 0.98f);

            _svTex = new Texture2D(SvSize, SvSize, TextureFormat.RGB24, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            _hueTex = new Texture2D(HueBarW, HueBarH, TextureFormat.RGB24, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            PaintHueBar();

            var svGo = CreateUi("Sv", _pickerPanel, typeof(RawImage), typeof(RectMask2D), typeof(SvPad));
            _svImage = svGo.GetComponent<RawImage>();
            var svRt = _svImage.rectTransform;
            svRt.anchorMin = new Vector2(0f, 0.5f);
            svRt.anchorMax = new Vector2(0f, 0.5f);
            svRt.pivot = new Vector2(0f, 0.5f);
            svRt.sizeDelta = new Vector2(SvSize, SvSize);
            svRt.anchoredPosition = new Vector2(12f, 0f);
            _svImage.texture = _svTex;
            svGo.GetComponent<SvPad>().OnNorm = OnSvPicked;
            svGo.GetComponent<SvPad>().OnReleased = FlushPersist;

            var hueGo = CreateUi("Hue", _pickerPanel, typeof(RawImage), typeof(RectMask2D), typeof(HuePad));
            _hueImage = hueGo.GetComponent<RawImage>();
            var hueRt = _hueImage.rectTransform;
            hueRt.anchorMin = new Vector2(1f, 0.5f);
            hueRt.anchorMax = new Vector2(1f, 0.5f);
            hueRt.pivot = new Vector2(1f, 0.5f);
            hueRt.sizeDelta = new Vector2(HueBarW, HueBarH);
            hueRt.anchoredPosition = new Vector2(-12f, 0f);
            _hueImage.texture = _hueTex;
            hueGo.GetComponent<HuePad>().OnNorm = OnHuePicked;
            hueGo.GetComponent<HuePad>().OnReleased = FlushPersist;

            _svCursor = CreateUi("SvCursor", _svImage.transform, typeof(Image)).GetComponent<RectTransform>();
            _svCursor.anchorMin = Vector2.zero;
            _svCursor.anchorMax = Vector2.zero;
            _svCursor.pivot = new Vector2(0.5f, 0.5f);
            _svCursor.sizeDelta = new Vector2(16f, 16f);
            var svCursorImg = _svCursor.GetComponent<Image>();
            svCursorImg.sprite = _ringSprite;
            svCursorImg.color = Color.white;
            svCursorImg.raycastTarget = false;

            _hueCursor = CreateUi("HueCursor", _hueImage.transform, typeof(Image)).GetComponent<RectTransform>();
            _hueCursor.anchorMin = new Vector2(0.5f, 0f);
            _hueCursor.anchorMax = new Vector2(0.5f, 0f);
            _hueCursor.pivot = new Vector2(0.5f, 0.5f);
            _hueCursor.sizeDelta = new Vector2(HueBarW + 8f, 6f);
            var hueCursorImg = _hueCursor.GetComponent<Image>();
            hueCursorImg.sprite = _whiteSprite;
            hueCursorImg.color = Color.white;
            hueCursorImg.raycastTarget = false;

            _pickerPanel.gameObject.SetActive(false);
        }

        void BuildPreviewWorld()
        {
            if (_previewRoot != null)
            {
                if (_previewCam != null)
                    _previewCam.targetTexture = _previewRt;
                SetLayerRecursive(_previewRoot, PreviewLayer());
                return;
            }

            _previewRoot = new GameObject("ShipCustomizePreviewRoot");
            _previewRoot.hideFlags = HideFlags.HideAndDontSave;
            _previewRoot.transform.position = new Vector3(0f, -420f, 0f);

            var camGo = new GameObject("Cam");
            camGo.transform.SetParent(_previewRoot.transform, false);
            _previewCam = camGo.AddComponent<Camera>();
            _previewCam.clearFlags = CameraClearFlags.SolidColor;
            _previewCam.backgroundColor = new Color(0.02f, 0.03f, 0.05f, 1f);
            _previewCam.orthographic = true;
            _previewCam.orthographicSize = 1.85f;
            _previewCam.nearClipPlane = 0.1f;
            _previewCam.farClipPlane = 20f;
            _previewCam.targetTexture = _previewRt;
            _previewCam.depth = -80;
            _previewCam.allowHDR = false;
            _previewCam.cullingMask = 1 << PreviewLayer();
            var listener = camGo.GetComponent<AudioListener>();
            if (listener != null)
                Destroy(listener);

            SetLayerRecursive(_previewRoot, PreviewLayer());
        }

        /// <summary>
        /// The preview camera is not a child of the overlay. Leaving it enabled
        /// (or clearing its RenderTexture) composites a tiny hull into the game view.
        /// </summary>
        void SetPreviewWorldActive(bool active)
        {
            if (_previewCam != null)
            {
                if (active)
                {
                    _previewCam.targetTexture = _previewRt;
                    _previewCam.cullingMask = 1 << PreviewLayer();
                    _previewCam.enabled = true;
                }
                else
                {
                    _previewCam.enabled = false;
                    _previewCam.targetTexture = null;
                }
            }

            if (_previewRoot != null)
                _previewRoot.SetActive(active);
        }

        static int PreviewLayer()
        {
            int layer = LayerMask.NameToLayer("ShipPreview");
            return layer >= 0 ? layer : 6;
        }

        static void SetLayerRecursive(GameObject go, int layer)
        {
            if (go == null)
                return;
            go.layer = layer;
            Transform t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursive(t.GetChild(i).gameObject, layer);
        }

        void ApplyTeamToPreview()
        {
            if (_previewHull != null)
                Destroy(_previewHull);

            _previewFamily = null;
            var config = PlanetShipFamilyConfig.LoadDefault();
            if (config != null && config.families != null && config.families.Count > 0)
                _previewFamily = config.families[0].shipFamilyDefinition;

            GameObject hull = null;
            if (_previewFamily != null && _previewFamily.TryGetVisualPrefabForLevel(1, out GameObject prefab) && prefab != null)
            {
                hull = Instantiate(prefab);
                hull.name = "CustomizePreviewHull";
                ShipVisualApplier.StripPhysicsAndNetworking(hull, keepColliders: false);
                ShipVisualApplier.ApplyTeamMaterials(_previewFamily, hull, _team);
            }
            else
            {
                hull = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(hull.GetComponent<Collider>());
            }

            hull.transform.SetParent(_previewRoot.transform, false);
            hull.transform.localPosition = Vector3.forward * 4f;
            hull.transform.localRotation = Quaternion.Euler(18f, 140f, 0f);
            SetLayerRecursive(hull, PreviewLayer());
            _previewCam.transform.localPosition = new Vector3(0f, 0.45f, 0f);
            _previewCam.transform.LookAt(hull.transform);
            _previewHull = hull;
            ShipColorizeAccentApplier.CaptureBaseAndApply(hull, ShipAccentColors.Default, _team);
            if (_teamSwatch != null)
                _teamSwatch.color = ShipColorizeAccentApplier.Opaque(TeamColor1Palette.GetColor1(_team));
        }

        void RefreshFromStore()
        {
            ShipAccentColors stored = LocalPlayerShipAccents.Get();
            _hasCustom = stored.IsCustom;
            if (_hasCustom)
            {
                _color2 = stored.Color2;
                _color3 = stored.Color3;
                _emission1 = stored.Emission;
                _emission2 = stored.Emission2;
                _emission3 = stored.Emission3;
            }
            else if (!ShipColorizeAccentApplier.TryReadBakedAccentsFromRoot(
                         _previewHull,
                         out _color2,
                         out _color3,
                         out _emission1,
                         out _emission2,
                         out _emission3))
            {
                Material sample = null;
                if (_previewFamily != null)
                {
                    var mats = _previewFamily.GetMaterialsForTeam(_team);
                    if (mats != null && mats.Count > 0)
                        sample = mats[0];
                }

                ShipColorizeAccentApplier.ReadBakedAccents(
                    sample,
                    out _color2,
                    out _color3,
                    out _emission1,
                    out _emission2,
                    out _emission3);
            }

            PaintWells();
            PaintPreview();
        }

        void OpenPicker(AccentSlot slot)
        {
            _pickerSlot = slot;
            Color32 current = ColorForSlot(slot);
            Color.RGBToHSV((Color)current, out _hue, out _sat, out _val);
            PaintSvSquare();
            UpdatePickerCursors();
            _pickerPanel.gameObject.SetActive(true);
        }

        void HidePicker()
        {
            HidePicker(persist: true);
        }

        void HidePicker(bool persist)
        {
            if (_pickerPanel != null)
                _pickerPanel.gameObject.SetActive(false);
            if (persist)
                FlushPersist();
        }

        void OnSvPicked(Vector2 norm)
        {
            _sat = Mathf.Clamp01(norm.x);
            _val = Mathf.Clamp01(norm.y);
            ApplyPickerColor(flush: false);
            UpdatePickerCursors();
        }

        void OnHuePicked(float norm)
        {
            _hue = Mathf.Clamp01(norm);
            PaintSvSquare();
            ApplyPickerColor(flush: false);
            UpdatePickerCursors();
        }

        void ApplyPickerColor(bool flush)
        {
            Color32 color = ShipColorizeAccentApplier.Opaque(Color.HSVToRGB(_hue, _sat, _val));
            if (_pickerSlot == AccentSlot.Thruster)
            {
                var style = BeginCustomThruster();
                style.FollowTeam = 0;
                style.ColorPacked = ShipAccentColors.Pack(color);
                LocalPlayerThrusterStyle.Set(style);
                RefreshThrusterSection();
                ShipPropulsionVisualApplier.ApplyTintToAllLive();
                if (flush)
                    ShipAccentColorsRpcClient.NotifyChanged();
                return;
            }

            SetSlotColor(_pickerSlot, color);
            _hasCustom = true;
            PersistAndPreview(flush);
        }

        /// <summary>
        /// Drops saved Color2 / Color3 / Glow 1–3 so the hull uses the Colorize
        /// material defaults again. Color1 stays the team swatch.
        /// </summary>
        void OnResetClicked()
        {
            // Clear first and skip persist — HidePicker used to FlushPersist while
            // _hasCustom was still true and write the old palette back over Default.
            _hasCustom = false;
            LocalPlayerShipAccents.Clear();
            ShipAccentColorsRpcClient.NotifyChanged();
            HidePicker(persist: false);
            RefreshFromStore();
        }

        void PersistAndPreview(bool flush)
        {
            var accents = CurrentCustomAccents();
            LocalPlayerShipAccents.Set(accents, flushToDisk: flush);
            if (flush)
                ShipAccentColorsRpcClient.NotifyChanged();
            PaintWells();
            PaintPreview();
        }

        void FlushPersist()
        {
            if (_pickerSlot == AccentSlot.Thruster)
                ShipAccentColorsRpcClient.NotifyChanged();
            if (!_hasCustom)
                return;
            LocalPlayerShipAccents.Set(CurrentCustomAccents(), flushToDisk: true);
            ShipAccentColorsRpcClient.NotifyChanged();
        }

        ShipAccentColors CurrentCustomAccents()
        {
            return ShipAccentColors.FromCustom(_color2, _color3, _emission1, _emission2, _emission3);
        }

        void PaintWells()
        {
            if (_teamSwatch != null)
                _teamSwatch.color = ShipColorizeAccentApplier.Opaque(TeamColor1Palette.GetColor1(_team));
            if (_color2Well != null)
                _color2Well.color = (Color)_color2;
            if (_color3Well != null)
                _color3Well.color = (Color)_color3;
            if (_glow1Well != null)
                _glow1Well.color = (Color)_emission1;
            if (_glow2Well != null)
                _glow2Well.color = (Color)_emission2;
            if (_glow3Well != null)
                _glow3Well.color = (Color)_emission3;
            if (_paintStateLabel != null)
                _paintStateLabel.text = _hasCustom ? "CUSTOM PAINT" : "TEAM DEFAULT COLORS";
        }

        void PaintPreview()
        {
            if (_previewHull == null)
                return;
            ShipAccentColors accents = _hasCustom
                ? CurrentCustomAccents()
                : ShipAccentColors.Default;
            ShipColorizeAccentApplier.ApplyFromCapturedBase(_previewHull, accents, _team);
        }

        Color32 ColorForSlot(AccentSlot slot)
        {
            switch (slot)
            {
                case AccentSlot.Color3: return _color3;
                case AccentSlot.Emission1: return _emission1;
                case AccentSlot.Emission2: return _emission2;
                case AccentSlot.Emission3: return _emission3;
                case AccentSlot.Thruster:
                    return LocalPlayerThrusterStyle.ResolveTint(LocalPlayerThrusterStyle.Get(), _team);
                default: return _color2;
            }
        }

        void SetSlotColor(AccentSlot slot, Color32 color)
        {
            switch (slot)
            {
                case AccentSlot.Color3:
                    _color3 = color;
                    break;
                case AccentSlot.Emission1:
                    _emission1 = color;
                    break;
                case AccentSlot.Emission2:
                    _emission2 = color;
                    break;
                case AccentSlot.Emission3:
                    _emission3 = color;
                    break;
                default:
                    _color2 = color;
                    break;
            }
        }

        void PaintHueBar()
        {
            for (int y = 0; y < HueBarH; y++)
            {
                float h = (float)y / (HueBarH - 1);
                Color c = Color.HSVToRGB(h, 1f, 1f);
                for (int x = 0; x < HueBarW; x++)
                    _hueTex.SetPixel(x, y, c);
            }

            _hueTex.Apply(false, false);
        }

        void PaintSvSquare()
        {
            for (int y = 0; y < SvSize; y++)
            {
                float v = (float)y / (SvSize - 1);
                for (int x = 0; x < SvSize; x++)
                {
                    float s = (float)x / (SvSize - 1);
                    _svTex.SetPixel(x, y, Color.HSVToRGB(_hue, s, v));
                }
            }

            _svTex.Apply(false, false);
        }

        void UpdatePickerCursors()
        {
            // Bottom-left anchors + 0.5 pivot: (0,0) is the dark corner, (w,h) is the bright corner.
            if (_svCursor != null)
            {
                float w = _svImage != null ? _svImage.rectTransform.rect.width : SvSize;
                float h = _svImage != null ? _svImage.rectTransform.rect.height : SvSize;
                _svCursor.anchoredPosition = new Vector2(_sat * w, _val * h);
            }

            if (_hueCursor != null)
            {
                float h = _hueImage != null ? _hueImage.rectTransform.rect.height : HueBarH;
                _hueCursor.anchoredPosition = new Vector2(0f, _hue * h);
            }
        }

        static GameObject CreateUi(string name, Transform parent, params Type[] extras)
        {
            var types = new Type[extras.Length + 1];
            types[0] = typeof(RectTransform);
            for (int i = 0; i < extras.Length; i++)
                types[i + 1] = extras[i];
            var go = new GameObject(name, types);
            go.transform.SetParent(parent, false);
            return go;
        }

        static TextMeshProUGUI CreateTmp(Transform parent, string name, string text, float size, FontStyles style)
        {
            var go = CreateUi(name, parent, typeof(TextMeshProUGUI));
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.fontStyle = style;
            tmp.color = BodyColor;
            tmp.raycastTarget = false;
            return tmp;
        }

        static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        static void PlaceTop(RectTransform rt, float down, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(-32f, height);
            rt.anchoredPosition = new Vector2(0f, -down);
        }

        /// <summary>
        /// Click/drag pad that reports 0–1 coordinates inside a RawImage rect.
        /// Uses the overlay canvas camera so a Screen Space Overlay press does not
        /// invent a stray world camera and throw the cursor off the square.
        /// </summary>
        sealed class SvPad : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
        {
            public Action<Vector2> OnNorm;
            public Action OnReleased;

            public void OnPointerDown(PointerEventData eventData) => Report(eventData);

            public void OnDrag(PointerEventData eventData) => Report(eventData);

            public void OnPointerUp(PointerEventData eventData) => OnReleased?.Invoke();

            void Report(PointerEventData eventData)
            {
                var rt = (RectTransform)transform;
                Camera cam = EventCamera(rt);
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        rt, eventData.position, cam, out Vector2 local))
                    return;
                float x = Mathf.Clamp01((local.x - rt.rect.xMin) / Mathf.Max(1f, rt.rect.width));
                float y = Mathf.Clamp01((local.y - rt.rect.yMin) / Mathf.Max(1f, rt.rect.height));
                OnNorm?.Invoke(new Vector2(x, y));
            }
        }

        /// <summary>Vertical hue bar: bottom = hue 0, top = hue 1.</summary>
        sealed class HuePad : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
        {
            public Action<float> OnNorm;
            public Action OnReleased;

            public void OnPointerDown(PointerEventData eventData) => Report(eventData);

            public void OnDrag(PointerEventData eventData) => Report(eventData);

            public void OnPointerUp(PointerEventData eventData) => OnReleased?.Invoke();

            void Report(PointerEventData eventData)
            {
                var rt = (RectTransform)transform;
                Camera cam = EventCamera(rt);
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        rt, eventData.position, cam, out Vector2 local))
                    return;
                float y = Mathf.Clamp01((local.y - rt.rect.yMin) / Mathf.Max(1f, rt.rect.height));
                OnNorm?.Invoke(y);
            }
        }

        static Camera EventCamera(Transform t)
        {
            var canvas = t.GetComponentInParent<Canvas>();
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                return null;
            return canvas.worldCamera != null ? canvas.worldCamera : canvas.rootCanvas.worldCamera;
        }
    }
}
