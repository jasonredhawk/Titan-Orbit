using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// After Unity Physics integrates hull motion and resolves collisions, mirrors linear velocity
    /// into ghost <see cref="ShipKinematics"/> for HUD, bullets, and VFX.
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

            foreach (var (velocity, kinematics, shipState) in SystemAPI
                         .Query<RefRW<PhysicsVelocity>, RefRW<ShipKinematics>, RefRO<ShipState>>()
                         .WithAll<ShipTag, Simulate>())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                {
                    velocity.ValueRW = PhysicsVelocity.Zero;
                    kinematics.ValueRW = new ShipKinematics { Velocity = float3.zero };
                    continue;
                }

                // Constraint already projected onto the shell and flattened to the tangent.
                // Do not zero world Y — that is a tangent axis off the equator.
                float3 linear = velocity.ValueRO.Linear;
                kinematics.ValueRW = new ShipKinematics { Velocity = linear };
            }
        }
    }
}
