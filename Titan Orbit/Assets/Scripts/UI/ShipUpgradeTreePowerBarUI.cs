using TitanOrbit.Data;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Ten-segment power bar grouped into five category pairs (no gap within a pair).
    /// Assign segment images in the inspector (pair order: offense … capacity).
    /// </summary>
    public class ShipUpgradeTreePowerBarUI : MonoBehaviour
    {
        [SerializeField] private Image[] segments = new Image[ShipAbilityCategoryColors.PowerBreakdownStatCount];
        [SerializeField] private float barHeight = 10f;
        [SerializeField] private float pairGap = 4f;

        public float TrackWidth { get; private set; }

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
            float total = breakdown.GetDisplayTotalForUi();
            bool hasData = total > 0.01f;
            float maxDen = Mathf.Max(strongestShipTotalPower, 0.001f);
            float nodeW = TrackWidth > 0.01f ? TrackWidth : 100f;
            float scaledBarHeight = barHeight * _heightScale;

            float sumSegW = 0f;
            for (int i = 0; i < ShipAbilityCategoryColors.PowerBreakdownStatCount; i++)
            {
                if (segments == null || i >= segments.Length || segments[i] == null)
                    continue;

                float val = breakdown.GetDisplayStatValue(i);
                Color statColor = ShipAbilityCategoryColors.GetPowerBreakdownStatColor(i);
                var seg = segments[i];
                var le = seg.GetComponent<LayoutElement>();

                float segW = 0f;
                if (hasData && val > 0.01f)
                {
                    segW = Mathf.Round(nodeW * val / maxDen);
                    if (segW < 1f)
                        segW = 1f;
                }
                else if (!hasData)
                    segW = Mathf.Round(Mathf.Max(1f, nodeW * 0.09f));

                if (le != null)
                {
                    le.preferredWidth = segW;
                    le.flexibleWidth = 0f;
                    le.minWidth = 0f;
                    le.preferredHeight = scaledBarHeight;
                    le.minHeight = scaledBarHeight;
                }

                seg.enabled = segW > 0.01f;
                if (segW > 0.01f)
                    sumSegW += segW;
                seg.color = hasData ? statColor : new Color(0.22f, 0.25f, 0.3f, 0.55f);
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
    }
}
