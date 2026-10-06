using System;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Full-screen Customize Ship studio: hull presets, profile badge, and thrusters.
    /// Color1 stays the team Colorize material. Color2 / Color3 / Glow come from
    /// authored <see cref="ShipAccentPreset"/> assets — not free HSV picks.
    /// Thruster type swaps the authored JetFlame prefab. Jets always follow team Color1.
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

        static readonly Color PanelFill = new Color(0.012f, 0.016f, 0.028f, 0.96f);
        static readonly Color CaptionColor = new Color(0.62f, 0.78f, 0.95f, 0.92f);
        static readonly Color BodyColor = new Color(0.88f, 0.92f, 0.98f, 0.95f);
        static readonly Color AccentCyan = new Color(0.42f, 0.78f, 0.98f, 0.95f);
        static readonly Color AccentGold = new Color(0.95f, 0.78f, 0.22f, 0.95f);
        static readonly Color CardFill = new Color(0.04f, 0.07f, 0.12f, 0.92f);
        static readonly Color RowFill = new Color(0.06f, 0.09f, 0.14f, 0.96f);
        static readonly Color FrameTint = new Color(0.22f, 0.36f, 0.52f, 0.55f);

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
        readonly Image[] _presetSwatches = new Image[6];
        MainMenuBadgePicker _badgePicker;
        readonly Image[] _teamChips = new Image[5];
        GameObject _unlockBanner;
        TextMeshProUGUI _unlockBannerLabel;

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

        Texture2D _whiteTex;
        Sprite _whiteSprite;

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
            // Free players may cycle every look here; nothing is kept until they buy.
            TitanOrbitCosmeticGate.BeginHangarPreview();
            EnsureChrome();
            SetPreviewWorldActive(true);
            ApplyTeamToPreview();
            RefreshFromStore();
            RefreshThrusterSection();
            RefreshUnlockBanner();
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
        }

        /// <summary>Hides the studio and tears down the leftover preview camera / hull.</summary>
        public void Close()
        {
            if (TitanOrbitCosmeticGate.IsCustomizationUnlocked)
                FlushPersist();
            TitanOrbitCosmeticGate.EndHangarPreview();
            SetPreviewWorldActive(false);
            gameObject.SetActive(false);
        }

        void OnEnable()
        {
            TitanOrbit.Services.TitanOrbitEntitlements.OrbitUnlockedOwnershipChanged += OnOrbitUnlockedChanged;
        }

        void OnDisable()
        {
            TitanOrbit.Services.TitanOrbitEntitlements.OrbitUnlockedOwnershipChanged -= OnOrbitUnlockedChanged;
            TitanOrbitCosmeticGate.EndHangarPreview();
        }

        /// <summary>
        /// Entitlement flipped while the studio is open — clamp the preview and
        /// show or hide the Orbit Unlocked buy strip.
        /// </summary>
        void OnOrbitUnlockedChanged()
        {
            RefreshFromStore();
            RefreshThrusterSection();
            RefreshUnlockBanner();
            if (_badgePicker != null)
                _badgePicker.RefreshChip();
        }

        /// <summary>
        /// Gold CTA under the title. Hidden once Orbit Unlocked is owned.
        /// </summary>
        void EnsureUnlockBanner()
        {
            Transform panel = transform.Find("Panel");
            if (panel == null)
                return;

            Transform existing = panel.Find("UnlockBanner");
            GameObject banner = existing != null
                ? existing.gameObject
                : CreateUi("UnlockBanner", panel, typeof(Image), typeof(Button));
            var rt = banner.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(360f, 28f);
            rt.anchoredPosition = new Vector2(140f, -10f);
            var bg = banner.GetComponent<Image>();
            bg.color = new Color(0.95f, 0.78f, 0.22f, 0.92f);
            var button = banner.GetComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.None;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(OpenOrbitUnlockedFromCustomize);

            Transform labelTf = banner.transform.Find("Label");
            TextMeshProUGUI label = labelTf != null
                ? labelTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(banner.transform, "Label", "PREVIEW ONLY — UNLOCK TO KEEP", 12f, FontStyles.Bold);
            StretchFull(label.rectTransform);
            label.alignment = TextAlignmentOptions.Center;
            label.color = new Color(0.06f, 0.05f, 0.02f, 0.95f);
            label.raycastTarget = false;
            label.characterSpacing = 0.8f;

            _unlockBanner = banner;
            _unlockBannerLabel = label;
        }

        /// <summary>Shows the buy strip only for free players.</summary>
        void RefreshUnlockBanner()
        {
            if (_unlockBanner == null)
                EnsureUnlockBanner();
            if (_unlockBanner == null)
                return;
            bool locked = !TitanOrbitCosmeticGate.IsCustomizationUnlocked;
            _unlockBanner.SetActive(locked);
            if (_unlockBannerLabel != null)
                _unlockBannerLabel.text = "PREVIEW ONLY — UNLOCK TO KEEP";
        }

        /// <summary>Opens the one-item IAP overlay above this studio.</summary>
        void OpenOrbitUnlockedFromCustomize()
        {
            // This overlay is a child Canvas; the parent is the Main Menu canvas.
            Transform host = transform.parent != null ? transform.parent : transform.root;
            OrbitUnlockedPurchaseScreen.Open(host);
        }

        void OnDestroy()
        {
            if (_previewRt != null)
            {
                _previewRt.Release();
                Destroy(_previewRt);
            }

            if (_whiteTex != null)
                Destroy(_whiteTex);
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
            EnsureTeamPreviewSection();
            EnsureHullSection();
            EnsureBadgeSection();
            EnsureThrusterSection();
            EnsureUnlockBanner();
            PaintTeamStrip();
            RefreshUnlockBanner();
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
            _badgeChip = null;
            _badgeEmpty = null;
            _badgeCaption = null;
            _badgePicker = null;
            _unlockBanner = null;
            _unlockBannerLabel = null;
            _thrusterStyleLabel = null;
            _previewNameplate = null;
            for (int i = 0; i < _presetSwatches.Length; i++)
                _presetSwatches[i] = null;
            for (int i = 0; i < _teamChips.Length; i++)
                _teamChips[i] = null;
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
                "Team Color1 stays locked. Jets follow team color.",
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
        }

        /// <summary>
        /// Team A–E chips so the player can preview Color1. This does not change
        /// their match team — Color1 stays locked to the palette asset.
        /// </summary>
        void EnsureTeamPreviewSection()
        {
            Transform right = ContentRoot();
            if (right == null)
                return;

            DestroyNamedChild(right, "TeamStrip");
            Transform hull = right.Find("HullSection");
            if (hull != null)
                DestroyNamedChild(hull, "TeamStrip");

            Transform existing = right.Find("TeamPreviewSection");
            GameObject section = existing != null
                ? existing.gameObject
                : CreateUi("TeamPreviewSection", right, typeof(Image));
            var rt = section.GetComponent<RectTransform>();
            BindLayoutSlot(rt, 78f);
            section.GetComponent<Image>().color = CardFill;
            EnsureCardAccent(section.transform);
            section.transform.SetSiblingIndex(0);
            EnsureSectionHeader(section.transform, "TEAM PREVIEW");
            DestroyNamedChild(section.transform, "Blurb");
            EnsureTeamStrip(section.transform);
        }

        /// <summary>Hull paint cycle — arrows and name only, no Preset caption.</summary>
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
            BindLayoutSlot(rt, 108f);
            section.GetComponent<Image>().color = CardFill;
            EnsureCardAccent(section.transform);
            section.transform.SetSiblingIndex(1);
            EnsureSectionHeader(section.transform, "HULL");
            DestroyNamedChild(section.transform, "Blurb");
            DestroyNamedChild(section.transform, "TeamStrip");
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
            rt.sizeDelta = new Vector2(-16f, 40f);
            rt.anchoredPosition = new Vector2(0f, -28f);
            row.GetComponent<Image>().color = RowFill;
            DestroyNamedChild(row.transform, "Label");

            for (int i = 0; i < 5; i++)
            {
                TeamId team = (TeamId)(i + 1);
                string name = "Team" + team.ToLetter();
                Transform chipTf = row.transform.Find(name);
                GameObject chipGo = chipTf != null
                    ? chipTf.gameObject
                    : CreateUi(name, row.transform, typeof(Image), typeof(Button));
                var chipRt = chipGo.GetComponent<RectTransform>();
                chipRt.anchorMin = new Vector2(0.5f, 0.5f);
                chipRt.anchorMax = new Vector2(0.5f, 0.5f);
                chipRt.pivot = new Vector2(0.5f, 0.5f);
                chipRt.sizeDelta = new Vector2(30f, 24f);
                chipRt.anchoredPosition = new Vector2((i - 2) * 36f, 0f);
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
                string.Empty,
                -28f,
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
            rt.anchoredPosition = new Vector2(0f, -64f);
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
            NotifyOwnedCosmeticsChanged();
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

        static TextMeshProUGUI EnsureSectionHeader(Transform section, string title)
        {
            Transform headerTf = section.Find("Header");
            TextMeshProUGUI header = headerTf != null
                ? headerTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(section, "Header", title, 15f, FontStyles.Bold);
            var headerRt = header.rectTransform;
            headerRt.anchorMin = new Vector2(0f, 1f);
            headerRt.anchorMax = new Vector2(1f, 1f);
            headerRt.pivot = new Vector2(0.5f, 1f);
            headerRt.sizeDelta = new Vector2(-20f, 18f);
            headerRt.anchoredPosition = new Vector2(0f, -4f);
            header.text = title;
            header.fontSize = 12f;
            header.alignment = TextAlignmentOptions.MidlineLeft;
            header.color = AccentCyan;
            header.characterSpacing = 1.2f;
            header.gameObject.SetActive(true);
            return header;
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

        /// <summary>Flame type cycle — arrows and name only. Jets follow team Color1.</summary>
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
            BindLayoutSlot(rt, 70f);
            section.GetComponent<Image>().color = CardFill;
            EnsureCardAccent(section.transform);
            section.transform.SetSiblingIndex(2);

            DestroyNamedChild(section.transform, "ColorRow");
            DestroyNamedChild(section.transform, "ThrusterColorRow");
            DestroyNamedChild(section.transform, "LifetimeRow");
            DestroyNamedChild(section.transform, "Mode");
            DestroyNamedChild(section.transform, "ResetThrusters");
            DestroyNamedChild(section.transform, "Blurb");
            DestroyNamedChild(section.transform, "ColorPicker");
            Transform right = transform.Find("Panel/Right");
            DestroyNamedChild(right, "ColorPicker");
            DestroyNamedChild(transform.Find("Panel"), "ColorPicker");

            EnsureSectionHeader(section.transform, "THRUSTERS");
            _thrusterStyleLabel = EnsureCycleRow(
                section.transform,
                "StyleRow",
                string.Empty,
                -28f,
                () => CycleThrusterStyle(-1),
                () => CycleThrusterStyle(1));
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

            bool showCaption = !string.IsNullOrEmpty(caption);
            Transform capTf = row.transform.Find("Caption");
            if (showCaption)
            {
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
                cap.gameObject.SetActive(true);
            }
            else if (capTf != null)
            {
                capTf.gameObject.SetActive(false);
            }

            EnsureArrow(row.transform, "Prev", new Vector2(showCaption ? 0.24f : 0.08f, 0.5f), "<", onPrev);
            EnsureArrow(row.transform, "Next", new Vector2(showCaption ? 0.94f : 0.92f, 0.5f), ">", onNext);

            Transform valueTf = row.transform.Find("Value");
            TextMeshProUGUI value = valueTf != null
                ? valueTf.GetComponent<TextMeshProUGUI>()
                : CreateTmp(row.transform, "Value", caption, 14f, FontStyles.Bold);
            var valueRt = value.rectTransform;
            valueRt.anchorMin = new Vector2(showCaption ? 0.32f : 0.16f, 0f);
            valueRt.anchorMax = new Vector2(showCaption ? 0.80f : 0.84f, 1f);
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
        }

        LocalPlayerThrusterStyle.Style BeginCustomThruster()
        {
            var style = LocalPlayerThrusterStyle.Get();
            if (style.IsCustom)
                return style;

            style.HasCustom = 1;
            style.FollowTeam = 1;
            style.StyleIndex = (byte)ThrusterVfxBank.DefaultStyleIndex;
            return style;
        }

        void PersistThruster(LocalPlayerThrusterStyle.Style style, bool rebuildJets)
        {
            LocalPlayerThrusterStyle.Set(style);
            ThrusterVfxBank.DebugCycleIndex = LocalPlayerThrusterStyle.ResolveStyleIndex(style);
            NotifyOwnedCosmeticsChanged();
            if (TitanOrbitCosmeticGate.IsCustomizationUnlocked)
            {
                if (rebuildJets)
                    ShipPropulsionVisualApplier.RebuildAllLive();
                else
                    ShipPropulsionVisualApplier.ApplyTintToAllLive();
            }

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

        /// <summary>
        /// Drops saved Color2 / Color3 / Glow 1–3 so the hull uses the Colorize
        /// material defaults again. Color1 stays the team swatch.
        /// </summary>
        void OnResetClicked()
        {
            LocalPlayerShipAccents.Clear();
            NotifyOwnedCosmeticsChanged();
            RefreshFromStore();
        }

        void FlushPersist()
        {
            LocalPlayerShipAccents.SetPresetIndex(LocalPlayerShipAccents.GetPresetIndex());
            NotifyOwnedCosmeticsChanged();
        }

        /// <summary>
        /// Publishes paint / jets to the match only for Orbit Unlocked owners.
        /// Free-player preview stays on the studio hull.
        /// </summary>
        void NotifyOwnedCosmeticsChanged()
        {
            if (!TitanOrbitCosmeticGate.IsCustomizationUnlocked)
                return;
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
    }
}
