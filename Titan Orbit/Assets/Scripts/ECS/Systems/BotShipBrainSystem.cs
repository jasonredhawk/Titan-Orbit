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
    /// Server brain for AI ships. Decides a task about twice a second, then writes
    /// <see cref="ShipInput"/> every tick so the existing motor, guns, moon dock,
    /// deposit, and troop systems do the work.
    /// <para>
    /// Runs inside the predicted fixed step, after NetCode copies the command buffer
    /// into <see cref="ShipInput"/> and before <see cref="ShipPhysicsDriveSystem"/>.
    /// That copy is OrderFirst and writes the latest buffer sample (default / no thrust
    /// when nobody owns the ghost). A system that is not itself OrderFirst cannot
    /// reliably sit between that copy and the drive, so the jets would show thrust
    /// that the motor never applied.
    /// </para>
    /// Map width and height come from <see cref="MapStateSingleton"/> via
    /// <see cref="ToroidalMapEcs.TryGetMapSize"/>. Missing size skips the tick.
    /// Asteroids are indexed on decision ticks into the same 16-unit grid bullets use
    /// (<see cref="BulletObstacleSpatialHash"/>). Steering each tick only reads the
    /// cells around that hull. Ship and planet lists are small and refreshed every tick.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(ShipPhysicsMassSyncSystem))]
    [UpdateBefore(typeof(ShipPhysicsDriveSystem))]
    public partial struct BotShipBrainSystem : ISystem
    {
        const float DecisionInterval = 0.5f;
        /// <summary>
        /// Only peel off to fight, chase, or shoot inside this distance.
        /// Farther enemies are ignored so a miner does not cross the map.
        /// </summary>
        const float EngageRange = 14f;
        const float DepositFill = 0.75f;
        const float FollowDistance = 8f;
        const float HoldDistance = 6f;
        const int MaxShipLevel = 6;
        const float DockShipRadius = 0.8f;
        const float DockZoneMultiplier = 1.05f;
        /// <summary>Gap past the drawn rock so the hull sits close without touching it.</summary>
        const float MineSurfaceGap = 1.75f;
        /// <summary>Open fire once the rock surface is within this distance. Bullet range is ~30.</summary>
        const float MineShotReach = 20f;
        /// <summary>Hull pickup is about 2.5. Coast onto the crystal inside this distance.</summary>
        const float CollectArrive = 1.6f;

        struct PlanetSnap
        {
            public Entity Entity;
            public int PlanetId;
            public TeamId Ownership;
            public float3 Position;
            public float Scale;
            public int Level;
            public int Population;
            public int HalfPopulation;
            public byte IsHome;
        }

        struct ShipSnap
        {
            public int NetworkId;
            public TeamId Team;
            public float3 Position;
            public byte Dead;
            public byte Landed;
        }

        struct RockSnap
        {
            public float3 Position;
            public float Radius;
        }

        EntityQuery _bots;
        EntityQuery _planets;
        EntityQuery _ships;
        EntityQuery _rocks;
        EntityQuery _gems;
        /// <summary>Gem-bearing rocks for mine-target choice. Not used for steering.</summary>
        NativeList<RockSnap> _mineRocks;
        /// <summary>Nearby-cell indices filled by <see cref="BulletObstacleSpatialHash.GatherNearby"/>.</summary>
        NativeList<int> _nearbyRocks;
        NativeHashSet<int> _nearbySeen;
        BulletObstacleSpatialHash _asteroidHash;
        /// <summary>Loose gems, rebuilt on decision ticks only. Steering does not read it.</summary>
        GemSpatialHash _gemHash;

        public void OnCreate(ref SystemState state)
        {
            _bots = state.GetEntityQuery(
                ComponentType.ReadOnly<BotShipTag>(),
                ComponentType.ReadOnly<ShipTag>());
            _planets = state.GetEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>());
            _ships = state.GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GhostOwner>());
            _rocks = state.GetEntityQuery(
                ComponentType.ReadOnly<AsteroidTag>(),
                ComponentType.ReadOnly<AsteroidState>(),
                ComponentType.ReadOnly<LocalTransform>());
            _gems = state.GetEntityQuery(
                ComponentType.ReadOnly<GemTag>(),
                ComponentType.ReadOnly<GemState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GemMotionState>());
            state.RequireForUpdate(_bots);
            _mineRocks = new NativeList<RockSnap>(64, Allocator.Persistent);
            _nearbyRocks = new NativeList<int>(32, Allocator.Persistent);
            _nearbySeen = new NativeHashSet<int>(32, Allocator.Persistent);
            BotShipOrderLogic.EnsureSingleton(state.EntityManager);
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_asteroidHash.IsCreated)
                _asteroidHash.Dispose();
            if (_gemHash.IsCreated)
                _gemHash.Dispose();
            if (_mineRocks.IsCreated)
                _mineRocks.Dispose();
            if (_nearbyRocks.IsCreated)
                _nearbyRocks.Dispose();
            if (_nearbySeen.IsCreated)
                _nearbySeen.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return;

            float now = (float)SystemAPI.Time.ElapsedTime;
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double orbitElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : SystemAPI.Time.ElapsedTime;

            var em = state.EntityManager;
            BotTeamOrders orders = default;
            bool haveOrders = false;
            if (SystemAPI.TryGetSingleton<BotTeamOrders>(out var orderSingleton))
            {
                orders = orderSingleton;
                haveOrders = true;
            }

            using var bots = _bots.ToEntityArray(Allocator.Temp);
            // No human hull in the match: keep the bots spawned, but do not mine, haul, or fight.
            if (!MatchHasHuman())
            {
                ParkIdle(em, bots);
                return;
            }

            bool anyDecide = false;
            for (int i = 0; i < bots.Length; i++)
            {
                if (!em.HasComponent<BotShipBrain>(bots[i]) || !em.HasComponent<ShipState>(bots[i]))
                    continue;
                if (em.GetComponentData<ShipState>(bots[i]).IsDead)
                    continue;
                var earlyBrain = em.GetComponentData<BotShipBrain>(bots[i]);
                if (earlyBrain.NextDecisionTime <= now)
                {
                    anyDecide = true;
                    break;
                }

                if (!haveOrders || !em.HasComponent<GhostOwner>(bots[i]))
                    continue;
                var earlyShip = em.GetComponentData<ShipState>(bots[i]);
                int earlyId = em.GetComponentData<GhostOwner>(bots[i]).NetworkId;
                BotTeamOrder earlyOrder = orders.Get(earlyShip.Team);
                if (earlyOrder.IsActive(now) && earlyOrder.Includes(earlyId) &&
                    OrderNeedsRetask(earlyOrder, earlyBrain))
                {
                    anyDecide = true;
                    break;
                }
            }

            using var planets = GatherPlanets(em);
            using var ships = GatherShips(em);
            if (anyDecide)
            {
                RefreshAsteroidHash(mapW, mapH);
                RefreshGemHash(mapW, mapH);
            }
            NativeArray<RockSnap> rocks = _mineRocks.AsArray();
            using var commanderShips = _ships.ToEntityArray(Allocator.Temp);
            using var commanderOwners = _ships.ToComponentDataArray<GhostOwner>(Allocator.Temp);

            for (int i = 0; i < bots.Length; i++)
            {
                Entity shipEntity = bots[i];
                if (!em.Exists(shipEntity) ||
                    !em.HasComponent<ShipState>(shipEntity) ||
                    !em.HasComponent<LocalTransform>(shipEntity) ||
                    !em.HasComponent<GhostOwner>(shipEntity) ||
                    !em.HasComponent<ShipInput>(shipEntity) ||
                    !em.HasComponent<BotShipBrain>(shipEntity))
                    continue;

                var ship = em.GetComponentData<ShipState>(shipEntity);
                var brain = em.GetComponentData<BotShipBrain>(shipEntity);
                if (ship.IsDead || ship.AwaitingTeamSelection || ship.Team == TeamId.None)
                {
                    em.SetComponentData(shipEntity, new ShipInput());
                    ClearDeposit(em, shipEntity);
                    int idleId = em.GetComponentData<GhostOwner>(shipEntity).NetworkId;
                    BotTeamOrder idleOrder = haveOrders ? orders.Get(ship.Team) : default;
                    BotShipPresentation.ApplyCommanderSkin(
                        em, shipEntity, idleId, idleOrder, now, commanderShips, commanderOwners);
                    continue;
                }

                // Drive job is WithAll<Simulate>. Unowned ghosts can sit with it disabled,
                // which leaves ShipInput.Thrust true for the jets and zero velocity on the hull.
                if (em.HasComponent<Simulate>(shipEntity) && !em.IsComponentEnabled<Simulate>(shipEntity))
                    em.SetComponentEnabled<Simulate>(shipEntity, true);

                float3 pos = em.GetComponentData<LocalTransform>(shipEntity).Position;
                int networkId = em.GetComponentData<GhostOwner>(shipEntity).NetworkId;
                bool landed = false;
                bool docking = false;
                int dockedPlanetId = 0;
                if (em.HasComponent<ShipMoonDockState>(shipEntity))
                {
                    var dock = em.GetComponentData<ShipMoonDockState>(shipEntity);
                    landed = dock.IsFullyLanded;
                    docking = dock.MoonPlanetId != 0 && !dock.IsTakingOff;
                    if (landed)
                        dockedPlanetId = dock.MoonPlanetId;
                }
                if (!landed)
                    brain.AttributeBoughtWhileLanded = 0;

                bool inOrbit = em.HasComponent<ShipOrbitState>(shipEntity) &&
                               em.GetComponentData<ShipOrbitState>(shipEntity).InOrbitRing;

                if (brain.GoalNetworkId != 0 &&
                    TryShipPosition(ships.AsArray(), brain.GoalNetworkId, out float3 tracked))
                    brain.Goal = tracked;

                BotTeamOrder liveOrder = haveOrders ? orders.Get(ship.Team) : default;
                bool orderNow = liveOrder.IsActive(now) && liveOrder.Includes(networkId)
                                && OrderNeedsRetask(liveOrder, brain);
                if (brain.NextDecisionTime <= now || orderNow)
                {
                    BotTeamOrder order = liveOrder.IsActive(now) && liveOrder.Includes(networkId)
                        ? liveOrder
                        : default;
                    Choose(
                        ref brain, ship, networkId, pos, landed, dockedPlanetId, order,
                        planets.AsArray(), ships.AsArray(),
                        rocks,
                        in _gemHash, _nearbyRocks, _nearbySeen,
                        mapW, mapH, em, now);
                    if (landed && (brain.Task == BotTaskKind.Deposit || brain.Task == BotTaskKind.Upgrade))
                        TryBuy(em, shipEntity, ref ship, ref brain);
                    if (!orderNow)
                        brain.NextDecisionTime = now + DecisionInterval;
                }

                var input = BuildInput(
                    brain, ship, networkId, pos, landed, docking, inOrbit,
                    planets.AsArray(), ships.AsArray(),
                    mapW, mapH, orbitElapsed);
                BotShipPresentation.ApplyCommanderSkin(
                    em, shipEntity, networkId, liveOrder, now, commanderShips, commanderOwners);
                em.SetComponentData(shipEntity, input);
                em.SetComponentData(shipEntity, brain);

                bool wantDeposit = input.WantDepositGems;
                if (em.HasComponent<ShipDepositIntent>(shipEntity))
                    em.SetComponentData(shipEntity, new ShipDepositIntent { WantDepositGems = wantDeposit });
                else if (wantDeposit)
                    em.AddComponentData(shipEntity, new ShipDepositIntent { WantDepositGems = true });
            }
        }

        static void ClearDeposit(EntityManager em, Entity ship)
        {
            if (em.HasComponent<ShipDepositIntent>(ship))
                em.SetComponentData(ship, new ShipDepositIntent());
        }

        /// <summary>
        /// True when a real player already has a team ship. Dead humans still count —
        /// they are in the match. Bots and hulls waiting on Join Team do not.
        /// </summary>
        bool MatchHasHuman()
        {
            using var owners = _ships.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var states = _ships.ToComponentDataArray<ShipState>(Allocator.Temp);
            int count = math.min(owners.Length, states.Length);
            for (int i = 0; i < count; i++)
            {
                if (BotShipIds.IsBot(owners[i].NetworkId))
                    continue;
                if (states[i].Team == TeamId.None || states[i].AwaitingTeamSelection)
                    continue;
                return true;
            }

            return false;
        }

        /// <summary>Zeros thrust and drops a copied commander hull. The entity stays in the world.</summary>
        static void ParkIdle(EntityManager em, NativeArray<Entity> bots)
        {
            for (int i = 0; i < bots.Length; i++)
            {
                Entity ship = bots[i];
                if (!em.Exists(ship))
                    continue;

                if (em.HasComponent<ShipInput>(ship))
                    em.SetComponentData(ship, new ShipInput());

                if (em.HasComponent<BotShipBrain>(ship))
                {
                    BotShipBrain brain = em.GetComponentData<BotShipBrain>(ship);
                    if (brain.Task != BotTaskKind.None || brain.GoalNetworkId != 0 || brain.GoalPlanetId != 0)
                        em.SetComponentData(ship, new BotShipBrain());
                }

                ClearDeposit(em, ship);
                BotShipPresentation.ClearCommanderSkin(em, ship);
            }
        }

        static void Choose(
            ref BotShipBrain brain,
            in ShipState ship,
            int networkId,
            float3 pos,
            bool landed,
            int dockedPlanetId,
            in BotTeamOrder order,
            NativeArray<PlanetSnap> planets,
            NativeArray<ShipSnap> ships,
            NativeArray<RockSnap> rocks,
            in GemSpatialHash gems,
            NativeList<int> nearby,
            NativeHashSet<int> seen,
            float mapW,
            float mapH,
            EntityManager em,
            float now)
        {
            // Cargo leaves only through GemDepositSystem: one shipLevel × planetLevel
            // chunk per beat, same as a player holding deposit. Stay on this moon until
            // the hold is empty so an order or a nearby enemy cannot cut the metronome.
            if (landed && ship.CurrentGems > 0.05f &&
                dockedPlanetId != 0 &&
                TryPlanet(planets, dockedPlanetId, out PlanetSnap docked) &&
                docked.Ownership == ship.Team)
            {
                SetPlanet(ref brain, BotTaskKind.Deposit, docked);
                NoteTask(ref brain, ship.CurrentPeople, now);
                return;
            }

            if (order.Task != BotTaskKind.None && order.Includes(networkId))
            {
                ApplyOrder(
                    ref brain, ship, networkId, pos, order, planets, ships, rocks,
                    gems, nearby, seen, mapW, mapH);
                NoteTask(ref brain, ship.CurrentPeople, now);
                return;
            }

            bool nearFull = ship.GemCapacity > 1f && ship.CurrentGems >= ship.GemCapacity * DepositFill;
            if (nearFull && TryNearestFriendly(planets, pos, ship.Team, mapW, mapH, out PlanetSnap depositMoon))
            {
                SetPlanet(ref brain, BotTaskKind.Deposit, depositMoon);
                NoteTask(ref brain, ship.CurrentPeople, now);
                return;
            }

            bool troopHoldFull = ship.PeopleCapacity > 0 && ship.CurrentPeople >= ship.PeopleCapacity;
            if (troopHoldFull &&
                TryNearestHostile(planets, pos, ship.Team, mapW, mapH, out PlanetSnap dump))
            {
                SetPlanet(ref brain, BotTaskKind.UnloadTroops, dump);
                NoteTask(ref brain, ship.CurrentPeople, now);
                return;
            }

            // Stay on the same rock until it is out of gems. Re-picking the nearest
            // rock every decision makes a dodge inside a cluster look like a new target.
            if (TryKeepMineRock(ref brain, rocks, mapW, mapH))
            {
                NoteTask(ref brain, ship.CurrentPeople, now);
                return;
            }

            if (TryCollectBurst(ref brain, ship, networkId, pos, gems, nearby, seen, mapW, mapH))
            {
                NoteTask(ref brain, ship.CurrentPeople, now);
                return;
            }

            if (TryNearestRock(rocks, pos, networkId, mapW, mapH, out RockSnap rock))
            {
                SetMine(ref brain, rock);
                NoteTask(ref brain, ship.CurrentPeople, now);
                return;
            }

            // Combat is last. A nearby enemy does not pull a ship off mining, hauling, or deposit.
            if (TryNearestEnemy(ships, pos, ship.Team, networkId, EngageRange, mapW, mapH, out ShipSnap enemy))
            {
                SetAttack(ref brain, enemy);
                NoteTask(ref brain, ship.CurrentPeople, now);
                return;
            }

            if (TryHome(planets, ship.Team, out PlanetSnap home))
                SetPlanet(ref brain, BotTaskKind.Hold, home);
            NoteTask(ref brain, ship.CurrentPeople, now);
        }

        static void NoteTask(ref BotShipBrain brain, int people, float now)
        {
            if (brain.TaskClock <= 0f || people != brain.PeopleSnapshot)
            {
                brain.TaskClock = now;
                brain.PeopleSnapshot = people;
            }
        }

        static void ApplyOrder(
            ref BotShipBrain brain,
            in ShipState ship,
            int networkId,
            float3 pos,
            in BotTeamOrder order,
            NativeArray<PlanetSnap> planets,
            NativeArray<ShipSnap> ships,
            NativeArray<RockSnap> rocks,
            in GemSpatialHash gems,
            NativeList<int> nearby,
            NativeHashSet<int> seen,
            float mapW,
            float mapH)
        {
            float3 ping = order.HasWaypoint != 0 ? order.Waypoint : pos;
            switch (order.Task)
            {
                case BotTaskKind.Mine:
                    if (TryKeepMineRock(ref brain, rocks, mapW, mapH))
                        return;
                    if (TryCollectBurst(ref brain, ship, networkId, pos, gems, nearby, seen, mapW, mapH))
                        return;
                    if (TryNearestRock(rocks, pos, networkId, mapW, mapH, out RockSnap rock))
                    {
                        SetMine(ref brain, rock);
                    }
                    else
                    {
                        brain.Task = BotTaskKind.Advance;
                        brain.Goal = ping;
                        brain.GoalPlanetId = 0;
                        brain.GoalNetworkId = 0;
                    }
                    return;
                case BotTaskKind.Deposit:
                    if (TryNearestFriendly(planets, pos, ship.Team, mapW, mapH, out PlanetSnap moon))
                        SetPlanet(ref brain, BotTaskKind.Deposit, moon);
                    return;
                case BotTaskKind.Transport:
                    if (ship.CurrentPeople > 0 &&
                        TryNearestHostile(planets, pos, ship.Team, mapW, mapH, out PlanetSnap dump))
                        SetPlanet(ref brain, BotTaskKind.UnloadTroops, dump);
                    else if (TryFriendlySurplus(planets, pos, ship.Team, mapW, mapH, out PlanetSnap source))
                        SetPlanet(ref brain, BotTaskKind.LoadTroops, source);
                    else if (TryNearestFriendly(planets, pos, ship.Team, mapW, mapH, out PlanetSnap wait))
                        SetPlanet(ref brain, BotTaskKind.LoadTroops, wait);
                    return;
                case BotTaskKind.Attack:
                    if (order.SubjectNetworkId > 0 &&
                        TryShip(ships, order.SubjectNetworkId, out ShipSnap marked) &&
                        marked.Dead == 0 &&
                        marked.Team != ship.Team &&
                        marked.Team != TeamId.None &&
                        ToroidalMapEcs.ToroidalDistance(pos, marked.Position, mapW, mapH) <= EngageRange)
                    {
                        SetAttack(ref brain, marked);
                        return;
                    }

                    if (TryNearestEnemy(ships, pos, ship.Team, networkId, EngageRange, mapW, mapH, out ShipSnap enemy))
                    {
                        SetAttack(ref brain, enemy);
                        return;
                    }

                    brain.Task = BotTaskKind.Attack;
                    brain.Goal = ping;
                    brain.GoalPlanetId = order.PlanetId;
                    brain.GoalNetworkId = 0;
                    return;
                case BotTaskKind.Defend:
                    if (order.PlanetId != 0 && TryPlanet(planets, order.PlanetId, out PlanetSnap planet))
                        SetPlanet(ref brain, BotTaskKind.Defend, planet);
                    else
                    {
                        brain.Task = BotTaskKind.Defend;
                        brain.Goal = ping;
                        brain.GoalPlanetId = 0;
                        brain.GoalNetworkId = 0;
                    }
                    return;
                case BotTaskKind.Follow:
                    if (order.SubjectNetworkId > 0 &&
                        TryShip(ships, order.SubjectNetworkId, out ShipSnap lead) &&
                        lead.Dead == 0 &&
                        lead.Team == ship.Team)
                    {
                        brain.Task = BotTaskKind.Follow;
                        brain.Goal = lead.Position;
                        brain.GoalNetworkId = lead.NetworkId;
                        brain.GoalPlanetId = 0;
                        return;
                    }

                    brain.Task = BotTaskKind.Hold;
                    brain.Goal = ping;
                    brain.GoalNetworkId = 0;
                    brain.GoalPlanetId = 0;
                    return;
                case BotTaskKind.Advance:
                    brain.Task = BotTaskKind.Advance;
                    brain.Goal = ping;
                    brain.GoalPlanetId = order.PlanetId;
                    brain.GoalNetworkId = 0;
                    return;
                default:
                    brain.Task = BotTaskKind.Hold;
                    brain.Goal = ping;
                    brain.GoalPlanetId = 0;
                    brain.GoalNetworkId = 0;
                    return;
            }
        }

        static void TryBuy(EntityManager em, Entity shipEntity, ref ShipState ship, ref BotShipBrain brain)
        {
            if (!em.HasComponent<ShipMoonDockState>(shipEntity))
                return;
            int storePlanetId = em.GetComponentData<ShipMoonDockState>(shipEntity).MoonPlanetId;
            if (storePlanetId == 0)
                return;

            int nextLevel = ship.ShipLevel + 1;
            int branch = ship.BranchIndex;
            if (nextLevel <= MaxShipLevel &&
                UpgradeTree.IsValidUpgradeStep(ship.ShipLevel, branch, nextLevel, branch))
            {
                if (MoonOrbitStoreSystem.TryPurchaseShipUpgradeForNetworkId(
                        em,
                        em.GetComponentData<GhostOwner>(shipEntity).NetworkId,
                        storePlanetId,
                        nextLevel,
                        branch,
                        out _))
                {
                    ship = em.GetComponentData<ShipState>(shipEntity);
                    brain.AttributeBoughtWhileLanded = 0;
                }
            }

            if (brain.AttributeBoughtWhileLanded != 0)
                return;
            // Attribute buys spend cargo in one lump. Leave the hold for the deposit beat.
            if (ship.CurrentGems > 0.05f)
                return;
            if (!em.HasComponent<ShipAttributeUpgradeState>(shipEntity))
                return;

            var attrs = em.GetComponentData<ShipAttributeUpgradeState>(shipEntity);
            if (!TryPickAttribute(attrs, ship.ShipLevel, ship.CurrentGems, out int attribute))
                return;

            int networkId = em.GetComponentData<GhostOwner>(shipEntity).NetworkId;
            if (!ShipAttributeUpgradeLogic.TryPurchaseForNetworkId(em, networkId, attribute, out _))
                return;

            brain.AttributeBoughtWhileLanded = 1;
            ship = em.GetComponentData<ShipState>(shipEntity);
        }

        static bool TryPickAttribute(in ShipAttributeUpgradeState attrs, int shipLevel, float gems, out int index)
        {
            index = -1;
            int max = ShipAttributeUpgradeLogic.GetMaxUpgrades(shipLevel);
            int cost = ShipAttributeUpgradeLogic.GetUpgradeCost(shipLevel);
            if (gems < cost - 0.01f)
                return false;

            for (int i = 0; i < 10; i++)
            {
                if (ShipAttributeUpgradeLogic.GetAttributeLevel(attrs, i) < max)
                {
                    index = i;
                    return true;
                }
            }

            return false;
        }

        static ShipInput BuildInput(
            in BotShipBrain brain,
            in ShipState ship,
            int networkId,
            float3 pos,
            bool landed,
            bool docking,
            bool inOrbit,
            NativeArray<PlanetSnap> planets,
            NativeArray<ShipSnap> ships,
            float mapW,
            float mapH,
            double orbitElapsed)
        {
            // Heading and thrust are separate, same as a player. AimPlanarDir is the
            // direction to face. ShipPhysicsDriveLogic turns the hull toward it at the
            // ship's rotation speed, then thrust accelerates along that facing.
            var input = new ShipInput();
            float3 aim = brain.Goal;
            bool thrust = true;
            bool releaseThrust = false;
            bool fire = false;
            bool deposit = false;

            switch (brain.Task)
            {
                case BotTaskKind.Deposit:
                case BotTaskKind.Upgrade:
                    if (TryPlanet(planets, brain.GoalPlanetId, out PlanetSnap moonPlanet))
                    {
                        aim = MoonPosition(pos, moonPlanet, orbitElapsed, mapW, mapH);
                        float dockRadius = PlanetGemMoonMath.GetMoonDockZoneRadiusWorld(
                            moonPlanet.Scale, moonPlanet.IsHome != 0, DockShipRadius, DockZoneMultiplier);
                        float dist = ToroidalMapEcs.ToroidalDistance(pos, aim, mapW, mapH);
                        bool inside = dist <= dockRadius;
                        deposit = landed && ship.CurrentGems > 0.05f;
                        // Same as letting go of the stick. Thrust inside the dock zone
                        // cancels landing, and the hull sinks into the moon mesh.
                        if (landed || docking || inside)
                        {
                            thrust = false;
                            releaseThrust = true;
                        }
                        else
                            thrust = true;
                    }
                    break;
                case BotTaskKind.LoadTroops:
                case BotTaskKind.UnloadTroops:
                    if (TryPlanet(planets, brain.GoalPlanetId, out PlanetSnap orbitPlanet))
                    {
                        aim = RingPoint(pos, orbitPlanet, mapW, mapH);
                        bool atGoalRing = inOrbit && brain.GoalPlanetId == orbitPlanet.PlanetId;
                        bool stayForTransfer = atGoalRing &&
                            ((brain.Task == BotTaskKind.LoadTroops && ship.CurrentPeople < ship.PeopleCapacity) ||
                             (brain.Task == BotTaskKind.UnloadTroops && ship.CurrentPeople > 0));
                        thrust = !stayForTransfer;
                        releaseThrust = stayForTransfer;
                    }
                    break;
                case BotTaskKind.Mine:
                {
                    // Face the rock. Thrust is only on or off — the server turns the hull.
                    aim = brain.Goal;
                    float stop = math.max(2f, brain.GoalRadius);
                    float dist = ToroidalMapEcs.ToroidalDistance(pos, aim, mapW, mapH);
                    thrust = dist > stop;
                    break;
                }
                case BotTaskKind.Collect:
                    aim = brain.Goal;
                    {
                        float dist = ToroidalMapEcs.ToroidalDistance(pos, aim, mapW, mapH);
                        thrust = dist <= EngageRange && dist > CollectArrive;
                    }
                    break;
                case BotTaskKind.Attack:
                    aim = brain.Goal;
                    {
                        float dist = ToroidalMapEcs.ToroidalDistance(pos, aim, mapW, mapH);
                        bool trackingEnemy = brain.GoalNetworkId != 0;
                        bool close = dist <= EngageRange;
                        // A runner who leaves close range is dropped. A waypoint with no
                        // hull is still flown to; guns stay off until an enemy is close.
                        thrust = trackingEnemy && !close ? false : inOrbit || dist > 12f;
                        fire = trackingEnemy && close && !inOrbit;
                    }
                    break;
                case BotTaskKind.Defend:
                    if (brain.GoalPlanetId != 0 && TryPlanet(planets, brain.GoalPlanetId, out PlanetSnap holdPlanet))
                        aim = HoldOutsideRing(pos, holdPlanet, mapW, mapH);
                    else
                        aim = brain.Goal;
                    {
                        float dist = ToroidalMapEcs.ToroidalDistance(pos, aim, mapW, mapH);
                        thrust = dist > HoldDistance;
                        if (TryNearestEnemy(ships, pos, ship.Team, networkId, EngageRange, mapW, mapH, out ShipSnap hostile))
                        {
                            aim = hostile.Position;
                            fire = !inOrbit;
                            if (inOrbit)
                                thrust = true;
                        }
                    }
                    break;
                case BotTaskKind.Follow:
                    aim = brain.Goal;
                    thrust = ToroidalMapEcs.ToroidalDistance(pos, aim, mapW, mapH) > FollowDistance;
                    break;
                default:
                    if (brain.GoalPlanetId != 0 && TryPlanet(planets, brain.GoalPlanetId, out PlanetSnap holdHome))
                        aim = HoldOutsideRing(pos, holdHome, mapW, mapH);
                    else
                        aim = brain.Goal;
                    thrust = inOrbit || ToroidalMapEcs.ToroidalDistance(pos, aim, mapW, mapH) > HoldDistance;
                    break;
            }

            // Releasing thrust inside the orbit annulus hands the hull to the passive
            // orbit motor, which keeps it on the ring facing the goal.
            // A landed ship with cargo must keep thrust off every tick. One thrust
            // tick undocks and resets the deposit metronome.
            bool unloadCargo = landed && ship.CurrentGems > 0.05f;
            if (unloadCargo)
            {
                thrust = false;
                releaseThrust = true;
                deposit = true;
            }
            else if (docking && (brain.Task == BotTaskKind.Deposit || brain.Task == BotTaskKind.Upgrade))
            {
                thrust = false;
                releaseThrust = true;
            }
            else if (inOrbit && !releaseThrust)
                thrust = true;
            else if (landed && brain.Task != BotTaskKind.Deposit && brain.Task != BotTaskKind.Upgrade)
                thrust = true;

            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(pos, aim, mapW, mapH);
            float len = math.length(new float2(offset.x, offset.z));
            if (len > 0.05f)
            {
                input.AimPlanarDir = new float2(offset.x / len, offset.z / len);
                input.AimDistance = len;
            }

            input.Thrust = thrust;
            if (brain.Task == BotTaskKind.Mine && !inOrbit)
            {
                float3 toRock = ToroidalMapEcs.ShortestOffsetXZ(pos, brain.Goal, mapW, mapH);
                toRock.y = 0f;
                float rockDist = math.length(toRock);
                float stop = math.max(2f, brain.GoalRadius);
                bool facing = len > 0.05f && rockDist > 0.05f &&
                              math.dot(
                                  new float2(offset.x / len, offset.z / len),
                                  new float2(toRock.x / rockDist, toRock.z / rockDist)) > 0.8f;
                // Guns use the same aim the hull is facing. A dodge or a brake-out
                // points elsewhere, so those ticks do not shoot.
                fire = facing && rockDist <= stop + MineShotReach;
            }

            if (fire)
            {
                var fireEvent = new InputEvent();
                fireEvent.Set();
                input.Fire = fireEvent;
            }

            input.WantDepositGems = deposit && !thrust;
            return input;
        }

        static bool IsGoalPlanet(in BotShipBrain brain, in PlanetSnap planet) =>
            brain.GoalPlanetId == planet.PlanetId;

        static float3 MoonPosition(float3 near, in PlanetSnap planet, double elapsed, float mapW, float mapH) =>
            PlanetOrbitMath.GetMoonWorldPositionNear(
                near, planet.Position, math.max(0.25f, planet.Scale), planet.Level, planet.PlanetId,
                elapsed, mapW, mapH);

        static float3 RingPoint(float3 shipPos, in PlanetSnap planet, float mapW, float mapH)
        {
            PlanetOrbitMath.GetRingRadiiWorld(math.max(0.25f, planet.Scale), planet.Level, out _, out _, out float center);
            float3 planetNear = shipPos + ToroidalMapEcs.ShortestOffsetXZ(shipPos, planet.Position, mapW, mapH);
            float3 fromPlanet = shipPos - planetNear;
            fromPlanet.y = 0f;
            float radial = math.length(fromPlanet);
            float3 radialDir = radial > 0.05f ? fromPlanet / radial : new float3(1f, 0f, 0f);
            float3 point = planetNear + radialDir * center;
            point.y = 0f;
            return point;
        }

        static float3 HoldOutsideRing(float3 shipPos, in PlanetSnap planet, float mapW, float mapH)
        {
            PlanetOrbitMath.GetRingRadiiWorld(
                math.max(0.25f, planet.Scale), planet.Level, out _, out float outer, out _);
            float3 planetNear = shipPos + ToroidalMapEcs.ShortestOffsetXZ(shipPos, planet.Position, mapW, mapH);
            float3 fromPlanet = shipPos - planetNear;
            fromPlanet.y = 0f;
            float radial = math.length(fromPlanet);
            float3 radialDir = radial > 0.05f ? fromPlanet / radial : new float3(1f, 0f, 0f);
            float3 point = planetNear + radialDir * (outer + 4f);
            point.y = 0f;
            return point;
        }

        static void SetPlanet(ref BotShipBrain brain, BotTaskKind task, in PlanetSnap planet)
        {
            brain.Task = task;
            brain.Goal = planet.Position;
            brain.GoalRadius = 0f;
            brain.GoalPlanetId = planet.PlanetId;
            brain.GoalNetworkId = 0;
        }

        static bool OrderNeedsRetask(in BotTeamOrder order, in BotShipBrain brain)
        {
            if (order.Task == brain.Task)
                return false;
            // Scooping the burst is still the Mining order, not a new command.
            return !(order.Task == BotTaskKind.Mine && brain.Task == BotTaskKind.Collect);
        }

        static void SetMine(ref BotShipBrain brain, in RockSnap rock)
        {
            brain.Task = BotTaskKind.Mine;
            brain.Goal = rock.Position;
            brain.GoalRadius = rock.Radius;
            brain.GoalPlanetId = 0;
            brain.GoalNetworkId = 0;
        }

        static void SetCollect(ref BotShipBrain brain, float3 gemPos, float3 origin)
        {
            brain.Task = BotTaskKind.Collect;
            brain.Goal = gemPos;
            brain.CollectOrigin = origin;
            brain.GoalRadius = 0f;
            brain.GoalPlanetId = 0;
            brain.GoalNetworkId = 0;
        }

        static void SetAttack(ref BotShipBrain brain, in ShipSnap enemy)
        {
            brain.Task = BotTaskKind.Attack;
            brain.Goal = enemy.Position;
            brain.GoalNetworkId = enemy.NetworkId;
            brain.GoalPlanetId = 0;
        }

        NativeList<PlanetSnap> GatherPlanets(EntityManager em)
        {
            using var entities = _planets.ToEntityArray(Allocator.Temp);
            using var states = _planets.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var transforms = _planets.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var list = new NativeList<PlanetSnap>(entities.Length, Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                float bonus = 0f;
                if (em.HasComponent<PlanetGrowthState>(entities[i]))
                    bonus = em.GetComponentData<PlanetGrowthState>(entities[i]).ConnectionBonusFraction;
                float scale = math.max(0.5f, transforms[i].Scale);
                int half = math.max(1, PlanetPopulationMath.GetEffectiveMaxPopulation(
                    scale, states[i].PlanetLevel, bonus) / 2);
                list.Add(new PlanetSnap
                {
                    Entity = entities[i],
                    PlanetId = states[i].PlanetId,
                    Ownership = states[i].Ownership,
                    Position = transforms[i].Position,
                    Scale = scale,
                    Level = states[i].PlanetLevel,
                    Population = states[i].Population,
                    HalfPopulation = half,
                    IsHome = (byte)(states[i].IsHomePlanet ? 1 : 0),
                });
            }

            return list;
        }

        NativeList<ShipSnap> GatherShips(EntityManager em)
        {
            using var states = _ships.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var transforms = _ships.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            using var owners = _ships.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var entities = _ships.ToEntityArray(Allocator.Temp);
            var list = new NativeList<ShipSnap>(entities.Length, Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                byte landed = 0;
                if (em.HasComponent<ShipMoonDockState>(entities[i]) &&
                    em.GetComponentData<ShipMoonDockState>(entities[i]).IsFullyLanded)
                    landed = 1;
                list.Add(new ShipSnap
                {
                    NetworkId = owners[i].NetworkId,
                    Team = states[i].Team,
                    Position = transforms[i].Position,
                    Dead = (byte)(states[i].IsDead || states[i].AwaitingTeamSelection ? 1 : 0),
                    Landed = landed,
                });
            }

            return list;
        }

        void RefreshAsteroidHash(float mapW, float mapH)
        {
            if (_asteroidHash.IsCreated)
                _asteroidHash.Dispose();

            using var states = _rocks.ToComponentDataArray<AsteroidState>(Allocator.Temp);
            using var transforms = _rocks.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            _asteroidHash = BulletObstacleSpatialHash.Create(mapW, mapH, states.Length, Allocator.Persistent);
            _mineRocks.Clear();
            for (int i = 0; i < states.Length; i++)
            {
                if (!states[i].IsAliveForCombat)
                    continue;

                float scale = math.max(0.1f, transforms[i].Scale);
                float sweep = BulletCollision.AsteroidHitRadiusForSweep(scale, 1f);
                _asteroidHash.Insert(new BulletObstacleEntry
                {
                    Position = transforms[i].Position,
                    Radius = sweep,
                    Kind = BulletObstacleKind.Asteroid,
                });

                if (states[i].RemainingGems <= 0.01f)
                    continue;
                // Drawn mesh puffs past the physics sphere. Stop outside that puff.
                float visual = scale * (BodyCollisionMath.AsteroidMeshBaseRadius +
                                        BodyCollisionMath.AsteroidVisualDisplacementLocal);
                _mineRocks.Add(new RockSnap
                {
                    Position = transforms[i].Position,
                    Radius = visual + MineSurfaceGap,
                });
            }
        }

        void RefreshGemHash(float mapW, float mapH)
        {
            if (_gemHash.IsCreated)
                _gemHash.Dispose();

            using var states = _gems.ToComponentDataArray<GemState>(Allocator.Temp);
            using var transforms = _gems.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            using var motions = _gems.ToComponentDataArray<GemMotionState>(Allocator.Temp);
            var entities = new NativeList<Entity>(states.Length, Allocator.Temp);
            var poses = new NativeList<LocalTransform>(states.Length, Allocator.Temp);
            var liveMotions = new NativeList<GemMotionState>(states.Length, Allocator.Temp);
            var source = _gems.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].IsConsumed || states[i].Value <= 0.01f)
                    continue;
                entities.Add(source[i]);
                poses.Add(transforms[i]);
                liveMotions.Add(motions[i]);
            }

            source.Dispose();
            _gemHash = GemSpatialHash.Build(
                entities.AsArray(), poses.AsArray(), default, liveMotions.AsArray(),
                mapW, mapH, Allocator.Persistent);
            entities.Dispose();
            poses.Dispose();
            liveMotions.Dispose();
        }

        /// <summary>
        /// True when the current mine goal is still a gem-bearing rock.
        /// Refreshes the stop radius from that same rock and does not search for a nearer one.
        /// </summary>
        static bool TryKeepMineRock(
            ref BotShipBrain brain,
            NativeArray<RockSnap> rocks,
            float mapW,
            float mapH)
        {
            if (brain.Task != BotTaskKind.Mine)
                return false;
            if (!TryRockAt(rocks, brain.Goal, mapW, mapH, out RockSnap kept))
                return false;
            SetMine(ref brain, kept);
            return true;
        }

        /// <summary>
        /// After the mine target is gone, scoop loose gems already within <see cref="EngageRange"/>.
        /// Crystals farther out are left behind. A gem locked by another ship is left alone.
        /// The same nearby gem is kept until it is scooped or leaves that range.
        /// </summary>
        static bool TryCollectBurst(
            ref BotShipBrain brain,
            in ShipState ship,
            int networkId,
            float3 pos,
            in GemSpatialHash gems,
            NativeList<int> nearby,
            NativeHashSet<int> seen,
            float mapW,
            float mapH)
        {
            bool gathering = brain.Task == BotTaskKind.Collect;
            bool wreck = brain.Task == BotTaskKind.Mine;
            if (!gathering && !wreck)
                return false;
            if (ship.GemCapacity > 1f && ship.CurrentGems >= ship.GemCapacity - 0.5f)
                return false;
            if (!gems.IsCreated || gems.Count == 0 || !nearby.IsCreated || !seen.IsCreated)
                return false;

            float3 origin = gathering ? brain.CollectOrigin : brain.Goal;
            if (gathering &&
                TryGemNear(gems, brain.Goal, 4f, networkId, nearby, seen, mapW, mapH, out float3 kept) &&
                ToroidalMapEcs.ToroidalDistance(pos, kept, mapW, mapH) <= EngageRange)
            {
                SetCollect(ref brain, kept, origin);
                return true;
            }

            if (!TryNearestGem(gems, pos, networkId, nearby, seen, mapW, mapH, out float3 gem))
                return false;

            SetCollect(ref brain, gem, origin);
            return true;
        }

        static bool TryGemNear(
            in GemSpatialHash gems,
            float3 goal,
            float radius,
            int networkId,
            NativeList<int> nearby,
            NativeHashSet<int> seen,
            float mapW,
            float mapH,
            out float3 match)
        {
            match = default;
            gems.GatherNearby(goal, radius, nearby, seen);
            float best = radius;
            bool found = false;
            for (int n = 0; n < nearby.Length; n++)
            {
                GemSpatialEntry gem = gems.Entries[nearby[n]];
                if (gem.TractorShipId != 0 && gem.TractorShipId != networkId)
                    continue;
                float dist = ToroidalMapEcs.ToroidalDistance(goal, gem.Position, mapW, mapH);
                if (dist > best)
                    continue;
                best = dist;
                match = gem.Position;
                found = true;
            }

            return found;
        }

        static bool TryNearestGem(
            in GemSpatialHash gems,
            float3 pos,
            int networkId,
            NativeList<int> nearby,
            NativeHashSet<int> seen,
            float mapW,
            float mapH,
            out float3 match)
        {
            match = default;
            gems.GatherNearby(pos, EngageRange, nearby, seen);
            float best = EngageRange;
            bool found = false;
            for (int n = 0; n < nearby.Length; n++)
            {
                GemSpatialEntry gem = gems.Entries[nearby[n]];
                if (gem.TractorShipId != 0 && gem.TractorShipId != networkId)
                    continue;
                float fromShip = ToroidalMapEcs.ToroidalDistance(pos, gem.Position, mapW, mapH);
                if (fromShip > best)
                    continue;
                best = fromShip;
                match = gem.Position;
                found = true;
            }

            return found;
        }

        static bool TryRockAt(
            NativeArray<RockSnap> rocks,
            float3 goal,
            float mapW,
            float mapH,
            out RockSnap match)
        {
            match = default;
            if (!rocks.IsCreated || rocks.Length == 0)
                return false;

            // The goal is the rock position stored on the last decision. Asteroids do not move.
            const float matchRadius = 2f;
            float best = matchRadius;
            bool found = false;
            for (int i = 0; i < rocks.Length; i++)
            {
                float dist = ToroidalMapEcs.ToroidalDistance(goal, rocks[i].Position, mapW, mapH);
                if (dist > best)
                    continue;
                best = dist;
                match = rocks[i];
                found = true;
            }

            return found;
        }

        static bool TryNearestRock(
            NativeArray<RockSnap> rocks,
            float3 from,
            int networkId,
            float mapW,
            float mapH,
            out RockSnap best)
        {
            best = default;
            if (!rocks.IsCreated || rocks.Length == 0)
                return false;

            int bias = networkId & 7;
            float bestScore = float.MaxValue;
            bool found = false;
            for (int i = 0; i < rocks.Length; i++)
            {
                float dist = ToroidalMapEcs.ToroidalDistance(from, rocks[i].Position, mapW, mapH);
                float score = dist + ((i + bias) % 5) * 6f;
                if (score >= bestScore)
                    continue;
                bestScore = score;
                best = rocks[i];
                found = true;
            }

            return found;
        }

        static bool TryNearestEnemy(
            NativeArray<ShipSnap> ships,
            float3 from,
            TeamId team,
            int selfId,
            float range,
            float mapW,
            float mapH,
            out ShipSnap best)
        {
            best = default;
            float bestDist = range;
            bool found = false;
            for (int i = 0; i < ships.Length; i++)
            {
                ShipSnap other = ships[i];
                if (other.Dead != 0 || other.Landed != 0)
                    continue;
                if (other.NetworkId == selfId || other.Team == TeamId.None || other.Team == team)
                    continue;
                float dist = ToroidalMapEcs.ToroidalDistance(from, other.Position, mapW, mapH);
                if (dist > bestDist)
                    continue;
                bestDist = dist;
                best = other;
                found = true;
            }

            return found;
        }

        static bool TryShip(NativeArray<ShipSnap> ships, int networkId, out ShipSnap ship)
        {
            for (int i = 0; i < ships.Length; i++)
            {
                if (ships[i].NetworkId != networkId)
                    continue;
                ship = ships[i];
                return true;
            }

            ship = default;
            return false;
        }

        static bool TryShipPosition(NativeArray<ShipSnap> ships, int networkId, out float3 position)
        {
            if (TryShip(ships, networkId, out ShipSnap ship) && ship.Dead == 0)
            {
                position = ship.Position;
                return true;
            }

            position = default;
            return false;
        }

        static bool TryPlanet(NativeArray<PlanetSnap> planets, int planetId, out PlanetSnap planet)
        {
            for (int i = 0; i < planets.Length; i++)
            {
                if (planets[i].PlanetId != planetId)
                    continue;
                planet = planets[i];
                return true;
            }

            planet = default;
            return false;
        }

        static bool TryHome(NativeArray<PlanetSnap> planets, TeamId team, out PlanetSnap home)
        {
            for (int i = 0; i < planets.Length; i++)
            {
                if (planets[i].IsHome == 0 || planets[i].Ownership != team)
                    continue;
                home = planets[i];
                return true;
            }

            home = default;
            return false;
        }

        static bool TryNearestFriendly(
            NativeArray<PlanetSnap> planets,
            float3 from,
            TeamId team,
            float mapW,
            float mapH,
            out PlanetSnap best)
        {
            return TryNearestPlanet(planets, from, team, mapW, mapH, friendly: true, surplusOnly: false, out best);
        }

        static bool TryNearestHostile(
            NativeArray<PlanetSnap> planets,
            float3 from,
            TeamId team,
            float mapW,
            float mapH,
            out PlanetSnap best)
        {
            return TryNearestPlanet(planets, from, team, mapW, mapH, friendly: false, surplusOnly: false, out best);
        }

        static bool TryFriendlySurplus(
            NativeArray<PlanetSnap> planets,
            float3 from,
            TeamId team,
            float mapW,
            float mapH,
            out PlanetSnap best)
        {
            return TryNearestPlanet(planets, from, team, mapW, mapH, friendly: true, surplusOnly: true, out best);
        }

        static bool TryNearestPlanet(
            NativeArray<PlanetSnap> planets,
            float3 from,
            TeamId team,
            float mapW,
            float mapH,
            bool friendly,
            bool surplusOnly,
            out PlanetSnap best)
        {
            best = default;
            float bestDist = float.MaxValue;
            bool found = false;
            for (int i = 0; i < planets.Length; i++)
            {
                PlanetSnap planet = planets[i];
                bool isFriend = planet.Ownership == team;
                if (friendly != isFriend)
                    continue;
                if (surplusOnly && planet.Population <= planet.HalfPopulation)
                    continue;
                float dist = ToroidalMapEcs.ToroidalDistance(from, planet.Position, mapW, mapH);
                if (dist >= bestDist)
                    continue;
                bestDist = dist;
                best = planet;
                found = true;
            }

            return found;
        }
    }
}
