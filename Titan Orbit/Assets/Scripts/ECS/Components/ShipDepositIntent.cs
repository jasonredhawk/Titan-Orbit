using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-authoritative gem deposit request that survives NetCode input resync.
    /// ShipInput.WantDepositGems can be lost during prediction rollback; this component
    /// is set by RPC (SetWantDepositGemsCommand) and read by GemDepositSystem.
    /// Ghost-serialized so clients see deposit toggle state in orbit UI.
    /// </summary>
    public struct ShipDepositIntent : IComponentData
    {
        [GhostField] public bool WantDepositGems;
    }
}
