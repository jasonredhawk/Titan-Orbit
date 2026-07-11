using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    // [TITAN-ORBIT] Pipeline order: (MonoBehaviour input) → ShipInputApplySystem → ShipClientPredictedMovementSystem → …
    /// <summary>
    /// Copies the latest player input from <see cref="ShipPendingInput"/> onto the local ship
    /// ghost during <see cref="GhostInputSystemGroup"/>. Runs on the client simulation world only;
    /// the dedicated server reads replicated <see cref="ShipInput"/> from NetCode ghost commands.
    /// Paired with <see cref="Game.ShipInputBridge"/> which fills ShipPendingInput each frame.
    /// </summary>
    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipInputApplySystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            // [NETCODE] Wait until the client connection is in-game before applying input.
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // --- System OnUpdate ---
            if (!ShipPendingInput.HasValue)
                return;

            var cmd = ShipPendingInput.Latest;

            // [NETCODE] GhostOwnerIsLocal — NetCode's tag for the connection-owned ghost.
            foreach (var input in SystemAPI.Query<RefRW<ShipInput>>().WithAll<ShipTag, GhostOwnerIsLocal>())
                input.ValueRW = cmd;

            // [TITAN-ORBIT] Fallback tag added by LocalPlayerTagSystem for hybrid host paths.
            foreach (var input in SystemAPI.Query<RefRW<ShipInput>>().WithAll<ShipTag, LocalPlayerShipTag>())
                input.ValueRW = cmd;
        }
    }
}
