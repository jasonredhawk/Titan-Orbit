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

    /// <summary>
    /// Idle. Moon gems used to drip out on a timer after the shield fell. They now leave
    /// only with the hit that dealt the damage, one gem sized to that damage.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PlanetGemMoonShieldSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct PlanetGemMoonCombatDrainSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.Enabled = false;
        }

        public void OnUpdate(ref SystemState state)
        {
        }
    }
}
