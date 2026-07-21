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
                BakeWeaponMounts(root, entry.WeaponMounts);
                BakeWingTractorBeams(root, entry);
            }
            finally
            {
                DestroyTemporaryInstance(instance);
            }

            return entry;
        }

        /// <summary>
        /// Runtime/editor helper: bake weapon mounts from a chassis prefab into
        /// <paramref name="dst"/> (clears the list first). Used by catalog apply so server and
        /// client share the same hull-root locals without depending on a stale ScriptableObject bake.
        /// Mirrors <see cref="TryBakeWingTractorBeams"/> — upgrade-tree hulls often lack a fresh
        /// catalog WeaponMounts list while the prefab still has 2–4 Weapon children.
        /// </summary>
        /// <returns>True when at least one weapon mount was found.</returns>
        public static bool TryBakeWeaponMounts(GameObject chassisPrefab, List<ShipWeaponMountBakeData> dst)
        {
            // --- Validate ---
            if (dst == null)
                return false;
            dst.Clear();
            if (chassisPrefab == null)
                return false;

            GameObject instance = null;
            try
            {
                // --- Temp instance at identity (same as BakeVisualEntry / wing live-bake) ---
                instance = Object.Instantiate(chassisPrefab);
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.transform.localScale = Vector3.one;
                BakeWeaponMounts(instance.transform, dst);
            }
            finally
            {
                DestroyTemporaryInstance(instance);
            }

            return dst.Count > 0;
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
        /// Collects weapon mount children into a catalog entry list.
        /// Positions/rotations are <b>hull-root-local</b> so nested Weapon children line up with
        /// ship <c>LocalTransform</c> at fire time. Empty list = intentional unarmed chassis.
        /// </summary>
        static void BakeWeaponMounts(Transform root, ShipChassisVisualEntry entry) =>
            BakeWeaponMounts(root, entry.WeaponMounts, entry.ChassisId);

        /// <summary>
        /// Shared weapon scan used by catalog bake and runtime <see cref="TryBakeWeaponMounts"/>.
        /// Prefers <see cref="ShipWeaponMountAuthoring"/>; falls back to Weapon-named / family-id
        /// children so upgrade prefabs without markers still get one muzzle per barrel.
        /// </summary>
        static void BakeWeaponMounts(Transform root, List<ShipWeaponMountBakeData> dst, string chassisIdForLog = null)
        {
            if (dst == null || root == null)
                return;

            // --- Authoring markers first ---
            var mountAuthorings = root.GetComponentsInChildren<ShipWeaponMountAuthoring>(true);
            for (int i = 0; i < mountAuthorings.Length; i++)
            {
                var mountAuth = mountAuthorings[i];
                if (mountAuth == null || mountAuth.transform == root)
                    continue;

                GetHullRootLocalPose(root, mountAuth.transform, out float3 localPos, out quaternion localRot);
                dst.Add(new ShipWeaponMountBakeData
                {
                    LocalPosition = localPos,
                    // Planar yaw only — pitched meshes must not collapse ShipWeaponPose aim to +Z.
                    LocalRotation = ToPlanarYawLocalRotation(localRot),
                    DirectionAngleDeg = mountAuth.DirectionAngleDeg,
                    CannonIndex = mountAuth.CannonIndex,
                });
            }

            if (dst.Count > 0)
            {
                // [TITAN-ORBIT] Many prefabs leave every CannonIndex at 0 — assign discovery order
                // so round-robin buffer slots stay paired with the same live GO barrel.
                EnsureUniqueCannonIndices(dst);
                return;
            }

            // --- Name / family weapon id scan (matches ShipWeaponMountCollector + IsWeaponComponent) ---
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root)
                    continue;
                if (!LooksLikeWeaponChildForBake(t))
                    continue;

                GetHullRootLocalPose(root, t, out float3 localPos, out quaternion localRot);
                dst.Add(new ShipWeaponMountBakeData
                {
                    LocalPosition = localPos,
                    LocalRotation = ToPlanarYawLocalRotation(localRot),
                    DirectionAngleDeg = 0f,
                    CannonIndex = dst.Count,
                });
            }

            // [TITAN-ORBIT] Zero mounts is valid (unarmed). Do not invent a centerline muzzle.
            if (dst.Count == 0 && !string.IsNullOrEmpty(chassisIdForLog))
                Debug.Log($"[ChassisBake] Chassis '{chassisIdForLog}' has no Weapon mounts — unarmed.");
        }

        /// <summary>
        /// When every authored <see cref="ShipWeaponMountBakeData.CannonIndex"/> is the same
        /// (usually all 0), rewrite to 0..N-1 in list order so ECS buffer slots and live GO
        /// barrels stay paired during round-robin fire.
        /// </summary>
        static void EnsureUniqueCannonIndices(List<ShipWeaponMountBakeData> mounts)
        {
            if (mounts == null || mounts.Count <= 1)
                return;

            bool allSame = true;
            int first = mounts[0].CannonIndex;
            for (int i = 1; i < mounts.Count; i++)
            {
                if (mounts[i].CannonIndex != first)
                {
                    allSame = false;
                    break;
                }
            }

            if (!allSame)
                return;

            for (int i = 0; i < mounts.Count; i++)
            {
                var m = mounts[i];
                m.CannonIndex = i;
                mounts[i] = m;
            }
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
        /// Flattens a mount local rotation to yaw-only so <see cref="ShipWeaponPose"/> aims along
        /// the weapon’s horizontal facing (pitched barrels no longer fall back to hull +Z).
        /// </summary>
        public static quaternion ToPlanarYawLocalRotation(quaternion localRotation)
        {
            float3 fwd = math.mul(localRotation, new float3(0f, 0f, 1f));
            fwd.y = 0f;
            if (math.lengthsq(fwd) < 0.0001f)
                return quaternion.identity;
            fwd = math.normalize(fwd);
            return quaternion.LookRotationSafe(fwd, new float3(0f, 1f, 0f));
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

        /// <summary>
        /// Runtime/editor helper: bake wing tractor slots from a chassis prefab into
        /// <paramref name="dst"/> (clears the list first). Used by catalog apply so server and
        /// client share the same hull-root locals without depending on a stale ScriptableObject bake.
        /// </summary>
        /// <returns>True when at least one wing was found.</returns>
        public static bool TryBakeWingTractorBeams(GameObject chassisPrefab, List<ShipWingTractorBeamBakeData> dst)
        {
            // --- Validate ---
            if (dst == null)
                return false;
            dst.Clear();
            if (chassisPrefab == null)
                return false;

            GameObject instance = null;
            try
            {
                // --- Temp instance at identity (same as BakeVisualEntry) ---
                instance = Object.Instantiate(chassisPrefab);
                instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                instance.transform.localScale = Vector3.one;
                BakeWingTractorBeams(instance.transform, dst);
            }
            finally
            {
                DestroyTemporaryInstance(instance);
            }

            return dst.Count > 0;
        }

        /// <summary>
        /// Collects wing tractor-beam children into the catalog entry.
        /// Positions are <b>hull-root-local unscaled prefab space</b> (same rule as weapon mounts) —
        /// nested Wing children must not use immediate-parent <c>localPosition</c>.
        /// Runtime <see cref="ShipWingTractorBeamPose.GetWorldPosition"/> applies presentation scale.
        /// </summary>
        static void BakeWingTractorBeams(Transform root, ShipChassisVisualEntry entry) =>
            BakeWingTractorBeams(root, entry.WingTractorBeams);

        /// <summary>
        /// Shared wing scan used by catalog bake and runtime <see cref="TryBakeWingTractorBeams"/>.
        /// Prefers <see cref="ShipWingTractorBeamAuthoring"/>; falls back to Wing-named children
        /// (same rule as <c>StarshipGhostAuthoring</c>) so upgrade prefabs without markers still get beams.
        /// </summary>
        static void BakeWingTractorBeams(Transform root, List<ShipWingTractorBeamBakeData> dst)
        {
            // --- Path A: explicit authoring markers ---
            var wingAuthorings = root.GetComponentsInChildren<ShipWingTractorBeamAuthoring>(true);
            for (int i = 0; i < wingAuthorings.Length; i++)
            {
                var wing = wingAuthorings[i];
                if (wing == null || wing.transform == root)
                    continue;

                // [TITAN-ORBIT] Hull-root local — mirrors BakeWeaponMounts. Parent-local offsets on
                // multi-wing upgrade chassis placed beams far from the visible wing tips.
                GetHullRootLocalPose(root, wing.transform, out float3 localPos, out _);
                dst.Add(new ShipWingTractorBeamBakeData
                {
                    LocalPosition = localPos,
                    TractorBeamDistance = wing.tractorBeamDistance,
                    TractorBeamDistancePerLevel = wing.tractorBeamDistancePerLevel,
                    TractorBeamPower = wing.tractorBeamPower,
                    TractorBeamPowerPerLevel = wing.tractorBeamPowerPerLevel,
                    MaxGems = wing.maxGems,
                    MaxGemsPerLevel = wing.maxGemsPerLevel,
                });
            }

            if (dst.Count > 0)
                return;

            // --- Path B: name scan (Wing*, exclude Weapon*) with default tractor stats ---
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root || string.IsNullOrEmpty(t.name))
                    continue;
                if (t.name.IndexOf("Wing", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (t.name.IndexOf("Weapon", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                GetHullRootLocalPose(root, t, out float3 localPos, out _);
                dst.Add(new ShipWingTractorBeamBakeData
                {
                    LocalPosition = localPos,
                    TractorBeamDistance = 3f,
                    TractorBeamDistancePerLevel = 0.75f,
                    TractorBeamPower = 4f,
                    TractorBeamPowerPerLevel = 1f,
                    MaxGems = 8f,
                    MaxGemsPerLevel = 2f,
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
