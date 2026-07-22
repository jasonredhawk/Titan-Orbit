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
            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;
            if (SystemAPI.TryGetSingleton(out MapStateSingleton mapState) &&
                mapState.MapWidth >= 100f && mapState.MapHeight >= 100f)
            {
                mapW = mapState.MapWidth;
                mapH = mapState.MapHeight;
            }

            // --- Gather obstacles (no nested SystemAPI.Query) ---
            // [ECS/DOTS] Idiomatic foreach must not nest; copy centers/radii then walk ships.
            var obstacles = new NativeList<WorldSphere>(128, Allocator.Temp);

            foreach (var planetTransform in SystemAPI.Query<RefRO<LocalTransform>>().WithAll<PlanetTag>())
            {
                obstacles.Add(new WorldSphere
                {
                    Position = planetTransform.ValueRO.Position,
                    Radius = BodyCollisionMath.GetPlanetBodyRadiusWorld(planetTransform.ValueRO.Scale),
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
                });
            }

            // --- Gem-moon kinematic hulls (orbiting planets) ---
            // [TITAN-ORBIT] Moon LocalTransform is logical orbit pose — same seam gap as planets.
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

                obstacles.Add(new WorldSphere
                {
                    Position = moonTransform.ValueRO.Position,
                    Radius = PlanetGemMoonMath.GetMoonBodyRadiusWorld(
                        planetScale, planetStateData.IsHomePlanet),
                });
            }

            if (obstacles.Length == 0)
            {
                obstacles.Dispose();
                return;
            }

            // --- Resolve each predicted/simulated ship ---
            foreach (var (transform, velocity, physicsCollider, shipState) in SystemAPI
                         .Query<RefRW<LocalTransform>, RefRW<PhysicsVelocity>, RefRO<PhysicsCollider>, RefRO<ShipState>>()
                         .WithAll<ShipTag, Simulate>())
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
                    if (ShipToroidalWorldCollisionLogic.TryResolveShipVsWorldSphere(
                            ref shipPos, ref shipVel, shipRadius,
                            body.Position, body.Radius,
                            mapW, mapH, ShipToroidalWorldCollisionLogic.WorldRestitution))
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
        }
    }
}
