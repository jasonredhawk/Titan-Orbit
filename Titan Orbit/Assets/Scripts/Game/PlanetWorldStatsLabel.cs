using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>
    /// World-space label floating above a planet body: ship family name (title) plus population.
    /// Layout reads top-to-bottom as <b>current people</b>, then the population <b>capacity</b>
    /// (base size/level max, and when territory triangles apply, <c>base + bonus</c>).
    /// Client / hybrid presentation only — reads replicated <see cref="PlanetState"/> and the
    /// published connection graph; never drives sim. Paired with <see cref="WorldBodyVisualApplier"/>
    /// (adds this component) and <see cref="PlanetPopulationMath"/> for cap formulas.
    /// </summary>
    public class PlanetWorldStatsLabel : MonoBehaviour
    {
        /// <summary>Last painted population — skip TMP writes when unchanged.</summary>
        int _cachedPopulation = int.MinValue;
        int _cachedBaseMax = int.MinValue;
        int _cachedBonusAmount = int.MinValue;
        TeamId _cachedTeam;
        int _cachedFamilyConfigIndex = int.MinValue;
        bool _cachedIsHomePlanet;
        bool _hasCachedPaint;
        string _cachedTitle;
        bool _legacyIconRemoved;
        /// <summary>
        /// True after label TMP children are wired and materials applied once.
        /// [TITAN-ORBIT] EnsureLabel used to re-enter TryRecoverExistingLabel every LateUpdate and
        /// call fontMaterial (TMP instance alloc) ×3 per planet → ~16KB GC (Profiler frame 2224).
        /// </summary>
        bool _labelReady;
        /// <summary>[UNITY] Sorting order so planet text draws above world meshes.</summary>
        const int TextSortingOrder = 5001;

        /// <summary>Large font for the live population count (top line).</summary>
        const float CurrentFontSize = 36f;

        /// <summary>Smaller font for the capacity line under current (base, or base + bonus).</summary>
        const float MaxFontSize = CurrentFontSize * (21f / 33f);

        /// <summary>Ship family title uses the same size as the capacity line.</summary>
        const float TitleFontSize = MaxFontSize;

        /// <summary>Local-space gap between family title and the population stack.</summary>
        const float TitleGapLocal = 2f;

        /// <summary>Local-space gap between current and capacity lines.</summary>
        const float ValueLineGapLocal = 0.5f;

        /// <summary>TMP outline width for readability over busy planet textures.</summary>
        const float OutlineWidth = 0.2f;

        /// <summary>TMP face dilate paired with outline so glyphs stay solid.</summary>
        const float FaceDilate = 0.12f;

        /// <summary>Capacity-line alpha vs full team color (current stays opaque).</summary>
        const float MaxLineAlpha = 0.6f;

        /// <summary>Slightly brighter alpha on the <c>+ bonus</c> span so the extra capacity reads as a boost.</summary>
        const float BonusSpanAlpha = 0.9f;

        static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;

        /// <summary>[TITAN-ORBIT] Stable planet id from <see cref="PlanetState.PlanetId"/> — set by Configure.</summary>
        [SerializeField] int planetId;

        Transform _labelRoot;
        TextMeshPro _titleText;
        StatRow _populationRow;

        static PlanetShipFamilyConfig _shipFamilyConfig;

        /// <summary>One vertical stack: current people on top, capacity (max) underneath.</summary>
        struct StatRow
        {
            public Transform Root;
            public TextMeshPro CurrentText;
            public TextMeshPro MaxText;
        }

        /// <summary>
        /// Binds this label to a planet id, builds missing TMP children, and refreshes once.
        /// Called from <see cref="WorldBodyVisualApplier"/> when the hybrid planet proxy spawns.
        /// </summary>
        /// <param name="id">Stable <see cref="PlanetState.PlanetId"/> for ECS lookups.</param>
        public void Configure(int id)
        {
            // --- Bind id and build / refresh label ---
            planetId = id;
            EnsureLabel();
            Refresh();
            ApplyLayout();
        }

        /// <summary>Creates the label hierarchy once, or recovers children after domain reload / reparent.</summary>
        void EnsureLabel()
        {
            // --- Already ready — skip Find / fontMaterial (hot LateUpdate path) ---
            if (_labelReady &&
                _labelRoot != null &&
                _titleText != null &&
                _populationRow.CurrentText != null &&
                _populationRow.MaxText != null)
                return;

            // --- Ensure setup ---
            if (TryRecoverExistingLabel())
            {
                _labelReady = true;
                return;
            }

            CleanupLegacyLabels();

            _labelRoot = CreateLabelRoot("PlanetStatsLabel", transform);
            _titleText = CreateValueText(_labelRoot, "FamilyTitle", TitleFontSize, Color.white);
            _populationRow = CreatePopulationRow(_labelRoot, "PopulationRow");

            KeepLabelOnPlanetRoot();
            _labelReady = true;
        }

        /// <summary>
        /// Re-wires references if PlanetStatsLabel already exists under this planet (Play Mode recompile).
        /// </summary>
        /// <returns>True when title + population TMP children were found and materials applied.</returns>
        bool TryRecoverExistingLabel()
        {
            // --- Attempt resolution ---
            if (_labelRoot == null)
            {
                Transform existing = transform.Find("PlanetStatsLabel");
                if (existing != null)
                    _labelRoot = existing;
            }

            if (_labelRoot == null)
                return false;

            if (_titleText == null)
                _titleText = _labelRoot.Find("FamilyTitle")?.GetComponent<TextMeshPro>();

            if (_populationRow.Root == null)
            {
                Transform row = _labelRoot.Find("PopulationRow");
                if (row != null)
                {
                    _populationRow.Root = row;
                    _populationRow.CurrentText = row.Find("Current")?.GetComponent<TextMeshPro>();
                    _populationRow.MaxText = row.Find("Max")?.GetComponent<TextMeshPro>();
                    RemoveLegacyPopulationIcon(row);
                }
            }

            if (_titleText == null || _populationRow.CurrentText == null || _populationRow.MaxText == null)
                return false;

            // Capacity line uses rich text for "base + bonus" coloring.
            _populationRow.MaxText.richText = true;

            ApplyReadableTextMaterial(_titleText);
            ApplyReadableTextMaterial(_populationRow.CurrentText);
            ApplyReadableTextMaterial(_populationRow.MaxText);
            KeepLabelOnPlanetRoot();
            return true;
        }

        /// <summary>Destroys old single-line PopulationText / previous PlanetStatsLabel before rebuilding.</summary>
        void CleanupLegacyLabels()
        {
            // --- CleanupLegacyLabels ---
            var legacy = transform.Find("PopulationText");
            if (legacy != null)
                Destroy(legacy.gameObject);

            if (_labelRoot != null)
            {
                Destroy(_labelRoot.gameObject);
                _labelRoot = null;
                _titleText = null;
                _populationRow = default;
            }
        }

        /// <summary>
        /// Registers the label with <see cref="PlanetSpinVisualProxy"/> so spin does not rotate the text.
        /// </summary>
        void KeepLabelOnPlanetRoot()
        {
            // --- KeepLabelOnPlanetRoot ---
            if (_labelRoot == null)
                return;

            var spin = GetComponent<PlanetSpinVisualProxy>();
            if (spin != null)
                spin.KeepOnPlanetRoot(_labelRoot);
        }

        /// <summary>Creates the flat billboard root (rotated for top-down camera, flipped Y for TMP).</summary>
        static Transform CreateLabelRoot(string name, Transform parent)
        {
            // --- Create instance ---
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            go.transform.localScale = new Vector3(
                WorldBodyLabelLayout.TextWorldScale,
                -WorldBodyLabelLayout.TextWorldScale,
                WorldBodyLabelLayout.TextWorldScale);
            go.transform.localPosition = Vector3.zero;
            return go.transform;
        }

        /// <summary>Removes the old people icon if a prior build left one under PopulationRow.</summary>
        static void RemoveLegacyPopulationIcon(Transform populationRow)
        {
            // --- RemoveLegacyPopulationIcon ---
            if (populationRow == null)
                return;

            Transform icon = populationRow.Find("Icon");
            if (icon != null)
                Object.Destroy(icon.gameObject);
        }

        /// <summary>Builds Current (large) + Max (capacity) TMP children under one row root.</summary>
        StatRow CreatePopulationRow(Transform parent, string rowName)
        {
            // --- Create instance ---
            var rowGo = new GameObject(rowName);
            rowGo.transform.SetParent(parent, false);

            var currentText = CreateValueText(rowGo.transform, "Current", CurrentFontSize, Color.white);
            var maxText = CreateValueText(rowGo.transform, "Max", MaxFontSize, Color.white);
            // [TITAN-ORBIT] Rich text lets "base + bonus" tint the boost span without a third TMP.
            maxText.richText = true;

            return new StatRow
            {
                Root = rowGo.transform,
                CurrentText = currentText,
                MaxText = maxText,
            };
        }

        /// <summary>Creates a centered bold TextMeshPro child for title or population digits.</summary>
        static TextMeshPro CreateValueText(Transform parent, string name, float fontSize, Color color)
        {
            // --- Create instance ---
            var textGo = new GameObject(name);
            textGo.transform.SetParent(parent, false);
            var tmp = textGo.AddComponent<TextMeshPro>();
            tmp.font = ResolveFont();
            tmp.fontSize = fontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.verticalAlignment = VerticalAlignmentOptions.Middle;
            tmp.enableWordWrapping = false;
            tmp.richText = false;
            tmp.color = color;
            ApplyReadableTextMaterial(tmp);
            return tmp;
        }

        /// <summary>Snaps the label root to the planet surface via shared world-body layout helpers.</summary>
        void ApplyLayout()
        {
            // --- Apply changes ---
            if (_labelRoot == null)
                return;

            KeepLabelOnPlanetRoot();
            WorldBodyLabelLayout.ApplySnugPlanetLabel(_labelRoot, transform);
        }

        /// <summary>Stacks current above capacity inside the population row (local Y).</summary>
        static void LayoutStatRow(ref StatRow row)
        {
            // --- LayoutStatRow ---
            if (row.CurrentText == null || row.MaxText == null)
                return;

            row.CurrentText.fontSize = CurrentFontSize;
            row.MaxText.fontSize = MaxFontSize;
            row.CurrentText.fontStyle = FontStyles.Bold;
            row.MaxText.fontStyle = FontStyles.Bold;
            row.CurrentText.ForceMeshUpdate();
            row.MaxText.ForceMeshUpdate();

            float currentHeight = row.CurrentText.preferredHeight;
            float maxHeight = row.MaxText.preferredHeight;
            float textHeight = currentHeight + ValueLineGapLocal + maxHeight;
            float stackTop = textHeight * 0.5f;

            row.CurrentText.transform.localPosition = new Vector3(
                0f,
                stackTop - currentHeight * 0.5f,
                0f);
            row.MaxText.transform.localPosition = new Vector3(
                0f,
                -stackTop + maxHeight * 0.5f,
                0f);
        }

        /// <summary>Preferred height of the current + gap + capacity stack.</summary>
        static float GetStatRowHeight(StatRow row)
        {
            // --- Compute value ---
            if (row.CurrentText == null || row.MaxText == null)
                return 0f;

            return row.CurrentText.preferredHeight + ValueLineGapLocal + row.MaxText.preferredHeight;
        }

        /// <summary>
        /// Centers title (optional) and population row as one block on the planet label root.
        /// </summary>
        /// <param name="showTitle">False when this planet has no ship family name.</param>
        void LayoutLabelBlock(bool showTitle)
        {
            // --- LayoutLabelBlock ---
            if (_titleText == null)
                return;

            LayoutStatRow(ref _populationRow);

            float titleHeight = 0f;
            if (showTitle)
            {
                _titleText.fontSize = TitleFontSize;
                _titleText.ForceMeshUpdate();
                titleHeight = _titleText.preferredHeight;
            }

            float titleGap = showTitle ? TitleGapLocal : 0f;
            float populationHeight = GetStatRowHeight(_populationRow);
            float totalHeight = populationHeight + (showTitle ? titleGap + titleHeight : 0f);

            // Center the whole block on the anchor point.
            _populationRow.Root.localPosition = new Vector3(
                0f,
                -totalHeight * 0.5f + populationHeight * 0.5f,
                0f);
            if (showTitle)
            {
                _titleText.fontStyle = FontStyles.Bold;
                _titleText.transform.localPosition = new Vector3(
                    0f,
                    totalHeight * 0.5f - titleHeight * 0.5f,
                    0f);
            }
        }

        /// <summary>Resolves TMP default font, then project fallback assets.</summary>
        static TMP_FontAsset ResolveFont()
        {
            // --- Resolve value ---
            if (TMP_Settings.defaultFontAsset != null)
                return TMP_Settings.defaultFontAsset;

            var fallback = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF - Fallback");
            if (fallback != null)
                return fallback;

#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
                "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Fallback.asset");
#else
            return null;
#endif
        }

        /// <summary>Enables outline + dilate and pushes the material into the Overlay queue.</summary>
        static void ApplyReadableTextMaterial(TMP_Text text)
        {
            // --- Apply changes ---
            if (text == null)
                return;

            Material mat = text.fontMaterial;
            if (mat == null)
                return;

            mat.EnableKeyword("OUTLINE_ON");
            if (mat.HasProperty("_OutlineColor"))
                mat.SetColor("_OutlineColor", new Color(1f, 1f, 1f, 0.92f));
            if (mat.HasProperty("_OutlineWidth"))
                mat.SetFloat("_OutlineWidth", OutlineWidth);
            if (mat.HasProperty("_OutlineSoftness"))
                mat.SetFloat("_OutlineSoftness", 0.04f);
            if (mat.HasProperty("_FaceDilate"))
                mat.SetFloat("_FaceDilate", FaceDilate);
            mat.renderQueue = RenderQueueOverlay;

            var renderer = text.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sortingOrder = TextSortingOrder;
        }

        /// <summary>Returns <paramref name="color"/> with a replaced alpha channel.</summary>
        static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        /// <summary>Lazy-loads the ship-family ScriptableObject used for planet title names.</summary>
        static PlanetShipFamilyConfig ShipFamilyConfig
        {
            get
            {
                if (_shipFamilyConfig != null)
                    return _shipFamilyConfig;

                _shipFamilyConfig = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
                return _shipFamilyConfig;
            }
        }

        /// <summary>
        /// Resolves the display title for this planet's ship family (designer name or camel-split id).
        /// </summary>
        static string ResolveShipFamilyTitle(in PlanetState state)
        {
            // --- Resolve value ---
            var config = ShipFamilyConfig;
            if (config == null)
                return string.Empty;

            var entry = config.GetFamilyForPlanet(
                state.PlanetId,
                state.IsHomePlanet,
                state.ShipFamilyConfigIndex);
            if (entry == null)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(entry.familyName))
                return entry.familyName.Trim();

            string familyId = entry.shipFamilyDefinition != null ? entry.shipFamilyDefinition.familyId : null;
            if (string.IsNullOrWhiteSpace(familyId))
                return string.Empty;

            return DisplayNameFormatting.SplitCamelCase(familyId.Trim());
        }

        /// <summary>
        /// Formats the capacity line under current population.
        /// No bonus → just the base max digits. With triangle bonus → <c>base + bonus</c>
        /// (no words — large current above / smaller capacity below already reads as now vs max).
        /// </summary>
        /// <param name="baseMax">Size × level cap with no territory boost.</param>
        /// <param name="bonusAmount">Extra people from connection triangles (≥ 0).</param>
        /// <param name="teamColor">Owning team tint for rich-text spans.</param>
        static string FormatCapacityLine(int baseMax, int bonusAmount, Color teamColor)
        {
            // --- Plain base only when no triangle boost ---
            if (bonusAmount <= 0)
                return baseMax.ToString();

            // --- "175 + 25": base dimmer, +bonus a bit brighter so the boost is obvious ---
            // [TITAN-ORBIT] No "bonus" / "current" labels — hierarchy + the plus sign teach the meaning.
            string baseHex = ColorUtility.ToHtmlStringRGBA(WithAlpha(teamColor, MaxLineAlpha));
            string bonusHex = ColorUtility.ToHtmlStringRGBA(WithAlpha(teamColor, BonusSpanAlpha));
            return $"<color=#{baseHex}>{baseMax}</color> <color=#{bonusHex}>+ {bonusAmount}</color>";
        }

        /// <summary>
        /// [UNITY] Per-frame refresh so population and triangle bonuses stay live.
        /// Dirty-checks TMP writes — assigning .text every frame rebuilt meshes + GC
        /// (~4ms / 93KB across labels, Profiler frame 41220).
        /// </summary>
        void LateUpdate()
        {
            // --- Per-frame refresh ---
            if (planetId == 0)
                return;

            bool dirty = Refresh();
            if (dirty)
                ApplyLayout();
        }

        /// <summary>
        /// Pulls planet state + triangle bonus, then writes title / current / capacity when dirty.
        /// </summary>
        /// <returns>True when TMP / layout need ApplyLayout.</returns>
        bool Refresh()
        {
            // --- Refresh ---
            if (planetId == 0)
                return false;

            EnsureLabel();
            if (_titleText == null || _populationRow.CurrentText == null || _populationRow.MaxText == null)
                return false;

            if (!_legacyIconRemoved)
            {
                RemoveLegacyPopulationIcon(_populationRow.Root);
                _legacyIconRemoved = true;
            }

            // [HYBRID] Replicated PlanetState — Population is the live count clients already trust.
            if (!EcsGameBridge.TryGetPlanetStateByPlanetId(planetId, out PlanetState state))
                return false;

            // Prefer ECS scale when available (proxy lossyScale can drift from sim).
            // Pose is frame-cached in EcsGameBridge — cheap after the first label this frame.
            float planetScale = transform.lossyScale.x;
            if (EcsGameBridge.TryGetPlanetPoseByPlanetId(planetId, out _, out float ecsScale, out _))
                planetScale = ecsScale;

            // --- Capacity: base from size/level, bonus from client connection triangles ---
            float bonusFraction = PlanetConnectionGraphCache.GetStackedConnectionBonusFraction(planetId);
            PlanetPopulationMath.GetMaxPopulationBreakdown(
                planetScale,
                state.PlanetLevel,
                bonusFraction,
                out int baseMax,
                out int bonusAmount);

            // --- Dirty check BEFORE ResolveShipFamilyTitle ---
            // [TITAN-ORBIT] Resolve used Trim()/SplitCamelCase every LateUpdate × N planets → ~15KB GC
            // (Profiler frame 5199). Family title only depends on id/config/home — not live population.
            if (_hasCachedPaint &&
                _cachedPopulation == state.Population &&
                _cachedBaseMax == baseMax &&
                _cachedBonusAmount == bonusAmount &&
                _cachedTeam == state.Ownership &&
                _cachedFamilyConfigIndex == state.ShipFamilyConfigIndex &&
                _cachedIsHomePlanet == state.IsHomePlanet)
            {
                return false;
            }

            string familyTitle = ResolveShipFamilyTitle(state);
            bool hasTitle = !string.IsNullOrEmpty(familyTitle);

            _hasCachedPaint = true;
            _cachedPopulation = state.Population;
            _cachedBaseMax = baseMax;
            _cachedBonusAmount = bonusAmount;
            _cachedTeam = state.Ownership;
            _cachedFamilyConfigIndex = state.ShipFamilyConfigIndex;
            _cachedIsHomePlanet = state.IsHomePlanet;
            _cachedTitle = familyTitle;

            Color teamColor = state.Ownership.ToColor();

            _titleText.gameObject.SetActive(hasTitle);
            _titleText.text = hasTitle ? familyTitle : string.Empty;
            _titleText.color = teamColor;

            _populationRow.CurrentText.text = state.Population.ToString();
            _populationRow.CurrentText.color = teamColor;

            _populationRow.MaxText.richText = true;
            _populationRow.MaxText.text = FormatCapacityLine(baseMax, bonusAmount, teamColor);
            _populationRow.MaxText.color = WithAlpha(teamColor, MaxLineAlpha);

            LayoutLabelBlock(hasTitle);
            return true;
        }
    }
}
