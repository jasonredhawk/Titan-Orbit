using TitanOrbit.Core;
using TitanOrbit.Simulation;
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

        /// <summary>
        /// [TITAN-ORBIT] <c>BulletVfxBank</c> category this planet stamps onto hulls bought here
        /// and onto its planetary-defense pads. Homes are always Laserbolt (0). Neutrals roll
        /// at spawn from match seed + PlanetId (Fireballs / Rift / …). Independent of
        /// <see cref="ShipFamilyConfigIndex"/> so the same captured family can fire a different
        /// gun each match. Ghosted so world labels, minimap hover, and clients resolve the same type.
        /// </summary>
        [GhostField] public byte BulletBankIndex;

        /// <summary>
        /// [TITAN-ORBIT] Player who delivered the most troops during the siege that captured this
        /// planet (0 = none / starting claim). Ghosted so late joiners see the name without an RPC.
        /// </summary>
        [GhostField] public int TopContributorNetworkId;
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
        /// [TITAN-ORBIT] Designer Size rolled at spawn (drives HP, gems, and visual scale).
        /// Ghosted so client prediction matches the server. Live bounce treats rocks as
        /// immovable walls — Size does not scale rebound.
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
        /// Mining chips stay free-for-all. Destroy bursts are reserved for the top damager
        /// for <see cref="GemExclusiveClaim"/>, then anyone may pull them.
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

        /// <summary>
        /// [TITAN-ORBIT] Server-only: last ship NetworkId that mined, shot, rammed, or burned
        /// this rock. Blue top-miner 5% gems Instantiates on destroy only when this id still
        /// holds the miner title. 0 = unknown (no blue extra). Not ghosted.
        /// </summary>
        public int LastInteractNetworkId;

        /// <summary>
        /// [TITAN-ORBIT] Server-only accrued proximity-mine value. A 60 Hz tick yields
        /// ~0.083 (&lt; <c>MinGemSpawnValue</c> 0.25), so without this remainder neither
        /// red chips nor yellow triangle extras Instantiates while parked on a rock.
        /// Flushed at ~1s of <c>MiningRate</c> (or when the rock empties).
        /// </summary>
        public float MiningYieldRemainder;

        /// <summary>
        /// Live rock for combat / aim. Matches destroy + HitRpc (<c>Health ≤ 0.01</c>),
        /// not a raw zero compare — leftover 0.00x HP corpses stay hidden on the client
        /// while miners would otherwise keep shooting empty space.
        /// </summary>
        public bool IsAliveForCombat => !IsDestroyed && Health > 0.01f;
    }

    /// <summary>
    /// Client-only: HitRpc already hid this asteroid. Not ghosted — survives lagging Health snapshots.
    /// See <see cref="AsteroidClientCullPhysicsSystem"/>.
    /// </summary>
    public struct AsteroidClientCulledTag : IComponentData { }

    /// <summary>
    /// Loose gem pickup. Not ghost-replicated — clients hydrate from spawn / burst RPCs
    /// and key consume / tractor events by <see cref="SpawnId"/>.
    /// </summary>
    public struct GemState : IComponentData
    {
        /// <summary>
        /// Stable recipe id from <c>GemSpawnMath.ComputeSpawnId</c> (same on server and client).
        /// </summary>
        public int SpawnId;

        /// <summary>[TITAN-ORBIT] Gem value when deposited or collected.</summary>
        public float Value;

        /// <summary>[TITAN-ORBIT] Visual scale multiplier for gem mesh.</summary>
        public float Size;

        /// <summary>[TITAN-ORBIT] Team that receives credit when deposited (from miner's team).</summary>
        public TeamId DepositTeam;

        /// <summary>
        /// ServerTick-timeline seconds when this gem was spawned (same clock as
        /// <c>PlanetGemMoonOrbitClock</c> — not World.Time.ElapsedTime).
        /// </summary>
        public float SpawnServerTime;

        /// <summary>
        /// [TITAN-ORBIT] Crystal colour only. Red = base yield, yellow = triangle extra,
        /// blue = top-miner 5%. Tractor, pickup, and cargo ignore tint — any ship may scoop.
        /// </summary>
        public GemVisualTint Tint;

        /// <summary>True when this is the yellow triangle extra (not the blue miner extra).</summary>
        public bool IsBonusGem => Tint == GemVisualTint.TerritoryBonus;

        /// <summary>
        /// <see cref="GhostOwner.NetworkId"/> of the ship that spilled this gem from
        /// damage, or 0 if that ship is not excluded (mining / asteroid burst).
        /// Paired with <see cref="ExcludePickupUntilServerTime"/>.
        /// </summary>
        public int ExcludePickupNetworkId;

        /// <summary>
        /// SpawnServerTime-timeline seconds when the expelling ship may collect /
        /// show tractor beams again. 0 = no exclusion.
        /// </summary>
        public float ExcludePickupUntilServerTime;

        /// <summary>
        /// <see cref="GhostOwner.NetworkId"/> of the ship that dealt the most health
        /// damage to the asteroid this crystal burst from. During
        /// <see cref="GemExclusiveClaim"/> only that ship may tractor or absorb.
        /// 0 = free for everyone (mining nuggets, cargo spills, no credited damage).
        /// </summary>
        public int ExclusiveClaimNetworkId;

        /// <summary>
        /// True after the server scoops this crystal into cargo. Clients hide via
        /// <see cref="GemConsumedRpc"/>; leftover flag covers same-tick teardown.
        /// </summary>
        public bool IsConsumed;
    }

    /// <summary>
    /// [LEGACY] Former ghost-hold after scoop. Event-hydrate gems destroy immediately.
    /// </summary>
    public struct GemConsumedPendingDestroy : IComponentData
    {
        /// <summary>Unused — leftover entities are destroyed the same tick.</summary>
        public byte SendsLeft;
    }

    /// <summary>
    /// [ECS/DOTS] Marker on the server singleton that owns the asteroid respawn queue buffer.
    /// Created by <c>AsteroidRespawnSystem</c>; not ghost-replicated.
    /// </summary>
    public struct AsteroidRespawnQueueTag : IComponentData { }

    /// <summary>
    /// [TITAN-ORBIT] One scheduled asteroid respawn — same position/scale/gems/HP as the destroyed rock.
    /// Original NGO <c>AsteroidRespawnManager.PendingRespawn</c>.
    /// Wait is <see cref="TitanOrbit.Data.AsteroidSettings.RespawnDelaySeconds"/> (default 30s).
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

        /// <summary>Blueprint slot to stamp on the respawned rock. −1 if the dead rock had none.</summary>
        public int LayoutSlot;

        /// <summary>
        /// 1 after the server has already sent <see cref="AsteroidRespawnRpc"/> (telegraph).
        /// Instantiates still waits for <see cref="RespawnAtElapsedTime"/> so clients can build
        /// the mesh before the authoritative hull exists.
        /// </summary>
        public byte RpcSent;
    }

    /// <summary>
    /// Client-only: a just-respawned asteroid is growing its hybrid mesh and must not collide
    /// yet. <c>LocalTransform.Scale</c> stays at the designer size (mining / match / restore);
    /// presentation reads <see cref="AsteroidRespawnGrowInLogic.ComputeVisualScale"/>.
    /// </summary>
    public struct AsteroidRespawnGrowIn : IComponentData
    {
        /// <summary>Final uniform mesh scale when the grow finishes.</summary>
        public float TargetScale;

        /// <summary>Seconds since the client Instantiates (presentation + collision arm).</summary>
        public float Elapsed;

        /// <summary>Grow duration (same value the server used as the Instantiates telegraph).</summary>
        public float Duration;
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
    /// [NETCODE] Replicated gem-moon shield reservoir. Shield absorbs bullet damage; leftover
    /// hits knock <see cref="CurrentMoonGems"/> out as pickups. While the shield stays down,
    /// the combat drain can also siphon the planet bank into loose gems.
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

        /// <summary>
        /// Moon-gem value already removed by body hits but still below a spawnable pickup.
        /// The next hit adds to this and spawns one gem. Server-only — not ghosted.
        /// </summary>
        public float GemExpelCarry;
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

    /// <summary>
    /// Back-link from a planet to its kinematic moon-shield sphere. Not a ghost —
    /// each sim world builds its own body. Radius follows
    /// <c>PlanetGemMoonMath.GetMoonShieldOuterRadiusLocal</c>; scale collapses when shield is down.
    /// </summary>
    public struct PlanetGemMoonShieldColliderEntity : IComponentData
    {
        /// <summary>Kinematic shield sphere entity.</summary>
        public Entity ShieldColliderEntity;
    }

    /// <summary>Query filter — kinematic moon-shield sphere that ships bounce on.</summary>
    public struct PlanetGemMoonShieldColliderTag : IComponentData { }

    /// <summary>
    /// Last planet owner baked into this shield collider filter. Recreate the blob when
    /// ownership flips — not every tick.
    /// </summary>
    public struct PlanetGemMoonShieldOwnerState : IComponentData
    {
        public TeamId Owner;
    }

    /// <summary>
    /// Server-only running total of health removed from one asteroid by one ship.
    /// Not ghosted — added at spawn after ghost identity is stripped. Overkill past
    /// 0 HP is not counted; only health the rock actually lost.
    /// </summary>
    public struct AsteroidDamageCreditElement : IBufferElementData
    {
        /// <summary><see cref="GhostOwner.NetworkId"/> of the attacker. Bots count.</summary>
        public int NetworkId;

        /// <summary>Health actually subtracted (clamped to what the rock had left).</summary>
        public float Damage;
    }

    /// <summary>
    /// Accumulates per-ship asteroid health damage and names the top contributor
    /// when the rock bursts its gems.
    /// </summary>
    public static class AsteroidDamageCredit
    {
        /// <summary>
        /// Empty credit buffer on a freshly spawned rock. Clears a copied buffer so a
        /// respawn cannot inherit the previous life's totals.
        /// </summary>
        public static void Ensure(EntityManager em, Entity asteroid)
        {
            if (!em.Exists(asteroid))
                return;

            if (!em.HasBuffer<AsteroidDamageCreditElement>(asteroid))
                em.AddBuffer<AsteroidDamageCreditElement>(asteroid);
            else
                em.GetBuffer<AsteroidDamageCreditElement>(asteroid).Clear();
        }

        /// <summary>
        /// Adds health this hit actually removed. No-op for unknown attackers
        /// (<paramref name="networkId"/> ≤ 0) or when the rock has no credit buffer.
        /// Does not add the buffer — that would be a structural change inside combat queries.
        /// </summary>
        public static void Record(EntityManager em, Entity asteroid, int networkId, float appliedDamage)
        {
            if (networkId <= 0 || appliedDamage <= 0.0001f || !em.Exists(asteroid))
                return;
            if (!em.HasBuffer<AsteroidDamageCreditElement>(asteroid))
                return;

            var credits = em.GetBuffer<AsteroidDamageCreditElement>(asteroid);
            for (int i = 0; i < credits.Length; i++)
            {
                if (credits[i].NetworkId != networkId)
                    continue;

                var row = credits[i];
                row.Damage += appliedDamage;
                credits[i] = row;
                return;
            }

            credits.Add(new AsteroidDamageCreditElement
            {
                NetworkId = networkId,
                Damage = appliedDamage,
            });
        }

        /// <summary>
        /// Highest accumulated health damage. Exact ties keep the earlier contributor
        /// (first ship to reach that total). 0 when nobody has credit.
        /// </summary>
        public static int ResolveTop(DynamicBuffer<AsteroidDamageCreditElement> credits)
        {
            int bestId = 0;
            float best = 0f;
            for (int i = 0; i < credits.Length; i++)
            {
                var row = credits[i];
                if (row.NetworkId <= 0 || row.Damage <= best)
                    continue;

                best = row.Damage;
                bestId = row.NetworkId;
            }

            return bestId;
        }
    }
}
