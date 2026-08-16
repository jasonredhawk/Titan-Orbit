using TitanOrbit.Data;
using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

// EcsGameBridge lives in TitanOrbit (parent of this namespace).

namespace TitanOrbit.Game
{
    /// <summary>
    /// Hybrid presentation: yaws MEGA turret assemblies on the GameObject proxy to match
    /// the ghosted pad yaw.
    /// <para>
    /// Weapon meshes are often grandchildren (Turrets / hardpoint / TurretBase / TurretBarrel).
    /// We walk up from the classified barrel to the turret assembly, then set that node's
    /// <b>world</b> heading to ship-forward + mount yaw. Intermediate parents keep their
    /// authored local poses. Sim mounts are already hull-root relative (bake uses
    /// InverseTransformPoint), so ECS yaw does not need the extra parents.
    /// </para>
    /// <para>
    /// [HYBRID] Server combat writes <see cref="MegaShipGunnerSlotElement.CurrentYawDeg"/>.
    /// This class only reads — it never writes ECS and never wraps the ship transform.
    /// </para>
    /// </summary>
    public static class MegaShipWeaponVisualSync
    {
        /// <summary>
        /// Rotates cached turret assemblies on <paramref name="proxy"/> to the live MEGA mount yaw.
        /// </summary>
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

            int count = binding.YawRoots.Length;
            for (int i = 0; i < count; i++)
            {
                Transform yawRoot = binding.YawRoots[i];
                if (yawRoot == null)
                    continue;

                float yawDeg = 0f;
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

                // --- World heading = ship forward + hull-local mount yaw ---
                // [TITAN-ORBIT] Set world rotation so nested hardpoints do not swallow the yaw.
                // Rest barrel forward (in yaw-root local) is preserved so a side-facing mesh
                // still points along the desired heading.
                Quaternion desiredBarrelWorld = shipHeading * Quaternion.AngleAxis(yawDeg, Vector3.up);
                Vector3 restLocalFwd = binding.RestBarrelLocalFwd != null && i < binding.RestBarrelLocalFwd.Length
                    ? binding.RestBarrelLocalFwd[i]
                    : Vector3.forward;
                if (restLocalFwd.sqrMagnitude < 1e-6f)
                    restLocalFwd = Vector3.forward;
                Quaternion restLook = Quaternion.LookRotation(restLocalFwd.normalized, Vector3.up);
                yawRoot.rotation = desiredBarrelWorld * Quaternion.Inverse(restLook);
            }
        }

        /// <summary>XZ unit vector; degenerate → +Z.</summary>
        public static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.forward;
        }
    }

    /// <summary>
    /// Caches MEGA turret assemblies (TurretBase when present, else the classified barrel)
    /// found anywhere under the hull, including grandchildren.
    /// </summary>
    [DefaultExecutionOrder(67020)]
    public sealed class MegaShipWeaponVisualBinding : MonoBehaviour
    {
        /// <summary>Transforms that yaw — TurretBase / launcher body, walking up from a nested barrel.</summary>
        public Transform[] YawRoots;

        /// <summary>Classified weapon (barrel / launcher), used for muzzle reticles.</summary>
        public Transform[] Barrels;

        /// <summary>Barrel forward in yaw-root local space at bind (after BankPivot reparent).</summary>
        public Vector3[] RestBarrelLocalFwd;

        /// <summary>Ship ghost this proxy draws. Set by <see cref="Ensure"/>.</summary>
        public Entity ShipEntity;

        /// <summary>
        /// Returns the binding on <paramref name="root"/>, collecting nested weapons the first time.
        /// </summary>
        public static MegaShipWeaponVisualBinding Ensure(GameObject root, Entity shipEntity)
        {
            if (root == null)
                return null;

            var binding = root.GetComponent<MegaShipWeaponVisualBinding>();
            if (binding != null
                && binding.YawRoots != null
                && binding.RestBarrelLocalFwd != null
                && binding.Barrels != null)
            {
                binding.ShipEntity = shipEntity;
                return binding;
            }

            if (binding == null)
                binding = root.AddComponent<MegaShipWeaponVisualBinding>();

            binding.ShipEntity = shipEntity;
            Transform hull = root.transform;
            var all = root.GetComponentsInChildren<Transform>(true);
            int count = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (MegaShipPartClassifier.IsWeaponMountTransform(all[i]))
                    count++;
            }

            binding.YawRoots = new Transform[count];
            binding.Barrels = new Transform[count];
            binding.RestBarrelLocalFwd = new Vector3[count];
            int w = 0;
            for (int i = 0; i < all.Length && w < count; i++)
            {
                Transform barrel = all[i];
                if (!MegaShipPartClassifier.IsWeaponMountTransform(barrel))
                    continue;

                Transform yawRoot = ResolveYawRoot(barrel, hull);
                binding.Barrels[w] = barrel;
                binding.YawRoots[w] = yawRoot;
                if (yawRoot != null)
                {
                    Vector3 barrelFwd = MegaShipWeaponVisualSync.Flatten(barrel.forward);
                    binding.RestBarrelLocalFwd[w] = yawRoot.InverseTransformDirection(barrelFwd);
                    if (binding.RestBarrelLocalFwd[w].sqrMagnitude < 1e-6f)
                        binding.RestBarrelLocalFwd[w] = Vector3.forward;
                }
                else
                {
                    binding.RestBarrelLocalFwd[w] = Vector3.forward;
                }

                w++;
            }

            return binding;
        }

        /// <summary>
        /// Walks toward the hull and stops on the first <c>TurretBase</c> ancestor so a nested
        /// barrel turns its whole assembly. Standalone launchers yaw themselves.
        /// </summary>
        public static Transform ResolveYawRoot(Transform weapon, Transform hull)
        {
            if (weapon == null)
                return null;

            Transform parent = weapon.parent;
            while (parent != null && parent != hull)
            {
                if (parent.name.IndexOf("TurretBase", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return parent;
                parent = parent.parent;
            }

            return weapon;
        }

        void LateUpdate()
        {
            if (ShipEntity == Entity.Null || YawRoots == null || YawRoots.Length == 0)
                return;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            MegaShipWeaponVisualSync.Apply(world.EntityManager, ShipEntity, gameObject);
            MegaShipAimReticleVisual.Sync(this, world.EntityManager);
        }
    }
}
