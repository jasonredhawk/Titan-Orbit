using TitanOrbit.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Ghost-replicated state for planets, asteroids, and gems — the main world entities
    /// in Titan Orbit. Fields marked [GhostField] serialize over NetCode to all clients. Server systems
    /// write authoritative values; HUD and presentation read. Paired with *GhostAuthoring bakers and
    /// planet/gem economy systems.
    /// </summary>
    public struct PlanetState : IComponentData
    {
        // --- Type members ---
        /// <summary>[TITAN-ORBIT] Controlling team; <see cref="TeamId.None"/> for neutral planets.</summary>
        [GhostField] public TeamId Ownership;

        /// <summary>[TITAN-ORBIT] Population units on the planet surface (growth and transport gameplay).</summary>
        [GhostField] public int Population;

        /// <summary>[TITAN-ORBIT] Upgrade ladder level (affects orbit ring size and store tiers).</summary>
        [GhostField] public int PlanetLevel;

        /// <summary>[TITAN-ORBIT] Gem reservoir on the planet surface (not moon gem pool).</summary>
        [GhostField] public float CurrentGems;

        /// <summary>[TITAN-ORBIT] Stable id for orbit/transport lookups across the match.</summary>
        [GhostField] public int PlanetId;

        /// <summary>[TITAN-ORBIT] True for team spawn worlds with ship family config and orbit store.</summary>
        [GhostField] public bool IsHomePlanet;

        /// <summary>[TITAN-ORBIT] Index into PlanetShipFamilyConfig.families; 0 = AstroEagle (home only).</summary>
        [GhostField] public byte ShipFamilyConfigIndex;
    }

    /// <summary>
    /// [NETCODE] Mineable asteroid body — destroyed when <see cref="RemainingGems"/> reaches zero.
    /// </summary>
    public struct AsteroidState : IComponentData
    {
        /// <summary>[TITAN-ORBIT] Gems left to mine before the asteroid is destroyed.</summary>
        [GhostField] public float RemainingGems;

        /// <summary>[TITAN-ORBIT] Hull points — asteroids can be shot for faster mining.</summary>
        [GhostField] public float Health;

        /// <summary>[TITAN-ORBIT] True after destruction; entity may be destroyed next tick.</summary>
        [GhostField] public bool IsDestroyed;

        /// <summary>[TITAN-ORBIT] Team that mined this cluster most recently (territory tint on minimap).</summary>
        [GhostField] public TeamId TerritoryTeam;
    }

    /// <summary>[NETCODE] Loose gem pickup spawned by mining or moon drain.</summary>
    public struct GemState : IComponentData
    {
        /// <summary>[TITAN-ORBIT] Gem value when deposited or collected.</summary>
        [GhostField] public float Value;

        /// <summary>[TITAN-ORBIT] Visual scale multiplier for gem mesh.</summary>
        [GhostField] public float Size;

        /// <summary>[TITAN-ORBIT] Team that receives credit when deposited (from miner's team).</summary>
        [GhostField] public TeamId DepositTeam;
    }

    /// <summary>[ECS/DOTS] Query filter — entity is a planet (home or neutral).</summary>
    public struct PlanetTag : IComponentData { }

    /// <summary>[ECS/DOTS] Query filter — entity is a mineable asteroid.</summary>
    public struct AsteroidTag : IComponentData { }

    /// <summary>[ECS/DOTS] Query filter — entity is a collectible gem pickup.</summary>
    public struct GemTag : IComponentData { }

    /// <summary>[ECS/DOTS] Subset of planets — team spawn point with ship family config.</summary>
    public struct HomePlanetTag : IComponentData { }

    /// <summary>
    /// [ECS/DOTS] Server-side passive population growth state. Not ghost-replicated — clients see
    /// resulting <see cref="PlanetState.Population"/> on the planet ghost.
    /// </summary>
    public struct PlanetGrowthState : IComponentData
    {
        /// <summary>[TITAN-ORBIT] Fractional population accumulator for smooth growth between integer ticks.</summary>
        public float FractionalPopulation;

        /// <summary>[UNITY] Server ElapsedTime of last hostile population impact (growth pause).</summary>
        public float LastHostilePopulationImpactServerTime;
    }

    /// <summary>
    /// [NETCODE] Replicated gem-moon shield reservoir. Shield absorbs bullet damage; when depleted,
    /// moon gems drain and spawn loose gems. Updated by <see cref="PlanetGemMoonShieldSystem"/> and
    /// combat logic.
    /// </summary>
    public struct PlanetGemMoonState : IComponentData
    {
        /// <summary>[TITAN-ORBIT] Current shield points (ghost-replicated for HUD shield bar).</summary>
        [GhostField] public float CurrentShield;

        /// <summary>[TITAN-ORBIT] Maximum shield capacity from planet level.</summary>
        [GhostField] public float MaxShield;

        /// <summary>[UNITY] Server ElapsedTime of last shield hit (VFX cooldown).</summary>
        public float LastShieldHitServerTime;

        /// <summary>[TITAN-ORBIT] Server-only moon gem reservoir damaged when shield is down.</summary>
        public float CurrentMoonGems;

        /// <summary>[TITAN-ORBIT] Maximum moon gem capacity.</summary>
        public float MaxMoonGems;

        /// <summary>[TITAN-ORBIT] Fractional accumulator for steady gem drain rate.</summary>
        public float GemDrainAccumulator;

        /// <summary>[TITAN-ORBIT] Timer until next gem spawn from moon drain.</summary>
        public float GemSpawnTimer;
    }

    /// <summary>
    /// [ECS/DOTS] Back-link from a planet entity to its runtime gem-moon physics body.
    /// Moon colliders are not ghost-replicated — each simulation world builds its own kinematic body.
    /// Written by <see cref="PlanetGemMoonColliderEnsureSystem"/>.
    /// </summary>
    public struct PlanetGemMoonColliderEntity : IComponentData
    {
        /// <summary>Kinematic moon hull entity with <see cref="PhysicsCollider"/> for ship bounce.</summary>
        public Entity MoonColliderEntity;
    }

    /// <summary>
    /// [ECS/DOTS] Query filter — kinematic sphere that blocks ships on the gem-moon orbit ring.
    /// Paired with <see cref="PlanetGemMoonColliderPlanetRef"/>.
    /// </summary>
    public struct PlanetGemMoonColliderTag : IComponentData { }

    /// <summary>
    /// [ECS/DOTS] Parent planet for a gem-moon kinematic collider. Used by
    /// <see cref="PlanetGemMoonColliderSyncSystem"/> to copy orbit pose each physics step.
    /// </summary>
    public struct PlanetGemMoonColliderPlanetRef : IComponentData
    {
        /// <summary>Planet ghost that owns this moon's shield, gems, and orbit phase.</summary>
        public Entity PlanetEntity;
    }
}
