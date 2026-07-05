using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>World-space ship family title and population above the planet body.</summary>
    public class PlanetWorldStatsLabel : MonoBehaviour
    {
        const int TextSortingOrder = 5001;
        const float CurrentFontSize = 36f;
        const float MaxFontSize = CurrentFontSize * (21f / 33f);
        const float TitleFontSize = MaxFontSize;
        const float TitleGapLocal = 2f;
        const float ValueLineGapLocal = 0.5f;
        const float OutlineWidth = 0.2f;
        const float FaceDilate = 0.12f;
        static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;

        [SerializeField] int planetId;

        Transform _labelRoot;
        TextMeshPro _titleText;
        StatRow _populationRow;

        static PlanetShipFamilyConfig _shipFamilyConfig;

        struct StatRow
        {
            public Transform Root;
            public TextMeshPro CurrentText;
            public TextMeshPro MaxText;
        }

        public void Configure(int id)
        {
            planetId = id;
            EnsureLabel();
            Refresh();
            ApplyLayout();
        }

        void EnsureLabel()
        {
            if (TryRecoverExistingLabel())
                return;

            CleanupLegacyLabels();

            _labelRoot = CreateLabelRoot("PlanetStatsLabel", transform);
            _titleText = CreateValueText(_labelRoot, "FamilyTitle", TitleFontSize, Color.white);
            _populationRow = CreatePopulationRow(_labelRoot, "PopulationRow");

            KeepLabelOnPlanetRoot();
        }

        bool TryRecoverExistingLabel()
        {
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

            ApplyReadableTextMaterial(_titleText);
            ApplyReadableTextMaterial(_populationRow.CurrentText);
            ApplyReadableTextMaterial(_populationRow.MaxText);
            KeepLabelOnPlanetRoot();
            return true;
        }

        void CleanupLegacyLabels()
        {
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

        void KeepLabelOnPlanetRoot()
        {
            if (_labelRoot == null)
                return;

            var spin = GetComponent<PlanetSpinVisualProxy>();
            if (spin != null)
                spin.KeepOnPlanetRoot(_labelRoot);
        }

        static Transform CreateLabelRoot(string name, Transform parent)
        {
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

        static void RemoveLegacyPopulationIcon(Transform populationRow)
        {
            if (populationRow == null)
                return;

            Transform icon = populationRow.Find("Icon");
            if (icon != null)
                Object.Destroy(icon.gameObject);
        }

        StatRow CreatePopulationRow(Transform parent, string rowName)
        {
            var rowGo = new GameObject(rowName);
            rowGo.transform.SetParent(parent, false);

            var currentText = CreateValueText(rowGo.transform, "Current", CurrentFontSize, Color.white);
            var maxText = CreateValueText(rowGo.transform, "Max", MaxFontSize, Color.white);

            return new StatRow
            {
                Root = rowGo.transform,
                CurrentText = currentText,
                MaxText = maxText,
            };
        }

        static TextMeshPro CreateValueText(Transform parent, string name, float fontSize, Color color)
        {
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

        void ApplyLayout()
        {
            if (_labelRoot == null)
                return;

            KeepLabelOnPlanetRoot();
            WorldBodyLabelLayout.ApplySnugPlanetLabel(_labelRoot, transform);
        }

        static void LayoutStatRow(ref StatRow row)
        {
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

        static float GetStatRowHeight(StatRow row)
        {
            if (row.CurrentText == null || row.MaxText == null)
                return 0f;

            return row.CurrentText.preferredHeight + ValueLineGapLocal + row.MaxText.preferredHeight;
        }

        void LayoutLabelBlock(bool showTitle)
        {
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

        static TMP_FontAsset ResolveFont()
        {
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

        static void ApplyReadableTextMaterial(TMP_Text text)
        {
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

        static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        static PlanetShipFamilyConfig ShipFamilyConfig
        {
            get
            {
                if (_shipFamilyConfig != null)
                    return _shipFamilyConfig;

                _shipFamilyConfig = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
                if (_shipFamilyConfig == null)
                    _shipFamilyConfig = Resources.Load<PlanetShipFamilyConfig>("Data/PlanetShipFamilyConfig");
                return _shipFamilyConfig;
            }
        }

        static string ResolveShipFamilyTitle(in PlanetState state)
        {
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

        void LateUpdate()
        {
            if (planetId == 0)
                return;

            Refresh();
            ApplyLayout();
        }

        void Refresh()
        {
            if (planetId == 0)
                return;

            EnsureLabel();
            if (_titleText == null || _populationRow.CurrentText == null)
                return;

            RemoveLegacyPopulationIcon(_populationRow.Root);

            if (!EcsGameBridge.TryGetPlanetStateByPlanetId(planetId, out PlanetState state))
                return;

            float planetScale = transform.lossyScale.x;
            if (EcsGameBridge.TryGetPlanetPoseByPlanetId(planetId, out _, out float ecsScale, out _))
                planetScale = ecsScale;

            int maxPopulation = PlanetPopulationMath.GetMaxPopulation(planetScale, state.PlanetLevel);
            Color teamColor = state.Ownership.ToColor();

            string familyTitle = ResolveShipFamilyTitle(state);
            bool hasTitle = !string.IsNullOrEmpty(familyTitle);
            _titleText.gameObject.SetActive(hasTitle);
            _titleText.text = hasTitle ? familyTitle : string.Empty;
            _titleText.color = teamColor;

            _populationRow.CurrentText.text = state.Population.ToString();
            _populationRow.MaxText.text = maxPopulation.ToString();
            _populationRow.CurrentText.color = teamColor;
            _populationRow.MaxText.color = WithAlpha(teamColor, 0.6f);

            LayoutLabelBlock(hasTitle);
        }
    }
}
