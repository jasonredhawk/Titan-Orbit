using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Query filter tag — entity is a people-transport projectile (not a ship or gem).
    /// Used by people transport sim systems to narrow queries.
    /// </summary>
    public struct PeopleTransportTag : IComponentData { }

    /// <summary>
    /// [NETCODE] Ghost-replicated people transport projectile state. Spawned by people transport
    /// dispatch systems when a ship loads or unloads population at an orbiting planet. Simulated by
    /// <see cref="PeopleTransportSystem"/> on the server; clients interpolate ghost replicas for VFX.
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
}
