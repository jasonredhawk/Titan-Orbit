using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

// EcsGameBridge lives in TitanOrbit (parent of this namespace).

namespace TitanOrbit.Game
{
    /// <summary>
    /// MEGA turret presentation is frozen — shots aim at the target in sim, not by rotating meshes.
    /// </summary>
    public static class MegaShipWeaponVisualSync
    {
        /// <summary>No-op. Turret GameObjects are not rotated.</summary>
        public static void Apply(EntityManager em, Entity shipEntity, GameObject proxy)
        {
            _ = em;
            _ = shipEntity;
            _ = proxy;
        }

        /// <summary>XZ unit vector; degenerate → +Z.</summary>
        public static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.forward;
        }
    }

    /// <summary>
    /// One yaw joint per MEGA turret assembly, found anywhere under the hull.
    /// </summary>
    [DefaultExecutionOrder(67020)]
    public sealed class MegaShipWeaponVisualBinding : MonoBehaviour
    {
        public Transform[] YawRoots;
        public Transform[] Barrels;
        public Vector3[] RestBarrelLocalFwd;
        public Entity ShipEntity;

        static readonly List<Transform> JointScratch = new List<Transform>(16);

        public static MegaShipWeaponVisualBinding Ensure(GameObject root, Entity shipEntity, int expectedMountCount = 0)
        {
            if (root == null)
                return null;

            var binding = root.GetComponent<MegaShipWeaponVisualBinding>();
            if (binding != null
                && binding.YawRoots != null
                && binding.RestBarrelLocalFwd != null
                && binding.Barrels != null
                && (expectedMountCount <= 0 || binding.YawRoots.Length == expectedMountCount))
            {
                binding.ShipEntity = shipEntity;
                return binding;
            }

            if (binding == null)
                binding = root.AddComponent<MegaShipWeaponVisualBinding>();

            binding.ShipEntity = shipEntity;
            CollectUniqueYawJoints(root.transform, JointScratch);
            int count = JointScratch.Count;
            binding.YawRoots = new Transform[count];
            binding.Barrels = new Transform[count];
            binding.RestBarrelLocalFwd = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                Transform joint = JointScratch[i];
                binding.YawRoots[i] = joint;
                Transform barrel = FindBarrelUnder(joint);
                binding.Barrels[i] = barrel != null ? barrel : joint;
                if (joint != null)
                {
                    Vector3 barrelFwd = MegaShipWeaponVisualSync.Flatten(binding.Barrels[i].forward);
                    binding.RestBarrelLocalFwd[i] = joint.InverseTransformDirection(barrelFwd);
                    if (binding.RestBarrelLocalFwd[i].sqrMagnitude < 1e-6f)
                        binding.RestBarrelLocalFwd[i] = Vector3.forward;
                }
                else
                {
                    binding.RestBarrelLocalFwd[i] = Vector3.forward;
                }
            }

            return binding;
        }

        /// <summary>
        /// Walks the whole hull (including nested prefab instances) and keeps one yaw joint
        /// per turret: a child named <c>Turret</c> / <c>TurretBase</c>, not the rolled socket.
        /// </summary>
        public static void CollectUniqueYawJoints(Transform hull, List<Transform> into)
        {
            into.Clear();
            if (hull == null)
                return;

            MegaShipPartClassifier.CollectWeaponAssemblies(hull, into);
            var seen = new HashSet<int>();
            int write = 0;
            for (int i = 0; i < into.Count; i++)
            {
                Transform assembly = into[i];
                Transform joint = ResolveYawRoot(assembly, hull);
                if (joint == null)
                    joint = assembly;
                if (!seen.Add(joint.GetInstanceID()))
                    continue;
                into[write++] = joint;
            }

            if (write < into.Count)
                into.RemoveRange(write, into.Count - write);
        }

        /// <summary>Turret / TurretBase joint, walking up and down through nested parents.</summary>
        public static Transform ResolveYawRoot(Transform weapon, Transform hull)
        {
            if (weapon == null)
                return null;

            Transform down = FindYawJointUnder(weapon);
            if (down != null)
                return down;

            Transform t = weapon;
            while (t != null && t != hull)
            {
                if (IsYawJointName(t.name))
                    return t;
                t = t.parent;
            }

            return IsSocketOrFolderName(weapon.name) ? weapon : weapon;
        }

        static Transform FindYawJointUnder(Transform root)
        {
            if (root == null)
                return null;

            var all = root.GetComponentsInChildren<Transform>(true);
            Transform best = null;
            int bestDepth = int.MaxValue;
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null || t == root || !IsYawJointName(t.name))
                    continue;
                int depth = 0;
                Transform w = t;
                while (w != null && w != root)
                {
                    depth++;
                    w = w.parent;
                }

                if (w != root)
                    continue;
                if (depth < bestDepth)
                {
                    bestDepth = depth;
                    best = t;
                }
            }

            return best;
        }

        static Transform FindBarrelUnder(Transform joint)
        {
            if (joint == null)
                return null;

            var all = joint.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null || t == joint)
                    continue;
                if (MegaShipPartClassifier.IsWeaponMountTransform(t) && !IsYawJointName(t.name))
                    return t;
            }

            return joint;
        }

        /// <summary>The rotating piece inside a StarSparrow turret prefab.</summary>
        public static bool IsYawJointName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (name.IndexOf("TurretBase", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return string.Equals(name, "Turret", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Placed hull socket or folder — must not be yawed (often rolled 90°).</summary>
        public static bool IsSocketOrFolderName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (string.Equals(name, "Turrets", System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (name.IndexOf("Turret_Single", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (name.IndexOf("Parts_Turret", System.StringComparison.OrdinalIgnoreCase) >= 0
                && !string.Equals(name, "Turret", System.StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
    }
}
