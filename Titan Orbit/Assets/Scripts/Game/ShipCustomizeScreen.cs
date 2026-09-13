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
    /// Full-screen hull-paint studio plus profile badge. Color1 stays the team Colorize material.
    /// Color2, Color3, Glow 1, Glow 2, and Glow 3 are color-well buttons — tap opens
    /// an HSV picker (saturation/value square + hue bar), not RGB sliders.
    /// CLOSE on the picker (or a tap on the preview / empty panel) dismisses it.
    /// <para>
    /// Opened from the Main Menu "Customize ship" button or the Orbit Menu hull button.
    /// Writes <see cref="LocalPlayerShipAccents"/> and pings
    /// <see cref="ShipAccentColorsRpcClient"/> so remotes see the paint.
    /// </para>
    /// Colorize .mat files store <c>a = 0</c>. Wells use an opaque white sprite so
    /// the starting palette is visible. Picker cursors are rings parented to the
    /// pads with bottom-left anchors so they stay inside the box.
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
            ApplyTeamToPreview();
            RefreshFromStore();
            HidePicker();
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
        }

        /// <summary>Hides the studio. Accents flush to disk here in case a drag never released.</summary>
        public void Close()
        {
            HidePicker();
            FlushPersist();
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
            if (transform.Find("Backdrop") == null)
                BuildFullChrome();

            BindChromeRefs();
            EnsureTeamStrip();
            EnsureBadgeRow();
            EnsurePickerCloseButton();
            EnsurePanelDismissesPicker();
            PaintTeamStrip();
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
            panelRt.sizeDelta = new Vector2(720f, 740f);
            var panelImage = panel.GetComponent<Image>();
            panelImage.color = PanelFill;
            panelImage.raycastTarget = true;

            var title = CreateTmp(panel.transform, "Title", "CUSTOMIZE SHIP", 26f, FontStyles.Bold);
            PlaceTop(title.rectTransform, 16f, 40f);
            title.alignment = TextAlignmentOptions.Center;
            title.color = CaptionColor;
            title.characterSpacing = 1.6f;

            var subtitle = CreateTmp(
                panel.transform,
                "Subtitle",
                "Color1 is the team preset (edit TeamColor1Palette). Tap A–E to preview. Accents are yours.",
                14f,
                FontStyles.Normal);
            PlaceTop(subtitle.rectTransform, 54f, 24f);
            subtitle.alignment = TextAlignmentOptions.Center;
            subtitle.color = CaptionColor;

            _previewImage = CreateUi("Preview", panel.transform, typeof(RawImage))
                .GetComponent<RawImage>();
            var previewRt = _previewImage.rectTransform;
            previewRt.anchorMin = new Vector2(0.5f, 1f);
            previewRt.anchorMax = new Vector2(0.5f, 1f);
            previewRt.pivot = new Vector2(0.5f, 1f);
            previewRt.sizeDelta = new Vector2(420f, 190f);
            previewRt.anchoredPosition = new Vector2(0f, -82f);
            _previewRt = new RenderTexture(512, 256, 16)
            {
                name = "ShipCustomizePreview",
                antiAliasing = 1,
            };
            _previewImage.texture = _previewRt;
            _previewImage.color = Color.white;

            BuildPreviewWorld();

            // Two columns so Color2 / Color3 / Glow 1–3 fit without burying the footer.
            const float leftX = -132f;
            const float rightX = 132f;
            float wellY = -382f;
            _teamSwatch = CreateLockedWell(panel.transform, "COLOR 1  LOCKED", leftX, wellY);
            _color2Well = CreatePickWell(panel.transform, "COLOR 2", rightX, wellY, AccentSlot.Color2);
            wellY -= 58f;
            _color3Well = CreatePickWell(panel.transform, "COLOR 3", leftX, wellY, AccentSlot.Color3);
            _glow1Well = CreatePickWell(panel.transform, "GLOW 1", rightX, wellY, AccentSlot.Emission1);
            wellY -= 58f;
            _glow2Well = CreatePickWell(panel.transform, "GLOW 2", leftX, wellY, AccentSlot.Emission2);
            _glow3Well = CreatePickWell(panel.transform, "GLOW 3", rightX, wellY, AccentSlot.Emission3);

            _paintStateLabel = CreateTmp(panel.transform, "PaintState", "TEAM DEFAULT COLORS", 13f, FontStyles.Bold);
            PlaceTop(_paintStateLabel.rectTransform, 532f, 22f);
            _paintStateLabel.alignment = TextAlignmentOptions.Center;
            _paintStateLabel.color = CaptionColor;

            CreateFooterButton(
                panel.transform,
                "Reset",
                "RESET TO DEFAULT COLORS",
                new Vector2(-168f, 22f),
                new Vector2(300f, 44f),
                new Color(0.32f, 0.16f, 0.12f, 0.96f),
                OnResetClicked);
            CreateFooterButton(
                panel.transform,
                "Done",
                "DONE",
                new Vector2(168f, 22f),
                new Vector2(300f, 44f),
                new Color(0.14f, 0.18f, 0.28f, 0.95f),
                Close);

            BuildPickerPanel(panel.transform);
        }

        void BindChromeRefs()
        {
            Transform panel = transform.Find("Panel");
            if (panel == null)
                return;

            if (_previewImage == null)
                _previewImage = panel.Find("Preview")?.GetComponent<RawImage>();
            if (_teamSwatch == null)
                _teamSwatch = panel.Find("TeamWell/Well")?.GetComponent<Image>();
            if (_color2Well == null)
                _color2Well = panel.Find("COLOR 2Well/Well")?.GetComponent<Image>();
            if (_color3Well == null)
                _color3Well = panel.Find("COLOR 3Well/Well")?.GetComponent<Image>();
            if (_glow1Well == null)
                _glow1Well = panel.Find("GLOW 1Well/Well")?.GetComponent<Image>();
            if (_glow2Well == null)
                _glow2Well = panel.Find("GLOW 2Well/Well")?.GetComponent<Image>();
            if (_glow3Well == null)
                _glow3Well = panel.Find("GLOW 3Well/Well")?.GetComponent<Image>();
            if (_paintStateLabel == null)
                _paintStateLabel = panel.Find("PaintState")?.GetComponent<TextMeshProUGUI>();
            if (_pickerPanel == null)
                _pickerPanel = panel.Find("ColorPicker") as RectTransform;
        }

        /// <summary>
        /// Team A–E chips so the player can preview Color1 presets. This does not
        /// change their match team — Color1 is still locked to the palette asset.
        /// </summary>
        void EnsureTeamStrip()
        {
            Transform panel = transform.Find("Panel");
            if (panel == null)
                return;

            Transform existing = panel.Find("TeamStrip");
            GameObject row = existing != null
                ? existing.gameObject
                : CreateUi("TeamStrip", panel, typeof(Image));
            var rt = row.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(520f, 40f);
            rt.anchoredPosition = new Vector2(0f, -278f);
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
        /// Badge chip above the color wells. Opens the same grid overlay the Main Menu used.
        /// </summary>
        void EnsureBadgeRow()
        {
            Transform panel = transform.Find("Panel");
            if (panel == null)
                return;

            Transform existing = panel.Find("BadgeRow");
            GameObject row = existing != null
                ? existing.gameObject
                : CreateWellRow(panel, "BadgeRow", "BADGE", 0f, -322f);
            var rt = row.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(520f, 56f);
            rt.anchoredPosition = new Vector2(0f, -322f);

            Transform wellTf = row.transform.Find("Well");
            if (wellTf != null)
                wellTf.gameObject.SetActive(false);

            Transform chipTf = row.transform.Find("Chip");
            GameObject chipGo = chipTf != null
                ? chipTf.gameObject
                : CreateUi("Chip", row.transform, typeof(Image), typeof(Button));
            var chipRt = chipGo.GetComponent<RectTransform>();
            chipRt.anchorMin = new Vector2(0.58f, 0.08f);
            chipRt.anchorMax = new Vector2(0.70f, 0.92f);
            chipRt.offsetMin = Vector2.zero;
            chipRt.offsetMax = Vector2.zero;
            var chipFill = chipGo.GetComponent<Image>();
            chipFill.sprite = _whiteSprite;
            chipFill.color = new Color(0.18f, 0.24f, 0.32f, 0.85f);
            chipFill.raycastTarget = true;

            Transform badgeTf = chipGo.transform.Find("Icon");
            GameObject badgeGo = badgeTf != null
                ? badgeTf.gameObject
                : CreateUi("Icon", chipGo.transform, typeof(Image));
            StretchFull(badgeGo.GetComponent<RectTransform>());
            badgeGo.GetComponent<RectTransform>().offsetMin = new Vector2(4f, 4f);
            badgeGo.GetComponent<RectTransform>().offsetMax = new Vector2(-4f, -4f);
            _badgeChip = badgeGo.GetComponent<Image>();
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

            Transform hintTf = row.transform.Find("PickHint");
            if (hintTf == null)
            {
                var hint = CreateTmp(row.transform, "PickHint", "TAP TO CHANGE", 12f, FontStyles.Bold);
                var hintRt = hint.rectTransform;
                hintRt.anchorMin = new Vector2(1f, 0.5f);
                hintRt.anchorMax = new Vector2(1f, 0.5f);
                hintRt.pivot = new Vector2(1f, 0.5f);
                hintRt.sizeDelta = new Vector2(130f, 28f);
                hintRt.anchoredPosition = new Vector2(-12f, 0f);
                hint.alignment = TextAlignmentOptions.MidlineRight;
                hint.color = CaptionColor;
                _badgeCaption = hint;
            }
            else
            {
                _badgeCaption = hintTf.GetComponent<TextMeshProUGUI>();
            }

            _badgePicker = row.GetComponent<MainMenuBadgePicker>();
            if (_badgePicker == null)
                _badgePicker = row.AddComponent<MainMenuBadgePicker>();
            _badgePicker.Configure(_badgeChip, chipFill, _badgeEmpty, _badgeCaption);

            var chipBtn = chipGo.GetComponent<Button>();
            chipBtn.targetGraphic = chipFill;
            chipBtn.transition = Selectable.Transition.None;
            chipBtn.onClick.RemoveAllListeners();
            chipBtn.onClick.AddListener(() =>
            {
                HidePicker();
                _badgePicker.OpenOverlay();
            });
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
                return;

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
            var listener = camGo.GetComponent<AudioListener>();
            if (listener != null)
                Destroy(listener);
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
            if (_pickerPanel != null)
                _pickerPanel.gameObject.SetActive(false);
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
            Color rgb = Color.HSVToRGB(_hue, _sat, _val);
            SetSlotColor(_pickerSlot, ShipColorizeAccentApplier.Opaque(rgb));
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
            HidePicker();
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
