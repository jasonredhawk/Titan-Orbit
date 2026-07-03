using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Server-authoritative gem deposit request (survives NetCode input resync).</summary>
    public struct ShipDepositIntent : IComponentData
    {
        [GhostField] public bool WantDepositGems;
    }
}
