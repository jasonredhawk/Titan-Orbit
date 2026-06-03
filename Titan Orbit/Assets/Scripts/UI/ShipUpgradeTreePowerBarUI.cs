using TitanOrbit.Data;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Ten-segment power bar grouped into five category pairs (no gap within a pair).
    /// Each segment width is proportional to its stat value; category pairs share only a visual grouping gap.
    /// Assign segment images in the inspector (pair order: offense … capacity).
    /// </summary>
    public class ShipUpgradeTreePowerBarUI : MonoBehaviour
    {
        /// <summary>
        /// Gem Cap (8) and People Cap (9) bar widths use this fraction of raw stat power
        /// so high gem capacity does not dominate the moon-menu upgrade tree bar (0.5 = 50% smaller).
        /// </summary>
        public const float MoonTreeCapacityStatBarScale = 0.5f;

        [SerializeField] private Image[] segments = new Image[ShipAbilityCategoryColors.PowerBreakdownStatCount];
        [SerializeField] private float barHeight = 10f;
        [SerializeField] private float pairGap = 4f;

        public float TrackWidth { get; private set; }

        public static float GetMoonTreeBarStatValue(ShipFamilyPowerScoreBreakdown breakdown, int statIndex)
        {
            float value = breakdown.GetDisplayStatValue(statIndex);
            if (statIndex == 8 || statIndex == 9)
                return value * MoonTreeCapacityStatBarScale;
            return value;
        }

        public static float GetMoonTreeBarDisplayTotal(ShipFamilyPowerScoreBreakdown breakdown)
        {
            float total = 0f;
            for (int i = 0; i < ShipFamilyPowerScoreBreakdown.DisplayStatCount; i++)
                total += GetMoonTreeBarStatValue(breakdown, i);
            return total;
        }

        private float _widthScale = 1f;
        private float _heightScale = 1f;

        public void ConfigureLayoutScale(float widthScale, float heightScale)
        {
            _widthScale = Mathf.Max(0.01f, widthScale);
            _heightScale = Mathf.Max(0.01f, heightScale);

            var hlg = GetComponent<HorizontalLayoutGroup>();
            if (hlg != null)
                hlg.spacing = pairGap * _widthScale;
        }

        public void ApplyBreakdown(ShipFamilyPowerScoreBreakdown breakdown, float strongestShipTotalPower, float trackWidth)
        {
            TrackWidth = Mathf.Max(0f, trackWidth);
            float total = GetMoonTreeBarDisplayTotal(breakdown);
            bool hasData = total > 0.01f;
            float maxDen = Mathf.Max(strongestShipTotalPower, 0.001f);
            float nodeW = TrackWidth > 0.01f ? TrackWidth : 100f;
            float scaledBarHeight = barHeight * _heightScale;
            float barFillW = hasData ? nodeW * total / maxDen : nodeW;

            int pairCount = ShipAbilityCategoryColors.PowerBreakdownPairCount;
            var barHlg = GetComponent<HorizontalLayoutGroup>();
            float gap = barHlg != null ? barHlg.spacing : pairGap * _widthScale;
            float totalGap = gap * Mathf.Max(0, pairCount - 1);
            float usableW = Mathf.Max(0f, barFillW - totalGap);

            for (int pair = 0; pair < pairCount; pair++)
            {
                int statA = pair * 2;
                int statB = statA + 1;
                float valA = GetMoonTreeBarStatValue(breakdown, statA);
                float valB = GetMoonTreeBarStatValue(breakdown, statB);
                float pairSum = valA + valB;

                float pairWidth;
                float segWA;
                float segWB;
                if (hasData && total > 0.01f && pairSum > 0.01f)
                {
                    pairWidth = usableW * pairSum / total;
                    segWA = pairWidth * valA / pairSum;
                    segWB = pairWidth * valB / pairSum;
                }
                else if (!hasData)
                {
                    pairWidth = usableW / pairCount;
                    segWA = segWB = pairWidth * 0.5f;
                }
                else
                {
                    pairWidth = 0f;
                    segWA = segWB = 0f;
                }

                ApplySegmentWidth(statA, segWA, hasData, scaledBarHeight);
                ApplySegmentWidth(statB, segWB, hasData, scaledBarHeight);
                ApplyPairWidth(statA, pairWidth, scaledBarHeight);
            }

            var barRow = GetComponent<RectTransform>();
            if (barRow != null)
            {
                var barLe = GetComponent<LayoutElement>();
                if (barLe != null)
                {
                    barLe.preferredWidth = Mathf.Round(nodeW);
                    barLe.flexibleWidth = 1f;
                    barLe.minWidth = Mathf.Round(nodeW);
                    barLe.preferredHeight = scaledBarHeight;
                    barLe.minHeight = scaledBarHeight;
                }

                LayoutRebuilder.ForceRebuildLayoutImmediate(barRow);
            }
        }

        private void ApplyPairWidth(int statIndex, float pairWidth, float scaledBarHeight)
        {
            if (segments == null || statIndex < 0 || statIndex >= segments.Length || segments[statIndex] == null)
                return;

            Transform pairTransform = segments[statIndex].transform.parent;
            if (pairTransform == null)
                return;

            var pairLe = pairTransform.GetComponent<LayoutElement>();
            if (pairLe == null)
                return;

            float roundedPairWidth = Mathf.Round(pairWidth);
            pairLe.preferredWidth = roundedPairWidth;
            pairLe.flexibleWidth = 0f;
            pairLe.minWidth = 0f;
            pairLe.preferredHeight = scaledBarHeight;
            pairLe.minHeight = scaledBarHeight;
        }

        private void ApplySegmentWidth(int statIndex, float segW, bool hasData, float scaledBarHeight)
        {
            if (segments == null || statIndex < 0 || statIndex >= segments.Length || segments[statIndex] == null)
                return;

            Color statColor = ShipAbilityCategoryColors.GetPowerBreakdownStatColor(statIndex);
            var seg = segments[statIndex];
            var le = seg.GetComponent<LayoutElement>();

            if (le != null)
            {
                le.preferredWidth = segW > 0.01f ? Mathf.Max(1f, Mathf.Round(segW)) : 0f;
                le.flexibleWidth = 0f;
                le.minWidth = 0f;
                le.preferredHeight = scaledBarHeight;
                le.minHeight = scaledBarHeight;
            }

            seg.enabled = segW > 0.01f;
            seg.color = hasData ? statColor : new Color(0.22f, 0.25f, 0.3f, 0.55f);
        }
    }
}
