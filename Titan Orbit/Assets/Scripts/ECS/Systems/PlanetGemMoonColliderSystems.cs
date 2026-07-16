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
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PlanetGemMoonEnsureSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct PlanetGemMoonColliderEnsureSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (planetState, planetTransform, entity) in SystemAPI
                         .Query<RefRO<PlanetState>, RefRO<LocalTransform>>()
                         .WithAll<PlanetTag, PlanetGemMoonState>()
                         .WithNone<PlanetGemMoonColliderEntity>()
                         .WithEntityAccess())
            {
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

                ecb.AddComponent(moonEntity, LocalTransform.FromPositionRotationScale(moonPos, quaternion.identity, planetScale));
                ecb.AddComponent(entity, new PlanetGemMoonColliderEntity { MoonColliderEntity = moonEntity });
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
