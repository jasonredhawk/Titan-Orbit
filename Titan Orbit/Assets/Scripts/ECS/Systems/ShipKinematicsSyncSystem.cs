using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// After Unity Physics integrates hull motion and resolves collisions, mirrors linear velocity
    /// and the escort formation heading / center into ghost <see cref="ShipKinematics"/> for HUD, bullets, and VFX.
    /// [PHYSICS] Runs after <see cref="PhysicsSystemGroup"/> and
    /// <see cref="ShipPlanarPhysicsConstraintSystem"/> so asteroid bounce velocity is captured —
    /// we do <b>not</b> hard-clamp to MaxSpeed here (that would erase collision impulse).
    /// Overspeed bleeds in the next drive tick via <see cref="ShipPhysicsDriveLogic"/>.
    /// </summary>
    // OrderLast + after Planar: capture post-collision velocity without UpdateAfter(PhysicsSystemGroup).
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(ShipPlanarPhysicsConstraintSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipKinematicsSyncSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            // [TITAN-ORBIT] Client: skip TeamChoice / ship Instantiates holds only
            // (ShouldSkipShipSimulation). Map Instantiates backlog must not freeze kinematics.
            // IsClient() — Local Host shares settle statics with the server world.
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            float dt = SystemAPI.Time.DeltaTime;
            // Map size from MapStateSingleton. Unset → euclidean lag (do not invent 1000×1000).
            float mapW = 0f;
            float mapH = 0f;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var map) &&
                ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
            {
                mapW = map.MapWidth;
                mapH = map.MapHeight;
            }

            if (state.World.IsClient())
            {
                foreach (var (velocity, kinematics, shipState, transform) in SystemAPI
                             .Query<RefRW<PhysicsVelocity>, RefRW<ShipKinematics>, RefRO<ShipState>, RefRO<LocalTransform>>()
                             .WithAll<ShipTag, Simulate, PredictedGhost>())
                    SyncKinematics(velocity, kinematics, shipState, transform, dt, mapW, mapH);
            }
            else
            {
                foreach (var (velocity, kinematics, shipState, transform) in SystemAPI
                             .Query<RefRW<PhysicsVelocity>, RefRW<ShipKinematics>, RefRO<ShipState>, RefRO<LocalTransform>>()
                             .WithAll<ShipTag, Simulate>())
                    SyncKinematics(velocity, kinematics, shipState, transform, dt, mapW, mapH);
            }
        }

        /// <summary>Mirrors post-collision planar velocity and escort heading / center into ghosted kinematics.</summary>
        static void SyncKinematics(
            RefRW<PhysicsVelocity> velocity,
            RefRW<ShipKinematics> kinematics,
            RefRO<ShipState> shipState,
            RefRO<LocalTransform> transform,
            float dt,
            float mapW,
            float mapH)
        {
            if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
            {
                velocity.ValueRW = PhysicsVelocity.Zero;
                kinematics.ValueRW = default;
                return;
            }

            float3 linear = kinematics.ValueRO.Velocity;
            linear.y = 0f;
            float3 shipPos = transform.ValueRO.Position;
            float3 origin = kinematics.ValueRO.FormationOrigin;
            byte seeded = kinematics.ValueRO.FormationCenterSeeded;
            if (seeded == 0)
            {
                origin = shipPos;
                origin.y = 0f;
                if (ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                    origin = ToroidalMapEcs.Wrap(origin, mapW, mapH);
                seeded = 1;
            }
            else
            {
                origin = PeopleTransportMath.StepEscortFormationOrigin(
                    origin, shipPos, mapW, mapH, dt);
            }

            kinematics.ValueRW = new ShipKinematics
            {
                Velocity = linear,
                FormationHeading = PeopleTransportMath.StepEscortFormationHeading(
                    kinematics.ValueRO.FormationHeading, transform.ValueRO.Rotation, dt),
                FormationOrigin = origin,
                FormationCenterSeeded = seeded,
            };
            velocity.ValueRW = PhysicsVelocity.Zero;
        }
    }
}
