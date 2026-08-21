using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// After PhysX integrates ship position from <c>PhysicsVelocity</c>, wraps every simulated
    /// hull into the canonical map cell. Collision then uses one torus path on those wrapped
    /// poses. Paired with <see cref="ShipToroidalWorldCollisionSystem"/> (runs after this).
    /// Pipeline: Drive → PhysX integrate → Wrap (this) → Toroidal collide → Planar → Kinematics.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(ShipToroidalWorldCollisionSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipWrapSystem : ISystem
    {
        /// <summary>
        /// Wrap is off while <see cref="ToroidalMapEcs.TopologyEnabled"/> is false.
        /// PhysX + edge walls keep hulls in the arena.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            state.Enabled = false;
            state.RequireForUpdate<ShipTag>();
        }

        /// <summary>
        /// Wraps each living simulated ship's <see cref="LocalTransform"/> into
        /// <c>[-W/2, W/2) × [-H/2, H/2)</c>. Skips when map size is missing.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            float preferredW = 0f;
            float preferredH = 0f;
            if (SystemAPI.TryGetSingleton(out MapStateSingleton mapState) &&
                ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight))
            {
                preferredW = mapState.MapWidth;
                preferredH = mapState.MapHeight;
            }

            if (!ToroidalMapEcs.ResolveMapSize(preferredW, preferredH, out float mapW, out float mapH))
                return;

            foreach (var (transform, shipState) in SystemAPI
                         .Query<RefRW<LocalTransform>, RefRO<ShipState>>()
                         .WithAll<ShipTag, Simulate>())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                var lt = transform.ValueRO;
                float3 wrapped = ToroidalMapEcs.Wrap(lt.Position, mapW, mapH);
                if (math.distancesq(lt.Position, wrapped) < 1e-8f)
                    continue;

                lt.Position = wrapped;
                transform.ValueRW = lt;
            }
        }
    }
}
