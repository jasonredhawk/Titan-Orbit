using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Instantiates ship-family chassis prefabs as render-only GameObject proxies and applies
    /// team-colored materials. Called by EcsWorldVisualizer when spawning or respawning ship visuals.
    /// Strips physics colliders, Rigidbodies, and NetCode MonoBehaviour components so the proxy
    /// cannot affect simulation — authoritative hull colliders are built on the ECS ghost by
    /// <see cref="ShipHullColliderLogic"/> from the same chassis prefab.
    /// </summary>
    public static class ShipVisualApplier
    {
        /// <summary>
        /// Creates a ship visual instance from family config or prefab override. Applies team materials
        /// and strips sim/network components. Returns false when no prefab resolves.
        /// </summary>
        public static bool TryCreateShipVisual(
            ShipFamilyDefinition family,
            GameObject prefabOverride,
            TeamId team,
            int shipLevel,
            out GameObject instance)
        {
            // --- Resolve prefab ---
            instance = null;
            GameObject prefab = prefabOverride;
            if (prefab == null && family != null)
                family.TryGetVisualPrefabForLevel(shipLevel, out prefab);
            if (prefab == null)
                return false;

            // --- Instantiate proxy ---
            instance = Object.Instantiate(prefab);
            instance.name = prefab.name + "Proxy";
            StripPhysicsAndNetworking(instance);
            ApplyTeamMaterials(family, instance, team);
            return true;
        }

        /// <summary>Swaps renderer sharedMaterials with team palette from ShipFamilyDefinition.</summary>
        public static void ApplyTeamMaterials(ShipFamilyDefinition family, GameObject root, TeamId team)
        {
            // --- Apply changes ---
            if (family == null || root == null || team == TeamId.None)
                return;

            List<Material> teamMats = family.GetMaterialsForTeam(team);
            if (teamMats == null || teamMats.Count == 0)
                return;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || renderer is ParticleSystemRenderer)
                    continue;

                Material[] current = renderer.sharedMaterials;
                if (current == null || current.Length == 0)
                    continue;

                var replaced = new Material[current.Length];
                for (int s = 0; s < current.Length; s++)
                {
                    Material chosen = teamMats[s % teamMats.Count];
                    replaced[s] = chosen != null ? chosen : current[s];
                }

                renderer.sharedMaterials = replaced;
            }
        }

        /// <summary>
        /// [TITAN-ORBIT] Proxy must not participate in physics or NetCode — ECS ghost is authoritative.
        /// </summary>
        public static void StripPhysicsAndNetworking(GameObject root)
        {
            // --- Strip components ---
            foreach (var col in root.GetComponentsInChildren<Collider>(true))
                Object.Destroy(col);
            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true))
                Object.Destroy(rb);
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;
                string typeName = component.GetType().Name;
                if (typeName.Contains("Network") || typeName.Contains("Netcode") || typeName.Contains("ClientNetwork"))
                    Object.Destroy(component);
            }
        }
    }
}
