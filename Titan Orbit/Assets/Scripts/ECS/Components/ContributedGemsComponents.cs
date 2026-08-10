using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Per-player contributed gem balance at a home planet — orbit-store currency earned
    /// by depositing gems at moons. Server-only buffer on home planet entities; not ghost-replicated
    /// per entry (clients request balance via <see cref="RequestContributedGemsCommand"/> RPC).
    /// Read/written by <see cref="ContributedGemsLogic"/> and moon orbit store purchase RPCs.
    /// </summary>
    public struct ContributedGemsElement : IBufferElementData
    {
        // --- Type members ---
        /// <summary>[NETCODE] NetworkId of the player who earned these gems via moon deposits.</summary>
        public int NetworkId;

        /// <summary>[TITAN-ORBIT] Contributed gem balance spendable at this home planet's orbit store.</summary>
        public float Amount;
    }

    /// <summary>
    /// [ECS/DOTS] Singleton config on a home planet linking it to ship-family ScriptableObject data
    /// for the orbit store UI. Baked or set at map generation for home planets only.
    /// </summary>
    public struct MoonOrbitStoreConfig : IComponentData
    {
        /// <summary>
        /// [ECS/DOTS] Entity holding ship family config (or reference entity from authoring bake).
        /// Orbit store UI reads upgrade branches from this entity's components.
        /// </summary>
        public Entity ShipFamilyEntity;
    }
}
