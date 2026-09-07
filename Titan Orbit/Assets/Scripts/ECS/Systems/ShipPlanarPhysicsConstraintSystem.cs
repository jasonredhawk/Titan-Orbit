using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Top-down constraint after Unity Physics, bounce, and canonical wrap. Hull impacts
    /// can impart pitch/roll; this re-locks yaw-only rotation, clamps <c>Position.y</c> to the play
    /// plane, and zeros vertical velocity. Bounce linear XZ is preserved for
    /// <see cref="ShipKinematicsSyncSystem"/>. Pipeline:
    /// Drive → Physics → Bounce → Friction → Wrap → Planar (this) → KinematicsSync.
    /// </summary>
    // OrderLast: after default-slot PhysicsSystemGroup. Avoid UpdateAfter(PhysicsSystemGroup) —
    // ClientWorld sorter warns when that group is not a PredictedFixedStep sibling.
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(ShipKinematicsSyncSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipPlanarPhysicsConstraintSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            // [TITAN-ORBIT] Client: skip TeamChoice / ship Instantiates holds only
            // (ShouldSkipShipSimulation). Map Instantiates backlog must not freeze planar lock.
            // IsClient() — Local Host shares settle statics with the server world.
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            if (state.World.IsClient())
            {
                foreach (var (transform, velocity, shipState) in SystemAPI
                             .Query<RefRW<LocalTransform>, RefRW<PhysicsVelocity>, RefRO<ShipState>>()
                             .WithAll<ShipTag, Simulate, PredictedGhost>())
                    ApplyPlanar(transform, velocity, shipState);
            }
            else
            {
                foreach (var (transform, velocity, shipState) in SystemAPI
                             .Query<RefRW<LocalTransform>, RefRW<PhysicsVelocity>, RefRO<ShipState>>()
                             .WithAll<ShipTag, Simulate>())
                    ApplyPlanar(transform, velocity, shipState);
            }
        }

        /// <summary>Yaw-only lock, Y = 0, planar linear / yaw angular.</summary>
        static void ApplyPlanar(
            RefRW<LocalTransform> transform,
            RefRW<PhysicsVelocity> velocity,
            RefRO<ShipState> shipState)
        {
            if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                return;

            // --- Yaw-only orientation (flatten forward onto XZ) ---
            // [TITAN-ORBIT] Only snap when collisions tilt the hull — avoids per-tick rotation
            // rewrites when the body is already planar (reduces visible stepping on the client).
            float3 forward = math.mul(transform.ValueRO.Rotation, new float3(0f, 0f, 1f));
            forward.y = 0f;
            if (math.lengthsq(forward) < 1e-8f)
                forward = new float3(0f, 0f, 1f);
            else
                forward = math.normalize(forward);

            quaternion planarRotation = quaternion.LookRotationSafe(forward, math.up());
            float tiltDegrees = math.degrees(math.angle(transform.ValueRO.Rotation, planarRotation));
            if (tiltDegrees > 0.35f)
                transform.ValueRW.Rotation = planarRotation;

            // --- Keep the hull on the play plane ---
            float3 pos = transform.ValueRO.Position;
            if (math.abs(pos.y) > 1e-4f)
            {
                pos.y = 0f;
                transform.ValueRW.Position = pos;
            }

            float3 linear = velocity.ValueRO.Linear;
            linear.y = 0f;
            float yawRate = velocity.ValueRO.Angular.y;
            velocity.ValueRW = new PhysicsVelocity
            {
                Linear = linear,
                Angular = new float3(0f, yawRate, 0f),
            };
        }
    }
}
