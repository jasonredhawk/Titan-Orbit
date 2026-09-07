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
    /// World-space label floating above a planet body: ship family name (title), optional
    /// capture-contributor name, plus population.
    /// Layout reads top-to-bottom as family title, a small "Captured by" caption, the
    /// player who delivered the most troops during capture, then <b>current people</b>,
    /// then the population <b>capacity</b>
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
        int _cachedContributorNetworkId = int.MinValue;
        bool _hasCachedPaint;
        string _cachedTitle;
        string _cachedContributorName;
        bool _legacyIconRemoved;
        /// <summary>
        /// True after label TMP children are wired and materials applied once.
        /// [TITAN-ORBIT] EnsureLabel used to re-enter TryRecoverExistingLabel every LateUpdate and
        /// call fontMaterial (TMP instance alloc) ×3 per planet → ~16KB GC (Profiler frame 2224).
        /// </summary>
        bool _labelReady;
        float _cachedLayoutPlanetSize = float.NaN;
        /// <summary>[UNITY] Sorting order so planet text draws above world meshes.</summary>
        const int TextSortingOrder = 5001;

        /// <summary>Large font for the live population count (top line).</summary>
        const float CurrentFontSize = 36f;

        /// <summary>Smaller font for the capacity line under current (base, or base + bonus).</summary>
        const float MaxFontSize = CurrentFontSize * (21f / 33f);

        /// <summary>Ship family title uses the same size as the capacity line.</summary>
        const float TitleFontSize = MaxFontSize;

        /// <summary>Player name on the capture credit — smaller than the family title.</summary>
        const float ContributorNameFontSize = TitleFontSize * 0.55f;

        /// <summary>"Captured by" caption — smaller than the player name underneath.</summary>
        const float CapturedByFontSize = ContributorNameFontSize * 0.7f;

        /// <summary>Local-space gap between family title and the population stack.</summary>
        const float TitleGapLocal = 2f;

        /// <summary>Local-space gap around the capture-contributor line.</summary>
        const float ContributorGapLocal = 0.35f;

        /// <summary>Player-name alpha vs full team color.</summary>
        const float ContributorAlpha = 0.85f;

        /// <summary>"Captured by" caption is a bit dimmer than the name underneath.</summary>
        const float CapturedByAlpha = 0.65f;

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
        CaptureCreditRow _captureCredit;
        StatRow _populationRow;

        static PlanetShipFamilyConfig _shipFamilyConfig;

        /// <summary>One vertical stack: current people on top, capacity (max) underneath.</summary>
        struct StatRow
        {
            public Transform Root;
            public TextMeshPro CurrentText;
            public TextMeshPro MaxText;
        }

        /// <summary>Capture credit: small "Captured by" caption over the player name.</summary>
        struct CaptureCreditRow
        {
            public Transform Root;
            public TextMeshPro CaptionText;
            public TextMeshPro NameText;
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
                _captureCredit.CaptionText != null &&
                _captureCredit.NameText != null &&
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
            _captureCredit = CreateCaptureCreditRow(_labelRoot, "CaptureCredit");
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

            if (_captureCredit.Root == null)
            {
                Transform credit = _labelRoot.Find("CaptureCredit");
                if (credit != null)
                {
                    _captureCredit.Root = credit;
                    _captureCredit.CaptionText = credit.Find("CapturedBy")?.GetComponent<TextMeshPro>();
                    _captureCredit.NameText = credit.Find("ContributorName")?.GetComponent<TextMeshPro>();
                }
            }

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

            RemoveLegacySingleLineContributor(_labelRoot);

            if (_captureCredit.CaptionText == null || _captureCredit.NameText == null)
                _captureCredit = CreateCaptureCreditRow(_labelRoot, "CaptureCredit");

            // Capacity line uses rich text for "base + bonus" coloring.
            _populationRow.MaxText.richText = true;

            ApplyReadableTextMaterial(_titleText);
            ApplyReadableTextMaterial(_captureCredit.CaptionText);
            ApplyReadableTextMaterial(_captureCredit.NameText);
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
                _captureCredit = default;
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

            // Spin lives on PlanetVisualBody — search children, not only this root.
            var spin = GetComponentInChildren<PlanetSpinVisualProxy>(true);
            if (spin != null)
                spin.KeepOnPlanetRoot(_labelRoot);
            else if (_labelRoot.parent != transform)
                _labelRoot.SetParent(transform, true);
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

        /// <summary>Drops the old single-line CaptureContributor TMP from an earlier credit layout.</summary>
        static void RemoveLegacySingleLineContributor(Transform labelRoot)
        {
            if (labelRoot == null)
                return;

            Transform legacy = labelRoot.Find("CaptureContributor");
            if (legacy != null)
                Object.Destroy(legacy.gameObject);
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

        /// <summary>Builds the two-line capture credit: "Captured by" over the player name.</summary>
        static CaptureCreditRow CreateCaptureCreditRow(Transform parent, string rowName)
        {
            Transform existing = parent.Find(rowName);
            if (existing != null)
                Object.Destroy(existing.gameObject);

            var rowGo = new GameObject(rowName);
            rowGo.transform.SetParent(parent, false);

            var caption = CreateValueText(rowGo.transform, "CapturedBy", CapturedByFontSize, Color.white);
            var name = CreateValueText(rowGo.transform, "ContributorName", ContributorNameFontSize, Color.white);
            caption.text = "Captured by";

            return new CaptureCreditRow
            {
                Root = rowGo.transform,
                CaptionText = caption,
                NameText = name,
            };
        }

        /// <summary>Stacks "Captured by" above the player name inside the credit row.</summary>
        static void LayoutCaptureCredit(ref CaptureCreditRow row)
        {
            if (row.CaptionText == null || row.NameText == null)
                return;

            row.CaptionText.fontSize = CapturedByFontSize;
            row.NameText.fontSize = ContributorNameFontSize;
            row.CaptionText.fontStyle = FontStyles.Bold;
            row.NameText.fontStyle = FontStyles.Bold;
            row.CaptionText.ForceMeshUpdate();
            row.NameText.ForceMeshUpdate();

            float captionHeight = row.CaptionText.preferredHeight;
            float nameHeight = row.NameText.preferredHeight;
            float textHeight = captionHeight + ContributorGapLocal + nameHeight;
            float stackTop = textHeight * 0.5f;

            row.CaptionText.transform.localPosition = new Vector3(
                0f,
                stackTop - captionHeight * 0.5f,
                0f);
            row.NameText.transform.localPosition = new Vector3(
                0f,
                -stackTop + nameHeight * 0.5f,
                0f);
        }

        /// <summary>Preferred height of the capture-credit stack.</summary>
        static float GetCaptureCreditHeight(CaptureCreditRow row)
        {
            if (row.CaptionText == null || row.NameText == null)
                return 0f;

            return row.CaptionText.preferredHeight + ContributorGapLocal + row.NameText.preferredHeight;
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

        /// <summary>
        /// Snaps the label to the planet surface and sets a readable world TMP scale.
        /// Unit-scale planet roots no longer enlarge children — scale from ECS diameter.
        /// </summary>
        void ApplyLayout()
        {
            // --- Apply changes ---
            if (_labelRoot == null)
                return;

            KeepLabelOnPlanetRoot();

            // --- World text scale (matches pre–PlanetVisualBody inherited look) ---
            float planetSize = PlanetVisualBody.ResolvePresentationSize(transform);
            if (EcsGameBridge.TryGetPlanetPoseByPlanetId(planetId, out _, out float ecsScale, out _))
                planetSize = ecsScale;
            float s = WorldBodyLabelLayout.GetReadablePlanetLabelWorldScale(planetSize);
            _labelRoot.localScale = new Vector3(s, -s, s);

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
        /// Centers title, capture-contributor, and population row as one block on the planet label.
        /// </summary>
        /// <param name="showTitle">False when this planet has no ship family name.</param>
        /// <param name="showContributor">False when this planet has no capture contributor.</param>
        void LayoutLabelBlock(bool showTitle, bool showContributor)
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

            float creditHeight = 0f;
            if (showContributor && _captureCredit.Root != null)
            {
                LayoutCaptureCredit(ref _captureCredit);
                creditHeight = GetCaptureCreditHeight(_captureCredit);
            }

            bool hasHeader = showTitle || showContributor;
            float nameGap = showTitle && showContributor ? ContributorGapLocal : 0f;
            float headerGap = hasHeader ? TitleGapLocal : 0f;
            float populationHeight = GetStatRowHeight(_populationRow);
            float headerHeight = (showTitle ? titleHeight : 0f)
                + (showContributor ? creditHeight : 0f)
                + nameGap
                + headerGap;
            float totalHeight = populationHeight + headerHeight;

            // Stack: family title + capture credit as one identity, then the population numbers.
            float cursor = totalHeight * 0.5f;
            if (showTitle)
            {
                _titleText.fontStyle = FontStyles.Bold;
                _titleText.transform.localPosition = new Vector3(
                    0f,
                    cursor - titleHeight * 0.5f,
                    0f);
                cursor -= titleHeight + nameGap;
            }

            if (showContributor && _captureCredit.Root != null)
            {
                _captureCredit.Root.localPosition = new Vector3(
                    0f,
                    cursor - creditHeight * 0.5f,
                    0f);
                cursor -= creditHeight + headerGap;
            }
            else if (showTitle)
            {
                cursor -= headerGap;
            }

            _populationRow.Root.localPosition = new Vector3(
                0f,
                cursor - populationHeight * 0.5f,
                0f);
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
        /// Same helper the minimap hover tip uses so both surfaces stay in sync.
        /// </summary>
        static string ResolveShipFamilyTitle(in PlanetState state)
        {
            // --- Resolve value ---
            var config = ShipFamilyConfig;
            if (config == null)
                return string.Empty;

            return config.GetPlanetDisplayName(
                state.PlanetId,
                state.IsHomePlanet,
                state.ShipFamilyConfigIndex);
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
        /// Layout/scale runs when text is dirty or the planet diameter changes — not every frame.
        /// </summary>
        void LateUpdate()
        {
            // --- Per-frame refresh ---
            if (planetId == 0)
                return;

            bool textDirty = Refresh();
            float planetSize = PlanetVisualBody.ResolvePresentationSize(transform);
            if (EcsGameBridge.TryGetPlanetPoseByPlanetId(planetId, out _, out float ecsScale, out _))
                planetSize = ecsScale;
            bool sizeDirty = float.IsNaN(_cachedLayoutPlanetSize) ||
                             Mathf.Abs(_cachedLayoutPlanetSize - planetSize) > 0.01f;
            if (textDirty || sizeDirty)
            {
                ApplyLayout();
                _cachedLayoutPlanetSize = planetSize;
            }
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
            if (_titleText == null ||
                _captureCredit.CaptionText == null ||
                _captureCredit.NameText == null ||
                _populationRow.CurrentText == null ||
                _populationRow.MaxText == null)
                return false;

            if (!_legacyIconRemoved)
            {
                RemoveLegacyPopulationIcon(_populationRow.Root);
                _legacyIconRemoved = true;
            }

            // [HYBRID] Replicated PlanetState — Population is the live count clients already trust.
            if (!EcsGameBridge.TryGetPlanetStateByPlanetId(planetId, out PlanetState state))
                return false;

            // Prefer ECS scale — unit-scale roots no longer carry diameter on lossyScale.
            float planetScale = PlanetVisualBody.ResolvePresentationSize(transform);
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

            // Roster cache first — avoids "Player N" string alloc every LateUpdate before announce.
            int contributorId = state.TopContributorNetworkId;
            string contributorName = string.Empty;
            if (contributorId > 0 &&
                !PlayerNameRosterCache.TryGet(contributorId, out contributorName))
            {
                if (_hasCachedPaint &&
                    _cachedContributorNetworkId == contributorId &&
                    !string.IsNullOrEmpty(_cachedContributorName))
                    contributorName = _cachedContributorName;
                else
                    contributorName = EcsGameBridge.GetCachedPlayerDisplayName(contributorId);
            }

            bool hasContributor = !string.IsNullOrEmpty(contributorName);

            // --- Dirty check BEFORE ResolveShipFamilyTitle ---
            // [TITAN-ORBIT] Resolve used Trim()/SplitCamelCase every LateUpdate × N planets → ~15KB GC
            // (Profiler frame 5199). Family title only depends on id/config/home — not live population.
            if (_hasCachedPaint &&
                _cachedPopulation == state.Population &&
                _cachedBaseMax == baseMax &&
                _cachedBonusAmount == bonusAmount &&
                _cachedTeam == state.Ownership &&
                _cachedFamilyConfigIndex == state.ShipFamilyConfigIndex &&
                _cachedIsHomePlanet == state.IsHomePlanet &&
                _cachedContributorNetworkId == contributorId &&
                _cachedContributorName == contributorName)
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
            _cachedContributorNetworkId = contributorId;
            _cachedTitle = familyTitle;
            _cachedContributorName = contributorName;

            Color teamColor = state.Ownership.ToColor();

            _titleText.gameObject.SetActive(hasTitle);
            _titleText.text = hasTitle ? familyTitle : string.Empty;
            _titleText.color = teamColor;

            if (_captureCredit.Root != null)
                _captureCredit.Root.gameObject.SetActive(hasContributor);
            _captureCredit.CaptionText.text = hasContributor ? "Captured by" : string.Empty;
            _captureCredit.CaptionText.color = WithAlpha(teamColor, CapturedByAlpha);
            _captureCredit.NameText.text = hasContributor ? contributorName : string.Empty;
            _captureCredit.NameText.color = WithAlpha(teamColor, ContributorAlpha);

            _populationRow.CurrentText.text = state.Population.ToString();
            _populationRow.CurrentText.color = teamColor;

            _populationRow.MaxText.richText = true;
            _populationRow.MaxText.text = FormatCapacityLine(baseMax, bonusAmount, teamColor);
            _populationRow.MaxText.color = WithAlpha(teamColor, MaxLineAlpha);

            LayoutLabelBlock(hasTitle, hasContributor);
            return true;
        }
    }
}
