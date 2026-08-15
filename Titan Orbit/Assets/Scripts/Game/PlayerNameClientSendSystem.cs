using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client tick that keeps the local display name published after GoInGame.
    /// Calls <see cref="PlayerNameRpcClient.TrySendLocalName"/> each frame; that helper
    /// rate-limits and injects Local Host RPCs. No ship-entity queries — safe during join settle.
    /// World: ClientSimulation. Group: SimulationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class PlayerNameClientSendSystem : SystemBase
    {
        /// <summary>
        /// [ECS/DOTS] Rate-limited name publish. Runs on the client world only.
        /// </summary>
        protected override void OnUpdate()
        {
            // --- Publish Main Menu name ---
            // [TITAN-ORBIT] Nameplates / leaderboard read PlayerNameRosterCache. This system is
            // the client half of SetPlayerNameCommand — without it the roster stays empty and HUD
            // falls back to "Player {networkId}".
            PlayerNameRpcClient.TrySendLocalName();
        }
    }
}
