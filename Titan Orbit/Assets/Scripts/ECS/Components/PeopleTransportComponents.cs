using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Query filter — entity is a people-transport projectile.</summary>
    public struct PeopleTransportTag : IComponentData { }

    /// <summary>
    /// Ghost-replicated people transport projectile state. Spawned by <see cref="PeopleTransportDispatchSystem"/>
    /// when a ship loads/unloads population at an orbiting planet. Simulated by people transport systems.
    /// </summary>
    public struct PeopleTransportState : IComponentData
    {
        /// <summary>Population units remaining in this transport.</summary>
        [GhostField] public float Amount;
        [GhostField] public float Health;
        [GhostField] public float3 Velocity;
        [GhostField] public float3 SpawnPosition;
        [GhostField] public float SpawnTime;
        [GhostField] public float CruiseSpeed;
        /// <summary>Destination ship network id (0 if planet-to-planet).</summary>
        [GhostField] public int TargetShipNetworkId;
        [GhostField] public int SourcePlanetId;
        [GhostField] public int TargetPlanetId;
        [GhostField] public int SourceShipNetworkId;
        /// <summary>1 = loading from planet, 0 = unloading toward target.</summary>
        [GhostField] public byte IsLoad;
        [GhostField] public byte Team;
    }
}
