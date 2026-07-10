using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Ghost-replicated state for planets, asteroids, and gems — the main world entities in Titan Orbit.
    /// Fields marked [GhostField] serialize over NetCode to all clients. Server systems write; HUD and
    /// presentation read. Paired with *GhostAuthoring bakers and planet/gem economy systems.
    /// </summary>
    public struct PlanetState : IComponentData
    {
        /// <summary>Controlling team; None for neutral planets.</summary>
        [GhostField] public TeamId Ownership;
        [GhostField] public int Population;
        [GhostField] public int PlanetLevel;
        /// <summary>Gem reservoir on the planet surface (not moon).</summary>
        [GhostField] public float CurrentGems;
        /// <summary>Stable id for orbit/transport lookups across the match.</summary>
        [GhostField] public int PlanetId;
        [GhostField] public bool IsHomePlanet;
        /// <summary>Index into PlanetShipFamilyConfig.families. 0 = AstroEagle (home only).</summary>
        [GhostField] public byte ShipFamilyConfigIndex;
    }

    /// <summary>Mineable asteroid body — destroyed when RemainingGems reaches zero.</summary>
    public struct AsteroidState : IComponentData
    {
        [GhostField] public float RemainingGems;
        [GhostField] public float Health;
        [GhostField] public bool IsDestroyed;
        /// <summary>Team that mined this cluster most recently (territory tint).</summary>
        [GhostField] public TeamId TerritoryTeam;
    }

    /// <summary>Loose gem pickup spawned by mining or moon drain.</summary>
    public struct GemState : IComponentData
    {
        [GhostField] public float Value;
        [GhostField] public float Size;
        /// <summary>Team that will receive credit when deposited (from miner).</summary>
        [GhostField] public TeamId DepositTeam;
    }

    /// <summary>Query filter tag — entity is a planet (home or neutral).</summary>
    public struct PlanetTag : IComponentData { }
    public struct AsteroidTag : IComponentData { }
    public struct GemTag : IComponentData { }
    /// <summary>Subset of planets — team spawn point with ship family config.</summary>
    public struct HomePlanetTag : IComponentData { }

    /// <summary>
    /// Server-side passive population growth (legacy Planet.Update growth loop).
    /// Not ghost-replicated — clients see resulting Population on PlanetState.
    /// </summary>
    public struct PlanetGrowthState : IComponentData
    {
        /// <summary>Fractional population used for smooth growth (legacy currentPopulation float).</summary>
        public float FractionalPopulation;
        public float LastHostilePopulationImpactServerTime;
    }

    /// <summary>
    /// Replicated gem-moon shield reservoir (legacy PlanetGemMoon shieldPoints).
    /// Shield absorbs bullet damage; when depleted, moon gems drain. Updated by
    /// <see cref="PlanetGemMoonShieldSystem"/> and combat logic.
    /// </summary>
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
