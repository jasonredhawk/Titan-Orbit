using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// One legend group: category title, two tone swatches, and stat names on a separate line.
    /// </summary>
    public class ShipUpgradeTreeLegendPairUI : MonoBehaviour
    {
        private const float RefTitleFontSize = 12f;
        private const float RefStatFontSize = 10f;
        private const float RefTitleHeight = 14f;
        private const float RefStatHeight = 14f;
        private const float RefSwatchSize = 10f;

        [SerializeField] private TextMeshProUGUI categoryTitle;
        [SerializeField] private Image toneASwatch;
        [SerializeField] private Image toneBSwatch;
        [SerializeField] private TextMeshProUGUI statLine;

        public void Configure(int pairIndex)
        {
            int statA = pairIndex * 2;
            int statB = statA + 1;
            if (categoryTitle != null)
            {
                categoryTitle.text = ShipAbilityCategoryColors.GetPowerBreakdownCategoryTitle(pairIndex);
                categoryTitle.color = ShipAbilityCategoryColors.PowerBreakdownOdEmc[pairIndex];
            }

            if (toneASwatch != null)
                toneASwatch.color = ShipAbilityCategoryColors.GetPowerBreakdownStatColor(statA);
            if (toneBSwatch != null)
                toneBSwatch.color = ShipAbilityCategoryColors.GetPowerBreakdownStatColor(statB);

            if (statLine != null)
            {
                statLine.text =
                    ShipAbilityCategoryColors.PowerBreakdownStatFullLabels[statA] +
                    " · " +
                    ShipAbilityCategoryColors.PowerBreakdownStatFullLabels[statB];
                statLine.color = new Color(0.78f, 0.84f, 0.92f, 1f);
            }
        }

        /// <summary>Moves stat text out of the cramped swatch row (legacy prefab fix).</summary>
        public void EnsureVerticalStatLayout()
        {
            if (statLine == null)
                return;

            var statParent = statLine.transform.parent;
            if (statParent == null || statParent.name != "SwatchRow")
                return;

            var pairRoot = statParent.parent;
            if (pairRoot == null)
                return;

            statLine.transform.SetParent(pairRoot, false);
            statLine.transform.SetSiblingIndex(statParent.GetSiblingIndex() + 1);
            statLine.enableWordWrapping = true;
            statLine.overflowMode = TextOverflowModes.Ellipsis;
            statLine.alignment = TextAlignmentOptions.TopLeft;

            var statLe = statLine.GetComponent<LayoutElement>();
            if (statLe == null)
                statLe = statLine.gameObject.AddComponent<LayoutElement>();
            statLe.flexibleWidth = 1f;
            statLe.preferredHeight = RefStatHeight;
            statLe.minHeight = 12f;
        }

        public void ApplyResponsiveScale(float scale)
        {
            scale = Mathf.Max(1f, scale);

            if (categoryTitle != null)
            {
                categoryTitle.fontSize = RefTitleFontSize * scale;
                categoryTitle.enableAutoSizing = false;
                var titleLe = categoryTitle.GetComponent<LayoutElement>();
                if (titleLe != null)
                {
                    titleLe.preferredHeight = RefTitleHeight * scale;
                    titleLe.minHeight = 12f * scale;
                }
            }

            if (statLine != null)
            {
                statLine.fontSize = RefStatFontSize * scale;
                statLine.enableAutoSizing = false;
                var statLe = statLine.GetComponent<LayoutElement>();
                if (statLe != null)
                {
                    statLe.preferredHeight = RefStatHeight * scale;
                    statLe.minHeight = 12f * scale;
                }
            }

            float swatch = RefSwatchSize * scale;
            ApplySwatchSize(toneASwatch, swatch);
            ApplySwatchSize(toneBSwatch, swatch);

            var pairLe = GetComponent<LayoutElement>();
            if (pairLe != null)
                pairLe.minWidth = 96f * scale;
        }

        private static void ApplySwatchSize(Image swatch, float size)
        {
            if (swatch == null)
                return;

            var le = swatch.GetComponent<LayoutElement>();
            if (le == null)
                le = swatch.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = size;
            le.preferredHeight = size;
            le.minWidth = size;
            le.minHeight = size;
        }
    }
}
