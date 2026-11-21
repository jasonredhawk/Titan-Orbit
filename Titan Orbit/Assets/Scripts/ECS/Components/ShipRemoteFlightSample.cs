using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Latest authoritative flight anchor for a remote hull. The server writes it after the
    /// motor and the deterministic tick. Clients integrate
    /// <see cref="ShipPhysicsDriveLogic"/> forward from this pose. Every field is
    /// <see cref="SmoothingAction.Clamp"/> so interpolation cannot blend the anchor.
    /// Position and velocity repeat <see cref="LocalTransform"/> and
    /// <see cref="ShipKinematics"/> on purpose: those transforms are still interpolated
    /// on a remote ghost.
    /// </summary>
    public struct ShipRemoteFlightSample : IComponentData
    {
        /// <summary>False until the server has stored one tick.</summary>
        [GhostField(Smoothing = SmoothingAction.Clamp)]
        public bool HasSample;

        /// <summary>Server tick index this pose was simulated on.</summary>
        [GhostField(Smoothing = SmoothingAction.Clamp)]
        public uint Tick;

        /// <summary>Canonical X at <see cref="Tick"/>.</summary>
        [GhostField(Quantization = 1000, Smoothing = SmoothingAction.Clamp)]
        public float PositionX;

        /// <summary>Canonical Z at <see cref="Tick"/>.</summary>
        [GhostField(Quantization = 1000, Smoothing = SmoothingAction.Clamp)]
        public float PositionZ;

        /// <summary>Planar yaw in radians. 0 faces +Z.</summary>
        [GhostField(Quantization = 1000, Smoothing = SmoothingAction.Clamp)]
        public float Yaw;

        /// <summary>Planar velocity X at <see cref="Tick"/>.</summary>
        [GhostField(Quantization = 1000, Smoothing = SmoothingAction.Clamp)]
        public float VelocityX;

        /// <summary>Planar velocity Z at <see cref="Tick"/>.</summary>
        [GhostField(Quantization = 1000, Smoothing = SmoothingAction.Clamp)]
        public float VelocityZ;

        /// <summary>Aim direction X copied from <see cref="ShipInput.AimPlanarDir"/>.</summary>
        [GhostField(Quantization = 1000, Smoothing = SmoothingAction.Clamp)]
        public float AimX;

        /// <summary>Aim direction Y copied from <see cref="ShipInput.AimPlanarDir"/>.</summary>
        [GhostField(Quantization = 1000, Smoothing = SmoothingAction.Clamp)]
        public float AimY;

        /// <summary>Forward thrust held on this tick.</summary>
        [GhostField(Smoothing = SmoothingAction.Clamp)]
        public bool Thrust;

        /// <summary>Shift held on this tick (overdrive intent, or MEGA heading lock).</summary>
        [GhostField(Smoothing = SmoothingAction.Clamp)]
        public bool Overdrive;

        /// <summary>Space brakes disabled (frictionless coast).</summary>
        [GhostField(Smoothing = SmoothingAction.Clamp)]
        public bool DisableSpaceBrakes;

        /// <summary>Planar position. Y is always 0.</summary>
        public float3 Position => new float3(PositionX, 0f, PositionZ);

        /// <summary>Planar velocity. Y is always 0.</summary>
        public float3 Velocity => new float3(VelocityX, 0f, VelocityZ);

        /// <summary>Yaw as a rotation around world up.</summary>
        public quaternion Rotation => quaternion.RotateY(Yaw);

        /// <summary>
        /// Flight inputs only. Fire, rockets, and mines stay on the owner command stream.
        /// </summary>
        public ShipInput ToFlightInput()
        {
            return new ShipInput
            {
                AimPlanarDir = new float2(AimX, AimY),
                Thrust = Thrust,
                Overdrive = Overdrive,
                DisableSpaceBrakes = DisableSpaceBrakes,
            };
        }

        /// <summary>Stores one finished server tick. Call after kinematics are committed.</summary>
        public void Capture(uint tick, in LocalTransform transform, in ShipKinematics kinematics, in ShipInput input)
        {
            HasSample = true;
            Tick = tick;
            PositionX = transform.Position.x;
            PositionZ = transform.Position.z;
            Yaw = YawFromRotation(transform.Rotation);
            VelocityX = kinematics.Velocity.x;
            VelocityZ = kinematics.Velocity.z;
            AimX = input.AimPlanarDir.x;
            AimY = input.AimPlanarDir.y;
            Thrust = input.Thrust;
            Overdrive = input.Overdrive;
            DisableSpaceBrakes = input.DisableSpaceBrakes;
        }

        /// <summary>Yaw of a hull whose forward is local +Z, matching the motor's aim turn.</summary>
        public static float YawFromRotation(quaternion rotation)
        {
            float3 forward = math.mul(rotation, new float3(0f, 0f, 1f));
            return math.atan2(forward.x, forward.z);
        }
    }
}
