using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Applies pending input on the in-game client for predicted ghosts (non-local-host online play).</summary>
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
            if (!ShipPendingInput.HasValue || ShipPendingInput.LocalHostMode)
                return;

            var cmd = ShipPendingInput.Latest;

            foreach (var input in SystemAPI.Query<RefRW<ShipInput>>().WithAll<ShipTag, GhostOwnerIsLocal>())
                input.ValueRW = cmd;

            foreach (var input in SystemAPI.Query<RefRW<ShipInput>>().WithAll<ShipTag, LocalPlayerShipTag>())
                input.ValueRW = cmd;
        }
    }
}
