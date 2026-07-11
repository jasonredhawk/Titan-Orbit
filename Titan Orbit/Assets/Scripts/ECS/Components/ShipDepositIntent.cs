using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-authoritative gem deposit toggle that survives NetCode input prediction rollback.
    /// [NETCODE] <see cref="ShipInput.WantDepositGems"/> can be lost during prediction resync;
    /// this component is set by <see cref="SetWantDepositGemsCommand"/> RPC and read by gem deposit
    /// systems each server tick. [NETCODE] Ghost-serialized so orbit UI shows deposit state on all clients.
    /// Paired with <see cref="ShipMoonDockState"/> (must be landed) and moon orbit store RPCs.
    /// </summary>
    public struct ShipDepositIntent : IComponentData
    {
        // --- Type members ---
        /// <summary>
        /// [TITAN-ORBIT] When true and the ship is fully docked at a moon, gems transfer from ship
        /// cargo to the planet pool each server tick. Toggled by orbit UI via RPC, not raw input alone.
        /// </summary>
        [GhostField] public bool WantDepositGems;
    }
}
