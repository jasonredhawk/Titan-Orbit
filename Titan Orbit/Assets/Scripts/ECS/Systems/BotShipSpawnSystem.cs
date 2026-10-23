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
    /// Spawns one AI hull per open team slot, the same way <see cref="PlayerShipSpawn.TrySpawn"/>
    /// spawns a player. A living bot is left alone. It is destroyed only when a human takes
    /// the slot, or when AI ships are turned off. Death sends it home in place — it is not
    /// replaced with a new ghost.
    /// Runs on the dedicated server (WebGL clients only render the ghosts).
    /// Map size is not required here — home placement comes from <see cref="ShipHomeSpawnLogic"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ShipRespawnSystem))]
    public partial struct BotShipSpawnSystem : ISystem
    {
        EntityQuery _bots;

        public void OnCreate(ref SystemState state)
        {
            _bots = state.GetEntityQuery(
                ComponentType.ReadOnly<BotShipTag>(),
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostOwner>(),
                ComponentType.ReadOnly<ShipState>());
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

            // Players spawn after the ghost collection exists. Bots must wait for that same prefab.
            bool collectionReady = PlayerShipSpawn.IsShipGhostCollectionReady(em);
            using var entities = _bots.ToEntityArray(Allocator.Temp);
            using var owners = _bots.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var ships = _bots.ToComponentDataArray<ShipState>(Allocator.Temp);

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
                    int found = FindSlot(owners, networkId);
                    bool keep = slot < keepCount;
                    if (!keep)
                    {
                        if (found >= 0 && em.Exists(entities[found]))
                            em.DestroyEntity(entities[found]);
                        continue;
                    }

                    if (found < 0)
                    {
                        if (collectionReady)
                            PlayerShipSpawn.TrySpawnBot(em, networkId, team, orbitElapsed, out _, out _);
                        continue;
                    }

                    Entity ship = entities[found];
                    if (!em.Exists(ship))
                        continue;
                    TryRespawn(em, ship, ships[found], team, now, orbitElapsed);
                }
            }
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
            state.IsDead = false;
            state.IsEliminated = false;
            state.OverdriveLockout = false;
            state.OverdriveBurstActive = false;
            state.CurrentEnergy = state.MaxEnergy;
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
