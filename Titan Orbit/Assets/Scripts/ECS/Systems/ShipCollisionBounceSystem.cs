using TitanOrbit.Data;
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
    /// After Unity Physics exports contacts, applies mass-aware bounce from
    /// <see cref="ShipCollisionImpulseLogic"/> for asteroids (virtual rock mass) and
    /// planets/moons (infinite-mass wall). Ship↔ship is left to the Unity Physics solver
    /// (hull restitution / friction on the real <see cref="PhysicsCollider"/>).
    /// MEGA hulls plow asteroids: restore pre-collision motion (no bounce) so a field does
    /// not slow the ship. MEGA vs planet also restores pose — the covering sphere must not
    /// park the hull outside a small planet's orbit ring; capped keep-out runs after this.
    /// Server ram damage + client soft-destroy happen elsewhere.
    /// <para>
    /// Runs on ServerSimulation and ClientSimulation (predicted). Collision-event stream only —
    /// no asteroid/planet <c>ToEntityArray</c> (join-crash safe). Tangential grip stays in
    /// <see cref="ShipAsteroidContactFrictionSystem"/> which runs after this system.
    /// </para>
    /// Pipeline: Drive → Snapshot → PhysicsSimulation → Export → ContactCollect →
    /// Bounce (this) → Friction → Wrap → Planar → Kinematics.
    /// </summary>
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    [UpdateBefore(typeof(ShipAsteroidContactFrictionSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipCollisionBounceSystem : ISystem
    {
        /// <summary>Require the classified contact buffer from <see cref="ShipPhysicsContactCollectSystem"/>.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipPhysicsContactQueueTag>();
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

            // --- Designer asteroid bounce tuning ---
            var settings = AsteroidSettingsCache.ResolveOrDefault();
            settings.ClampValues();
            float asteroidMassPerSize = settings.CollisionMassPerSize;
            float asteroidRestitution = settings.BounceRestitution;

            if (!SystemAPI.TryGetSingletonBuffer<ShipPhysicsContactElement>(out var pairs) ||
                pairs.Length == 0)
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

            // Working velocities start from the pre-collision snapshot so multiple contacts
            // in one tick accumulate correctly without reading PhysX's inelastic result.
            var working = new NativeHashMap<Entity, float3>(math.max(8, pairs.Length * 2), Allocator.Temp);

            // MEGA asteroid plow / planet approach: restore unconstrained pose unless a
            // ship or moon contact this tick must keep PhysX / wall bounce. Two sets so
            // pair order cannot re-add a MEGA after a ship hit (or drop a plow after a
            // later planet pair). Capped planet keep-out runs in ToroidalWorldCollision.
            var megaUnconstrained = new NativeHashSet<Entity>(math.max(8, pairs.Length), Allocator.Temp);
            var megaKeepPhysX = new NativeHashSet<Entity>(math.max(8, pairs.Length), Allocator.Temp);

            // --- Deduplicate ship↔ship pairs (A,B) and (B,A) ---
            var seenShipPairs = new NativeHashSet<long>(pairs.Length, Allocator.Temp);

            for (int i = 0; i < pairs.Length; i++)
            {
                ShipPhysicsContactElement pair = pairs[i];

                if (pair.Kind == ShipPhysicsContactKind.Ship)
                {
                    long key = PackEntityPairKey(pair.Ship, pair.Other);
                    if (!seenShipPairs.Add(key))
                        continue;
                    // Unity Physics already bounced these hulls. Do not rewrite velocity
                    // (that felt like a magnet). Keep MEGA plow from undoing the solver pose.
                    megaKeepPhysX.Add(pair.Ship);
                    megaKeepPhysX.Add(pair.Other);
                }
                else if (pair.Kind == ShipPhysicsContactKind.Asteroid)
                {
                    bool plowed = ApplyShipVsAsteroid(
                        pair, ref working, snapshotLookup, motorLookup, shipStateLookup,
                        megaLookup, asteroidStateLookup, culledLookup, asteroidMassPerSize, asteroidRestitution);
                    if (plowed)
                        megaUnconstrained.Add(pair.Ship);
                }
                else if (pair.Kind == ShipPhysicsContactKind.Planet)
                {
                    if (IsTakingOffMoon(pair.Ship, moonDockLookup))
                        continue;

                    bool megaPlanet = megaLookup.HasComponent(pair.Ship)
                                      && megaLookup[pair.Ship].IsMega;
                    if (megaPlanet)
                    {
                        // Keep snapshot velocity — PhysX planet depenetration is undone below.
                        working[pair.Ship] = GetWorkingOrSnapshot(
                            pair.Ship, ref working, snapshotLookup);
                        megaUnconstrained.Add(pair.Ship);
                    }
                    else
                    {
                        ApplyShipVsInfiniteWall(pair, ref working, snapshotLookup);
                    }
                }
                else if (pair.Kind == ShipPhysicsContactKind.Moon)
                {
                    if (IsTakingOffMoon(pair.Ship, moonDockLookup))
                        continue;

                    ApplyShipVsInfiniteWall(pair, ref working, snapshotLookup);
                    megaKeepPhysX.Add(pair.Ship);
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
                    ShipPhysicsContactElement pair = pairs[i];
                    if (pair.Kind != ShipPhysicsContactKind.Asteroid)
                        continue;
                    if (!megaLookup.HasComponent(pair.Ship) || !megaLookup[pair.Ship].IsMega)
                        continue;
                    seenPlowRocks.Add(pair.Other);
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
            seenShipPairs.Dispose();
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
            ShipPhysicsContactElement pair,
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

            float mShip = GetShipCollisionMass(ship, motors, shipStates, megas);
            float mRock = ShipCollisionImpulseLogic.ComputeAsteroidCollisionMass(rock.Size, massPerSize);

            if (!ShipCollisionImpulseLogic.ApplyShipVsStaticMassiveImpulse(
                    ref vShip, pair.NormalShipFromOther, mShip, mRock, restitution))
                return false;

            working[ship] = vShip;
            return false;
        }

        static void ApplyShipVsInfiniteWall(
            ShipPhysicsContactElement pair,
            ref NativeHashMap<Entity, float3> working,
            ComponentLookup<ShipPreCollisionVelocity> snapshots)
        {
            Entity ship = pair.Ship;
            float3 vShip = GetWorkingOrSnapshot(ship, ref working, snapshots);
            if (!ShipCollisionImpulseLogic.ApplyInfiniteMassWallImpulse(
                    ref vShip, pair.NormalShipFromOther,
                    ShipCollisionImpulseLogic.DefaultInfiniteMassRestitution))
                return;
            working[ship] = vShip;
        }
    }
}
