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
    /// Predicted ship↔planet / asteroid / gem-moon bounce across toroidal map seams.
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
    /// Pipeline: Drive → Physics → ToroidalWorldCollision (this) → Planar → KinematicsSync.
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
            // Prefer MapStateSingleton when present (server / ghost); else ToroidalMapEcs cache
            // (client often gets size from MapSessionMetaRpc into that static).
            // Missing size → skip (never invent 1000).
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

            // --- Gather obstacles (no nested SystemAPI.Query) ---
            // [ECS/DOTS] Idiomatic foreach must not nest; copy centers/radii then walk ships.
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
                });
            }

            // --- Asteroids (skip dead / client-culled ghosts) ---
            // [TITAN-ORBIT] HitRpc hides the mesh immediately. Ghost Health can lag (logs:
            // hidden:true dead:false) so Health/IsDestroyed alone is not enough — also skip
            // AsteroidClientCulledTag and rocks with no solid PhysicsCollider.
            foreach (var (asteroidTransform, asteroidState, entity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<AsteroidState>>()
                         .WithAll<AsteroidTag>()
                         .WithEntityAccess())
            {
                if (asteroidState.ValueRO.IsDestroyed || asteroidState.ValueRO.Health <= 0f)
                    continue;
                if (state.EntityManager.HasComponent<AsteroidClientCulledTag>(entity))
                    continue;
                // [TITAN-ORBIT] Isolation toggle — F3 in ClientStutterIsolator.
                if (TitanOrbitDebugFlags.IsolateDisableAsteroidShipCollision)
                    continue;

                obstacles.Add(new WorldSphere
                {
                    Position = asteroidTransform.ValueRO.Position,
                    Radius = BodyCollisionMath.GetAsteroidBodyRadiusWorld(asteroidTransform.ValueRO.Scale),
                    Entity = entity,
                    IsAsteroid = 1,
                });
            }

            // --- Gem-moon snapshots (canonical collider pose + planet data for Near unwrap) ---
            // [TITAN-ORBIT] Moon colliders stay on the canonical tile (one shared kinematic hull).
            // Ships on a duplicate tile are toroidally "on top of" that hull (shortest dist ≈ 0)
            // while Euclidean-far — the old path shoved the ship every tick (stepped orbit and
            // post-dock snap toward the original tile). Resolve moons per-ship via Near pose below.
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

            if (obstacles.Length == 0 && moons.Length == 0)
            {
                obstacles.Dispose();
                moons.Dispose();
                return;
            }

            // --- Server: queue real cross-seam asteroid penetrations for ramming damage ---
            // PhysX never sees these pairs; CollisionEvents miss them. Only enqueue when
            // TryResolve actually depenetrated (true collision), never for flybys.
            DynamicBuffer<PendingRamContactElement> ramQueue = default;
            bool enqueueRam = state.World.IsServer() &&
                              SystemAPI.TryGetSingletonBuffer(out ramQueue);

            // --- Asteroid grip (Inspector AsteroidSettings.Friction) for cross-seam resolves ---
            float asteroidFriction = 0f;
            float fixedDt = SystemAPI.Time.DeltaTime;
            if (fixedDt <= 0f)
                fixedDt = 1f / 60f;
            {
                var asteroidSettings = TitanOrbit.Data.AsteroidSettingsCache.ResolveOrDefault();
                asteroidSettings.ClampValues();
                asteroidFriction = asteroidSettings.Friction;
            }

            // --- Resolve each predicted/simulated ship ---
            foreach (var (transform, velocity, physicsCollider, shipState, shipEntity) in SystemAPI
                         .Query<RefRW<LocalTransform>, RefRW<PhysicsVelocity>, RefRO<PhysicsCollider>, RefRO<ShipState>>()
                         .WithAll<ShipTag, Simulate>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                float3 shipPos = transform.ValueRO.Position;
                float3 shipVel = velocity.ValueRO.Linear;
                float shipRadius = ShipToroidalWorldCollisionLogic.GetShipCollisionRadiusWorld(
                    physicsCollider.ValueRO, transform.ValueRO.Scale);

                bool anyHit = false;
                for (int i = 0; i < obstacles.Length; i++)
                {
                    WorldSphere body = obstacles[i];
                    float3 posBefore = shipPos;
                    float3 velBefore = shipVel;
                    float bodyFriction = body.IsAsteroid != 0 ? asteroidFriction : 0f;
                    if (ShipToroidalWorldCollisionLogic.TryResolveShipVsWorldSphere(
                            ref shipPos, ref shipVel, shipRadius,
                            body.Position, body.Radius,
                            mapW, mapH, ShipToroidalWorldCollisionLogic.WorldRestitution,
                            bodyFriction, fixedDt))
                    {
                        anyHit = true;

                        // --- Cross-seam asteroid ram (server only) ---
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
                                Ship = shipEntity,
                                Other = body.Entity,
                                OtherIsShip = 0,
                                NormalShipFromOther = outward,
                                ClosingSpeed = closing,
                                EstimatedImpulse = closing * 10f,
                            });
                        }
                    }
                }

                // --- Moons: PhysX on canonical tile; Near Euclidean when ship is on another tile ---
                for (int i = 0; i < moons.Length; i++)
                {
                    MoonObstacle moon = moons[i];
                    // Same tile as the kinematic collider — Unity Physics already owns the contact.
                    if (!ShipToroidalWorldCollisionLogic.NeedsToroidalResolve(
                            shipPos, moon.CanonicalPosition, mapW, mapH))
                        continue;

                    // [TITAN-ORBIT] Same Near unwrap as dock attach / shield repel — bounce stays
                    // on the duplicate continuum instead of shoving toward the canonical moon.
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
                            ref shipPos, ref shipVel, shipRadius,
                            moonNear, moon.Radius,
                            ShipToroidalWorldCollisionLogic.WorldRestitution))
                    {
                        anyHit = true;
                    }
                }

                if (!anyHit)
                    continue;

                // --- Write back ship pose / velocity ---
                var lt = transform.ValueRO;
                lt.Position = shipPos;
                transform.ValueRW = lt;

                var pv = velocity.ValueRO;
                pv.Linear = shipVel;
                velocity.ValueRW = pv;
            }

            obstacles.Dispose();
            moons.Dispose();
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
