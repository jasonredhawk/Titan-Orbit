using System.Collections.Generic;
using TitanOrbit.Data;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Orbit Menu lineage overlays on the Ship Upgrade Tree.
    /// Family name, bullet type, and the bicycle-wheel all sit in the top-left
    /// so the L1–L6 hull cards keep the rest of the row.
    /// <para>
    /// Ice ring = stock 1.00×. Spokes past the ring are boosts, spokes short of
    /// the ring are penalties. Stock spokes stay muted; live ones take category
    /// colour (washed, not grey). Spoke tips stay horizontal: name only at 1×,
    /// name plus signed percent when the family actually changes the stat.
    /// Titles sit on a fixed outer orbit; only the spoke length follows the value.
    /// </para>
    /// Both plates ignore the tree VerticalLayoutGroup
    /// (<see cref="PreferredHeight"/> is 0). Presentation-only — dock time, not
    /// per frame. Paired with <see cref="ShipUpgradeTreeUI.ApplyFamilyIdentity"/>.
    /// </summary>
    public class ShipFamilyBonusListUI : MonoBehaviour
    {
        /// <summary>Caption the player reads centered above the wheel.</summary>
        public const string CaptionText = "FAMILY BONUSES";
        public const string HubCaptionText = "Fleet\nBonuses";

        /// <summary>Empty-family / missing definition copy.</summary>
        public const string NoFamilyText = "NO LINEAGE LOCKED";

        const float WheelSize = 238f;
        const float PadX = 0f;
        const float PadTop = 0f;
        const float PadBottom = 0f;
        const float SpokeLabelFont = 7.25f;
        /// <summary>Shared line box so a taller glyph (percent, digits) cannot resize one title.</summary>
        const float SpokeLabelLineHeight = 12f;
        const float WheelPlatePadLeft = 88f;
        /// <summary>
        /// Pull the wheel square up by this many pixels so the empty 12 o'clock
        /// band does not sit as a blank gap under the description. Upper spoke
        /// titles stay a few pixels below the identity rail.
        /// </summary>
        const float WheelPlateTopTuck = 28f;
        const float IdentityRailWidth = 268f;
        const float IdentityRailMinHeight = 40f;

        /// <summary>Hover card width. Wide enough for a wrapped description.</summary>
        const float SpokeTipWidth = 268f;

        /// <summary>Floor so a missing description still looks like a card, not a sliver.</summary>
        const float SpokeTipMinHeight = 88f;

        static readonly Color CaptionColor = new Color(0.62f, 0.78f, 0.95f, 0.95f);
        static readonly Color IdentityValue = new Color(0.42f, 0.54f, 0.62f, 0.55f);
        static readonly Color NeutralValue = new Color(0.62f, 0.78f, 0.95f, 1f);
        static readonly Color IdentityRailFill = new Color(0.012f, 0.016f, 0.028f, 0.45f);

        /// <summary>Reusable row buffer — dock / tab refresh only, not the sim tick.</summary>
        readonly List<FamilyStatHudCopy.BonusRow> _rows = new List<FamilyStatHudCopy.BonusRow>(24);

        /// <summary>Pooled titles, one per spoke. Extra slots hide.</summary>
        readonly List<TextMeshProUGUI> _spokeLabels = new List<TextMeshProUGUI>(24);

        RectTransform _rootRt;
        LayoutElement _rootLe;
        RectTransform _identityRt;
        RectTransform _wheelPlateRt;
        TextMeshProUGUI _familyName;
        TextMeshProUGUI _bankCaption;
        TextMeshProUGUI _bankDetail;
        TextMeshProUGUI _emptyLabel;
        GameObject _wheelPlate;
        RectTransform _wheelRt;
        ShipFamilyBonusWheelGraphic _wheel;
        TextMeshProUGUI _hubLabel;
        ShipStatTooltipChrome.Handles _spokeTip;
        TMP_FontAsset _font;
        bool _built;

        /// <summary>
        /// True while <see cref="PinOverlays"/> is writing rects. Stops
        /// <see cref="OnRectTransformDimensionsChange"/> from re-entering and
        /// freezing Local Play (layout loop).
        /// </summary>
        bool _pinning;

        /// <summary>
        /// Always 0 — overlays must not shrink the hull-card row.
        /// </summary>
        public float PreferredHeight { get; private set; }

        /// <summary>
        /// Builds the chrome under <paramref name="parent"/> when this component
        /// was added at runtime (older ShipUpgradeTree prefabs have no child).
        /// Called from <see cref="ShipUpgradeTreeUI.EnsurePanelHeader"/>.
        /// </summary>
        public static ShipFamilyBonusListUI Create(
            Transform parent,
            Transform siblingAfter,
            TMP_FontAsset font)
        {
            var go = new GameObject("FamilyBonusMatrix", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            if (siblingAfter != null)
                go.transform.SetSiblingIndex(siblingAfter.GetSiblingIndex() + 1);

            var list = go.AddComponent<ShipFamilyBonusListUI>();
            list._font = font;
            list.EnsureBuilt();
            return list;
        }

        /// <summary>
        /// Writes family name, bullet type, and the bonus wheel in the top-left.
        /// Null family shows the empty caption.
        /// </summary>
        public void Paint(
            ShipFamilyDefinition family,
            int shipLevel = 1,
            int planetOrHullBankIndex = -1)
        {
            EnsureBuilt();
            if (_rootRt == null)
                return;

            if (family == null)
            {
                SetEmpty(NoFamilyText);
                PinOverlays();
                return;
            }

            // [TITAN-ORBIT] Fleet Bonuses are ShipFamilyDefinition.specialBonuses only.
            // Bullet-bank vs-target muls (VS GEMS, VS ASTEROIDS, BANK DAMAGE) are weapon
            // stats, not family identity — they stay on the weapon rail, not this wheel.
            _rows.Clear();
            FamilyStatHudCopy.CollectBonusRows(family.specialBonuses, _rows, includeIdentity: true);

            // [TITAN-ORBIT] Planet roll wins over the family Laserbolt fallback.
            string typeName = BulletBankHudCopy.FormatFamilyTypeName(family, planetOrHullBankIndex);

            if (_emptyLabel != null)
                _emptyLabel.gameObject.SetActive(false);
            if (_identityRt != null)
                _identityRt.gameObject.SetActive(true);
            if (_wheelPlate != null)
                _wheelPlate.SetActive(true);

            if (_familyName != null)
                _familyName.text = FamilyStatHudCopy.FormatFamilyCaption(family);
            if (_bankCaption != null)
            {
                _bankCaption.text = string.IsNullOrEmpty(typeName)
                    ? string.Empty
                    : typeName.ToUpperInvariant();
                _bankCaption.gameObject.SetActive(!string.IsNullOrEmpty(typeName));
            }

            if (_bankDetail != null)
            {
                string detail = BulletBankHudCopy.FormatFamilyWeaponRailDetail(
                    family, shipLevel, planetOrHullBankIndex);
                _bankDetail.text = detail;
                bool show = !string.IsNullOrEmpty(detail);
                _bankDetail.gameObject.SetActive(show);
                if (show)
                {
                    _bankDetail.ForceMeshUpdate();
                    var detailLe = _bankDetail.GetComponent<LayoutElement>();
                    if (detailLe != null)
                        detailLe.preferredHeight = Mathf.Max(14f, _bankDetail.preferredHeight + 2f);
                }
            }

            if (_wheel != null)
                _wheel.SetRows(_rows);
            if (_hubLabel != null)
                _hubLabel.text = HubCaptionText;

            PinOverlays();
            PaintSpokeLabels();
        }

        /// <summary>
        /// Creates the stretch overlay host, top-left identity rail, and
        /// bottom-left wheel plate. Safe to call again — later calls no-op.
        /// </summary>
        public void EnsureBuilt()
        {
            if (_built)
                return;
            _built = true;

            _rootRt = transform as RectTransform;
            if (_rootRt == null)
                _rootRt = gameObject.AddComponent<RectTransform>();

            if (transform.childCount > 0)
                WipeOwnedChrome();

            // Root is a transparent stretch host — no dark plate over the tree.
            var fill = gameObject.GetComponent<Image>();
            if (fill != null)
                fill.enabled = false;

            var vlg = gameObject.GetComponent<VerticalLayoutGroup>();
            if (vlg != null)
                vlg.enabled = false;

            _rootLe = gameObject.GetComponent<LayoutElement>();
            if (_rootLe == null)
                _rootLe = gameObject.AddComponent<LayoutElement>();
            _rootLe.ignoreLayout = true;
            _rootLe.minHeight = 0f;
            _rootLe.preferredHeight = 0f;
            _rootLe.flexibleHeight = 0f;
            _rootLe.flexibleWidth = 0f;

            BuildIdentityRail();
            BuildWheelPlate();

            _emptyLabel = CreateLabel(transform, "Empty", NoFamilyText, 11f, IdentityValue, FontStyles.Normal);
            _emptyLabel.alignment = TextAlignmentOptions.MidlineLeft;
            _emptyLabel.overflowMode = TextOverflowModes.Overflow;
            _emptyLabel.gameObject.SetActive(false);

            PinOverlays();
        }

        /// <summary>Top-left COSMIC SHARK / FIREBALLS stack.</summary>
        void BuildIdentityRail()
        {
            var go = new GameObject("IdentityRail", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            _identityRt = go.GetComponent<RectTransform>();
            var img = go.AddComponent<Image>();
            img.color = IdentityRailFill;
            img.raycastTarget = false;
            var vlg = go.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 10, 4, 4);
            vlg.spacing = 0;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            _familyName = CreateLabel(go.transform, "Family", "FAMILY", 15f, CaptionColor, FontStyles.Bold);
            _familyName.characterSpacing = 1.4f;
            _familyName.alignment = TextAlignmentOptions.MidlineLeft;
            _familyName.overflowMode = TextOverflowModes.Overflow;
            var famLe = _familyName.gameObject.AddComponent<LayoutElement>();
            famLe.preferredHeight = 20f;

            _bankCaption = CreateLabel(go.transform, "BankType", string.Empty, 11f, NeutralValue, FontStyles.Bold);
            _bankCaption.characterSpacing = 1.2f;
            _bankCaption.alignment = TextAlignmentOptions.MidlineLeft;
            _bankCaption.overflowMode = TextOverflowModes.Overflow;
            _bankCaption.gameObject.SetActive(false);
            var bankLe = _bankCaption.gameObject.AddComponent<LayoutElement>();
            bankLe.preferredHeight = 16f;

            _bankDetail = CreateLabel(go.transform, "BankDetail", string.Empty, 8.5f, IdentityValue, FontStyles.Normal);
            _bankDetail.richText = true;
            _bankDetail.enableWordWrapping = true;
            _bankDetail.overflowMode = TextOverflowModes.Overflow;
            _bankDetail.alignment = TextAlignmentOptions.TopLeft;
            _bankDetail.gameObject.SetActive(false);
            var detailLe = _bankDetail.gameObject.AddComponent<LayoutElement>();
            detailLe.minHeight = 14f;
            detailLe.preferredHeight = 28f;
            detailLe.flexibleHeight = 0f;
        }

        /// <summary>Top-left family-name caption + line wheel, under the identity rail.</summary>
        void BuildWheelPlate()
        {
            _wheelPlate = new GameObject("WheelPlate", typeof(RectTransform));
            _wheelPlate.transform.SetParent(transform, false);
            _wheelPlateRt = _wheelPlate.GetComponent<RectTransform>();
            var vlg = _wheelPlate.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset((int)PadX, (int)PadX, (int)PadTop, (int)PadBottom);
            vlg.spacing = 0f;
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var wheelGo = new GameObject("Wheel", typeof(RectTransform));
            wheelGo.transform.SetParent(_wheelPlate.transform, false);
            _wheelRt = wheelGo.GetComponent<RectTransform>();
            var wheelLe = wheelGo.AddComponent<LayoutElement>();
            wheelLe.minWidth = WheelSize;
            wheelLe.preferredWidth = WheelSize;
            wheelLe.minHeight = WheelSize;
            wheelLe.preferredHeight = WheelSize;
            wheelLe.flexibleWidth = 0f;
            wheelLe.flexibleHeight = 0f;
            _wheel = wheelGo.AddComponent<ShipFamilyBonusWheelGraphic>();
            _wheel.raycastTarget = false;

            _hubLabel = CreateLabel(wheelGo.transform, "Hub", HubCaptionText, 9f, CaptionColor, FontStyles.Bold);
            _hubLabel.alignment = TextAlignmentOptions.Center;
            _hubLabel.overflowMode = TextOverflowModes.Overflow;
            _hubLabel.enableWordWrapping = true;
            _hubLabel.maxVisibleLines = 2;
            _hubLabel.lineSpacing = -4f;
            var hubRt = _hubLabel.rectTransform;
            hubRt.anchorMin = new Vector2(0.5f, 0.5f);
            hubRt.anchorMax = new Vector2(0.5f, 0.5f);
            hubRt.pivot = new Vector2(0.5f, 0.5f);
            hubRt.sizeDelta = new Vector2(56f, 26f);
            hubRt.anchoredPosition = Vector2.zero;
        }

        /// <summary>Shows the empty caption and hides both overlays.</summary>
        void SetEmpty(string copy)
        {
            if (_wheelPlate != null)
                _wheelPlate.SetActive(false);
            if (_identityRt != null)
                _identityRt.gameObject.SetActive(true);
            if (_familyName != null)
                _familyName.text = copy;
            if (_bankCaption != null)
            {
                _bankCaption.text = string.Empty;
                _bankCaption.gameObject.SetActive(false);
            }

            if (_bankDetail != null)
            {
                _bankDetail.text = string.Empty;
                _bankDetail.gameObject.SetActive(false);
            }

            HideSpokeLabels();
            HideSpokeHover();

            if (_emptyLabel != null)
                _emptyLabel.gameObject.SetActive(false);
        }

        /// <summary>
        /// One horizontal line on the fixed outer orbit. Spoke length can
        /// change with the value; the title does not move. Stock 1× is the name only.
        /// </summary>
        void PaintSpokeLabels()
        {
            if (_wheel == null || _wheelRt == null)
                return;

            int count = _wheel.SpokeCount;
            for (int i = 0; i < count; i++)
            {
                if (!_wheel.TryGetSpokeLabelAnchor(i, out Vector2 anchor, out float angleRad, out FamilyStatHudCopy.BonusRow row))
                    continue;

                TextMeshProUGUI label = i < _spokeLabels.Count
                    ? _spokeLabels[i]
                    : CreateSpokeLabel();
                if (i >= _spokeLabels.Count)
                    _spokeLabels.Add(label);

                ApplySpokeLabel(label, anchor, angleRad, row);
                if (label.gameObject != null)
                    label.gameObject.SetActive(true);
            }

            for (int i = count; i < _spokeLabels.Count; i++)
            {
                if (_spokeLabels[i] != null)
                    _spokeLabels[i].gameObject.SetActive(false);
            }
        }

        /// <summary>Writes one-line copy, colour, hover, and pose on the fixed orbit.</summary>
        void ApplySpokeLabel(
            TextMeshProUGUI label,
            Vector2 fromCenter,
            float angleRad,
            in FamilyStatHudCopy.BonusRow row)
        {
            if (label == null)
                return;

            // [TITAN-ORBIT] One point size for every spoke. A tight width box and
            // auto-size were letting longer bonuses render smaller than short ones.
            LockSpokeLabelMetrics(label);
            if (row.IsIdentity)
                label.text = row.FullLabel;
            else
                label.text = row.FullLabel + "  " + FamilyStatHudCopy.FormatSignedPercent(row);

            Color cat = ShipFamilyBonusWheelGraphic.ColorForCategory(row.Category);
            if (row.IsIdentity)
                label.color = ShipFamilyBonusWheelGraphic.MuteCategory(cat);
            else if (row.IsNeutral)
                label.color = NeutralValue;
            else if (row.IsBoost)
                label.color = new Color(0.35f, 0.98f, 0.62f, 1f);
            else if (row.IsPenalty)
                label.color = new Color(0.95f, 0.72f, 0.32f, 1f);
            else
                label.color = cat;

            LockSpokeLabelMetrics(label);
            label.ForceMeshUpdate();
            Vector2 pref = label.GetPreferredValues(label.text);
            // Measure can rewrite point size when a glyph falls back. Put it back.
            LockSpokeLabelMetrics(label);
            Vector2 dir = new Vector2(Mathf.Cos(angleRad), Mathf.Sin(angleRad));
            Vector2 pos = fromCenter;
            pos.x = Mathf.Round(pos.x);
            pos.y = Mathf.Round(pos.y);

            RectTransform rt = label.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.localRotation = Quaternion.identity;
            rt.localScale = Vector3.one;
            rt.sizeDelta = new Vector2(Mathf.Max(20f, pref.x + 4f), SpokeLabelLineHeight);
            rt.anchoredPosition = pos;
            if (dir.x >= 0.28f)
            {
                rt.pivot = new Vector2(0f, 0.5f);
                label.alignment = TextAlignmentOptions.MidlineLeft;
            }
            else if (dir.x <= -0.28f)
            {
                rt.pivot = new Vector2(1f, 0.5f);
                label.alignment = TextAlignmentOptions.MidlineRight;
            }
            else if (dir.y > 0f)
            {
                rt.pivot = new Vector2(0.5f, 0f);
                label.alignment = TextAlignmentOptions.Bottom;
            }
            else
            {
                rt.pivot = new Vector2(0.5f, 1f);
                label.alignment = TextAlignmentOptions.Top;
            }

            var hover = label.GetComponent<SpokeHover>();
            if (hover != null)
            {
                hover.Owner = this;
                hover.FullTitle = row.FullLabel;
                // Percent plus the sentence that explains the spoke. Built here
                // so pointer-enter only assigns text it already has.
                hover.BodyCopy = FamilyStatHudCopy.FormatSpokeHoverBody(row);
                hover.Accent = ShipFamilyBonusWheelGraphic.ColorForCategory(row.Category);
            }
        }

        /// <summary>
        /// One pooled spoke title with a hit pad so hover works on the small type.
        /// Parent is the wheel so local pos matches the mesh.
        /// </summary>
        TextMeshProUGUI CreateSpokeLabel()
        {
            var tmp = CreateLabel(_wheelRt, "Spoke", "MOVE SPEED", SpokeLabelFont, CaptionColor, FontStyles.Bold);
            LockSpokeLabelMetrics(tmp);
            // [UNITY] Raycast so IPointerEnter fires. Cards still receive clicks
            // on empty wheel glass because the plate itself has no Graphic.
            tmp.raycastTarget = true;

            var hover = tmp.gameObject.AddComponent<SpokeHover>();
            hover.Owner = this;
            return tmp;
        }

        /// <summary>
        /// Pins one spoke title to <see cref="SpokeLabelFont"/>. Called on create
        /// and every paint so a longer name cannot drop to a smaller point size.
        /// </summary>
        static void LockSpokeLabelMetrics(TextMeshProUGUI label)
        {
            if (label == null)
                return;

            label.enableAutoSizing = false;
            label.fontSizeMin = SpokeLabelFont;
            label.fontSizeMax = SpokeLabelFont;
            label.fontSize = SpokeLabelFont;
            label.fontStyle = FontStyles.Bold;
            label.characterSpacing = 0f;
            label.lineSpacing = 0f;
            label.richText = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.maxVisibleLines = 1;
            label.rectTransform.localScale = Vector3.one;
        }

        /// <summary>Hides every pooled spoke title (empty / no-family state).</summary>
        void HideSpokeLabels()
        {
            for (int i = 0; i < _spokeLabels.Count; i++)
            {
                if (_spokeLabels[i] != null)
                    _spokeLabels[i].gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Shared chrome for a spoke hover. Caption is the full name
        /// (MOVE SPEED). Body is the lineage percent plus a short description
        /// of what that family multiplier changes.
        /// </summary>
        internal void ShowSpokeHover(SpokeHover spoke)
        {
            if (spoke == null || string.IsNullOrWhiteSpace(spoke.FullTitle))
                return;

            EnsureSpokeTip();
            if (_spokeTip.Root == null)
                return;

            // --- Copy ---
            if (_spokeTip.CaptionLabel != null)
                _spokeTip.CaptionLabel.text = spoke.FullTitle.ToUpperInvariant();
            if (_spokeTip.BodyLabel != null)
                _spokeTip.BodyLabel.text = string.IsNullOrEmpty(spoke.BodyCopy) ? string.Empty : spoke.BodyCopy;

            // Stripe follows the wheel category (move / combat / hull / energy / hold).
            ShipStatTooltipChrome.ApplyAccent(in _spokeTip, spoke.Accent);

            var self = spoke.transform as RectTransform;
            var tip = _spokeTip.RootRect;
            Canvas canvas = GetComponentInParent<Canvas>();
            if (self == null || tip == null || canvas == null)
                return;

            // Active before the measure. An inactive TMP rect can report 0 height
            // and the description would draw past the card.
            _spokeTip.Root.SetActive(true);

            // --- Size to the description ---
            // Width first so TMP wraps before we read preferredHeight.
            tip.sizeDelta = new Vector2(SpokeTipWidth, tip.sizeDelta.y);
            if (_spokeTip.BodyLabel != null)
                _spokeTip.BodyLabel.ForceMeshUpdate(true);
            float bodyH = _spokeTip.BodyLabel != null ? _spokeTip.BodyLabel.preferredHeight : 0f;
            float tipH = Mathf.Max(SpokeTipMinHeight, bodyH + _spokeTip.ExtraHeightPadding);
            tip.sizeDelta = new Vector2(SpokeTipWidth, tipH);

            // --- Place just above the spoke title, then keep it on the canvas ---
            tip.SetParent(canvas.transform, false);
            Vector3[] corners = new Vector3[4];
            self.GetWorldCorners(corners);
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(canvas.worldCamera, corners[2]);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvas.transform as RectTransform, screen, canvas.worldCamera, out Vector2 local);
            tip.pivot = new Vector2(0f, 0f);
            tip.anchoredPosition = ClampSpokeTip(canvas.transform as RectTransform, local + new Vector2(10f, 10f), tip.sizeDelta);
        }

        /// <summary>
        /// Keeps the hover card inside the canvas. Pivot is bottom-left, so the
        /// card grows up and to the right from <paramref name="bottomLeft"/>.
        /// </summary>
        static Vector2 ClampSpokeTip(RectTransform canvasRt, Vector2 bottomLeft, Vector2 size)
        {
            if (canvasRt == null)
                return bottomLeft;

            Rect area = canvasRt.rect;
            const float margin = 8f;
            float maxX = area.xMax - size.x - margin;
            float maxY = area.yMax - size.y - margin;
            bottomLeft.x = Mathf.Clamp(bottomLeft.x, area.xMin + margin, Mathf.Max(area.xMin + margin, maxX));
            bottomLeft.y = Mathf.Clamp(bottomLeft.y, area.yMin + margin, Mathf.Max(area.yMin + margin, maxY));
            return bottomLeft;
        }

        /// <summary>Hides the shared spoke hover card.</summary>
        internal void HideSpokeHover()
        {
            if (_spokeTip.Root != null)
                _spokeTip.Root.SetActive(false);
        }

        /// <summary>Builds the shared hover card once (same chrome as gear tips).</summary>
        void EnsureSpokeTip()
        {
            if (_spokeTip.Root != null)
                return;

            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
                return;

            _spokeTip = ShipStatTooltipChrome.Build(
                "FamilyBonusSpokeTip",
                canvas.transform,
                "MOVE SPEED",
                SpokeTipWidth,
                SpokeTipMinHeight,
                1f);
            if (_spokeTip.Root != null)
                _spokeTip.Root.SetActive(false);
        }

        /// <summary>Family + type + wrapped specials / stats. Dock refresh only.</summary>
        float MeasureIdentityRailHeight()
        {
            float h = 8f + 20f;
            if (_bankCaption != null && _bankCaption.gameObject.activeSelf)
                h += 16f;
            if (_bankDetail != null && _bankDetail.gameObject.activeSelf)
            {
                _bankDetail.ForceMeshUpdate();
                h += Mathf.Max(14f, _bankDetail.preferredHeight) + 2f;
            }

            return Mathf.Max(IdentityRailMinHeight, h);
        }

        /// <summary>
        /// Stretch host fills the tree. Identity rail and wheel pin top-left
        /// under the title row. Neither is in the VLG.
        /// </summary>
        void PinOverlays()
        {
            if (_pinning)
                return;
            _pinning = true;
            try
            {
                PreferredHeight = 0f;
                if (_rootLe != null)
                {
                    _rootLe.ignoreLayout = true;
                    _rootLe.minHeight = 0f;
                    _rootLe.preferredHeight = 0f;
                }

                if (_rootRt == null)
                    return;

                // --- Stretch host (no fill) ---
                _rootRt.anchorMin = Vector2.zero;
                _rootRt.anchorMax = Vector2.one;
                _rootRt.offsetMin = Vector2.zero;
                _rootRt.offsetMax = Vector2.zero;
                _rootRt.pivot = new Vector2(0.5f, 0.5f);

                float header = 8f;
                Transform headerTr = transform.parent != null ? transform.parent.Find("HeaderRow") : null;
                if (headerTr is RectTransform headerRt
                    && headerRt.gameObject.activeSelf
                    && headerRt.rect.height > 1f)
                    header = headerRt.rect.height + 4f;
                else
                    header = 0f;

                float identityH = 0f;
                if (_identityRt != null)
                {
                    identityH = MeasureIdentityRailHeight();
                    _identityRt.anchorMin = new Vector2(0f, 1f);
                    _identityRt.anchorMax = new Vector2(0f, 1f);
                    _identityRt.pivot = new Vector2(0f, 1f);
                    _identityRt.sizeDelta = new Vector2(IdentityRailWidth, identityH);
                    _identityRt.anchoredPosition = new Vector2(8f, -header);
                    if (!_identityRt.gameObject.activeSelf)
                        identityH = 0f;
                }

                if (_wheelPlateRt != null)
                {
                    float plateH = WheelSize + PadTop + PadBottom;
                    float plateW = WheelSize + PadX * 2f;
                    _wheelPlateRt.anchorMin = new Vector2(0f, 1f);
                    _wheelPlateRt.anchorMax = new Vector2(0f, 1f);
                    _wheelPlateRt.pivot = new Vector2(0f, 1f);
                    _wheelPlateRt.sizeDelta = new Vector2(plateW, plateH);
                    float tuck = identityH > 0f
                        ? Mathf.Min(WheelPlateTopTuck, Mathf.Max(0f, identityH - 8f))
                        : 0f;
                    _wheelPlateRt.anchoredPosition = new Vector2(
                        WheelPlatePadLeft,
                        -header - identityH + tuck);
                }

                // Only reorder when we are not already last — SetAsLastSibling
                // dirties the parent layout every call.
                if (transform.parent != null && transform.GetSiblingIndex() != transform.parent.childCount - 1)
                    transform.SetAsLastSibling();
            }
            finally
            {
                _pinning = false;
            }
        }

        /// <summary>
        /// Destroys leftover children from the previous overlay layout
        /// (script reload in Play Mode).
        /// </summary>
        void WipeOwnedChrome()
        {
            HideSpokeHover();
            _spokeLabels.Clear();
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i);
                if (child == null)
                    continue;
                DestroyImmediate(child.gameObject);
            }
        }

        void OnDisable()
        {
            HideSpokeHover();
        }

        /// <summary>TMP label with the tree font when we have one.</summary>
        TextMeshProUGUI CreateLabel(
            Transform parent,
            string name,
            string text,
            float size,
            Color color,
            FontStyles style)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.fontStyle = style;
            tmp.color = color;
            tmp.raycastTarget = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.enableWordWrapping = false;
            if (_font != null)
                tmp.font = _font;
            else if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
            return tmp;
        }

        /// <summary>
        /// Pointer hover on one spoke title. Forwards to the list so one shared
        /// chrome card can show the full name plus what that bonus changes.
        /// </summary>
        public sealed class SpokeHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            /// <summary>List that owns the shared tip.</summary>
            public ShipFamilyBonusListUI Owner;

            /// <summary>Full player-facing name (MOVE SPEED).</summary>
            public string FullTitle;

            /// <summary>
            /// Rich-text body: coloured lineage percent, then the sentence that
            /// explains what this spoke changes. Empty until the wheel paints.
            /// </summary>
            public string BodyCopy;

            /// <summary>Category stripe colour (move, combat, hull, energy, hold).</summary>
            public Color Accent;

            /// <summary>[UNITY] Pointer entered the short tag.</summary>
            public void OnPointerEnter(PointerEventData eventData)
            {
                if (Owner != null)
                    Owner.ShowSpokeHover(this);
            }

            /// <summary>[UNITY] Pointer left the short tag.</summary>
            public void OnPointerExit(PointerEventData eventData)
            {
                if (Owner != null)
                    Owner.HideSpokeHover();
            }
        }
    }
}
