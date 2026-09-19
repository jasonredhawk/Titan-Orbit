using TitanOrbit.Data;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Ten-segment power bar grouped into five ODEMC pairs (Offense … Capacity).
    /// Moon / Orbit Menu cards keep the five groups in one horizontal row (each pair
    /// is 20% of the track). Inside a pair the two abilities stack vertically — Fire
    /// Power over Bullet Speed, Health Cap over Health Regen, and so on — so each
    /// fill is twice as wide as the old side-by-side 10% lanes. Small catalog
    /// increments stay visible on that wider fill. The ship's value is a solid fill
    /// and the rest of the lane is a dimmed matching color.
    /// Equipment cards keep the older proportional widths (one or two stats side by
    /// side, hide empty pairs). Hovering a slot opens
    /// <see cref="ShipPowerBarStatTooltip"/> (stat details + small RANK 1 hull).
    /// Built at runtime by <see cref="Create"/> /
    /// <see cref="CreateInTrack"/>, or upgraded in place from the serialized node prefab.
    /// </summary>
    public class ShipUpgradeTreePowerBarUI : MonoBehaviour
    {
        /// <summary>
        /// Gem Cap (8) and Troop Cap (9) bar widths use this fraction of raw stat power
        /// on equipment cards only, so high gem capacity does not dominate those bars.
        /// Moon-tree equal slots no longer need this — each stat has its own stacked lane.
        /// </summary>
        public const float MoonTreeCapacityStatBarScale = 0.5f;

        /// <summary>Live empty-track tint: keep the stat hue, but lift it so the lane stays readable.</summary>
        const float DimRgbScale = 0.62f;
        const float DimWhiteMix = 0.22f;
        const float DimAlpha = 0.88f;
        static readonly Color DisabledSlotFill = new Color(0.13f, 0.15f, 0.19f, 0.7f);
        static readonly Color DisabledSlotTrack = new Color(0.1f, 0.12f, 0.16f, 0.55f);
        /// <summary>
        /// Pixels between the two stacked abilities in a pair (Fire Power over Bullet Speed).
        /// Smaller than <see cref="MoonTreePairGapPx"/> so ODEMC groups still read as columns.
        /// </summary>
        const float MoonTreeStackGapPx = 2f;
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
        ShipPowerBarStatHoverRelay _hoverRelay;
        ShipFamilyPowerScoreBreakdown _hoverBreakdown;
        ShipPowerBarStatMaxes _hoverMaxes;
        bool _hoverMegaPool;
        string _hoverChassisId;
        /// <summary>
        /// True after one ForceRebuild attempt this enable. Hide() SetActive(false) the
        /// Orbit Menu backdrop; the next land can leave this tray at 0×0 until a rebuild.
        /// We only try once so a truly empty equipment bar does not rebuild every LateUpdate.
        /// </summary>
        bool _hoverLayoutHealed;

        /// <summary>
        /// Extra pixels around the dark tray. Matches the invisible HoverHit pad so
        /// a 10px stacked bar stays hittable without needing the 4px fill under the cursor.
        /// </summary>
        const float HoverPadPx = 8f;

        /// <summary>Reused world-corner buffer so hover never allocates per slot per frame.</summary>
        static readonly Vector3[] s_WorldCorners = new Vector3[4];

        public float TrackWidth { get; private set; }

        /// <summary>Stores segment images and bar metrics from <see cref="BuildBar"/>.</summary>
        public void Initialize(Image[] segmentImages, float height, float gap)
        {
            segments = segmentImages;
            barHeight = height;
            pairGap = gap;
        }

        void OnEnable()
        {
            // --- Fresh hover layout ---
            // [UNITY] OnDisable/OnEnable runs when the moon-dock backdrop is
            // SetActive. The next hover must be allowed one rebuild if the tray is 0×0.
            _hoverLayoutHealed = false;
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
                // Equipment cards keep this pair side-by-side. Orbit Menu tree bars
                // swap it for a VerticalLayoutGroup in ApplyMoonTreeFlexLayout.
                var pairHlg = pairGo.AddComponent<HorizontalLayoutGroup>();
                pairHlg.spacing = 0f;
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
            powerBar.EnsureHoverRelay();
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
        /// Equipment cards: offense pair is sustained DPS plus ramming, then bullet speed.
        /// Fire Rate is already inside DPS — do not add it again on the Bullet Speed lane.
        /// </summary>
        public static float GetEquipmentBarStatValue(ShipFamilyPowerScoreBreakdown breakdown, int statIndex)
        {
            switch (statIndex)
            {
                case 0: return breakdown.GetDisplayDps() + breakdown.rammingPower;
                case 1: return breakdown.bulletSpeed;
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
        /// Moon / Orbit Menu layout: full track width, five equal category columns,
        /// two abilities stacked in each column. Fill amount = value / pool max
        /// (regular-family maxes on L1–L6, MEGA catalog maxes on L7).
        /// Slot 0 is sustained DPS (<c>firePower × fireRate</c>), not raw Fire Power.
        /// Called when a tree node or store tile paints its colourful stats bar.
        /// </summary>
        /// <param name="megaPool">True when <paramref name="globalMaxes"/> came from the MEGA catalog (RANK 1 must match).</param>
        /// <param name="chassisId">Optional hull id so RANK 1 can say "this hull" on the hover card.</param>
        public void ApplyBreakdown(
            ShipFamilyPowerScoreBreakdown breakdown,
            in ShipPowerBarStatMaxes globalMaxes,
            float trackWidth,
            bool megaPool = false,
            string chassisId = null)
        {
            EnsureSlotLayers();
            // Hover tips must use this paint's breakdown, pool (regular vs MEGA), and hull id.
            BindHoverContext(breakdown, in globalMaxes, megaPool, chassisId);
            TrackWidth = Mathf.Max(0f, trackWidth);
            float nodeW = TrackWidth > 0.01f ? TrackWidth : 100f;
            float scaledBarHeight = barHeight * _heightScale;
            bool hasData = breakdown.HasDisplayStats;

            // --- Stack each ODEMC pair, then paint fills ---
            ApplyMoonTreeFlexLayout(scaledBarHeight);
            GetMoonTreeStackMetrics(scaledBarHeight, out float slotHeight, out _);

            for (int i = 0; i < ShipAbilityCategoryColors.PowerBreakdownStatCount; i++)
            {
                float val = breakdown.GetDisplayStatValue(i);
                float max = globalMaxes.Get(i);
                bool slotLive = hasData && val > 0.0001f;
                float ratio = slotLive && max > ShipPowerBarStatMaxes.MinDenominator
                    ? Mathf.Clamp01(val / max)
                    : 0f;
                ApplyMoonSlotFill(i, ratio, slotLive, slotHeight);
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
            // Gear tiles still explain the ten stats + RANK 1 from the regular-family pool.
            BindHoverContext(
                breakdown,
                ShipFamilyPowerBarNorm.GetGlobalMaxPerStat(),
                megaPool: false,
                chassisId: null);
            ApplyBreakdownInternal(breakdown, strongestComponentTotalPower, trackWidth, equipmentLayout: true);
        }

        /// <summary>
        /// Gives every ODEMC category an equal share of the track and stacks its two
        /// stats. Total bar height stays <paramref name="scaledBarHeight"/> — the same
        /// reserved strip the node already budgeted — so the card does not grow.
        /// </summary>
        void ApplyMoonTreeFlexLayout(float scaledBarHeight)
        {
            // --- Five equal columns across the track ---
            var barHlg = GetComponent<HorizontalLayoutGroup>();
            if (barHlg != null)
            {
                barHlg.childForceExpandWidth = true;
                barHlg.spacing = MoonTreePairGapPx;
            }

            GetMoonTreeStackMetrics(scaledBarHeight, out float slotHeight, out float stackGap);
            int pairCount = ShipAbilityCategoryColors.PowerBreakdownPairCount;
            for (int pair = 0; pair < pairCount; pair++)
            {
                // One iteration = Offense, Defense, Energy, Movement, or Capacity.
                int statA = pair * 2;
                ApplyPairWidth(statA, 0f, scaledBarHeight, flexible: true, stackVertically: true, stackGap);
                ApplySlotFlex(statA, slotHeight);
                ApplySlotFlex(statA + 1, slotHeight);
                SetPairActive(statA, true);
            }
        }

        /// <summary>
        /// Paints one stacked lane: bright fill from the left for the ship's share of the
        /// catalog max, dim remainder behind it. <paramref name="slotHeight"/> is half the
        /// reserved bar (minus the gutter), not the full track height.
        /// </summary>
        void ApplyMoonSlotFill(int statIndex, float ratio, bool hasData, float slotHeight)
        {
            if (segments == null || statIndex < 0 || statIndex >= segments.Length || segments[statIndex] == null)
                return;

            // --- Solid fill (value / global max) ---
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

            ApplySlotFlex(statIndex, slotHeight);
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
                ApplyPairWidth(statA, pairWidth, scaledBarHeight, flexible: false, stackVertically: false, stackGap: 0f);
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

        /// <summary>
        /// Sizes one ODEMC pair column and chooses side-by-side vs stacked children.
        /// <paramref name="flexible"/> true = Orbit Menu equal columns; false = equipment pixel width.
        /// </summary>
        void ApplyPairWidth(
            int statIndex,
            float pairWidth,
            float scaledBarHeight,
            bool flexible,
            bool stackVertically,
            float stackGap)
        {
            if (segments == null || statIndex < 0 || statIndex >= segments.Length || segments[statIndex] == null)
                return;

            Transform pairTransform = GetPairTransform(statIndex);
            if (pairTransform == null)
                return;

            var pairLe = pairTransform.GetComponent<LayoutElement>();
            if (pairLe == null)
                pairLe = pairTransform.gameObject.AddComponent<LayoutElement>();

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

            SetPairOrientation(pairTransform, stackVertically, stackGap, expandWidth: flexible);
        }

        /// <summary>
        /// Makes one ability lane fill the pair's width and use the stacked slot height.
        /// Width is flexible so five columns share the track equally; height is fixed so
        /// the two stacked fills plus the gutter equal the reserved bar height.
        /// </summary>
        void ApplySlotFlex(int statIndex, float slotHeight)
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
            slotLe.preferredHeight = slotHeight;
            slotLe.minHeight = slotHeight;
            slotLe.flexibleHeight = 0f;
        }

        /// <summary>
        /// Splits the reserved bar height into two stacked lanes plus a small gutter.
        /// On a 10px tree bar that is 4 + 2 + 4. We never spend the whole height on
        /// the gap — at least 1px stays for each fill so empty dim tracks still show.
        /// </summary>
        static void GetMoonTreeStackMetrics(float scaledBarHeight, out float slotHeight, out float stackGap)
        {
            stackGap = Mathf.Min(MoonTreeStackGapPx, Mathf.Max(0f, scaledBarHeight - 2f));
            slotHeight = Mathf.Max(1f, (scaledBarHeight - stackGap) * 0.5f);
        }

        /// <summary>
        /// Switches a category pair between side-by-side (equipment) and stacked (Orbit Menu).
        /// [UNITY] <see cref="LayoutGroup"/> is <c>DisallowMultipleComponent</c>. A pair
        /// GameObject can hold Horizontal <em>or</em> Vertical, never both — adding the
        /// second type returns null. We remove the old group the same frame, then add
        /// the one we need. After the first refresh the right group is already there.
        /// </summary>
        static void SetPairOrientation(Transform pairTransform, bool stackVertically, float stackGap, bool expandWidth)
        {
            if (pairTransform == null)
                return;

            GameObject go = pairTransform.gameObject;
            if (stackVertically)
            {
                var vlg = ReplacePairLayout<VerticalLayoutGroup>(go);
                if (vlg == null)
                    return;
                vlg.spacing = stackGap;
                vlg.childAlignment = TextAnchor.MiddleCenter;
                vlg.childControlWidth = true;
                vlg.childControlHeight = true;
                vlg.childForceExpandWidth = true;
                vlg.childForceExpandHeight = false;
                vlg.padding = new RectOffset(0, 0, 0, 0);
            }
            else
            {
                var hlg = ReplacePairLayout<HorizontalLayoutGroup>(go);
                if (hlg == null)
                    return;
                hlg.spacing = 0f;
                hlg.childAlignment = TextAnchor.MiddleLeft;
                hlg.childControlWidth = true;
                hlg.childControlHeight = true;
                hlg.childForceExpandWidth = expandWidth;
                hlg.childForceExpandHeight = true;
                hlg.padding = new RectOffset(0, 0, 0, 0);
            }
        }

        /// <summary>
        /// Returns the pair's <typeparamref name="TLayout"/>, creating it after removing
        /// any other <see cref="LayoutGroup"/>. Same-frame <c>Destroy</c> would still
        /// block AddComponent, so this uses DestroyImmediate on the old group only.
        /// </summary>
        static TLayout ReplacePairLayout<TLayout>(GameObject go) where TLayout : LayoutGroup
        {
            if (go == null)
                return null;

            var existing = go.GetComponent<TLayout>();
            if (existing != null)
                return existing;

            // Prefab pairs start with HorizontalLayoutGroup. Tear it down before stacking.
            var other = go.GetComponent<LayoutGroup>();
            if (other != null)
                Object.DestroyImmediate(other);

            return go.AddComponent<TLayout>();
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
                // Inside a PowerBarTrack the parent VLG already pads. A large minWidth
                // here shoved the colourful lanes into the tray edges.
                bool inTrack = transform.parent != null && transform.parent.name == "PowerBarTrack";
                if (inTrack)
                {
                    barLe.minWidth = 0f;
                    barLe.preferredWidth = -1f;
                    barLe.flexibleWidth = 1f;
                }
                else
                {
                    barLe.preferredWidth = Mathf.Round(nodeW);
                    barLe.minWidth = Mathf.Round(nodeW);
                    barLe.flexibleWidth = 1f;
                }

                barLe.preferredHeight = scaledBarHeight;
                barLe.minHeight = scaledBarHeight;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(barRow);
            RefreshHoverHitRect();
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

        /// <summary>
        /// Stores the painted breakdown so hover tips use the same numbers as the fill.
        /// Creates the padded hit overlay the first time a bar is shown.
        /// </summary>
        void BindHoverContext(
            in ShipFamilyPowerScoreBreakdown breakdown,
            in ShipPowerBarStatMaxes maxes,
            bool megaPool,
            string chassisId)
        {
            _hoverBreakdown = breakdown;
            _hoverMaxes = maxes;
            _hoverMegaPool = megaPool;
            _hoverChassisId = chassisId;
            EnsureHoverRelay();
        }

        /// <summary>
        /// Adds a transparent, layout-ignored overlay that is taller than the 10px bar
        /// so the tiny stacked lanes are actually hoverable. Click/drag still reach the card.
        /// Lives on the dark PowerBarTrack when that tray exists — the track has a stable
        /// height, while the colourful row can be 0px for one layout frame.
        /// </summary>
        void EnsureHoverRelay()
        {
            if (_hoverRelay != null)
            {
                _hoverRelay.Owner = this;
                RefreshHoverHitRect();
                return;
            }

            // --- Hit parent ---
            // [TITAN-ORBIT] Tree cards wrap this row in PowerBarTrack. Hovering the tray
            // (not only the 4px stacked fills) is what the player expects.
            Transform hitParent = transform;
            if (transform.parent != null && transform.parent.name == "PowerBarTrack")
                hitParent = transform.parent;

            Transform existing = hitParent.Find("HoverHit");
            if (existing == null && hitParent != transform)
                existing = transform.Find("HoverHit");

            GameObject hitGo = existing != null ? existing.gameObject : null;
            if (hitGo == null)
            {
                hitGo = new GameObject("HoverHit");
                hitGo.transform.SetParent(hitParent, false);
                RectTransform hitRt = hitGo.AddComponent<RectTransform>();
                var hitLe = hitGo.AddComponent<LayoutElement>();
                hitLe.ignoreLayout = true;
                var hitImg = hitGo.AddComponent<Image>();
                // [UNITY] A sprite-less Image can skip the raycast mesh. Use the same
                // 1×1 white fill as the lanes, then alpha-0 so the pad stays invisible.
                hitImg.sprite = GetFillSprite();
                hitImg.color = new Color(1f, 1f, 1f, 0f);
                hitImg.raycastTarget = true;
                if (hitImg.canvasRenderer != null)
                    hitImg.canvasRenderer.cullTransparentMesh = false;
                ApplyHoverHitStretch(hitRt);
            }
            else if (hitGo.transform.parent != hitParent)
            {
                hitGo.transform.SetParent(hitParent, false);
            }

            _hoverRelay = hitGo.GetComponent<ShipPowerBarStatHoverRelay>();
            if (_hoverRelay == null)
                _hoverRelay = hitGo.AddComponent<ShipPowerBarStatHoverRelay>();
            _hoverRelay.Owner = this;
            RefreshHoverHitRect();
        }

        /// <summary>
        /// Re-applies stretch + last-sibling after a layout rebuild. PowerBarTrack is a
        /// VerticalLayoutGroup — without ignoreLayout the pad can collapse to 0px and
        /// never receive pointer enter.
        /// </summary>
        void RefreshHoverHitRect()
        {
            if (_hoverRelay == null)
                return;

            Transform hit = _hoverRelay.transform;
            var hitLe = hit.GetComponent<LayoutElement>();
            if (hitLe != null)
                hitLe.ignoreLayout = true;

            var hitImg = hit.GetComponent<Image>();
            if (hitImg != null)
            {
                if (hitImg.sprite == null)
                    hitImg.sprite = GetFillSprite();
                hitImg.raycastTarget = true;
                if (hitImg.canvasRenderer != null)
                    hitImg.canvasRenderer.cullTransparentMesh = false;
            }

            ApplyHoverHitStretch(hit as RectTransform);
            hit.SetAsLastSibling();
        }

        /// <summary>Fills the dark tray with a few extra pixels so 4px stacked lanes stay hoverable.</summary>
        static void ApplyHoverHitStretch(RectTransform hitRt)
        {
            if (hitRt == null)
                return;
            hitRt.anchorMin = Vector2.zero;
            hitRt.anchorMax = Vector2.one;
            hitRt.pivot = new Vector2(0.5f, 0.5f);
            hitRt.offsetMin = new Vector2(-4f, -8f);
            hitRt.offsetMax = new Vector2(4f, 8f);
        }

        /// <summary>
        /// True when <paramref name="screenPoint"/> is over this bar's dark tray
        /// (or any painted slot). Used by the hover probe so a 0px HoverHit overlay
        /// cannot hide the STAT TELEMETRY card.
        /// </summary>
        public bool ContainsScreenPoint(Vector2 screenPoint, UnityEngine.Camera eventCamera)
        {
            return TryHitSlot(screenPoint, eventCamera, out _, out _, out _);
        }

        /// <summary>
        /// Slot 0–9 under <paramref name="screenPoint"/>, or the nearest slot when the
        /// pointer is in the padded gutter. Hidden equipment pairs are skipped.
        /// </summary>
        /// <param name="eventCamera">
        /// Canvas camera, or null for Overlay. [UNITY] Must be
        /// <c>UnityEngine.Camera</c> — <c>TitanOrbit.Camera</c> is a namespace.
        /// </param>
        public int PickSlotAtScreenPoint(Vector2 screenPoint, UnityEngine.Camera eventCamera)
        {
            return TryHitSlot(screenPoint, eventCamera, out int slot, out _, out _) ? slot : -1;
        }

        /// <summary>
        /// Maps the pointer to a painted slot on this bar. Tries the canvas camera, then
        /// the Overlay (null) camera, so a Screen Space mismatch cannot hide the card.
        /// </summary>
        /// <param name="screenPoint">Mouse or touch in screen pixels.</param>
        /// <param name="eventCamera">Preferred canvas camera (null = Overlay).</param>
        /// <param name="slot">Slot 0–9 when this returns true.</param>
        /// <param name="area">Tray width × height. Tie-break when two trays are equally close.</param>
        /// <param name="distSq">Sqr distance from the pointer to the tray center.</param>
        /// <returns>True when the pointer is over this tray (plus a few pixels of pad).</returns>
        public bool TryHitSlot(
            Vector2 screenPoint,
            UnityEngine.Camera eventCamera,
            out int slot,
            out float area,
            out float distSq)
        {
            // --- Screen-space tray, then local fallback ---
            // [UNITY] Overlay wants a null camera. Screen Space Camera wants worldCamera.
            // Local-rect tests die when Hide() SetActive the dock: rect can stay 0×0
            // while the colourful fills still *look* painted. World corners → screen
            // AABB still hit if the bar is on screen.
            EnsureSlotLayers();
            RectTransform tray = ResolveHoverTray();
            if (tray == null)
            {
                slot = -1;
                area = float.MaxValue;
                distSq = float.MaxValue;
                return false;
            }

            TryHealDegenerateHoverTray(tray);

            if (TryHitSlotOnScreen(tray, screenPoint, eventCamera, out slot, out area, out distSq))
                return true;

            UnityEngine.Camera other = eventCamera == null ? FindRootWorldCamera() : null;
            if (other != eventCamera
                && TryHitSlotOnScreen(tray, screenPoint, other, out slot, out area, out distSq))
                return true;

            return TryHitSlotWithCamera(screenPoint, eventCamera, out slot, out area, out distSq)
                || (other != eventCamera
                    && TryHitSlotWithCamera(screenPoint, other, out slot, out area, out distSq));
        }

        /// <summary>
        /// Rebuilds the dark tray after the Orbit Menu is shown again. Public so the
        /// hover probe can fix every live bar in one pass — not only the first 0×0 hit.
        /// </summary>
        public void ForceRebuildHoverTray()
        {
            _hoverLayoutHealed = false;
            RectTransform tray = ResolveHoverTray();
            if (tray == null)
                return;
            LayoutRebuilder.ForceRebuildLayoutImmediate(tray);
            if (transform is RectTransform self)
                LayoutRebuilder.ForceRebuildLayoutImmediate(self);
            RefreshHoverHitRect();
            _hoverLayoutHealed = true;
        }

        /// <summary>Root canvas worldCamera, or null when this bar has no camera canvas.</summary>
        UnityEngine.Camera FindRootWorldCamera()
        {
            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
                return null;
            if (canvas.rootCanvas != null)
                canvas = canvas.rootCanvas;
            return canvas.worldCamera;
        }

        /// <summary>
        /// Hits the tray from world corners in screen pixels. Does not need a valid
        /// <c>RectTransform.rect</c> — only that the bar is posed on screen.
        /// </summary>
        bool TryHitSlotOnScreen(
            RectTransform tray,
            Vector2 screenPoint,
            UnityEngine.Camera eventCamera,
            out int slot,
            out float area,
            out float distSq)
        {
            slot = -1;
            area = float.MaxValue;
            distSq = float.MaxValue;
            if (tray == null)
                return false;

            tray.GetWorldCorners(s_WorldCorners);
            Vector2 a = RectTransformUtility.WorldToScreenPoint(eventCamera, s_WorldCorners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(eventCamera, s_WorldCorners[2]);
            if (!float.IsFinite(a.x) || !float.IsFinite(b.x))
                return false;

            float xMin = Mathf.Min(a.x, b.x) - HoverPadPx;
            float xMax = Mathf.Max(a.x, b.x) + HoverPadPx;
            float yMin = Mathf.Min(a.y, b.y) - HoverPadPx;
            float yMax = Mathf.Max(a.y, b.y) + HoverPadPx;
            float w = xMax - xMin;
            float h = yMax - yMin;
            if (w < 2f || h < 2f)
                return false;

            if (screenPoint.x < xMin || screenPoint.x > xMax
                || screenPoint.y < yMin || screenPoint.y > yMax)
                return false;

            area = w * h;
            Vector2 center = new Vector2((xMin + xMax) * 0.5f, (yMin + yMax) * 0.5f);
            distSq = (screenPoint - center).sqrMagnitude;

            if (TryPickPaintedSlot(screenPoint, eventCamera, out int painted, out _, out _))
            {
                slot = painted;
                return true;
            }

            if (IsMoonTreeStacked())
            {
                float nx = Mathf.Clamp01((screenPoint.x - xMin) / w);
                float ny = Mathf.Clamp01((screenPoint.y - yMin) / h);
                int pairCount = ShipAbilityCategoryColors.PowerBreakdownPairCount;
                int pair = Mathf.Clamp((int)(nx * pairCount), 0, pairCount - 1);
                // Screen Y is up. VerticalLayoutGroup paints the first child at the top.
                int tone = ny >= 0.5f ? 0 : 1;
                slot = pair * 2 + tone;
                return slot >= 0;
            }

            slot = NearestSlot(screenPoint, eventCamera);
            return slot >= 0;
        }

        /// <summary>One-camera hit test: padded tray, then painted slots, then stacked 5×2 map.</summary>
        bool TryHitSlotWithCamera(
            Vector2 screenPoint,
            UnityEngine.Camera eventCamera,
            out int slot,
            out float area,
            out float distSq)
        {
            slot = -1;
            area = float.MaxValue;
            distSq = float.MaxValue;

            EnsureSlotLayers();
            RectTransform tray = ResolveHoverTray();
            if (tray == null)
                return false;

            TryHealDegenerateHoverTray(tray);

            if (!TryLocalInPaddedRect(tray, screenPoint, eventCamera, HoverPadPx, out Vector2 local))
            {
                // Tray can be 0px for one layout frame after warmup. Fall back to any
                // painted slot that still has a real rect.
                if (!TryPickPaintedSlot(screenPoint, eventCamera, out slot, out area, out distSq))
                    return false;
                return slot >= 0;
            }

            area = Mathf.Max(1f, tray.rect.width) * Mathf.Max(1f, tray.rect.height);
            Vector2 trayCenter = tray.rect.center;
            distSq = (local - trayCenter).sqrMagnitude;

            // --- Exact slot under the cursor ---
            if (TryPickPaintedSlot(screenPoint, eventCamera, out int painted, out _, out _))
            {
                slot = painted;
                return true;
            }

            // --- Tree layout: five equal columns, two stacked lanes ---
            // Slot rects are ~4px. After a skipped ForceRebuild they are often 0×0, so
            // we map from the tray instead of asking each Slot_N rect.
            if (IsMoonTreeStacked())
            {
                slot = MapStackedSlot(tray.rect, local);
                return slot >= 0;
            }

            slot = NearestSlot(screenPoint, eventCamera);
            return slot >= 0;
        }

        /// <summary>
        /// Rebuilds a 0×0 dark tray once after the Orbit Menu is SetActive again.
        /// [UNITY] VerticalLayoutGroup children can keep a cached mesh (the colourful
        /// fills still *look* painted) while <c>rect</c> is empty — hover then misses.
        /// </summary>
        /// <param name="tray">PowerBarTrack or this row.</param>
        void TryHealDegenerateHoverTray(RectTransform tray)
        {
            if (tray == null || _hoverLayoutHealed)
                return;
            if (tray.rect.width > 1f && tray.rect.height > 1f)
                return;

            _hoverLayoutHealed = true;
            LayoutRebuilder.ForceRebuildLayoutImmediate(tray);
            if (tray.parent is RectTransform parentRt)
                LayoutRebuilder.ForceRebuildLayoutImmediate(parentRt);
            RefreshHoverHitRect();
        }

        /// <summary>Dark PowerBarTrack when the tree card wrapped us; otherwise this row.</summary>
        RectTransform ResolveHoverTray()
        {
            if (transform.parent != null && transform.parent.name == "PowerBarTrack")
                return transform.parent as RectTransform;
            return transform as RectTransform;
        }

        /// <summary>
        /// True when this bar stacked each ODEMC pair (Orbit Menu tree / Your Ship).
        /// Equipment cards keep a HorizontalLayoutGroup and must use painted slot widths.
        /// </summary>
        bool IsMoonTreeStacked()
        {
            Transform pair = GetPairTransform(0);
            return pair != null && pair.GetComponent<VerticalLayoutGroup>() != null;
        }

        /// <summary>
        /// Converts a screen point into tray-local space and tests the padded rect.
        /// [UNITY] ScreenPointToLocalPointInRectangle returns true for any conversion;
        /// we still have to test the padded bounds ourselves.
        /// </summary>
        static bool TryLocalInPaddedRect(
            RectTransform rt,
            Vector2 screenPoint,
            UnityEngine.Camera eventCamera,
            float pad,
            out Vector2 local)
        {
            local = default;
            if (rt == null || rt.rect.width <= 1f || rt.rect.height <= 1f)
                return false;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, screenPoint, eventCamera, out local))
                return false;

            Rect r = rt.rect;
            return local.x >= r.xMin - pad && local.x <= r.xMax + pad
                && local.y >= r.yMin - pad && local.y <= r.yMax + pad;
        }

        /// <summary>Slot whose live rect contains the pointer. Skips 0px / hidden equipment pairs.</summary>
        bool TryPickPaintedSlot(
            Vector2 screenPoint,
            UnityEngine.Camera eventCamera,
            out int slot,
            out float area,
            out float distSq)
        {
            slot = -1;
            area = float.MaxValue;
            distSq = float.MaxValue;
            if (segments == null)
                return false;

            for (int i = 0; i < segments.Length; i++)
            {
                RectTransform slotRt = GetSlotRect(i);
                if (slotRt == null || !slotRt.gameObject.activeInHierarchy)
                    continue;
                if (slotRt.rect.width <= 1f || slotRt.rect.height <= 1f)
                    continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(slotRt, screenPoint, eventCamera))
                    continue;

                slot = i;
                area = slotRt.rect.width * slotRt.rect.height;
                slotRt.GetWorldCorners(s_WorldCorners);
                Vector3 worldCenter = (s_WorldCorners[0] + s_WorldCorners[2]) * 0.5f;
                Vector2 screenCenter = RectTransformUtility.WorldToScreenPoint(eventCamera, worldCenter);
                distSq = (screenCenter - screenPoint).sqrMagnitude;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Five equal columns across the tray, top lane = even slot (Fire Power, …),
        /// bottom lane = odd slot (Bullet Speed, …). Matches ApplyMoonTreeFlexLayout.
        /// </summary>
        static int MapStackedSlot(Rect tray, Vector2 local)
        {
            int pairCount = ShipAbilityCategoryColors.PowerBreakdownPairCount;
            if (tray.width <= 0.01f || tray.height <= 0.01f || pairCount <= 0)
                return -1;

            float nx = Mathf.Clamp01((local.x - tray.xMin) / tray.width);
            float ny = Mathf.Clamp01((local.y - tray.yMin) / tray.height);
            int pair = Mathf.Clamp((int)(nx * pairCount), 0, pairCount - 1);
            // [UNITY] Rect local Y is up. VerticalLayoutGroup paints the first child at the top.
            int tone = ny >= 0.5f ? 0 : 1;
            return pair * 2 + tone;
        }

        /// <summary>Closest painted slot by screen-space center. No per-call array alloc.</summary>
        int NearestSlot(Vector2 screenPoint, UnityEngine.Camera eventCamera)
        {
            if (segments == null)
                return -1;

            int nearest = -1;
            float nearestDist = float.MaxValue;
            for (int i = 0; i < segments.Length; i++)
            {
                RectTransform slotRt = GetSlotRect(i);
                if (slotRt == null || !slotRt.gameObject.activeInHierarchy)
                    continue;

                slotRt.GetWorldCorners(s_WorldCorners);
                Vector3 worldCenter = (s_WorldCorners[0] + s_WorldCorners[2]) * 0.5f;
                Vector2 screenCenter = RectTransformUtility.WorldToScreenPoint(eventCamera, worldCenter);
                float dist = (screenCenter - screenPoint).sqrMagnitude;
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = i;
                }
            }

            return nearest;
        }

        /// <summary>Opens the shared STAT TELEMETRY card for one painted slot.</summary>
        public void ShowStatTooltip(int statIndex)
        {
            // Prefer the slot so the card sits beside the hovered lane. A 0px slot
            // (stale layout) would park the tip at the origin — use the dark tray instead.
            RectTransform anchor = GetSlotRect(statIndex);
            if (anchor == null || anchor.rect.width < 1f || anchor.rect.height < 1f)
                anchor = ResolveHoverTray();
            if (anchor == null)
                anchor = transform as RectTransform;
            ShipPowerBarStatTooltip.Show(
                statIndex,
                in _hoverBreakdown,
                in _hoverMaxes,
                _hoverMegaPool,
                anchor,
                _hoverChassisId,
                _hoverRelay);
        }

        /// <summary>Slot wrapper rect (fill + dim). Null when the segment was never built.</summary>
        RectTransform GetSlotRect(int statIndex)
        {
            if (segments == null || statIndex < 0 || statIndex >= segments.Length || segments[statIndex] == null)
                return null;
            return segments[statIndex].transform.parent as RectTransform;
        }
    }
}
