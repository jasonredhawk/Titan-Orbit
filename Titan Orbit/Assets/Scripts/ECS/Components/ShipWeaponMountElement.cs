using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One weapon mount on a ship hull — local offset/rotation relative to the ship transform.
    /// Stored in a DynamicBuffer so multi-cannon ships can fire from multiple muzzles.
    /// Baked from child ShipWeaponMountAuthoring objects in StarshipGhostAuthoring.
    /// </summary>
    public struct ShipWeaponMountElement : IBufferElementData
    {
        public float3 LocalPosition;
        public quaternion LocalRotation;
        /// <summary>Extra yaw offset in degrees for angled cannons.</summary>
        public float DirectionAngleDeg;
        /// <summary>Index into weapon config arrays for multi-cannon loadouts.</summary>
        public int CannonIndex;
    }

    /// <summary>
    /// Shared muzzle origin and fire direction from ship hull transform + weapon mount buffer element.
    /// Single source of truth for where bullets spawn — used by BulletSimulationSystem (server hits)
    /// and ClientLocalBulletVfxBridge (client tracers). BurstCompile target per ship-simulation rule.
    /// </summary>
    public static class ShipWeaponPose
    {
        /// <summary>
        /// Resolves world-space fire origin and forward direction for one mount.
        /// Returns false if the computed forward vector degenerates to zero length.
        /// </summary>
        [BurstCompile]
        public static bool TryResolve(
            in LocalTransform shipTransform,
            in ShipWeaponMountElement mount,
            out float3 fireOrigin,
            out float3 fireForward)
        {
            fireOrigin = float3.zero;
            fireForward = new float3(0f, 0f, 1f);

            // --- Local mount forward, flattened to XZ plane ---
            float3 localFwd = math.mul(mount.LocalRotation, new float3(0f, 0f, 1f));
            localFwd.y = 0f;
            if (math.lengthsq(localFwd) < 0.0001f)
                localFwd = new float3(0f, 0f, 1f);
            else
                localFwd = math.normalize(localFwd);

            fireOrigin = shipTransform.Position + math.rotate(shipTransform.Rotation, mount.LocalPosition);
            fireOrigin.y = shipTransform.Position.y;

            // [TITAN-ORBIT] Legacy Starship convention: hullRot * flatten(Inverse(hullRot) * weaponWorldForward)
            float3 cannonFwd = math.rotate(shipTransform.Rotation, localFwd);
            cannonFwd.y = 0f;
            if (math.lengthsq(cannonFwd) < 0.0001f)
                cannonFwd = math.rotate(shipTransform.Rotation, new float3(0f, 0f, 1f));
            cannonFwd = math.normalize(cannonFwd);

            // Apply authored yaw offset for angled cannons.
            float angleRad = math.radians(mount.DirectionAngleDeg);
            float3 cannonRight = math.normalize(math.cross(new float3(0f, 1f, 0f), cannonFwd));
            fireForward = math.normalize(cannonFwd * math.cos(angleRad) + cannonRight * math.sin(angleRad));
            fireForward.y = 0f;
            if (math.lengthsq(fireForward) < 0.0001f)
                return false;
            fireForward = math.normalize(fireForward);
            return true;
        }
    }
}
