using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    // Pipeline: ShipInputApplySystem → ShipClientPredictedPhysicsDriveSystem → PhysicsSystemGroup → …
    /// <summary>
    /// Copies the latest player input from <see cref="ShipPendingInput"/> onto the local ship
    /// ghost during <see cref="GhostInputSystemGroup"/> — before prediction runs this tick.
    /// [NETCODE] Client-side instancy (Starblast pillar 1): the local ship executes
    /// <see cref="ShipClientPredictedPhysicsDriveSystem"/> on the current tick immediately;
    /// server reconciliation is silent via NetCode rollback/resim. Dedicated server reads
    /// replicated <see cref="ShipInput"/> ghost commands instead. Paired with
    /// <see cref="Game.ShipInputBridge"/>.
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
