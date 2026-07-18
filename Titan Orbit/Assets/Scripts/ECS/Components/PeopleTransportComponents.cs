using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Query filter tag — server ghost people-transport projectile (combat / delivery).
    /// Client float VFX uses <see cref="PeopleTransportPresentationTag"/> instead (RPC / VFX bridge).
    /// </summary>
    public struct PeopleTransportTag : IComponentData { }

    /// <summary>
    /// [NETCODE] Ghost-replicated people transport projectile state (server combat / delivery).
    /// Visual floats do <b>not</b> wait on this ghost — clients spawn
    /// <see cref="PeopleTransportPresentation"/> from <see cref="PeopleTransportSpawnRpc"/>.
    /// </summary>
    public struct PeopleTransportState : IComponentData
    {
        // --- Type members ---
        /// <summary>[TITAN-ORBIT] Population units remaining in this transport capsule.</summary>
        [GhostField] public float Amount;

        /// <summary>[TITAN-ORBIT] Hull points — transport can be shot down mid-flight.</summary>
        [GhostField] public float Health;

        /// <summary>[ECS/DOTS] World-units-per-second velocity on the XZ plane.</summary>
        [GhostField] public float3 Velocity;

        /// <summary>[ECS/DOTS] Toroidal spawn position for trail VFX and interpolation.</summary>
        [GhostField] public float3 SpawnPosition;

        /// <summary>[UNITY] Server ElapsedTime when this transport was spawned.</summary>
        [GhostField] public float SpawnTime;

        /// <summary>[TITAN-ORBIT] Target cruise speed in world units per second.</summary>
        [GhostField] public float CruiseSpeed;

        /// <summary>
        /// [NETCODE] Destination ship network id; 0 when transport is planet-to-planet only.
        /// </summary>
        [GhostField] public int TargetShipNetworkId;

        /// <summary>[TITAN-ORBIT] Source planet <see cref="PlanetState.PlanetId"/>.</summary>
        [GhostField] public int SourcePlanetId;

        /// <summary>[TITAN-ORBIT] Destination planet <see cref="PlanetState.PlanetId"/>.</summary>
        [GhostField] public int TargetPlanetId;

        /// <summary>[NETCODE] Source ship network id when launched from a ship cargo hold.</summary>
        [GhostField] public int SourceShipNetworkId;

        /// <summary>[TITAN-ORBIT] 1 = loading population from planet onto ship; 0 = unloading toward target.</summary>
        [GhostField] public byte IsLoad;

        /// <summary>[TITAN-ORBIT] Owning team as byte (cast to <see cref="TeamId"/>).</summary>
        [GhostField] public byte Team;
    }

    /// <summary>
    /// [HYBRID] Client-local (non-ghost) tag for cosmetic people-transport floats.
    /// Created from <see cref="PeopleTransportSpawnRpc"/> / in-process VFX bridge — never waits on GhostSpawn.
    /// </summary>
    public struct PeopleTransportPresentationTag : IComponentData { }

    /// <summary>
    /// [HYBRID] Presentation-only flight state on the client. Magnet-steered by
    /// <c>PeopleTransportVisualSyncSystem</c>; drawn by <c>EcsWorldVisualizer</c>.
    /// </summary>
    public struct PeopleTransportPresentation : IComponentData
    {
        /// <summary>Population amount (visual scale).</summary>
        public float Amount;

        /// <summary>Current planar velocity.</summary>
        public float3 Velocity;

        /// <summary>Spawn position (for travel checks).</summary>
        public float3 SpawnPosition;

        /// <summary>Cruise speed for magnet steering.</summary>
        public float CruiseSpeed;

        /// <summary>Load destination ship network id.</summary>
        public int TargetShipNetworkId;

        /// <summary>Source planet id.</summary>
        public int SourcePlanetId;

        /// <summary>Unload destination planet id.</summary>
        public int TargetPlanetId;

        /// <summary>1 = load, 0 = unload.</summary>
        public byte IsLoad;

        /// <summary>Team tint byte.</summary>
        public byte Team;

        /// <summary>Seconds until auto-despawn (cosmetic lifetime).</summary>
        public float RemainingLifetime;

        /// <summary>Dedupe key from server spawn sequence.</summary>
        public uint Sequence;
    }
}
