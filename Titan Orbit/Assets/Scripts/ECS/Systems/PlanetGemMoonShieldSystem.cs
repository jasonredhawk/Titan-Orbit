using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Collections;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only: ensures every planet has <see cref="PlanetGemMoonState"/> after spawn.
    /// Initializes shield capacity from planet level and moon gem reservoir.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct PlanetGemMoonEnsureSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            // --- System OnUpdate ---
            var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);

            foreach (var (planet, entity) in SystemAPI.Query<RefRO<PlanetState>>()
                         .WithAll<PlanetTag>()
                         .WithNone<PlanetGemMoonState>()
                         .WithEntityAccess())
            {
                float maxShield = PlanetGemMoonMath.GetMaxShieldForLevel(planet.ValueRO.PlanetLevel);
                var moonState = new PlanetGemMoonState
                {
                    CurrentShield = maxShield,
                    MaxShield = maxShield,
                };
                PlanetGemMoonCombatLogic.InitMoonGems(ref moonState);
                ecb.AddComponent(entity, moonState);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    /// <summary>
    /// Regenerates gem-moon shields after combat downtime and drains moon gems into spawned pickups
    /// when shield is depleted (legacy PlanetGemMoon server loop).
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PlanetGemMoonEnsureSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct PlanetGemMoonShieldSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            // --- System OnUpdate ---
            float dt = SystemAPI.Time.DeltaTime;
            double now = SystemAPI.Time.ElapsedTime;

            foreach (var (planet, moonShield) in SystemAPI.Query<RefRO<PlanetState>, RefRW<PlanetGemMoonState>>()
                         .WithAll<PlanetTag>())
            {
                float scaledMax = PlanetGemMoonMath.GetMaxShieldForLevel(planet.ValueRO.PlanetLevel);
                ref PlanetGemMoonState shield = ref moonShield.ValueRW;

                if (shield.MaxMoonGems <= 0.001f)
                    PlanetGemMoonCombatLogic.InitMoonGems(ref shield);

                if (math.abs(scaledMax - shield.MaxShield) > 0.001f)
                {
                    float prevMax = math.max(0.001f, shield.MaxShield);
                    float ratio = math.clamp(shield.CurrentShield / prevMax, 0f, 1f);
                    shield.MaxShield = scaledMax;
                    shield.CurrentShield = math.clamp(ratio * scaledMax, 0f, scaledMax);
                }

                if (shield.CurrentShield >= shield.MaxShield - 0.001f)
                    continue;

                if (now - shield.LastShieldHitServerTime < PlanetGemMoonMath.ShieldRegenDelaySeconds)
                    continue;

                float regenRate = shield.MaxShield / math.max(0.01f, PlanetGemMoonMath.ShieldRegenSecondsToFull);
                shield.CurrentShield = math.min(shield.MaxShield, shield.CurrentShield + regenRate * dt);
            }
        }
    }

    /// <summary>When the moon shield is down, drain planet gems and expel them as collectibles.</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PlanetGemMoonShieldSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct PlanetGemMoonCombatDrainSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GamePrefabs>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // --- System OnUpdate ---
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs) || prefabs.Gem == Entity.Null)
                return;

            float dt = SystemAPI.Time.DeltaTime;

            // --- Shared orbit clock for gem spawn at moon position ---
            // [TITAN-ORBIT] Spawn at the same analytic pose clients see (ServerTick, not World.ElapsedTime).
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double moonElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : SystemAPI.Time.ElapsedTime;

            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState))
            {
                mapW = mapState.MapWidth;
                mapH = mapState.MapHeight;
            }

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (planet, moon, transform) in SystemAPI
                         .Query<RefRW<PlanetState>, RefRW<PlanetGemMoonState>, RefRO<LocalTransform>>()
                         .WithAll<PlanetTag>())
            {
                ref PlanetGemMoonState moonState = ref moon.ValueRW;
                if (moonState.CurrentShield > 0f)
                    continue;
                if (moonState.CurrentMoonGems <= 0.0001f)
                    continue;

                ref PlanetState planetState = ref planet.ValueRW;
                if (planetState.CurrentGems <= 0.0001f)
                    continue;

                float drain = PlanetGemMoonMath.GemDrainPerSecondWhenShieldDown * dt;
                drain = math.min(drain, math.min(moonState.CurrentMoonGems, planetState.CurrentGems));
                if (drain <= 0.0001f)
                    continue;

                moonState.CurrentMoonGems -= drain;
                planetState.CurrentGems -= drain;
                moonState.GemDrainAccumulator += drain;
                moonState.GemSpawnTimer += dt;

                if (moonState.GemSpawnTimer < PlanetGemMoonMath.GemSpawnInterval)
                    continue;
                if (moonState.GemDrainAccumulator < PlanetGemMoonMath.GemSpawnMinValue)
                    continue;

                float spawnValue = moonState.GemDrainAccumulator;
                moonState.GemDrainAccumulator = 0f;
                moonState.GemSpawnTimer = 0f;

                float planetSize = math.max(0.25f, transform.ValueRO.Scale);
                float3 moonPos = PlanetOrbitMath.GetMoonWorldPositionNear(
                    transform.ValueRO.Position,
                    transform.ValueRO.Position,
                    planetSize,
                    planetState.PlanetLevel,
                    planetState.PlanetId,
                    moonElapsed,
                    mapW,
                    mapH);

                GemSpawning.Spawn(
                    ecb,
                    prefabs.Gem,
                    moonPos,
                    spawnValue,
                    (uint)planetState.PlanetId,
                    burst: false,
                    (float)moonElapsed);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
