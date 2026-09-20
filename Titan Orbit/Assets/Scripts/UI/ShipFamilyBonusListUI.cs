using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Orbit Menu lineage matrix under the Ship Upgrade Tree title.
    /// Lists every <see cref="ShipFamilySpecialBonuses"/> field in four telemetry
    /// rows (MOBILITY / COMBAT / HULL / HOLD) plus a BANK row for the planet's
    /// bullet-type damage bonuses (shot damage and vs-asteroid / ship / moon / gem).
    /// <para>
    /// Presentation-only — no ECS reads. Built at dock time, not per frame.
    /// Dark void glass + thin ice-blue accent (space gamer theme).
    /// Paired with <see cref="ShipUpgradeTreeUI.ApplyFamilyIdentity"/>.
    /// </para>
    /// </summary>
    public class ShipFamilyBonusListUI : MonoBehaviour
    {
        /// <summary>Caption the player reads on the matrix rail.</summary>
        public const string CaptionText = "FAMILY BONUSES";

        /// <summary>Empty-family / missing definition copy.</summary>
        public const string NoFamilyText = "NO LINEAGE LOCKED";

        const int CategoryCount = 5;
        const float AccentHeight = 3f;
        const float CaptionHeight = 18f;
        const float RowHeight = 24f;
        const float PadX = 8f;
        const float PadTop = 6f;
        const float PadBottom = 6f;
        const float RowGap = 2f;
        const float CategoryLabelWidth = 78f;
        const float CellMinWidth = 88f;
        const float CellGap = 4f;

        static readonly Color FillColor = new Color(0.012f, 0.016f, 0.028f, 1f);
        static readonly Color CaptionColor = new Color(0.62f, 0.78f, 0.95f, 0.95f);
        static readonly Color BodyColor = new Color(0.88f, 0.92f, 0.98f, 1f);
        static readonly Color AccentColor = new Color(0.35f, 0.72f, 0.95f, 0.95f);
        static readonly Color CellFill = new Color(0.018f, 0.028f, 0.045f, 1f);
        static readonly Color IdentityValue = new Color(0.36f, 0.48f, 0.58f, 0.85f);
        static readonly Color BoostValue = new Color(0.35f, 0.98f, 0.62f, 1f);
        static readonly Color PenaltyValue = new Color(0.95f, 0.72f, 0.32f, 1f);
        static readonly Color NeutralValue = new Color(0.62f, 0.78f, 0.95f, 1f);

        /// <summary>Reusable row buffer — dock / tab refresh only, not the sim tick.</summary>
        readonly List<FamilyStatHudCopy.BonusRow> _rows = new List<FamilyStatHudCopy.BonusRow>(20);

        /// <summary>One chip pool per category row so we do not Instantiate on every refresh.</summary>
        readonly List<CellView>[] _cellsByCategory = new List<CellView>[CategoryCount];

        RectTransform _rootRt;
        LayoutElement _rootLe;
        TextMeshProUGUI _caption;
        TextMeshProUGUI _bankCaption;
        GameObject _rowsHost;
        TextMeshProUGUI _emptyLabel;
        CategoryRowView[] _categoryRows;
        TMP_FontAsset _font;
        bool _built;

        /// <summary>
        /// Layout height after the last <see cref="Paint"/>. The tree subtracts this
        /// from the node canvas so the matrix does not cover hull cards.
        /// </summary>
        public float PreferredHeight { get; private set; }

        /// <summary>
        /// One telemetry cell: short tag + signed percent (or a baseline mid-dot).
        /// </summary>
        struct CellView
        {
            public GameObject Root;
            public TextMeshProUGUI Label;
            public TextMeshProUGUI Value;
            public Image Fill;
        }

        /// <summary>One MOBILITY / COMBAT / HULL / HOLD strip.</summary>
        struct CategoryRowView
        {
            public GameObject Root;
            public TextMeshProUGUI Caption;
            public RectTransform ChipHost;
        }

        /// <summary>
        /// Builds the chrome under <paramref name="parent"/> when this component
        /// was added at runtime (older ShipUpgradeTree prefabs have no child).
        /// Called from <see cref="ShipUpgradeTreeUI.EnsurePanelHeader"/>.
        /// </summary>
        /// <param name="parent">Tree root (vertical layout).</param>
        /// <param name="siblingAfter">Header row — the matrix sits just below it.</param>
        /// <param name="font">Same TMP face as the tree title when available.</param>
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
        /// Writes every family bonus plus the docked bullet type's damage board.
        /// Identity families still show the full family matrix (baseline mid-dots);
        /// the BANK row lists shot damage and vs-target muls for Fireballs, Rockets, …
        /// </summary>
        /// <param name="family">Docked planet family. Null hides the matrix.</param>
        /// <param name="shipLevel">Hull level for Extra-Level scaling on bank damage.</param>
        /// <param name="planetOrHullBankIndex">Rolled planet bank, or −1 for the family default.</param>
        public void Paint(
            ShipFamilyDefinition family,
            int shipLevel = 1,
            int planetOrHullBankIndex = -1)
        {
            // --- Ensure chrome ---
            EnsureBuilt();
            if (_rootRt == null)
                return;

            if (family == null)
            {
                // No docked lineage yet (join warmup / missing catalog).
                SetEmpty(NoFamilyText);
                ApplyHeight();
                return;
            }

            // --- Family fields, including 1× ---
            _rows.Clear();
            FamilyStatHudCopy.CollectBonusRows(family.specialBonuses, _rows, includeIdentity: true);

            // --- Bullet-type damage (BANK row) ---
            // [TITAN-ORBIT] Planet roll wins over the family Laserbolt fallback.
            // Store preview uses hull Extra Levels only — no Fire Power purchases.
            string typeName = BulletBankHudCopy.FormatFamilyTypeName(family, planetOrHullBankIndex);
            BulletBankProfile profile = ResolveBankProfile(family, planetOrHullBankIndex);
            int extras = BulletBankCombatLogic.CountFirePowerExtraLevels(Mathf.Max(1, shipLevel), 0);
            FamilyStatHudCopy.CollectBankDamageRows(profile, extras, _rows, includeIdentity: true);

            if (_emptyLabel != null)
                _emptyLabel.gameObject.SetActive(false);
            if (_rowsHost != null)
                _rowsHost.SetActive(true);

            if (_caption != null)
                _caption.text = CaptionText;
            if (_bankCaption != null)
            {
                _bankCaption.text = string.IsNullOrEmpty(typeName)
                    ? string.Empty
                    : typeName.ToUpperInvariant();
                _bankCaption.gameObject.SetActive(!string.IsNullOrEmpty(typeName));
            }

            // --- Paint family strips + BANK ---
            for (int c = 0; c < CategoryCount; c++)
                PaintCategory((FamilyStatHudCopy.BonusCategory)c);

            ApplyHeight();
        }

        /// <summary>
        /// Resolves the ScriptableObject profile for the planet / family bank.
        /// Null when the combat bank catalog is not loaded yet.
        /// </summary>
        static BulletBankProfile ResolveBankProfile(ShipFamilyDefinition family, int planetOrHullBankIndex)
        {
            int idx = BulletBankProfileUtility.ResolveBankIndexForFamily(family, planetOrHullBankIndex);
            var bank = BulletBankCombatLogic.Bank;
            if (bank == null || !bank.TryGetProfile(idx, out BulletBankProfile profile))
                return null;
            return profile;
        }

        /// <summary>
        /// Creates fill, accent, caption, and four category rows once.
        /// Safe to call again — later calls no-op.
        /// </summary>
        public void EnsureBuilt()
        {
            if (_built)
                return;
            _built = true;

            _rootRt = transform as RectTransform;
            if (_rootRt == null)
                _rootRt = gameObject.AddComponent<RectTransform>();

            // --- Dark void glass ---
            // [TITAN-ORBIT] Same fill as ShipStatTooltipChrome — not a light card.
            var fill = gameObject.GetComponent<Image>();
            if (fill == null)
                fill = gameObject.AddComponent<Image>();
            fill.color = FillColor;
            fill.raycastTarget = false;

            var vlg = gameObject.GetComponent<VerticalLayoutGroup>();
            if (vlg == null)
                vlg = gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset((int)PadX, (int)PadX, (int)PadTop, (int)PadBottom);
            vlg.spacing = RowGap;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            _rootLe = gameObject.GetComponent<LayoutElement>();
            if (_rootLe == null)
                _rootLe = gameObject.AddComponent<LayoutElement>();
            _rootLe.flexibleHeight = 0f;
            _rootLe.flexibleWidth = 1f;

            // Accent stripe — only bright colour on the plate.
            var accentGo = new GameObject("Accent", typeof(RectTransform));
            accentGo.transform.SetParent(transform, false);
            var accent = accentGo.AddComponent<Image>();
            accent.color = AccentColor;
            accent.raycastTarget = false;
            var accentLe = accentGo.AddComponent<LayoutElement>();
            accentLe.minHeight = AccentHeight;
            accentLe.preferredHeight = AccentHeight;
            accentLe.flexibleHeight = 0f;

            // Caption row: FAMILY BONUSES on the left, ROCKETS / FIREBALLS on the right.
            var captionRow = new GameObject("CaptionRow", typeof(RectTransform));
            captionRow.transform.SetParent(transform, false);
            var captionHlg = captionRow.AddComponent<HorizontalLayoutGroup>();
            captionHlg.childAlignment = TextAnchor.MiddleLeft;
            captionHlg.childControlWidth = true;
            captionHlg.childControlHeight = true;
            captionHlg.childForceExpandWidth = true;
            captionHlg.childForceExpandHeight = false;
            captionHlg.spacing = 8f;
            var captionRowLe = captionRow.AddComponent<LayoutElement>();
            captionRowLe.minHeight = CaptionHeight;
            captionRowLe.preferredHeight = CaptionHeight;
            captionRowLe.flexibleHeight = 0f;

            _caption = CreateLabel(captionRow.transform, "Caption", CaptionText, 11f, CaptionColor, FontStyles.Bold);
            _caption.characterSpacing = 2.2f;
            _caption.alignment = TextAlignmentOptions.MidlineLeft;
            var capLe = _caption.gameObject.AddComponent<LayoutElement>();
            capLe.flexibleWidth = 1f;
            capLe.minHeight = CaptionHeight;
            capLe.preferredHeight = CaptionHeight;

            _bankCaption = CreateLabel(captionRow.transform, "BankType", string.Empty, 11f, NeutralValue, FontStyles.Bold);
            _bankCaption.characterSpacing = 1.6f;
            _bankCaption.alignment = TextAlignmentOptions.MidlineRight;
            _bankCaption.enableWordWrapping = false;
            var bankCapLe = _bankCaption.gameObject.AddComponent<LayoutElement>();
            bankCapLe.flexibleWidth = 0.7f;
            bankCapLe.minWidth = 80f;
            bankCapLe.minHeight = CaptionHeight;
            bankCapLe.preferredHeight = CaptionHeight;
            _bankCaption.gameObject.SetActive(false);

            _rowsHost = new GameObject("CategoryRows", typeof(RectTransform));
            _rowsHost.transform.SetParent(transform, false);
            var rowsVlg = _rowsHost.AddComponent<VerticalLayoutGroup>();
            rowsVlg.spacing = RowGap;
            rowsVlg.childAlignment = TextAnchor.UpperLeft;
            rowsVlg.childControlWidth = true;
            rowsVlg.childControlHeight = true;
            rowsVlg.childForceExpandWidth = true;
            rowsVlg.childForceExpandHeight = false;
            var rowsFitter = _rowsHost.AddComponent<ContentSizeFitter>();
            rowsFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            rowsFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            _categoryRows = new CategoryRowView[CategoryCount];
            for (int i = 0; i < CategoryCount; i++)
            {
                _cellsByCategory[i] = new List<CellView>(5);
                _categoryRows[i] = CreateCategoryRow(
                    _rowsHost.transform,
                    FamilyStatHudCopy.GetCategoryCaption((FamilyStatHudCopy.BonusCategory)i));
            }

            _emptyLabel = CreateLabel(transform, "Empty", NoFamilyText, 11f, IdentityValue, FontStyles.Normal);
            _emptyLabel.alignment = TextAlignmentOptions.MidlineLeft;
            var emptyLe = _emptyLabel.gameObject.AddComponent<LayoutElement>();
            emptyLe.minHeight = RowHeight;
            emptyLe.preferredHeight = RowHeight;
            _emptyLabel.gameObject.SetActive(false);

            ApplyHeight();
        }

        /// <summary>Shows the empty caption and hides the four category strips.</summary>
        void SetEmpty(string copy)
        {
            if (_caption != null)
                _caption.text = CaptionText;
            if (_rowsHost != null)
                _rowsHost.SetActive(false);
            if (_bankCaption != null)
            {
                _bankCaption.text = string.Empty;
                _bankCaption.gameObject.SetActive(false);
            }

            if (_emptyLabel != null)
            {
                _emptyLabel.gameObject.SetActive(true);
                _emptyLabel.text = copy;
            }
        }

        /// <summary>
        /// Fills one category strip from <see cref="_rows"/>. Extra pooled chips hide.
        /// </summary>
        void PaintCategory(FamilyStatHudCopy.BonusCategory category)
        {
            int cat = (int)category;
            if (cat < 0 || cat >= CategoryCount)
                return;

            CategoryRowView rowView = _categoryRows[cat];
            if (rowView.Caption != null)
                rowView.Caption.text = FamilyStatHudCopy.GetCategoryCaption(category);

            List<CellView> pool = _cellsByCategory[cat];
            int written = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Category != category)
                    continue;

                CellView cell = written < pool.Count
                    ? pool[written]
                    : CreateCell(rowView.ChipHost);
                if (written >= pool.Count)
                    pool.Add(cell);

                ApplyCell(cell, _rows[i]);
                if (cell.Root != null)
                    cell.Root.SetActive(true);
                written++;
            }

            // Hide leftover chips from a previous family with more live fields.
            for (int i = written; i < pool.Count; i++)
            {
                if (pool[i].Root != null)
                    pool[i].Root.SetActive(false);
            }
        }

        /// <summary>
        /// Writes label / value / tint on one chip. Baseline cells stay dim so
        /// live +12% / −25% trade-offs pop on the dark plate.
        /// </summary>
        void ApplyCell(in CellView cell, in FamilyStatHudCopy.BonusRow row)
        {
            if (cell.Label != null)
                cell.Label.text = row.ShortLabel;
            if (cell.Value != null)
            {
                cell.Value.text = FamilyStatHudCopy.FormatSignedPercent(row);
                cell.Value.color = ColorForRow(row);
            }

            if (cell.Fill != null)
            {
                // Live trade-offs get a slightly brighter well; baseline stays void.
                cell.Fill.color = row.IsIdentity
                    ? CellFill
                    : new Color(0.024f, 0.038f, 0.058f, 1f);
            }
        }

        /// <summary>Boost green, penalty amber, camera ice, baseline muted.</summary>
        static Color ColorForRow(in FamilyStatHudCopy.BonusRow row)
        {
            if (row.IsIdentity)
                return IdentityValue;
            if (row.IsNeutral)
                return NeutralValue;
            if (row.IsBoost)
                return BoostValue;
            if (row.IsPenalty)
                return PenaltyValue;
            return NeutralValue;
        }

        /// <summary>
        /// Category caption on the left, chip strip on the right.
        /// [UNITY] HorizontalLayoutGroup — chips size from preferred width.
        /// </summary>
        CategoryRowView CreateCategoryRow(Transform parent, string caption)
        {
            var go = new GameObject("Row_" + caption, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var hlg = go.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 6f;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            var rowLe = go.AddComponent<LayoutElement>();
            rowLe.minHeight = RowHeight;
            rowLe.preferredHeight = RowHeight;
            rowLe.flexibleHeight = 0f;

            var cap = CreateLabel(go.transform, "Cat", caption, 9.5f, CaptionColor, FontStyles.Bold);
            cap.characterSpacing = 1.1f;
            cap.alignment = TextAlignmentOptions.MidlineLeft;
            cap.enableWordWrapping = false;
            var capLe = cap.gameObject.AddComponent<LayoutElement>();
            capLe.minWidth = CategoryLabelWidth;
            capLe.preferredWidth = CategoryLabelWidth;
            capLe.flexibleWidth = 0f;

            var chipGo = new GameObject("Chips", typeof(RectTransform));
            chipGo.transform.SetParent(go.transform, false);
            var chipHlg = chipGo.AddComponent<HorizontalLayoutGroup>();
            chipHlg.spacing = CellGap;
            chipHlg.childAlignment = TextAnchor.MiddleLeft;
            chipHlg.childControlWidth = true;
            chipHlg.childControlHeight = true;
            chipHlg.childForceExpandWidth = false;
            chipHlg.childForceExpandHeight = true;
            var chipLe = chipGo.AddComponent<LayoutElement>();
            chipLe.flexibleWidth = 1f;

            return new CategoryRowView
            {
                Root = go,
                Caption = cap,
                ChipHost = chipGo.GetComponent<RectTransform>()
            };
        }

        /// <summary>
        /// One compact tag + value chip. Pooled per category — created the first
        /// time that slot is needed, then reused.
        /// </summary>
        CellView CreateCell(Transform parent)
        {
            var go = new GameObject("Cell", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var fill = go.AddComponent<Image>();
            fill.color = CellFill;
            fill.raycastTarget = false;
            var hlg = go.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(6, 6, 2, 2);
            hlg.spacing = 4f;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = CellMinWidth;
            le.preferredWidth = CellMinWidth;
            le.minHeight = RowHeight - 2f;
            le.preferredHeight = RowHeight - 2f;
            le.flexibleWidth = 0f;

            var label = CreateLabel(go.transform, "Tag", "MOVE", 9f, CaptionColor, FontStyles.Bold);
            label.enableWordWrapping = false;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            var labelLe = label.gameObject.AddComponent<LayoutElement>();
            labelLe.flexibleWidth = 0f;

            var value = CreateLabel(go.transform, "Val", "·", 10.5f, IdentityValue, FontStyles.Bold);
            value.enableWordWrapping = false;
            value.alignment = TextAlignmentOptions.MidlineRight;
            var valueLe = value.gameObject.AddComponent<LayoutElement>();
            valueLe.minWidth = 28f;
            valueLe.flexibleWidth = 1f;

            return new CellView
            {
                Root = go,
                Label = label,
                Value = value,
                Fill = fill
            };
        }

        /// <summary>
        /// Pushes the vertical layout height into <see cref="LayoutElement"/> so
        /// the tree's VerticalLayoutGroup reserves space before node geometry.
        /// </summary>
        void ApplyHeight()
        {
            float h = PadTop + AccentHeight + CaptionHeight + PadBottom + RowGap * 2f;
            if (_emptyLabel != null && _emptyLabel.gameObject.activeSelf)
                h += RowHeight;
            else
                h += CategoryCount * RowHeight + (CategoryCount - 1) * RowGap;

            PreferredHeight = h;
            if (_rootLe != null)
            {
                _rootLe.minHeight = h;
                _rootLe.preferredHeight = h;
            }

            if (_rootRt != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(_rootRt);
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
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            if (_font != null)
                tmp.font = _font;
            else if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
            return tmp;
        }
    }
}
