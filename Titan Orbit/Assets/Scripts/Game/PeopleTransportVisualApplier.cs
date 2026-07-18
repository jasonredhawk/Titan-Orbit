using TitanOrbit.Core;
using TitanOrbit.Simulation;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Lightweight people-transport GameObject proxies for client presentation entities.
    /// Uses a shared unlit primitive so spheres stay visible against planet meshes and do not
    /// hitch when many load/unload floats spawn.
    /// </summary>
    public static class PeopleTransportVisualApplier
    {
        /// <summary>Base world scale — large enough to read in orbit against planet bodies.</summary>
        const float BasePrefabScale = 0.85f;

        /// <summary>Hidden template sphere — cloned with Instantiate.</summary>
        static GameObject s_Template;

        /// <summary>Shared unlit materials per <see cref="TeamId"/>.</summary>
        static readonly Material[] s_TeamMaterials = new Material[6];

        /// <summary>
        /// Legacy loader kept for EcsWorldVisualizer serialized field fallback — CreateVisual
        /// ignores the heavy prefab and uses the lightweight template.
        /// </summary>
        public static GameObject LoadDefaultPrefab()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/PeopleTransport.prefab");
#else
            return Resources.Load<GameObject>("PeopleTransport");
#endif
        }

        /// <summary>
        /// Creates a bright unlit sphere proxy tinted by team. Prefab argument is ignored.
        /// </summary>
        public static GameObject CreateVisual(GameObject prefab, float peopleAmount, TeamId team)
        {
            _ = prefab;
            EnsureTemplate();

            var instance = Object.Instantiate(s_Template);
            instance.name = "PeopleTransportProxy";
            instance.SetActive(true);
            ApplyTeamTint(instance, team);
            instance.transform.localScale = Vector3.one * ComputeWorldScale(peopleAmount);
            return instance;
        }

        /// <summary>World uniform scale from carried population amount.</summary>
        public static float ComputeWorldScale(float peopleAmount)
        {
            return BasePrefabScale * PeopleTransportMath.GetVisualScaleMultiplier(math.max(1f, peopleAmount));
        }

        /// <summary>Builds the hidden template once (primitive sphere, no collider).</summary>
        static void EnsureTemplate()
        {
            if (s_Template != null)
                return;

            s_Template = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            s_Template.name = "PeopleTransportProxyTemplate";
            s_Template.hideFlags = HideFlags.HideAndDontSave;
            var col = s_Template.GetComponent<Collider>();
            if (col != null)
                Object.Destroy(col);
            s_Template.SetActive(false);
            Object.DontDestroyOnLoad(s_Template);
        }

        /// <summary>Assigns a shared bright unlit team material.</summary>
        static void ApplyTeamTint(GameObject root, TeamId team)
        {
            var renderer = root.GetComponent<Renderer>();
            if (renderer == null)
                return;

            renderer.sharedMaterial = GetSharedMaterial(team);
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        /// <summary>Lazy shared unlit materials indexed by team byte.</summary>
        static Material GetSharedMaterial(TeamId team)
        {
            int index = (int)team;
            if (index < 0 || index >= s_TeamMaterials.Length)
                index = 0;

            if (s_TeamMaterials[index] == null)
                s_TeamMaterials[index] = CreateUnlitMaterial(team.ToColor());

            return s_TeamMaterials[index];
        }

        /// <summary>
        /// Unlit / bright material so transports stay visible against lit planet surfaces.
        /// URP Lit often reads as nearly black from the top-down camera without a strong fill light.
        /// </summary>
        static Material CreateUnlitMaterial(Color color)
        {
            // Boost saturation/value so team tint pops in space.
            Color.RGBToHSV(color, out float h, out float s, out float v);
            color = Color.HSVToRGB(h, math.clamp(s * 1.15f, 0.55f, 1f), math.clamp(math.max(v, 0.75f), 0.75f, 1f));
            color.a = 1f;

            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Standard");
            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);
            mat.color = color;
            return mat;
        }
    }
}
