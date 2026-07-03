using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Per-player contributed gem balance at a home planet (store currency).</summary>
    public struct ContributedGemsElement : IBufferElementData
    {
        public int NetworkId;
        public float Amount;
    }

    /// <summary>Singleton config for moon orbit store (ship family + upgrade tree assets).</summary>
    public struct MoonOrbitStoreConfig : IComponentData
    {
        public Entity ShipFamilyEntity;
    }
}
