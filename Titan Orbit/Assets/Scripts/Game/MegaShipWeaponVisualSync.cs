using System.Collections.Generic;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

// EcsGameBridge lives in TitanOrbit (parent of this namespace).

namespace TitanOrbit.Game
{
    /// <summary>
    /// Live display poses for MEGA turret LookAt. Filled from hybrid proxies each frame
    /// so barrels track the <b>target</b>, not the lagged lead intercept.
    /// </summary>
    public static class MegaShipWeaponVisualTargets
    {
        struct Entry
        {
            public int GhostId;
            public Vector3 DisplayPos;
        }

        static readonly List<Entry> Entries = new List<Entry>(64);

        /// <summary>Rebuild from current hybrid proxies (no new ECS gather query).</summary>
        public static void RebuildFromProxies(EntityManager em, Dictionary<Entity, GameObject> proxies)
        {
            Entries.Clear();
            if (proxies == null)
                return;

            foreach (var kv in proxies)
            {
                if (kv.Value == null || !em.Exists(kv.Key))
                    continue;

                int ghostId = MegaShipWeaponAim.ReadGhostId(em, kv.Key);
                if (ghostId == 0)
                    continue;

                Entries.Add(new Entry
                {
                    GhostId = ghostId,
                    DisplayPos = kv.Value.transform.position,
                });
            }
        }

        /// <summary>Display position of a ghost id, tiled near <paramref name="reference"/>.</summary>
        public static bool TryGetDisplayPos(int ghostId, Vector3 reference, out Vector3 displayPos)
        {
            displayPos = reference;
            if (ghostId == 0)
                return false;

            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].GhostId != ghostId)
                    continue;

