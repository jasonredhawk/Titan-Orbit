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
    /// [PHYSICS] Gem moons are visual children on planet proxies — their meshes intentionally have no
    /// colliders (<see cref="Game.PlanetGemMoonVisualProxy"/>). This ensure pass spawns a kinematic
    /// Unity Physics sphere per planet so ships bounce off the moon hull like planets and asteroids.
    /// Runs on server and client (colliders are local sim geometry, not NetCode ghosts).
    /// <para>
    /// [TITAN-ORBIT] On clients we wait until <see cref="ClientJoinSettleCache.Settling"/> is false
    /// (GhostSpawn Instantiates backlog drained) and create at most one moon hull per frame.
    /// Creating moon colliders in the same window as NetCode Instantiates contributed to Windows
    /// player hard-crashes right after Relay go-in-game.
    /// </para>
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PlanetGemMoonEnsureSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct PlanetGemMoonColliderEnsureSystem : ISystem
    {
        /// <summary>
        /// Max moon collider entities to create in one frame on the client.
        /// Server may create more — map gen already batches planet spawns.
        /// </summary>
        const int MaxClientEnsuresPerFrame = 1;

        /// <summary>
        /// Spawns missing kinematic moon hulls for planets that already have <see cref="PlanetGemMoonState"/>.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Client vs server ---
            // [NETCODE] ClientWorld is the predicted/interpolated replica; server is authoritative.
            // MapStateSingleton often does NOT replicate to dedicated clients, so we do not gate on
            // LoadingComplete (that would skip moon hulls forever).
            bool isClient = state.World.IsClient();
            if (isClient)
            {
                // --- Join settle gate ---
                // [TITAN-ORBIT] Do not CreateEntity moon hulls while GhostSpawn Instantiates backlog
                // is still draining. Shared flag with TransformSystemGroup gate + EcsWorldVisualizer.
                if (ClientJoinSettleCache.Settling)
                    return;

                // Also wait until we are actually in-game (settle cache clears when not in-game).
                bool inGame = false;
                foreach (var _ in SystemAPI.Query<RefRO<NetworkStreamInGame>>())
                {
                    inGame = true;
                    break;
                }

                if (!inGame)
                    return;
            }

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            int ensuredThisFrame = 0;

            foreach (var (planetState, planetTransform, entity) in SystemAPI
                         .Query<RefRO<PlanetState>, RefRO<LocalTransform>>()
                         .WithAll<PlanetTag, PlanetGemMoonState>()
                         .WithNone<PlanetGemMoonColliderEntity>()
                         .WithEntityAccess())
            {
                // --- Rate-limit on client ---
                // [TITAN-ORBIT] Spread CreateEntity across frames after the join settle delay.
                if (isClient && ensuredThisFrame >= MaxClientEnsuresPerFrame)
                    break;

                float planetScale = math.max(0.25f, planetTransform.ValueRO.Scale);
                bool isHome = planetState.ValueRO.IsHomePlanet;

                // --- Kinematic moon hull entity ---
                // [PHYSICS] Radius matches GetMoonBodyRadiusWorld via local radius × planet scale.
                Entity moonEntity = ecb.CreateEntity();
                float moonBodyRadiusLocal = PlanetGemMoonMath.GetMoonBodyRadiusLocal(planetScale, isHome);

                var material = Unity.Physics.Material.Default;
                material.Restitution = 0.5f;

                var collider = SphereCollider.Create(
                    new SphereGeometry { Center = float3.zero, Radius = moonBodyRadiusLocal },
                    TitanOrbitPhysicsLayers.WorldStatic,
                    material);

                ecb.AddComponent(moonEntity, new PhysicsCollider { Value = collider });
                ecb.AddComponent(moonEntity, PhysicsMass.CreateKinematic(collider.Value.MassProperties));
                ecb.AddComponent(moonEntity, new PhysicsGravityFactor { Value = 0f });
                ecb.AddSharedComponent(moonEntity, new PhysicsWorldIndex(0));
                ecb.AddComponent(moonEntity, new PlanetGemMoonColliderTag());
                ecb.AddComponent(moonEntity, new PlanetGemMoonColliderPlanetRef { PlanetEntity = entity });

                // --- Initial pose (sync system keeps this updated each physics step) ---
                double elapsed = state.World.Time.ElapsedTime;
                float3 moonPos = PlanetOrbitMath.GetMoonWorldPosition(
                    planetTransform.ValueRO.Position,
                    planetScale,
                    planetState.ValueRO.PlanetLevel,
                    planetState.ValueRO.PlanetId,
                    elapsed,
                    isHome);

                // [ECS/DOTS] LocalToWorld required alongside LocalTransform for TransformSystemGroup.
                var moonLt = LocalTransform.FromPositionRotationScale(moonPos, quaternion.identity, planetScale);
                ecb.AddComponent(moonEntity, moonLt);
                ecb.AddComponent(moonEntity, new LocalToWorld { Value = float4x4.TRS(moonPos, quaternion.identity, planetScale) });
                ecb.AddComponent(entity, new PlanetGemMoonColliderEntity { MoonColliderEntity = moonEntity });
                ensuredThisFrame++;
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    /// <summary>
    /// [PHYSICS] Teleports each gem-moon kinematic collider to its analytic orbit position before
    /// <see cref="PhysicsSystemGroup"/> integrates ships. Same math as moon visuals and bullet hits.
    /// Paired with <see cref="PlanetGemMoonColliderEnsureSystem"/>.
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(PhysicsSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct PlanetGemMoonColliderSyncSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            double elapsed = state.World.Time.ElapsedTime;

            foreach (var (planetRef, moonTransform) in SystemAPI
                         .Query<RefRO<PlanetGemMoonColliderPlanetRef>, RefRW<LocalTransform>>()
                         .WithAll<PlanetGemMoonColliderTag>())
            {
                Entity planetEntity = planetRef.ValueRO.PlanetEntity;
                if (!state.EntityManager.Exists(planetEntity))
                    continue;

                if (!state.EntityManager.HasComponent<PlanetState>(planetEntity)
                    || !state.EntityManager.HasComponent<LocalTransform>(planetEntity))
                    continue;

                var planetState = state.EntityManager.GetComponentData<PlanetState>(planetEntity);
                var planetTransform = state.EntityManager.GetComponentData<LocalTransform>(planetEntity);
                float planetScale = math.max(0.25f, planetTransform.Scale);

                float3 moonPos = PlanetOrbitMath.GetMoonWorldPosition(
                    planetTransform.Position,
                    planetScale,
                    planetState.PlanetLevel,
                    planetState.PlanetId,
                    elapsed,
                    planetState.IsHomePlanet);

                moonTransform.ValueRW = LocalTransform.FromPositionRotationScale(
                    moonPos,
                    quaternion.identity,
                    planetScale);
            }
        }
    }
}
