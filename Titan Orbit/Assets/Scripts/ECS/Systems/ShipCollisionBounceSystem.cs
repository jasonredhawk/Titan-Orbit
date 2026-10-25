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
    /// After Unity Physics exports contacts, applies world-body bounce from
    /// <see cref="ShipCollisionImpulseLogic"/> and <see cref="AsteroidSettings.BounceRestitution"/>.
    /// Asteroids, planets, moons, and moon shields use the wall reflect.
    /// Ship-vs-ship is not solved here. <see cref="ShipDeterministicTickSystem"/> already
    /// integrated the hull and applied the mass-weighted impulse. A second rewrite
    /// would fight that tick. MEGA plow stays here. A regular hull that
    /// deals more damage than the asteroid has left does not reflect: into-rock speed drops
    /// by remainingHealth / collisionDamage (5 HP vs 20 damage keeps 75% of that speed)
    /// and the pre-collision pose is restored so depenetration cannot pop the hull back.
    /// A rock that survives the hit still uses the wall reflect. Planets and moons
    /// use the same wall bounce as regular ships — undoing PhysX depenetration there
    /// let Titans tunnel through the body. Friendly shields are off via
    /// <see cref="TitanOrbitPhysicsLayers.ShipForTeam"/>. Flying ships that PhysX
    /// exports from the moon rock onto the concentric shield rim get snapshot pose
    /// restore. Dock / takeoff skip bounce and restore pose so PhysX cannot add a
    /// second shove.
    /// Server ram damage + client soft-destroy happen elsewhere.
    /// <para>
    /// Runs on ServerSimulation and ClientSimulation (predicted). Collision-event stream only —
    /// no asteroid/planet <c>ToEntityArray</c> (join-crash safe). Tangential grip stays in
    /// <see cref="ShipAsteroidContactFrictionSystem"/> which runs after this system.
    /// </para>
    /// Pipeline: Drive → Snapshot → PhysicsSimulation → Export → ContactCollect →
    /// Bounce (this) → Friction → Wrap → Planar → Kinematics.
    /// Ships use one covering box; rocks / planets / moons / shields are spheres.
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
        NativeHashSet<long> _seenAsteroidPairs;
        NativeHashSet<long> _overkillPairs;
        NativeHashSet<Entity> _overkillRocks;
        /// <summary>Asteroid wall hits whose PhysX pose must be eased, not published as a teleport.</summary>
        NativeHashSet<Entity> _asteroidEase;
        NativeHashMap<long, double> _impactRearmUntil;
        NativeHashSet<Entity> _shipShipTouched;
        NativeList<ShipShipContactOutbound> _contactOutbound;
        EntityQuery _slideLatchQuery;
        /// <summary>Strongest local impact this tick. Presentation reads it once.</summary>
        float3 _pendingBumpNormal;
        float _pendingBumpClosing;
        /// <summary>Local simulated hulls tested against interpolated ships this tick.</summary>
        NativeList<Entity> _clientLocals;
        NativeList<float> _clientLocalRadii;

        /// <summary>Require the classified contact buffer from <see cref="ShipPhysicsContactCollectSystem"/>.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipPhysicsContactQueueTag>();
            _working = new NativeHashMap<Entity, float3>(32, Allocator.Persistent);
            _megaUnconstrained = new NativeHashSet<Entity>(16, Allocator.Persistent);
            _megaKeepPhysX = new NativeHashSet<Entity>(16, Allocator.Persistent);
            _seenShipPairs = new NativeHashSet<long>(16, Allocator.Persistent);
            _seenPlowRocks = new NativeHashSet<Entity>(16, Allocator.Persistent);
            _seenAsteroidPairs = new NativeHashSet<long>(16, Allocator.Persistent);
            _overkillPairs = new NativeHashSet<long>(16, Allocator.Persistent);
            _overkillRocks = new NativeHashSet<Entity>(16, Allocator.Persistent);
            _asteroidEase = new NativeHashSet<Entity>(16, Allocator.Persistent);
            _impactRearmUntil = new NativeHashMap<long, double>(32, Allocator.Persistent);
            _shipShipTouched = new NativeHashSet<Entity>(16, Allocator.Persistent);
            _contactOutbound = new NativeList<ShipShipContactOutbound>(8, Allocator.Persistent);
            _clientLocals = new NativeList<Entity>(4, Allocator.Persistent);
            _clientLocalRadii = new NativeList<float>(4, Allocator.Persistent);
            _slideLatchQuery = state.GetEntityQuery(ComponentType.ReadWrite<ShipShipSlideLatch>());
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
            if (_seenAsteroidPairs.IsCreated)
                _seenAsteroidPairs.Dispose();
            if (_overkillPairs.IsCreated)
                _overkillPairs.Dispose();
            if (_overkillRocks.IsCreated)
                _overkillRocks.Dispose();
            if (_asteroidEase.IsCreated)
                _asteroidEase.Dispose();
            if (_impactRearmUntil.IsCreated)
                _impactRearmUntil.Dispose();
            if (_shipShipTouched.IsCreated)
                _shipShipTouched.Dispose();
            if (_contactOutbound.IsCreated)
                _contactOutbound.Dispose();
            if (_clientLocals.IsCreated)
                _clientLocals.Dispose();
            if (_clientLocalRadii.IsCreated)
                _clientLocalRadii.Dispose();
        }

        /// <summary>
        /// Collects collision events, then applies impulses from pre-collision snapshots into
        /// <see cref="PhysicsVelocity"/>. Safe to write velocities here (post-Export).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            _pendingBumpClosing = 0f;
            _pendingBumpNormal = float3.zero;

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
            var loadoutLookup = SystemAPI.GetComponentLookup<ShipLoadoutState>(true);
            var territoryLookup = SystemAPI.GetComponentLookup<ShipTerritoryBoostLatch>(true);
            var contactLookup = SystemAPI.GetComponentLookup<ShipAsteroidContactState>(false);
            var velocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(false);
            var kinematicsLookup = SystemAPI.GetComponentLookup<ShipKinematics>(false);
            var transformLookup = SystemAPI.GetComponentLookup<LocalTransform>(false);
            var latchLookup = SystemAPI.GetComponentLookup<ShipShipSlideLatch>(false);
            bool isClient = state.World.IsClient();

            EnsureSetCapacity(ref _shipShipTouched, math.max(16, pairs.Length * 2));
            _shipShipTouched.Clear();
            _contactOutbound.Clear();

            // Working velocities start from the pre-collision snapshot so multiple contacts
            // in one tick accumulate correctly without reading PhysX's inelastic result.
            // Persistent scratch — grind emits contacts every predicted tick (Allocator.Temp
            // here used to allocate every physics step while pinned on a rock).
            int pairCap = math.max(8, pairs.Length * 2);
            EnsureMapCapacity(ref _working, pairCap);
            EnsureSetCapacity(ref _megaUnconstrained, pairCap);
            EnsureSetCapacity(ref _megaKeepPhysX, pairCap);
            EnsureSetCapacity(ref _seenShipPairs, math.max(8, pairs.Length));
            EnsureSetCapacity(ref _seenAsteroidPairs, math.max(8, pairs.Length));
            EnsureSetCapacity(ref _overkillPairs, math.max(8, pairs.Length));
            EnsureSetCapacity(ref _overkillRocks, math.max(8, pairs.Length));
            EnsureSetCapacity(ref _asteroidEase, math.max(8, pairs.Length));
            _working.Clear();
            _megaUnconstrained.Clear();
            _megaKeepPhysX.Clear();
            _seenShipPairs.Clear();
            _seenAsteroidPairs.Clear();
            _overkillPairs.Clear();
            _overkillRocks.Clear();
            _asteroidEase.Clear();
            double now = SystemAPI.Time.ElapsedTime;
            PruneImpactRearm(now);

            for (int i = 0; i < pairs.Length; i++)
            {
                ShipPhysicsContactElement pair = pairs[i];

                if (pair.Kind == ShipPhysicsContactKind.Ship)
                {
                    // Ship-vs-ship velocity and separation already ran in
                    // ShipDeterministicTickSystem. Do not apply a second impulse.
                    continue;
                }
                else if (pair.Kind == ShipPhysicsContactKind.Asteroid)
                {
                    // ShipDeterministicTickSystem already reflected and separated this pair.
                    // Easing the PhysX pose here rewrote the ghost snapshot and snapped the
                    // predicting client back onto a pose the kinematic tick never wrote.
                    continue;
                }
                else if (pair.Kind == ShipPhysicsContactKind.Planet)
                {
                    if (ShouldSkipMoonWorldBounce(pair.Ship, moonDockLookup))
                    {
                        // Takeoff / landed own the pose — undo PhysX depenetration too.
                        RestoreUnconstrainedPose(
                            pair.Ship, ref _working, snapshotLookup, _megaUnconstrained);
                        continue;
                    }

                    ApplyWorldWallBounce(pair, ref _working, snapshotLookup, restitution);
                }
                else if (pair.Kind == ShipPhysicsContactKind.Moon
                         || pair.Kind == ShipPhysicsContactKind.Shield)
                {
                    bool dockOwnsPose = ShouldSkipMoonWorldBounce(pair.Ship, moonDockLookup);
                    bool friendlyMoonWorld = IsFriendlyMoonWorldContact(
                        pair, shipStateLookup, shieldPlanetLookup, planetStateLookup);

                    // Takeoff/landed: restore pose so attach cannot fight a second shove.
                    // Friendly shield: pass-through includes pose (not just bounce skip).
                    // Moon rock (Titan or regular) bounces — restoring pose here tunneled
                    // Titans through the body.
                    if (dockOwnsPose
                        || (pair.Kind == ShipPhysicsContactKind.Shield && friendlyMoonWorld))
                    {
                        RestoreUnconstrainedPose(
                            pair.Ship, ref _working, snapshotLookup, _megaUnconstrained);
                        continue;
                    }

                    ApplyWorldWallBounce(pair, ref _working, snapshotLookup, restitution);
                }
            }

            // --- Write the wall bounce onto ghosted flight speed ---
            // PhysicsVelocity stays zero so the solver cannot integrate the hull again.
            foreach (var kv in _working)
            {
                Entity e = kv.Key;
                float3 bounced = kv.Value;
                bounced.y = 0f;
                if (kinematicsLookup.HasComponent(e))
                {
                    var kin = kinematicsLookup[e];
                    kin.Velocity = bounced;
                    kinematicsLookup[e] = kin;
                }

                if (!velocityLookup.HasComponent(e))
                    continue;
                var pv = velocityLookup[e];
                pv.Linear = float3.zero;
                pv.Angular = float3.zero;
                velocityLookup[e] = pv;
            }

            // --- Undo PhysX depenetration (MEGA plow / overkill punch-through / dock-owned pose) ---
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

            // Asteroid wall: PhysX depenetration and contact spin are one-tick teleports.
            // The ghost snapshot is this pose. Clients interpolate position and rotation,
            // then extrapolation corrects when the next sample is metres away or facing
            // the other way — that is the blink. Ease the separation and keep the
            // pre-collision heading (the motor slerp) so both axes stay continuous.
            if (_asteroidEase.Count > 0)
            {
                const float MaxSeparationStep = 0.75f;
                foreach (Entity ship in _asteroidEase)
                {
                    if (_megaUnconstrained.Contains(ship))
                        continue;
                    if (!snapshotLookup.HasComponent(ship) || !transformLookup.HasComponent(ship))
                        continue;

                    var snap = snapshotLookup[ship];
                    float3 bounced = _working.TryGetValue(ship, out float3 worked) ? worked : snap.Linear;
                    bounced.y = 0f;
                    float3 continuous = snap.Position + bounced * fixedDt;
                    continuous.y = 0f;

                    var lt = transformLookup[ship];
                    float3 popped = lt.Position;
                    popped.y = 0f;
                    float3 delta = popped - continuous;
                    float distSq = math.lengthsq(delta);
                    if (distSq > 1e-8f)
                    {
                        float dist = math.sqrt(distSq);
                        float maxStep = math.max(MaxSeparationStep, math.length(bounced) * fixedDt);
                        continuous += delta * (math.min(dist, maxStep) / dist);
                    }

                    continuous.y = 0f;
                    lt.Position = continuous;
                    lt.Rotation = snap.Rotation;
                    transformLookup[ship] = lt;

                    if (velocityLookup.HasComponent(ship))
                    {
                        var pv = velocityLookup[ship];
                        pv.Angular = float3.zero;
                        velocityLookup[ship] = pv;
                    }
                }
            }

            // Flying friendly ships: PhysX can export a 4-unit snap from the moon rock
            // onto the concentric shield / orbit-zone rim. Restore unconstrained pose;
            // bounced velocity already written above stays.
            UndoFriendlyMoonShieldRimSnaps(ref state, fixedDt);

            ReleaseStaleSlideLatches(latchLookup);

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

                // Predicted overkill: hide the rock this tick so the next physics step cannot
                // bounce the hull again while HitRpc is still in flight. Server kill, gems,
                // and self-damage stay in ramming.
                foreach (Entity rock in _overkillRocks)
                {
                    ClientLocalAsteroidCombatSync.SoftDestroyLocalAsteroidEntity(
                        state.EntityManager, rock);
                }
            }

            if (!isClient)
                FlushContactSamples(ref state);
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
        /// Friendly moon rock or shield. Pair-disable is the solver gate; this is the
        /// gameplay backup so a leftover event cannot bounce or yeet an ally.
        /// </summary>
        static bool IsFriendlyMoonWorldContact(
            in ShipPhysicsContactElement pair,
            ComponentLookup<ShipState> ships,
            ComponentLookup<PlanetGemMoonColliderPlanetRef> moonWorldPlanets,
            ComponentLookup<PlanetState> planets)
        {
            if (!ships.HasComponent(pair.Ship) || !moonWorldPlanets.HasComponent(pair.Other))
                return false;
            Entity planet = moonWorldPlanets[pair.Other].PlanetEntity;
            if (!planets.HasComponent(planet))
                return false;
            return PlanetGemMoonCombatLogic.IsTeamFriendlyToMoon(
                planets[planet].Ownership, ships[pair.Ship].Team);
        }

        /// <summary>
        /// Keep snapshot velocity and queue pose restore so PhysX depenetration cannot
        /// launch the hull off the drive/takeoff path.
        /// </summary>
        static void RestoreUnconstrainedPose(
            Entity ship,
            ref NativeHashMap<Entity, float3> working,
            ComponentLookup<ShipPreCollisionVelocity> snapshots,
            NativeHashSet<Entity> unconstrained)
        {
            working[ship] = GetWorkingOrSnapshot(ship, ref working, snapshots);
            unconstrained.Add(ship);
        }

        /// <summary>
        /// Client-only ship touch against interpolated hulls. The solver does not pair
        /// those hulls, so this is a distance test: one local ship against each remote,
        /// and only a velocity impulse. No position write. mapW/mapH from
        /// <see cref="ToroidalMapEcs"/>.
        /// </summary>
        void ApplyClientRemoteShipTouches(
            ref SystemState state,
            ref NativeHashMap<Entity, float3> working,
            ref NativeList<ShipShipContactOutbound> outbound,
            ref NativeList<Entity> locals,
            ref NativeList<float> localRadii,
            ComponentLookup<ShipPreCollisionVelocity> snapshots,
            ComponentLookup<PhysicsVelocity> velocities,
            ComponentLookup<ShipKinematics> kinematics,
            ComponentLookup<ShipMotorConfig> motors,
            ComponentLookup<ShipState> shipStates,
            ComponentLookup<MegaShipState> megas,
            ComponentLookup<LocalTransform> transforms,
            ComponentLookup<PhysicsCollider> colliders,
            ComponentLookup<ShipPlanarHullRadius> hullRadii,
            ComponentLookup<ShipShipSlideLatch> latches,
            ComponentLookup<GhostOwner> owners,
            ComponentLookup<GhostOwnerIsLocal> localOwners,
            ComponentLookup<LocalPlayerShipTag> localPlayers,
            ref float3 pendingBumpNormal,
            ref float pendingBumpClosing,
            bool hasContactSamples,
            DynamicBuffer<ShipShipContactSampleElement> contactSamples,
            float restitution)
        {
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return;

            const float BroadPhase = 80f;
            float broadSq = BroadPhase * BroadPhase;
            locals.Clear();
            localRadii.Clear();
            foreach (var (localTransform, entity) in SystemAPI.Query<RefRO<LocalTransform>>()
                         .WithAll<ShipTag, Simulate, PhysicsVelocity>()
                         .WithEntityAccess())
            {
                if (shipStates.HasComponent(entity) && shipStates[entity].IsDead)
                    continue;
                if (megas.HasComponent(entity) && megas[entity].IsMega)
                    continue;
                locals.Add(entity);
                localRadii.Add(PlanarHullRadius(
                    entity, math.max(0.01f, localTransform.ValueRO.Scale), hullRadii, colliders));
            }

            if (locals.Length == 0)
                return;

            foreach (var (remoteTransform, remoteEntity) in SystemAPI.Query<RefRO<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithNone<PhysicsVelocity, Prefab>()
                         .WithEntityAccess())
            {
                if (shipStates.HasComponent(remoteEntity) && shipStates[remoteEntity].IsDead)
                    continue;

                float remoteScale = math.max(0.01f, remoteTransform.ValueRO.Scale);
                float remoteRadius = PlanarHullRadius(remoteEntity, remoteScale, hullRadii, colliders);
                float3 remotePos = remoteTransform.ValueRO.Position;
                for (int i = 0; i < locals.Length; i++)
                {
                    Entity local = locals[i];
                    if (!transforms.HasComponent(local))
                        continue;
                    var localLt = transforms[local];
                    float3 offset = ToroidalMapEcs.ShortestOffsetXZ(remotePos, localLt.Position, mapW, mapH);
                    if (math.lengthsq(offset) > broadSq)
                        continue;
                    float reach = localRadii[i] + remoteRadius;
                    if (math.lengthsq(offset) > reach * reach)
                        continue;
                    float dist = math.length(offset);
                    float3 normal = dist > 1e-4f ? offset / dist : new float3(1f, 0f, 0f);
                    _shipShipTouched.Add(local);
                    _shipShipTouched.Add(remoteEntity);
                    ApplyShipVsShip(
                        new ShipPhysicsContactElement
                        {
                            Ship = local,
                            Other = remoteEntity,
                            NormalShipFromOther = normal,
                            Kind = ShipPhysicsContactKind.Ship,
                        },
                        ref working, ref outbound, snapshots, velocities, kinematics, motors,
                        shipStates, megas, latches, owners, localOwners, localPlayers,
                        ref pendingBumpNormal, ref pendingBumpClosing, true, hasContactSamples,
                        contactSamples, restitution);
                }
            }
        }

        /// <summary>World XZ radius. Uses the cached unscaled hull when the client pair pass stored one.</summary>
        static float PlanarHullRadius(
            Entity ship,
            float scale,
            ComponentLookup<ShipPlanarHullRadius> hullRadii,
            ComponentLookup<PhysicsCollider> colliders)
        {
            if (hullRadii.HasComponent(ship))
            {
                float unscaled = hullRadii[ship].Unscaled;
                if (unscaled > 0.01f)
                    return unscaled * scale;
            }

            if (colliders.HasComponent(ship))
                return ShipToroidalWorldCollisionLogic.GetShipCollisionRadiusWorld(colliders[ship], scale);
            return BodyCollisionMath.GetShipHullRadiusWorld(scale);
        }

        /// <summary>
        /// Ship↔ship response shared by the server and the predicting client.
        /// Both worlds rewrite velocity with the same impulse. Neither writes a
        /// positional shove. The server queues one <see cref="ShipShipContactSampleRpc"/>
        /// per overlap so resim can use the other hull's velocity and ramming mass.
        /// </summary>
        static void ApplyShipVsShip(
            ShipPhysicsContactElement pair,
            ref NativeHashMap<Entity, float3> working,
            ref NativeList<ShipShipContactOutbound> outbound,
            ComponentLookup<ShipPreCollisionVelocity> snapshots,
            ComponentLookup<PhysicsVelocity> velocities,
            ComponentLookup<ShipKinematics> kinematics,
            ComponentLookup<ShipMotorConfig> motors,
            ComponentLookup<ShipState> shipStates,
            ComponentLookup<MegaShipState> megas,
            ComponentLookup<ShipShipSlideLatch> latches,
            ComponentLookup<GhostOwner> owners,
            ComponentLookup<GhostOwnerIsLocal> localOwners,
            ComponentLookup<LocalPlayerShipTag> localPlayers,
            ref float3 pendingBumpNormal,
            ref float pendingBumpClosing,
            bool isClient,
            bool hasContactSamples,
            DynamicBuffer<ShipShipContactSampleElement> contactSamples,
            float restitution)
        {
            bool shipHasVel = velocities.HasComponent(pair.Ship);
            bool otherHasVel = velocities.HasComponent(pair.Other);
            if (!shipHasVel && !otherHasVel)
                return;

            // Client MEGA vs a static interpolated hull: leave the solver pose (plow).
            if (isClient && shipHasVel != otherHasVel)
            {
                Entity local = shipHasVel ? pair.Ship : pair.Other;
                if (megas.HasComponent(local) && megas[local].IsMega)
                    return;
            }

            if (shipStates.HasComponent(pair.Ship) && shipStates[pair.Ship].IsDead)
                return;
            if (shipStates.HasComponent(pair.Other) && shipStates[pair.Other].IsDead)
                return;

            bool remoteContact = shipHasVel != otherHasVel;
            Entity dynamicHull = shipHasVel ? pair.Ship : pair.Other;

            float ramShip = GetShipCollisionMass(pair.Ship, motors, shipStates, megas);
            float ramOther = GetShipCollisionMass(pair.Other, motors, shipStates, megas);
            float moveShip = GetShipMovementMass(pair.Ship, motors, shipStates, megas);
            float moveOther = GetShipMovementMass(pair.Other, motors, shipStates, megas);

            float3 velShip = shipHasVel
                ? GetWorkingOrSnapshot(pair.Ship, ref working, snapshots)
                : GhostVelocity(pair.Ship, kinematics);
            float3 velOther = otherHasVel
                ? GetWorkingOrSnapshot(pair.Other, ref working, snapshots)
                : GhostVelocity(pair.Other, kinematics);

            // Server sample replaces the lagged ghost for the impulse only.
            if (isClient && hasContactSamples && remoteContact)
            {
                Entity remote = shipHasVel ? pair.Other : pair.Ship;
                if (owners.HasComponent(remote)
                    && TryContactSample(contactSamples, owners[remote].NetworkId, out var sample))
                {
                    if (shipHasVel)
                    {
                        velOther = sample.OtherVelocity;
                        ramOther = sample.OtherRammingMass;
                    }
                    else
                    {
                        velShip = sample.OtherVelocity;
                        ramShip = sample.OtherRammingMass;
                    }
                }
            }

            var resolved = ShipCollisionImpulseLogic.ResolveShipShipContact(
                velShip,
                velOther,
                pair.NormalShipFromOther,
                ramShip,
                ramOther,
                restitution);
            if (!resolved.IsValid)
                return;

            if (shipHasVel)
                working[pair.Ship] = resolved.VelocityA;
            if (otherHasVel)
                working[pair.Other] = resolved.VelocityB;

            bool sampleSent = SampleAlreadySent(pair.Ship, pair.Other, latches)
                              || SampleAlreadySent(pair.Other, pair.Ship, latches);
            if (!sampleSent)
            {
                MarkSampleSent(pair.Ship, pair.Other, latches);
                MarkSampleSent(pair.Other, pair.Ship, latches);
                if (!isClient)
                {
                    QueueContactSamples(
                        pair, owners, velShip, velOther, ramShip, ramOther, moveShip, moveOther, ref outbound);
                }
            }

            if (!isClient || !remoteContact || !resolved.IsImpact)
                return;
            if (!IsLocalPredicted(dynamicHull, localOwners, localPlayers))
                return;
            if (DisplayAlreadyNoted(dynamicHull, shipHasVel ? pair.Other : pair.Ship, latches))
                return;

            MarkDisplayNoted(dynamicHull, shipHasVel ? pair.Other : pair.Ship, latches);
            if (resolved.ClosingSpeed > pendingBumpClosing)
            {
                float3 n = shipHasVel ? pair.NormalShipFromOther : -pair.NormalShipFromOther;
                n.y = 0f;
                pendingBumpNormal = n;
                pendingBumpClosing = resolved.ClosingSpeed;
            }
        }

        /// <summary>True when this ghost is the connection-owned predicted ship.</summary>
        static bool IsLocalPredicted(
            Entity ship,
            ComponentLookup<GhostOwnerIsLocal> localOwners,
            ComponentLookup<LocalPlayerShipTag> localPlayers)
        {
            if (localOwners.HasComponent(ship) && localOwners.IsComponentEnabled(ship))
                return true;
            return localPlayers.HasComponent(ship);
        }

        static bool SampleAlreadySent(Entity ship, Entity other, ComponentLookup<ShipShipSlideLatch> latches)
        {
            if (!latches.HasComponent(ship))
                return false;
            var latch = latches[ship];
            return latch.SampleSent != 0 && latch.Other == other;
        }

        static void MarkSampleSent(Entity ship, Entity other, ComponentLookup<ShipShipSlideLatch> latches)
        {
            if (!latches.HasComponent(ship))
                return;
            var latch = latches[ship];
            if (latch.Other != other)
                latch.DisplayNoted = 0;
            latch.Other = other;
            latch.SampleSent = 1;
            latches[ship] = latch;
        }

        static bool DisplayAlreadyNoted(Entity ship, Entity other, ComponentLookup<ShipShipSlideLatch> latches)
        {
            if (!latches.HasComponent(ship))
                return false;
            var latch = latches[ship];
            return latch.DisplayNoted != 0 && latch.Other == other;
        }

        static void MarkDisplayNoted(Entity ship, Entity other, ComponentLookup<ShipShipSlideLatch> latches)
        {
            if (!latches.HasComponent(ship))
                return;
            var latch = latches[ship];
            latch.Other = other;
            latch.DisplayNoted = 1;
            latches[ship] = latch;
        }

        static bool TryContactSample(
            DynamicBuffer<ShipShipContactSampleElement> samples,
            int otherNetworkId,
            out ShipShipContactSampleElement sample)
        {
            for (int i = samples.Length - 1; i >= 0; i--)
            {
                if (samples[i].OtherNetworkId != otherNetworkId)
                    continue;
                sample = samples[i];
                return sample.OtherMovementMass > 0f;
            }

            sample = default;
            return false;
        }

        static void QueueContactSamples(
            ShipPhysicsContactElement pair,
            ComponentLookup<GhostOwner> owners,
            float3 velShip,
            float3 velOther,
            float ramShip,
            float ramOther,
            float moveShip,
            float moveOther,
            ref NativeList<ShipShipContactOutbound> outbound)
        {
            QueueOneSample(pair.Ship, pair.Other, velOther, ramOther, moveOther, owners, ref outbound);
            QueueOneSample(pair.Other, pair.Ship, velShip, ramShip, moveShip, owners, ref outbound);
        }

        static void QueueOneSample(
            Entity owner,
            Entity other,
            float3 otherVelocity,
            float otherRammingMass,
            float otherMovementMass,
            ComponentLookup<GhostOwner> owners,
            ref NativeList<ShipShipContactOutbound> outbound)
        {
            if (!owners.HasComponent(owner) || !owners.HasComponent(other))
                return;
            int ownerId = owners[owner].NetworkId;
            int otherId = owners[other].NetworkId;
            if (ownerId <= 0 || otherId == 0)
                return;
            otherVelocity.y = 0f;
            outbound.Add(new ShipShipContactOutbound
            {
                OwnerNetworkId = ownerId,
                OtherNetworkId = otherId,
                OtherVelocity = otherVelocity,
                OtherMovementMass = otherMovementMass,
                OtherRammingMass = otherRammingMass,
            });
        }

        void ReleaseStaleSlideLatches(ComponentLookup<ShipShipSlideLatch> latches)
        {
            if (_slideLatchQuery.IsEmpty)
                return;
            using var entities = _slideLatchQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                Entity entity = entities[i];
                if (_shipShipTouched.Contains(entity) || !latches.HasComponent(entity))
                    continue;
                var latch = latches[entity];
                if (latch.SampleSent == 0 && latch.DisplayNoted == 0 && latch.Other == Entity.Null)
                    continue;
                latches[entity] = default;
            }
        }

        void FlushContactSamples(ref SystemState state)
        {
            if (_contactOutbound.Length == 0)
                return;

            var em = state.EntityManager;
            for (int i = 0; i < _contactOutbound.Length; i++)
            {
                ShipShipContactOutbound row = _contactOutbound[i];
                Entity connection = FindOwnerConnection(ref state, row.OwnerNetworkId);
                if (connection == Entity.Null)
                    continue;

                Entity rpc = em.CreateEntity();
                em.AddComponentData(rpc, new ShipShipContactSampleRpc
                {
                    OtherNetworkId = row.OtherNetworkId,
                    OtherVelocity = row.OtherVelocity,
                    OtherMovementMass = row.OtherMovementMass,
                    OtherRammingMass = row.OtherRammingMass,
                });
                em.AddComponentData(rpc, new SendRpcCommandRequest { TargetConnection = connection });
            }
        }

        Entity FindOwnerConnection(ref SystemState state, int networkId)
        {
            if (networkId <= 0)
                return Entity.Null;
            foreach (var (id, entity) in SystemAPI.Query<RefRO<NetworkId>>()
                         .WithAll<NetworkStreamInGame>()
                         .WithEntityAccess())
            {
                if (id.ValueRO.Value == networkId)
                    return entity;
            }

            return Entity.Null;
        }

        /// <summary>Quantized ghost velocity for an interpolated remote. Zero when the field is absent.</summary>
        static float3 GhostVelocity(Entity ship, ComponentLookup<ShipKinematics> kinematics)
        {
            if (!kinematics.HasComponent(ship))
                return float3.zero;
            float3 velocity = kinematics[ship].Velocity;
            velocity.y = 0f;
            return velocity;
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
        /// Movement mass the physics solver used for positional correction
        /// (<see cref="ShipPhysicsMassSyncSystem"/>). Ramming mass is only the bounce impulse.
        /// </summary>
        static float GetShipMovementMass(
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
            float mass = ShipMassLogic.ComputeMovementMass(
                motor.HullMassReference,
                ss.MaxHealth,
                motor.ChassisReferenceHealth,
                ss.CurrentGems,
                baseMass,
                ss.CurrentPeople);
            if (megas.HasComponent(ship) && megas[ship].IsMega)
                mass = math.max(mass, MegaShipCatalog.DefaultHullCollisionMass);
            return math.max(ShipMassLogic.MinMass, mass);
        }

        enum AsteroidContactResult : byte
        {
            Ignored = 0,
            MegaPlow = 1,
            Wall = 2,
            Overkill = 3,
        }

        /// <summary>
        /// Drop expired impact-rearm keys so the map stays bounded. Checked lazily on read;
        /// this only runs once the map is large enough to matter.
        /// </summary>
        void PruneImpactRearm(double now)
        {
            if (!_impactRearmUntil.IsCreated || _impactRearmUntil.Count <= 48)
                return;

            var kept = new NativeHashMap<long, double>(_impactRearmUntil.Capacity, Allocator.Temp);
            foreach (var kv in _impactRearmUntil)
            {
                if (kv.Value > now)
                    kept.TryAdd(kv.Key, kv.Value);
            }

            _impactRearmUntil.Clear();
            foreach (var kv in kept)
                _impactRearmUntil.TryAdd(kv.Key, kv.Value);
            kept.Dispose();
        }

        /// <summary>
        /// Extend this pair's "not a new impact" hold. A real impact uses the ram rearm
        /// window. Any other contact only covers a few missed physics events (ram's
        /// sticky pair), and never shortens a longer hold already in the map.
        /// </summary>
        static void TouchImpactRearm(
            ref NativeHashMap<long, double> map,
            long key,
            bool newImpact,
            double now,
            float fixedDt)
        {
            double hold = newImpact
                ? now + ShipRammingCollisionDamageSystem.ImpactRearmSeconds
                : now + 4.0 * math.max(1e-4f, fixedDt);
            if (map.TryGetValue(key, out double existing) && existing > hold)
                hold = existing;
            SetImpactRearm(ref map, key, hold);
        }

        /// <summary>Insert or replace one ship↔asteroid impact-rearm deadline.</summary>
        static void SetImpactRearm(ref NativeHashMap<long, double> map, long key, double until)
        {
            if (map.ContainsKey(key))
            {
                map[key] = until;
                return;
            }

            if (map.Capacity < map.Count + 1)
            {
                var grown = new NativeHashMap<long, double>(
                    math.max(map.Count + 1, map.Capacity * 2), Allocator.Persistent);
                foreach (var kv in map)
                    grown.TryAdd(kv.Key, kv.Value);
                map.Dispose();
                map = grown;
            }

            map.TryAdd(key, until);
        }

        /// <summary>
        /// Planar speed from the pre-collision snapshot (OVERDRIVE included). Matches ram
        /// impact HP, which does not use the post-solve velocity.
        /// </summary>
        static float ReadSnapshotPlanarSpeed(
            Entity ship,
            ComponentLookup<ShipPreCollisionVelocity> snapshots)
        {
            if (!snapshots.HasComponent(ship))
                return 0f;
            float3 pre = snapshots[ship].Linear;
            return math.length(new float2(pre.x, pre.z));
        }

        /// <summary>
        /// Wall-reflect off one asteroid (rock stays put). Skips dead / client-culled rocks so a
        /// leftover PhysX contact after the mesh hid cannot keep shoving the hull.
        /// MEGAs restore the pre-collision snapshot instead of bouncing (plow).
        /// A new impact whose damage exceeds remaining health punches through: into-rock speed
        /// drops by health/damage instead of reflecting.
        /// </summary>
        static AsteroidContactResult ApplyShipVsAsteroid(
            ShipPhysicsContactElement pair,
            ref NativeHashMap<Entity, float3> working,
            ComponentLookup<ShipPreCollisionVelocity> snapshots,
            ComponentLookup<ShipState> shipStates,
            ComponentLookup<MegaShipState> megas,
            ComponentLookup<AsteroidState> asteroidStates,
            ComponentLookup<AsteroidClientCulledTag> culled,
            float restitution,
            ComponentLookup<ShipMotorConfig> motors,
            ComponentLookup<ShipLoadoutState> loadouts,
            ComponentLookup<ShipTerritoryBoostLatch> territory,
            ComponentLookup<ShipAsteroidContactState> contacts,
            ref NativeHashMap<long, double> impactRearmUntil,
            ref NativeHashSet<long> seenPairs,
            ref NativeHashSet<long> overkillPairs,
            double now,
            float fixedDt)
        {
            Entity ship = pair.Ship;
            Entity asteroid = pair.Other;
            if (!shipStates.HasComponent(ship) || shipStates[ship].IsDead)
                return AsteroidContactResult.Ignored;
            if (!asteroidStates.HasComponent(asteroid))
                return AsteroidContactResult.Ignored;
            var rock = asteroidStates[asteroid];
            // Dead / client-culled rocks must not bounce — PhysX can still emit events for a
            // stale static hull after the mesh hid (phantom grind).
            if (rock.IsDestroyed || !(rock.Health > 0.01f))
                return AsteroidContactResult.Ignored;
            if (culled.HasComponent(asteroid))
                return AsteroidContactResult.Ignored;

            bool isMega = MegaShipCatalog.PlowsAsteroids
                          && megas.HasComponent(ship)
                          && megas[ship].IsMega;
            long key = PackEntityPairKey(ship, asteroid);
            // First manifold this tick owns the impact decision (same as ram's isNewContact).
            // Later manifolds must not reflect a hull that is already punching through.
            if (!seenPairs.Add(key))
            {
                if (isMega)
                    return AsteroidContactResult.MegaPlow;
                // Later manifold of a pair ram already counted. Keep the sticky hold alive.
                TouchImpactRearm(ref impactRearmUntil, key, newImpact: false, now, fixedDt);
                if (overkillPairs.Contains(key))
                    return AsteroidContactResult.Overkill;
                ApplyWorldWallBounce(pair, ref working, snapshots, restitution);
                return AsteroidContactResult.Wall;
            }

            float3 vShip = GetWorkingOrSnapshot(ship, ref working, snapshots);
            if (isMega)
            {
                // Keep pre-collision velocity so PhysX's inelastic stop cannot park the hull.
                working[ship] = vShip;
                return AsteroidContactResult.MegaPlow;
            }

            bool wasInContact = contacts.HasComponent(ship) && contacts[ship].InContact != 0;
            bool inRearm = impactRearmUntil.TryGetValue(key, out double rearmUntil) && now < rearmUntil;
            bool newImpact = !wasInContact
                             && !inRearm
                             && pair.ClosingSpeed >= ShipRammingCollisionDamageSystem.ImpactMinClosingSpeed;
            // Ram keeps the pair through a few missed events and, after a real impact, for
            // ImpactRearmSeconds. Refresh that hold on every contact so a one-tick gap cannot
            // punch through a rock the server will not damage again.
            TouchImpactRearm(ref impactRearmUntil, key, newImpact, now, fixedDt);

            if (newImpact
                && TryAsteroidOverkillReduction(
                    ship, rock.Health, motors, shipStates, loadouts, territory, snapshots,
                    out float speedReduction))
            {
                // Write even when the hull was not closing along this normal so the solver
                // stop cannot park it on a rock this hit destroys.
                ShipCollisionImpulseLogic.ApplyForwardSpeedReduction(
                    ref vShip, pair.NormalShipFromOther, speedReduction);
                working[ship] = vShip;
                overkillPairs.Add(key);
                return AsteroidContactResult.Overkill;
            }

            ApplyWorldWallBounce(pair, ref working, snapshots, restitution);
            return AsteroidContactResult.Wall;
        }

        /// <summary>
        /// True when this hit's ram damage exceeds the asteroid's current health.
        /// <paramref name="speedReduction"/> is health / damage (the fraction of into-rock
        /// speed to remove). Health ≥ damage keeps the full wall bounce.
        /// </summary>
        static bool TryAsteroidOverkillReduction(
            Entity ship,
            float asteroidHealth,
            ComponentLookup<ShipMotorConfig> motors,
            ComponentLookup<ShipState> shipStates,
            ComponentLookup<ShipLoadoutState> loadouts,
            ComponentLookup<ShipTerritoryBoostLatch> territory,
            ComponentLookup<ShipPreCollisionVelocity> snapshots,
            out float speedReduction)
        {
            speedReduction = 0f;
            if (!motors.HasComponent(ship) || !shipStates.HasComponent(ship))
                return false;

            var shipState = shipStates[ship];
            var motor = motors[ship];
            float bankMul = 1f;
            if (loadouts.HasComponent(ship))
            {
                int bank = BulletBankFireResolve.ResolveFireBankIndex(loadouts[ship]);
                bankMul = BulletBankCombatLogic.GetRammingPowerMultiplier(bank);
            }

            float territoryMult = 1f;
            if (territory.HasComponent(ship) && territory[ship].LatchedMult > 1f)
                territoryMult = territory[ship].LatchedMult;

            float damage = ShipRammingCollisionDamageSystem.ComputeAsteroidImpactDamage(
                in shipState,
                in motor,
                ReadSnapshotPlanarSpeed(ship, snapshots),
                territoryMult,
                bankMul);
            if (!(damage > 0.01f) || !(asteroidHealth < damage))
                return false;

            speedReduction = asteroidHealth / damage;
            return true;
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

    struct ShipShipContactOutbound
    {
        public int OwnerNetworkId;
        public int OtherNetworkId;
        public float3 OtherVelocity;
        public float OtherMovementMass;
        public float OtherRammingMass;
    }

    /// <summary>
    /// Copies <see cref="ShipShipContactSampleRpc"/> into a client buffer before prediction
    /// so resim can apply the same velocity impulse. The sample does not move the hull.
    /// One message per new contact, not per tick.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(PredictedSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipShipContactSampleClientSystem : ISystem
    {
        const int MaxSamples = 8;

        public void OnCreate(ref SystemState state)
        {
            var entity = state.EntityManager.CreateEntity();
            state.EntityManager.AddComponentData(entity, new ShipShipContactSampleQueueTag());
            state.EntityManager.AddBuffer<ShipShipContactSampleElement>(entity);
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonBuffer<ShipShipContactSampleElement>(out var samples))
                return;

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (rpc, entity) in SystemAPI
                         .Query<RefRO<ShipShipContactSampleRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                var row = rpc.ValueRO;
                int slot = -1;
                for (int i = 0; i < samples.Length; i++)
                {
                    if (samples[i].OtherNetworkId != row.OtherNetworkId)
                        continue;
                    slot = i;
                    break;
                }

                var stored = new ShipShipContactSampleElement
                {
                    OtherNetworkId = row.OtherNetworkId,
                    OtherVelocity = row.OtherVelocity,
                    OtherMovementMass = row.OtherMovementMass,
                    OtherRammingMass = row.OtherRammingMass,
                };
                if (slot >= 0)
                    samples[slot] = stored;
                else
                {
                    if (samples.Length >= MaxSamples)
                        samples.RemoveAt(0);
                    samples.Add(stored);
                }

                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
