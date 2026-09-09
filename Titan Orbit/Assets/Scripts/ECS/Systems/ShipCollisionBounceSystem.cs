using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Physics.Systems;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// After Unity Physics exports contacts, applies one bounce pass from
    /// <see cref="ShipCollisionImpulseLogic"/> and <see cref="AsteroidSettings.BounceRestitution"/>.
    /// World bodies (asteroid, planet, moon, moon shield) use the same wall reflect.
    /// Predicted ship↔ship uses two-body impulse at that same <c>e</c>. Client remotes have
    /// no <see cref="PhysicsVelocity"/> — moving wall from ghosted <see cref="ShipKinematics"/>
    /// at the same <c>e</c>. PhysX materials stay restitution 0 so this pass owns rebound.
    /// MEGA hulls plow asteroids / planets (restore pre-collision motion). Moon, shield,
    /// planet, and asteroid share one wall bounce. Friendly shields are off via
    /// <see cref="TitanOrbitPhysicsLayers.ShipForTeam"/>. Flying ships that PhysX
    /// exports from the moon rock onto the concentric shield rim get snapshot pose
    /// restore (same idea as MEGA planet undo). Dock / takeoff skip only while
    /// fully landed or taking off — flying into the moon is a normal bounce.
    /// Server ram damage + client soft-destroy happen elsewhere.
    /// <para>
    /// Runs on ServerSimulation and ClientSimulation (predicted). Collision-event stream only —
    /// no asteroid/planet <c>ToEntityArray</c> (join-crash safe). Tangential grip stays in
    /// <see cref="ShipAsteroidContactFrictionSystem"/> which runs after this system.
    /// </para>
    /// Pipeline: Drive → Snapshot → PhysicsSimulation → Export → ContactCollect →
    /// Bounce (this) → Friction → Wrap → Planar → Kinematics.
    /// All hulls are single spheres (ship, rock, planet, moon, shield).
    /// </summary>
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    [UpdateBefore(typeof(ShipAsteroidContactFrictionSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipCollisionBounceSystem : ISystem
    {
        NativeHashMap<Entity, float3> _working;
        NativeHashSet<Entity> _megaUnconstrained;
        NativeHashSet<Entity> _megaKeepPhysX;
        NativeHashSet<long> _seenShipPairs;
        NativeHashSet<Entity> _seenPlowRocks;

        /// <summary>Require the classified contact buffer from <see cref="ShipPhysicsContactCollectSystem"/>.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipPhysicsContactQueueTag>();
            _working = new NativeHashMap<Entity, float3>(32, Allocator.Persistent);
            _megaUnconstrained = new NativeHashSet<Entity>(16, Allocator.Persistent);
            _megaKeepPhysX = new NativeHashSet<Entity>(16, Allocator.Persistent);
            _seenShipPairs = new NativeHashSet<long>(16, Allocator.Persistent);
            _seenPlowRocks = new NativeHashSet<Entity>(16, Allocator.Persistent);
        }

        /// <summary>Persistent scratch from <see cref="OnCreate"/>.</summary>
        public void OnDestroy(ref SystemState state)
        {
            if (_working.IsCreated)
                _working.Dispose();
            if (_megaUnconstrained.IsCreated)
                _megaUnconstrained.Dispose();
            if (_megaKeepPhysX.IsCreated)
                _megaKeepPhysX.Dispose();
            if (_seenShipPairs.IsCreated)
                _seenShipPairs.Dispose();
            if (_seenPlowRocks.IsCreated)
                _seenPlowRocks.Dispose();
        }

        /// <summary>
        /// Collects collision events, then applies impulses from pre-collision snapshots into
        /// <see cref="PhysicsVelocity"/>. Safe to write velocities here (post-Export).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Join-crash gate (client only) ---
            // [TITAN-ORBIT] ShipTag lookups during TeamChoice Instantiates Crash!!! —
            // use ShouldSkipShipSimulation (not full GhostSpawnBacklog / map Instantiates trickle).
            // Server always bounces.
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            float fixedDt = SystemAPI.Time.DeltaTime;
            if (fixedDt <= 0f)
                fixedDt = 1f / 60f;

            // --- One designer e for every bounce (ships, rocks, planets, moons, shields) ---
            var settings = AsteroidSettingsCache.ResolveOrDefault();
            settings.ClampValues();
            float restitution = settings.BounceRestitution;

            // Empty contact queue still runs friendly-shield rim undo — the yeet is the
            // physics tick after moon-rock contact, which often has no kind-3 event.
            if (!SystemAPI.TryGetSingletonBuffer<ShipPhysicsContactElement>(out var pairs))
            {
                UndoFriendlyMoonShieldRimSnaps(ref state, fixedDt);
                return;
            }

            // --- Lookups for impulse resolve ---
            var snapshotLookup = SystemAPI.GetComponentLookup<ShipPreCollisionVelocity>(true);
            var motorLookup = SystemAPI.GetComponentLookup<ShipMotorConfig>(true);
            var shipStateLookup = SystemAPI.GetComponentLookup<ShipState>(true);
            var moonDockLookup = SystemAPI.GetComponentLookup<ShipMoonDockState>(true);
            var shieldPlanetLookup = SystemAPI.GetComponentLookup<PlanetGemMoonColliderPlanetRef>(true);
            var planetStateLookup = SystemAPI.GetComponentLookup<PlanetState>(true);
            var megaLookup = SystemAPI.GetComponentLookup<MegaShipState>(true);
            var asteroidStateLookup = SystemAPI.GetComponentLookup<AsteroidState>(true);
            var culledLookup = SystemAPI.GetComponentLookup<AsteroidClientCulledTag>(true);
            var velocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(false);
            var kinematicsLookup = SystemAPI.GetComponentLookup<ShipKinematics>(true);
            var transformLookup = SystemAPI.GetComponentLookup<LocalTransform>(false);
            bool isClient = state.World.IsClient();

            // Working velocities start from the pre-collision snapshot so multiple contacts
            // in one tick accumulate correctly without reading PhysX's inelastic result.
            // Persistent scratch — grind emits contacts every predicted tick (Allocator.Temp
            // here used to allocate every physics step while pinned on a rock).
            int pairCap = math.max(8, pairs.Length * 2);
            EnsureMapCapacity(ref _working, pairCap);
            EnsureSetCapacity(ref _megaUnconstrained, pairCap);
            EnsureSetCapacity(ref _megaKeepPhysX, pairCap);
            EnsureSetCapacity(ref _seenShipPairs, math.max(8, pairs.Length));
            _working.Clear();
            _megaUnconstrained.Clear();
            _megaKeepPhysX.Clear();
            _seenShipPairs.Clear();

            for (int i = 0; i < pairs.Length; i++)
            {
                ShipPhysicsContactElement pair = pairs[i];

                if (pair.Kind == ShipPhysicsContactKind.Ship)
                {
                    long key = PackEntityPairKey(pair.Ship, pair.Other);
                    if (!_seenShipPairs.Add(key))
                        continue;
                    // Keep MEGA plow from undoing the solver pose. Predicted pairs get a
                    // snapshot two-body rewrite (not stacked on PhysX). Interpolated remotes
                    // have no PhysicsVelocity — moving wall from ghosted kinematics.
                    _megaKeepPhysX.Add(pair.Ship);
                    _megaKeepPhysX.Add(pair.Other);
                    ApplyShipVsShip(
                        pair, ref _working, snapshotLookup, velocityLookup, kinematicsLookup,
                        motorLookup, shipStateLookup, megaLookup, isClient, restitution);
                }
                else if (pair.Kind == ShipPhysicsContactKind.Asteroid)
                {
                    bool plowed = ApplyShipVsAsteroid(
                        pair, ref _working, snapshotLookup, shipStateLookup,
                        megaLookup, asteroidStateLookup, culledLookup, restitution);
                    if (plowed)
                        _megaUnconstrained.Add(pair.Ship);
                }
                else if (pair.Kind == ShipPhysicsContactKind.Planet)
                {
                    if (ShouldSkipMoonWorldBounce(pair.Ship, moonDockLookup))
                        continue;

                    bool megaPlanet = megaLookup.HasComponent(pair.Ship)
                                      && megaLookup[pair.Ship].IsMega;
                    if (megaPlanet)
                    {
                        // Keep snapshot velocity — PhysX planet depenetration is undone below.
                        _working[pair.Ship] = GetWorkingOrSnapshot(
                            pair.Ship, ref _working, snapshotLookup);
                        _megaUnconstrained.Add(pair.Ship);
                    }
                    else
                    {
                        ApplyWorldWallBounce(pair, ref _working, snapshotLookup, restitution);
                    }
                }
                else if (pair.Kind == ShipPhysicsContactKind.Moon
                         || pair.Kind == ShipPhysicsContactKind.Shield)
                {
                    bool skipMoon = ShouldSkipMoonWorldBounce(pair.Ship, moonDockLookup)
                                    || (pair.Kind == ShipPhysicsContactKind.Shield
                                        && ShouldSkipFriendlyShieldBounce(
                                            pair, shipStateLookup, shieldPlanetLookup,
                                            planetStateLookup));
                    if (!skipMoon)
                    {
                        ApplyWorldWallBounce(pair, ref _working, snapshotLookup, restitution);
                    }
                }
            }

            // --- Write PhysicsVelocity ---
            foreach (var kv in _working)
            {
                Entity e = kv.Key;
                if (!velocityLookup.HasComponent(e))
                    continue;
                var pv = velocityLookup[e];
                pv.Linear = kv.Value;
                velocityLookup[e] = pv;
            }

            // --- Undo PhysX depenetration (MEGA plow / planet only) ---
            // Reconstruct unconstrained pose from the pre-physics snapshot (drive already applied).
            // [PHYSICS] The solver already wrote LocalTransform. Writing it back here is what
            // stops the visible snap; velocity restore alone is not enough.
            if (_megaUnconstrained.Count > 0)
            {
                foreach (Entity ship in _megaUnconstrained)
                {
                    if (_megaKeepPhysX.Contains(ship))
                        continue;
                    if (!snapshotLookup.HasComponent(ship) || !transformLookup.HasComponent(ship))
                        continue;

                    var snap = snapshotLookup[ship];
                    var lt = transformLookup[ship];
                    float3 pos = snap.Position + snap.Linear * fixedDt;
                    pos.y = 0f;
                    lt.Position = pos;
                    transformLookup[ship] = lt;
                }
            }

            // Flying friendly ships: PhysX can export a 4-unit snap from the moon rock
            // onto the concentric shield / orbit-zone rim. Restore unconstrained pose;
            // bounced velocity already written above stays.
            UndoFriendlyMoonShieldRimSnaps(ref state, fixedDt);

            // Client predicts the rock vanishing so the next physics step cannot pin the MEGA
            // while HitRpc is still in flight. Server authority + self-damage stay in ramming.
            // SoftDestroy strips PhysicsCollider (structural) and invalidates ComponentLookup —
            // collect rocks first, then teardown after megaLookup is no longer used.
            if (state.World.IsClient())
            {
                EnsureSetCapacity(ref _seenPlowRocks, math.max(8, pairs.Length));
                _seenPlowRocks.Clear();
                for (int i = 0; i < pairs.Length; i++)
                {
                    ShipPhysicsContactElement pair = pairs[i];
                    if (pair.Kind != ShipPhysicsContactKind.Asteroid)
                        continue;
                    if (!megaLookup.HasComponent(pair.Ship) || !megaLookup[pair.Ship].IsMega)
                        continue;
                    _seenPlowRocks.Add(pair.Other);
                }

                foreach (Entity rock in _seenPlowRocks)
                {
                    ClientLocalAsteroidCombatSync.SoftDestroyLocalAsteroidEntity(
                        state.EntityManager, rock);
                }
            }
        }

        static void EnsureMapCapacity(ref NativeHashMap<Entity, float3> map, int needed)
        {
            if (map.Capacity >= needed)
                return;
            var grown = new NativeHashMap<Entity, float3>(
                math.max(needed, map.Capacity * 2), Allocator.Persistent);
            map.Dispose();
            map = grown;
        }

        static void EnsureSetCapacity<T>(ref NativeHashSet<T> set, int needed)
            where T : unmanaged, System.IEquatable<T>
        {
            if (set.Capacity >= needed)
                return;
            var grown = new NativeHashSet<T>(math.max(needed, set.Capacity * 2), Allocator.Persistent);
            set.Dispose();
            set = grown;
        }

        /// <summary>
        /// Stable pair key so (A,B) and (B,A) collide to the same slot (Index/Version order).
        /// </summary>
        static long PackEntityPairKey(Entity a, Entity b)
        {
            int aIdx = a.Index;
            int aVer = a.Version;
            int bIdx = b.Index;
            int bVer = b.Version;
            // Order by Index then Version so both orientations hash identically.
            if (aIdx > bIdx || (aIdx == bIdx && aVer > bVer))
            {
                (aIdx, bIdx) = (bIdx, aIdx);
                (aVer, bVer) = (bVer, aVer);
            }

            unchecked
            {
                long lo = ((long)aIdx << 32) | (uint)aVer;
                long hi = ((long)bIdx << 32) | (uint)bVer;
                // Mix into one long — good enough for per-frame dedup sets.
                return lo ^ (hi * 397);
            }
        }

        /// <summary>
        /// Pose jump that is a PhysX export, not a velocity integrate, at cruise.
        /// Session 8b4ec2 yeet was ~4 units vs ~0.09 expected.
        /// </summary>
        const float FriendlyShieldRimJumpMin = 1.5f;

        /// <summary>Slack so a hull sitting on the rock still counts as "near rock."</summary>
        const float FriendlyShieldRimRockSlack = 1.3f;

        /// <summary>How close to shield+hull the live pose must be to count as a rim snap.</summary>
        const float FriendlyShieldRimSurfaceSlack = 0.5f;

        /// <summary>
        /// AfterPhysics: if a flying ship was sitting on a friendly moon rock before
        /// physics and is now on the shield / dock-zone rim, write back snapshot pose.
        /// Landed attach and takeoff own those poses and are skipped.
        /// mapW/mapH from <see cref="ToroidalMapEcs"/> / <c>MapStateSingleton</c>.
        /// </summary>
        void UndoFriendlyMoonShieldRimSnaps(ref SystemState state, float fixedDt)
        {
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return;

            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double elapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : state.World.Time.ElapsedTime;

            var moons = new NativeList<FriendlyMoonRim>(8, state.WorldUpdateAllocator);
            foreach (var (planetState, planetTransform, moon) in SystemAPI
                         .Query<RefRO<PlanetState>, RefRO<LocalTransform>, RefRO<PlanetGemMoonState>>()
                         .WithAll<PlanetTag>())
            {
                if (moon.ValueRO.CurrentShield <= 0.001f)
                    continue;
                if (planetState.ValueRO.Ownership == TeamId.None)
                    continue;

                float planetSize = math.max(0.25f, planetTransform.ValueRO.Scale);
                bool home = planetState.ValueRO.IsHomePlanet;
                moons.Add(new FriendlyMoonRim
                {
                    Owner = planetState.ValueRO.Ownership,
                    PlanetPos = planetTransform.ValueRO.Position,
                    PlanetSize = planetSize,
                    PlanetLevel = planetState.ValueRO.PlanetLevel,
                    PlanetId = planetState.ValueRO.PlanetId,
                    BodyRadius = PlanetGemMoonMath.GetMoonBodyRadiusWorld(planetSize, home),
                    ShieldRadius = PlanetGemMoonMath.GetMoonShieldOuterRadiusWorld(planetSize, home),
                });
            }

            if (moons.Length == 0)
                return;

            foreach (var (transform, snapshot, shipState, moonDock, physicsCollider) in SystemAPI
                         .Query<RefRW<LocalTransform>, RefRO<ShipPreCollisionVelocity>,
                             RefRO<ShipState>, RefRO<ShipMoonDockState>, RefRO<PhysicsCollider>>()
                         .WithAll<ShipTag, Simulate>())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;
                if (moonDock.ValueRO.IsTakingOff || moonDock.ValueRO.IsFullyLanded)
                    continue;

                TeamId team = shipState.ValueRO.Team;
                if (team == TeamId.None)
                    continue;

                float3 live = transform.ValueRO.Position;
                float3 snapPos = snapshot.ValueRO.Position;
                float jump = math.length(new float2(live.x - snapPos.x, live.z - snapPos.z));
                if (jump < FriendlyShieldRimJumpMin)
                    continue;

                float hullR = physicsCollider.ValueRO.Value.IsCreated
                    ? ShipToroidalWorldCollisionLogic.GetShipCollisionRadiusWorld(
                        physicsCollider.ValueRO, transform.ValueRO.Scale)
                    : 0.7f;

                if (!IsFriendlyMoonRockToShieldRimSnap(
                        live, snapPos, team, hullR, mapW, mapH, elapsed, moons))
                    continue;

                float3 pos = snapPos + snapshot.ValueRO.Linear * fixedDt;
                pos.y = 0f;
                var lt = transform.ValueRO;
                lt.Position = pos;
                transform.ValueRW = lt;
            }
        }

        /// <summary>
        /// True when snapshot was near the friendly moon rock and live pose is on the
        /// shield / visual-shell rim (the orbit-zone push-out).
        /// </summary>
        static bool IsFriendlyMoonRockToShieldRimSnap(
            float3 livePos,
            float3 snapPos,
            TeamId team,
            float hullR,
            float mapW,
            float mapH,
            double elapsed,
            in NativeList<FriendlyMoonRim> moons)
        {
            for (int i = 0; i < moons.Length; i++)
            {
                FriendlyMoonRim moon = moons[i];
                if (!PlanetGemMoonCombatLogic.IsTeamFriendlyToMoon(moon.Owner, team))
                    continue;

                float3 moonPos = PlanetOrbitMath.GetMoonWorldPositionNear(
                    livePos,
                    moon.PlanetPos,
                    moon.PlanetSize,
                    moon.PlanetLevel,
                    moon.PlanetId,
                    elapsed,
                    mapW,
                    mapH);

                float snapDist = ToroidalMapEcs.ToroidalDistance(snapPos, moonPos, mapW, mapH);
                float liveDist = ToroidalMapEcs.ToroidalDistance(livePos, moonPos, mapW, mapH);
                float rockContact = moon.BodyRadius + hullR + FriendlyShieldRimRockSlack;
                float rimContact = moon.ShieldRadius + hullR;
                if (snapDist > rockContact)
                    continue;
                if (liveDist + FriendlyShieldRimSurfaceSlack < rimContact)
                    continue;
                if (liveDist <= snapDist + FriendlyShieldRimJumpMin)
                    continue;
                return true;
            }

            return false;
        }

        struct FriendlyMoonRim
        {
            public TeamId Owner;
            public float3 PlanetPos;
            public float PlanetSize;
            public int PlanetLevel;
            public int PlanetId;
            public float BodyRadius;
            public float ShieldRadius;
        }

        /// <summary>
        /// Landed pad and takeoff own the hull. Bounce must not fight attach or the
        /// outward takeoff that exits the moon orbit zone.
        /// </summary>
        static bool ShouldSkipMoonWorldBounce(Entity ship, ComponentLookup<ShipMoonDockState> moonDock)
        {
            if (!moonDock.HasComponent(ship))
                return false;
            var dock = moonDock[ship];
            return dock.IsTakingOff || dock.IsFullyLanded;
        }

        /// <summary>
        /// Friendly shields are pass-through. A leftover kind-4 event must not bounce
        /// the hull (pair-disable is the solver gate; this is the gameplay backup).
        /// </summary>
        static bool ShouldSkipFriendlyShieldBounce(
            in ShipPhysicsContactElement pair,
            ComponentLookup<ShipState> ships,
            ComponentLookup<PlanetGemMoonColliderPlanetRef> shieldPlanets,
            ComponentLookup<PlanetState> planets)
        {
            if (!ships.HasComponent(pair.Ship) || !shieldPlanets.HasComponent(pair.Other))
                return false;
            Entity planet = shieldPlanets[pair.Other].PlanetEntity;
            if (!planets.HasComponent(planet))
                return false;
            return PlanetGemMoonCombatLogic.IsTeamFriendlyToMoon(
                planets[planet].Ownership, ships[pair.Ship].Team);
        }

        /// <summary>
        /// Predicted ship↔ship: snapshot two-body impulse (ramming mass) at the shared <c>e</c>.
        /// Client local vs an interpolated remote (no <see cref="PhysicsVelocity"/>) uses a
        /// moving-wall reflect at that same <c>e</c>.
        /// </summary>
        static void ApplyShipVsShip(
            ShipPhysicsContactElement pair,
            ref NativeHashMap<Entity, float3> working,
            ComponentLookup<ShipPreCollisionVelocity> snapshots,
            ComponentLookup<PhysicsVelocity> velocities,
            ComponentLookup<ShipKinematics> kinematics,
            ComponentLookup<ShipMotorConfig> motors,
            ComponentLookup<ShipState> shipStates,
            ComponentLookup<MegaShipState> megas,
            bool isClient,
            float restitution)
        {
            bool shipHasVel = velocities.HasComponent(pair.Ship);
            bool otherHasVel = velocities.HasComponent(pair.Other);
            if (shipHasVel != otherHasVel)
            {
                if (isClient)
                {
                    ApplyLocalVsInterpolatedRemote(
                        pair, ref working, snapshots, velocities, kinematics, megas, restitution);
                }

                return;
            }

            if (!shipHasVel)
                return;
            if (shipStates.HasComponent(pair.Ship) && shipStates[pair.Ship].IsDead)
                return;
            if (shipStates.HasComponent(pair.Other) && shipStates[pair.Other].IsDead)
                return;

            float3 vA = GetWorkingOrSnapshot(pair.Ship, ref working, snapshots);
            float3 vB = GetWorkingOrSnapshot(pair.Other, ref working, snapshots);
            float mA = GetShipCollisionMass(pair.Ship, motors, shipStates, megas);
            float mB = GetShipCollisionMass(pair.Other, motors, shipStates, megas);
            if (!ShipCollisionImpulseLogic.ApplyTwoBodyImpulse(
                    ref vA, ref vB, pair.NormalShipFromOther, mA, mB, restitution))
                return;

            working[pair.Ship] = vA;
            working[pair.Other] = vB;
        }

        /// <summary>
        /// Client only: local predicted hull vs interpolated remote (no <see cref="PhysicsVelocity"/>).
        /// Restores pre-collision velocity then reflects in the remote's rest frame so a ram
        /// scrapes off a moving ghost instead of a static magnet. No-ops when both hulls have
        /// velocity (listen-server / two predicted) or the local ship is a MEGA plow.
        /// </summary>
        static void ApplyLocalVsInterpolatedRemote(
            ShipPhysicsContactElement pair,
            ref NativeHashMap<Entity, float3> working,
            ComponentLookup<ShipPreCollisionVelocity> snapshots,
            ComponentLookup<PhysicsVelocity> velocities,
            ComponentLookup<ShipKinematics> kinematics,
            ComponentLookup<MegaShipState> megas,
            float restitution)
        {
            Entity local = pair.Ship;
            Entity remote = pair.Other;
            float3 n = pair.NormalShipFromOther;

            bool shipHasVel = velocities.HasComponent(local);
            bool otherHasVel = velocities.HasComponent(remote);
            if (shipHasVel == otherHasVel)
                return;

            if (!shipHasVel)
            {
                local = pair.Other;
                remote = pair.Ship;
                n = -n;
            }

            if (megas.HasComponent(local) && megas[local].IsMega)
                return;

            float3 wallVel = kinematics.HasComponent(remote)
                ? kinematics[remote].Velocity
                : float3.zero;
            float3 v = GetWorkingOrSnapshot(local, ref working, snapshots);
            ShipCollisionImpulseLogic.ApplyMovingWallImpulse(ref v, wallVel, n, restitution);
            working[local] = v;
        }

        /// <summary>Reads snapshot (or current working) velocity for a ship entity.</summary>
        static float3 GetWorkingOrSnapshot(
            Entity ship,
            ref NativeHashMap<Entity, float3> working,
            ComponentLookup<ShipPreCollisionVelocity> snapshots)
        {
            if (working.TryGetValue(ship, out float3 v))
                return v;
            if (snapshots.HasComponent(ship))
                return snapshots[ship].Linear;
            return float3.zero;
        }

        /// <summary>Ramming mass for bounce feel (linear HP bulk + weighted gems).</summary>
        static float GetShipCollisionMass(
            Entity ship,
            ComponentLookup<ShipMotorConfig> motors,
            ComponentLookup<ShipState> shipStates,
            ComponentLookup<MegaShipState> megas)
        {
            if (!motors.HasComponent(ship) || !shipStates.HasComponent(ship))
                return ShipMassLogic.MinMass;

            var motor = motors[ship];
            var ss = shipStates[ship];
            float baseMass = motor.Mass > 0f ? motor.Mass : ShipMassLogic.DefaultBaseMass;
            float mass = ShipMassLogic.ComputeRammingMass(
                motor.HullMassReference,
                ss.MaxHealth,
                motor.ChassisReferenceHealth,
                ss.CurrentGems,
                baseMass,
                ss.CurrentPeople);
            if (megas.HasComponent(ship) && megas[ship].IsMega)
                mass = math.max(mass, MegaShipCatalog.MinHullCollisionMass);
            return mass;
        }

        /// <summary>
        /// Wall-reflect off one asteroid (rock stays put). Skips dead / client-culled rocks so a
        /// leftover PhysX contact after the mesh hid cannot keep shoving the hull.
        /// MEGAs restore the pre-collision snapshot instead of bouncing (plow).
        /// </summary>
        /// <returns>True when this pair was a MEGA plow (caller may restore unconstrained pose).</returns>
        static bool ApplyShipVsAsteroid(
            ShipPhysicsContactElement pair,
            ref NativeHashMap<Entity, float3> working,
            ComponentLookup<ShipPreCollisionVelocity> snapshots,
            ComponentLookup<ShipState> shipStates,
            ComponentLookup<MegaShipState> megas,
            ComponentLookup<AsteroidState> asteroidStates,
            ComponentLookup<AsteroidClientCulledTag> culled,
            float restitution)
        {
            Entity ship = pair.Ship;
            Entity asteroid = pair.Other;
            if (!shipStates.HasComponent(ship) || shipStates[ship].IsDead)
                return false;
            if (!asteroidStates.HasComponent(asteroid))
                return false;
            var rock = asteroidStates[asteroid];
            // Dead / client-culled rocks must not bounce — PhysX can still emit events for a
            // stale static hull after the mesh hid (phantom grind).
            if (rock.IsDestroyed || !(rock.Health > 0.01f))
                return false;
            if (culled.HasComponent(asteroid))
                return false;

            bool isMega = MegaShipCatalog.PlowsAsteroids
                          && megas.HasComponent(ship)
                          && megas[ship].IsMega;
            float3 vShip = GetWorkingOrSnapshot(ship, ref working, snapshots);
            if (isMega)
            {
                // Keep pre-collision velocity so PhysX's inelastic stop cannot park the hull.
                working[ship] = vShip;
                return true;
            }

            ApplyWorldWallBounce(pair, ref working, snapshots, restitution);
            return false;
        }

        /// <summary>
        /// Shared wall bounce for asteroid / planet / moon / shield. Always writes the
        /// pre-collision (or bounced) velocity so PhysX depenetration speed cannot launch
        /// the hull (solver MaxDynamicDepenetrationVelocity is 25).
        /// </summary>
        static void ApplyWorldWallBounce(
            ShipPhysicsContactElement pair,
            ref NativeHashMap<Entity, float3> working,
            ComponentLookup<ShipPreCollisionVelocity> snapshots,
            float restitution)
        {
            Entity ship = pair.Ship;
            float3 vShip = GetWorkingOrSnapshot(ship, ref working, snapshots);
            ShipCollisionImpulseLogic.ApplyInfiniteMassWallImpulse(
                ref vShip, pair.NormalShipFromOther, restitution);
            working[ship] = vShip;
        }
    }
}
