using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Retired. Canonical wrap (<see cref="ShipCanonicalWrapSystem"/>) puts hulls on the same
    /// chart as world colliders, so Unity.Physics owns contacts. Kept as an empty
    /// <see cref="ISystem"/> so existing <c>UpdateAfter</c> attributes still compile until
    /// those call sites are retargeted.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(ShipCanonicalWrapSystem))]
    [UpdateBefore(typeof(ShipPlanarPhysicsConstraintSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipToroidalWorldCollisionSystem : ISystem
    {
        /// <summary>Disabled — wrap + Unity.Physics replaced seam sphere-resolve.</summary>
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