                displayPos = TileNear(Entries[i].DisplayPos, reference);
                return true;
            }

            return false;
        }

        /// <summary>Tiles a ghosted XZ point next to <paramref name="reference"/> (toroidal map).</summary>
        public static bool TryGetTiledPoint(float worldX, float worldZ, Vector3 reference, out Vector3 displayPos)
        {
            displayPos = reference;
            if (math.abs(worldX) + math.abs(worldZ) < 0.05f)
                return false;
            displayPos = TileNear(new Vector3(worldX, reference.y, worldZ), reference);
            return true;
        }

        static Vector3 TileNear(Vector3 logical, Vector3 reference)
        {
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return logical;

            float3 tiled = ToroidalMapEcs.GetDisplayPosition(
                (float3)logical, (float3)reference, mapW, mapH);
            return new Vector3(tiled.x, logical.y, tiled.z);
        }
    }

    /// <summary>
    /// Hybrid presentation: while a MEGA gun is tracking, LookAt the lock's <b>live</b>
    /// display pose from the current muzzle. Do not yaw to the lead intercept — that
    /// heading swings away from the target while the hull accelerates or turns.
    /// Idle guns park at ship-forward + hull-local yaw.
    /// </summary>
    public static class MegaShipWeaponVisualSync
    {
        /// <summary>Rotates cached turret joints on <paramref name="proxy"/> to the live MEGA aim.</summary>
        public static void Apply(EntityManager em, Entity shipEntity, GameObject proxy)
        {
            if (proxy == null || shipEntity == Entity.Null || !em.Exists(shipEntity))
                return;
            if (!em.HasComponent<MegaShipState>(shipEntity)
                || !em.GetComponentData<MegaShipState>(shipEntity).IsMega)
                return;

            var binding = MegaShipWeaponVisualBinding.Ensure(proxy, shipEntity);
            if (binding == null || binding.YawRoots == null || binding.YawRoots.Length == 0)
                return;

            bool hasGunners = em.HasBuffer<MegaShipGunnerSlotElement>(shipEntity);
            var gunners = hasGunners
                ? em.GetBuffer<MegaShipGunnerSlotElement>(shipEntity)
                : default;
            bool hasMounts = em.HasBuffer<ShipWeaponMountElement>(shipEntity);
            var mounts = hasMounts
                ? em.GetBuffer<ShipWeaponMountElement>(shipEntity)
                : default;

            Vector3 hullFwd = Flatten(proxy.transform.forward);
            Quaternion shipHeading = Quaternion.LookRotation(hullFwd, Vector3.up);
            bool haveOwnerMouseDir = TryGetLocalOwnerMouseWorldDir(
                em, shipEntity, out Vector3 ownerMouseDir);
            bool ownerFiring = em.HasComponent<LocalPlayerShipTag>(shipEntity)
                && em.HasComponent<ShipInput>(shipEntity)
                && em.GetComponentData<ShipInput>(shipEntity).Fire.IsSet;

            int count = binding.YawRoots.Length;
            for (int i = 0; i < count; i++)
            {
                Transform yawRoot = binding.YawRoots[i];
                if (yawRoot == null)
                    continue;

                bool occupied = hasGunners && i < gunners.Length
                    && gunners[i].OccupiedByNetworkId != 0;
                Quaternion desiredBarrelWorld;
                float yawDeg = 0f;
                bool tracking = hasGunners && i < gunners.Length
                    && MegaShipWeaponAim.IsTrackingAim(gunners[i]);
                if (haveOwnerMouseDir && !occupied)
                {
                    desiredBarrelWorld = Quaternion.LookRotation(ownerMouseDir, Vector3.up);
                    binding.RememberWorldYaw(i, PlanarYaw(ownerMouseDir));
                }
                else if (TryGetLiveTargetDir(
                    hasGunners, gunners, i, yawRoot.position, out Vector3 toTarget))
                {
                    desiredBarrelWorld = Quaternion.LookRotation(toTarget, Vector3.up);
                    binding.RememberWorldYaw(i, PlanarYaw(toTarget));
                }
                else
                {
                    bool haveYaw = false;
                    if (hasGunners && i < gunners.Length)
                    {
                        yawDeg = gunners[i].CurrentYawDeg;
                        haveYaw = true;
                    }
                    else if (hasMounts && i < mounts.Length)
                    {
                        yawDeg = MegaShipWeaponAim.GetLocalYawDeg(mounts[i].LocalRotation);
                        haveYaw = true;
                    }

                    if (!haveYaw)
                        continue;

                    // Client predicted TargetDistance often drops to 0 between snapshots.
                    // Hold the last LookAt / world yaw while Fire is still down.
                    if (tracking)
                    {
                        desiredBarrelWorld = Quaternion.AngleAxis(yawDeg, Vector3.up);
                        binding.RememberWorldYaw(i, yawDeg);
                    }
                    else if (binding.TryGetHeldWorldYaw(i, ownerFiring, out float heldYaw))
                    {
                        desiredBarrelWorld = Quaternion.AngleAxis(heldYaw, Vector3.up);
                    }
                    else
                    {
                        desiredBarrelWorld = shipHeading * Quaternion.AngleAxis(yawDeg, Vector3.up);
                    }
                }

                Vector3 restLocalFwd = binding.RestBarrelLocalFwd != null && i < binding.RestBarrelLocalFwd.Length
                    ? binding.RestBarrelLocalFwd[i]
                    : Vector3.forward;
                if (restLocalFwd.sqrMagnitude < 1e-6f)
                    restLocalFwd = Vector3.forward;
                Quaternion restLook = Quaternion.LookRotation(restLocalFwd.normalized, Vector3.up);
                yawRoot.rotation = desiredBarrelWorld * Quaternion.Inverse(restLook);
            }
        }

        /// <summary>
        /// World-planar direction from this muzzle to the sticky lock (live ghost, or
        /// tiled current target point — not the lead intercept).
        /// </summary>
        static bool TryGetLiveTargetDir(
            bool hasGunners,
            DynamicBuffer<MegaShipGunnerSlotElement> gunners,
            int mountIndex,
            Vector3 muzzleDisplay,
            out Vector3 worldDir)
        {
            worldDir = Vector3.forward;
            if (!hasGunners || mountIndex < 0 || mountIndex >= gunners.Length)
                return false;

            var slot = gunners[mountIndex];
            int ghostId = slot.TargetGhostId;
            if (ghostId != 0
                && MegaShipWeaponVisualTargets.TryGetDisplayPos(
                    ghostId, muzzleDisplay, out Vector3 lockPos))
            {
                Vector3 toLock = Flatten(lockPos - muzzleDisplay);
                if (toLock.sqrMagnitude > 1e-6f)
                {
                    worldDir = toLock;
                    return true;
                }
            }

            // AimWorldX/Z is the target's current point. Planets have ghostId 0
            // (stripped map bodies), so this is the usual LookAt path.
            if (MegaShipWeaponAim.IsTrackingAim(in slot)
                && MegaShipWeaponVisualTargets.TryGetTiledPoint(
                    slot.AimWorldX, slot.AimWorldZ, muzzleDisplay, out Vector3 aimPos))
            {
                Vector3 toAim = Flatten(aimPos - muzzleDisplay);
                if (toAim.sqrMagnitude > 1e-6f)
                {
                    worldDir = toAim;
                    return true;
                }
            }

            return false;
        }

        static float PlanarYaw(Vector3 v)
        {
            v = Flatten(v);
            return Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Local MEGA owner holding Shift: live mouse world direction (not a reconstructed
        /// point LookAt — that swings as the predicted hull moves).
        /// </summary>
        static bool TryGetLocalOwnerMouseWorldDir(
            EntityManager em,
            Entity shipEntity,
            out Vector3 worldDir)
        {
            worldDir = Vector3.forward;
            if (!em.HasComponent<LocalPlayerShipTag>(shipEntity)
                || !em.HasComponent<ShipInput>(shipEntity))
                return false;

            var input = em.GetComponentData<ShipInput>(shipEntity);
            if (!input.Overdrive)
                return false;

            Vector3 dir = Flatten(new Vector3(input.AimPlanarDir.x, 0f, input.AimPlanarDir.y));
            if (dir.sqrMagnitude < 1e-4f)
                return false;

            worldDir = dir;
            return true;
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
        float[] _heldWorldYawDeg;
        float[] _heldWorldYawTime;

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
            binding._heldWorldYawDeg = new float[count];
            binding._heldWorldYawTime = new float[count];
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

        /// <summary>Store the last ghosted world fire heading for this mount.</summary>
        public void RememberWorldYaw(int mountIndex, float worldYawDeg)
        {
            EnsureHoldSlots();
            if (mountIndex < 0 || mountIndex >= _heldWorldYawDeg.Length)
                return;
            _heldWorldYawDeg[mountIndex] = worldYawDeg;
            _heldWorldYawTime[mountIndex] = Time.unscaledTime;
        }

        /// <summary>
        /// True when we should keep the last tracking world yaw (Fire held, or the
        /// ClientWorld TargetDistance flicker within 0.4s of a real lock).
        /// </summary>
        public bool TryGetHeldWorldYaw(int mountIndex, bool ownerFiring, out float worldYawDeg)
        {
            worldYawDeg = 0f;
            EnsureHoldSlots();
            if (mountIndex < 0 || mountIndex >= _heldWorldYawDeg.Length)
                return false;
            if (_heldWorldYawTime[mountIndex] <= 0f)
                return false;
            // Drop the hold on Fire release so a new press does not snap to the old lock.
            if (!ownerFiring)
            {
                _heldWorldYawTime[mountIndex] = 0f;
                return false;
            }
            if (Time.unscaledTime - _heldWorldYawTime[mountIndex] > 0.4f)
                return false;
            worldYawDeg = _heldWorldYawDeg[mountIndex];
            return true;
        }

        void EnsureHoldSlots()
        {
            int n = YawRoots != null ? YawRoots.Length : 0;
            if (_heldWorldYawDeg != null && _heldWorldYawDeg.Length == n)
                return;
            _heldWorldYawDeg = new float[n];
            _heldWorldYawTime = new float[n];
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
