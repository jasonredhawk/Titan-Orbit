using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    public struct ShipWeaponMountElement : IBufferElementData
    {
        public float3 LocalPosition;
        public quaternion LocalRotation;
        public float DirectionAngleDeg;
        public int CannonIndex;
    }

    public static class ShipWeaponPose
    {
        public static bool TryResolve(
            in LocalTransform shipTransform,
            in ShipWeaponMountElement mount,
            out float3 fireOrigin,
            out float3 fireForward)
        {
            fireOrigin = float3.zero;
            fireForward = new float3(0f, 0f, 1f);

            float3 localFwd = math.mul(mount.LocalRotation, new float3(0f, 0f, 1f));
            localFwd.y = 0f;
            if (math.lengthsq(localFwd) < 0.0001f)
                localFwd = new float3(0f, 0f, 1f);
            else
                localFwd = math.normalize(localFwd);

            fireOrigin = shipTransform.Position + math.rotate(shipTransform.Rotation, mount.LocalPosition);
            fireOrigin.y = shipTransform.Position.y;

            // Legacy Starship: hullRot * flatten(Inverse(hullRot) * weaponWorldForward)
            float3 cannonFwd = math.rotate(shipTransform.Rotation, localFwd);
            cannonFwd.y = 0f;
            if (math.lengthsq(cannonFwd) < 0.0001f)
                cannonFwd = math.rotate(shipTransform.Rotation, new float3(0f, 0f, 1f));
            cannonFwd = math.normalize(cannonFwd);

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
