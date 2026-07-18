using TitanOrbit.Core;
using TitanOrbit.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Lightweight people-transport GameObject proxies for ECS ghosts.
    /// Uses a shared primitive + shared materials (not the heavy legacy PeopleTransport prefab)
    /// so spawning many load/unload spheres does not hitch the main thread.
    /// </summary>
    public static class PeopleTransportVisualApplier
    {
        /// <summary>Base world scale before amount multiplier (matches prior ~0.25 prefab root).</summary>
        const float BasePrefabScale = 0.28f;

        /// <summary>Hidden template sphere — cloned with Instantiate (cheap vs prefab + Strip).</summary>
        static GameObject s_Template;

        /// <summary>Shared materials per <see cref="TeamId"/> — avoids renderer.material instancing per spawn.</summary>
        static readonly Material[] s_TeamMaterials = new Material[6];

        /// <summary>
        /// Legacy loader kept for EcsWorldVisualizer serialized field fallback — prefer CreateVisual
        /// which ignores the heavy prefab and uses the lightweight template.
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
        /// Creates a cheap sphere proxy tinted by team. Prefab argument is ignored (kept for call-site
        /// compatibility) — Instantiating + stripping the legacy prefab caused load/unload hitches.
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

        /// <summary>Assigns a shared team-tinted material (no per-instance material alloc).</summary>
        static void ApplyTeamTint(GameObject root, TeamId team)
        {
            var renderer = root.GetComponent<Renderer>();
            if (renderer == null)
                return;

            renderer.sharedMaterial = GetSharedMaterial(team);
        }

        /// <summary>Lazy shared materials indexed by team byte.</summary>
        static Material GetSharedMaterial(TeamId team)
        {
            int index = (int)team;
            if (index < 0 || index >= s_TeamMaterials.Length)
                index = 0;

            if (s_TeamMaterials[index] == null)
                s_TeamMaterials[index] = WorldBodyVisualApplier.CreateLitMaterial(team.ToColor());

            return s_TeamMaterials[index];
        }
    }
}
