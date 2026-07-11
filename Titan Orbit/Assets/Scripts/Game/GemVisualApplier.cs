using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Instantiates legacy gem crystal prefabs as ECS presentation proxies for
    /// <see cref="EcsWorldVisualizer"/>. Strips NGO/legacy components, tints for team visibility,
    /// and scales mesh by gem value. Render only — sim value lives on <see cref="GemState"/>.
    /// </summary>
    public static class GemVisualApplier
    {
        const string DefaultGemPrefabPath = "Assets/Prefabs/Gem.prefab";

        /// <summary>Designer reference range for visual scale curve.</summary>
        const float MinGemValue = 1f;
        const float MaxGemValue = 100f;
        const float ScaleAtMinValue = 0.5f;
        const float ScaleAtMaxValue = 4f;

        /// <summary>Semi-transparent red overlay so gems read on busy backgrounds.</summary>
        static readonly Color GemTintColor = new Color(1f, 0f, 0f, 0.45f);

        /// <summary>MonoBehaviour types removed so the proxy is a pure visual shell.</summary>
        static readonly HashSet<string> StripComponentNames = new HashSet<string>
        {
            "NetworkObject",
            "NetworkBehaviour",
            "ToroidalRenderer",
            "Gem",
        };

        /// <summary>
        /// Creates a stripped gem proxy at default scale for the given gem value.
        /// </summary>
        public static bool TryCreateGemVisual(GameObject gemPrefab, float gemValue, out GameObject instance)
        {
            instance = null;
            if (gemPrefab == null)
                return false;

            // --- Instantiate legacy prefab and strip sim/network components ---
            instance = Object.Instantiate(gemPrefab);
            instance.name = "GemTagProxy";
            StripForProxy(instance);
            ApplyGemTint(instance);
            float scale = ComputeVisualScale(gemValue);
            instance.transform.localScale = Vector3.one * scale;
            return true;
        }

        /// <summary>Maps gem value to uniform local scale via inverse lerp between min and max value.</summary>
        public static float ComputeVisualScale(float gemValue)
        {
            float t = Mathf.InverseLerp(MinGemValue, MaxGemValue, gemValue);
            return Mathf.Lerp(ScaleAtMinValue, ScaleAtMaxValue, t);
        }

        /// <summary>Estimated world diameter when proxy renderer bounds are unavailable.</summary>
        public static float ComputeVisualDiameter(float gemValue) =>
            ComputeVisualScale(gemValue);

        /// <summary>Reads renderer bounds world diameter when proxy exists; else analytic estimate.</summary>
        public static float ReadWorldDiameter(GameObject proxy, float gemValue)
        {
            if (proxy != null)
            {
                var renderer = proxy.GetComponentInChildren<Renderer>();
                if (renderer != null)
                {
                    Vector3 extents = renderer.bounds.extents;
                    return Mathf.Max(extents.x, extents.z) * 2f;
                }
            }

            return ComputeVisualDiameter(gemValue);
        }

        static void ApplyGemTint(GameObject root)
        {
            var renderer = root.GetComponentInChildren<Renderer>();
            if (renderer == null)
                return;

            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var material = renderer.material;
            if (material.HasProperty("_Surface"))
                material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Mode"))
                material.SetFloat("_Mode", 3f);
            if (material.HasProperty("_SrcBlend"))
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend"))
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite"))
                material.SetInt("_ZWrite", 0);
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", GemTintColor);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", GemTintColor);
            material.renderQueue = 3000;
        }

        /// <summary>Removes physics, networking, and legacy Gem behaviours so proxy is render-only.</summary>
        public static void StripForProxy(GameObject root)
        {
            ShipVisualApplier.StripPhysicsAndNetworking(root);

            var components = root.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null)
                    continue;
                // [HYBRID] Keep mesh/render shell; destroy gameplay and NetCode types.
                if (component is Transform || component is MeshFilter || component is Renderer)
                    continue;

                string typeName = component.GetType().Name;
                if (StripComponentNames.Contains(typeName) || typeName.Contains("Network"))
                    Object.Destroy(component);
            }
        }

        /// <summary>[EDITOR] Loads default gem prefab from project path when none assigned at runtime.</summary>
        public static GameObject LoadDefaultGemPrefab()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(DefaultGemPrefabPath);
#else
            return null;
#endif
        }
    }
}
