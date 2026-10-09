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
    /// plane, and zeros vertical velocity. While thrust is held, planar speed is pulled back to
    /// the motor cap (<see cref="ShipTerritoryBoostLatch.LastAppliedMaxSpeed"/>) so solver
    /// separation cannot leave the speedometer above cruise / overdrive. Released-thrust bounce
    /// still reaches <see cref="ShipKinematicsSyncSystem"/>. Pipeline:
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

            var inputLookup = SystemAPI.GetComponentLookup<ShipInput>(true);
            var latchLookup = SystemAPI.GetComponentLookup<ShipTerritoryBoostLatch>(true);
            var moonLookup = SystemAPI.GetComponentLookup<ShipMoonDockState>(true);

            if (state.World.IsClient())
            {
                foreach (var (transform, velocity, shipState, entity) in SystemAPI
                             .Query<RefRW<LocalTransform>, RefRW<PhysicsVelocity>, RefRO<ShipState>>()
                             .WithAll<ShipTag, Simulate, PredictedGhost>()
                             .WithEntityAccess())
                    ApplyPlanar(transform, velocity, shipState, entity, inputLookup, latchLookup, moonLookup);
            }
            else
            {
                foreach (var (transform, velocity, shipState, entity) in SystemAPI
                             .Query<RefRW<LocalTransform>, RefRW<PhysicsVelocity>, RefRO<ShipState>>()
                             .WithAll<ShipTag, Simulate>()
                             .WithEntityAccess())
                    ApplyPlanar(transform, velocity, shipState, entity, inputLookup, latchLookup, moonLookup);
            }
        }

        /// <summary>Yaw-only lock, Y = 0, planar linear / yaw angular, thrust cruise cap.</summary>
        static void ApplyPlanar(
            RefRW<LocalTransform> transform,
            RefRW<PhysicsVelocity> velocity,
            RefRO<ShipState> shipState,
            Entity entity,
            ComponentLookup<ShipInput> inputLookup,
            ComponentLookup<ShipTerritoryBoostLatch> latchLookup,
            ComponentLookup<ShipMoonDockState> moonLookup)
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

            // --- Thrust cruise cap (after the solver) ---
            // [TITAN-ORBIT] The motor already clamps thrust to MaxSpeed, then Unity Physics can
            // still write a separation speed (up to MaxDynamicDepenetrationVelocity, 25). The
            // next drive tick used to keep that magnitude, so a cruise of 3 climbed to 7–9 and
            // stuck at the 3× safety ceiling. Holding thrust pulls the hull back to the same
            // cap the speedometer tick shows. Takeoff / landed moon co-orbit own velocity
            // themselves (latch cap is cleared). Releasing thrust still allows a ram bleed.
            bool thrusting = inputLookup.HasComponent(entity) && inputLookup[entity].Thrust;
            bool takeoffOrLanded = moonLookup.HasComponent(entity) &&
                                   (moonLookup[entity].IsTakingOff || moonLookup[entity].IsFullyLanded);
            if (thrusting &&
                !takeoffOrLanded &&
                latchLookup.HasComponent(entity))
            {
                float cap = latchLookup[entity].LastAppliedMaxSpeed;
                if (cap > 0.1f)
                    ShipPhysicsDriveLogic.ClampPlanarSpeed(ref linear, cap);
            }

            float yawRate = velocity.ValueRO.Angular.y;
            velocity.ValueRW = new PhysicsVelocity
            {
                Linear = linear,
                Angular = new float3(0f, yawRate, 0f),
            };
        }
    }
}
