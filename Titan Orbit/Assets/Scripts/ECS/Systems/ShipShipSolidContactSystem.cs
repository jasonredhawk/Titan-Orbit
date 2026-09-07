using Unity.Entities;
using Unity.NetCode;
using Unity.Physics.Systems;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Retired. Unity Physics + <see cref="ShipPhysicsContactCollectSystem"/> own
    /// ship↔ship and ship↔world contacts. Compound <c>CastCollider</c> against the
    /// whole world does not scale to 60–100 ships. Kept as a disabled
    /// <see cref="ISystem"/> so existing <c>UpdateAfter</c> attributes still compile.
    /// </summary>
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    [UpdateAfter(typeof(ShipAsteroidContactFrictionSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipShipSolidContactSystem : ISystem
    {
        /// <summary>Disabled — event-stream contacts replaced world-wide compound queries.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.Enabled = false;
        }

        /// <summary>No-op. System stays disabled after <see cref="OnCreate"/>.</summary>
        public void OnUpdate(ref SystemState state)
        {
        }
    }
}
