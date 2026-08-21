using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Physics.Systems;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Copies each simulated ship's <see cref="PhysicsVelocity.Linear"/> and
    /// <see cref="LocalTransform.Position"/> into <see cref="ShipPreCollisionVelocity"/>
    /// after thrust and before the physics solve.
    /// <para>
    /// [PHYSICS] Must run inside <see cref="PhysicsSystemGroup"/> before
    /// <see cref="PhysicsSimulationGroup"/> so the snapshot is the post-drive, pre-contact state.
    /// [NETCODE] Server + predicted client — identical timing so bounce prediction matches authority.
    /// </para>
    /// Pipeline: Drive → Snapshot (this) → PhysicsSimulation → Export → Bounce → Friction → Toroidal.
    /// </summary>
    [UpdateInGroup(typeof(PhysicsSystemGroup))]
    [UpdateBefore(typeof(PhysicsSimulationGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipPreCollisionVelocitySnapshotSystem : ISystem
    {
        /// <summary>Require ships before walking PhysicsVelocity.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipTag>();
        }

        /// <summary>
        /// Writes pre-collision linear velocity for every living simulated ship that has
        /// <see cref="ShipPreCollisionVelocity"/> baked (or added at runtime).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Join-crash gate (client only) ---
            // [TITAN-ORBIT] Ship entity walks during TeamChoice Instantiates Crash!!! —
            // ShouldSkipShipSimulation covers that window; map Instantiates backlog must not
            // freeze pre-collision snapshots. Server always snapshots.
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            foreach (var (velocity, transform, snapshot, shipState) in SystemAPI
                         .Query<RefRO<PhysicsVelocity>, RefRO<LocalTransform>, RefRW<ShipPreCollisionVelocity>,
                             RefRO<ShipState>>()
                         .WithAll<ShipTag, Simulate>())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                // Keep the full tangent velocity. Zeroing world Y was a leftover XZ-plane
                // restraint and deleted north/south motion on the sphere.
                snapshot.ValueRW.Linear = velocity.ValueRO.Linear;
                snapshot.ValueRW.Position = transform.ValueRO.Position;
                snapshot.ValueRW.Rotation = transform.ValueRO.Rotation;
            }
        }
    }
}
