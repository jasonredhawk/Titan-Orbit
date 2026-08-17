using TitanOrbit.Data;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Ten-segment power bar grouped into five ODEMC pairs (Offense … Capacity).
    /// Moon upgrade-tree cards use equal slots: each pair is 20% of the track, each
    /// ability 10%. That 10% slot is 100% of the global catalog max for that stat;
    /// the ship's value is a solid fill and the rest is a dimmed matching color.
    /// Equipment cards keep the older proportional widths (one or two stats, hide empty pairs).
    /// Built at runtime by <see cref="Create"/> / <see cref="CreateInTrack"/>, or upgraded
    /// in place from the serialized node prefab.
    /// </summary>
    public class ShipUpgradeTreePowerBarUI : MonoBehaviour
    {
        /// <summary>
        /// Gem Cap (8) and People Cap (9) bar widths use this fraction of raw stat power
        /// on equipment cards only, so high gem capacity does not dominate those bars.
        /// Moon-tree equal slots no longer need this — each stat has its own 10% lane.
        /// </summary>
        public const float MoonTreeCapacityStatBarScale = 0.5f;

        /// <summary>Live empty-track tint: keep the stat hue, but lift it so the lane stays readable.</summary>
        const float DimRgbScale = 0.62f;
        const float DimWhiteMix = 0.22f;
        const float DimAlpha = 0.88f;
        static readonly Color DisabledSlotFill = new Color(0.13f, 0.15f, 0.19f, 0.7f);
        static readonly Color DisabledSlotTrack = new Color(0.1f, 0.12f, 0.16f, 0.55f);
        /// <summary>
        /// Pixels between the two abilities in a pair (Fire Power | Bullet Speed).
        /// Smaller than <see cref="MoonTreePairGapPx"/> so ODEMC groups still read as pairs.
        /// </summary>
        const float MoonTreeInnerGapPx = 2f;
        /// <summary>
        /// Pixels between the five ODEMC pairs (Offense | Defense | Energy | …).
        /// Not scaled with node width so the gutter stays visible on small cards.
        /// </summary>
        const float MoonTreePairGapPx = 6f;

        [SerializeField] private Image[] segments = new Image[ShipAbilityCategoryColors.PowerBreakdownStatCount];
        [SerializeField] private float barHeight = 10f;
        [SerializeField] private float pairGap = 4f;

        Image[] _remainders;
        bool _slotLayersReady;

        public float TrackWidth { get; private set; }

        /// <summary>Stores segment images and bar metrics from <see cref="BuildBar"/>.</summary>
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

        static ShipUpgradeTreePowerBarUI BuildBar(
            Transform parent,
            float barHeight,
            float pairGap,
            float trackWidth,
            Color? trackBackground,
            float trackPadding)
        {
            Transform barParent = parent;
            if (trackBackground.HasValue)
            {
                int pad = Mathf.Max(1, Mathf.RoundToInt(trackPadding));
                var trackGo = new GameObject("PowerBarTrack");
                trackGo.transform.SetParent(parent, false);
                var trackLe = trackGo.AddComponent<LayoutElement>();
                trackLe.flexibleHeight = 0f;
                trackLe.flexibleWidth = 1f;
                trackLe.minWidth = trackWidth;
                trackLe.preferredWidth = trackWidth;
                trackLe.preferredHeight = barHeight + pad * 2f;
                trackLe.minHeight = trackLe.preferredHeight;
                var trackBg = trackGo.AddComponent<Image>();
                trackBg.color = trackBackground.Value;
                trackBg.raycastTarget = false;
                var trackVlg = trackGo.AddComponent<VerticalLayoutGroup>();
                trackVlg.padding = new RectOffset(pad, pad, pad, pad);
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
            barHlg.childForceExpandWidth = true;
            barHlg.childForceExpandHeight = true;

            var segments = new Image[ShipAbilityCategoryColors.PowerBreakdownStatCount];
            var remainders = new Image[ShipAbilityCategoryColors.PowerBreakdownStatCount];
            Sprite fillSprite = GetFillSprite();
            for (int pair = 0; pair < ShipAbilityCategoryColors.PowerBreakdownPairCount; pair++)
            {
                var pairGo = new GameObject("Pair_" + pair);
                pairGo.transform.SetParent(barRow.transform, false);
                var pairHlg = pairGo.AddComponent<HorizontalLayoutGroup>();
                pairHlg.spacing = MoonTreeInnerGapPx;
                pairHlg.childAlignment = TextAnchor.MiddleLeft;
                pairHlg.childControlWidth = true;
                pairHlg.childControlHeight = true;
                pairHlg.childForceExpandWidth = true;
                pairHlg.childForceExpandHeight = true;
                var pairLe = pairGo.AddComponent<LayoutElement>();
                pairLe.flexibleWidth = 1f;
                pairLe.minWidth = 0f;
                pairLe.preferredWidth = 0f;

                for (int tone = 0; tone < 2; tone++)
                {
                    int idx = pair * 2 + tone;
                    CreateSlot(pairGo.transform, idx, barHeight, fillSprite, out Image fill, out Image dim);
                    segments[idx] = fill;
                    remainders[idx] = dim;
                }
            }

            var powerBar = barRow.AddComponent<ShipUpgradeTreePowerBarUI>();
            powerBar.Initialize(segments, barHeight, pairGap);
            powerBar._remainders = remainders;
            powerBar._slotLayersReady = true;
            return powerBar;
        }

        static void CreateSlot(
            Transform pairParent,
            int statIndex,
            float barHeight,
            Sprite fillSprite,
            out Image fill,
            out Image dim)
        {
            var slotGo = new GameObject("Slot_" + statIndex);
            slotGo.transform.SetParent(pairParent, false);
            if (slotGo.GetComponent<RectTransform>() == null)
                slotGo.AddComponent<RectTransform>();
            var slotLe = slotGo.AddComponent<LayoutElement>();
            slotLe.flexibleWidth = 1f;
            slotLe.minWidth = 0f;
            slotLe.preferredWidth = 0f;
            slotLe.preferredHeight = barHeight;
            slotLe.minHeight = barHeight;

            dim = CreateStretchImage(slotGo.transform, "Dim_" + statIndex, fillSprite);
            dim.color = GetDimmedStatColor(statIndex);
            dim.type = Image.Type.Simple;

            fill = CreateStretchImage(slotGo.transform, "Seg_" + statIndex, fillSprite);
            fill.color = ShipAbilityCategoryColors.GetPowerBreakdownStatColor(statIndex);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 0f;
        }

        static Image CreateStretchImage(Transform parent, string name, Sprite sprite)
        {
            // Image replaces Transform with RectTransform — stretch only after that swap.
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.raycastTarget = false;
            StretchRect(img.rectTransform);
            return img;
        }

        static Sprite s_fillSprite;

        /// <summary>1×1 white sprite so <see cref="Image.Type.Filled"/> has a texture to clip.</summary>
        static Sprite GetFillSprite()
        {
            if (s_fillSprite != null)
                return s_fillSprite;

            Texture2D tex = Texture2D.whiteTexture;
            s_fillSprite = Sprite.Create(
                tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                100f);
            s_fillSprite.name = "PowerBarFillWhite";
            return s_fillSprite;
        }

        /// <summary>Same hue as the solid fill, lightened so the empty part of a live slot still reads as that ability.</summary>
        public static Color GetDimmedStatColor(int statIndex)
        {
            Color c = ShipAbilityCategoryColors.GetPowerBreakdownStatColor(statIndex);
            Color mixed = Color.Lerp(c, Color.white, DimWhiteMix);
            return new Color(mixed.r * DimRgbScale, mixed.g * DimRgbScale, mixed.b * DimRgbScale, DimAlpha);
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

        float _widthScale = 1f;
        float _heightScale = 1f;

        public void ConfigureLayoutScale(float widthScale, float heightScale)
        {
            _widthScale = Mathf.Max(0.01f, widthScale);
            _heightScale = Mathf.Max(0.01f, heightScale);

            var hlg = GetComponent<HorizontalLayoutGroup>();
            if (hlg != null)
                hlg.spacing = pairGap * _widthScale;
        }

        /// <summary>
        /// Moon upgrade-tree layout: full track width, equal 10% slots, fill = value / global max.
        /// </summary>
        public void ApplyBreakdown(
            ShipFamilyPowerScoreBreakdown breakdown,
            in ShipPowerBarStatMaxes globalMaxes,
            float trackWidth)
        {
            EnsureSlotLayers();
            TrackWidth = Mathf.Max(0f, trackWidth);
            float nodeW = TrackWidth > 0.01f ? TrackWidth : 100f;
            float scaledBarHeight = barHeight * _heightScale;
            bool hasData = breakdown.HasDisplayStats;

            ApplyMoonTreeFlexLayout(scaledBarHeight);

            for (int i = 0; i < ShipAbilityCategoryColors.PowerBreakdownStatCount; i++)
            {
                float val = breakdown.GetDisplayStatValue(i);
                float max = globalMaxes.Get(i);
                bool slotLive = hasData && val > 0.0001f;
                float ratio = slotLive && max > ShipPowerBarStatMaxes.MinDenominator
                    ? Mathf.Clamp01(val / max)
                    : 0f;
                ApplyMoonSlotFill(i, ratio, slotLive, scaledBarHeight);
            }

            ApplyBarRowSize(nodeW, scaledBarHeight);
        }

        /// <summary>
        /// Equipment cards usually contribute one or two stats. Hide empty category pairs and
        /// scale the active segments across the full track width for readability.
        /// </summary>
        public void ApplyEquipmentBreakdown(
            ShipFamilyPowerScoreBreakdown breakdown,
            float strongestComponentTotalPower,
            float trackWidth)
        {
            EnsureSlotLayers();
            ApplyBreakdownInternal(breakdown, strongestComponentTotalPower, trackWidth, equipmentLayout: true);
        }

        void ApplyMoonTreeFlexLayout(float scaledBarHeight)
        {
            var barHlg = GetComponent<HorizontalLayoutGroup>();
            if (barHlg != null)
            {
                barHlg.childForceExpandWidth = true;
                barHlg.spacing = MoonTreePairGapPx;
            }

            int pairCount = ShipAbilityCategoryColors.PowerBreakdownPairCount;
            for (int pair = 0; pair < pairCount; pair++)
            {
                int statA = pair * 2;
                ApplyPairWidth(statA, 0f, scaledBarHeight, flexible: true);
                ApplySlotFlex(statA, scaledBarHeight);
                ApplySlotFlex(statA + 1, scaledBarHeight);
                SetPairActive(statA, true);
            }

        }

        void ApplyMoonSlotFill(int statIndex, float ratio, bool hasData, float scaledBarHeight)
        {
            if (segments == null || statIndex < 0 || statIndex >= segments.Length || segments[statIndex] == null)
                return;

            Image fill = segments[statIndex];
            fill.sprite = GetFillSprite();
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = hasData ? ratio : 0f;
            fill.enabled = true;
            fill.color = hasData
                ? ShipAbilityCategoryColors.GetPowerBreakdownStatColor(statIndex)
                : DisabledSlotFill;
            fill.raycastTarget = false;

            var fillLe = fill.GetComponent<LayoutElement>();
            if (fillLe != null)
                fillLe.ignoreLayout = true;

            StretchRect(fill.rectTransform);

            if (_remainders != null && statIndex < _remainders.Length && _remainders[statIndex] != null)
            {
                Image dim = _remainders[statIndex];
                dim.sprite = GetFillSprite();
                dim.type = Image.Type.Simple;
                dim.enabled = true;
                dim.color = hasData
                    ? GetDimmedStatColor(statIndex)
                    : DisabledSlotTrack;
                dim.raycastTarget = false;
                var dimLe = dim.GetComponent<LayoutElement>();
                if (dimLe != null)
                    dimLe.ignoreLayout = true;
                StretchRect(dim.rectTransform);
            }

            ApplySlotFlex(statIndex, scaledBarHeight);
        }

        void ApplyBreakdownInternal(
            ShipFamilyPowerScoreBreakdown breakdown,
            float strongestTotalPower,
            float trackWidth,
            bool equipmentLayout)
        {
            TrackWidth = Mathf.Max(0f, trackWidth);
            float total = GetEquipmentBarDisplayTotal(breakdown);
            bool hasData = total > 0.01f;
            float maxDen = Mathf.Max(strongestTotalPower, 0.001f);
            float nodeW = TrackWidth > 0.01f ? TrackWidth : 100f;
            float scaledBarHeight = barHeight * _heightScale;
            float barFillW = hasData ? nodeW * total / maxDen : nodeW;

            int pairCount = ShipAbilityCategoryColors.PowerBreakdownPairCount;
            var barHlg = GetComponent<HorizontalLayoutGroup>();
            if (barHlg != null)
            {
                barHlg.childForceExpandWidth = false;
                barHlg.spacing = pairGap * _widthScale;
            }

            float gap = barHlg != null ? barHlg.spacing : pairGap * _widthScale;

            float activePairSum = 0f;
            int activePairCount = 0;
            if (hasData)
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

            if (hasData && activePairCount > 0)
                barFillW = nodeW * activePairSum / maxDen;

            float totalGap = gap * Mathf.Max(0, activePairCount - 1);
            float usableW = Mathf.Max(0f, barFillW - totalGap);
            float widthDenominator = hasData && activePairSum > 0.01f ? activePairSum : total;

            for (int pair = 0; pair < pairCount; pair++)
            {
                int statA = pair * 2;
                int statB = statA + 1;
                float valA = GetEquipmentBarStatValue(breakdown, statA);
                float valB = GetEquipmentBarStatValue(breakdown, statB);
                float pairSum = valA + valB;
                bool pairActive = pairSum > 0.01f || !hasData;

                float pairWidth;
                float segWA;
                float segWB;
                if (hasData && widthDenominator > 0.01f && pairActive && pairSum > 0.01f)
                {
                    pairWidth = usableW * pairSum / widthDenominator;
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

                ApplyEquipmentSegment(statA, segWA, hasData && pairActive, scaledBarHeight);
                ApplyEquipmentSegment(statB, segWB, hasData && pairActive, scaledBarHeight);
                ApplyPairWidth(statA, pairWidth, scaledBarHeight, flexible: false);
                SetPairActive(statA, pairActive || !hasData);
            }

            ApplyBarRowSize(nodeW, scaledBarHeight);
        }

        void ApplyEquipmentSegment(int statIndex, float segW, bool hasData, float scaledBarHeight)
        {
            if (segments == null || statIndex < 0 || statIndex >= segments.Length || segments[statIndex] == null)
                return;

            if (_remainders != null && statIndex < _remainders.Length && _remainders[statIndex] != null)
                _remainders[statIndex].enabled = false;

            Image fill = segments[statIndex];
            fill.sprite = GetFillSprite();
            fill.type = Image.Type.Simple;
            fill.fillAmount = 1f;
            fill.enabled = segW > 0.01f;
            fill.color = hasData
                ? ShipAbilityCategoryColors.GetPowerBreakdownStatColor(statIndex)
                : DisabledSlotFill;

            var fillLe = fill.GetComponent<LayoutElement>();
            if (fillLe != null)
                fillLe.ignoreLayout = true;
            StretchRect(fill.rectTransform);

            Transform slot = fill.transform.parent;
            if (slot == null)
                return;

            var slotLe = slot.GetComponent<LayoutElement>();
            if (slotLe == null)
                slotLe = slot.gameObject.AddComponent<LayoutElement>();

            float rounded = segW > 0.01f ? Mathf.Max(1f, Mathf.Round(segW)) : 0f;
            slotLe.preferredWidth = rounded;
            slotLe.flexibleWidth = 0f;
            slotLe.minWidth = 0f;
            slotLe.preferredHeight = scaledBarHeight;
            slotLe.minHeight = scaledBarHeight;
        }

        void ApplyPairWidth(int statIndex, float pairWidth, float scaledBarHeight, bool flexible)
        {
            if (segments == null || statIndex < 0 || statIndex >= segments.Length || segments[statIndex] == null)
                return;

            Transform pairTransform = GetPairTransform(statIndex);
            if (pairTransform == null)
                return;

            var pairLe = pairTransform.GetComponent<LayoutElement>();
            if (pairLe == null)
                return;

            if (flexible)
            {
                pairLe.preferredWidth = 0f;
                pairLe.flexibleWidth = 1f;
                pairLe.minWidth = 0f;
            }
            else
            {
                float roundedPairWidth = Mathf.Round(pairWidth);
                pairLe.preferredWidth = roundedPairWidth;
                pairLe.flexibleWidth = 0f;
                pairLe.minWidth = 0f;
            }

            pairLe.preferredHeight = scaledBarHeight;
            pairLe.minHeight = scaledBarHeight;

            var pairHlg = pairTransform.GetComponent<HorizontalLayoutGroup>();
            if (pairHlg != null)
            {
                pairHlg.childForceExpandWidth = flexible;
                // Moon tree: gap between the two abilities in the pair. Equipment stays flush.
                pairHlg.spacing = flexible ? MoonTreeInnerGapPx : 0f;
            }
        }

        void ApplySlotFlex(int statIndex, float scaledBarHeight)
        {
            if (segments == null || statIndex < 0 || statIndex >= segments.Length || segments[statIndex] == null)
                return;

            Transform slot = segments[statIndex].transform.parent;
            if (slot == null)
                return;

            var slotLe = slot.GetComponent<LayoutElement>();
            if (slotLe == null)
                slotLe = slot.gameObject.AddComponent<LayoutElement>();

            slotLe.preferredWidth = 0f;
            slotLe.flexibleWidth = 1f;
            slotLe.minWidth = 0f;
            slotLe.preferredHeight = scaledBarHeight;
            slotLe.minHeight = scaledBarHeight;
        }

        void SetPairActive(int statIndex, bool active)
        {
            Transform pairTransform = GetPairTransform(statIndex);
            if (pairTransform != null)
                pairTransform.gameObject.SetActive(active);
        }

        Transform GetPairTransform(int statIndex)
        {
            if (segments == null || statIndex < 0 || statIndex >= segments.Length || segments[statIndex] == null)
                return null;

            Transform slot = segments[statIndex].transform.parent;
            return slot != null ? slot.parent : null;
        }

        void ApplyBarRowSize(float nodeW, float scaledBarHeight)
        {
            var barRow = GetComponent<RectTransform>();
            if (barRow == null)
                return;

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

        static void StretchRect(RectTransform rt)
        {
            if (rt == null)
                return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Prefab nodes still have Pair → Seg Image. Wrap each segment in a Slot and add a
        /// dim Image behind it so equal-slot fills work without rebaking the node prefab.
        /// </summary>
        void EnsureSlotLayers()
        {
            if (_slotLayersReady)
                return;
            if (segments == null || segments.Length == 0)
                return;

            Sprite fillSprite = GetFillSprite();
            _remainders = new Image[segments.Length];

            for (int i = 0; i < segments.Length; i++)
            {
                Image fill = segments[i];
                if (fill == null)
                    continue;

                Transform currentParent = fill.transform.parent;
                if (currentParent != null && currentParent.name.StartsWith("Slot_"))
                {
                    _remainders[i] = FindDimInSlot(currentParent);
                    continue;
                }

                var slotGo = new GameObject("Slot_" + i);
                slotGo.transform.SetParent(currentParent, false);
                if (slotGo.GetComponent<RectTransform>() == null)
                    slotGo.AddComponent<RectTransform>();
                slotGo.transform.SetSiblingIndex(fill.transform.GetSiblingIndex());
                var slotLe = slotGo.AddComponent<LayoutElement>();
                slotLe.flexibleWidth = 1f;
                slotLe.minWidth = 0f;
                slotLe.preferredWidth = 0f;

                fill.transform.SetParent(slotGo.transform, false);
                fill.sprite = fillSprite;
                fill.type = Image.Type.Filled;
                fill.fillMethod = Image.FillMethod.Horizontal;
                fill.fillOrigin = (int)Image.OriginHorizontal.Left;
                var oldLe = fill.GetComponent<LayoutElement>();
                if (oldLe != null)
                    oldLe.ignoreLayout = true;
                StretchRect(fill.rectTransform);

                Image dim = CreateStretchImage(slotGo.transform, "Dim_" + i, fillSprite);
                dim.transform.SetAsFirstSibling();
                dim.color = GetDimmedStatColor(i);
                dim.type = Image.Type.Simple;
                _remainders[i] = dim;
            }

            _slotLayersReady = true;
        }

        static Image FindDimInSlot(Transform slot)
        {
            for (int i = 0; i < slot.childCount; i++)
            {
                Transform child = slot.GetChild(i);
                if (child != null && child.name.StartsWith("Dim_"))
                    return child.GetComponent<Image>();
            }

            return null;
        }
    }
}
