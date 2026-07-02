using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    public struct PlanetState : IComponentData
    {
        [GhostField] public TeamId Ownership;
        [GhostField] public int Population;
        [GhostField] public int PlanetLevel;
        [GhostField] public float CurrentGems;
        [GhostField] public int PlanetId;
        [GhostField] public bool IsHomePlanet;
    }

    public struct AsteroidState : IComponentData
    {
        [GhostField] public float RemainingGems;
        [GhostField] public float Health;
        [GhostField] public bool IsDestroyed;
        [GhostField] public TeamId TerritoryTeam;
    }

    public struct GemState : IComponentData
    {
        [GhostField] public float Value;
        [GhostField] public float Size;
        [GhostField] public TeamId DepositTeam;
    }

    public struct PlanetTag : IComponentData { }
    public struct AsteroidTag : IComponentData { }
    public struct GemTag : IComponentData { }
    public struct HomePlanetTag : IComponentData { }

    /// <summary>Server-side passive population growth (legacy Planet.Update growth loop).</summary>
    public struct PlanetGrowthState : IComponentData
    {
        /// <summary>Fractional population used for smooth growth (legacy currentPopulation float).</summary>
        public float FractionalPopulation;
        public float LastHostilePopulationImpactServerTime;
    }
}
