using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Spawns, despawns, and respawns AI ships while <see cref="TitanOrbitDebugFlags.AiShips"/> is on.
    /// The inspector count is a per-team ceiling. Humans already on that team take slots first,
    /// so a team of 12 with 11 configured bots keeps 11 bots beside one player and drops a bot
    /// when a second player joins. Leaving brings a bot back while the ceiling still has room.
    /// Runs on the dedicated server (WebGL clients only render the ghosts).
    /// Runs after death bookkeeping so a dead bot can be sent home once the respawn timer elapses.
    /// Map size is not required here — home placement comes from <see cref="ShipHomeSpawnLogic"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ShipRespawnSystem))]
    public partial struct BotShipSpawnSystem : ISystem
    {
        EntityQuery _bots;
        /// <summary>
        /// One bit per team slot. Set when that slot has a collection hull, or after the
        /// pre-collection copy was removed once. Stops a bad flag from spawning a new ship every tick.
        /// </summary>
        ulong _slotSettled;

        public void OnCreate(ref SystemState state)
        {
            // ShipState is optional — leftover husks often lost it and must still be found.
            _bots = state.GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostOwner>());
        }

        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            bool enabled = TitanOrbitDebugFlags.AiShips;
            if (!enabled && _bots.IsEmpty)
                return;

            if (!enabled)
            {
                DestroyAll(em);
                return;
            }

            int desired = TitanOrbitDebugFlags.AiShipsPerTeam;
            if (desired < 1)
                desired = 1;
            if (desired > BotShipIds.MaxPerTeam)
                desired = BotShipIds.MaxPerTeam;

            if (!SystemAPI.TryGetSingleton<TeamStateSingleton>(out var roster))
                return;
            int maxPerTeam = roster.MaxPlayersPerTeam > 0
                ? roster.MaxPlayersPerTeam
                : TitanOrbitDebugFlags.AiShipsPerTeamCap;

            float now = (float)SystemAPI.Time.ElapsedTime;
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double orbitElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : SystemAPI.Time.ElapsedTime;

            bool collectionReady = PlayerShipSpawn.IsShipGhostCollectionReady(em);
            using var entities = _bots.ToEntityArray(Allocator.Temp);
            using var owners = _bots.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            var matches = new NativeList<int>(8, Allocator.Temp);

            for (int teamIndex = 1; teamIndex <= BotShipIds.TeamCount; teamIndex++)
            {
                var team = (TeamId)teamIndex;
                if (roster.ActiveTeamCount > 0 && teamIndex > roster.ActiveTeamCount)
                    continue;
                if (!TeamHasHome(em, team))
                    continue;

                int humans = HumanCount(roster, team);
                int room = math.max(0, maxPerTeam - humans);
                int keepCount = math.min(desired, room);

                for (int slot = 0; slot < BotShipIds.MaxPerTeam; slot++)
                {
                    int networkId = BotShipIds.ForSlot(team, slot);
                    int bit = (teamIndex - 1) * BotShipIds.MaxPerTeam + slot;
                    ulong bitMask = 1UL << bit;
                    CollectSlot(owners, networkId, matches);
                    bool keep = slot < keepCount;
                    if (!keep)
                    {
                        DestroyMatches(em, entities, matches, keeper: -1);
                        continue;
                    }

                    int keeper = PickKeeper(em, entities, matches);
                    bool settled = (_slotSettled & bitMask) != 0;
                    // A hull spawned before the ghost collection existed has no client mesh.
                    // Remove that slot once, then the empty slot spawns a real ghost next tick.
                    if (!settled && collectionReady && keeper >= 0 && IsLegacyHull(em, entities[keeper]))
                    {
                        _slotSettled |= bitMask;
                        DestroyMatches(em, entities, matches, keeper: -1);
                        continue;
                    }

                    if (keeper < 0)
                    {
                        if (collectionReady
                            && PlayerShipSpawn.TrySpawnBot(em, networkId, team, orbitElapsed, out _, out _))
                            _slotSettled |= bitMask;
                        continue;
                    }

                    if (!IsLegacyHull(em, entities[keeper]))
                        _slotSettled |= bitMask;

                    DestroyMatches(em, entities, matches, keeper);
                    Entity ship = entities[keeper];
                    if (!em.Exists(ship) || !em.HasComponent<ShipState>(ship))
                        continue;
                    TryRespawn(em, ship, em.GetComponentData<ShipState>(ship), team, now, orbitElapsed);
                }
            }

            matches.Dispose();
        }

        static void CollectSlot(NativeArray<GhostOwner> owners, int networkId, NativeList<int> matches)
        {
            matches.Clear();
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId == networkId)
                    matches.Add(i);
            }
        }

        /// <summary>
        /// Prefers the hull that came from the ghost collection and already has a ghost id.
        /// A linked child or an early empty copy scores lower and is deleted.
        /// </summary>
        static int PickKeeper(EntityManager em, NativeArray<Entity> entities, NativeList<int> matches)
        {
            int keeper = -1;
            int bestScore = -1;
            int bestIndex = -1;
            for (int i = 0; i < matches.Length; i++)
            {
                int index = matches[i];
                Entity entity = entities[index];
                if (!em.Exists(entity))
                    continue;
                int score = KeeperScore(em, entity);
                int entityIndex = entity.Index;
                if (score > bestScore || (score == bestScore && entityIndex > bestIndex))
                {
                    bestScore = score;
                    bestIndex = entityIndex;
                    keeper = index;
                }
            }

            return keeper;
        }

        static int KeeperScore(EntityManager em, Entity entity)
        {
            int score = 0;
            if (em.HasComponent<ShipState>(entity))
                score += 4;
            if (em.HasComponent<BotShipTag>(entity)
                && em.GetComponentData<BotShipTag>(entity).FromGhostCollection != 0)
                score += 8;
            if (em.HasComponent<GhostInstance>(entity)
                && em.GetComponentData<GhostInstance>(entity).ghostId != 0)
                score += 2;
            return score;
        }

        static bool IsLegacyHull(EntityManager em, Entity entity)
        {
            if (!em.Exists(entity) || !em.HasComponent<BotShipTag>(entity))
                return true;
            return em.GetComponentData<BotShipTag>(entity).FromGhostCollection == 0;
        }

        /// <summary>
        /// Destroys every match except <paramref name="keeper"/>. Pass -1 to destroy the whole slot.
        /// Linked children are included so an empty ghost child is not left in the world.
        /// </summary>
        static void DestroyMatches(
            EntityManager em,
            NativeArray<Entity> entities,
            NativeList<int> matches,
            int keeper)
        {
            Entity keeperEntity = keeper >= 0 ? entities[keeper] : Entity.Null;
            var roots = new NativeList<Entity>(matches.Length, Allocator.Temp);
            for (int i = 0; i < matches.Length; i++)
            {
                int index = matches[i];
                if (index == keeper)
                    continue;
                Entity entity = entities[index];
                if (!em.Exists(entity))
                    continue;
                if (keeperEntity != Entity.Null && InLinkedGroup(em, keeperEntity, entity))
                    continue;
                roots.Add(entity);
            }

            DestroyGroups(em, roots);
            roots.Dispose();
        }

        static int HumanCount(in TeamStateSingleton team, TeamId id)
        {
            switch (id)
            {
                case TeamId.TeamA: return math.max(0, team.TeamACount);
                case TeamId.TeamB: return math.max(0, team.TeamBCount);
                case TeamId.TeamC: return math.max(0, team.TeamCCount);
                case TeamId.TeamD: return math.max(0, team.TeamDCount);
                case TeamId.TeamE: return math.max(0, team.TeamECount);
                default: return 0;
            }
        }

        static int FindSlot(NativeArray<GhostOwner> owners, int networkId)
        {
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId == networkId)
                    return i;
            }

            return -1;
        }

        static bool TeamHasHome(EntityManager em, TeamId team)
        {
            using var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>());
            using var planets = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            for (int i = 0; i < planets.Length; i++)
            {
                if (planets[i].IsHomePlanet && planets[i].Ownership == team)
                    return true;
            }

            return false;
        }

        static void DestroyAll(EntityManager em)
        {
            using var query = em.CreateEntityQuery(ComponentType.ReadOnly<BotShipTag>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                if (em.Exists(entities[i]))
                    em.DestroyEntity(entities[i]);
            }
        }

        /// <summary>
        /// Sends a dead bot home after the same delay players wait, without a respawn RPC.
        /// Eliminated hulls (team owns no planets) stay dead.
        /// </summary>
        static void TryRespawn(
            EntityManager em,
            Entity ship,
            in ShipState shipState,
            TeamId team,
            float now,
            double orbitElapsed)
        {
            if (!shipState.IsDead || shipState.IsEliminated)
                return;
            if (!em.HasComponent<ShipDeathState>(ship))
                return;
            if (now < em.GetComponentData<ShipDeathState>(ship).RespawnAtTime)
                return;
            if (!ShipHomeSpawnLogic.TryFindHomeSpawnPosition(em, team, orbitElapsed, out float3 spawnPos))
                return;

            if (em.HasComponent<MegaShipState>(ship)
                && em.GetComponentData<MegaShipState>(ship).IsMega)
            {
                MegaShipStatApplyLogic.RestorePreviousHull(em, ship);
            }

            var state = em.GetComponentData<ShipState>(ship);
            state.Health = state.MaxHealth;
            state.CurrentGems = 0f;
            state.CurrentPeople = 0;
            state.CurrentEnergy = state.MaxEnergy;
            state.IsDead = false;
            state.IsEliminated = false;
            state.OverdriveLockout = false;
            state.OverdriveBurstActive = false;
            em.SetComponentData(ship, state);

            if (em.HasComponent<LocalTransform>(ship))
            {
                var transform = em.GetComponentData<LocalTransform>(ship);
                transform.Position = spawnPos;
                transform.Rotation = quaternion.identity;
                em.SetComponentData(ship, transform);
            }

            if (em.HasComponent<ShipKinematics>(ship))
            {
                var kinematics = em.GetComponentData<ShipKinematics>(ship);
                kinematics.Velocity = float3.zero;
                kinematics.FormationHeading = float3.zero;
                em.SetComponentData(ship, kinematics);
            }

            if (em.HasComponent<PhysicsVelocity>(ship))
                em.SetComponentData(ship, PhysicsVelocity.Zero);

            if (em.HasComponent<ShipOrbitState>(ship))
            {
                var orbit = em.GetComponentData<ShipOrbitState>(ship);
                orbit.OrbitPlanetId = 0;
                orbit.InOrbitRing = false;
                orbit.UsingOrbitMotor = false;
                orbit.OrbitLocked = false;
                orbit.IsTransferringPeople = false;
                em.SetComponentData(ship, orbit);
            }

            if (em.HasComponent<ShipMoonDockState>(ship))
                em.SetComponentData(ship, new ShipMoonDockState());

            if (em.HasComponent<ShipDepositIntent>(ship))
                em.SetComponentData(ship, new ShipDepositIntent());

            if (em.HasComponent<ShipTerritoryBoostLatch>(ship))
            {
                var latch = em.GetComponentData<ShipTerritoryBoostLatch>(ship);
                ShipPhysicsDriveLogic.ClearTerritoryBoostLatch(ref latch);
                em.SetComponentData(ship, latch);
            }

            if (em.HasComponent<ShipInput>(ship))
                em.SetComponentData(ship, new ShipInput());

            if (em.HasComponent<BotShipBrain>(ship))
                em.SetComponentData(ship, new BotShipBrain());

            PeopleTransportEscortLogic.ClearAll(em, ship);

            if (em.HasComponent<ShipDeathVfxState>(ship))
                em.SetComponentData(ship, new ShipDeathVfxState());

            em.RemoveComponent<ShipDeathState>(ship);
        }
    }
}
