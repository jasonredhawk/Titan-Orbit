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
    /// deposit, and troop systems do the work. Asteroids, deposits, and level-ups
    /// stay the goal until a nearby planet can be captured. Loot from a rock that
    /// just broke is followed every tick so the hull does not sit out the scan
    /// while the gems coast away. An enemy inside
    /// <see cref="SecondaryShotRange"/> is only a gun target while mining; during a
    /// scoop the hull keeps flying until it is on the crystal. An attack intercept
    /// cycles <see cref="BotEngagePhase"/> (acquire, approach, commit, re-evaluate).
    /// Each tick is a distance and deadline compare. A missed window thrusts off the
    /// orbit ring, then takes a new side or a new target. Guns stay locked while
    /// <see cref="ShipOrbitState.InOrbitRing"/> is set, so commit does not fire there.
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
    /// A live mine target is kept until the half-second scan. Once that rock is gone,
    /// gems from the wreck are followed every tick. Ship and planet lists stay small
    /// and refresh every tick.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(ShipPhysicsMassSyncSystem))]
    [UpdateBefore(typeof(ShipPhysicsDriveSystem))]
    public partial struct BotShipBrainSystem : ISystem
    {
        const float DecisionInterval = 0.5f;
        /// <summary>
        /// Chase and the idle attack task stay inside this distance.
        /// Farther enemies are ignored so a miner does not cross the map.
        /// </summary>
        const float EngageRange = 14f;
        /// <summary>
        /// Attack intercept counts as closed inside this distance. Still inside
        /// <see cref="EngageRange"/>, so a hull that only skims the leash has to
        /// come in or the approach window expires.
        /// </summary>
        const float EngageCommitRange = 11f;
        /// <summary>
        /// Seconds an approach or an in-ring commit may spend without a shot.
        /// One float compare per ship against <see cref="SystemAPI.Time.ElapsedTime"/>.
        /// </summary>
        const float EngageWindow = 4f;
        /// <summary>How long re-evaluate thrusts out of a ring before it retargets anyway.</summary>
        const float OrbitBreakWindow = 1.5f;
        /// <summary>Lateral offset of the approach aim, in world units. Sign is <see cref="BotShipBrain.ApproachSide"/>.</summary>
        const float ApproachLateral = 8f;
        /// <summary>After two failed approaches, ignore that hull this long so the next scan picks someone else.</summary>
        const float EngageAvoidSeconds = 6f;
        /// <summary>
        /// While the goal is still a rock or a loose gem, guns answer an enemy
        /// inside this reach and the hull does not follow them. Bullet range is ~30.
        /// Past this, the asteroid stays the only target.
        /// </summary>
        const float SecondaryShotRange = 22f;
        /// <summary>
        /// Crystals counted as this kill's drop. Destroy bursts coast about 8u and a
        /// jam can shove one farther; past this the gem belongs to some other wreck.
        /// Measured from the rock, so a hull that fired from <see cref="MineShotReach"/>
        /// still acquires the drop and flies in.
        /// </summary>
        const float WreckGemRadius = 24f;
        /// <summary>
        /// Keep the same crystal while it drifts. Two gems in one burst must not swap
        /// the nose every tick.
        /// </summary>
        const float GemStickRadius = 6f;
        const float DepositFill = 0.75f;
        /// <summary>
        /// Auto-capture only considers planets inside this toroidal distance.
        /// Homes sit about 90 apart and neutrals clear them by about 40, so this
        /// reaches the next worlds over on a 300–1000 map without a map-wide raid.
        /// A capture already in progress ignores the range so the haul home for
        /// troops does not cancel the target. Map size comes from
        /// <see cref="MapStateSingleton"/> via <see cref="ToroidalMapEcs.TryGetMapSize"/>.
        /// </summary>
        const float CaptureNearbyRange = 180f;
        const float FollowDistance = 8f;
        const float HoldDistance = 6f;
        const int MaxShipLevel = 6;
        /// <summary>Server-only coin flip for the next hull. Not ghosted.</summary>
        static uint _upgradeRoll = 0xC001D00Du;
        const float DockShipRadius = 0.8f;
        const float DockZoneMultiplier = 1.05f;
        /// <summary>Gap past the drawn rock so the hull sits close without touching it.</summary>
        const float MineSurfaceGap = 1.75f;
        /// <summary>Open fire once the rock surface is within this distance. Bullet range is ~30.</summary>
        const float MineShotReach = 20f;
        /// <summary>
        /// Stop inside the hull scoop. The live tractor settings use a hull range of 1,
        /// and a burst has more crystals than beams, so parking at 1.6 never absorbed
        /// the ones the tractor was not already pulling.
        /// </summary>
        const float CollectArrive = 0.45f;
        /// <summary>
        /// Collect has to finish the last units. <see cref="CruiseResumeGap"/> (7) parks
        /// the hull outside pickup and leaves thrust latched off while the gem sits there.
        /// </summary>
        const float CollectResumeGap = 0.45f;

        /// <summary>
        /// After thrust latches off, the hull must drift this far past the hold before
        /// thrust comes back. Stops the arrive-and-reverse twitch at a mine or escort hold.
        /// Scooping uses <see cref="CollectResumeGap"/> instead.
        /// </summary>
        const float CruiseResumeGap = 7f;

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
            public int MaxPopulation;
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
            bool anyLoot = false;
            for (int i = 0; i < bots.Length; i++)
            {
                if (!em.HasComponent<BotShipBrain>(bots[i]) || !em.HasComponent<ShipState>(bots[i]))
                    continue;
                if (em.GetComponentData<ShipState>(bots[i]).IsDead)
                    continue;
                var earlyBrain = em.GetComponentData<BotShipBrain>(bots[i]);
                if (earlyBrain.Task == BotTaskKind.Mine || earlyBrain.Task == BotTaskKind.Collect)
                    anyLoot = true;
                if (earlyBrain.NextDecisionTime <= now)
                {
                    anyDecide = true;
                    continue;
                }

                if (!haveOrders || !em.HasComponent<GhostOwner>(bots[i]))
                    continue;
                var earlyShip = em.GetComponentData<ShipState>(bots[i]);
                int earlyId = em.GetComponentData<GhostOwner>(bots[i]).NetworkId;
                BotTeamOrder earlyOrder = orders.Get(earlyShip.Team);
                if (earlyOrder.IsActive(now) && earlyOrder.Includes(earlyId) &&
                    OrderNeedsRetask(earlyOrder, earlyBrain))
                    anyDecide = true;
            }

            using var planets = GatherPlanets(em);
            using var ships = GatherShips(em);
            // Rocks every loot tick so a kill is noticed on the next tick, not half a
            // second later. The bullet hash stays on the decision scan. Gems are built
            // only once a wreck actually needs a scoop (one shared hash, not per ship).
            if (anyDecide)
                RefreshAsteroidHash(mapW, mapH);
            else if (anyLoot)
                RefreshMineRocks();
            NativeArray<RockSnap> rocks = _mineRocks.AsArray();
            GemSpatialHash lootGems = default;
            if (anyDecide)
                lootGems = BuildGemHash(mapW, mapH);
            float claimNow = (float)orbitElapsed;
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

                bool inOrbit = false;
                int orbitPlanetId = 0;
                if (em.HasComponent<ShipOrbitState>(shipEntity))
                {
                    var orbit = em.GetComponentData<ShipOrbitState>(shipEntity);
                    inOrbit = orbit.InOrbitRing;
                    if (inOrbit)
                        orbitPlanetId = orbit.OrbitPlanetId;
                }

                int inbound = 0;
                if (em.HasComponent<ShipPeopleTransferState>(shipEntity))
                    inbound = math.max(0, (int)math.round(
                        em.GetComponentData<ShipPeopleTransferState>(shipEntity).PeopleInTransit));

                if ((brain.Task == BotTaskKind.Follow || brain.Task == BotTaskKind.Attack) &&
                    brain.GoalNetworkId != 0 &&
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
                    int taskBefore = (int)brain.Task;
                    float3 goalBefore = brain.Goal;
                    Choose(
                        ref brain, ship, networkId, pos, inbound, landed, dockedPlanetId, order,
                        planets.AsArray(), ships.AsArray(),
                        rocks,
                        in lootGems, _nearbyRocks, _nearbySeen,
                        mapW, mapH, em, now, claimNow);
                    if (landed && (brain.Task == BotTaskKind.Deposit || brain.Task == BotTaskKind.Upgrade))
                        TryBuy(em, shipEntity, ref ship, ref brain);
                    if (!orderNow)
                        brain.NextDecisionTime = now + DecisionInterval;
                    // A new goal starts from thrust-on. The latch then coasts once
                    // brakes can finish the stop, instead of carrying the old coast.
                    if ((int)brain.Task != taskBefore ||
                        math.distancesq(goalBefore, brain.Goal) > 16f)
                        brain.ThrustHeld = 1;
                }
                else if (brain.Task == BotTaskKind.LoadTroops || brain.Task == BotTaskKind.UnloadTroops)
                {
                    // The half-second scan left a full hold sitting on the friendly ring.
                    // Leave for the capture as soon as the gather step is done.
                    int taskBefore = (int)brain.Task;
                    float3 goalBefore = brain.Goal;
                    bool transport = liveOrder.IsActive(now) && liveOrder.Includes(networkId) &&
                                     liveOrder.Task == BotTaskKind.Transport;
                    if (!AdvanceTroops(
                            ref brain, ship, pos, planets.AsArray(), mapW, mapH, inbound,
                            transport, transport ? liveOrder.PlanetId : 0))
                    {
                        ClearCapture(ref brain);
                        if (TryHome(planets.AsArray(), ship.Team, out PlanetSnap home))
                            SetPlanet(ref brain, BotTaskKind.Hold, home);
                        else
                        {
                            brain.Task = BotTaskKind.None;
                            brain.GoalPlanetId = 0;
                            brain.GoalNetworkId = 0;
                        }
                    }

                    if ((int)brain.Task != taskBefore ||
                        math.distancesq(goalBefore, brain.Goal) > 16f)
                        brain.ThrustHeld = 1;
                }
                else if (!landed &&
                         (brain.Task == BotTaskKind.Mine || brain.Task == BotTaskKind.Collect))
                {
                    // The half-second scan is what left miners parked on a dead rock.
                    // Follow the burst as soon as the rock is gone, then the next rock.
                    bool rockHeld = brain.Task == BotTaskKind.Mine &&
                                    TryKeepMineRock(ref brain, rocks, mapW, mapH);
                    if (!rockHeld)
                    {
                        if (!lootGems.IsCreated)
                            lootGems = BuildGemHash(mapW, mapH);
                        TickLoot(
                            ref brain, ship, networkId, pos, rocks,
                            in lootGems, _nearbyRocks, _nearbySeen,
                            mapW, mapH, em, claimNow);
                    }
                }

                float speed = 0f;
                if (em.HasComponent<ShipKinematics>(shipEntity))
                {
                    float3 planar = em.GetComponentData<ShipKinematics>(shipEntity).Velocity;
                    speed = math.length(new float2(planar.x, planar.z));
                }

                quaternion rotation = em.GetComponentData<LocalTransform>(shipEntity).Rotation;
                var input = BuildInput(
                    ref brain, ship, networkId, pos, rotation, speed, landed, docking, inOrbit,
                    orbitPlanetId, inbound,
                    planets.AsArray(), ships.AsArray(),
                    mapW, mapH, orbitElapsed, now);
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

            if (lootGems.IsCreated)
                lootGems.Dispose();
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

        /// <summary>Zeros thrust and drops a copied commander material. The entity stays in the world.</summary>
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
            int inbound,
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
            float now,
            float claimNow)
        {
            // Cargo leaves only through GemDepositSystem: one shipLevel × planetLevel
            // chunk per beat, same as a player holding deposit. Stay on this moon until
            // the hold is empty so an order or a nearby enemy cannot cut the metronome.
            if (landed && ship.CurrentGems > 0.05f &&
                dockedPlanetId != 0 &&
                TryPlanet(planets, dockedPlanetId, out PlanetSnap docked) &&
                docked.Ownership == ship.Team)
            {
                ClearCapture(ref brain);
                SetPlanet(ref brain, BotTaskKind.Deposit, docked);
                NoteTask(ref brain, ship.CurrentPeople, now);
                return;
            }

            if (order.Task != BotTaskKind.None && order.Includes(networkId))
            {
                ApplyOrder(
                    ref brain, ship, networkId, pos, inbound, order, planets, ships, rocks,
                    gems, nearby, seen, mapW, mapH, em, claimNow, now);
                NoteTask(ref brain, ship.CurrentPeople, now);
                return;
            }

            // A planet this hold can actually take beats mining. Gathering is the
            // trip there: AdvanceTroops leaves the friendly ring once the load is enough.
            if (AdvanceTroops(ref brain, ship, pos, planets, mapW, mapH, inbound, false, 0))
            {
                NoteTask(ref brain, ship.CurrentPeople, now);
                return;
            }

            ClearCapture(ref brain);

            bool nearFull = ship.GemCapacity > 1f && ship.CurrentGems >= ship.GemCapacity * DepositFill;
            if (nearFull && TryNearestFriendly(planets, pos, ship.Team, mapW, mapH, out PlanetSnap depositMoon))
            {
                SetPlanet(ref brain, BotTaskKind.Deposit, depositMoon);
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

            if (TryCollectBurst(ref brain, ship, networkId, pos, gems, nearby, seen, mapW, mapH, em, claimNow))
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

            // No rock or gem left. A close enemy is the fallback task. A miner who still
            // has a rock keeps that goal; BuildInput only turns to fire inside
            // SecondaryShotRange and does not chase. A hull that just failed two
            // approaches is skipped until AvoidUntil so the scan can name someone else.
            int skip = brain.AvoidUntil > now ? brain.AvoidNetworkId : 0;
            if (TryNearestEnemy(ships, pos, ship.Team, networkId, EngageRange, mapW, mapH, out ShipSnap enemy, skip))
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
            int inbound,
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
            float claimNow,
            float now)
        {
            if (order.Task != BotTaskKind.Transport)
                ClearCapture(ref brain);

            float3 ping = order.HasWaypoint != 0 ? order.Waypoint : pos;
            switch (order.Task)
            {
                case BotTaskKind.Mine:
                    if (TryKeepMineRock(ref brain, rocks, mapW, mapH))
                        return;
                    if (TryCollectBurst(ref brain, ship, networkId, pos, gems, nearby, seen, mapW, mapH, em, claimNow))
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
                    if (!AdvanceTroops(
                            ref brain, ship, pos, planets, mapW, mapH, inbound,
                            true, order.PlanetId))
                    {
                        ClearCapture(ref brain);
                        brain.Task = BotTaskKind.Advance;
                        brain.Goal = ping;
                        brain.GoalPlanetId = 0;
                        brain.GoalNetworkId = 0;
                    }
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

                    int skip = brain.AvoidUntil > now ? brain.AvoidNetworkId : 0;
                    if (TryNearestEnemy(ships, pos, ship.Team, networkId, EngageRange, mapW, mapH, out ShipSnap enemy, skip))
                    {
                        SetAttack(ref brain, enemy);
                        return;
                    }

                    brain.Task = BotTaskKind.Attack;
                    brain.Goal = ping;
                    brain.GoalPlanetId = order.PlanetId;
                    brain.GoalNetworkId = 0;
                    brain.EngagePhase = BotEngagePhase.Acquire;
                    brain.EngageTries = 0;
                    brain.ApproachSide = 0;
                    brain.EngageDeadline = 0f;
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
            if (nextLevel <= MaxShipLevel &&
                TryRollUpgradeBranch(ship.ShipLevel, ship.BranchIndex, out int branch) &&
                UpgradeTree.IsValidUpgradeStep(ship.ShipLevel, ship.BranchIndex, nextLevel, branch))
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

        /// <summary>
        /// Picks a legal next hull. Levels 1–5 can step to the current branch or the
        /// next one; both are a fair coin flip. A single legal hull is taken as-is.
        /// </summary>
        static bool TryRollUpgradeBranch(int fromLevel, int fromBranch, out int branch)
        {
            branch = fromBranch;
            if (fromLevel < 1 || fromLevel > 5)
                return false;
            if (fromBranch < 0 || fromBranch >= UpgradeTree.GetShipCountForLevel(fromLevel))
                return false;

            int nextCount = UpgradeTree.GetShipCountForLevel(fromLevel + 1);
            int first = -1;
            int second = -1;
            int count = 0;
            for (int j = fromBranch; j <= fromBranch + 1; j++)
            {
                if (j < 0 || j >= nextCount)
                    continue;
                if (count == 0)
                    first = j;
                else
                    second = j;
                count++;
            }

            if (count <= 0)
                return false;
            if (count == 1)
            {
                branch = first;
                return true;
            }

            uint x = _upgradeRoll;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _upgradeRoll = x == 0u ? 0xC001D00Du : x;
            branch = (_upgradeRoll >> 31) == 0u ? first : second;
            return true;
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
            ref BotShipBrain brain,
            in ShipState ship,
            int networkId,
            float3 pos,
            quaternion rotation,
            float speed,
            bool landed,
            bool docking,
            bool inOrbit,
            int orbitPlanetId,
            int inbound,
            NativeArray<PlanetSnap> planets,
            NativeArray<ShipSnap> ships,
            float mapW,
            float mapH,
            double orbitElapsed,
            float now)
        {
            // Heading and thrust are separate, same as a player. AimPlanarDir is the
            // direction to face. ShipPhysicsDriveLogic turns the hull toward it at the
            // ship's rotation speed, then thrust accelerates along that facing.
            // Thrust latches off once brakes can finish the stop, and stays off until
            // the hull is clearly outside the hold. Flipping it every tick aliases
            // through the snapshot and looks like the client cannot catch the server.
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
                        // InOrbitRing alone is not arrival. The old check compared the goal
                        // id to itself, so a hull still on the friendly ring cut thrust and
                        // never flew on to the planet it was supposed to capture.
                        bool atGoalRing = inOrbit && orbitPlanetId != 0 && orbitPlanetId == orbitPlanet.PlanetId;
                        // Stay through every pickup, not the first sphere. Thrust in the ring
                        // ends the dwell, so a shortfall has to keep the jets off until the
                        // people aboard cover the garrison. Inbound crew is still on the way.
                        bool gatheringShort = brain.Task == BotTaskKind.LoadTroops &&
                                              brain.CapturePeopleNeeded > 0 &&
                                              ship.CurrentPeople < brain.CapturePeopleNeeded;
                        bool stayForLoad = gatheringShort &&
                                           atGoalRing &&
                                           orbitPlanet.Ownership == ship.Team;
                        // Hostile worlds drain until they flip. A friendly world under 49%
                        // keeps the jets off so the drop can finish; thrust would end the dwell.
                        bool garrisonDrop = orbitPlanet.Ownership == ship.Team &&
                                            ship.CurrentPeople > 0 &&
                                            PlanetPopulationMath.NeedsFriendlyGarrison(
                                                orbitPlanet.Population, orbitPlanet.MaxPopulation);
                        bool stayForUnload = brain.Task == BotTaskKind.UnloadTroops &&
                                             atGoalRing &&
                                             ship.CurrentPeople > 0 &&
                                             (orbitPlanet.Ownership != ship.Team || garrisonDrop);
                        bool stayForTransfer = stayForLoad || stayForUnload;
                        thrust = !stayForTransfer;
                        releaseThrust = stayForTransfer;
                    }
                    break;
                case BotTaskKind.Mine:
                {
                    aim = brain.Goal;
                    float stop = math.max(2f, brain.GoalRadius);
                    float dist = ToroidalMapEcs.ToroidalDistance(pos, aim, mapW, mapH);
                    thrust = CruiseToward(dist, stop, speed, ref brain.ThrustHeld);
                    break;
                }
                case BotTaskKind.Collect:
                    aim = brain.Goal;
                    // Keep closing. The mine-hold gap (7u) latches thrust off outside
                    // pickup range, and the old range check did the same once a crystal
                    // coasted past ~18u. Hull scoop is about 1u.
                    thrust = CruiseToward(
                        ToroidalMapEcs.ToroidalDistance(pos, aim, mapW, mapH),
                        CollectArrive, speed, ref brain.ThrustHeld, CollectResumeGap);
                    break;
                case BotTaskKind.Attack:
                    aim = brain.Goal;
                    {
                        float dist = ToroidalMapEcs.ToroidalDistance(pos, aim, mapW, mapH);
                        TickEngage(ref brain, now, dist, inOrbit);
                        bool trackingEnemy = brain.GoalNetworkId != 0;
                        // A ping with no hull still flies to the goal. A tracked hull
                        // uses the engage phase. Coasting inside a ring hands the ship
                        // to the passive orbit motor, and guns stay locked there.
                        if (!trackingEnemy)
                        {
                            brain.EngagePhase = BotEngagePhase.Acquire;
                            if (inOrbit && TryPlanet(planets, orbitPlanetId, out PlanetSnap ring))
                                aim = HoldOutsideRing(pos, ring, mapW, mapH);
                            thrust = inOrbit || CruiseToward(dist, 10f, speed, ref brain.ThrustHeld);
                            fire = false;
                            break;
                        }

                        PlanetSnap orbiting = default;
                        bool leavingRing = inOrbit && TryPlanet(planets, orbitPlanetId, out orbiting);
                        if (leavingRing)
                            aim = HoldOutsideRing(pos, orbiting, mapW, mapH);
                        else if (brain.EngagePhase == BotEngagePhase.Approach ||
                                 brain.EngagePhase == BotEngagePhase.Reevaluate)
                            aim = ApproachAim(pos, brain.Goal, brain.ApproachSide, mapW, mapH);
                        else
                            aim = brain.Goal;

                        if (inOrbit ||
                            brain.EngagePhase == BotEngagePhase.Approach ||
                            brain.EngagePhase == BotEngagePhase.Reevaluate)
                        {
                            thrust = true;
                            brain.ThrustHeld = 1;
                        }
                        else
                            thrust = CruiseToward(dist, 8f, speed, ref brain.ThrustHeld);

                        fire = brain.EngagePhase == BotEngagePhase.Commit && dist <= EngageRange && !inOrbit;
                    }
                    break;
                case BotTaskKind.Defend:
                    if (brain.GoalPlanetId != 0 && TryPlanet(planets, brain.GoalPlanetId, out PlanetSnap holdPlanet))
                        aim = HoldOutsideRing(pos, holdPlanet, mapW, mapH);
                    else
                        aim = brain.Goal;
                    {
                        float hold = HoldDistance;
                        if (TryStickyEnemy(ref brain, ships, pos, ship.Team, networkId, mapW, mapH, out ShipSnap hostile))
                        {
                            aim = hostile.Position;
                            hold = FollowDistance;
                            fire = !inOrbit;
                        }

                        float dist = ToroidalMapEcs.ToroidalDistance(pos, aim, mapW, mapH);
                        thrust = CruiseToward(dist, hold, speed, ref brain.ThrustHeld);
                    }
                    break;
                case BotTaskKind.Follow:
                    aim = brain.Goal;
                    thrust = CruiseToward(
                        ToroidalMapEcs.ToroidalDistance(pos, aim, mapW, mapH),
                        FollowDistance, speed, ref brain.ThrustHeld);
                    break;
                default:
                    if (brain.GoalPlanetId != 0 && TryPlanet(planets, brain.GoalPlanetId, out PlanetSnap holdHome))
                        aim = HoldOutsideRing(pos, holdHome, mapW, mapH);
                    else
                        aim = brain.Goal;
                    thrust = CruiseToward(
                        ToroidalMapEcs.ToroidalDistance(pos, aim, mapW, mapH),
                        HoldDistance, speed, ref brain.ThrustHeld);
                    break;
            }

            // Coasting must set releaseThrust. Otherwise the orbit override below
            // forces thrust back on inside a ring and the hull spirals instead of
            // settling on the passive orbit motor.
            if (!thrust)
                releaseThrust = true;

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

            // Secondary target. The mine goal stays put. Guns turn onto a close enemy
            // and thrust stays off so the hull brakes instead of chasing.
            // A scoop is the exception: braking here left the drop on the ground while
            // the hull waited out the next scan. Deposit, docking, and orbit are left
            // alone so a level-up is not cut short.
            bool chasingLoot = false;
            if (brain.Task == BotTaskKind.Collect)
            {
                float lootDist = ToroidalMapEcs.ToroidalDistance(pos, brain.Goal, mapW, mapH);
                chasingLoot = lootDist > CollectArrive + CollectResumeGap;
            }

            bool shootShip = false;
            if (!chasingLoot && !landed && !docking && !inOrbit && !unloadCargo &&
                (brain.Task == BotTaskKind.Mine || brain.Task == BotTaskKind.Collect) &&
                TrySecondaryShip(
                    ref brain, ships, pos, ship.Team, networkId, mapW, mapH, out ShipSnap threat))
            {
                aim = threat.Position;
                shootShip = true;
                thrust = false;
                releaseThrust = true;
                brain.ThrustHeld = 0;
            }

            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(pos, aim, mapW, mapH);
            float len = math.length(new float2(offset.x, offset.z));
            if (len > 0.05f)
            {
                input.AimPlanarDir = new float2(offset.x / len, offset.z / len);
                input.AimDistance = len;
            }

            input.Thrust = thrust;
            if (shootShip)
            {
                // Barrels fire along hull forward. Wait until the turn finishes so
                // the burst does not dump into the rock the ship was mining.
                fire = HullFaces(rotation, pos, aim, mapW, mapH);
            }
            else if (brain.Task == BotTaskKind.Mine && !inOrbit)
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

        /// <summary>
        /// Player-style arrive: thrust stays on until space-brakes can finish the stop,
        /// then stays off until the hull is <see cref="CruiseResumeGap"/> past the hold.
        /// A raw <c>dist &gt; hold</c> test flips every tick as the hull crosses the line.
        /// </summary>
        static bool CruiseToward(float dist, float hold, float speed, ref byte held, float resumeGap = CruiseResumeGap)
        {
            bool thrusting = held != 0;
            if (thrusting)
            {
                float brake = math.max(0.5f, ShipMassLogic.DefaultBrakeDeceleration);
                float brakeDist = math.min(36f, (speed * speed) / (2f * brake));
                if (dist <= hold + brakeDist)
                    thrusting = false;
            }
            else if (dist > hold + resumeGap)
                thrusting = true;

            held = (byte)(thrusting ? 1 : 0);
            return thrusting;
        }

        /// <summary>
        /// Enemy hull to shoot while a rock or gem stays the goal. Keeps the current
        /// ship until they leave <see cref="SecondaryShotRange"/> plus a small pad, so
        /// two crossing hulls do not swap the nose every tick. Clears
        /// <see cref="BotShipBrain.GoalNetworkId"/> when nobody is close.
        /// </summary>
        static bool TrySecondaryShip(
            ref BotShipBrain brain,
            NativeArray<ShipSnap> ships,
            float3 from,
            TeamId team,
            int selfId,
            float mapW,
            float mapH,
            out ShipSnap best)
        {
            const float stickPad = 4f;
            float stickRange = SecondaryShotRange + stickPad;
            if (brain.GoalNetworkId != 0 &&
                TryShip(ships, brain.GoalNetworkId, out ShipSnap current) &&
                current.Dead == 0 &&
                current.Landed == 0 &&
                current.Team != TeamId.None &&
                current.Team != team &&
                ToroidalMapEcs.ToroidalDistance(from, current.Position, mapW, mapH) <= stickRange)
            {
                best = current;
                if (TryNearestEnemy(ships, from, team, selfId, SecondaryShotRange, mapW, mapH, out ShipSnap nearer) &&
                    nearer.NetworkId != current.NetworkId)
                {
                    float currentDist = ToroidalMapEcs.ToroidalDistance(from, current.Position, mapW, mapH);
                    float nearerDist = ToroidalMapEcs.ToroidalDistance(from, nearer.Position, mapW, mapH);
                    if (nearerDist + stickPad < currentDist)
                        best = nearer;
                }

                brain.GoalNetworkId = best.NetworkId;
                return true;
            }

            if (!TryNearestEnemy(ships, from, team, selfId, SecondaryShotRange, mapW, mapH, out best))
            {
                brain.GoalNetworkId = 0;
                return false;
            }

            brain.GoalNetworkId = best.NetworkId;
            return true;
        }

        /// <summary>
        /// True when hull forward (+Z) is within about 37 degrees of <paramref name="target"/>.
        /// </summary>
        static bool HullFaces(quaternion rotation, float3 from, float3 target, float mapW, float mapH)
        {
            float3 toTarget = ToroidalMapEcs.ShortestOffsetXZ(from, target, mapW, mapH);
            toTarget.y = 0f;
            float targetLen = math.length(toTarget);
            if (targetLen < 0.05f)
                return true;

            float3 forward = math.mul(rotation, new float3(0f, 0f, 1f));
            forward.y = 0f;
            float forwardLen = math.length(forward);
            if (forwardLen < 0.01f)
                return false;

            return math.dot(forward / forwardLen, toTarget / targetLen) > 0.8f;
        }

        /// <summary>
        /// Keeps the enemy this hull is already facing while they stay in range.
        /// Nearest-enemy every tick swaps the heading when two ships cross.
        /// </summary>
        static bool TryStickyEnemy(
            ref BotShipBrain brain,
            NativeArray<ShipSnap> ships,
            float3 from,
            TeamId team,
            int selfId,
            float mapW,
            float mapH,
            out ShipSnap best)
        {
            float stickRange = EngageRange * 1.35f;
            if (brain.GoalNetworkId != 0 &&
                TryShip(ships, brain.GoalNetworkId, out ShipSnap current) &&
                current.Dead == 0 &&
                current.Landed == 0 &&
                current.Team != TeamId.None &&
                current.Team != team &&
                ToroidalMapEcs.ToroidalDistance(from, current.Position, mapW, mapH) <= stickRange)
            {
                best = current;
                if (TryNearestEnemy(ships, from, team, selfId, EngageRange, mapW, mapH, out ShipSnap nearer) &&
                    nearer.NetworkId != current.NetworkId)
                {
                    float currentDist = ToroidalMapEcs.ToroidalDistance(from, current.Position, mapW, mapH);
                    float nearerDist = ToroidalMapEcs.ToroidalDistance(from, nearer.Position, mapW, mapH);
                    if (nearerDist + 4f < currentDist)
                        best = nearer;
                }

                brain.GoalNetworkId = best.NetworkId;
                return true;
            }

            if (!TryNearestEnemy(ships, from, team, selfId, EngageRange, mapW, mapH, out best))
                return false;

            brain.GoalNetworkId = best.NetworkId;
            return true;
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
            // GoalNetworkId stays. It is the secondary ship, not the rock.
        }

        static void SetCollect(ref BotShipBrain brain, float3 gemPos, float3 origin)
        {
            brain.Task = BotTaskKind.Collect;
            brain.Goal = gemPos;
            brain.CollectOrigin = origin;
            brain.GoalRadius = 0f;
            brain.GoalPlanetId = 0;
            // GoalNetworkId stays. It is the secondary ship, not the gem.
        }

        static void SetAttack(ref BotShipBrain brain, in ShipSnap enemy)
        {
            bool same = brain.Task == BotTaskKind.Attack && brain.GoalNetworkId == enemy.NetworkId;
            brain.Task = BotTaskKind.Attack;
            brain.Goal = enemy.Position;
            brain.GoalNetworkId = enemy.NetworkId;
            brain.GoalPlanetId = 0;
            if (same)
                return;

            brain.EngagePhase = BotEngagePhase.Acquire;
            brain.EngageTries = 0;
            brain.ApproachSide = 0;
            brain.EngageDeadline = 0f;
        }

        /// <summary>
        /// One compare pass for an attack intercept. No scans. Acquire stamps an
        /// approach, approach becomes commit once the range closes, and a missed
        /// window becomes re-evaluate: leave the ring, then a new side or a new target.
        /// </summary>
        static void TickEngage(ref BotShipBrain brain, float now, float dist, bool inOrbit)
        {
            if (brain.GoalNetworkId == 0)
            {
                brain.EngagePhase = BotEngagePhase.Acquire;
                return;
            }

            // Leash. Acquire still uses EngageRange. An intercept already in progress
            // may step this far past it while thrusting off a ring, then has to close
            // again. Farther than this is a chase, so the hull drops the target.
            const float EngageLeash = EngageRange + 8f;
            if (dist > EngageLeash && !inOrbit)
            {
                brain.AvoidNetworkId = brain.GoalNetworkId;
                brain.AvoidUntil = now + OrbitBreakWindow + 0.5f;
                brain.GoalNetworkId = 0;
                brain.EngagePhase = BotEngagePhase.Acquire;
                brain.EngageTries = 0;
                brain.ApproachSide = 0;
                brain.EngageDeadline = 0f;
                brain.NextDecisionTime = now;
                return;
            }

            switch (brain.EngagePhase)
            {
                case BotEngagePhase.None:
                case BotEngagePhase.Acquire:
                    BeginApproach(ref brain, now);
                    break;
                case BotEngagePhase.Approach:
                    if (dist <= EngageCommitRange)
                    {
                        brain.EngagePhase = BotEngagePhase.Commit;
                        brain.EngageDeadline = now + EngageWindow;
                    }
                    else if (brain.EngageDeadline > 0f && now >= brain.EngageDeadline)
                        EnterReevaluate(ref brain, now, inOrbit);
                    break;
                case BotEngagePhase.Commit:
                    // Guns work in this band once the hull is off the ring. Refresh the
                    // window so a shot in progress is not treated as a failed approach.
                    if (!inOrbit && dist <= EngageRange)
                    {
                        brain.EngageTries = 0;
                        brain.EngageDeadline = now + EngageWindow;
                    }
                    else if (brain.EngageDeadline > 0f && now >= brain.EngageDeadline)
                        EnterReevaluate(ref brain, now, inOrbit);
                    break;
                case BotEngagePhase.Reevaluate:
                    if (!inOrbit || now >= brain.EngageDeadline)
                        FinishReevaluate(ref brain, now);
                    break;
            }
        }

        static void BeginApproach(ref BotShipBrain brain, float now)
        {
            if (brain.ApproachSide == 0)
                brain.ApproachSide = 1;
            brain.EngagePhase = BotEngagePhase.Approach;
            brain.EngageDeadline = now + EngageWindow;
        }

        static void EnterReevaluate(ref BotShipBrain brain, float now, bool inOrbit)
        {
            brain.EngageTries++;
            if (!inOrbit)
            {
                FinishReevaluate(ref brain, now);
                return;
            }

            brain.EngagePhase = BotEngagePhase.Reevaluate;
            brain.EngageDeadline = now + OrbitBreakWindow;
        }

        /// <summary>
        /// First miss flips the approach side. The second drops the hull and asks
        /// the next decision scan to pick someone else.
        /// </summary>
        static void FinishReevaluate(ref BotShipBrain brain, float now)
        {
            if (brain.EngageTries >= 2)
            {
                brain.AvoidNetworkId = brain.GoalNetworkId;
                brain.AvoidUntil = now + EngageAvoidSeconds;
                brain.GoalNetworkId = 0;
                brain.EngagePhase = BotEngagePhase.Acquire;
                brain.EngageTries = 0;
                brain.ApproachSide = 0;
                brain.EngageDeadline = 0f;
                brain.NextDecisionTime = now;
                return;
            }

            brain.ApproachSide = (sbyte)(brain.ApproachSide >= 0 ? -1 : 1);
            brain.EngagePhase = BotEngagePhase.Approach;
            brain.EngageDeadline = now + EngageWindow;
        }

        /// <summary>
        /// Point beside the target so the intercept is not a straight tangent into a ring.
        /// The offset is world XZ; steering wraps it with the toroidal shortest path.
        /// </summary>
        static float3 ApproachAim(float3 pos, float3 target, sbyte side, float mapW, float mapH)
        {
            float3 toTarget = ToroidalMapEcs.ShortestOffsetXZ(pos, target, mapW, mapH);
            float2 flat = new float2(toTarget.x, toTarget.z);
            float len = math.length(flat);
            if (len < 0.05f)
                return target;

            float2 dir = flat / len;
            float sign = side >= 0 ? 1f : -1f;
            float2 perp = new float2(-dir.y, dir.x) * sign;
            float3 aim = target + new float3(perp.x, 0f, perp.y) * ApproachLateral;
            aim.y = 0f;
            return aim;
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
                int maxPop = PlanetPopulationMath.GetEffectiveMaxPopulation(
                    scale, states[i].PlanetLevel, bonus);
                int half = math.max(1, maxPop / 2);
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
                    MaxPopulation = maxPop,
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

                if (TryMineSnap(states[i], transforms[i], out RockSnap rock))
                    _mineRocks.Add(rock);
            }
        }

        /// <summary>
        /// Gem-bearing rocks only. Used between decision scans so a destroyed mine
        /// target is noticed on the next tick. Does not rebuild the bullet hash.
        /// </summary>
        void RefreshMineRocks()
        {
            using var states = _rocks.ToComponentDataArray<AsteroidState>(Allocator.Temp);
            using var transforms = _rocks.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            _mineRocks.Clear();
            for (int i = 0; i < states.Length; i++)
            {
                if (TryMineSnap(states[i], transforms[i], out RockSnap rock))
                    _mineRocks.Add(rock);
            }
        }

        /// <summary>
        /// Drawn mesh puffs past the physics sphere. The stop radius sits outside that puff.
        /// </summary>
        static bool TryMineSnap(in AsteroidState state, in LocalTransform transform, out RockSnap rock)
        {
            rock = default;
            if (!state.IsAliveForCombat || state.RemainingGems <= 0.01f)
                return false;

            float scale = math.max(0.1f, transform.Scale);
            float visual = scale * (BodyCollisionMath.AsteroidMeshBaseRadius +
                                    BodyCollisionMath.AsteroidVisualDisplacementLocal);
            rock = new RockSnap
            {
                Position = transform.Position,
                Radius = visual + MineSurfaceGap,
            };
            return true;
        }

        /// <summary>
        /// One shared gem hash for this tick. TempJob so it can outlive the scratch
        /// lists (Temp is a stack). Caller disposes before <c>OnUpdate</c> returns.
        /// </summary>
        GemSpatialHash BuildGemHash(float mapW, float mapH)
        {
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
            GemSpatialHash hash = GemSpatialHash.Build(
                entities.AsArray(), poses.AsArray(), default, liveMotions.AsArray(),
                mapW, mapH, Allocator.TempJob);
            liveMotions.Dispose();
            poses.Dispose();
            entities.Dispose();
            return hash;
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
        /// Rock is already gone. Chase this wreck's gems, then the next rock.
        /// Runs every tick so the hull does not wait out <see cref="DecisionInterval"/>
        /// parked on the old stop radius.
        /// </summary>
        static void TickLoot(
            ref BotShipBrain brain,
            in ShipState ship,
            int networkId,
            float3 pos,
            NativeArray<RockSnap> rocks,
            in GemSpatialHash gems,
            NativeList<int> nearby,
            NativeHashSet<int> seen,
            float mapW,
            float mapH,
            EntityManager em,
            float claimNow)
        {
            BotTaskKind before = brain.Task;
            if (TryCollectBurst(
                    ref brain, ship, networkId, pos, gems, nearby, seen, mapW, mapH, em, claimNow))
            {
                if (brain.Task != before)
                    brain.ThrustHeld = 1;
                return;
            }

            // Hold is full enough that the next scan will deposit. Don't peel off to a new rock.
            if (ship.GemCapacity > 1f && ship.CurrentGems >= ship.GemCapacity * DepositFill)
                return;

            if (!TryNearestRock(rocks, pos, networkId, mapW, mapH, out RockSnap rock))
                return;

            SetMine(ref brain, rock);
            if (brain.Task != before)
                brain.ThrustHeld = 1;
        }

        /// <summary>
        /// After the mine target is gone, scoop loose gems within <see cref="WreckGemRadius"/>
        /// of that rock. The search is around the wreck, not the hull, so a kill from
        /// <see cref="MineShotReach"/> still counts. A gem reserved for another ship, or
        /// already tractored by one, is left alone. The same crystal is kept until it
        /// is scooped or leaves the wreck.
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
            float mapH,
            EntityManager em,
            float claimNow)
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
                TryGemNear(
                    gems, brain.Goal, GemStickRadius, networkId, nearby, seen, mapW, mapH,
                    em, claimNow, out float3 kept) &&
                ToroidalMapEcs.ToroidalDistance(origin, kept, mapW, mapH) <= WreckGemRadius)
            {
                SetCollect(ref brain, kept, origin);
                return true;
            }

            if (!TryNearestGem(
                    gems, origin, pos, networkId, nearby, seen, mapW, mapH, em, claimNow, out float3 gem))
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
            EntityManager em,
            float claimNow,
            out float3 match)
        {
            match = default;
            gems.GatherNearby(goal, radius, nearby, seen);
            float best = radius;
            bool found = false;
            for (int n = 0; n < nearby.Length; n++)
            {
                GemSpatialEntry gem = gems.Entries[nearby[n]];
                if (SkipLootGem(em, gem, networkId, claimNow))
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
            float3 origin,
            float3 pos,
            int networkId,
            NativeList<int> nearby,
            NativeHashSet<int> seen,
            float mapW,
            float mapH,
            EntityManager em,
            float claimNow,
            out float3 match)
        {
            match = default;
            gems.GatherNearby(origin, WreckGemRadius, nearby, seen);
            float best = float.MaxValue;
            bool found = false;
            for (int n = 0; n < nearby.Length; n++)
            {
                GemSpatialEntry gem = gems.Entries[nearby[n]];
                if (SkipLootGem(em, gem, networkId, claimNow))
                    continue;
                if (ToroidalMapEcs.ToroidalDistance(origin, gem.Position, mapW, mapH) > WreckGemRadius)
                    continue;
                float fromShip = ToroidalMapEcs.ToroidalDistance(pos, gem.Position, mapW, mapH);
                if (fromShip >= best)
                    continue;
                best = fromShip;
                match = gem.Position;
                found = true;
            }

            return found;
        }

        /// <summary>
        /// True when this hull should not fly to the crystal: another ship already has
        /// the tractor, or first-dibs / a self-spill block would reject the pickup.
        /// Chasing those is what left the bot sitting on a gem it could not take.
        /// </summary>
        static bool SkipLootGem(EntityManager em, in GemSpatialEntry gem, int networkId, float claimNow)
        {
            if (gem.TractorShipId != 0 && gem.TractorShipId != networkId)
                return true;
            if (networkId <= 0 || !em.Exists(gem.Entity) || !em.HasComponent<GemState>(gem.Entity))
                return true;
            return GemSelfPickupBlock.IsPickupBlockedForShip(
                em.GetComponentData<GemState>(gem.Entity), networkId, claimNow);
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
            out ShipSnap best,
            int skipNetworkId = 0)
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
                if (skipNetworkId != 0 && other.NetworkId == skipNetworkId)
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

        static void ClearCapture(ref BotShipBrain brain)
        {
            brain.SeekTroops = 0;
            brain.CapturePlanetId = 0;
            brain.CapturePeopleNeeded = 0;
        }

        /// <summary>
        /// Load until the people aboard can zero <see cref="BotShipBrain.CapturePlanetId"/>,
        /// then fly there and unload. A short crew stays on the friendly ring.
        /// Distances are toroidal on the map size from <see cref="ToroidalMapEcs.TryGetMapSize"/>
        /// (passed in as <paramref name="mapW"/> and <paramref name="mapH"/>).
        /// </summary>
        static bool AdvanceTroops(
            ref BotShipBrain brain,
            in ShipState ship,
            float3 pos,
            NativeArray<PlanetSnap> planets,
            float mapW,
            float mapH,
            int inbound,
            bool orderedTransport,
            int forcedPlanetId)
        {
            int forced = orderedTransport ? forcedPlanetId : 0;
            return TryCommitCapture(
                ref brain, ship, pos, planets, mapW, mapH, inbound, forced);
        }

        static bool TryCommitCapture(
            ref BotShipBrain brain,
            in ShipState ship,
            float3 pos,
            NativeArray<PlanetSnap> planets,
            float mapW,
            float mapH,
            int inbound,
            int forcedPlanetId)
        {
            // Just captured, or still sitting on a friendly world under the buffer.
            // Keep unloading until the planet reaches 49% so the 50% load line does
            // not pick the same crew back up.
            if (brain.Task == BotTaskKind.UnloadTroops &&
                brain.CapturePlanetId != 0 &&
                (forcedPlanetId == 0 || forcedPlanetId == brain.CapturePlanetId) &&
                TryPlanet(planets, brain.CapturePlanetId, out PlanetSnap reinforcing) &&
                reinforcing.Ownership == ship.Team &&
                ship.CurrentPeople > 0 &&
                PlanetPopulationMath.NeedsFriendlyGarrison(
                    reinforcing.Population, reinforcing.MaxPopulation))
            {
                brain.SeekTroops = 1;
                SetPlanet(ref brain, BotTaskKind.UnloadTroops, reinforcing);
                return true;
            }

            // A haul already on the way keeps going only while the people aboard can
            // still zero the live garrison. One sphere is not a reason to finish the trip.
            if (brain.Task == BotTaskKind.UnloadTroops &&
                brain.CapturePlanetId != 0 &&
                (forcedPlanetId == 0 || forcedPlanetId == brain.CapturePlanetId) &&
                TryPlanet(planets, brain.CapturePlanetId, out PlanetSnap dropping) &&
                dropping.Ownership != ship.Team &&
                dropping.Population > 0 &&
                ship.CurrentPeople >= CaptureQuota(dropping, ship.PeopleCapacity))
            {
                brain.SeekTroops = 1;
                brain.CapturePeopleNeeded = CaptureQuota(dropping, ship.PeopleCapacity);
                SetPlanet(ref brain, BotTaskKind.UnloadTroops, dropping);
                return true;
            }

            int held = ship.CurrentPeople + math.max(0, inbound);
            if (!TryPickCaptureTarget(
                    planets, pos, ship.Team, ship.PeopleCapacity, held, mapW, mapH,
                    brain.CapturePlanetId, forcedPlanetId,
                    out PlanetSnap target, out int needed))
                return false;

            return BeginCaptureTrip(
                ref brain, ship, pos, planets, mapW, mapH, inbound, in target, needed);
        }

        /// <summary>
        /// People who must be aboard before the hull may leave for <paramref name="target"/>.
        /// Two extra bodies when the hold has room, so the garrison can tick up on the flight.
        /// </summary>
        static int CaptureQuota(in PlanetSnap target, int capacity)
        {
            int pop = math.max(1, target.Population);
            if (capacity <= pop)
                return pop;
            return math.min(capacity, pop + 2);
        }

        /// <summary>
        /// Point the hull at a friendly surplus world until the capture load is aboard,
        /// then at the enemy ring. Departs only when <see cref="ShipState.CurrentPeople"/>
        /// covers the live garrison. A sphere still in flight does not count.
        /// </summary>
        static bool BeginCaptureTrip(
            ref BotShipBrain brain,
            in ShipState ship,
            float3 pos,
            NativeArray<PlanetSnap> planets,
            float mapW,
            float mapH,
            int inbound,
            in PlanetSnap target,
            int needed)
        {
            int boarded = math.max(0, ship.CurrentPeople);
            int incoming = math.max(0, inbound);
            bool holdFull = ship.PeopleCapacity > 0 && boarded + incoming >= ship.PeopleCapacity;
            int stickSource = brain.Task == BotTaskKind.LoadTroops ? brain.GoalPlanetId : 0;
            bool canLoad = false;
            PlanetSnap source = default;
            if (!holdFull && ship.PeopleCapacity > 0)
                canLoad = TryLoadSource(planets, pos, ship.Team, mapW, mapH, stickSource, out source);

            int pop = math.max(0, target.Population);
            // Pad when the hold and the surplus can spare it. Otherwise the live garrison is the bar.
            bool haveEnough = pop > 0 && boarded >= needed;
            bool canZeroNow = pop > 0 && boarded >= pop && !canLoad && incoming == 0;
            bool waitingToBoard = !haveEnough && !canZeroNow && needed > 0 && incoming > 0 &&
                                  boarded + incoming >= math.max(pop, needed);

            brain.SeekTroops = 1;
            brain.CapturePlanetId = target.PlanetId;
            brain.CapturePeopleNeeded = math.max(1, needed);

            if (haveEnough || canZeroNow)
            {
                SetPlanet(ref brain, BotTaskKind.UnloadTroops, target);
                return true;
            }

            if (waitingToBoard && brain.Task == BotTaskKind.LoadTroops && brain.GoalPlanetId != 0)
                return true;

            if (canLoad)
            {
                SetPlanet(ref brain, BotTaskKind.LoadTroops, source);
                return true;
            }

            // Surplus is dry this tick. Hold the friendly ring so the next person
            // above the reserve loads, instead of attacking with a short crew.
            if (TryNearestFriendly(planets, pos, ship.Team, mapW, mapH, out PlanetSnap wait))
            {
                if (brain.Task != BotTaskKind.LoadTroops || brain.GoalPlanetId != wait.PlanetId)
                    SetPlanet(ref brain, BotTaskKind.LoadTroops, wait);
                return true;
            }

            return false;
        }

        static bool TryPickCaptureTarget(
            NativeArray<PlanetSnap> planets,
            float3 from,
            TeamId team,
            int capacity,
            int held,
            float mapW,
            float mapH,
            int stickId,
            int forcedPlanetId,
            out PlanetSnap best,
            out int needed)
        {
            best = default;
            needed = 0;
            int pool = FriendlySurplus(planets, team);
            if (forcedPlanetId != 0 &&
                TryPlanet(planets, forcedPlanetId, out PlanetSnap forced) &&
                CanAffordCapture(forced, team, capacity, held, pool))
            {
                best = forced;
                needed = CaptureQuota(forced, capacity);
                return true;
            }

            if (stickId != 0 &&
                TryPlanet(planets, stickId, out PlanetSnap sticky) &&
                CanAffordCapture(sticky, team, capacity, held, pool))
            {
                best = sticky;
                needed = CaptureQuota(sticky, capacity);
                return true;
            }

            // 100 floor reaches the next world past a ~90 home separation. The cap
            // keeps a small map from treating the far side as nearby.
            float span = math.max(1f, math.min(mapW, mapH));
            float range = math.min(CaptureNearbyRange, math.max(100f, span * 0.34f));
            float bestDist = range;
            bool found = false;
            for (int i = 0; i < planets.Length; i++)
            {
                PlanetSnap planet = planets[i];
                if (!CanAffordCapture(planet, team, capacity, held, pool))
                    continue;
                float dist = ToroidalMapEcs.ToroidalDistance(from, planet.Position, mapW, mapH);
                if (dist > range || dist >= bestDist)
                    continue;
                bestDist = dist;
                best = planet;
                found = true;
            }

            if (!found)
                return false;
            needed = CaptureQuota(best, capacity);
            return true;
        }

        static bool CanAffordCapture(in PlanetSnap planet, TeamId team, int capacity, int held, int surplus)
        {
            if (planet.Ownership == team || planet.Population <= 0 || capacity < planet.Population)
                return false;
            return held >= planet.Population || held + surplus >= planet.Population;
        }

        static int FriendlySurplus(NativeArray<PlanetSnap> planets, TeamId team)
        {
            int sum = 0;
            for (int i = 0; i < planets.Length; i++)
            {
                PlanetSnap planet = planets[i];
                if (planet.Ownership != team)
                    continue;
                int extra = planet.Population - planet.HalfPopulation;
                if (extra > 0)
                    sum += extra;
            }

            return sum;
        }

        static bool TryLoadSource(
            NativeArray<PlanetSnap> planets,
            float3 from,
            TeamId team,
            float mapW,
            float mapH,
            int stickPlanetId,
            out PlanetSnap best)
        {
            if (stickPlanetId != 0 &&
                TryPlanet(planets, stickPlanetId, out PlanetSnap stick) &&
                stick.Ownership == team &&
                stick.Population > stick.HalfPopulation)
            {
                best = stick;
                return true;
            }

            return TryFriendlySurplus(planets, from, team, mapW, mapH, out best);
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
