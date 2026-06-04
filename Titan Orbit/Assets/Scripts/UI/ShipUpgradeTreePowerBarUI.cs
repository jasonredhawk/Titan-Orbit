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

        public void Initialize(Image[] segmentImages, float height, float gap)
        {
            segments = segmentImages;
            barHeight = height;
            pairGap = gap;
        }

        /// <summary>Builds the same ten-segment bar used on ship upgrade tree nodes (for runtime UI).</summary>
        public static ShipUpgradeTreePowerBarUI Create(
            Transform parent,
            float barHeight = 10f,
            float pairGap = 4f,
            float minTrackWidth = 48f)
        {
            return BuildBar(parent, barHeight, pairGap, minTrackWidth, null, 0f);
        }

        /// <summary>Power bar on a dark full-width track (matches upgrade-tree node readability on tinted cards).</summary>
        public static ShipUpgradeTreePowerBarUI CreateInTrack(
            Transform parent,
            Color trackBackground,
            float barHeight = 10f,
            float pairGap = 4f,
            float trackWidth = 48f)
        {
            return BuildBar(parent, barHeight, pairGap, trackWidth, trackBackground, 2f);
        }

        private static ShipUpgradeTreePowerBarUI BuildBar(
            Transform parent,
            float barHeight,
            float pairGap,
            float trackWidth,
            Color? trackBackground,
            float trackVerticalPadding)
        {
            Transform barParent = parent;
            if (trackBackground.HasValue)
            {
                var trackGo = new GameObject("PowerBarTrack");
                trackGo.transform.SetParent(parent, false);
                var trackLe = trackGo.AddComponent<LayoutElement>();
                trackLe.flexibleHeight = 0f;
                trackLe.flexibleWidth = 1f;
                trackLe.minWidth = trackWidth;
                trackLe.preferredWidth = trackWidth;
                trackLe.preferredHeight = barHeight + trackVerticalPadding * 2f;
                trackLe.minHeight = trackLe.preferredHeight;
                var trackBg = trackGo.AddComponent<Image>();
                trackBg.color = trackBackground.Value;
                trackBg.raycastTarget = false;
                var trackVlg = trackGo.AddComponent<VerticalLayoutGroup>();
                trackVlg.padding = new RectOffset(0, 0, (int)trackVerticalPadding, (int)trackVerticalPadding);
                trackVlg.spacing = 0f;
                trackVlg.childAlignment = TextAnchor.MiddleCenter;
                trackVlg.childControlWidth = true;
                trackVlg.childControlHeight = true;
                trackVlg.childForceExpandWidth = true;
                trackVlg.childForceExpandHeight = false;
                barParent = trackGo.transform;
            }

            var barRow = new GameObject("PowerBar");
            barRow.transform.SetParent(barParent, false);
            var barLe = barRow.AddComponent<LayoutElement>();
            barLe.preferredHeight = barHeight;
            barLe.minHeight = barHeight;
            barLe.flexibleHeight = 0f;
            barLe.flexibleWidth = 1f;
            barLe.minWidth = trackWidth;
            if (!trackBackground.HasValue)
                barLe.preferredWidth = trackWidth;

            var barHlg = barRow.AddComponent<HorizontalLayoutGroup>();
            barHlg.spacing = pairGap;
            barHlg.childAlignment = TextAnchor.MiddleLeft;
            barHlg.childControlWidth = true;
            barHlg.childControlHeight = true;
            barHlg.childForceExpandWidth = false;
            barHlg.childForceExpandHeight = true;

            var segments = new Image[ShipAbilityCategoryColors.PowerBreakdownStatCount];
            for (int pair = 0; pair < ShipAbilityCategoryColors.PowerBreakdownPairCount; pair++)
            {
                var pairGo = new GameObject("Pair_" + pair);
                pairGo.transform.SetParent(barRow.transform, false);
                var pairHlg = pairGo.AddComponent<HorizontalLayoutGroup>();
                pairHlg.spacing = 0f;
                pairHlg.childAlignment = TextAnchor.MiddleLeft;
                pairHlg.childControlWidth = true;
                pairHlg.childControlHeight = true;
                pairHlg.childForceExpandWidth = false;
                pairHlg.childForceExpandHeight = true;
                var pairLe = pairGo.AddComponent<LayoutElement>();
                pairLe.flexibleWidth = 0f;

                for (int tone = 0; tone < 2; tone++)
                {
                    int idx = pair * 2 + tone;
                    var segGo = new GameObject("Seg_" + idx);
                    segGo.transform.SetParent(pairGo.transform, false);
                    var segImg = segGo.AddComponent<Image>();
                    segImg.color = ShipAbilityCategoryColors.GetPowerBreakdownStatColor(idx);
                    segImg.raycastTarget = false;
                    var segLe = segGo.AddComponent<LayoutElement>();
                    segLe.flexibleWidth = 0f;
                    segLe.minWidth = 0f;
                    segLe.preferredHeight = barHeight;
                    segments[idx] = segImg;
                }
            }

            var powerBar = barRow.AddComponent<ShipUpgradeTreePowerBarUI>();
            powerBar.Initialize(segments, barHeight, pairGap);
            return powerBar;
        }

        public static float GetMoonTreeBarStatValue(ShipFamilyPowerScoreBreakdown breakdown, int statIndex)
        {
            float value = breakdown.GetDisplayStatValue(statIndex);
            if (statIndex == 8 || statIndex == 9)
                return value * MoonTreeCapacityStatBarScale;
            return value;
        }

        /// <summary>
        /// Equipment cards include fire rate and ramming in the offense pair segments
        /// (fire rate with bullet speed, ramming with fire power).
        /// </summary>
        public static float GetEquipmentBarStatValue(ShipFamilyPowerScoreBreakdown breakdown, int statIndex)
        {
            switch (statIndex)
            {
                case 0: return breakdown.firePower + breakdown.rammingPower;
                case 1: return breakdown.bulletSpeed + breakdown.fireRate;
                default: return GetMoonTreeBarStatValue(breakdown, statIndex);
            }
        }

        public static float GetEquipmentBarDisplayTotal(ShipFamilyPowerScoreBreakdown breakdown)
        {
            float total = 0f;
            for (int i = 0; i < ShipFamilyPowerScoreBreakdown.DisplayStatCount; i++)
                total += GetEquipmentBarStatValue(breakdown, i);
            return total;
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
            ApplyBreakdownInternal(breakdown, strongestShipTotalPower, trackWidth, equipmentLayout: false);
        }

        /// <summary>
        /// Equipment cards usually contribute one or two stats. Hide empty category pairs and
        /// scale the active segments across the full track width for readability.
        /// </summary>
        public void ApplyEquipmentBreakdown(ShipFamilyPowerScoreBreakdown breakdown, float strongestComponentTotalPower, float trackWidth)
        {
            ApplyBreakdownInternal(breakdown, strongestComponentTotalPower, trackWidth, equipmentLayout: true);
        }

        private void ApplyBreakdownInternal(
            ShipFamilyPowerScoreBreakdown breakdown,
            float strongestTotalPower,
            float trackWidth,
            bool equipmentLayout)
        {
            TrackWidth = Mathf.Max(0f, trackWidth);
            float total = equipmentLayout
                ? GetEquipmentBarDisplayTotal(breakdown)
                : GetMoonTreeBarDisplayTotal(breakdown);
            bool hasData = total > 0.01f;
            float maxDen = Mathf.Max(strongestTotalPower, 0.001f);
            float nodeW = TrackWidth > 0.01f ? TrackWidth : 100f;
            float scaledBarHeight = barHeight * _heightScale;
            float barFillW = hasData ? nodeW * total / maxDen : nodeW;

            int pairCount = ShipAbilityCategoryColors.PowerBreakdownPairCount;
            var barHlg = GetComponent<HorizontalLayoutGroup>();
            float gap = barHlg != null ? barHlg.spacing : pairGap * _widthScale;

            float activePairSum = 0f;
            int activePairCount = 0;
            if (equipmentLayout && hasData)
            {
                for (int pair = 0; pair < pairCount; pair++)
                {
                    float pairSum = GetEquipmentBarStatValue(breakdown, pair * 2) +
                                    GetEquipmentBarStatValue(breakdown, pair * 2 + 1);
                    if (pairSum > 0.01f)
                    {
                        activePairSum += pairSum;
                        activePairCount++;
                    }
                }
            }

            if (equipmentLayout && hasData && activePairCount > 0)
                barFillW = nodeW * activePairSum / maxDen;

            float totalGap = equipmentLayout && hasData
                ? gap * Mathf.Max(0, activePairCount - 1)
                : gap * Mathf.Max(0, pairCount - 1);
            float usableW = Mathf.Max(0f, barFillW - totalGap);
            float widthDenominator = equipmentLayout && hasData && activePairSum > 0.01f
                ? activePairSum
                : total;

            for (int pair = 0; pair < pairCount; pair++)
            {
                int statA = pair * 2;
                int statB = statA + 1;
                float valA = equipmentLayout
                    ? GetEquipmentBarStatValue(breakdown, statA)
                    : GetMoonTreeBarStatValue(breakdown, statA);
                float valB = equipmentLayout
                    ? GetEquipmentBarStatValue(breakdown, statB)
                    : GetMoonTreeBarStatValue(breakdown, statB);
                float pairSum = valA + valB;
                bool pairActive = pairSum > 0.01f || (!equipmentLayout && !hasData);

                float pairWidth;
                float segWA;
                float segWB;
                if (hasData && widthDenominator > 0.01f && pairActive && pairSum > 0.01f)
                {
                    pairWidth = usableW * pairSum / widthDenominator;
                    segWA = pairWidth * valA / pairSum;
                    segWB = pairWidth * valB / pairSum;
                }
                else if (!hasData && !equipmentLayout)
                {
                    pairWidth = usableW / pairCount;
                    segWA = segWB = pairWidth * 0.5f;
                }
                else
                {
                    pairWidth = 0f;
                    segWA = segWB = 0f;
                }

                ApplySegmentWidth(statA, segWA, hasData && pairActive, scaledBarHeight);
                ApplySegmentWidth(statB, segWB, hasData && pairActive, scaledBarHeight);
                ApplyPairWidth(statA, pairWidth, scaledBarHeight);

                if (equipmentLayout && segments != null && statA < segments.Length && segments[statA] != null)
                {
                    Transform pairTransform = segments[statA].transform.parent;
                    if (pairTransform != null)
                        pairTransform.gameObject.SetActive(pairActive || !hasData);
                }
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
