using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Burst;
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
    /// After Unity Physics exports contacts, applies mass-aware normal bounce from
    /// <see cref="ShipCollisionImpulseLogic"/> using pre-physics velocity snapshots.
    /// Owns ship↔asteroid (finite virtual rock mass) and ship↔planet/moon (infinite-mass
    /// wall). Ship↔ship is <see cref="ShipShipHullContactSystem"/> (PhysX does not pair ships).
    /// MEGA hulls plow asteroids: restore pre-collision motion (no bounce) so a field does
    /// not slow the ship. MEGA vs planet also restores pose — the covering sphere must not
    /// park the hull outside a small planet's orbit ring; capped keep-out runs after this.
    /// Server ram damage + client soft-destroy happen elsewhere.
    /// <para>
    /// Runs on ServerSimulation and ClientSimulation (predicted). Collision-event stream only —
    /// no asteroid/planet <c>ToEntityArray</c> (join-crash safe). Tangential grip stays in
    /// <see cref="ShipAsteroidContactFrictionSystem"/> which runs after this system.
    /// </para>
    /// Pipeline: Drive → Snapshot → PhysicsSimulation → Export → Bounce (this) → Friction →
    /// Planar → Kinematics.
    /// </summary>
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    [UpdateBefore(typeof(ShipAsteroidContactFrictionSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipCollisionBounceSystem : ISystem
    {
        /// <summary>One classified contact pair collected from the PhysX collision-event stream.</summary>
        struct BouncePair
        {
            public Entity EntityA;
            public Entity EntityB;
            /// <summary>Unit normal from B toward A (XZ).</summary>
            public float3 NormalAFromB;
            /// <summary>0 = asteroid, 1 = other ship, 2 = planet, 3 = gem moon.</summary>
            public byte Kind;
        }

        const byte KindAsteroid = 0;
        const byte KindShip = 1;
        const byte KindPlanet = 2;
        const byte KindMoon = 3;

        /// <summary>Need at least one ship; collision events are optional.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipTag>();
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

            if (!SystemAPI.TryGetSingleton(out SimulationSingleton simSingleton))
            {
                foreach (var contact in SystemAPI
                             .Query<RefRW<ShipShipContactState>>()
                             .WithAll<ShipTag, Simulate>())
                    contact.ValueRW = default;
                return;
            }

            float fixedDt = SystemAPI.Time.DeltaTime;
            if (fixedDt <= 0f)
                fixedDt = 1f / 60f;

            // --- Designer asteroid bounce tuning ---
            var settings = AsteroidSettingsCache.ResolveOrDefault();
            settings.ClampValues();
            float asteroidMassPerSize = settings.CollisionMassPerSize;
            float asteroidRestitution = settings.BounceRestitution;

            // --- Collect pairs on the main thread (never ICollisionEventsJob) ---
            // Burst NativeStream.ForEachCount NREs on a disposed-but-IsCreated predicted stream.
            var events = new NativeList<CollisionEvent>(32, state.WorldUpdateAllocator);
            if (!PhysicsCollisionEventStream.TryCopyEvents(simSingleton, events))
            {
                events.Dispose();
                foreach (var contact in SystemAPI
                             .Query<RefRW<ShipShipContactState>>()
                             .WithAll<ShipTag, Simulate>())
                    contact.ValueRW = default;
                return;
            }

            var pairs = new NativeList<BouncePair>(32, state.WorldUpdateAllocator);
            // Compound hulls raise one event per child collider. One impulse per entity pair.
            var seenPairs = new NativeHashSet<long>(32, Allocator.Temp);
            var shipLookup = SystemAPI.GetComponentLookup<ShipTag>(true);
            var asteroidLookup = SystemAPI.GetComponentLookup<AsteroidTag>(true);
            var planetLookup = SystemAPI.GetComponentLookup<PlanetTag>(true);
            var moonLookup = SystemAPI.GetComponentLookup<PlanetGemMoonColliderTag>(true);
            for (int i = 0; i < events.Length; i++)
            {
                TryAddBouncePair(
                    events[i], pairs, seenPairs, shipLookup, asteroidLookup, planetLookup, moonLookup);
            }

            seenPairs.Dispose();

            events.Dispose();

            // Keep ship↔ship contact zero so drive never glues hulls (PhysX owns those pairs).
            foreach (var contact in SystemAPI
                         .Query<RefRW<ShipShipContactState>>()
                         .WithAll<ShipTag, Simulate>())
                contact.ValueRW = default;

            if (pairs.Length == 0)
                return;

            // --- Lookups for impulse resolve ---
            var snapshotLookup = SystemAPI.GetComponentLookup<ShipPreCollisionVelocity>(true);
            var motorLookup = SystemAPI.GetComponentLookup<ShipMotorConfig>(true);
            var shipStateLookup = SystemAPI.GetComponentLookup<ShipState>(true);
            var moonDockLookup = SystemAPI.GetComponentLookup<ShipMoonDockState>(true);
            var megaLookup = SystemAPI.GetComponentLookup<MegaShipState>(true);
            var asteroidStateLookup = SystemAPI.GetComponentLookup<AsteroidState>(true);
            var culledLookup = SystemAPI.GetComponentLookup<AsteroidClientCulledTag>(true);
            var velocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(false);
            var simulateLookup = SystemAPI.GetComponentLookup<Simulate>(true);
            bool clientWorld = state.World.IsClient();

            // Working velocities start from the pre-collision snapshot so multiple contacts
            // in one tick accumulate correctly without reading PhysX's inelastic result.
            var working = new NativeHashMap<Entity, float3>(math.max(8, pairs.Length * 2), Allocator.Temp);

            // MEGA asteroid plow / planet approach: restore unconstrained pose unless a
            // ship or moon contact this tick must keep PhysX / wall bounce. Two sets so
            // pair order cannot re-add a MEGA after a ship hit (or drop a plow after a
            // later planet pair). Capped planet keep-out runs in ToroidalWorldCollision.
            var megaUnconstrained = new NativeHashSet<Entity>(math.max(8, pairs.Length), Allocator.Temp);
            var megaKeepPhysX = new NativeHashSet<Entity>(math.max(8, pairs.Length), Allocator.Temp);

            for (int i = 0; i < pairs.Length; i++)
            {
                BouncePair pair = pairs[i];

                if (pair.Kind == KindShip)
                {
                    // Unity Physics already resolved this pair (hull restitution). A second
                    // snapshot impulse + inward motor reject is what made rams jitter / glue.
                    megaKeepPhysX.Add(pair.EntityA);
                    megaKeepPhysX.Add(pair.EntityB);
                }
                else if (pair.Kind == KindAsteroid)
                {
                    bool plowed = ApplyShipVsAsteroid(
                        pair, ref working, snapshotLookup, motorLookup, shipStateLookup,
                        megaLookup, asteroidStateLookup, culledLookup, asteroidMassPerSize, asteroidRestitution);
                    if (plowed)
                        megaUnconstrained.Add(pair.EntityA);
                }
                else if (pair.Kind == KindPlanet)
                {
                    if (IsTakingOffMoon(pair.EntityA, moonDockLookup))
                        continue;

                    bool megaPlanet = megaLookup.HasComponent(pair.EntityA)
                                      && megaLookup[pair.EntityA].IsMega;
                    if (megaPlanet)
                    {
                        // Keep snapshot velocity — PhysX planet depenetration is undone below.
                        working[pair.EntityA] = GetWorkingOrSnapshot(
                            pair.EntityA, ref working, snapshotLookup);
                        megaUnconstrained.Add(pair.EntityA);
                    }
                    else
                    {
                        ApplyShipVsInfiniteWall(pair, ref working, snapshotLookup);
                    }
                }
                else if (pair.Kind == KindMoon)
                {
                    if (IsTakingOffMoon(pair.EntityA, moonDockLookup))
                        continue;

                    ApplyShipVsInfiniteWall(pair, ref working, snapshotLookup);
                    megaKeepPhysX.Add(pair.EntityA);
                }
            }

            // --- Write PhysicsVelocity ---
            var written = working.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < written.Length; i++)
            {
                Entity e = written[i];
                if (!velocityLookup.HasComponent(e))
                    continue;
                // Interpolated remotes are not in predicted PhysX — do not poke their velocity
                // if a stale event somehow lists them (owner prediction writes the local hull).
                if (clientWorld &&
                    !(simulateLookup.HasComponent(e) && simulateLookup.IsComponentEnabled(e)))
                    continue;
                var pv = velocityLookup[e];
                pv.Linear = working[e];
                velocityLookup[e] = pv;
            }

            // --- MEGA plow / planet approach: undo PhysX depenetration ---
            // Reconstruct unconstrained pose from the pre-physics snapshot (drive already applied).
            if (megaUnconstrained.Count > 0)
            {
                var transformLookup = SystemAPI.GetComponentLookup<LocalTransform>(false);
                var plowShips = megaUnconstrained.ToNativeArray(Allocator.Temp);
                for (int i = 0; i < plowShips.Length; i++)
                {
                    Entity ship = plowShips[i];
                    if (megaKeepPhysX.Contains(ship))
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

                plowShips.Dispose();
            }

            // Client predicts the rock vanishing so the next physics step cannot pin the MEGA
            // while HitRpc is still in flight. Server authority + self-damage stay in ramming.
            // SoftDestroy strips PhysicsCollider (structural) and invalidates ComponentLookup —
            // collect rocks first, then teardown after megaLookup is no longer used.
            if (state.World.IsClient())
            {
                var seenPlowRocks = new NativeHashSet<Entity>(math.max(8, pairs.Length), Allocator.Temp);
                for (int i = 0; i < pairs.Length; i++)
                {
                    BouncePair pair = pairs[i];
                    if (pair.Kind != KindAsteroid)
                        continue;
                    if (!megaLookup.HasComponent(pair.EntityA) || !megaLookup[pair.EntityA].IsMega)
                        continue;
                    if (asteroidStateLookup.HasComponent(pair.EntityB))
                    {
                        var rock = asteroidStateLookup[pair.EntityB];
                        if (rock.IsDestroyed || !(rock.Health > 0.01f))
                            continue;
                    }
                    if (culledLookup.HasComponent(pair.EntityB))
                        continue;
                    seenPlowRocks.Add(pair.EntityB);
                }

                if (seenPlowRocks.Count > 0)
                {
                    var plowList = seenPlowRocks.ToNativeArray(Allocator.Temp);
                    for (int i = 0; i < plowList.Length; i++)
                    {
                        ClientLocalAsteroidCombatSync.SoftDestroyLocalAsteroidEntity(
                            state.EntityManager, plowList[i]);
                    }

                    plowList.Dispose();
                }

                seenPlowRocks.Dispose();
            }

            written.Dispose();
            working.Dispose();
            megaUnconstrained.Dispose();
            megaKeepPhysX.Dispose();
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
        /// True while forced moon takeoff owns the hull — planet/moon wall bounce would
        /// shove the ship back into the orbit sandwich.
        /// </summary>
        static bool IsTakingOffMoon(Entity ship, ComponentLookup<ShipMoonDockState> moonDock)
        {
            return moonDock.HasComponent(ship) && moonDock[ship].IsTakingOff;
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
        /// Mass-aware bounce off one asteroid. Skips dead / client-culled rocks so a leftover
        /// PhysX contact after the mesh hid cannot keep shoving the hull.
        /// MEGAs restore the pre-collision snapshot instead of bouncing (plow).
        /// </summary>
        /// <returns>True when this pair was a MEGA plow (caller may restore unconstrained pose).</returns>
        static bool ApplyShipVsAsteroid(
            BouncePair pair,
            ref NativeHashMap<Entity, float3> working,
            ComponentLookup<ShipPreCollisionVelocity> snapshots,
            ComponentLookup<ShipMotorConfig> motors,
            ComponentLookup<ShipState> shipStates,
            ComponentLookup<MegaShipState> megas,
            ComponentLookup<AsteroidState> asteroidStates,
            ComponentLookup<AsteroidClientCulledTag> culled,
            float massPerSize,
            float restitution)
        {
            // EntityA is the ship, EntityB the asteroid (collector normalizes this).
            Entity ship = pair.EntityA;
            Entity asteroid = pair.EntityB;
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

            float mShip = GetShipCollisionMass(ship, motors, shipStates, megas);
            float mRock = ShipCollisionImpulseLogic.ComputeAsteroidCollisionMass(rock.Size, massPerSize);

            if (!ShipCollisionImpulseLogic.ApplyShipVsStaticMassiveImpulse(
                    ref vShip, pair.NormalAFromB, mShip, mRock, restitution))
                return false;

            working[ship] = vShip;
            return false;
        }

        static void ApplyShipVsInfiniteWall(
            BouncePair pair,
            ref NativeHashMap<Entity, float3> working,
            ComponentLookup<ShipPreCollisionVelocity> snapshots)
        {
            Entity ship = pair.EntityA;
            float3 vShip = GetWorkingOrSnapshot(ship, ref working, snapshots);
            if (!ShipCollisionImpulseLogic.ApplyInfiniteMassWallImpulse(
                    ref vShip, pair.NormalAFromB,
                    ShipCollisionImpulseLogic.DefaultInfiniteMassRestitution))
                return;
            working[ship] = vShip;
        }

        /// <summary>
        /// Classifies one PhysX collision event into a bounce pair. Ship is always EntityA;
        /// NormalAFromB points from the other body toward the ship (or from B→A for ship↔ship).
        /// </summary>
        static void TryAddBouncePair(
            in CollisionEvent collisionEvent,
            NativeList<BouncePair> pairs,
            NativeHashSet<long> seenPairs,
            ComponentLookup<ShipTag> ships,
            ComponentLookup<AsteroidTag> asteroids,
            ComponentLookup<PlanetTag> planets,
            ComponentLookup<PlanetGemMoonColliderTag> moons)
        {
            Entity a = collisionEvent.EntityA;
            Entity b = collisionEvent.EntityB;
            float3 normalAFromB = collisionEvent.Normal;
            normalAFromB.y = 0f;
            if (math.lengthsq(normalAFromB) > 1e-8f)
                normalAFromB = math.normalize(normalAFromB);
            else
                normalAFromB = new float3(0f, 0f, 1f);

            bool aShip = ships.HasComponent(a);
            bool bShip = ships.HasComponent(b);

            if (aShip && bShip)
            {
                if (!seenPairs.Add(PackEntityPairKey(a, b)))
                    return;
                pairs.Add(new BouncePair
                {
                    EntityA = a,
                    EntityB = b,
                    NormalAFromB = normalAFromB,
                    Kind = KindShip,
                });
                return;
            }

            Entity ship;
            Entity other;
            float3 normalShipFromOther;
            if (aShip)
            {
                ship = a;
                other = b;
                normalShipFromOther = normalAFromB;
            }
            else if (bShip)
            {
                ship = b;
                other = a;
                normalShipFromOther = -normalAFromB;
            }
            else
            {
                return;
            }

            byte kind;
            if (asteroids.HasComponent(other))
                kind = KindAsteroid;
            else if (planets.HasComponent(other))
                kind = KindPlanet;
            else if (moons.HasComponent(other))
                kind = KindMoon;
            else
                return;

            if (!seenPairs.Add(PackEntityPairKey(ship, other)))
                return;

            pairs.Add(new BouncePair
            {
                EntityA = ship,
                EntityB = other,
                NormalAFromB = normalShipFromOther,
                Kind = kind,
            });
        }

    }
}
