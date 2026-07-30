using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Physics.Systems;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// After Unity Physics exports contacts, applies mass-aware normal bounce from
    /// <see cref="ShipCollisionImpulseLogic"/> using pre-physics velocity snapshots.
    /// Owns ship↔asteroid (finite virtual rock mass), ship↔ship (energy transfer), and
    /// ship↔planet/moon (infinite-mass wall) so PhysX material restitution can stay 0.
    /// <para>
    /// Runs on ServerSimulation and ClientSimulation (predicted). Collision-event stream only —
    /// no asteroid/planet <c>ToEntityArray</c> (join-crash safe). Tangential grip stays in
    /// <see cref="ShipAsteroidContactFrictionSystem"/> which runs after this system.
    /// </para>
    /// Pipeline: Drive → Snapshot → PhysicsSimulation → Export → Bounce (this) → Friction → Toroidal.
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
            /// <summary>0 = asteroid, 1 = other ship, 2 = infinite-mass world (planet/moon).</summary>
            public byte Kind;
        }

        const byte KindAsteroid = 0;
        const byte KindShip = 1;
        const byte KindInfiniteWall = 2;

        /// <summary>Require physics simulation + world for collision events.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimulationSingleton>();
            state.RequireForUpdate<PhysicsWorldSingleton>();
        }

        /// <summary>
        /// Collects collision events, then applies impulses from pre-collision snapshots into
        /// <see cref="PhysicsVelocity"/>. Safe to write velocities here (post-Export).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Join-crash gate (client only) ---
            // [TITAN-ORBIT] ShipTag lookups during TeamChoice Instantiates Crash!!! —
            // Settling OFF but GhostSpawnBacklog ON. Server always bounces.
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            // --- Designer asteroid bounce tuning ---
            var settings = AsteroidSettingsCache.ResolveOrDefault();
            settings.ClampValues();
            float asteroidMassPerSize = settings.CollisionMassPerSize;
            float asteroidRestitution = settings.BounceRestitution;

            // --- Collect pairs (Burst job) ---
            var pairs = new NativeList<BouncePair>(32, state.WorldUpdateAllocator);
            var shipLookup = SystemAPI.GetComponentLookup<ShipTag>(true);
            var asteroidLookup = SystemAPI.GetComponentLookup<AsteroidTag>(true);
            var planetLookup = SystemAPI.GetComponentLookup<PlanetTag>(true);
            var moonLookup = SystemAPI.GetComponentLookup<PlanetGemMoonColliderTag>(true);

            state.Dependency = new CollectBouncePairsJob
            {
                Pairs = pairs,
                Ships = shipLookup,
                Asteroids = asteroidLookup,
                Planets = planetLookup,
                Moons = moonLookup,
            }.Schedule(SystemAPI.GetSingleton<SimulationSingleton>(), state.Dependency);
            state.Dependency.Complete();

            if (pairs.Length == 0)
                return;

            // --- Lookups for impulse resolve ---
            var snapshotLookup = SystemAPI.GetComponentLookup<ShipPreCollisionVelocity>(true);
            var motorLookup = SystemAPI.GetComponentLookup<ShipMotorConfig>(true);
            var shipStateLookup = SystemAPI.GetComponentLookup<ShipState>(true);
            var asteroidStateLookup = SystemAPI.GetComponentLookup<AsteroidState>(true);
            var velocityLookup = SystemAPI.GetComponentLookup<PhysicsVelocity>(false);

            // Working velocities start from the pre-collision snapshot so multiple contacts
            // in one tick accumulate correctly without reading PhysX's inelastic result.
            var working = new NativeHashMap<Entity, float3>(math.max(8, pairs.Length * 2), Allocator.Temp);

            // --- Deduplicate ship↔ship pairs (A,B) and (B,A) ---
            var seenShipPairs = new NativeHashSet<long>(pairs.Length, Allocator.Temp);

            for (int i = 0; i < pairs.Length; i++)
            {
                BouncePair pair = pairs[i];

                if (pair.Kind == KindShip)
                {
                    long key = PackEntityPairKey(pair.EntityA, pair.EntityB);
                    if (!seenShipPairs.Add(key))
                        continue;
                    ApplyShipVsShip(pair, ref working, snapshotLookup, motorLookup, shipStateLookup);
                }
                else if (pair.Kind == KindAsteroid)
                {
                    ApplyShipVsAsteroid(
                        pair, ref working, snapshotLookup, motorLookup, shipStateLookup,
                        asteroidStateLookup, asteroidMassPerSize, asteroidRestitution);
                }
                else if (pair.Kind == KindInfiniteWall)
                {
                    ApplyShipVsInfiniteWall(pair, ref working, snapshotLookup);
                }
            }

            // --- Write PhysicsVelocity ---
            var written = working.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < written.Length; i++)
            {
                Entity e = written[i];
                if (!velocityLookup.HasComponent(e))
                    continue;
                var pv = velocityLookup[e];
                pv.Linear = working[e];
                velocityLookup[e] = pv;
            }

            written.Dispose();
            working.Dispose();
            seenShipPairs.Dispose();
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
            ComponentLookup<ShipState> shipStates)
        {
            if (!motors.HasComponent(ship) || !shipStates.HasComponent(ship))
                return ShipMassLogic.MinMass;

            var motor = motors[ship];
            var ss = shipStates[ship];
            float baseMass = motor.Mass > 0f ? motor.Mass : ShipMassLogic.DefaultBaseMass;
            return ShipMassLogic.ComputeRammingMass(
                motor.HullMassReference,
                ss.MaxHealth,
                motor.ChassisReferenceHealth,
                ss.CurrentGems,
                baseMass,
                ss.CurrentPeople);
        }

        static void ApplyShipVsShip(
            BouncePair pair,
            ref NativeHashMap<Entity, float3> working,
            ComponentLookup<ShipPreCollisionVelocity> snapshots,
            ComponentLookup<ShipMotorConfig> motors,
            ComponentLookup<ShipState> shipStates)
        {
            Entity a = pair.EntityA;
            Entity b = pair.EntityB;
            if (!shipStates.HasComponent(a) || !shipStates.HasComponent(b))
                return;
            if (shipStates[a].IsDead || shipStates[b].IsDead)
                return;

            float3 vA = GetWorkingOrSnapshot(a, ref working, snapshots);
            float3 vB = GetWorkingOrSnapshot(b, ref working, snapshots);
            float mA = GetShipCollisionMass(a, motors, shipStates);
            float mB = GetShipCollisionMass(b, motors, shipStates);

            if (!ShipCollisionImpulseLogic.ApplyTwoBodyImpulse(
                    ref vA, ref vB, pair.NormalAFromB, mA, mB,
                    ShipCollisionImpulseLogic.DefaultShipShipRestitution))
                return;

            working[a] = vA;
            working[b] = vB;
        }

        static void ApplyShipVsAsteroid(
            BouncePair pair,
            ref NativeHashMap<Entity, float3> working,
            ComponentLookup<ShipPreCollisionVelocity> snapshots,
            ComponentLookup<ShipMotorConfig> motors,
            ComponentLookup<ShipState> shipStates,
            ComponentLookup<AsteroidState> asteroidStates,
            float massPerSize,
            float restitution)
        {
            // EntityA is the ship, EntityB the asteroid (collector normalizes this).
            Entity ship = pair.EntityA;
            Entity asteroid = pair.EntityB;
            if (!shipStates.HasComponent(ship) || shipStates[ship].IsDead)
                return;
            if (!asteroidStates.HasComponent(asteroid))
                return;
            var rock = asteroidStates[asteroid];
            if (rock.IsDestroyed || rock.Health <= 0f)
                return;

            float3 vShip = GetWorkingOrSnapshot(ship, ref working, snapshots);
            float mShip = GetShipCollisionMass(ship, motors, shipStates);
            float mRock = ShipCollisionImpulseLogic.ComputeAsteroidCollisionMass(rock.Size, massPerSize);

            if (!ShipCollisionImpulseLogic.ApplyShipVsStaticMassiveImpulse(
                    ref vShip, pair.NormalAFromB, mShip, mRock, restitution))
                return;

            working[ship] = vShip;
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
        /// Classifies PhysX collision events into bounce pairs. Ship is always EntityA in the
        /// stored pair; NormalAFromB points from the other body toward the ship (or from B→A
        /// for ship↔ship using the event's native orientation).
        /// </summary>
        [BurstCompile]
        struct CollectBouncePairsJob : ICollisionEventsJob
        {
            public NativeList<BouncePair> Pairs;
            [ReadOnly] public ComponentLookup<ShipTag> Ships;
            [ReadOnly] public ComponentLookup<AsteroidTag> Asteroids;
            [ReadOnly] public ComponentLookup<PlanetTag> Planets;
            [ReadOnly] public ComponentLookup<PlanetGemMoonColliderTag> Moons;

            /// <summary>One solver contact pair this tick.</summary>
            public void Execute(CollisionEvent collisionEvent)
            {
                Entity a = collisionEvent.EntityA;
                Entity b = collisionEvent.EntityB;
                float3 normalAFromB = collisionEvent.Normal;
                normalAFromB.y = 0f;
                if (math.lengthsq(normalAFromB) > 1e-8f)
                    normalAFromB = math.normalize(normalAFromB);
                else
                    normalAFromB = new float3(0f, 0f, 1f);

                bool aShip = Ships.HasComponent(a);
                bool bShip = Ships.HasComponent(b);

                // --- Ship ↔ ship ---
                if (aShip && bShip)
                {
                    Pairs.Add(new BouncePair
                    {
                        EntityA = a,
                        EntityB = b,
                        NormalAFromB = normalAFromB,
                        Kind = KindShip,
                    });
                    return;
                }

                // --- Ship ↔ asteroid / planet / moon ---
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
                    // Event normal is A←B; flip so it points from other toward ship.
                    normalShipFromOther = -normalAFromB;
                }
                else
                {
                    return;
                }

                byte kind;
                if (Asteroids.HasComponent(other))
                    kind = KindAsteroid;
                else if (Planets.HasComponent(other) || Moons.HasComponent(other))
                    kind = KindInfiniteWall;
                else
                    return;

                Pairs.Add(new BouncePair
                {
                    EntityA = ship,
                    EntityB = other,
                    NormalAFromB = normalShipFromOther,
                    Kind = kind,
                });
            }
        }
    }
}
