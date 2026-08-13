using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Builds and configures gem crystal GameObject shells for
    /// <see cref="GemVisualPool"/> and networked gem proxies in <see cref="EcsWorldVisualizer"/>.
    /// <para>
    /// Hot path is <see cref="TryCreateGemVisual"/> → pool Rent (no Instantiates).
    /// Cold path is <see cref="TryCreateGemVisualRaw"/> (Instantiates + strip + shared tint)
    /// used only when the pool must grow. Render only — sim value lives on ECS <c>GemState</c>.
    /// Gem GOs are created only after networked gem ghosts Instantiates (server pose/velocity).
    /// </para>
    /// </summary>
    public static class GemVisualApplier
    {
        const string DefaultGemPrefabPath = "Assets/Prefabs/Gem.prefab";

        /// <summary>Resources path for player builds (<c>Assets/Resources/Gem.prefab</c>).</summary>
        const string DefaultGemResourcesName = "Gem";

        /// <summary>
        /// Semi-transparent red so gems read on busy backgrounds.
        /// Alpha is only correct when URP Lit keywords/blend state are set together — see
        /// <see cref="ApplyGemTint"/>.
        /// </summary>
        static readonly Color GemTintColor = new Color(1f, 0.2f, 0.2f, 0.55f);

        /// <summary>
        /// [TITAN-ORBIT] Territory bonus gem tint (NGO bonusGemTintColor ≈ yellow).
        /// </summary>
        static readonly Color BonusGemTintColor = new Color(1f, 0.9f, 0.15f, 0.55f);

        /// <summary>
        /// Shared tinted material for normal (red) gem proxies.
        /// </summary>
        static Material s_sharedTintedGemMaterial;

        /// <summary>Shared yellow material for territory bonus gems.</summary>
        static Material s_sharedBonusTintedGemMaterial;

        /// <summary>True after <see cref="EnsureSharedTintReady"/> finished material + keyword setup.</summary>
        static bool s_tintReady;

        /// <summary>MonoBehaviour types removed so the proxy is a pure visual shell.</summary>
        static readonly HashSet<string> StripComponentNames = new HashSet<string>
        {
            "NetworkObject",
            "NetworkBehaviour",
            "ToroidalRenderer",
            "Gem",
        };

        /// <summary>
        /// [UNITY] Domain reload safety — drop static material reference across Play Mode.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_sharedTintedGemMaterial = null;
            s_sharedBonusTintedGemMaterial = null;
            s_tintReady = false;
        }

        /// <summary>
        /// Rents a gem visual from <see cref="GemVisualPool"/> (preferred hot path).
        /// Grows the pool via Instantiates only when idle stock is empty.
        /// </summary>
        /// <param name="gemPrefab">Crystal mesh prefab — remembered by the pool for growth.</param>
        /// <param name="gemValue">Authoritative gem value — drives visual scale only.</param>
        /// <param name="instance">Active GameObject, or null on failure.</param>
        /// <returns>True when a visual is ready to place.</returns>
        public static bool TryCreateGemVisual(GameObject gemPrefab, float gemValue, out GameObject instance) =>
            TryCreateGemVisual(gemPrefab, gemValue, isBonusGem: false, out instance);

        /// <summary>
        /// Rents a gem visual and applies red or yellow tint from <paramref name="isBonusGem"/>.
        /// </summary>
        public static bool TryCreateGemVisual(
            GameObject gemPrefab, float gemValue, bool isBonusGem, out GameObject instance)
        {
            GemVisualPool.EnsurePrefab(gemPrefab);
            if (!GemVisualPool.TryRent(gemValue, out instance, "GemTagProxy"))
                return false;
            ApplyGemTint(instance, isBonusGem);
            return true;
        }

        /// <summary>
        /// Cold Instantiates path used by the pool when it must grow.
        /// Prefer <see cref="TryCreateGemVisual"/> from gameplay code.
        /// </summary>
        /// <param name="gemPrefab">Source prefab (may include Rigidbody — stripped here).</param>
        /// <param name="gemValue">Initial scale value.</param>
        /// <param name="immediateStrip">
        /// When true, uses DestroyImmediate for physics/network components so PhysX never
        /// registers the proxy (pool growth / prewarm). Gameplay Rent never Instantiates.
        /// </param>
        /// <param name="instance">Created GameObject (caller parents/activates).</param>
        public static bool TryCreateGemVisualRaw(
            GameObject gemPrefab,
            float gemValue,
            bool immediateStrip,
            out GameObject instance)
        {
            instance = null;
            if (gemPrefab == null)
                return false;

            // --- Instantiates legacy prefab and strip sim/network components ---
            instance = Object.Instantiate(gemPrefab);
            instance.name = "GemTagProxy";
            StripForProxy(instance, immediateStrip);
            ApplyGemTint(instance, isBonusGem: false);
            float scale = ComputeVisualScale(gemValue);
            instance.transform.localScale = Vector3.one * scale;
            return true;
        }

        /// <summary>
        /// Re-applies red/yellow shared tint on an already-rented gem proxy (pool shells start red).
        /// </summary>
        public static void ApplyTintForBonusFlag(GameObject root, bool isBonusGem) =>
            ApplyGemTint(root, isBonusGem);

        /// <summary>
        /// [TITAN-ORBIT] Builds the shared URP tint material (and warms keywords) before combat.
        /// Called from <see cref="GemVisualPool.Prewarm"/> so the first Rent does not hitch.
        /// </summary>
        /// <param name="gemPrefab">Prefab whose MeshRenderer supplies the source material.</param>
        public static void EnsureSharedTintReady(GameObject gemPrefab)
        {
            if (s_tintReady && s_sharedTintedGemMaterial != null && s_sharedBonusTintedGemMaterial != null)
                return;

            if (gemPrefab == null)
                gemPrefab = LoadDefaultGemPrefab();
            if (gemPrefab == null)
                return;

            // --- Build shared materials from prefab without leaving a visible GO ---
            // Instantiates briefly so we can read MeshRenderer.sharedMaterial, then destroy.
            var probe = Object.Instantiate(gemPrefab);
            probe.name = "GemTintProbe";
            probe.SetActive(false);
            ApplyGemTint(probe, isBonusGem: false);
            ApplyGemTint(probe, isBonusGem: true);
            Object.Destroy(probe);

            // --- Force URP to compile the transparent variant off the destroy frame ---
            // [UNITY] Creating a Material alone does not compile shader variants; a tiny draw does.
            // We park an invisible quad with the shared material for one frame via a warm camera
            // is heavy — instead create a disabled renderer that ShaderVariantCollection would
            // cover. Practical fallback: assign material on an active hidden mesh once.
            if (s_sharedTintedGemMaterial != null)
            {
                var warm = GameObject.CreatePrimitive(PrimitiveType.Quad);
                warm.name = "GemTintShaderWarm";
                warm.hideFlags = HideFlags.HideAndDontSave;
                var col = warm.GetComponent<Collider>();
                if (col != null)
                    Object.Destroy(col);
                var renderer = warm.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.sharedMaterial = s_sharedTintedGemMaterial;
                    // Off-camera but active so the first real gem is not the first draw.
                    warm.transform.position = new Vector3(0f, -10000f, 0f);
                }

                Object.Destroy(warm, 0.5f);
            }

            s_tintReady = s_sharedTintedGemMaterial != null;
        }

        /// <summary>Maps gem value to uniform local scale via inverse lerp between min and max value.</summary>
        public static float ComputeVisualScale(float gemValue) =>
            GemPresentationScale.ComputeVisualScale(gemValue);

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

        /// <summary>
        /// Estimated world diameter when proxy mesh bounds are unavailable.
        /// [TITAN-ORBIT] Treats visual scale as diameter when the mesh is ~1 unit across at scale 1.
        /// </summary>
        public static float ComputeVisualDiameter(float gemValue) =>
            ComputeVisualScale(gemValue);

        /// <summary>
        /// World-space XZ diameter of a gem proxy for tractor-beam mouth sizing.
        /// Prefers local mesh bounds × lossy scale so a spinning crystal does not inflate
        /// the axis-aligned world AABB (which made the beam mouth wider than the gem).
        /// </summary>
        /// <param name="proxy">Hybrid gem GameObject (may be null).</param>
        /// <param name="gemValue">Fallback value curve when mesh is missing.</param>
        /// <returns>World diameter on the play plane (XZ).</returns>
        public static float ReadWorldDiameter(GameObject proxy, float gemValue)
        {
            if (proxy != null)
            {
                // --- Path A: local mesh size × uniform scale (rotation-stable) ---
                // [UNITY] renderer.bounds is a world AABB — a diamond spun 45° grows that box
                // even though the crystal silhouette stays the same. Tractor mouths used that
                // inflated width and looked larger than the gem.
                var meshFilter = proxy.GetComponentInChildren<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    Vector3 localSize = meshFilter.sharedMesh.bounds.size;
                    Vector3 lossy = proxy.transform.lossyScale;
                    float worldX = localSize.x * Mathf.Abs(lossy.x);
                    float worldZ = localSize.z * Mathf.Abs(lossy.z);
                    float fromMesh = Mathf.Max(worldX, worldZ);
                    if (fromMesh > 0.01f)
                        return fromMesh;
                }

                // --- Path B: renderer local bounds when MeshFilter is absent ---
                var renderer = proxy.GetComponentInChildren<Renderer>();
                if (renderer != null)
                {
                    Vector3 localSize = renderer.localBounds.size;
                    Vector3 lossy = proxy.transform.lossyScale;
                    float worldX = localSize.x * Mathf.Abs(lossy.x);
                    float worldZ = localSize.z * Mathf.Abs(lossy.z);
                    float fromRenderer = Mathf.Max(worldX, worldZ);
                    if (fromRenderer > 0.01f)
                        return fromRenderer;
                }
            }

            return ComputeVisualDiameter(gemValue);
        }

        /// <summary>
        /// Tints the gem mesh red (normal) or yellow (territory bonus) with URP Lit alpha blending.
        /// Uses two shared materials (no per-Instantiates Material alloc).
        /// </summary>
        /// <param name="root">Gem proxy root (may have a child MeshRenderer).</param>
        /// <param name="isBonusGem">True → yellow bonus tint.</param>
        static void ApplyGemTint(GameObject root, bool isBonusGem)
        {
            var renderer = root.GetComponentInChildren<Renderer>();
            if (renderer == null)
                return;

            renderer.shadowCastingMode = ShadowCastingMode.Off;

            // --- Build shared tinted materials once from the prefab's sharedMaterial ---
            if (s_sharedTintedGemMaterial == null || s_sharedBonusTintedGemMaterial == null)
            {
                Material source = renderer.sharedMaterial != null
                    ? renderer.sharedMaterial
                    : renderer.material;
                if (s_sharedTintedGemMaterial == null)
                {
                    s_sharedTintedGemMaterial = new Material(source)
                    {
                        name = "TitanOrbit_GemTinted_Shared",
                    };
                    ConfigureUrpTransparentTint(s_sharedTintedGemMaterial, GemTintColor);
                }

                if (s_sharedBonusTintedGemMaterial == null)
                {
                    s_sharedBonusTintedGemMaterial = new Material(source)
                    {
                        name = "TitanOrbit_GemBonusTinted_Shared",
                    };
                    ConfigureUrpTransparentTint(s_sharedBonusTintedGemMaterial, BonusGemTintColor);
                }
            }

            // [UNITY] sharedMaterial — all gems of a tint class share one Material.
            renderer.sharedMaterial = isBonusGem ? s_sharedBonusTintedGemMaterial : s_sharedTintedGemMaterial;
        }

        /// <summary>
        /// Writes URP Lit transparent + tint onto <paramref name="material"/> (once at shared create).
        /// </summary>
        static void ConfigureUrpTransparentTint(Material material, Color tint)
        {
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", tint);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", tint);
            material.color = tint;

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

        /// <summary>
        /// Removes physics, networking, and legacy Gem behaviours so proxy is render-only.
        /// </summary>
        /// <param name="root">Instantiated gem root.</param>
        /// <param name="immediate">
        /// [TITAN-ORBIT] Pool prewarm/growth uses DestroyImmediate so Rigidbody never enters PhysX.
        /// </param>
        public static void StripForProxy(GameObject root, bool immediate = false)
        {
            StripPhysicsAndNetworking(root, immediate);

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
                    DestroyComponent(component, immediate);
            }
        }

        /// <summary>
        /// Strips colliders/rigidbodies/network behaviours. Local copy so pool growth can
        /// DestroyImmediate without changing ship proxy strip timing.
        /// </summary>
        static void StripPhysicsAndNetworking(GameObject root, bool immediate)
        {
            foreach (var col in root.GetComponentsInChildren<Collider>(true))
                DestroyComponent(col, immediate);
            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true))
                DestroyComponent(rb, immediate);
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;
                string typeName = component.GetType().Name;
                if (typeName.Contains("Network") || typeName.Contains("Netcode") || typeName.Contains("ClientNetwork"))
                    DestroyComponent(component, immediate);
            }
        }

        /// <summary>Destroy vs DestroyImmediate for strip paths.</summary>
        static void DestroyComponent(Object component, bool immediate)
        {
            if (immediate)
                Object.DestroyImmediate(component);
            else
                Object.Destroy(component);
        }

        /// <summary>
        /// Loads default gem prefab — Editor AssetDatabase path, or <c>Resources/Gem</c> in players.
        /// </summary>
        public static GameObject LoadDefaultGemPrefab()
        {
#if UNITY_EDITOR
            var fromAssets = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(DefaultGemPrefabPath);
            if (fromAssets != null)
                return fromAssets;
#endif
            // [UNITY] Player builds cannot use AssetDatabase — Resources/Gem.prefab ships in builds.
            return Resources.Load<GameObject>(DefaultGemResourcesName);
        }
    }
}
