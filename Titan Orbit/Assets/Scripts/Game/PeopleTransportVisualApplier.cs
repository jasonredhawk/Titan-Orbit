using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Instantiates legacy PeopleTransport prefab visuals for ECS ghost proxies.</summary>
    public static class PeopleTransportVisualApplier
    {
        const string DefaultPrefabPath = "Assets/Prefabs/PeopleTransport.prefab";
        const float BasePrefabScale = 0.25f;

        static readonly HashSet<string> StripComponentNames = new HashSet<string>
        {
            "NetworkObject",
            "NetworkBehaviour",
            "NetworkTransform",
            "NetworkRigidbody",
            "ToroidalRenderer",
            "PeopleTransportProjectile",
        };

        public static GameObject LoadDefaultPrefab()
        {
            // --- LoadDefaultPrefab ---
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(DefaultPrefabPath);
#else
            return Resources.Load<GameObject>("PeopleTransport");
#endif
        }

        public static GameObject CreateVisual(GameObject prefab, float peopleAmount, TeamId team)
        {
            // --- Create instance ---
            if (prefab == null)
            {
                var fallback = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                fallback.name = "PeopleTransportProxy";
                var col = fallback.GetComponent<Collider>();
                if (col != null)
                    Object.Destroy(col);
                ApplyTeamTint(fallback, team);
                fallback.transform.localScale = Vector3.one * ComputeWorldScale(peopleAmount);
                return fallback;
            }

            var instance = Object.Instantiate(prefab);
            instance.name = "PeopleTransportProxy";
            StripForProxy(instance);
            ApplyTeamTint(instance, team);
            instance.transform.localScale = Vector3.one * ComputeWorldScale(peopleAmount);
            return instance;
        }

        public static float ComputeWorldScale(float peopleAmount)
        {
            return BasePrefabScale * PeopleTransportMath.GetVisualScaleMultiplier(math.max(1f, peopleAmount));
        }

        public static void StripForProxy(GameObject root)
        {
            // --- Strip components ---
            ShipVisualApplier.StripPhysicsAndNetworking(root);

            var components = root.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null)
                    continue;

                var typeName = component.GetType().Name;
                if (StripComponentNames.Contains(typeName))
                    Object.Destroy(component);
            }

            foreach (var col in root.GetComponentsInChildren<Collider>(true))
            {
                if (col != null)
                    Object.Destroy(col);
            }
        }

        static void ApplyTeamTint(GameObject root, TeamId team)
        {
            // --- Apply changes ---
            var color = team.ToColor();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null)
                    continue;

                var material = renderer.material;
                if (material.HasProperty("_BaseColor"))
                    material.SetColor("_BaseColor", color);
                if (material.HasProperty("_Color"))
                    material.SetColor("_Color", color);
            }
        }
    }
}
