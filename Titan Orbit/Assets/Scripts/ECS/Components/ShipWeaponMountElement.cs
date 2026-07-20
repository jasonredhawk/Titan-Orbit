using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] One weapon mount on a ship hull — local offset and rotation relative to the ship
    /// transform. Stored in a DynamicBuffer so multi-cannon ships fire from multiple muzzles.
    /// Baked from child <see cref="Authoring.ShipWeaponMountAuthoring"/> objects in StarshipGhostAuthoring.
    /// </summary>
    public struct ShipWeaponMountElement : IBufferElementData
    {
        /// <summary>[UNITY] Local position offset from ship hull origin.</summary>
        public float3 LocalPosition;

        /// <summary>[UNITY] Local rotation of the mount relative to hull.</summary>
        public quaternion LocalRotation;

        /// <summary>[TITAN-ORBIT] Extra yaw offset in degrees for angled cannons.</summary>
        public float DirectionAngleDeg;

        /// <summary>[TITAN-ORBIT] Index into weapon config arrays for multi-cannon loadouts.</summary>
        public int CannonIndex;
    }

    /// <summary>
    /// [ECS/DOTS] Shared muzzle origin and fire direction from ship hull transform + weapon mount
    /// buffer element. Single source of truth for where bullets spawn — used by
    /// <see cref="BulletSimulationSystem"/> (server hits) and client tracer VFX bridges.
    /// [BurstCompile] target per ship-simulation architecture rule.
    /// </summary>
    public static class ShipWeaponPose
    {
        /// <summary>
        /// [ECS/DOTS] Resolves world-space fire origin and forward direction for one mount.
        /// </summary>
        /// <param name="shipTransform">Ship hull LocalTransform at fire time.</param>
        /// <param name="mount">Baked mount element with local pose and yaw offset.</param>
        /// <param name="fireOrigin">Output muzzle world position (keeps mount local Y — barrels often sit below hull origin).</param>
        /// <param name="fireForward">Output normalized fire direction on XZ plane.</param>
        /// <returns>False if the computed forward vector degenerates to zero length.</returns>
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
            // [TITAN-ORBIT] Top-down shooter — ignore vertical aim component.
            float3 localFwd = math.mul(mount.LocalRotation, new float3(0f, 0f, 1f));
            localFwd.y = 0f;
            if (math.lengthsq(localFwd) < 0.0001f)
                localFwd = new float3(0f, 0f, 1f);
            else
                localFwd = math.normalize(localFwd);

            // --- World muzzle origin (full mount offset, including Y) ---
            // [TITAN-ORBIT] Do not flatten to hull Y — weapon child components are often below
            // the ship root; forcing hull Y made muzzle flashes float above the barrels.
            fireOrigin = shipTransform.Position + math.rotate(shipTransform.Rotation, mount.LocalPosition);

            // --- Hull-relative cannon forward ---
            // [TITAN-ORBIT] Legacy Starship convention: hullRot * flatten(Inverse(hullRot) * weaponWorldForward)
            float3 cannonFwd = math.rotate(shipTransform.Rotation, localFwd);
            cannonFwd.y = 0f;
            if (math.lengthsq(cannonFwd) < 0.0001f)
                cannonFwd = math.rotate(shipTransform.Rotation, new float3(0f, 0f, 1f));
            cannonFwd = math.normalize(cannonFwd);

            // --- Apply authored yaw offset for angled cannons ---
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
