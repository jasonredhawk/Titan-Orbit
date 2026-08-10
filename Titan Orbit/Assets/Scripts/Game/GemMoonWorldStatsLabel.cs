using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>World-space gem count / max above the orbiting gem moon.</summary>
    public class GemMoonWorldStatsLabel : MonoBehaviour
    {
        const string GemsColorHex = "#FF3333";
        const string GemsMaxColorHex = "#FF333399";
        const string ShieldColorHex = "#40F2FF";
        const string ShieldMaxColorHex = "#40F2FF99";
        const int TextSortingOrder = 5001;
        const int IconSortingOrder = 5000;
        /// <summary>Fallback moon label scale before planet size is known (unit-scale roots).</summary>
        const float LabelWorldScaleFallback = 0.24f;
        const float CurrentFontSize = 33f;
        const float MaxFontSize = 21f;
        const float StatBlockGapLocal = 2f;
        const float ValueLineGapLocal = 0.5f;
        const float IconGapLocal = 2.5f;
        const float IconHeightOverFontSize = 0.11f;
        const float OutlineWidth = 0.14f;
        static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;

        [SerializeField] int planetId;
        float _moonLocalRadius = 0.25f;

        Transform _labelRoot;
        StatRow _gemRow;
        StatRow _shieldRow;

        int _cachedCurrentGems = int.MinValue;
        int _cachedMaxGems = int.MinValue;
        int _cachedCurrentShield = int.MinValue;
        int _cachedMaxShield = int.MinValue;

        static readonly Color GemsColor = ParseHexColor(GemsColorHex);
        static readonly Color GemsMaxColor = ParseHexColor(GemsMaxColorHex);
        static readonly Color ShieldColor = ParseHexColor(ShieldColorHex);
        static readonly Color ShieldMaxColor = ParseHexColor(ShieldMaxColorHex);

        struct StatRow
        {
            public Transform Root;
            public SpriteRenderer Icon;
            public TextMeshPro CurrentText;
            public TextMeshPro MaxText;
        }

        public void Configure(int id, float moonLocalRadius)
        {
            // --- Configure ---
            planetId = id;
            _moonLocalRadius = Mathf.Max(0.02f, moonLocalRadius);
            EnsureLabel();
            ApplyLayout();
            Refresh();
        }

        void EnsureLabel()
        {
            // --- Ensure setup ---
            if (_labelRoot != null)
                return;

            var legacyCanvas = transform.Find("GemMoonStatsCanvas");
            if (legacyCanvas != null)
                Destroy(legacyCanvas.gameObject);

            var legacyMax = transform.Find("GemsMax");
            if (legacyMax != null)
                Destroy(legacyMax.gameObject);

            var legacyValue = transform.Find("GemsValue");
            if (legacyValue != null)
                Destroy(legacyValue.gameObject);

            var legacyLabel = transform.Find("GemsLabel");
            if (legacyLabel != null)
                Destroy(legacyLabel.gameObject);

            _labelRoot = CreateLabelRoot("GemsLabel", transform);
            _gemRow = CreateStatRow(_labelRoot, "GemRow", WorldStatLabelIcons.Gem, ParseHexColor(GemsColorHex));
            _shieldRow = CreateStatRow(_labelRoot, "ShieldRow", WorldStatLabelIcons.Shield, ParseHexColor(ShieldColorHex));
        }

        static Transform CreateLabelRoot(string name, Transform parent)
        {
            // --- Create instance ---
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            go.transform.localScale = new Vector3(
                LabelWorldScaleFallback, -LabelWorldScaleFallback, LabelWorldScaleFallback);
            go.transform.localPosition = Vector3.zero;
            return go.transform;
        }

        StatRow CreateStatRow(Transform parent, string rowName, Sprite iconSprite, Color iconColor)
        {
            // --- Create instance ---
            var rowGo = new GameObject(rowName);
            rowGo.transform.SetParent(parent, false);

            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(rowGo.transform, false);
            var iconRenderer = iconGo.AddComponent<SpriteRenderer>();
            iconRenderer.sprite = iconSprite;
            iconRenderer.color = iconColor;
            iconRenderer.sortingOrder = IconSortingOrder;
            iconRenderer.enabled = iconSprite != null;

            var currentText = CreateValueText(rowGo.transform, "Current", CurrentFontSize, Color.white);
            var maxText = CreateValueText(rowGo.transform, "Max", MaxFontSize, Color.white);

            if (iconSprite != null)
                ApplyIconScale(iconRenderer, iconSprite, CurrentFontSize);

            return new StatRow
            {
                Root = rowGo.transform,
                Icon = iconRenderer,
                CurrentText = currentText,
                MaxText = maxText,
            };
        }

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
            tmp.enableWordWrapping = false;
            tmp.richText = false;
            tmp.color = color;
            ApplyReadableTextMaterial(tmp);
            return tmp;
        }

        void ApplyLayout()
        {
            // --- Apply changes ---
            if (_labelRoot == null)
                return;

            // --- World text scale under unit-scale planet root ---
            // Legacy moon labels inherited planet LocalTransform.Scale; restore that size.
            float planetSize = 10f;
            if (EcsGameBridge.TryGetPlanetPoseByPlanetId(planetId, out _, out float ecsScale, out _))
                planetSize = ecsScale;
            float s = WorldBodyLabelLayout.GetReadableMoonLabelWorldScale(planetSize);
            _labelRoot.localScale = new Vector3(s, -s, s);

            WorldBodyLabelLayout.ApplySnugMoonLabel(_labelRoot, transform, _moonLocalRadius);
        }

        static void ApplyIconScale(SpriteRenderer iconRenderer, Sprite iconSprite, float fontSize)
        {
            // --- Apply changes ---
            if (iconRenderer == null || iconSprite == null)
                return;

            float iconHeight = fontSize * IconHeightOverFontSize;
            float spriteHeight = Mathf.Max(0.001f, iconSprite.bounds.size.y);
            iconRenderer.transform.localScale = Vector3.one * (iconHeight / spriteHeight);
        }

        static void LayoutStatRow(ref StatRow row)
        {
            // --- LayoutStatRow ---
            if (row.CurrentText == null || row.MaxText == null)
                return;

            row.CurrentText.fontSize = CurrentFontSize;
            row.MaxText.fontSize = MaxFontSize;

            if (row.Icon != null && row.Icon.enabled && row.Icon.sprite != null)
                ApplyIconScale(row.Icon, row.Icon.sprite, row.CurrentText.fontSize);

            row.CurrentText.ForceMeshUpdate();
            row.MaxText.ForceMeshUpdate();

            float textWidth = Mathf.Max(row.CurrentText.preferredWidth, row.MaxText.preferredWidth);
            float currentHeight = row.CurrentText.preferredHeight;
            float maxHeight = row.MaxText.preferredHeight;
            float textHeight = currentHeight + ValueLineGapLocal + maxHeight;

            float iconWidth = 0f;
            if (row.Icon != null && row.Icon.enabled && row.Icon.sprite != null)
                iconWidth = row.Icon.transform.localScale.x * row.Icon.sprite.bounds.size.x;

            float gap = iconWidth > 0f ? IconGapLocal : 0f;
            float totalWidth = iconWidth + gap + textWidth;
            float rowLeft = -totalWidth * 0.5f;
            float textCenterX = rowLeft + iconWidth + gap + textWidth * 0.5f;

            float stackTop = textHeight * 0.5f;
            row.CurrentText.transform.localPosition = new Vector3(
                textCenterX,
                stackTop - currentHeight * 0.5f,
                0f);
            row.MaxText.transform.localPosition = new Vector3(
                textCenterX,
                -stackTop + maxHeight * 0.5f,
                0f);

            if (row.Icon != null && row.Icon.enabled && row.Icon.sprite != null)
                row.Icon.transform.localPosition = new Vector3(rowLeft + iconWidth * 0.5f, 0f, 0f);
        }

        static float GetStatRowHeight(StatRow row)
        {
            // --- Compute value ---
            if (row.CurrentText == null || row.MaxText == null)
                return 0f;

            return row.CurrentText.preferredHeight + ValueLineGapLocal + row.MaxText.preferredHeight;
        }

        static void LayoutLabelBlock(ref StatRow gemRow, ref StatRow shieldRow)
        {
            // --- LayoutLabelBlock ---
            LayoutStatRow(ref gemRow);
            LayoutStatRow(ref shieldRow);

            float gemHeight = GetStatRowHeight(gemRow);
            float shieldHeight = GetStatRowHeight(shieldRow);
            float totalHeight = gemHeight + StatBlockGapLocal + shieldHeight;

            gemRow.Root.localPosition = new Vector3(0f, (totalHeight - gemHeight) * 0.5f, 0f);
            shieldRow.Root.localPosition = new Vector3(0f, -(totalHeight - shieldHeight) * 0.5f, 0f);
        }

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
                mat.SetFloat("_OutlineSoftness", 0.06f);
            mat.renderQueue = RenderQueueOverlay;

            var renderer = text.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sortingOrder = TextSortingOrder;
        }

        static Color ParseHexColor(string hex)
        {
            return ColorUtility.TryParseHtmlString(hex, out Color color) ? color : Color.white;
        }

        void LateUpdate()
        {
            // Dirty Refresh skips ApplyLayout when gem/shield digits unchanged.
            if (Refresh())
                ApplyLayout();
        }

        /// <summary>
        /// Updates gem/shield TMP only when values change.
        /// [TITAN-ORBIT] Per-frame ParseHexColor + .text on every moon was ~1.4ms (Profiler 41220).
        /// </summary>
        /// <returns>True when layout should run.</returns>
        bool Refresh()
        {
            // --- Refresh ---
            if (planetId == 0)
                return false;

            EnsureLabel();
            if (_gemRow.CurrentText == null || _shieldRow.CurrentText == null)
                return false;

            if (!EcsGameBridge.TryGetPlanetStateByPlanetId(planetId, out PlanetState state))
                return false;

            int currentGems = Mathf.RoundToInt(state.CurrentGems);
            int maxGems = Mathf.RoundToInt(PlanetEconomyMath.GetMaxGemsForLevel(state.PlanetLevel));

            int currentShield;
            int maxShield;
            if (EcsGameBridge.TryGetPlanetGemMoonStateByPlanetId(planetId, out PlanetGemMoonState moonState))
            {
                currentShield = Mathf.RoundToInt(moonState.CurrentShield);
                maxShield = Mathf.RoundToInt(moonState.MaxShield);
            }
            else
            {
                maxShield = Mathf.RoundToInt(PlanetGemMoonMath.GetMaxShieldForLevel(state.PlanetLevel));
                currentShield = maxShield;
            }

            if (_cachedCurrentGems == currentGems &&
                _cachedMaxGems == maxGems &&
                _cachedCurrentShield == currentShield &&
                _cachedMaxShield == maxShield)
            {
                return false;
            }

            _cachedCurrentGems = currentGems;
            _cachedMaxGems = maxGems;
            _cachedCurrentShield = currentShield;
            _cachedMaxShield = maxShield;

            _gemRow.CurrentText.text = currentGems.ToString();
            _gemRow.MaxText.text = maxGems.ToString();
            _gemRow.CurrentText.color = GemsColor;
            _gemRow.MaxText.color = GemsMaxColor;

            _shieldRow.CurrentText.text = currentShield.ToString();
            _shieldRow.MaxText.text = maxShield.ToString();
            _shieldRow.CurrentText.color = ShieldColor;
            _shieldRow.MaxText.color = ShieldMaxColor;

            LayoutLabelBlock(ref _gemRow, ref _shieldRow);
            return true;
        }
    }
}
