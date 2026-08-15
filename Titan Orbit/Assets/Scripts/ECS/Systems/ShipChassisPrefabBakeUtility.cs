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
        /// Counts distinct weapon <b>bodies</b> on a chassis prefab (no Instantiates).
        /// Used to detect undercounted <see cref="ShipWeaponMountElement"/> buffers after the
        /// old bake stopped at the first <see cref="ShipWeaponMountAuthoring"/>.
        /// </summary>
        public static int CountDistinctWeaponBodies(GameObject chassisPrefab)
        {
            if (chassisPrefab == null)
                return 0;

            var bodies = new List<Transform>(8);
            CollectDistinctWeaponBodies(chassisPrefab.transform, bodies);
            return bodies.Count;
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
        /// One mount per distinct weapon <b>body</b> (not every Weapon-named tip/mesh child).
        /// <para>
        /// [TITAN-ORBIT] Old Path A returned after any <see cref="ShipWeaponMountAuthoring"/>, so a
        /// hull with one marker + three unnamed Weapon children got a single fire slot while the
        /// hybrid GO showed four barrels. Name-scan of every Weapon* child over-counted tips.
        /// This bake walks distinct weapon bodies (same climb idea as wing bodies) and pulls
        /// CannonIndex / angle from an authoring under that body when present.
        /// </para>
        /// </summary>
        static void BakeWeaponMounts(Transform root, List<ShipWeaponMountBakeData> dst, string chassisIdForLog = null)
        {
            if (dst == null || root == null)
                return;

            // --- Distinct weapon bodies (one muzzle slot each) ---
            var bodies = new List<Transform>(8);
            CollectDistinctWeaponBodies(root, bodies);
            if (bodies.Count == 0)
            {
                // [TITAN-ORBIT] Zero mounts is valid (unarmed). Do not invent a centerline muzzle.
                if (!string.IsNullOrEmpty(chassisIdForLog))
                    Debug.Log($"[ChassisBake] Chassis '{chassisIdForLog}' has no Weapon mounts — unarmed.");
                return;
            }

            var authorings = root.GetComponentsInChildren<ShipWeaponMountAuthoring>(true);

            for (int bi = 0; bi < bodies.Count; bi++)
            {
                Transform body = bodies[bi];
                ShipWeaponMountAuthoring bestAuth = FindAuthoringForWeaponBody(root, body, authorings);

                Transform poseSource = bestAuth != null ? bestAuth.transform : body;
                GetHullRootLocalPose(root, poseSource, out float3 localPos, out quaternion localRot);

                dst.Add(new ShipWeaponMountBakeData
                {
                    LocalPosition = localPos,
                    // Planar yaw only — pitched meshes must not collapse ShipWeaponPose aim to +Z.
                    LocalRotation = ToPlanarYawLocalRotation(localRot),
                    DirectionAngleDeg = bestAuth != null ? bestAuth.DirectionAngleDeg : 0f,
                    // Prefer authored index; EnsureUniqueCannonIndices fixes all-zero prefabs.
                    CannonIndex = bestAuth != null ? bestAuth.CannonIndex : bi,
                });
            }

            // [TITAN-ORBIT] Many prefabs leave every CannonIndex at 0 — assign discovery order
            // so round-robin buffer slots stay paired with the same live GO barrel.
            EnsureUniqueCannonIndices(dst);
        }

        /// <summary>
        /// Fills <paramref name="dst"/> with one transform per distinct weapon body under
        /// <paramref name="root"/>. Dedupes nested Weapon tip/mesh children into their body root.
        /// </summary>
        static void CollectDistinctWeaponBodies(Transform root, List<Transform> dst)
        {
            dst.Clear();
            if (root == null)
                return;

            var seen = new HashSet<int>();
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null || t == root || !LooksLikeWeaponChildForBake(t))
                    continue;

                Transform body = ResolveWeaponBodyRoot(t, root);
                if (body == null || !seen.Add(body.GetInstanceID()))
                    continue;

                dst.Add(body);
            }
        }

        /// <summary>
        /// Climb from a Weapon-named tip/marker to its single-weapon body. Stops before multi-weapon
        /// group parents (e.g. a "Weapons" folder).
        /// </summary>
        static Transform ResolveWeaponBodyRoot(Transform weaponMarker, Transform hullRoot)
        {
            Transform body = weaponMarker;
            Transform candidate = weaponMarker.parent;
            int markersInBody = CountWeaponNamedUnder(body);

            while (candidate != null && candidate != hullRoot && LooksLikeWeaponChildForBake(candidate))
            {
                int markersInCandidate = CountWeaponNamedUnder(candidate);
                // Parent owns more weapon-named descendants than this branch → weapon group; stop.
                if (markersInCandidate > markersInBody)
                    break;

                body = candidate;
                markersInBody = markersInCandidate;
                candidate = candidate.parent;
            }

            return body;
        }

        /// <summary>Counts weapon-like transforms under <paramref name="root"/> inclusive.</summary>
        static int CountWeaponNamedUnder(Transform root)
        {
            if (root == null)
                return 0;

            int count = 0;
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && LooksLikeWeaponChildForBake(all[i]))
                    count++;
            }

            return count;
        }

        /// <summary>
        /// Picks a <see cref="ShipWeaponMountAuthoring"/> whose resolved weapon body matches
        /// <paramref name="body"/>, preferring markers closest to the body transform.
        /// </summary>
        static ShipWeaponMountAuthoring FindAuthoringForWeaponBody(
            Transform hullRoot,
            Transform body,
            ShipWeaponMountAuthoring[] authorings)
        {
            if (body == null || authorings == null || authorings.Length == 0)
                return null;

            ShipWeaponMountAuthoring best = null;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < authorings.Length; i++)
            {
                var auth = authorings[i];
                if (auth == null || auth.transform == null || auth.transform == hullRoot)
                    continue;

                Transform authBody = ResolveWeaponBodyRoot(auth.transform, hullRoot);
                if (authBody != body)
                    continue;

                float d = (auth.transform.position - body.position).sqrMagnitude;
                if (d < bestDistSq)
                {
                    bestDistSq = d;
                    best = auth;
                }
            }

            return best;
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
                // [UNITY] InverseTransformPoint needs a consistent root pose; Instantiates at identity.
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
        /// Counts distinct wing <b>bodies</b> on a chassis prefab (no Instantiates).
        /// Used to detect undercounted <see cref="ShipWingTractorBeamElement"/> buffers after the
        /// old bake stopped at the first <see cref="ShipWingTractorBeamAuthoring"/>.
        /// </summary>
        public static int CountDistinctWingBodies(GameObject chassisPrefab)
        {
            if (chassisPrefab == null)
                return 0;

            var bodies = new List<Transform>(8);
            CollectDistinctWingBodies(chassisPrefab.transform, bodies);
            return bodies.Count;
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
        /// One tractor slot per distinct wing <b>body</b> (not every Wing-named tip/mesh child).
        /// <para>
        /// [TITAN-ORBIT] Old Path A returned after the first <see cref="ShipWingTractorBeamAuthoring"/>,
        /// so multi-wing upgrade hulls got a single ECS beam while the hybrid GO still showed every
        /// wing (EnsureWingTractorBeamsOnHierarchy). Path B name-scan of every Wing* child over-counted.
        /// This bake walks distinct wing bodies (same climb rule as beam VFX mid-centers) and pulls
        /// stats from an authoring under that body when present.
        /// </para>
        /// </summary>
        static void BakeWingTractorBeams(Transform root, List<ShipWingTractorBeamBakeData> dst)
        {
            if (root == null || dst == null)
                return;

            // --- Distinct wing bodies (left/right/… not tip children or "Wings" folders) ---
            var bodies = new List<Transform>(8);
            CollectDistinctWingBodies(root, bodies);
            if (bodies.Count == 0)
                return;

            var authorings = root.GetComponentsInChildren<ShipWingTractorBeamAuthoring>(true);

            for (int bi = 0; bi < bodies.Count; bi++)
            {
                Transform body = bodies[bi];
                ShipWingTractorBeamAuthoring bestAuth = FindAuthoringForWingBody(root, body, authorings);

                // Prefer authoring marker pose (often the tip) when present; else wing body root.
                Transform poseSource = bestAuth != null ? bestAuth.transform : body;
                GetHullRootLocalPose(root, poseSource, out float3 localPos, out _);

                if (bestAuth != null)
                {
                    dst.Add(new ShipWingTractorBeamBakeData
                    {
                        LocalPosition = localPos,
                        TractorBeamDistance = bestAuth.tractorBeamDistance,
                        TractorBeamDistancePerLevel = bestAuth.tractorBeamDistancePerExtraLevel,
                        TractorBeamPower = bestAuth.tractorBeamPower,
                        TractorBeamPowerPerLevel = bestAuth.tractorBeamPowerPerExtraLevel,
                        MaxGems = bestAuth.maxGems,
                        MaxGemsPerLevel = bestAuth.maxGemsPerExtraLevel,
                    });
                }
                else
                {
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
        }

        /// <summary>
        /// Fills <paramref name="dst"/> with one transform per distinct wing body under
        /// <paramref name="root"/>. Dedupes nested Wing tip/mesh children into their body root.
        /// </summary>
        static void CollectDistinctWingBodies(Transform root, List<Transform> dst)
        {
            dst.Clear();
            if (root == null)
                return;

            var seen = new HashSet<int>();
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null || t == root || !LooksLikeWingTransformName(t.name))
                    continue;

                Transform body = ResolveWingBodyRootByName(t, root);
                if (body == null || !seen.Add(body.GetInstanceID()))
                    continue;

                dst.Add(body);
            }
        }

        /// <summary>
        /// Climb from a Wing-named tip/marker to its single-wing body. Stops before multi-wing
        /// group parents (e.g. a "Wings" folder) — same rule as <c>GemTractorBeamVisual</c>.
        /// </summary>
        static Transform ResolveWingBodyRootByName(Transform wingMarker, Transform hullRoot)
        {
            Transform body = wingMarker;
            Transform candidate = wingMarker.parent;
            int markersInBody = CountWingNamedUnder(body);

            while (candidate != null && candidate != hullRoot && LooksLikeWingTransformName(candidate.name))
            {
                int markersInCandidate = CountWingNamedUnder(candidate);
                // Parent owns more wing-named descendants than this branch → wing group; stop.
                if (markersInCandidate > markersInBody)
                    break;

                body = candidate;
                markersInBody = markersInCandidate;
                candidate = candidate.parent;
            }

            return body;
        }

        /// <summary>Counts Wing-named (non-Weapon) transforms under <paramref name="root"/> inclusive.</summary>
        static int CountWingNamedUnder(Transform root)
        {
            if (root == null)
                return 0;

            int count = 0;
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && LooksLikeWingTransformName(all[i].name))
                    count++;
            }

            return count;
        }

        /// <summary>True when the transform name is a wing slot (Wing*, not Weapon*).</summary>
        static bool LooksLikeWingTransformName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (name.IndexOf("Weapon", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return name.IndexOf("Wing", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Picks a <see cref="ShipWingTractorBeamAuthoring"/> whose resolved wing body matches
        /// <paramref name="body"/>, preferring markers closest to the body transform.
        /// </summary>
        static ShipWingTractorBeamAuthoring FindAuthoringForWingBody(
            Transform hullRoot,
            Transform body,
            ShipWingTractorBeamAuthoring[] authorings)
        {
            if (body == null || authorings == null || authorings.Length == 0)
                return null;

            ShipWingTractorBeamAuthoring best = null;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < authorings.Length; i++)
            {
                var auth = authorings[i];
                if (auth == null || auth.transform == null || auth.transform == hullRoot)
                    continue;

                Transform authBody = ResolveWingBodyRootByName(auth.transform, hullRoot);
                if (authBody != body)
                    continue;

                float d = (auth.transform.position - body.position).sqrMagnitude;
                if (d < bestDistSq)
                {
                    bestDistSq = d;
                    best = auth;
                }
            }

            return best;
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
