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
        const int TextSortingOrder = 5001;
        static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;

        [SerializeField] int planetId;
        float _moonLocalRadius = 0.25f;

        TextMeshPro _labelText;

        public void Configure(int id, float moonLocalRadius)
        {
            planetId = id;
            _moonLocalRadius = Mathf.Max(0.02f, moonLocalRadius);
            EnsureText();
            ApplyLayout();
            Refresh();
        }

        void EnsureText()
        {
            if (_labelText != null)
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

            _labelText = CreateText("GemsLabel");
        }

        TextMeshPro CreateText(string name)
        {
            Transform existing = transform.Find(name);
            GameObject go = existing != null ? existing.gameObject : new GameObject(name);
            if (existing == null)
                go.transform.SetParent(transform, false);

            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            go.transform.localScale = new Vector3(WorldBodyLabelLayout.TextWorldScale, -WorldBodyLabelLayout.TextWorldScale, WorldBodyLabelLayout.TextWorldScale);

            var tmp = go.GetComponent<TextMeshPro>();
            if (tmp == null)
                tmp = go.AddComponent<TextMeshPro>();

            tmp.font = ResolveFont();
            tmp.fontSize = 36f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.verticalAlignment = VerticalAlignmentOptions.Bottom;
            tmp.enableWordWrapping = false;
            tmp.richText = true;
            tmp.lineSpacing = -8f;
            tmp.color = new Color(1f, 0.2f, 0.2f);
            ApplyReadableTextMaterial(tmp);
            tmp.ForceMeshUpdate();
            return tmp;
        }

        void ApplyLayout()
        {
            if (_labelText == null)
                return;

            WorldBodyLabelLayout.ApplySnugMoonLabel(_labelText, transform, _moonLocalRadius);
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
                mat.SetColor("_OutlineColor", new Color(0f, 0f, 0f, 0.85f));
            if (mat.HasProperty("_OutlineWidth"))
                mat.SetFloat("_OutlineWidth", 0.2f);
            mat.renderQueue = RenderQueueOverlay;

            var renderer = text.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sortingOrder = TextSortingOrder;
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

            EnsureText();
            if (_labelText == null)
                return;

            if (!EcsGameBridge.TryGetPlanetStateByPlanetId(planetId, out PlanetState state))
                return;

            int current = Mathf.RoundToInt(state.CurrentGems);
            int max = Mathf.RoundToInt(PlanetEconomyMath.GetMaxGemsForLevel(state.PlanetLevel));
            _labelText.text = $"<size=110%>{current}</size>\n<size=70%>{max}</size>";
            _labelText.color = state.Ownership.ToColor();
        }
    }
}
