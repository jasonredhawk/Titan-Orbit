using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    // Order: (input) → ShipInputApplySystem → ShipClientPredictedMovementSystem → …
    /// <summary>
    /// Copies pending player input onto the local predicted ship ghost during
    /// <see cref="GhostInputSystemGroup"/>. Client simulation only; server reads replicated input.
    /// </summary>
    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipInputApplySystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!ShipPendingInput.HasValue)
                return;

            var cmd = ShipPendingInput.Latest;

            foreach (var input in SystemAPI.Query<RefRW<ShipInput>>().WithAll<ShipTag, GhostOwnerIsLocal>())
                input.ValueRW = cmd;

            foreach (var input in SystemAPI.Query<RefRW<ShipInput>>().WithAll<ShipTag, LocalPlayerShipTag>())
                input.ValueRW = cmd;
        }
    }
}
