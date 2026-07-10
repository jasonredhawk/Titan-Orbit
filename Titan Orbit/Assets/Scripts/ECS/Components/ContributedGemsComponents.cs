using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Per-player contributed gem balance at a home planet (orbit-store currency).
    /// Server-only buffer on home planet entities — not ghost-replicated per entry.
    /// Read/written by <see cref="ContributedGemsLogic"/> and moon orbit store RPCs.
    /// </summary>
    public struct ContributedGemsElement : IBufferElementData
    {
        /// <summary>Player who earned these gems via deposits.</summary>
        public int NetworkId;
        public float Amount;
    }

    /// <summary>
    /// Singleton config linking a planet entity to ship-family ScriptableObject data for the orbit store UI.
    /// Baked or set at map generation for home planets.
    /// </summary>
    public struct MoonOrbitStoreConfig : IComponentData
    {
        /// <summary>Entity holding ship family config (or reference entity from authoring).</summary>
        public Entity ShipFamilyEntity;
    }
}
