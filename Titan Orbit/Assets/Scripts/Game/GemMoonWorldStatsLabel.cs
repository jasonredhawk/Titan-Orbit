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
        const float LabelWorldScale = 0.022f;
        const float LabelFontSize = 22f;
        const float RowSpacingLocal = 10f;
        const float IconGapLocal = 2.5f;
        const float IconHeightOverFontSize = 0.11f;
        const float OutlineWidth = 0.14f;
        static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;

        [SerializeField] int planetId;
        float _moonLocalRadius = 0.25f;

        Transform _labelRoot;
        StatRow _gemRow;
        StatRow _shieldRow;

        struct StatRow
        {
            public Transform Root;
            public SpriteRenderer Icon;
            public TextMeshPro Text;
        }

        public void Configure(int id, float moonLocalRadius)
        {
            planetId = id;
            _moonLocalRadius = Mathf.Max(0.02f, moonLocalRadius);
            EnsureLabel();
            ApplyLayout();
            Refresh();
        }

        void EnsureLabel()
        {
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
            _gemRow = CreateStatRow(_labelRoot, "GemRow", GemMoonLabelIcons.Gem, ParseHexColor(GemsColorHex));
            _shieldRow = CreateStatRow(_labelRoot, "ShieldRow", GemMoonLabelIcons.Shield, ParseHexColor(ShieldColorHex));
            _shieldRow.Root.localPosition = new Vector3(0f, -RowSpacingLocal * 0.5f, 0f);
            _gemRow.Root.localPosition = new Vector3(0f, RowSpacingLocal * 0.5f, 0f);
        }

        static Transform CreateLabelRoot(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            go.transform.localScale = new Vector3(LabelWorldScale, -LabelWorldScale, LabelWorldScale);
            go.transform.localPosition = Vector3.zero;
            return go.transform;
        }

        StatRow CreateStatRow(Transform parent, string rowName, Sprite iconSprite, Color iconColor)
        {
            var rowGo = new GameObject(rowName);
            rowGo.transform.SetParent(parent, false);

            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(rowGo.transform, false);
            var iconRenderer = iconGo.AddComponent<SpriteRenderer>();
            iconRenderer.sprite = iconSprite;
            iconRenderer.color = iconColor;
            iconRenderer.sortingOrder = IconSortingOrder;
            iconRenderer.enabled = iconSprite != null;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(rowGo.transform, false);
            var tmp = textGo.AddComponent<TextMeshPro>();
            tmp.font = ResolveFont();
            tmp.fontSize = LabelFontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Midline;
            tmp.enableWordWrapping = false;
            tmp.richText = true;
            tmp.color = Color.white;
            ApplyReadableTextMaterial(tmp);

            if (iconSprite != null)
                ApplyIconScale(iconRenderer, iconSprite, tmp.fontSize);

            return new StatRow
            {
                Root = rowGo.transform,
                Icon = iconRenderer,
                Text = tmp,
            };
        }

        void ApplyLayout()
        {
            if (_labelRoot == null)
                return;

            WorldBodyLabelLayout.ApplySnugMoonLabel(_labelRoot, transform, _moonLocalRadius);
        }

        static void ApplyIconScale(SpriteRenderer iconRenderer, Sprite iconSprite, float fontSize)
        {
            if (iconRenderer == null || iconSprite == null)
                return;

            float iconHeight = fontSize * IconHeightOverFontSize;
            float spriteHeight = Mathf.Max(0.001f, iconSprite.bounds.size.y);
            iconRenderer.transform.localScale = Vector3.one * (iconHeight / spriteHeight);
        }

        static void LayoutStatRow(ref StatRow row)
        {
            if (row.Text == null)
                return;

            if (row.Icon != null && row.Icon.enabled && row.Icon.sprite != null)
                ApplyIconScale(row.Icon, row.Icon.sprite, row.Text.fontSize);

            row.Text.ForceMeshUpdate();
            float textWidth = row.Text.preferredWidth;
            float iconWidth = 0f;
            if (row.Icon != null && row.Icon.enabled && row.Icon.sprite != null)
                iconWidth = row.Icon.transform.localScale.x * row.Icon.sprite.bounds.size.x;

            float gap = iconWidth > 0f ? IconGapLocal : 0f;
            float totalWidth = iconWidth + gap + textWidth;
            float rowLeft = -totalWidth * 0.5f;

            if (row.Icon != null && row.Icon.enabled && row.Icon.sprite != null)
                row.Icon.transform.localPosition = new Vector3(rowLeft + iconWidth * 0.5f, 0f, 0f);

            row.Text.transform.localPosition = new Vector3(rowLeft + iconWidth + gap + textWidth * 0.5f, 0f, 0f);
        }

        static void LayoutLabelBlock(ref StatRow gemRow, ref StatRow shieldRow)
        {
            LayoutStatRow(ref gemRow);
            LayoutStatRow(ref shieldRow);

            float gemWidth = GetRowWidth(gemRow);
            float shieldWidth = GetRowWidth(shieldRow);
            float maxWidth = Mathf.Max(gemWidth, shieldWidth);
            if (maxWidth <= 0f)
                return;

            CenterRowOnAxis(ref gemRow, gemWidth, maxWidth);
            CenterRowOnAxis(ref shieldRow, shieldWidth, maxWidth);
        }

        static float GetRowWidth(StatRow row)
        {
            if (row.Text == null)
                return 0f;

            float textWidth = row.Text.preferredWidth;
            float iconWidth = 0f;
            if (row.Icon != null && row.Icon.enabled && row.Icon.sprite != null)
                iconWidth = row.Icon.transform.localScale.x * row.Icon.sprite.bounds.size.x;

            float gap = iconWidth > 0f ? IconGapLocal : 0f;
            return iconWidth + gap + textWidth;
        }

        static void CenterRowOnAxis(ref StatRow row, float rowWidth, float blockWidth)
        {
            float shift = (blockWidth - rowWidth) * 0.5f;
            if (Mathf.Abs(shift) < 0.001f || row.Root == null)
                return;

            if (row.Icon != null && row.Icon.enabled)
            {
                var iconPos = row.Icon.transform.localPosition;
                iconPos.x += shift;
                row.Icon.transform.localPosition = iconPos;
            }

            if (row.Text != null)
            {
                var textPos = row.Text.transform.localPosition;
                textPos.x += shift;
                row.Text.transform.localPosition = textPos;
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
            ApplyLayout();
            Refresh();
        }

        void Refresh()
        {
            if (planetId == 0)
                return;

            EnsureLabel();
            if (_gemRow.Text == null || _shieldRow.Text == null)
                return;

            if (!EcsGameBridge.TryGetPlanetStateByPlanetId(planetId, out PlanetState state))
                return;

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

            _gemRow.Text.text =
                $"<color={GemsColorHex}>{currentGems}</color> <color={GemsMaxColorHex}>/ {maxGems}</color>";
            _shieldRow.Text.text =
                $"<color={ShieldColorHex}>{currentShield}</color> <color={ShieldMaxColorHex}>/ {maxShield}</color>";

            LayoutLabelBlock(ref _gemRow, ref _shieldRow);
        }

        static class GemMoonLabelIcons
        {
            const string GemIconPath =
                "Assets/CleanFlatIcon/png_128/icon_line/icon_line_store/icon_line_store_25.png";
            const string ShieldIconPath =
                "Assets/CleanFlatIcon/png_128/icon/icon_shield/icon_shield_20.png";

            static Sprite _gem;
            static Sprite _shield;

            public static Sprite Gem => Load(ref _gem, GemIconPath);
            public static Sprite Shield => Load(ref _shield, ShieldIconPath);

            static Sprite Load(ref Sprite cache, string assetPath)
            {
                if (cache != null)
                    return cache;

#if UNITY_EDITOR
                cache = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
#endif
                return cache;
            }
        }
    }
}
