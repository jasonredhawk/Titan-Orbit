using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client-only: interpolated remote ships become kinematic solids in Unity Physics
    /// before the solver runs. Owner-predicted ghosts stay dynamic.
    /// <para>
    /// [NETCODE] Remotes have <see cref="Simulate"/> disabled and their pose is driven by
    /// ghost interpolation. If they stay dynamic, the solver writes a depenetration that
    /// interpolation immediately overwrites — hulls sink into each other and stick.
    /// Kinematic remotes keep the interpolated pose and act as infinite-mass obstacles
    /// the local predicted ship can bounce off with real <see cref="PhysicsCollider"/>s.
    /// </para>
    /// [TITAN-ORBIT] No ship gather with <c>WithEntityAccess</c> — presentation uses
    /// <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/>; this is predicted
    /// sim and uses <see cref="ClientJoinSettleCache.ShouldSkipShipSimulation"/>.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(ShipPhysicsMassSyncSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipRemoteKinematicObstacleSystem : ISystem
    {
        /// <summary>Need a ship hull before converting remotes to kinematic obstacles.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipTag>();
        }

        /// <summary>
        /// Sets interpolated remotes to moving kinematics so Unity Physics can generate
        /// real ship↔ship contacts against the interpolated hull.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            foreach (var (physicsMass, physicsVelocity, collider, kinematics) in SystemAPI
                         .Query<RefRW<PhysicsMass>, RefRW<PhysicsVelocity>, RefRO<PhysicsCollider>,
                             RefRO<ShipKinematics>>()
                         .WithAll<ShipTag>()
                         .WithNone<PredictedGhost>())
            {
                if (!collider.ValueRO.Value.IsCreated)
                    continue;

                physicsMass.ValueRW = PhysicsMass.CreateKinematic(
                    collider.ValueRO.Value.Value.MassProperties);

                // Moving kinematic — Unity Physics bounces the local dynamic hull off this
                // interpolated pose/velocity instead of treating the remote as a parked wall.
                float3 linear = kinematics.ValueRO.Velocity;
                linear.y = 0f;
                physicsVelocity.ValueRW = new PhysicsVelocity
                {
                    Linear = linear,
                    Angular = float3.zero,
                };
            }
        }
    }
}
