using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client tick that keeps the local Colorize accents published after GoInGame.
    /// Calls <see cref="ShipAccentColorsRpcClient.TrySendLocalAccents"/> each frame;
    /// that helper rate-limits and injects Local Host RPCs.
    /// World: ClientSimulation. Group: SimulationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class ShipAccentColorsClientSendSystem : SystemBase
    {
        /// <summary>Rate-limited accent publish. Runs on the client world only.</summary>
        protected override void OnUpdate()
        {
            ShipAccentColorsRpcClient.TrySendLocalAccents();
        }
    }
}
