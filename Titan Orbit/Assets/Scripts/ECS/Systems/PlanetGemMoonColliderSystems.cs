using TitanOrbit.Core;
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
    /// [PHYSICS] Gem moons are visual children on planet proxies — their meshes intentionally have no
    /// colliders (<see cref="Game.PlanetGemMoonVisualProxy"/>). This ensure pass spawns a kinematic
    /// Unity Physics sphere per planet so ships bounce off the moon hull like planets and asteroids.
    /// Runs on server and client (colliders are local sim geometry, not NetCode ghosts).
    /// <para>
    /// [TITAN-ORBIT] On clients skip while Settling OR TransformQuarantine (quarantine stays on for
    /// the whole Windows in-game session — Settling OFF alone still Crash!!! on mass CreateEntity).
    /// At most one moon hull per frame when those gates eventually allow work.
    /// </para>
    /// </summary>
    // No UpdateAfter(PlanetGemMoonEnsureSystem) — that ensure is server-only; ClientWorld sorter warns.
    [UpdateInGroup(typeof(SimulationSystemGroup))]
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
                // --- Join / quarantine gate ---
                // [TITAN-ORBIT] Do not CreateEntity moon hulls while Instantiates backlog drains OR
                // while TransformQuarantine is on (Settling OFF still Crash!!! if we scan/CreateEntity).
                if (ClientJoinSettleCache.TransformQuarantine || ClientJoinSettleCache.Settling)
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

            // --- Shared orbit clock (once per frame) ---
            // [TITAN-ORBIT] ServerTick seconds — not World.Time.ElapsedTime (late-join desync).
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double elapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : state.World.Time.ElapsedTime;

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

            // --- Moon shield sphere (same pose as the moon, larger radius) ---
            foreach (var (planetState, planetTransform, moonState, entity) in SystemAPI
                         .Query<RefRO<PlanetState>, RefRO<LocalTransform>, RefRO<PlanetGemMoonState>>()
                         .WithAll<PlanetTag, PlanetGemMoonColliderEntity>()
                         .WithNone<PlanetGemMoonShieldColliderEntity>()
                         .WithEntityAccess())
            {
                if (isClient && ensuredThisFrame >= MaxClientEnsuresPerFrame)
                    break;

                float planetScale = math.max(0.25f, planetTransform.ValueRO.Scale);
                bool isHome = planetState.ValueRO.IsHomePlanet;
                float shieldLocal = PlanetGemMoonMath.GetMoonShieldOuterRadiusLocal(planetScale, isHome);
                bool shieldUp = moonState.ValueRO.CurrentShield > 0.001f;

                Entity shieldEntity = ecb.CreateEntity();
                var material = Unity.Physics.Material.Default;
                material.Restitution = 0.5f;
                var owner = planetState.ValueRO.Ownership;
                var collider = Unity.Physics.SphereCollider.Create(
                    new SphereGeometry { Center = float3.zero, Radius = math.max(0.05f, shieldLocal) },
                    TitanOrbitPhysicsLayers.MoonShieldForOwner(owner),
                    material);

                ecb.AddComponent(shieldEntity, new PhysicsCollider { Value = collider });
                ecb.AddComponent(shieldEntity, PhysicsMass.CreateKinematic(collider.Value.MassProperties));
                ecb.AddComponent(shieldEntity, new PhysicsGravityFactor { Value = 0f });
                ecb.AddSharedComponent(shieldEntity, new PhysicsWorldIndex(0));
                ecb.AddComponent(shieldEntity, new PlanetGemMoonShieldColliderTag());
                ecb.AddComponent(shieldEntity, new PlanetGemMoonColliderPlanetRef { PlanetEntity = entity });
                ecb.AddComponent(shieldEntity, new PlanetGemMoonShieldOwnerState { Owner = owner });

                float3 moonPos = PlanetOrbitMath.GetMoonWorldPosition(
                    planetTransform.ValueRO.Position,
                    planetScale,
                    planetState.ValueRO.PlanetLevel,
                    planetState.ValueRO.PlanetId,
                    elapsed,
                    isHome);

                float liveScale = shieldUp ? planetScale : 0.01f;
                var shieldLt = LocalTransform.FromPositionRotationScale(
                    moonPos, quaternion.identity, liveScale);
                ecb.AddComponent(shieldEntity, shieldLt);
                ecb.AddComponent(shieldEntity, new LocalToWorld
                {
                    Value = float4x4.TRS(moonPos, quaternion.identity, liveScale),
                });
                ecb.AddComponent(entity, new PlanetGemMoonShieldColliderEntity
                {
                    ShieldColliderEntity = shieldEntity,
                });
                ensuredThisFrame++;
            }

            foreach (var (planetRef, shieldEntity) in SystemAPI
                         .Query<RefRO<PlanetGemMoonColliderPlanetRef>>()
                         .WithAll<PlanetGemMoonShieldColliderTag>()
                         .WithNone<PlanetGemMoonShieldOwnerState>()
                         .WithEntityAccess())
            {
                Entity planetEntity = planetRef.ValueRO.PlanetEntity;
                var owner = TeamId.None;
                if (state.EntityManager.Exists(planetEntity)
                    && state.EntityManager.HasComponent<PlanetState>(planetEntity))
                    owner = state.EntityManager.GetComponentData<PlanetState>(planetEntity).Ownership;

                ecb.AddComponent(shieldEntity, new PlanetGemMoonShieldOwnerState { Owner = owner });
                if (state.EntityManager.HasComponent<PhysicsCollider>(shieldEntity)
                    && state.EntityManager.HasComponent<LocalTransform>(planetEntity))
                {
                    var planetState = state.EntityManager.GetComponentData<PlanetState>(planetEntity);
                    var planetTransform = state.EntityManager.GetComponentData<LocalTransform>(planetEntity);
                    float planetScale = math.max(0.25f, planetTransform.Scale);
                    float shieldLocal = PlanetGemMoonMath.GetMoonShieldOuterRadiusLocal(
                        planetScale, planetState.IsHomePlanet);
                    var material = Unity.Physics.Material.Default;
                    material.Restitution = 0.5f;
                    var collider = Unity.Physics.SphereCollider.Create(
                        new SphereGeometry { Center = float3.zero, Radius = math.max(0.05f, shieldLocal) },
                        TitanOrbitPhysicsLayers.MoonShieldForOwner(owner),
                        material);
                    ecb.SetComponent(shieldEntity, new PhysicsCollider { Value = collider });
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    /// <summary>
    /// [PHYSICS] Teleports each gem-moon kinematic collider to its analytic orbit position before
    /// <see cref="PhysicsSystemGroup"/> integrates ships. Same math as moon visuals and bullet hits.
    /// Orbit phase uses <see cref="PlanetGemMoonOrbitClock"/> (ServerTick seconds), not World.ElapsedTime.
    /// Paired with <see cref="PlanetGemMoonColliderEnsureSystem"/>.
    /// </summary>
    // OrderFirst: moon hull pose before PhysicsSystemGroup without UpdateBefore(Physics…).
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct PlanetGemMoonColliderSyncSystem : ISystem
    {
        /// <summary>Require NetCode time so moon hulls stay on the shared ServerTick orbit clock.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkTime>();
        }

        /// <summary>
        /// Teleports each moon collider to the analytic orbit pose for the current ServerTick.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Shared orbit clock ---
            // [NETCODE] ServerTick → seconds. Client prediction resim uses the tick being predicted.
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double elapsed = PlanetGemMoonOrbitClock.GetElapsedSeconds(
                SystemAPI.GetSingleton<NetworkTime>(),
                hz,
                includeTickFraction: false);

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

            foreach (var (planetRef, shieldTransform, ownerState, shieldEntity) in SystemAPI
                         .Query<RefRO<PlanetGemMoonColliderPlanetRef>, RefRW<LocalTransform>,
                             RefRW<PlanetGemMoonShieldOwnerState>>()
                         .WithAll<PlanetGemMoonShieldColliderTag>()
                         .WithEntityAccess())
            {
                Entity planetEntity = planetRef.ValueRO.PlanetEntity;
                if (!state.EntityManager.Exists(planetEntity))
                    continue;
                if (!state.EntityManager.HasComponent<PlanetState>(planetEntity)
                    || !state.EntityManager.HasComponent<LocalTransform>(planetEntity)
                    || !state.EntityManager.HasComponent<PlanetGemMoonState>(planetEntity))
                    continue;

                var planetState = state.EntityManager.GetComponentData<PlanetState>(planetEntity);
                var planetTransform = state.EntityManager.GetComponentData<LocalTransform>(planetEntity);
                var moon = state.EntityManager.GetComponentData<PlanetGemMoonState>(planetEntity);
                float planetScale = math.max(0.25f, planetTransform.Scale);

                float3 moonPos = PlanetOrbitMath.GetMoonWorldPosition(
                    planetTransform.Position,
                    planetScale,
                    planetState.PlanetLevel,
                    planetState.PlanetId,
                    elapsed,
                    planetState.IsHomePlanet);

                float liveScale = moon.CurrentShield > 0.001f ? planetScale : 0.01f;
                shieldTransform.ValueRW = LocalTransform.FromPositionRotationScale(
                    moonPos,
                    quaternion.identity,
                    liveScale);

                // Ownership flip (capture) — new filter so the old owner's ships pass through.
                if (ownerState.ValueRO.Owner != planetState.Ownership
                    && state.EntityManager.HasComponent<PhysicsCollider>(shieldEntity))
                {
                    float shieldLocal = PlanetGemMoonMath.GetMoonShieldOuterRadiusLocal(
                        planetScale, planetState.IsHomePlanet);
                    var material = Unity.Physics.Material.Default;
                    material.Restitution = 0.5f;
                    var collider = Unity.Physics.SphereCollider.Create(
                        new SphereGeometry { Center = float3.zero, Radius = math.max(0.05f, shieldLocal) },
                        TitanOrbitPhysicsLayers.MoonShieldForOwner(planetState.Ownership),
                        material);
                    state.EntityManager.SetComponentData(
                        shieldEntity, new PhysicsCollider { Value = collider });
                    ownerState.ValueRW.Owner = planetState.Ownership;
                }
            }
        }
    }
}
