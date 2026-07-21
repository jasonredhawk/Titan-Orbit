using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Instantiates gem crystal prefabs as ECS presentation proxies for
    /// <see cref="EcsWorldVisualizer"/> and <see cref="ClientGemBurstPresenter"/>.
    /// Strips NGO/legacy components, applies a readable red tint, and scales mesh by gem value.
    /// Render only — sim value lives on ECS <c>GemState</c>. Applies end-of-life shrink
    /// (original Gem.shrinkDuration) when spawn time + settings are provided.
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

        /// <summary>
        /// Shared tinted material for all gem proxies. Creating <c>renderer.material</c> per Instantiates
        /// allocated a new Material on every burst gem and hitchs destroy frames — reuse one instance.
        /// </summary>
        static Material s_sharedTintedGemMaterial;

        /// <summary>True after <see cref="PrewarmSharedTint"/> ran (shader/material setup done off the destroy frame).</summary>
        static bool s_prewarmed;

        /// <summary>MonoBehaviour types removed so the proxy is a pure visual shell.</summary>
        static readonly HashSet<string> StripComponentNames = new HashSet<string>
        {
            "NetworkObject",
            "NetworkBehaviour",
            "ToroidalRenderer",
            "Gem",
        };

        /// <summary>
        /// [TITAN-ORBIT] Builds the shared URP tint material before the first asteroid destroy.
        /// First gem Instantiates after settle can hitch on shader/material setup — prewarm avoids
        /// that cost on the destroy frame.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void PrewarmSharedTint()
        {
            if (s_prewarmed)
                return;
            s_prewarmed = true;

            GameObject prefab = LoadDefaultGemPrefab();
            if (prefab == null)
                return;

            // --- One throwaway Instantiates so URP keywords compile off the hot path ---
            if (!TryCreateGemVisual(prefab, 1f, out GameObject warmup) || warmup == null)
                return;
            warmup.name = "GemTintPrewarm";
            warmup.SetActive(false);
            Object.Destroy(warmup);
        }

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

        /// <summary>
        /// Value scale × lifetime shrink (original: full size until last 3s, then linear to zero).
        /// </summary>
        /// <param name="gemValue">Authoritative gem value.</param>
        /// <param name="spawnServerTime">Ghosted <c>GemState.SpawnServerTime</c> (ServerTick seconds).</param>
        /// <param name="nowServerTime">Current ServerTick seconds (via <c>PlanetGemMoonOrbitClock</c>).</param>
        public static float ComputeLifetimeVisualScale(float gemValue, float spawnServerTime, float nowServerTime)
        {
            float baseScale = ComputeVisualScale(Mathf.Max(0.25f, gemValue));
            if (spawnServerTime <= 0f)
                return baseScale;

            var settings = GemExplosionSettingsCache.ResolveOrDefault();
            float mul = GemExplosionMath.LifetimeScaleMultiplier(
                spawnServerTime,
                nowServerTime,
                settings.GemLifetimeSeconds,
                settings.GemShrinkDurationSeconds);
            return baseScale * mul;
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
        /// Uses one shared material for all gems (no per-Instantiates Material alloc).
        /// </summary>
        /// <remarks>
        /// [TITAN-ORBIT] Incomplete URP transparent setup used to draw near-black meshes.
        /// Full keyword/blend setup is applied once on <see cref="s_sharedTintedGemMaterial"/>.
        /// </remarks>
        /// <param name="root">Gem proxy root (may have a child MeshRenderer).</param>
        static void ApplyGemTint(GameObject root)
        {
            var renderer = root.GetComponentInChildren<Renderer>();
            if (renderer == null)
                return;

            renderer.shadowCastingMode = ShadowCastingMode.Off;

            // --- Build shared tinted material once from the prefab's sharedMaterial ---
            if (s_sharedTintedGemMaterial == null)
            {
                Material source = renderer.sharedMaterial != null
                    ? renderer.sharedMaterial
                    : renderer.material;
                s_sharedTintedGemMaterial = new Material(source)
                {
                    name = "TitanOrbit_GemTinted_Shared",
                };
                ConfigureUrpTransparentTint(s_sharedTintedGemMaterial);
            }

            // [UNITY] sharedMaterial — all gems share one Material; no per-gem .material clone.
            renderer.sharedMaterial = s_sharedTintedGemMaterial;
        }

        /// <summary>
        /// Writes URP Lit transparent + red tint onto <paramref name="material"/> (once at shared create).
        /// </summary>
        static void ConfigureUrpTransparentTint(Material material)
        {
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", GemTintColor);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", GemTintColor);
            material.color = GemTintColor;

            // [UNITY] URP Lit needs keywords + blend + queue together — floats alone are not enough.
            if (material.HasProperty("_Surface"))
                material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Blend"))
                material.SetFloat("_Blend", 0f);
            if (material.HasProperty("_AlphaClip"))
                material.SetFloat("_AlphaClip", 0f);
            if (material.HasProperty("_Mode"))
                material.SetFloat("_Mode", 3f);

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
