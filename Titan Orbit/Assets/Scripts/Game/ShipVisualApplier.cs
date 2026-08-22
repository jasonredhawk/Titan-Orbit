using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Instantiates ship-family chassis prefabs as render-only GameObject proxies and applies
    /// team-colored materials. Called by EcsWorldVisualizer when spawning or respawning ship visuals.
    /// Strips Rigidbodies and NetCode MonoBehaviour components so the proxy cannot run a
    /// second GameObject physics world. Authoritative hulls are Unity.Physics
    /// <c>PhysicsCollider</c> + <c>PhysicsVelocity</c> on the ECS ghost (that pair is the
    /// rigidbody). Authored Box/Capsule colliders stay on the proxy, disabled, so they
    /// remain visible in the Inspector — same as MEGA module boxes.
    /// <para>
    /// Prefers an exact chassis id from <see cref="PlanetShipFamilyConfig"/> (level + branch ladder)
    /// so moon-orbit upgrade-tree clicks load the hull that was selected, not a generic level placeholder.
    /// </para>
    /// </summary>
    public static class ShipVisualApplier
    {
        /// <summary>
        /// Legacy level-only create. Prefer <see cref="TryCreateShipVisualForChassis"/> when branch/chassis is known.
        /// </summary>
        public static bool TryCreateShipVisual(
            ShipFamilyDefinition family,
            GameObject prefabOverride,
            TeamId team,
            int shipLevel,
            out GameObject instance)
        {
            return TryCreateShipVisualForChassis(
                family,
                prefabOverride,
                team,
                shipLevel,
                chassisId: null,
                out instance);
        }

        /// <summary>
        /// Creates a ship visual for a specific chassis id (upgrade-tree slot).
        /// Resolves the prefab from <see cref="PlanetShipFamilyConfig"/> when <paramref name="chassisId"/> is set;
        /// falls back to level-based family lookup, then <paramref name="prefabOverride"/>.
        /// </summary>
        /// <param name="familyFallback">Family used for team materials / level fallback when chassis family is unknown.</param>
        /// <param name="prefabOverride">Optional forced prefab (inspector override on the visualizer).</param>
        /// <param name="team">Team palette for materials.</param>
        /// <param name="shipLevel">Used only for legacy level fallback when chassis id is empty.</param>
        /// <param name="chassisId">Exact ladder chassis id (e.g. AstroEagle_T3) from ShipStatApplyLogic.</param>
        /// <param name="instance">Instantiated proxy root, or null on failure.</param>
        public static bool TryCreateShipVisualForChassis(
            ShipFamilyDefinition familyFallback,
            GameObject prefabOverride,
            TeamId team,
            int shipLevel,
            string chassisId,
            out GameObject instance)
        {
            // --- Resolve prefab + family for this chassis ---
            instance = null;
            GameObject prefab = prefabOverride;
            ShipFamilyDefinition family = familyFallback;

            // [TITAN-ORBIT] Exact chassis from PlanetShipFamilyConfig upgradeTree — matches moon menu slots.
            if (prefab == null && !string.IsNullOrEmpty(chassisId))
            {
                var config = ShipStatApplyLogic.Config;
                if (config != null)
                    prefab = config.GetPrefabByChassisId(chassisId);

                if (prefab == null && MegaShipCatalog.IsMegaChassisId(chassisId))
                {
                    var mega = MegaShipCatalog.Load();
                    if (mega != null)
                        prefab = mega.GetPrefabByChassisId(chassisId);
                }

                if (ShipStatApplyLogic.TryResolveFamilyForChassisId(chassisId, out ShipFamilyDefinition resolvedFamily)
                    && resolvedFamily != null)
                {
                    family = resolvedFamily;
                }
            }

            // [LEGACY] Level-only pick when chassis id is missing (old callers / incomplete loadout).
            if (prefab == null && family != null)
                family.TryGetVisualPrefabForLevel(shipLevel, out prefab);

            if (prefab == null)
                return false;

            // --- Instantiate proxy ---
            instance = Object.Instantiate(prefab);
            instance.name = prefab.name + "Proxy";
            StripPhysicsAndNetworking(instance, keepColliders: true);
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
        /// [TITAN-ORBIT] Proxy must not participate in GameObject PhysX or NetCode.
        /// Keep authored colliders (disabled) so the Inspector still shows the hull boxes.
        /// </summary>
        public static void StripPhysicsAndNetworking(GameObject root)
        {
            StripPhysicsAndNetworking(root, keepColliders: false);
        }

        /// <summary>
        /// [TITAN-ORBIT] Proxy must not participate in physics or NetCode — ECS ghost is authoritative.
        /// </summary>
        /// <param name="keepColliders">
        /// When true, leave UnityEngine colliders on the hierarchy and disable them instead of
        /// Destroy so they still show in the Inspector. Do not add a Rigidbody — that would
        /// create a second physics world that fights ECS.
        /// </param>
        public static void StripPhysicsAndNetworking(GameObject root, bool keepColliders)
        {
            if (root == null)
                return;

            // --- Colliders ---
            foreach (var col in root.GetComponentsInChildren<Collider>(true))
            {
                if (col == null)
                    continue;
                if (keepColliders)
                    col.enabled = false;
                else
                    Object.Destroy(col);
            }

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
