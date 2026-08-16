using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared MEGA turret math: yaw a mount toward a world-planar aim and read the barrel
    /// forward used for bullets. Server combat writes
    /// <see cref="ShipWeaponMountElement.LocalRotation"/>; clients read the ghosted
    /// <see cref="MegaShipGunnerSlotElement.CurrentYawDeg"/> for hybrid visuals.
    /// <para>
    /// [TITAN-ORBIT] Desired aim must be a toroidal shortest-path direction
    /// (<see cref="TitanOrbit.Generation.ToroidalMapEcs.ShortestOffsetXZ"/>), never a raw
    /// world subtract across a seam. The ship hull transform is never wrapped.
    /// </para>
    /// Paired with <see cref="MegaShipAutoFireSystem"/> and <see cref="MegaShipPlayerCombatSystem"/>.
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
        /// Writes ghosted yaw and optional acquire distance for hybrid visuals / reticles.
        /// </summary>
        /// <param name="gunners">1:1 gunner-pad buffer (may be uncreated).</param>
        /// <param name="mountIndex">Index into both mounts and gunners.</param>
        /// <param name="mount">Mount after this tick's rotate.</param>
        /// <param name="targetDistance">Toroidal muzzle→target range, or 0 when no target.</param>
        public static void WriteGhostedYaw(
            DynamicBuffer<MegaShipGunnerSlotElement> gunners,
            int mountIndex,
            in ShipWeaponMountElement mount,
            float targetDistance = 0f)
        {
            if (!gunners.IsCreated || mountIndex < 0 || mountIndex >= gunners.Length)
                return;

            var slot = gunners[mountIndex];
            slot.CurrentYawDeg = GetLocalYawDeg(mount.LocalRotation);
            slot.TargetDistance = math.max(0f, targetDistance);
            gunners[mountIndex] = slot;
        }
    }
}
