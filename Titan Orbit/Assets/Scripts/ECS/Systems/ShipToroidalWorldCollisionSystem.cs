using TitanOrbit;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Predicted ship↔planet / asteroid / gem-moon / ship bounce across toroidal map seams.
    /// Runs after Unity Physics integrates hulls and before
    /// <see cref="ShipPlanarPhysicsConstraintSystem"/> flattens tilt. Same math on
    /// ServerSimulation and ClientSimulation (<see cref="Simulate"/>) so NetCode prediction
    /// matches authority — except the Windows client under
    /// <see cref="ClientJoinSettleCache.TransformQuarantine"/> skips the obstacle gather
    /// (full planet/asteroid queries Crash!!! after TeamChoice; Player.log 2026-07-22).
    /// Moons use per-ship <see cref="PlanetOrbitMath.GetMoonWorldPositionNear"/> when the hull
    /// is on a different tile than the canonical kinematic collider — treating the canonical
    /// moon as a toroidal obstacle falsely looked like center-overlap and shoved ships every
    /// tick (stepped orbit / post-dock snap toward the original tile).
    /// Presentation still draws bodies via <c>ToroidalDisplay</c>; this system
    /// only adjusts ship <see cref="LocalTransform"/> / <see cref="PhysicsVelocity"/>.
    /// Pipeline: Drive → Physics → Bounce → Friction → ToroidalWorldCollision (this) → Planar →
    /// KinematicsSync.
    /// </summary>
    // OrderLast: after default-slot PhysicsSystemGroup. Avoid UpdateAfter(PhysicsSystemGroup) —
    // ClientWorld sorter warns when that group is not a PredictedFixedStep sibling.
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderLast = true)]
    [UpdateBefore(typeof(ShipPlanarPhysicsConstraintSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipToroidalWorldCollisionSystem : ISystem
    {
        /// <summary>One static/kinematic world sphere used as an obstacle for ship resolve.</summary>
        struct WorldSphere
        {
            /// <summary>Sim center (logical / unbounded — not display-tiled).</summary>
            public float3 Position;

            /// <summary>World-space collision radius.</summary>
            public float Radius;

            /// <summary>Obstacle entity (asteroid or planet) — used for server ramming queue.</summary>
            public Entity Entity;

            /// <summary>1 when this sphere is a living asteroid (ramming damage); 0 for planets.</summary>
            public byte IsAsteroid;

            /// <summary>Designer Size for asteroid virtual collision mass (0 for planets).</summary>
            public float AsteroidSize;
        }

        /// <summary>One simulated ship snapshot for cross-seam ship↔ship resolve.</summary>
        struct ShipSphere
        {
            public Entity Entity;
            public float3 Position;
            public float3 Velocity;
            public float Radius;
            public float CollisionMass;
            public bool Dirty;
        }

        /// <summary>
        /// Caches that at least one ship exists before we allocate obstacle lists.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            // [ECS/DOTS] Ships are the only dynamic side of this resolve; world bodies are static/kinematic.
            state.RequireForUpdate<ShipTag>();
        }

        /// <summary>
        /// Collects world spheres once, then resolves each simulated living ship against them
        /// with toroidal math when Unity Physics cannot see the contact (different map tile).
        /// Also resolves cross-seam ship↔ship pairs with two-body mass-aware impulse.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Client late-join safety (map-body gathers) ---
            // [TITAN-ORBIT] RequireForUpdate<ShipTag> means this system first runs when the
            // TeamChoice ship Instantiates — Settling is already OFF (JoinSettleCompleted).
            // Planet/asteroid/moon foreach below is a full map gather. Player.log 2026-07-22:
            // TeamChoiceResult → Crash!!! in Burst. Use ShouldSkipMapBodyQueries (quarantine
            // session-long OR Settling). Must use IsClient() — Local Host shares the static
            // cache with the server world, and the server must keep seam resolve.
            // Under quarantine the client relies on same-tile PhysX + server authority for seams.
            // See titan-orbit-teamchoice-crash-hardstop.mdc.
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return;

            // --- Map size ---
            float preferredW = 0f;
            float preferredH = 0f;
            if (SystemAPI.TryGetSingleton(out MapStateSingleton mapState) &&
                ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight))
            {
                preferredW = mapState.MapWidth;
                preferredH = mapState.MapHeight;
            }

            if (!ToroidalMapEcs.ResolveMapSize(preferredW, preferredH, out float mapW, out float mapH))
                return;

            // --- Designer asteroid bounce mass / restitution ---
            var asteroidSettings = TitanOrbit.Data.AsteroidSettingsCache.ResolveOrDefault();
            asteroidSettings.ClampValues();
            float asteroidFriction = asteroidSettings.Friction;
            float asteroidMassPerSize = asteroidSettings.CollisionMassPerSize;
            float asteroidBounceRestitution = asteroidSettings.BounceRestitution;

            float fixedDt = SystemAPI.Time.DeltaTime;
            if (fixedDt <= 0f)
                fixedDt = 1f / 60f;

            // --- Gather obstacles (no nested SystemAPI.Query) ---
            var obstacles = new NativeList<WorldSphere>(128, Allocator.Temp);

            foreach (var (planetTransform, planetEntity) in SystemAPI
                         .Query<RefRO<LocalTransform>>()
                         .WithAll<PlanetTag>()
                         .WithEntityAccess())
            {
                obstacles.Add(new WorldSphere
                {
                    Position = planetTransform.ValueRO.Position,
                    Radius = BodyCollisionMath.GetPlanetBodyRadiusWorld(planetTransform.ValueRO.Scale),
                    Entity = planetEntity,
                    IsAsteroid = 0,
                    AsteroidSize = 0f,
                });
            }

            // --- Asteroids (skip dead / client-culled ghosts) ---
            foreach (var (asteroidTransform, asteroidState, entity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<AsteroidState>>()
                         .WithAll<AsteroidTag>()
                         .WithEntityAccess())
            {
                if (asteroidState.ValueRO.IsDestroyed || asteroidState.ValueRO.Health <= 0f)
                    continue;
                if (state.EntityManager.HasComponent<AsteroidClientCulledTag>(entity))
                    continue;
                if (TitanOrbitDebugFlags.IsolateDisableAsteroidShipCollision)
                    continue;

                obstacles.Add(new WorldSphere
                {
                    Position = asteroidTransform.ValueRO.Position,
                    Radius = BodyCollisionMath.GetAsteroidBodyRadiusWorld(asteroidTransform.ValueRO.Scale),
                    Entity = entity,
                    IsAsteroid = 1,
                    AsteroidSize = math.max(0.01f, asteroidState.ValueRO.Size),
                });
            }

            // --- Gem-moon snapshots ---
            var moons = new NativeList<MoonObstacle>(16, Allocator.Temp);
            double elapsed = 0.0;
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            if (SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime))
                elapsed = PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false);
            else
                elapsed = state.World.Time.ElapsedTime;

            foreach (var (moonTransform, planetRef) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<PlanetGemMoonColliderPlanetRef>>()
                         .WithAll<PlanetGemMoonColliderTag>())
            {
                Entity planetEntity = planetRef.ValueRO.PlanetEntity;
                if (!state.EntityManager.Exists(planetEntity) ||
                    !state.EntityManager.HasComponent<PlanetState>(planetEntity) ||
                    !state.EntityManager.HasComponent<LocalTransform>(planetEntity))
                    continue;

                var planetStateData = state.EntityManager.GetComponentData<PlanetState>(planetEntity);
                var planetLt = state.EntityManager.GetComponentData<LocalTransform>(planetEntity);
                float planetScale = math.max(0.25f, planetLt.Scale);

                moons.Add(new MoonObstacle
                {
                    CanonicalPosition = moonTransform.ValueRO.Position,
                    PlanetPosition = planetLt.Position,
                    PlanetScale = planetScale,
                    PlanetLevel = planetStateData.PlanetLevel,
                    PlanetId = planetStateData.PlanetId,
                    Radius = PlanetGemMoonMath.GetMoonBodyRadiusWorld(
                        planetScale, planetStateData.IsHomePlanet),
                });
            }

            // --- Ship snapshots for world + ship↔ship seam resolve ---
            var ships = new NativeList<ShipSphere>(16, Allocator.Temp);
            foreach (var (transform, velocity, physicsCollider, shipState, motor, shipEntity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<PhysicsVelocity>, RefRO<PhysicsCollider>,
                             RefRO<ShipState>, RefRO<ShipMotorConfig>>()
                         .WithAll<ShipTag, Simulate>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                // Prefer current PhysicsVelocity (post same-tile bounce/friction when those ran).
                // Seam-only contacts never hit PhysX, so this is still the post-drive velocity.
                float3 lin = velocity.ValueRO.Linear;
                float baseMass = motor.ValueRO.Mass > 0f ? motor.ValueRO.Mass : ShipMassLogic.DefaultBaseMass;
                float collisionMass = ShipMassLogic.ComputeRammingMass(
                    motor.ValueRO.HullMassReference,
                    shipState.ValueRO.MaxHealth,
                    motor.ValueRO.ChassisReferenceHealth,
                    shipState.ValueRO.CurrentGems,
                    baseMass,
                    shipState.ValueRO.CurrentPeople);

                ships.Add(new ShipSphere
                {
                    Entity = shipEntity,
                    Position = transform.ValueRO.Position,
                    Velocity = lin,
                    Radius = ShipToroidalWorldCollisionLogic.GetShipCollisionRadiusWorld(
                        physicsCollider.ValueRO, transform.ValueRO.Scale),
                    CollisionMass = collisionMass,
                    Dirty = false,
                });
            }

            if (obstacles.Length == 0 && moons.Length == 0 && ships.Length == 0)
            {
                obstacles.Dispose();
                moons.Dispose();
                ships.Dispose();
                return;
            }

            DynamicBuffer<PendingRamContactElement> ramQueue = default;
            bool enqueueRam = state.World.IsServer() &&
                              SystemAPI.TryGetSingletonBuffer(out ramQueue);

            // --- Resolve each ship vs world spheres / moons ---
            for (int s = 0; s < ships.Length; s++)
            {
                ShipSphere ship = ships[s];
                float3 shipPos = ship.Position;
                float3 shipVel = ship.Velocity;
                bool anyHit = false;

                for (int i = 0; i < obstacles.Length; i++)
                {
                    WorldSphere body = obstacles[i];
                    float3 posBefore = shipPos;
                    float3 velBefore = shipVel;
                    float bodyFriction = body.IsAsteroid != 0 ? asteroidFriction : 0f;
                    float bodyMass = body.IsAsteroid != 0
                        ? ShipCollisionImpulseLogic.ComputeAsteroidCollisionMass(
                            body.AsteroidSize, asteroidMassPerSize)
                        : 0f;
                    float restitution = body.IsAsteroid != 0
                        ? asteroidBounceRestitution
                        : ShipToroidalWorldCollisionLogic.WorldRestitution;

                    if (ShipToroidalWorldCollisionLogic.TryResolveShipVsWorldSphere(
                            ref shipPos, ref shipVel, ship.Radius,
                            body.Position, body.Radius,
                            mapW, mapH, restitution,
                            bodyFriction, fixedDt,
                            ship.CollisionMass, bodyMass))
                    {
                        anyHit = true;

                        if (enqueueRam && body.IsAsteroid != 0 && body.Entity != Entity.Null)
                        {
                            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(posBefore, body.Position, mapW, mapH);
                            float dist = math.length(offset);
                            float3 outward = dist > 1e-5f
                                ? offset / dist
                                : new float3(0f, 0f, 1f);
                            float3 planarVel = new float3(velBefore.x, 0f, velBefore.z);
                            float closing = math.max(0f, -math.dot(planarVel, outward));
                            ramQueue.Add(new PendingRamContactElement
                            {
                                Ship = ship.Entity,
                                Other = body.Entity,
                                OtherIsShip = 0,
                                NormalShipFromOther = outward,
                                ClosingSpeed = closing,
                                EstimatedImpulse = closing * 10f,
                            });
                        }
                    }
                }

                for (int i = 0; i < moons.Length; i++)
                {
                    MoonObstacle moon = moons[i];
                    if (!ShipToroidalWorldCollisionLogic.NeedsToroidalResolve(
                            shipPos, moon.CanonicalPosition, mapW, mapH))
                        continue;

                    float3 moonNear = PlanetOrbitMath.GetMoonWorldPositionNear(
                        shipPos,
                        moon.PlanetPosition,
                        moon.PlanetScale,
                        moon.PlanetLevel,
                        moon.PlanetId,
                        elapsed,
                        mapW,
                        mapH);
                    if (ShipToroidalWorldCollisionLogic.TryResolveShipVsNearWorldSphere(
                            ref shipPos, ref shipVel, ship.Radius,
                            moonNear, moon.Radius,
                            ShipToroidalWorldCollisionLogic.WorldRestitution))
                    {
                        anyHit = true;
                    }
                }

                if (!anyHit)
                    continue;

                ship.Position = shipPos;
                ship.Velocity = shipVel;
                ship.Dirty = true;
                ships[s] = ship;
            }

            // --- Cross-seam ship↔ship (PhysX misses different-tile pairs) ---
            for (int i = 0; i < ships.Length; i++)
            {
                for (int j = i + 1; j < ships.Length; j++)
                {
                    ShipSphere a = ships[i];
                    ShipSphere b = ships[j];
                    float3 posA = a.Position;
                    float3 velA = a.Velocity;
                    float3 posB = b.Position;
                    float3 velB = b.Velocity;

                    if (!ShipToroidalWorldCollisionLogic.TryResolveShipVsShip(
                            ref posA, ref velA, a.Radius, a.CollisionMass,
                            ref posB, ref velB, b.Radius, b.CollisionMass,
                            mapW, mapH,
                            ShipCollisionImpulseLogic.DefaultShipShipRestitution))
                        continue;

                    a.Position = posA;
                    a.Velocity = velA;
                    a.Dirty = true;
                    b.Position = posB;
                    b.Velocity = velB;
                    b.Dirty = true;
                    ships[i] = a;
                    ships[j] = b;
                }
            }

            // --- Write back dirty ships ---
            for (int s = 0; s < ships.Length; s++)
            {
                ShipSphere ship = ships[s];
                if (!ship.Dirty)
                    continue;

                var lt = state.EntityManager.GetComponentData<LocalTransform>(ship.Entity);
                lt.Position = ship.Position;
                state.EntityManager.SetComponentData(ship.Entity, lt);

                var pv = state.EntityManager.GetComponentData<PhysicsVelocity>(ship.Entity);
                pv.Linear = ship.Velocity;
                state.EntityManager.SetComponentData(ship.Entity, pv);
            }

            obstacles.Dispose();
            moons.Dispose();
            ships.Dispose();
        }

        /// <summary>
        /// Per-planet gem-moon obstacle: canonical collider pose plus data to rebuild a Near copy
        /// for each ship on a different map tile.
        /// </summary>
        struct MoonObstacle
        {
            /// <summary>Kinematic collider LocalTransform (canonical tile).</summary>
            public float3 CanonicalPosition;

            /// <summary>Parent planet logical position (canonical).</summary>
            public float3 PlanetPosition;

            /// <summary>Planet uniform scale (world radius proxy).</summary>
            public float PlanetScale;

            /// <summary>Planet level (ring radii API).</summary>
            public int PlanetLevel;

            /// <summary>Planet id — seeds moon orbit phase.</summary>
            public int PlanetId;

            /// <summary>Moon hull radius in world units (home scale already applied).</summary>
            public float Radius;
        }
    }
}
