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
        /// <summary>Index into PlanetShipFamilyConfig.families. 0 = AstroEagle (home only).</summary>
        [GhostField] public byte ShipFamilyConfigIndex;
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

    /// <summary>Replicated gem-moon shield reservoir (legacy PlanetGemMoon shieldPoints).</summary>
    public struct PlanetGemMoonState : IComponentData
    {
        [GhostField] public float CurrentShield;
        [GhostField] public float MaxShield;
        public float LastShieldHitServerTime;
        /// <summary>Server-only moon gem reservoir damaged when shield is down (legacy gemPoints).</summary>
        public float CurrentMoonGems;
        public float MaxMoonGems;
        public float GemDrainAccumulator;
        public float GemSpawnTimer;
    }
}
