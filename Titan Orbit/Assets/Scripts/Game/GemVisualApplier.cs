using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Instantiates gem crystal prefabs as ECS presentation proxies for
    /// <see cref="EcsWorldVisualizer"/> and <see cref="ClientGemBurstPresenter"/>.
    /// Strips NGO/legacy components, applies a readable red tint, and scales mesh by gem value.
    /// Render only — sim value lives on <see cref="GemState"/>.
    /// </summary>
    public static class GemVisualApplier
    {
        const string DefaultGemPrefabPath = "Assets/Prefabs/Gem.prefab";

        /// <summary>Designer reference range for visual scale curve.</summary>
        const float MinGemValue = 1f;
        const float MaxGemValue = 100f;
        const float ScaleAtMinValue = 0.5f;
        const float ScaleAtMaxValue = 4f;

        /// <summary>
        /// Semi-transparent red so gems read on busy backgrounds.
        /// Alpha is only correct when URP Lit keywords/blend state are set together — see
        /// <see cref="ApplyGemTint"/>.
        /// </summary>
        static readonly Color GemTintColor = new Color(1f, 0.2f, 0.2f, 0.55f);

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
        /// Used for networked gem ghosts and for the immediate local destroy burst.
        /// </summary>
        /// <param name="gemPrefab">Crystal mesh prefab (usually <c>Assets/Prefabs/Gem.prefab</c>).</param>
        /// <param name="gemValue">Authoritative gem value — drives visual scale only.</param>
        /// <param name="instance">Created GameObject, or null on failure.</param>
        /// <returns>True when Instantiates succeeded.</returns>
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

        /// <summary>
        /// Tints the gem mesh red and configures URP Lit for real alpha blending.
        /// </summary>
        /// <remarks>
        /// [TITAN-ORBIT] Bug (2026-07-20): setting only <c>_Surface = 1</c> + blend floats without
        /// enabling <c>_SURFACE_TYPE_TRANSPARENT</c> leaves URP Lit in a broken opaque/transparent
        /// hybrid. Those meshes draw near-black. When an asteroid is destroyed, up to
        /// <see cref="Data.GemExplosionSettings.MaxGemCount"/> local gems Instantiates at once
        /// near the camera — that looked like a full-screen dark wash (easy to blame on bullet impact VFX).
        /// </remarks>
        /// <param name="root">Gem proxy root (may have a child MeshRenderer).</param>
        static void ApplyGemTint(GameObject root)
        {
            var renderer = root.GetComponentInChildren<Renderer>();
            if (renderer == null)
                return;

            // --- Instance the material so we do not mutate the shared TitanOrbit_Gem asset ---
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            Material material = renderer.material;

            // --- Color ---
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", GemTintColor);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", GemTintColor);
            material.color = GemTintColor;

            // --- Full URP Lit transparent setup (keywords + blend + queue) ---
            // [UNITY] URP Lit ignores half of the float toggles unless the matching keywords / tags
            // are set — incomplete setup is a common source of black transparent meshes.
            if (material.HasProperty("_Surface"))
                material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Blend"))
                material.SetFloat("_Blend", 0f); // 0 = Alpha blend
            if (material.HasProperty("_AlphaClip"))
                material.SetFloat("_AlphaClip", 0f);
            if (material.HasProperty("_Mode"))
                material.SetFloat("_Mode", 3f); // Legacy Built-in transparent enum (harmless on URP)

            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_SrcBlendAlpha"))
                material.SetInt("_SrcBlendAlpha", (int)BlendMode.One);
            if (material.HasProperty("_DstBlendAlpha"))
                material.SetInt("_DstBlendAlpha", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);

            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.DisableKeyword("_ALPHAMODULATE_ON");
            material.SetShaderPassEnabled("ShadowCaster", false);

            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)RenderQueue.Transparent;
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
