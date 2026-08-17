using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-authoritative mine detonation. Each live <see cref="DeployedMineElement"/> sits
    /// still until an enemy ship or enemy moon (shield shell, or surface if the shield is down)
    /// overlaps it, or until <see cref="DeployedMineElement.ExpireTime"/> — then it explodes
    /// with the same damage + concussive blast. Hull absorbs first; leftover damage
    /// expels cargo 1:1. Death still requires hull and gems both empty.
    /// <para>
    /// [TITAN-ORBIT] All range tests use <see cref="ToroidalMapEcs.ToroidalDistance"/> /
    /// <see cref="PlanetOrbitMath.GetMoonWorldPositionNear"/>. Friendly ships and friendly moons
    /// pass through. Asteroids do not trigger.
    /// </para>
    /// World: ServerSimulation. Group: SimulationSystemGroup, after deploy.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ShipMineDeploySystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct MineSimulationSystem : ISystem
    {
        /// <summary>Wait until the match is in-game.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        /// <summary>
        /// Walk every ship's mine buffer. Contact or timeout → damage, splash, RPC, remove.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState) ||
                !ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight))
                return;

            float mapW = mapState.MapWidth;
            float mapH = mapState.MapHeight;
            double serverElapsed = SystemAPI.Time.ElapsedTime;

            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double moonElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : serverElapsed;

            Entity gemPrefab = Entity.Null;
            if (SystemAPI.TryGetSingleton<GamePrefabs>(out var gamePrefabs))
                gemPrefab = gamePrefabs.Gem;

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (_, entity) in SystemAPI
                         .Query<RefRO<ShipTag>>()
                         .WithEntityAccess())
            {
                if (!state.EntityManager.HasBuffer<DeployedMineElement>(entity))
                    continue;

                var mines = state.EntityManager.GetBuffer<DeployedMineElement>(entity);
                for (int i = mines.Length - 1; i >= 0; i--)
                {
                    var mine = mines[i];
                    bool timedOut = serverElapsed >= mine.ExpireTime - 0.0001;
                    Entity contactShip = Entity.Null;
                    Entity contactPlanet = Entity.Null;

                    if (!timedOut)
                    {
                        if (TryFindEnemyShipContact(
                                state.EntityManager, in mine, serverElapsed, mapW, mapH, out contactShip))
                        {
                            // Contact ship found — explode below.
                        }
                        else if (TryFindEnemyMoonContact(
                                     state.EntityManager, in mine, moonElapsed, mapW, mapH,
                                     out contactPlanet))
                        {
                            // Contact moon found — explode below.
                        }
                        else
                        {
                            continue;
                        }
                    }

                    ExplodeMine(
                        state.EntityManager, ref ecb, in mine,
                        contactShip, contactPlanet,
                        gemPrefab, serverElapsed, mapW, mapH);

                    mines = state.EntityManager.GetBuffer<DeployedMineElement>(entity);
                    if (i < mines.Length)
                        mines.RemoveAt(i);
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>
        /// True when an enemy (or owner-mismatched) living ship overlaps the mine trigger.
        /// Same-team and owner hulls are ignored so the dropper can fly away.
        /// </summary>
        static bool TryFindEnemyShipContact(
            EntityManager em,
            in DeployedMineElement mine,
            double serverElapsed,
            float mapW,
            float mapH,
            out Entity contactShip)
        {
            contactShip = Entity.Null;
            bool selfHarm = TitanOrbitDebugFlags.IsSelfHarmArmed(mine.PlaceTime, serverElapsed);

            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GhostOwner>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var states = query.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);

            float3 minePos = mine.Position;
            minePos.y = 0f;

            for (int i = 0; i < entities.Length; i++)
            {
                if (states[i].IsDead || states[i].AwaitingTeamSelection)
                    continue;
                if (!selfHarm)
                {
                    if (mine.OwnerNetworkId > 0 && owners[i].NetworkId == mine.OwnerNetworkId)
                        continue;
                    if (mine.OwnerTeam != 0 && (byte)states[i].Team == mine.OwnerTeam)
                        continue;
                }
                if (ShipMoonDockState.IsFullyLandedOnMoon(em, entities[i]))
                    continue;

                float3 shipPos = transforms[i].Position;
                shipPos.y = 0f;
                float hull = BodyCollisionMath.GetShipHullRadiusWorld(transforms[i].Scale);
                float reach = hull + math.max(0.1f, mine.HitRadius);
                float dist = ToroidalMapEcs.ToroidalDistance(minePos, shipPos, mapW, mapH);
                if (dist <= reach)
                {
                    contactShip = entities[i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when an enemy / neutral moon shield (or surface if the shield is down) overlaps
        /// the mine. Friendly moons pass through.
        /// </summary>
        static bool TryFindEnemyMoonContact(
            EntityManager em,
            in DeployedMineElement mine,
            double moonElapsed,
            float mapW,
            float mapH,
            out Entity contactPlanet)
        {
            contactPlanet = Entity.Null;

            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<PlanetGemMoonState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var planets = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var moons = query.ToComponentDataArray<PlanetGemMoonState>(Allocator.Temp);
            using var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            float3 minePos = mine.Position;
            minePos.y = 0f;
            var attackerTeam = (TeamId)mine.OwnerTeam;

            for (int i = 0; i < entities.Length; i++)
            {
                if (PlanetGemMoonCombatLogic.IsTeamFriendlyToMoon(planets[i].Ownership, attackerTeam))
                    continue;

                float planetSize = math.max(0.25f, transforms[i].Scale);
                float hitRadius = PlanetGemMoonMath.GetMoonBulletHitRadiusWorld(
                    planetSize,
                    planets[i].IsHomePlanet,
                    moons[i].CurrentShield,
                    attackerFriendlyToMoon: false);
                hitRadius += math.max(0.1f, mine.HitRadius);

                float3 moonPos = PlanetOrbitMath.GetMoonWorldPositionNear(
                    minePos,
                    transforms[i].Position,
                    planetSize,
                    planets[i].PlanetLevel,
                    planets[i].PlanetId,
                    moonElapsed,
                    mapW,
                    mapH);

                float dist = ToroidalMapEcs.ToroidalDistance(minePos, moonPos, mapW, mapH);
                if (dist <= hitRadius)
                {
                    contactPlanet = entities[i];
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Applies contact damage, splash + knockback, then broadcasts the explosion VFX.
        /// Timeout with no contact still blasts nearby enemies (full self-destruct).
        /// </summary>
        static void ExplodeMine(
            EntityManager em,
            ref EntityCommandBuffer ecb,
            in DeployedMineElement mine,
            Entity contactShip,
            Entity contactPlanet,
            Entity gemPrefab,
            double serverElapsed,
            float mapW,
            float mapH)
        {
            float3 hitPoint = mine.Position;
            hitPoint.y = 0f;
            var attackerTeam = (TeamId)mine.OwnerTeam;
            bool selfHarm = TitanOrbitDebugFlags.IsSelfHarmArmed(mine.PlaceTime, serverElapsed);

            // --- Contact ship (full center damage + push) ---
            if (contactShip != Entity.Null && em.HasComponent<ShipState>(contactShip))
            {
                var ship = em.GetComponentData<ShipState>(contactShip);
                float health = ship.Health;
                float gems = ship.CurrentGems;
                bool isDead = ship.IsDead;
                bool moonImmune = ShipMoonDockState.IsFullyLandedOnMoon(em, contactShip);
                // Team.None skips the same-team early-out so self-harm debug can hurt the owner.
                // 1:1 leftover damage → cargo so hull 0 + remaining gems stays alive.
                var damageTeam = selfHarm && ship.Team == attackerTeam ? TeamId.None : attackerTeam;
                var result = ShipDamageLogic.ApplyHullAndGemDamage(
                    ref health, ref gems, ref isDead,
                    CardEffectQuery.ScaleIncomingDamage(em, contactShip, mine.Damage),
                    ship.Team, damageTeam,
                    gemExpulsionPerHullDamage: ShipDamageLogic.ExcessDamageGemExpulsionPerHullDamage,
                    isImmune: moonImmune);
                ship.Health = health;
                ship.CurrentGems = gems;
                ship.IsDead = isDead;
                em.SetComponentData(contactShip, ship);

                if (result.AppliedHullDamage && em.HasComponent<ShipVitalsState>(contactShip))
                {
                    var vitals = em.GetComponentData<ShipVitalsState>(contactShip);
                    vitals.LastHullDamageTime = serverElapsed;
                    em.SetComponentData(contactShip, vitals);
                }

                if ((result.AppliedHullDamage || result.GemsToExpel > 0.0001f || result.BecameDead) &&
                    mine.OwnerNetworkId > 0)
                {
                    ShipMatchStatsLogic.SetLastDamager(
                        em, contactShip, mine.OwnerNetworkId, (float)serverElapsed);
                }

                if (result.GemsToExpel > 0.0001f &&
                    em.HasComponent<LocalTransform>(contactShip) &&
                    gemPrefab != Entity.Null)
                {
                    float3 shipPos = em.GetComponentData<LocalTransform>(contactShip).Position;
                    int sourceNetworkId = 0;
                    if (em.HasComponent<GhostOwner>(contactShip))
                        sourceNetworkId = em.GetComponentData<GhostOwner>(contactShip).NetworkId;
                    ShipGemExpulsion.SpawnFromDamage(
                        ecb,
                        gemPrefab,
                        shipPos,
                        result.GemsToExpel,
                        intensity: 0.5f,
                        salt: mine.Sequence ^ (uint)(serverElapsed * 1000.0),
                        (float)serverElapsed,
                        sourceNetworkId);
                }

                if (em.HasComponent<LocalTransform>(contactShip))
                {
                    float3 shipPos = em.GetComponentData<LocalTransform>(contactShip).Position;
                    BulletBankHitEffects.ApplyConcussivePushForce(
                        em, contactShip, hitPoint, shipPos, mine.BlastForce, mapW, mapH);
                }
            }

            // --- Contact moon (shield first, then moon gems) ---
            if (contactPlanet != Entity.Null &&
                em.HasComponent<PlanetGemMoonState>(contactPlanet) &&
                em.HasComponent<PlanetState>(contactPlanet))
            {
                var moon = em.GetComponentData<PlanetGemMoonState>(contactPlanet);
                var planet = em.GetComponentData<PlanetState>(contactPlanet);
                PlanetGemMoonCombatLogic.ApplyBulletDamage(
                    ref moon, mine.Damage, attackerTeam, planet.Ownership, serverElapsed);
                em.SetComponentData(contactPlanet, moon);
            }

            // --- Splash (skip the contact ship — it already took full damage) ---
            BulletBankHitEffects.TryApplyMineBlast(
                em,
                hitPoint,
                mine.BlastRadius,
                mine.BlastForce,
                mine.Damage,
                contactShip,
                mine.OwnerTeam,
                mine.OwnerNetworkId,
                serverElapsed,
                mapW,
                mapH,
                ecb,
                gemPrefab,
                allowOwnerHits: selfHarm);

            MineNetNotify.SendExplode(ref ecb, in mine);
        }
    }
}
