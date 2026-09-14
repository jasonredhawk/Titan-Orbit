using System;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Full-screen Customize Ship studio: hull presets, profile badge, and thrusters.
    /// Color1 stays the team Colorize material. Color2 / Color3 / Glow come from
    /// authored <see cref="ShipAccentPreset"/> assets — not free HSV picks.
    /// Thruster color swaps the authored JetFlame prefab (Blue / Green / Purple / Red /
    /// Yellow) instead of tinting materials.
    /// <para>
    /// The world-space preview hull lives on the unused ShipPreview layer over the
    /// same star + gas shader as the match. Close() disables that camera and hides
    /// the root so a leftover ship cannot composite into the game view.
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

        static readonly Color PanelFill = new Color(0.012f, 0.016f, 0.028f, 0.96f);
        static readonly Color CaptionColor = new Color(0.62f, 0.78f, 0.95f, 0.92f);
        static readonly Color BodyColor = new Color(0.88f, 0.92f, 0.98f, 0.95f);
        static readonly Color AccentCyan = new Color(0.42f, 0.78f, 0.98f, 0.95f);
        static readonly Color AccentGold = new Color(0.95f, 0.78f, 0.22f, 0.95f);
        static readonly Color CardFill = new Color(0.04f, 0.07f, 0.12f, 0.92f);
        static readonly Color RowFill = new Color(0.06f, 0.09f, 0.14f, 0.96f);
        static readonly Color FrameTint = new Color(0.22f, 0.36f, 0.52f, 0.55f);

        enum AccentSlot
        {
            Color2 = 0,
            Color3 = 1,
            Emission1 = 2,
            Emission2 = 3,
            Emission3 = 4,
            Thruster = 5,
            Life0 = 6,
            Life1 = 7,
            Life2 = 8,
            Life3 = 9,
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
        TextMeshProUGUI _paintPresetLabel;
        TextMeshProUGUI _thrusterStyleLabel;
        TextMeshProUGUI _thrusterColorLabel;
        TextMeshProUGUI _thrusterModeLabel;
        Image _thrusterColorWell;
        readonly Image[] _lifetimeSwatches = new Image[4];
        readonly Image[] _presetSwatches = new Image[6];
        MainMenuBadgePicker _badgePicker;
        readonly Image[] _teamChips = new Image[5];

        GameObject _previewRoot;
        Camera _previewCam;
        RenderTexture _previewRt;
        RawImage _previewImage;
        GameObject _previewHull;
        ShipFamilyDefinition _previewFamily;
        PreviewStarfieldBackdrop _previewStarfield;
        ShipWorldNameplate _previewNameplate;
        Vector3 _previewCamOffset = new Vector3(0f, 22f, 0f);
        float _previewHeading;
        float _previewSpeed;
        float _previewTargetSpeed;
        float _previewYawRate;
        float _previewTargetYawRate;
        float _previewRetargetTimer;
        int _previewBadgeId = int.MinValue;
        string _previewFlameColorName;

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
            if (transform.Find("Panel/Layout_v15") == null)
            {
                if (transform.Find("Backdrop") != null)
                    TearDownChrome();
                BuildFullChrome();
            }

            BindChromeRefs();
            EnsureHullSection();
            EnsureBadgeSection();
            EnsureThrusterSection();
            EnsureColorPicker();
            EnsurePickerCloseButton();
            StripPanelDismissesPicker();
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
            _paintPresetLabel = null;
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
            _previewNameplate = null;
            for (int i = 0; i < _lifetimeSwatches.Length; i++)
                _lifetimeSwatches[i] = null;
            for (int i = 0; i < _presetSwatches.Length; i++)
                _presetSwatches[i] = null;
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
            Transform right = transform.Find("Panel/Right");
            if (right != null)
                return right;
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
            MainMenuPresenter.ApplyTransparentMenuBackdrop(backdropImage);
            backdropImage.color = new Color(0.02f, 0.04f, 0.08f, 0.55f);
            var backdropBtn = backdrop.GetComponent<Button>();
            backdropBtn.transition = Selectable.Transition.None;
            backdropBtn.onClick.AddListener(Close);

            var panel = CreateUi("Panel", transform, typeof(Image), typeof(Outline));
            var panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(1180f, 752f);
            var panelImage = panel.GetComponent<Image>();
            panelImage.color = PanelFill;
            panelImage.raycastTarget = true;
            var panelOutline = panel.GetComponent<Outline>();
            panelOutline.effectColor = FrameTint;
            panelOutline.effectDistance = new Vector2(1.5f, -1.5f);

            AddAccentRail(panel.transform, "TopRail", AccentCyan, 3f, top: true);
            AddAccentRail(panel.transform, "BottomRail", AccentGold, 2f, top: false);

            CreateUi("Layout_v15", panel.transform);

            var title = CreateTmp(panel.transform, "Title", "CUSTOMIZE SHIP", 22f, FontStyles.Bold);
            PlaceTop(title.rectTransform, 10f, 26f);
            title.rectTransform.offsetMin = new Vector2(22f, title.rectTransform.offsetMin.y);
            title.rectTransform.offsetMax = new Vector2(-200f, title.rectTransform.offsetMax.y);
            title.alignment = TextAlignmentOptions.MidlineLeft;
            title.color = AccentCyan;
            title.characterSpacing = 2.2f;

            var subtitle = CreateTmp(
                panel.transform,
                "Subtitle",
                "Team Color1 stays locked. Jets use a four-stop lifetime fade.",
                12f,
                FontStyles.Normal);
            PlaceTop(subtitle.rectTransform, 34f, 16f);
            subtitle.rectTransform.offsetMin = new Vector2(22f, subtitle.rectTransform.offsetMin.y);
            subtitle.alignment = TextAlignmentOptions.MidlineLeft;
            subtitle.color = CaptionColor;

            var left = CreateUi("Left", panel.transform, typeof(Image), typeof(Outline));
            var leftRt = left.GetComponent<RectTransform>();
            leftRt.anchorMin = new Vector2(0f, 1f);
            leftRt.anchorMax = new Vector2(0f, 1f);
            leftRt.pivot = new Vector2(0f, 1f);
            leftRt.sizeDelta = new Vector2(452f, 592f);
            leftRt.anchoredPosition = new Vector2(18f, -56f);
            left.GetComponent<Image>().color = new Color(0.01f, 0.02f, 0.04f, 1f);
            var leftOutline = left.GetComponent<Outline>();
            leftOutline.effectColor = new Color(AccentCyan.r, AccentCyan.g, AccentCyan.b, 0.55f);
            leftOutline.effectDistance = new Vector2(1f, -1f);

            var previewCaption = CreateTmp(left.transform, "PreviewCaption", "HULL PREVIEW", 11f, FontStyles.Bold);
            PlaceTop(previewCaption.rectTransform, 4f, 16f);
            previewCaption.alignment = TextAlignmentOptions.Center;
            previewCaption.color = CaptionColor;
            previewCaption.characterSpacing = 1.6f;

            var previewHost = CreateUi("PreviewHost", left.transform);
            var hostRt = previewHost.GetComponent<RectTransform>();
            hostRt.anchorMin = new Vector2(0.5f, 1f);
            hostRt.anchorMax = new Vector2(0.5f, 1f);
            hostRt.pivot = new Vector2(0.5f, 1f);
            hostRt.sizeDelta = new Vector2(400f, 400f);
            hostRt.anchoredPosition = new Vector2(0f, -22f);

            _previewImage = CreateUi("Preview", previewHost.transform, typeof(RawImage), typeof(AspectRatioFitter))
                .GetComponent<RawImage>();
            StretchFull(_previewImage.rectTransform);
            var previewFit = _previewImage.GetComponent<AspectRatioFitter>();
            previewFit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            previewFit.aspectRatio = 1f;
            EnsurePreviewRenderTexture();
            _previewImage.texture = _previewRt;
            _previewImage.color = Color.white;
            _previewImage.uvRect = new Rect(0f, 0f, 1f, 1f);

            BuildPreviewWorld();

            var right = CreateUi("Right", panel.transform, typeof(VerticalLayoutGroup));
            var rightRt = right.GetComponent<RectTransform>();
            rightRt.anchorMin = new Vector2(0f, 0f);
            rightRt.anchorMax = new Vector2(1f, 1f);
            rightRt.offsetMin = new Vector2(486f, 56f);
            rightRt.offsetMax = new Vector2(-18f, -56f);
            var vlg = right.GetComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.spacing = 10f;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            CreateFooterButton(
                panel.transform,
                "Done",
                "DONE",
                new Vector2(0f, 12f),
                new Vector2(220f, 34f),
                new Color(0.16f, 0.28f, 0.40f, 0.95f),
                Close);
        }

        static void BindLayoutSlot(RectTransform rt, float height)
        {
            if (rt == null)
                return;
            var slot = rt.GetComponent<LayoutElement>();
            if (slot == null)
                slot = rt.gameObject.AddComponent<LayoutElement>();
            slot.minHeight = height;
            slot.preferredHeight = height;
            slot.flexibleHeight = 0f;
            slot.flexibleWidth = 1f;
        }

        static void AddAccentRail(Transform parent, string name, Color color, float height, bool top)
        {
            var go = CreateUi(name, parent, typeof(Image));
            var rt = go.GetComponent<RectTransform>();
            if (top)
            {
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = Vector2.zero;
            }
            else
            {
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.anchoredPosition = Vector2.zero;
            }

            rt.sizeDelta = new Vector2(0f, height);
            go.GetComponent<Image>().color = color;
            go.GetComponent<Image>().raycastTarget = false;
        }

        static void EnsureCardAccent(Transform card)
        {
            if (card == null || card.Find("CardRail") != null)
                return;
            AddAccentRail(card, "CardRail", AccentCyan, 2f, top: true);
        }

        void EnsurePreviewRenderTexture()
        {
            if (_previewRt != null && _previewRt.width == 512 && _previewRt.height == 512)
                return;

            if (_previewRt != null)
            {
                if (_previewCam != null)
                    _previewCam.targetTexture = null;
                _previewRt.Release();
                Destroy(_previewRt);
            }

            _previewRt = new RenderTexture(512, 512, 16)
            {
                name = "ShipCustomizePreview",
                antiAliasing = 1,
                filterMode = FilterMode.Bilinear,
            };
        }

        void BindChromeRefs()
        {
            Transform content = ContentRoot();
            Transform panel = transform.Find("Panel");
            if (content == null)
                return;

            if (_previewImage == null)
            {
                Transform left = transform.Find("Panel/Left");
                Transform host = left != null ? left.Find("PreviewHost") : null;
                _previewImage = (host != null ? host.Find("Preview") : null)?.GetComponent<RawImage>()
                    ?? (left != null ? left.Find("Preview") : null)?.GetComponent<RawImage>()
                    ?? content.Find("Preview")?.GetComponent<RawImage>();
            }
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
            if (_pickerPanel == null)
            {
                Transform thruster = content.Find("ThrusterSection");
                _pickerPanel = (thruster != null ? thruster.Find("ColorPicker") : null) as RectTransform
                    ?? transform.Find("ColorPicker") as RectTransform
                    ?? (panel != null ? panel.Find("Right/ColorPicker") as RectTransform : null)
                    ?? (panel != null ? panel.Find("ColorPicker") as RectTransform : null);
            }
        }

        /// <summary>
        /// One Hull card (header + team chips + paint preset), matching Thrusters.
        /// </summary>
        void EnsureHullSection()
        {
            Transform right = ContentRoot();
            if (right == null)
                return;

            DestroyNamedChild(right, "HullHeader");
            DestroyNamedChild(right, "TeamStrip");
            DestroyNamedChild(right, "PaintPresetRow");
            DestroyNamedChild(right, "PaintPresetSwatches");

            Transform existing = right.Find("HullSection");
            GameObject section = existing != null
                ? existing.gameObject
                : CreateUi("HullSection", right, typeof(Image));
            var rt = section.GetComponent<RectTransform>();
            BindLayoutSlot(rt, 186f);
            section.GetComponent<Image>().color = CardFill;
            EnsureCardAccent(section.transform);

            Transform headerTf = section.transform.Find("Header");
            TextMeshProUGUI header = headerTf != null
                ? headerTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(section.transform, "Header", "HULL", 15f, FontStyles.Bold);
            var headerRt = header.rectTransform;
            headerRt.anchorMin = new Vector2(0f, 1f);
            headerRt.anchorMax = new Vector2(1f, 1f);
            headerRt.pivot = new Vector2(0.5f, 1f);
            headerRt.sizeDelta = new Vector2(-20f, 18f);
            headerRt.anchoredPosition = new Vector2(0f, -4f);
            header.fontSize = 12f;
            header.alignment = TextAlignmentOptions.MidlineLeft;
            header.color = AccentCyan;
            header.characterSpacing = 1.2f;

            Transform blurbTf = section.transform.Find("Blurb");
            TextMeshProUGUI blurb = blurbTf != null
                ? blurbTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(
                    section.transform,
                    "Blurb",
                    "Team Color1 stays locked. Cycle authored Color2, Color3, and glow presets.",
                    11f,
                    FontStyles.Normal);
            var blurbRt = blurb.rectTransform;
            blurbRt.anchorMin = new Vector2(0f, 1f);
            blurbRt.anchorMax = new Vector2(1f, 1f);
            blurbRt.pivot = new Vector2(0.5f, 1f);
            blurbRt.sizeDelta = new Vector2(-20f, 20f);
            blurbRt.anchoredPosition = new Vector2(0f, -22f);
            blurb.text = "Team Color1 stays locked. Cycle authored Color2, Color3, and glow presets.";
            blurb.alignment = TextAlignmentOptions.TopLeft;
            blurb.color = CaptionColor;

            EnsureTeamStrip(section.transform);
            EnsurePaintPresetSection(section.transform);
        }

        /// <summary>
        /// Team A–E chips so the player can preview Color1 presets. This does not
        /// change their match team — Color1 is still locked to the palette asset.
        /// </summary>
        void EnsureTeamStrip(Transform parent)
        {
            if (parent == null)
                return;

            Transform existing = parent.Find("TeamStrip");
            GameObject row = existing != null
                ? existing.gameObject
                : CreateUi("TeamStrip", parent, typeof(Image));
            var rt = row.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(-16f, 56f);
            rt.anchoredPosition = new Vector2(0f, -44f);
            row.GetComponent<Image>().color = RowFill;

            Transform labelTf = row.transform.Find("Label");
            TextMeshProUGUI label = labelTf != null
                ? labelTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(row.transform, "Label", "PREVIEW TEAM", 12f, FontStyles.Bold);
            var labelRt = label.rectTransform;
            labelRt.anchorMin = new Vector2(0f, 1f);
            labelRt.anchorMax = new Vector2(1f, 1f);
            labelRt.pivot = new Vector2(0.5f, 1f);
            labelRt.sizeDelta = new Vector2(-8f, 18f);
            labelRt.anchoredPosition = new Vector2(0f, -3f);
            label.alignment = TextAlignmentOptions.Center;
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
                chipRt.anchorMin = new Vector2(0.5f, 0f);
                chipRt.anchorMax = new Vector2(0.5f, 0f);
                chipRt.pivot = new Vector2(0.5f, 0f);
                chipRt.sizeDelta = new Vector2(30f, 24f);
                chipRt.anchoredPosition = new Vector2((i - 2) * 36f, 6f);
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
            PaintTeamStrip();
            RefreshFromStore();
            RefreshThrusterSection();
            RefreshPreviewThrusters(rebuild: false);
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
                var outline = chip.GetComponent<Outline>();
                if (outline == null)
                    outline = chip.gameObject.AddComponent<Outline>();
                outline.effectColor = selected ? AccentGold : Color.clear;
                outline.effectDistance = new Vector2(1.2f, -1.2f);
            }
        }

        /// <summary>
        /// Cycles authored Color2 / Color3 / Glow presets. Color1 stays the team swatch.
        /// </summary>
        void EnsurePaintPresetSection(Transform parent)
        {
            Transform content = parent != null ? parent : ContentRoot();
            if (content == null)
                return;

            DestroyNamedChild(content, "TeamWell");
            DestroyNamedChild(content, "COLOR 2Well");
            DestroyNamedChild(content, "COLOR 3Well");
            DestroyNamedChild(content, "GLOW 1Well");
            DestroyNamedChild(content, "GLOW 2Well");
            DestroyNamedChild(content, "GLOW 3Well");
            DestroyNamedChild(content, "PresetSwatches");
            DestroyNamedChild(content, "PaintState");
            DestroyNamedChild(content, "Reset");
            _teamSwatch = null;
            _color2Well = null;
            _color3Well = null;
            _glow1Well = null;
            _glow2Well = null;
            _glow3Well = null;
            _paintStateLabel = null;

            _paintPresetLabel = EnsureCycleRow(
                content,
                "PaintPresetRow",
                "PRESET",
                -106f,
                () => CyclePaintPreset(-1),
                () => CyclePaintPreset(1));

            EnsurePaintPresetSwatches(content);
            RefreshPaintPresetSection();
        }

        void EnsurePaintPresetSwatches(Transform content)
        {
            Transform existing = content.Find("PaintPresetSwatches");
            GameObject row = existing != null
                ? existing.gameObject
                : CreateUi("PaintPresetSwatches", content, typeof(Image));
            if (existing == null && _paintPresetLabel != null)
                row.transform.SetSiblingIndex(_paintPresetLabel.transform.parent.GetSiblingIndex() + 1);

            var rt = row.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(-16f, 28f);
            rt.anchoredPosition = new Vector2(0f, -144f);
            row.GetComponent<Image>().color = RowFill;

            const float swatch = 22f;
            const float gap = 8f;
            float total = _presetSwatches.Length * swatch + (_presetSwatches.Length - 1) * gap;
            float start = -total * 0.5f + swatch * 0.5f;
            for (int i = 0; i < _presetSwatches.Length; i++)
            {
                string name = "Swatch" + i;
                Transform child = row.transform.Find(name);
                GameObject go = child != null
                    ? child.gameObject
                    : CreateUi(name, row.transform, typeof(Image));
                var srt = go.GetComponent<RectTransform>();
                srt.anchorMin = new Vector2(0.5f, 0.5f);
                srt.anchorMax = new Vector2(0.5f, 0.5f);
                srt.pivot = new Vector2(0.5f, 0.5f);
                srt.sizeDelta = new Vector2(swatch, swatch);
                srt.anchoredPosition = new Vector2(start + i * (swatch + gap), 0f);
                var img = go.GetComponent<Image>();
                img.sprite = _whiteSprite;
                img.raycastTarget = false;
                _presetSwatches[i] = img;
            }
        }

        void CyclePaintPreset(int delta)
        {
            int next = TeamColor1Palette.WrapPresetIndex(
                LocalPlayerShipAccents.GetPresetIndex() + delta);
            LocalPlayerShipAccents.SetPresetIndex(next);
            ShipAccentColorsRpcClient.NotifyChanged();
            RefreshFromStore();
        }

        void RefreshPaintPresetSection()
        {
            int index = LocalPlayerShipAccents.GetPresetIndex();
            if (_paintPresetLabel != null)
                _paintPresetLabel.text = TeamColor1Palette.GetPresetDisplayName(index).ToUpperInvariant();

            Color[] colors =
            {
                TeamColor1Palette.GetColor1(_team),
                _color2,
                _color3,
                _emission1,
                _emission2,
                _emission3,
            };
            if (TeamColor1Palette.TryGetAccentPreset(index, out ShipAccentPreset preset) && preset != null)
            {
                colors[1] = preset.color2;
                colors[2] = preset.color3;
                colors[3] = preset.emission1;
                colors[4] = preset.emission2;
                colors[5] = preset.emission3;
            }

            for (int i = 0; i < _presetSwatches.Length; i++)
            {
                if (_presetSwatches[i] == null)
                    continue;
                Color c = colors[i];
                c.a = 1f;
                _presetSwatches[i].color = c;
            }
        }

        static void DestroyNamedChild(Transform parent, string name)
        {
            if (parent == null)
                return;
            Transform child = parent.Find(name);
            if (child != null)
                DestroyImmediate(child.gameObject);
        }

        /// <summary>
        /// Own PROFILE BADGE block — not a color-well lookalike. Opens the same grid overlay.
        /// </summary>
        void EnsureBadgeSection()
        {
            Transform right = ContentRoot();
            if (right != null)
            {
                DestroyNamedChild(right, "BadgeRow");
                DestroyNamedChild(right, "BadgeSection");
            }

            Transform left = transform.Find("Panel/Left");
            if (left == null)
                return;

            Transform existing = left.Find("BadgeSection");
            GameObject section = existing != null
                ? existing.gameObject
                : CreateUi("BadgeSection", left, typeof(Image));
            var rt = section.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(-16f, 148f);
            rt.anchoredPosition = new Vector2(0f, 8f);
            section.GetComponent<Image>().color = Color.clear;

            Transform headerTf = section.transform.Find("Header");
            if (headerTf != null)
                headerTf.gameObject.SetActive(false);

            Transform chipTf = section.transform.Find("Chip");
            GameObject chipGo = chipTf != null
                ? chipTf.gameObject
                : CreateUi("Chip", section.transform, typeof(Image), typeof(Button));
            var chipRt = chipGo.GetComponent<RectTransform>();
            chipRt.anchorMin = new Vector2(0.5f, 1f);
            chipRt.anchorMax = new Vector2(0.5f, 1f);
            chipRt.pivot = new Vector2(0.5f, 1f);
            chipRt.anchoredPosition = new Vector2(0f, -4f);
            chipRt.sizeDelta = new Vector2(104f, 104f);
            var chipFill = chipGo.GetComponent<Image>();
            chipFill.sprite = _whiteSprite;
            chipFill.color = new Color(0.08f, 0.12f, 0.20f, 0.95f);
            chipFill.raycastTarget = true;

            Transform iconTf = chipGo.transform.Find("Icon");
            GameObject iconGo = iconTf != null
                ? iconTf.gameObject
                : CreateUi("Icon", chipGo.transform, typeof(Image));
            StretchFull(iconGo.GetComponent<RectTransform>());
            iconGo.GetComponent<RectTransform>().offsetMin = new Vector2(6f, 6f);
            iconGo.GetComponent<RectTransform>().offsetMax = new Vector2(-6f, -6f);
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
            _badgeEmpty.fontSize = 42f;
            _badgeEmpty.alignment = TextAlignmentOptions.Center;
            _badgeEmpty.color = CaptionColor;
            _badgeEmpty.raycastTarget = false;

            Transform hintTf = section.transform.Find("Status");
            if (hintTf != null)
                hintTf.gameObject.SetActive(false);
            _badgeCaption = null;

            Transform btnTf = section.transform.Find("Choose");
            GameObject btnGo = btnTf != null
                ? btnTf.gameObject
                : CreateUi("Choose", section.transform, typeof(Image), typeof(Button));
            var btnRt = btnGo.GetComponent<RectTransform>();
            btnRt.anchorMin = new Vector2(0.5f, 1f);
            btnRt.anchorMax = new Vector2(0.5f, 1f);
            btnRt.pivot = new Vector2(0.5f, 1f);
            btnRt.sizeDelta = new Vector2(118f, 22f);
            btnRt.anchoredPosition = new Vector2(0f, -114f);
            var btnBg = btnGo.GetComponent<Image>();
            btnBg.sprite = _whiteSprite;
            btnBg.color = new Color(0.16f, 0.28f, 0.40f, 0.96f);
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
            btnLabel.fontSize = 11f;
            btnLabel.alignment = TextAlignmentOptions.Center;
            btnLabel.color = BodyColor;
            btnLabel.characterSpacing = 1.2f;
            btnLabel.raycastTarget = false;

            _badgePicker = section.GetComponent<MainMenuBadgePicker>();
            if (_badgePicker == null)
                _badgePicker = section.AddComponent<MainMenuBadgePicker>();
            _badgePicker.Configure(_badgeChip, chipFill, _badgeEmpty, null);

            var chipBtn = chipGo.GetComponent<Button>();
            chipBtn.targetGraphic = chipFill;
            chipBtn.transition = Selectable.Transition.None;
            chipBtn.onClick.RemoveAllListeners();
            chipBtn.onClick.AddListener(OpenBadgePicker);
        }

        void OpenBadgePicker()
        {
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
            BindLayoutSlot(rt, 220f);
            section.GetComponent<Image>().color = CardFill;
            EnsureCardAccent(section.transform);

            Transform headerTf = section.transform.Find("Header");
            TextMeshProUGUI header = headerTf != null
                ? headerTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(section.transform, "Header", "THRUSTERS", 15f, FontStyles.Bold);
            var headerRt = header.rectTransform;
            headerRt.anchorMin = new Vector2(0f, 1f);
            headerRt.anchorMax = new Vector2(1f, 1f);
            headerRt.pivot = new Vector2(0.5f, 1f);
            headerRt.sizeDelta = new Vector2(-20f, 18f);
            headerRt.anchoredPosition = new Vector2(0f, -4f);
            header.fontSize = 12f;
            header.alignment = TextAlignmentOptions.MidlineLeft;
            header.color = AccentCyan;
            header.characterSpacing = 1.2f;

            Transform blurbTf = section.transform.Find("Blurb");
            TextMeshProUGUI blurb = blurbTf != null
                ? blurbTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(
                    section.transform,
                    "Blurb",
                    "Swap authored JetFlame colors. Dark picks snap to a visible flame.",
                    12f,
                    FontStyles.Normal);
            var blurbRt = blurb.rectTransform;
            blurbRt.anchorMin = new Vector2(0f, 1f);
            blurbRt.anchorMax = new Vector2(1f, 1f);
            blurbRt.pivot = new Vector2(0.5f, 1f);
            blurbRt.sizeDelta = new Vector2(-20f, 20f);
            blurbRt.anchoredPosition = new Vector2(0f, -22f);
            blurb.text = "Color over lifetime: hot core, team or picked color, then fade.";
            blurb.fontSize = 11f;
            blurb.alignment = TextAlignmentOptions.TopLeft;
            blurb.color = CaptionColor;

            _thrusterStyleLabel = EnsureCycleRow(
                section.transform,
                "StyleRow",
                "TYPE",
                -44f,
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
            colorRt.anchorMin = new Vector2(0f, 1f);
            colorRt.anchorMax = new Vector2(1f, 1f);
            colorRt.pivot = new Vector2(0.5f, 1f);
            colorRt.sizeDelta = new Vector2(-16f, 32f);
            colorRt.anchoredPosition = new Vector2(0f, -80f);
            colorRow.GetComponent<Image>().color = RowFill;

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
            wellRt.sizeDelta = new Vector2(56f, 22f);
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

            EnsureLifetimeStrip(section.transform);

            Transform modeTf = section.transform.Find("Mode");
            GameObject modeGo = modeTf != null
                ? modeTf.gameObject
                : CreateUi("Mode", section.transform, typeof(Image), typeof(Button));
            var modeRt = modeGo.GetComponent<RectTransform>();
            modeRt.anchorMin = new Vector2(0.5f, 1f);
            modeRt.anchorMax = new Vector2(0.5f, 1f);
            modeRt.pivot = new Vector2(0.5f, 1f);
            modeRt.sizeDelta = new Vector2(300f, 28f);
            modeRt.anchoredPosition = new Vector2(-110f, -154f);
            var modeBg = modeGo.GetComponent<Image>();
            modeBg.sprite = _whiteSprite;
            modeBg.color = new Color(0.16f, 0.28f, 0.40f, 0.96f);
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
            resetRt.sizeDelta = new Vector2(140f, 28f);
            resetRt.anchoredPosition = new Vector2(196f, -154f);
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

        /// <summary>
        /// HSV popup sits under the right-column cards, inset from the panel edge
        /// so the cyan frame is not clipped.
        /// </summary>
        void EnsureColorPicker()
        {
            Transform panel = transform.Find("Panel");
            if (panel == null)
                return;

            if (_pickerPanel == null)
            {
                Transform existing = panel.Find("Right/ColorPicker")
                    ?? panel.Find("ColorPicker")
                    ?? transform.Find("ColorPicker")
                    ?? (ContentRoot() != null ? ContentRoot().Find("ThrusterSection/ColorPicker") : null);
                if (existing != null)
                    _pickerPanel = existing as RectTransform;
                else
                    BuildPickerPanel(panel);
            }

            if (_pickerPanel == null)
                return;

            var le = _pickerPanel.GetComponent<LayoutElement>();
            if (le == null)
                le = _pickerPanel.gameObject.AddComponent<LayoutElement>();
            le.ignoreLayout = true;

            // Dock under the right-column cards, inset so the cyan frame stays inside the panel.
            Transform right = transform.Find("Panel/Right");
            Transform pickerParent = right != null ? right : panel;
            if (_pickerPanel.parent != pickerParent)
                _pickerPanel.SetParent(pickerParent, false);

            _pickerPanel.anchorMin = new Vector2(1f, 0f);
            _pickerPanel.anchorMax = new Vector2(1f, 0f);
            _pickerPanel.pivot = new Vector2(1f, 0f);
            _pickerPanel.sizeDelta = new Vector2(236f, 196f);
            _pickerPanel.anchoredPosition = new Vector2(-8f, 8f);

            var pickerOutline = _pickerPanel.GetComponent<Outline>();
            if (pickerOutline == null)
                pickerOutline = _pickerPanel.gameObject.AddComponent<Outline>();
            pickerOutline.effectColor = AccentCyan;
            pickerOutline.effectDistance = new Vector2(2f, -2f);

            if (_svImage != null)
            {
                var svRt = _svImage.rectTransform;
                svRt.sizeDelta = new Vector2(128f, 128f);
                svRt.anchoredPosition = new Vector2(10f, -6f);
            }

            if (_hueImage != null)
            {
                var hueRt = _hueImage.rectTransform;
                hueRt.sizeDelta = new Vector2(18f, 128f);
                hueRt.anchoredPosition = new Vector2(-10f, 0f);
            }
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
            if (parent.GetComponent<VerticalLayoutGroup>() != null)
            {
                BindLayoutSlot(rt, 32f);
            }
            else
            {
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.sizeDelta = new Vector2(-16f, 30f);
                rt.anchoredPosition = new Vector2(0f, y);
            }

            row.GetComponent<Image>().color = RowFill;

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
            string flameName = LocalPlayerThrusterStyle.ResolveFlameColorName(style, _team);
            bool followTeam = style.UseTeamColor;
            if (_thrusterColorWell != null)
            {
                _thrusterColorWell.color = (Color)tint;
                SetColorWellInteractable(_thrusterColorWell, !followTeam);
            }

            if (_thrusterColorLabel != null)
                _thrusterColorLabel.text = followTeam
                    ? "TEAM · " + flameName.ToUpperInvariant()
                    : flameName.ToUpperInvariant();
            if (_thrusterModeLabel != null)
                _thrusterModeLabel.text = followTeam ? "FOLLOW TEAM COLOR" : "LOCKED CHOSEN COLOR";
            PaintLifetimeStrip(style);
        }

        void EnsureLifetimeStrip(Transform section)
        {
            Transform existing = section.Find("LifetimeRow");
            GameObject row = existing != null
                ? existing.gameObject
                : CreateUi("LifetimeRow", section, typeof(Image));
            var rt = row.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(-16f, 28f);
            rt.anchoredPosition = new Vector2(0f, -118f);
            row.GetComponent<Image>().color = RowFill;

            Transform capTf = row.transform.Find("Caption");
            TextMeshProUGUI cap = capTf != null
                ? capTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(row.transform, "Caption", "LIFETIME", 11f, FontStyles.Bold);
            var capRt = cap.rectTransform;
            capRt.anchorMin = new Vector2(0f, 0f);
            capRt.anchorMax = new Vector2(0.28f, 1f);
            capRt.offsetMin = new Vector2(10f, 0f);
            capRt.offsetMax = Vector2.zero;
            cap.alignment = TextAlignmentOptions.MidlineLeft;
            cap.color = CaptionColor;

            for (int i = 0; i < _lifetimeSwatches.Length; i++)
            {
                string name = "Stop" + i;
                Transform stopTf = row.transform.Find(name);
                GameObject stopGo = stopTf != null
                    ? stopTf.gameObject
                    : CreateUi(name, row.transform, typeof(Image), typeof(Button));
                if (stopGo.GetComponent<Button>() == null)
                    stopGo.AddComponent<Button>();
                var stopRt = stopGo.GetComponent<RectTransform>();
                stopRt.anchorMin = new Vector2(0.30f, 0.2f);
                stopRt.anchorMax = new Vector2(0.30f, 0.2f);
                stopRt.pivot = new Vector2(0f, 0f);
                stopRt.sizeDelta = new Vector2(72f, 16f);
                stopRt.anchoredPosition = new Vector2(i * 78f, 0f);
                var img = stopGo.GetComponent<Image>();
                img.sprite = _whiteSprite;
                img.raycastTarget = true;
                var stopBtn = stopGo.GetComponent<Button>();
                stopBtn.targetGraphic = img;
                stopBtn.transition = Selectable.Transition.None;
                stopBtn.onClick.RemoveAllListeners();
                int captured = i;
                stopBtn.onClick.AddListener(() => OpenLifetimeStopPicker(captured));
                _lifetimeSwatches[i] = img;
            }
        }

        void PaintLifetimeStrip(in LocalPlayerThrusterStyle.Style style)
        {
            bool followTeam = style.UseTeamColor;
            for (int i = 0; i < _lifetimeSwatches.Length; i++)
            {
                Image swatch = _lifetimeSwatches[i];
                if (swatch == null)
                    continue;
                Color c = LocalPlayerThrusterStyle.GetLifetimeStop(style, i, _team);
                c.a = 1f;
                swatch.color = c;
                SetColorWellInteractable(swatch, !followTeam);
            }
        }

        static void SetColorWellInteractable(Image well, bool interactable)
        {
            if (well == null)
                return;
            well.raycastTarget = interactable;
            var button = well.GetComponent<Button>();
            if (button != null)
                button.interactable = interactable;
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
            ThrusterVfxBank.DebugCycleIndex = LocalPlayerThrusterStyle.ResolveStyleIndex(style);
            ShipAccentColorsRpcClient.NotifyChanged();
            if (rebuildJets)
                ShipPropulsionVisualApplier.RebuildAllLive();
            else
                ShipPropulsionVisualApplier.ApplyTintToAllLive();
            RefreshPreviewThrusters(rebuildJets);
            _previewFlameColorName = LocalPlayerThrusterStyle.ResolveFlameColorName(style, _team);
            RefreshThrusterSection();
        }

        void CycleThrusterStyle(int delta)
        {
            var style = BeginCustomThruster();
            style.StyleIndex = (byte)ThrusterVfxBank.CycleStyleIndex(style.StyleIndex, delta);
            PersistThruster(style, rebuildJets: true);
        }

        void OpenThrusterColorPicker()
        {
            var style = LocalPlayerThrusterStyle.Get();
            if (style.UseTeamColor)
                return;

            OpenPicker(AccentSlot.Thruster);
        }

        void OpenLifetimeStopPicker(int index)
        {
            var style = LocalPlayerThrusterStyle.Get();
            if (style.UseTeamColor)
                return;

            OpenPicker(LifetimeSlot(index));
        }

        void ToggleThrusterFollowTeam()
        {
            HidePicker(persist: false);
            var style = BeginCustomThruster();
            bool follow = style.FollowTeam != 0;
            style.FollowTeam = (byte)(follow ? 0 : 1);
            if (style.FollowTeam == 0)
            {
                if (style.ColorPacked == 0)
                {
                    style.ColorPacked = ShipAccentColors.Pack(
                        ShipColorizeAccentApplier.Opaque(TeamColor1Palette.GetColor1(_team)));
                }

                if (style.LifeCustom == 0)
                    LocalPlayerThrusterStyle.WriteAnchorStops(ref style, _team);
            }

            PersistThruster(style, rebuildJets: true);
        }

        void OnResetThrustersClicked()
        {
            HidePicker(persist: false);
            int styleIndex = LocalPlayerThrusterStyle.ResolveStyleIndex(LocalPlayerThrusterStyle.Get());
            PersistThruster(LocalPlayerThrusterStyle.CreateLockedWhite(styleIndex), rebuildJets: true);
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

        /// <summary>Picker stays open until CLOSE — strip leftover panel/preview dismiss buttons.</summary>
        void StripPanelDismissesPicker()
        {
            Transform panel = transform.Find("Panel");
            if (panel != null)
            {
                var button = panel.GetComponent<Button>();
                if (button != null)
                {
                    button.onClick.RemoveAllListeners();
                    Destroy(button);
                }
            }

            if (_previewImage == null)
                return;

            var previewBtn = _previewImage.GetComponent<Button>();
            if (previewBtn == null)
                return;

            previewBtn.onClick.RemoveAllListeners();
            Destroy(previewBtn);
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
            rt.sizeDelta = new Vector2(288f, 36f);
            rt.anchoredPosition = new Vector2(x, y);
            row.GetComponent<Image>().color = new Color(0.07f, 0.09f, 0.14f, 0.96f);

            var label = CreateTmp(row.transform, "Label", caption, 11f, FontStyles.Bold);
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
            rt.sizeDelta = new Vector2(width, 32f);
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
            _pickerPanel = CreateUi("ColorPicker", parent, typeof(Image), typeof(Outline)).GetComponent<RectTransform>();
            _pickerPanel.anchorMin = new Vector2(1f, 0.5f);
            _pickerPanel.anchorMax = new Vector2(1f, 0.5f);
            _pickerPanel.pivot = new Vector2(1f, 0.5f);
            _pickerPanel.sizeDelta = new Vector2(260f, 236f);
            _pickerPanel.anchoredPosition = new Vector2(-14f, -40f);
            _pickerPanel.GetComponent<Image>().color = new Color(0.04f, 0.07f, 0.12f, 0.98f);
            var pickerOutline = _pickerPanel.GetComponent<Outline>();
            pickerOutline.effectColor = AccentCyan;
            pickerOutline.effectDistance = new Vector2(1.2f, -1.2f);

            var pickerTitle = CreateTmp(_pickerPanel, "PickerTitle", "JET COLOR", 12f, FontStyles.Bold);
            var titleRt = pickerTitle.rectTransform;
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.sizeDelta = new Vector2(-16f, 22f);
            titleRt.anchoredPosition = new Vector2(0f, -8f);
            pickerTitle.alignment = TextAlignmentOptions.MidlineLeft;
            pickerTitle.color = AccentCyan;

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
            svRt.anchoredPosition = new Vector2(12f, -10f);
            _svImage.texture = _svTex;
            svGo.GetComponent<SvPad>().OnNorm = OnSvPicked;
            svGo.GetComponent<SvPad>().OnReleased = OnSvReleased;

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
                EnsurePreviewRenderTexture();
                ConfigurePreviewCamera();
                SetLayerRecursive(_previewRoot, PreviewLayer());
                EnsurePreviewStarfield();
                return;
            }

            _previewRoot = new GameObject("ShipCustomizePreviewRoot");
            _previewRoot.hideFlags = HideFlags.HideAndDontSave;
            _previewRoot.transform.position = new Vector3(0f, -420f, 0f);

            var camGo = new GameObject("Cam");
            camGo.transform.SetParent(_previewRoot.transform, false);
            _previewCam = camGo.AddComponent<Camera>();
            ConfigurePreviewCamera();
            var listener = camGo.GetComponent<AudioListener>();
            if (listener != null)
                Destroy(listener);

            SetLayerRecursive(_previewRoot, PreviewLayer());
            EnsurePreviewStarfield();
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
                    EnsurePreviewRenderTexture();
                    ConfigurePreviewCamera();
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

        void BindPreviewJets(GameObject hull)
        {
            if (hull == null)
                return;

            var propulsion = hull.GetComponent<ShipPropulsionVisualApplier>();
            if (propulsion == null)
                propulsion = hull.AddComponent<ShipPropulsionVisualApplier>();
            propulsion.BindPreview(
                ShipPropulsionVisualApplier.LoadDefaultSettings(),
                _previewFamily,
                _team);
            SetLayerRecursive(hull, PreviewLayer());
        }

        void BindPreviewBank(GameObject hull)
        {
            if (hull == null)
                return;

            var bank = hull.GetComponent<ShipBankVisualApplier>();
            if (bank == null)
                bank = hull.AddComponent<ShipBankVisualApplier>();
            ShipBankVisualSettings settings = _previewFamily != null
                ? _previewFamily.bankVisualSettings
                : null;
            bank.BindPreview(settings);
        }

        void BindPreviewNameplate(GameObject hull)
        {
            if (hull == null)
                return;

            _previewNameplate = hull.GetComponent<ShipWorldNameplate>();
            if (_previewNameplate == null)
                _previewNameplate = hull.AddComponent<ShipWorldNameplate>();
            RefreshPreviewNameplate();
        }

        void RefreshPreviewNameplate()
        {
            if (_previewNameplate == null && _previewHull != null)
                _previewNameplate = _previewHull.GetComponent<ShipWorldNameplate>();
            if (_previewNameplate == null)
                return;

            _previewBadgeId = LocalPlayerBadge.Get();
            _previewNameplate.ApplyStudioPreview(
                _previewRoot != null ? _previewRoot.transform : null,
                PreviewLayer(),
                LocalPlayerDisplayName.Get(),
                _previewBadgeId,
                _team);
        }

        void LateUpdate()
        {
            if (!isActiveAndEnabled || _previewHull == null || _previewCam == null)
                return;

            TickPreviewFlight();
            int badgeId = LocalPlayerBadge.Get();
            if (badgeId != _previewBadgeId)
                RefreshPreviewNameplate();
        }

        void TickPreviewFlight()
        {
            const float maxSpeed = 8f;
            const float maxYawRate = 0.95f;
            float dt = Time.unscaledDeltaTime;
            _previewRetargetTimer -= dt;
            if (_previewRetargetTimer <= 0f)
            {
                _previewRetargetTimer = UnityEngine.Random.Range(1.6f, 3.2f);
                _previewTargetSpeed = UnityEngine.Random.value < 0.5f ? 0f : maxSpeed;

                float turnPick = UnityEngine.Random.value;
                float sign = UnityEngine.Random.value < 0.5f ? -1f : 1f;
                if (turnPick < 0.14f)
                    _previewTargetYawRate = 0f;
                else if (turnPick < 0.42f)
                    _previewTargetYawRate = sign * UnityEngine.Random.Range(0.28f, 0.52f) * maxYawRate;
                else
                    _previewTargetYawRate = sign * maxYawRate;
            }

            _previewSpeed = Mathf.MoveTowards(_previewSpeed, _previewTargetSpeed, 2.6f * dt);
            _previewYawRate = Mathf.MoveTowards(_previewYawRate, _previewTargetYawRate, 2.4f * dt);
            _previewHeading += _previewYawRate * dt;

            Vector3 forward = new Vector3(Mathf.Sin(_previewHeading), 0f, Mathf.Cos(_previewHeading));
            _previewHull.transform.localPosition += forward * (_previewSpeed * dt);
            if (forward.sqrMagnitude > 0.0001f)
                _previewHull.transform.localRotation = Quaternion.LookRotation(forward, Vector3.up);

            float turn = maxYawRate > 0.001f ? Mathf.Clamp(_previewYawRate / maxYawRate, -1f, 1f) : 0f;
            var prop = _previewHull.GetComponent<ShipPropulsionVisualApplier>();
            if (prop != null)
                prop.SetPreviewMotion(1f, turn);

            _previewCam.transform.position = _previewHull.transform.position + _previewCamOffset;
            if (_previewStarfield != null)
            {
                Vector3 world = _previewHull.transform.position;
                _previewStarfield.SetTravel(world.x, world.z);
            }
        }

        void RefreshPreviewThrusters(bool rebuild)
        {
            if (_previewHull == null)
                return;

            var prop = _previewHull.GetComponent<ShipPropulsionVisualApplier>();
            if (prop == null)
            {
                BindPreviewJets(_previewHull);
                prop = _previewHull.GetComponent<ShipPropulsionVisualApplier>();
            }

            if (prop == null)
                return;

            prop.SetPreviewTeam(_team);
            if (rebuild)
                prop.RebuildJets();
            else
                prop.ApplyCurrentTint();
            SetLayerRecursive(_previewHull, PreviewLayer());
        }

        void ConfigurePreviewCamera()
        {
            if (_previewCam == null)
                return;

            EnsurePreviewRenderTexture();
            _previewCam.clearFlags = CameraClearFlags.SolidColor;
            _previewCam.backgroundColor = new Color(0.01f, 0.015f, 0.03f, 1f);
            _previewCam.orthographic = true;
            _previewCam.orthographicSize = 4.2f;
            _previewCam.nearClipPlane = 0.2f;
            _previewCam.farClipPlane = 60f;
            _previewCam.useOcclusionCulling = false;
            _previewCam.targetTexture = _previewRt;
            _previewCam.depth = -80;
            _previewCam.allowHDR = true;
            _previewCam.cullingMask = 1 << PreviewLayer();
            if (_previewImage != null)
                _previewImage.texture = _previewRt;
        }

        void FramePreviewCamera(Transform hull)
        {
            if (_previewCam == null || hull == null)
                return;

            Bounds bounds = ComputePreviewBounds(hull.gameObject);
            float hullHalf = Mathf.Max(bounds.extents.x, bounds.extents.z, 0.35f);
            // Keep the ship centered; pull back enough that the in-game-scale nameplate
            // (clearance + stack) still sits in the lower half of the square.
            const float nameplateBelow = 2.65f;
            float half = Mathf.Max(hullHalf * 1.55f, hullHalf + nameplateBelow);
            _previewCam.orthographic = true;
            _previewCam.orthographicSize = half;
            _previewCam.nearClipPlane = 0.2f;
            _previewCam.farClipPlane = 60f;
            Vector3 hullPos = hull.position;
            _previewCam.transform.position = new Vector3(hullPos.x, hullPos.y + 22f, hullPos.z);
            _previewCam.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            _previewCamOffset = new Vector3(0f, 22f, 0f);
            EnsurePreviewStarfield();
            if (_previewStarfield != null)
                _previewStarfield.FitToCamera();
        }

        void EnsurePreviewStarfield()
        {
            if (_previewRoot == null || _previewCam == null)
                return;
            _previewStarfield = PreviewStarfieldBackdrop.Create(
                _previewRoot.transform,
                _previewCam,
                PreviewLayer());
        }

        static Bounds ComputePreviewBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            Bounds bounds = new Bounds(root.transform.position, Vector3.one * 2f);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer rend = renderers[i];
                if (rend == null || rend is ParticleSystemRenderer || rend is TrailRenderer)
                    continue;
                if (WorldBodyLabelLayout.ShouldSkipRenderer(rend))
                    continue;
                if (!any)
                {
                    bounds = rend.bounds;
                    any = true;
                }
                else
                    bounds.Encapsulate(rend.bounds);
            }

            if (!any)
                bounds = new Bounds(root.transform.position, new Vector3(3f, 1f, 4f));
            return bounds;
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
            hull.transform.localPosition = Vector3.zero;
            hull.transform.localRotation = Quaternion.identity;
            // Bigger than match presentation so the hull reads in the square; nameplate stays world-scaled.
            const float StudioHullScaleMul = 1.4f;
            hull.transform.localScale = Vector3.one * (BodyCollisionMath.ShipPresentationScale * StudioHullScaleMul);
            SetLayerRecursive(hull, PreviewLayer());
            BindPreviewJets(hull);
            BindPreviewBank(hull);
            BindPreviewNameplate(hull);
            FramePreviewCamera(hull.transform);
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
            RefreshPaintPresetSection();
            RefreshPreviewNameplate();
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

        void OnSvReleased()
        {
            ApplyPickerColor(flush: true);
            HidePicker(persist: false);
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
                LocalPlayerThrusterStyle.WriteAnchorStops(ref style, _team);
                PersistThrusterStyleFromPicker(style, flush);
                return;
            }

            if (TryGetLifetimeIndex(_pickerSlot, out int lifeIndex))
            {
                var style = BeginCustomThruster();
                style.FollowTeam = 0;
                LocalPlayerThrusterStyle.SetLifetimeStop(ref style, lifeIndex, color, _team);
                PersistThrusterStyleFromPicker(style, flush);
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
            LocalPlayerShipAccents.Clear();
            ShipAccentColorsRpcClient.NotifyChanged();
            HidePicker(persist: false);
            RefreshFromStore();
        }

        void PersistAndPreview(bool flush)
        {
            if (flush)
                ShipAccentColorsRpcClient.NotifyChanged();
            PaintWells();
            PaintPreview();
        }

        void FlushPersist()
        {
            LocalPlayerShipAccents.SetPresetIndex(LocalPlayerShipAccents.GetPresetIndex());
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
            RefreshPaintPresetSection();
        }

        void PaintPreview()
        {
            if (_previewHull == null)
                return;
            ShipColorizeAccentApplier.ApplyFromCapturedBase(
                _previewHull,
                LocalPlayerShipAccents.Get(),
                _team);
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
                case AccentSlot.Life0:
                case AccentSlot.Life1:
                case AccentSlot.Life2:
                case AccentSlot.Life3:
                    return LocalPlayerThrusterStyle.GetLifetimeStop(
                        LocalPlayerThrusterStyle.Get(),
                        (int)slot - (int)AccentSlot.Life0,
                        _team);
                default: return _color2;
            }
        }

        void PersistThrusterStyleFromPicker(LocalPlayerThrusterStyle.Style style, bool flush)
        {
            LocalPlayerThrusterStyle.Set(style);
            _previewFlameColorName = LocalPlayerThrusterStyle.ResolveFlameColorName(style, _team);
            RefreshThrusterSection();
            ShipPropulsionVisualApplier.ApplyTintToAllLive();
            RefreshPreviewThrusters(rebuild: false);
            if (flush)
                ShipAccentColorsRpcClient.NotifyChanged();
        }

        static AccentSlot LifetimeSlot(int index)
        {
            switch (index)
            {
                case 1: return AccentSlot.Life1;
                case 2: return AccentSlot.Life2;
                case 3: return AccentSlot.Life3;
                default: return AccentSlot.Life0;
            }
        }

        static bool TryGetLifetimeIndex(AccentSlot slot, out int index)
        {
            switch (slot)
            {
                case AccentSlot.Life0:
                    index = 0;
                    return true;
                case AccentSlot.Life1:
                    index = 1;
                    return true;
                case AccentSlot.Life2:
                    index = 2;
                    return true;
                case AccentSlot.Life3:
                    index = 3;
                    return true;
                default:
                    index = 0;
                    return false;
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
