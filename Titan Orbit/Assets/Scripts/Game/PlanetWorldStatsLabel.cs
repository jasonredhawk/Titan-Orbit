using TitanOrbit.Core;
using TitanOrbit.ECS;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>Updates population text snug above the planet body.</summary>
    public class PlanetWorldStatsLabel : MonoBehaviour
    {
        const int TextSortingOrder = 5001;
        static readonly int RenderQueueOverlay = (int)RenderQueue.Overlay;

        [SerializeField] int planetId;
        TextMeshPro _label;

        public void Configure(int id)
        {
            planetId = id;
            EnsureLabel();
            ApplyLayout();
            Refresh();
        }

        void EnsureLabel()
        {
            if (_label != null)
                return;

            var existing = transform.Find("PopulationText");
            if (existing != null)
                _label = existing.GetComponent<TextMeshPro>();
            if (_label == null)
                _label = GetComponentInChildren<TextMeshPro>(true);
            if (_label == null)
                _label = CreateFallbackLabel();
            else
                ApplyReadableTextMaterial(_label);
        }

        TextMeshPro CreateFallbackLabel()
        {
            var go = new GameObject("PopulationText");
            go.transform.SetParent(transform, false);
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            go.transform.localScale = new Vector3(
                WorldBodyLabelLayout.TextWorldScale,
                -WorldBodyLabelLayout.TextWorldScale,
                WorldBodyLabelLayout.TextWorldScale);

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.font = ResolveFont();
            tmp.fontSize = 36f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.verticalAlignment = VerticalAlignmentOptions.Bottom;
            tmp.enableWordWrapping = false;
            ApplyReadableTextMaterial(tmp);
            tmp.ForceMeshUpdate();
            return tmp;
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

        void ApplyLayout()
        {
            EnsureLabel();
            if (_label == null)
                return;

            WorldBodyLabelLayout.ApplySnugPlanetLabel(_label, transform);
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
            if (_label == null)
                return;

            if (!EcsGameBridge.TryGetPlanetStateByPlanetId(planetId, out PlanetState state))
                return;

            _label.text = state.Population.ToString();
            _label.color = state.Ownership.ToColor();
        }
    }
}
