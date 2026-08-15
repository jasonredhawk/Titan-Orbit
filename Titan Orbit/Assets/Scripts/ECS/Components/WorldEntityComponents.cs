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
    /// Server schedules a fresh spawn via <see cref="PendingAsteroidRespawnElement"/> (original ~30s).
    /// </summary>
    public struct AsteroidState : IComponentData
    {
        /// <summary>[TITAN-ORBIT] Gems left to mine before the asteroid is destroyed.</summary>
        [GhostField] public float RemainingGems;

        /// <summary>[TITAN-ORBIT] Hull points — asteroids can be shot for faster mining.</summary>
        [GhostField] public float Health;

        /// <summary>
        /// [TITAN-ORBIT] Designer Size rolled at spawn (drives HP, gems, visual scale, and
        /// virtual collision mass for ship bounce). Ghosted so client prediction uses the same
        /// mass as the server when applying <c>ShipCollisionImpulseLogic</c>.
        /// </summary>
        [GhostField] public float Size;

        /// <summary>[TITAN-ORBIT] True after destruction; entity may be destroyed next tick.</summary>
        [GhostField] public bool IsDestroyed;

        /// <summary>
        /// [TITAN-ORBIT] Strongest (highest gem-mult) team triangle containing this asteroid.
        /// Fallback tint when the viewer is not in <see cref="TerritoryTeamsMask"/>.
        /// Written by <c>AsteroidTerritorySystem</c>. None = outside all triangles.
        /// </summary>
        [GhostField] public TeamId TerritoryTeam;

        /// <summary>
        /// [TITAN-ORBIT] Bitmask of every team whose triangle covers this rock (TeamA=bit0…TeamE=bit4).
        /// Overlaps set multiple bits — the asteroid is “both teams” for extra mining / destroy
        /// yield (yellow tint). Client rock tint prefers the local team when their bit is set.
        /// Extra crystals are still free-for-all once they Instantiates.
        /// </summary>
        [GhostField] public byte TerritoryTeamsMask;

        /// <summary>
        /// [TITAN-ORBIT] Original gem capacity at spawn (server-only). RemainingGems hits 0 on destroy,
        /// so respawn must restore from this — matches NGO maxGems when a fresh instance was spawned.
        /// </summary>
        public float MaxGems;

        /// <summary>
        /// [TITAN-ORBIT] Original max Health at spawn (server-only). Independent of MaxGems when
        /// <c>AsteroidSettings</c> HealthPerSize ≠ GemsPerSize. Respawn restores Health from this.
        /// </summary>
        public float MaxHealth;

        /// <summary>
        /// [TITAN-ORBIT] Server-only: last ship team that mined or shot this rock. Extra yellow
        /// crystals Instantiates on destroy only when this team owns the rock (triangle bonus
        /// yield). Once spawned, yellow gems scoop like red — any ship, including enemies.
        /// Not ghosted — clients do not need it for presentation.
        /// </summary>
        public TeamId LastInteractTeam;
    }

    /// <summary>
    /// Client-only: HitRpc already hid this asteroid. Not ghosted — survives lagging Health snapshots.
    /// See <see cref="AsteroidClientCullPhysicsSystem"/>.
    /// </summary>
    public struct AsteroidClientCulledTag : IComponentData { }

    /// <summary>[NETCODE] Loose gem pickup spawned by mining or moon drain.</summary>
    public struct GemState : IComponentData
    {
        /// <summary>
        /// Session-unique id stamped at spawn. <see cref="GhostInstance.ghostId"/> is reused
        /// after despawn, so leftovers cannot be traced by ghostId alone.
        /// </summary>
        [GhostField] public int SpawnId;

        /// <summary>[TITAN-ORBIT] Gem value when deposited or collected.</summary>
        [GhostField] public float Value;

        /// <summary>[TITAN-ORBIT] Visual scale multiplier for gem mesh.</summary>
        [GhostField] public float Size;

        /// <summary>[TITAN-ORBIT] Team that receives credit when deposited (from miner's team).</summary>
        [GhostField] public TeamId DepositTeam;

        /// <summary>
        /// [NETCODE] ServerTick-timeline seconds when this gem was spawned (same clock as
        /// <c>PlanetGemMoonOrbitClock</c> — not World.Time.ElapsedTime, which diverges on late-join).
        /// Ghosted so clients can shrink in the last seconds of life; server destroys after lifetime.
        /// </summary>
        [GhostField] public float SpawnServerTime;

        /// <summary>
        /// [TITAN-ORBIT] Yellow tint only (NGO <c>isBonusGem</c>). Marks extra yield from a
        /// friendly triangle so players can see the bonus. Tractor, pickup, and cargo treat
        /// this like any other gem — colour does not gate who may collect.
        /// </summary>
        [GhostField] public bool IsBonusGem;

        /// <summary>
        /// [TITAN-ORBIT] <see cref="GhostOwner.NetworkId"/> of the ship that spilled this gem from
        /// damage, or 0 if free for everyone (mining / asteroid burst). Ghosted so client tractor
        /// VFX can hide beams during the self-pickup penalty (server already skips pull/pickup).
        /// Paired with <see cref="ExcludePickupUntilServerTime"/>.
        /// </summary>
        [GhostField] public int ExcludePickupNetworkId;

        /// <summary>
        /// [TITAN-ORBIT] SpawnServerTime-timeline seconds when the expelling ship may collect /
        /// show tractor beams again. 0 = no exclusion. Ghosted with <see cref="ExcludePickupNetworkId"/>.
        /// </summary>
        [GhostField] public float ExcludePickupUntilServerTime;

        /// <summary>
        /// [NETCODE] True after the server scoops this crystal into cargo. Ghosted so clients
        /// hide the mesh immediately — interpolated despawn can lag or drop, which left a
        /// shrinking leftover on the map while cargo had already increased.
        /// Server destroys the entity after a couple of GhostSend ticks
        /// (<see cref="GemConsumedPendingDestroy"/>).
        /// </summary>
        [GhostField] public bool IsConsumed;
    }

    /// <summary>
    /// Server-only: keep a scooped gem alive for a few GhostSend ticks so
    /// <see cref="GemState.IsConsumed"/> can replicate, then DestroyEntity.
    /// </summary>
    public struct GemConsumedPendingDestroy : IComponentData
    {
        /// <summary>GhostSend passes remaining before DestroyEntity (decremented after each send).</summary>
        public byte SendsLeft;
    }

    /// <summary>
    /// [ECS/DOTS] Marker on the server singleton that owns the asteroid respawn queue buffer.
    /// Created by <c>AsteroidRespawnSystem</c>; not ghost-replicated.
    /// </summary>
    public struct AsteroidRespawnQueueTag : IComponentData { }

    /// <summary>
    /// [TITAN-ORBIT] One scheduled asteroid respawn — same position/scale/gems/HP as the destroyed rock.
    /// Original NGO <c>AsteroidRespawnManager.PendingRespawn</c> (default delay 30s).
    /// </summary>
    public struct PendingAsteroidRespawnElement : IBufferElementData
    {
        /// <summary>World XZ position (Y locked to 0 on spawn).</summary>
        public float3 Position;

        /// <summary>Uniform LocalTransform scale for the fresh asteroid.</summary>
        public float Scale;

        /// <summary>Full gem capacity restored on respawn (<see cref="AsteroidState.MaxGems"/>).</summary>
        public float GemValue;

        /// <summary>Full Health restored on respawn (<see cref="AsteroidState.MaxHealth"/>).</summary>
        public float MaxHealth;

        /// <summary>Designer Size restored on respawn (<see cref="AsteroidState.Size"/>).</summary>
        public float Size;

        /// <summary>Server ElapsedTime when this entry is due to spawn.</summary>
        public double RespawnAtElapsedTime;
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

        /// <summary>
        /// [TITAN-ORBIT] Stacked connection-triangle bonus fraction for max pop + growth
        /// (original NGO <c>SetConnectionBonuses</c>). Server-only; written by
        /// <c>PlanetConnectionGraphSystem</c>. 0 = no triangle corners.
        /// </summary>
        public float ConnectionBonusFraction;
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

        /// <summary>
        /// [TITAN-ORBIT] Moon gem reservoir damaged when shield is down.
        /// Ghosted so clients can gate crown (Lv7) defense UI when the pool is full.
        /// </summary>
        [GhostField(Quantization = 100)] public float CurrentMoonGems;

        /// <summary>
        /// [TITAN-ORBIT] Maximum moon gem capacity. Ghosted with <see cref="CurrentMoonGems"/>.
        /// </summary>
        [GhostField(Quantization = 100)] public float MaxMoonGems;

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
