using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Server-authoritative health and energy regeneration from ship-family vitals.</summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(BulletSimulationSystem))]
    public partial struct ShipVitalsRegenSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;
            if (dt <= 0f)
                return;

            double now = SystemAPI.Time.ElapsedTime;

            foreach (var (ship, vitals, vitalsState) in SystemAPI
                         .Query<RefRW<ShipState>, RefRO<ShipVitalsConfig>, RefRW<ShipVitalsState>>()
                         .WithAll<ShipTag>())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;

                ref var s = ref ship.ValueRW;
                var cfg = vitals.ValueRO;

                if (s.CurrentEnergy < s.MaxEnergy && cfg.EnergyRegenPerSecond > 0f)
                {
                    s.CurrentEnergy = UnityEngine.Mathf.Min(
                        s.MaxEnergy,
                        s.CurrentEnergy + cfg.EnergyRegenPerSecond * dt);
                }

                if (s.Health < s.MaxHealth && cfg.HealthRegenPerSecond > 0f)
                {
                    float delay = UnityEngine.Mathf.Max(0f, cfg.HealthRegenDelayAfterDamage);
                    if (now >= vitalsState.ValueRO.LastHullDamageTime + delay)
                    {
                        s.Health = UnityEngine.Mathf.Min(
                            s.MaxHealth,
                            s.Health + cfg.HealthRegenPerSecond * dt);
                    }
                }
            }
        }
    }
}
