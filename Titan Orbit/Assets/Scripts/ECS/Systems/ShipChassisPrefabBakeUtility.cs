using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS.Authoring;
using TitanOrbit.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Editor and runtime utility that scans a USC chassis prefab and produces catalog data
    /// for Entities Graphics render parts, weapon mounts, and wing tractor beams.
    /// </summary>
    public static class ShipChassisPrefabBakeUtility
    {
        /// <summary>
        /// Bakes render parts and attachment points from a chassis prefab hierarchy.
        /// </summary>
        public static ShipChassisVisualEntry BakeVisualEntry(
            GameObject chassisPrefab,
            string chassisId,
            ShipFamilyDefinition family,
            TeamId teamForMaterials)
        {
            var entry = new ShipChassisVisualEntry { ChassisId = chassisId };
            if (chassisPrefab == null)
                return entry;

            float presentationScale = BodyCollisionMath.ShipPresentationScale;
            GameObject instance = null;

            try
            {
                instance = Object.Instantiate(chassisPrefab);
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.transform.localScale = Vector3.one;
                var root = instance.transform;

                BakeRenderParts(root, family, teamForMaterials, presentationScale, entry);
                BakeWeaponMounts(root, entry);
                BakeWingTractorBeams(root, entry);
            }
            finally
            {
                DestroyTemporaryInstance(instance);
            }

            return entry;
        }

        /// <summary>
        /// Destroys a temporary prefab instance created during catalog bake.
        /// [EDITOR] Menu bake runs in edit mode — <c>Destroy</c> is invalid; use <c>DestroyImmediate</c>.
        /// </summary>
        static void DestroyTemporaryInstance(GameObject instance)
        {
            if (instance == null)
                return;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                Object.DestroyImmediate(instance);
                return;
            }
#endif
            Object.Destroy(instance);
        }

        static void BakeRenderParts(
            Transform root,
            ShipFamilyDefinition family,
            TeamId team,
            float presentationScale,
            ShipChassisVisualEntry entry)
        {
            var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            List<Material> teamMaterials = family != null ? family.GetMaterialsForTeam(team) : null;

            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null)
                    continue;

                var meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null)
                    continue;

                Matrix4x4 relative = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                DecomposeMatrix(relative, presentationScale, out float3 position, out quaternion rotation, out float3 scale);

                Material material = ResolveMaterial(renderer, teamMaterials);
                entry.RenderParts.Add(new ShipChassisRenderPart
                {
                    Mesh = meshFilter.sharedMesh,
                    Material = material,
                    LocalPosition = position,
                    LocalRotation = rotation,
                    LocalScale = scale,
                });
            }
        }

        static Material ResolveMaterial(MeshRenderer renderer, List<Material> teamMaterials)
        {
            if (teamMaterials == null || teamMaterials.Count == 0)
                return renderer.sharedMaterial;

            Material[] current = renderer.sharedMaterials;
            if (current == null || current.Length == 0)
                return renderer.sharedMaterial;

            int slot = 0;
            Material chosen = teamMaterials[slot % teamMaterials.Count];
            return chosen != null ? chosen : current[0];
        }

        /// <summary>
        /// Collects weapon mount children into the catalog entry.
        /// Positions/rotations are <b>hull-root-local</b> so nested Weapon children line up with
        /// ship <c>LocalTransform</c> at fire time. Empty list = intentional unarmed chassis.
        /// </summary>
        static void BakeWeaponMounts(Transform root, ShipChassisVisualEntry entry)
        {
            // --- Authoring markers first ---
            var mountAuthorings = root.GetComponentsInChildren<ShipWeaponMountAuthoring>(true);
            for (int i = 0; i < mountAuthorings.Length; i++)
            {
                var mountAuth = mountAuthorings[i];
                if (mountAuth == null || mountAuth.transform == root)
                    continue;

                GetHullRootLocalPose(root, mountAuth.transform, out float3 localPos, out quaternion localRot);
                entry.WeaponMounts.Add(new ShipWeaponMountBakeData
                {
                    LocalPosition = localPos,
                    LocalRotation = localRot,
                    DirectionAngleDeg = mountAuth.DirectionAngleDeg,
                    CannonIndex = mountAuth.CannonIndex,
                });
            }

            if (entry.WeaponMounts.Count > 0)
                return;

            // --- Name / family weapon id scan (matches ShipWeaponMountCollector + IsWeaponComponent) ---
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root)
                    continue;
                if (!LooksLikeWeaponChildForBake(t))
                    continue;

                GetHullRootLocalPose(root, t, out float3 localPos, out quaternion localRot);
                entry.WeaponMounts.Add(new ShipWeaponMountBakeData
                {
                    LocalPosition = localPos,
                    LocalRotation = localRot,
                    DirectionAngleDeg = 0f,
                    CannonIndex = entry.WeaponMounts.Count,
                });
            }

            // [TITAN-ORBIT] Zero mounts is valid (unarmed). Do not invent a centerline muzzle.
            if (entry.WeaponMounts.Count == 0 && !string.IsNullOrEmpty(entry.ChassisId))
                Debug.Log($"[ChassisBake] Chassis '{entry.ChassisId}' has no Weapon mounts — unarmed.");
        }

        /// <summary>
        /// Converts a child world pose into hull-root local space (handles nested Weapon children).
        /// </summary>
        public static void GetHullRootLocalPose(
            Transform hullRoot,
            Transform mount,
            out float3 localPosition,
            out quaternion localRotation)
        {
            // --- Hull-root local ---
            // [UNITY] InverseTransformPoint / relative rotation — not immediate-parent localPosition.
            Vector3 lp = hullRoot.InverseTransformPoint(mount.position);
            Quaternion lr = Quaternion.Inverse(hullRoot.rotation) * mount.rotation;
            localPosition = lp;
            localRotation = lr;
        }

        /// <summary>
        /// True when a child should become a sim mount (family weapon id or "Weapon" in name).
        /// Shared rule with live client discovery — keep offensive barrels in the buffer.
        /// </summary>
        public static bool LooksLikeWeaponChildForBake(Transform t)
        {
            if (t == null)
                return false;
            string name = t.name;
            if (string.IsNullOrEmpty(name))
                return false;
            if (name.IndexOf("Weapon", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            string id = name;
            int underscore = name.IndexOf('_');
            if (underscore > 0 && underscore < name.Length - 1)
                id = name.Substring(underscore + 1);

            return ShipComponentAbilityStatsMath.IsWeaponComponent(id)
                   || ShipComponentAbilityStatsMath.IsWeaponComponent(name);
        }

        static void BakeWingTractorBeams(Transform root, ShipChassisVisualEntry entry)
        {
            var wingAuthorings = root.GetComponentsInChildren<ShipWingTractorBeamAuthoring>(true);
            for (int i = 0; i < wingAuthorings.Length; i++)
            {
                var wing = wingAuthorings[i];
                if (wing == null || wing.transform == root)
                    continue;

                var t = wing.transform;
                entry.WingTractorBeams.Add(new ShipWingTractorBeamBakeData
                {
                    LocalPosition = t.localPosition,
                    TractorBeamDistance = wing.tractorBeamDistance,
                    TractorBeamDistancePerLevel = wing.tractorBeamDistancePerLevel,
                    TractorBeamPower = wing.tractorBeamPower,
                    TractorBeamPowerPerLevel = wing.tractorBeamPowerPerLevel,
                    MaxGems = wing.maxGems,
                    MaxGemsPerLevel = wing.maxGemsPerLevel,
                });
            }
        }

        static void DecomposeMatrix(
            Matrix4x4 matrix,
            float presentationScale,
            out float3 position,
            out quaternion rotation,
            out float3 lossyScale)
        {
            float3x3 rotScale = new float3x3((float4x4)matrix);
            lossyScale = new float3(
                math.length(rotScale.c0),
                math.length(rotScale.c1),
                math.length(rotScale.c2));
            lossyScale = math.max(lossyScale, new float3(1e-6f));

            rotScale.c0 /= lossyScale.x;
            rotScale.c1 /= lossyScale.y;
            rotScale.c2 /= lossyScale.z;

            rotation = new quaternion(rotScale);
            position = (float3)matrix.GetPosition() * presentationScale;
            lossyScale *= presentationScale;
        }
    }
}
