using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Ensures gem-moon shield state exists and regenerates after hits (legacy PlanetGemMoon).</summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct PlanetGemMoonEnsureSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);

            foreach (var (planet, entity) in SystemAPI.Query<RefRO<PlanetState>>()
                         .WithAll<PlanetTag>()
                         .WithNone<PlanetGemMoonState>()
                         .WithEntityAccess())
            {
                float maxShield = PlanetGemMoonMath.GetMaxShieldForLevel(planet.ValueRO.PlanetLevel);
                ecb.AddComponent(entity, new PlanetGemMoonState
                {
                    CurrentShield = maxShield,
                    MaxShield = maxShield,
                });
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PlanetGemMoonEnsureSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct PlanetGemMoonShieldSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;
            double now = SystemAPI.Time.ElapsedTime;

            foreach (var (planet, moonShield) in SystemAPI.Query<RefRO<PlanetState>, RefRW<PlanetGemMoonState>>()
                         .WithAll<PlanetTag>())
            {
                float scaledMax = PlanetGemMoonMath.GetMaxShieldForLevel(planet.ValueRO.PlanetLevel);
                ref PlanetGemMoonState shield = ref moonShield.ValueRW;

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
}
