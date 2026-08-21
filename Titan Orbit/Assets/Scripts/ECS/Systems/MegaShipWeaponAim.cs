using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared MEGA turret math: yaw a mount toward a world-planar aim and read the barrel
    /// forward used for bullets. Server combat writes hull-local
    /// <see cref="ShipWeaponMountElement.LocalRotation"/> and ghosts the lock's
    /// <b>current</b> point on <see cref="MegaShipGunnerSlotElement.AimWorldX"/> so hybrid
    /// meshes LookAt the target. <see cref="MegaShipGunnerSlotElement.CurrentYawDeg"/> is
    /// the world fire heading (lead) while tracking — bullets use that ray, not the mesh.
    /// Idle guns park with ghosted local yaw × live hull heading.
    /// <para>
    /// [TITAN-ORBIT] Desired aim must be a toroidal shortest-path direction
    /// (<see cref="TitanOrbit.Generation.ToroidalMapEcs.ShortestOffsetXZ"/>), never a raw
    /// world subtract across a seam. The ship hull transform is never wrapped.
    /// </para>
    /// Paired with <see cref="MegaShipAutoFireSystem"/>.
    /// Mounts snap to the aim heading — no traverse delay.
    /// </summary>
    public static class MegaShipWeaponAim
    {
        /// <summary>Squared length below which a planar direction cannot be normalized.</summary>
        const float MinDirectionSq = 0.0001f;

        /// <summary>
        /// Hull-local planar yaw in degrees from a mount's local rotation (0 = hull +Z).
        /// </summary>
        /// <param name="localRotation">Mount rotation relative to the hull.</param>
        /// <returns>Signed degrees, atan2(x, z) of the flattened local forward.</returns>
        public static float GetLocalYawDeg(in quaternion localRotation)
        {
            float3 fwd = math.mul(localRotation, new float3(0f, 0f, 1f));
            fwd.y = 0f;
            if (math.lengthsq(fwd) < MinDirectionSq)
                return 0f;
            return math.degrees(math.atan2(fwd.x, fwd.z));
        }

        /// <summary>
        /// World-planar barrel forward from hull pose + current mount yaw.
        /// Same contract as <see cref="ShipWeaponPose"/> fire direction (no presentation scale).
        /// </summary>
        /// <param name="hull">MEGA <see cref="LocalTransform"/> (unbounded, not wrapped).</param>
        /// <param name="mount">Mount whose <see cref="ShipWeaponMountElement.LocalRotation"/> is live.</param>
        /// <returns>Normalized XZ forward. Degenerate pose falls back to hull +Z.</returns>
        public static float3 GetBarrelForward(in LocalTransform hull, in ShipWeaponMountElement mount)
        {
            float3 localFwd = math.mul(mount.LocalRotation, new float3(0f, 0f, 1f));
            localFwd.y = 0f;
            if (math.lengthsq(localFwd) < MinDirectionSq)
                localFwd = new float3(0f, 0f, 1f);
            else
                localFwd = math.normalize(localFwd);

            float3 worldFwd = math.rotate(hull.Rotation, localFwd);
            worldFwd.y = 0f;
            if (math.lengthsq(worldFwd) < MinDirectionSq)
                worldFwd = math.rotate(hull.Rotation, new float3(0f, 0f, 1f));
            worldFwd.y = 0f;
            if (math.lengthsq(worldFwd) < MinDirectionSq)
                return new float3(0f, 0f, 1f);
            return math.normalize(worldFwd);
        }

        /// <summary>
        /// Snaps <paramref name="mount"/>.LocalRotation to a world-planar aim direction.
        /// Heading is the signed yaw from the MEGA hull's forward to the aim — not a full
        /// quaternion LookAt that can fight baked pitch on the mount.
        /// </summary>
        /// <param name="hull">MEGA hull transform (yaw-only sim, unbounded).</param>
        /// <param name="mount">Mount to rotate (written back by the caller).</param>
        /// <param name="desiredWorldDir">World XZ aim (already toroidal-shortest if from a target).</param>
        /// <param name="dt">Unused; kept so existing callers do not change.</param>
        public static void RotateMountTowardWorldDir(
            in LocalTransform hull,
            ref ShipWeaponMountElement mount,
            float3 desiredWorldDir,
            float dt)
        {
            _ = dt;
            desiredWorldDir.y = 0f;
            if (math.lengthsq(desiredWorldDir) < MinDirectionSq)
                return;

            desiredWorldDir = math.normalize(desiredWorldDir);

            // --- Hull forward on XZ (ship heading) ---
            // [TITAN-ORBIT] Turret local yaw is an offset from this forward. The hull itself
            // is never wrapped; aim must already be a toroidal shortest-path direction.
            float3 hullFwd = math.rotate(hull.Rotation, new float3(0f, 0f, 1f));
            hullFwd.y = 0f;
            if (math.lengthsq(hullFwd) < MinDirectionSq)
                return;
            hullFwd = math.normalize(hullFwd);

            float desiredLocalYaw = SignedPlanarYawDeg(hullFwd, desiredWorldDir);
            mount.LocalRotation = quaternion.AxisAngle(math.up(), math.radians(desiredLocalYaw));
        }

        /// <summary>Signed yaw degrees from planar <paramref name="fromFwd"/> to <paramref name="toFwd"/> (Unity +Y, +Z forward).</summary>
        public static float SignedPlanarYawDeg(float3 fromFwd, float3 toFwd)
        {
            fromFwd.y = 0f;
            toFwd.y = 0f;
            if (math.lengthsq(fromFwd) < MinDirectionSq || math.lengthsq(toFwd) < MinDirectionSq)
                return 0f;
            fromFwd = math.normalize(fromFwd);
            toFwd = math.normalize(toFwd);
            float fromDeg = math.degrees(math.atan2(fromFwd.x, fromFwd.z));
            float toDeg = math.degrees(math.atan2(toFwd.x, toFwd.z));
            return DeltaAngleDeg(fromDeg, toDeg);
        }

        /// <summary>Shortest signed delta from <paramref name="current"/> to <paramref name="target"/> in degrees.</summary>
        public static float DeltaAngleDeg(float current, float target)
        {
            float diff = target - current;
            diff -= math.floor((diff + 180f) / 360f) * 360f;
            return diff;
        }

        /// <summary>
        /// True when this slot is tracking — <see cref="MegaShipGunnerSlotElement.CurrentYawDeg"/>
        /// is a world fire heading, not a hull-local park yaw.
        /// </summary>
        public static bool IsTrackingAim(in MegaShipGunnerSlotElement slot)
        {
            return slot.TargetDistance > 0.05f;
        }

        /// <summary>World-planar yaw in degrees from a flattened XZ direction (0 = world +Z).</summary>
        public static float GetWorldYawDeg(float3 worldDir)
        {
            worldDir.y = 0f;
            if (math.lengthsq(worldDir) < MinDirectionSq)
                return 0f;
            worldDir = math.normalize(worldDir);
            return math.degrees(math.atan2(worldDir.x, worldDir.z));
        }

        /// <summary>Unit XZ direction from a world-planar yaw in degrees.</summary>
        public static float3 WorldDirFromYawDeg(float worldYawDeg)
        {
            float r = math.radians(worldYawDeg);
            return new float3(math.sin(r), 0f, math.cos(r));
        }

        /// <summary>
        /// World aim point from a tracking slot (Y supplied by the caller — muzzle or hull).
        /// Reticles only; turret meshes must use <see cref="GetWorldYawDeg"/> / CurrentYawDeg.
        /// </summary>
        public static bool TryGetTrackingAimPoint(
            in MegaShipGunnerSlotElement slot,
            float y,
            out float3 aimPoint)
        {
            aimPoint = new float3(slot.AimWorldX, y, slot.AimWorldZ);
            return IsTrackingAim(in slot);
        }

        /// <summary>
        /// Owner Shift mouse point in the same unbounded space as the hull
        /// (<c>hull + AimPlanarDir × AimDistance</c>).
        /// </summary>
        public static bool TryGetOwnerMouseAimPoint(
            in LocalTransform hull,
            in ShipInput input,
            out float3 aimPoint)
        {
            aimPoint = hull.Position;
            if (math.lengthsq(input.AimPlanarDir) < 0.01f || input.AimDistance <= 0.05f)
                return false;

            float3 mouseDir = TitanOrbit.Generation.SphericalMapEcs.DecodeTangentDir(
                hull.Position, hull.Rotation, input.AimPlanarDir);
            float dist = input.AimDistance;
            float radius = TitanOrbit.Generation.SphericalMapEcs.BurstSafeRadius(hull.Position);
            aimPoint = TitanOrbit.Generation.SphericalMapEcs.ProjectToSphere(
                hull.Position + mouseDir * dist, radius);
            return true;
        }

        /// <summary>
        /// World XZ from this muzzle to the owner's mouse point (streams converge).
        /// Falls back to a shared mouse direction only when <see cref="ShipInput.AimDistance"/>
        /// is missing (old command layout).
        /// </summary>
        public static bool TryGetMuzzleDirToMousePoint(
            in LocalTransform hull,
            in ShipWeaponMountElement mount,
            in ShipInput input,
            float mapW,
            float mapH,
            out float3 worldDir)
        {
            worldDir = default;
            if (TryGetOwnerMouseAimPoint(in hull, in input, out float3 aimPoint))
            {
                if (!ShipWeaponPose.TryResolve(hull, mount, out float3 muzzle, out _))
                    muzzle = hull.Position;
                float3 offset = ToroidalMapEcs.ShortestOffsetXZ(muzzle, aimPoint, mapW, mapH);
                float len = math.length(offset);
                if (len < 0.05f)
                    return false;
                worldDir = offset / len;
                return true;
            }

            if (math.lengthsq(input.AimPlanarDir) < 0.01f)
                return false;
            worldDir = SphericalMapEcs.DecodeTangentDir(hull.Position, hull.Rotation, input.AimPlanarDir);
            return true;
        }

        /// <summary>
        /// Writes ghosted yaw, world aim point, and acquire distance.
        /// While tracking, <c>CurrentYawDeg</c> is the world fire heading (same as
        /// <paramref name="desiredWorldDir"/>). Idle writes hull-local park yaw.
        /// </summary>
        /// <param name="gunners">1:1 MEGA aim-slot buffer (may be uncreated).</param>
        /// <param name="mountIndex">Index into both mounts and gunners.</param>
        /// <param name="mount">Mount after this tick's rotate.</param>
        /// <param name="aimPoint">Current lock point (ship / pad / moon) for turret LookAt.</param>
        /// <param name="targetDistance">Toroidal muzzle→aim range, or 0 when not tracking.</param>
        /// <param name="desiredWorldDir">World XZ fire heading written while tracking.</param>
        public static void WriteGhostedYaw(
            DynamicBuffer<MegaShipGunnerSlotElement> gunners,
            int mountIndex,
            in ShipWeaponMountElement mount,
            float3 aimPoint = default,
            float targetDistance = 0f,
            float3 desiredWorldDir = default,
            int targetGhostId = 0)
        {
            if (!gunners.IsCreated || mountIndex < 0 || mountIndex >= gunners.Length)
                return;

            var slot = gunners[mountIndex];
            bool tracking = targetDistance > 0.05f;
            slot.CurrentYawDeg = tracking
                ? GetWorldYawDeg(desiredWorldDir)
                : GetLocalYawDeg(mount.LocalRotation);
            slot.TargetDistance = math.max(0f, targetDistance);
            slot.AimWorldX = aimPoint.x;
            slot.AimWorldZ = aimPoint.z;
            slot.TargetGhostId = tracking ? targetGhostId : 0;
            gunners[mountIndex] = slot;
        }

        /// <summary><c>GhostInstance.ghostId</c> when the entity is a live ghost; otherwise 0.</summary>
        public static int ReadGhostId(EntityManager em, Entity entity)
        {
            if (entity == Entity.Null || !em.Exists(entity)
                || !em.HasComponent<GhostInstance>(entity))
                return 0;
            return em.GetComponentData<GhostInstance>(entity).ghostId;
        }

        /// <summary>
        /// Drops tracking so clients park MEGA barrels on the hull when Fire is released.
        /// </summary>
        public static void ClearUnoccupiedTracking(
            DynamicBuffer<MegaShipGunnerSlotElement> gunners,
            DynamicBuffer<ShipWeaponMountElement> mounts)
        {
            if (!gunners.IsCreated)
                return;

            for (int i = 0; i < gunners.Length; i++)
            {
                var slot = gunners[i];
                slot.TargetDistance = 0f;
                slot.AimWorldX = 0f;
                slot.AimWorldZ = 0f;
                slot.TargetGhostId = 0;
                if (mounts.IsCreated && i < mounts.Length)
                    slot.CurrentYawDeg = GetLocalYawDeg(mounts[i].LocalRotation);
                gunners[i] = slot;
            }
        }
    }
}
