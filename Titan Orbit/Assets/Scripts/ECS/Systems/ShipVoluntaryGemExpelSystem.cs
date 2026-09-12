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
    /// Server-authoritative hold-V cargo dump. While <see cref="ShipInput.WantExpelGems"/>
    /// is set, deducts <c>shipLevel + gem-capacity upgrades</c> (or leftover cargo) every
    /// <see cref="GemEconomyConstants.VoluntaryGemExpelIntervalSeconds"/> and spawns one
    /// world gem along the ship's planar forward — not a random burst.
    /// <para>
    /// First eligible tick fires immediately (same Accum prime as moon deposit).
    /// Self-pickup is blocked so the tractor cannot vacuum the dump back.
    /// </para>
    /// World: ServerSimulation. Gems Instantiates from <see cref="GamePrefabs.Gem"/>.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedFixedStepSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipVoluntaryGemExpelSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
            state.RequireForUpdate<GamePrefabs>();
            state.RequireForUpdate<ShipTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs))
                return;

            Entity gemPrefab = prefabs.Gem;
            if (gemPrefab == Entity.Null)
                return;

            float dt = SystemAPI.Time.DeltaTime;
            if (dt <= 0f)
                dt = 1f / 60f;

            double now = SystemAPI.Time.ElapsedTime;
            float spawnServerTime = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                state.EntityManager, now);
            float interval = GemEconomyConstants.VoluntaryGemExpelIntervalSeconds;

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (input, shipState, transform, timer, entity) in SystemAPI
                         .Query<RefRO<ShipInput>, RefRW<ShipState>, RefRO<LocalTransform>,
                             RefRW<ShipGemExpelTimer>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                bool wantExpel = input.ValueRO.WantExpelGems;
                bool canDump = wantExpel
                    && !shipState.ValueRO.IsDead
                    && !shipState.ValueRO.AwaitingTeamSelection
                    && !ShipImpactSpinApply.IsWrecked(state.EntityManager, entity, now)
                    && shipState.ValueRO.CurrentGems >= GemEconomyConstants.MinGemSpawnValue;

                if (canDump &&
                    SystemAPI.HasComponent<ShipTurretControlState>(entity) &&
                    SystemAPI.GetComponentRO<ShipTurretControlState>(entity).ValueRO.IsControlling)
                    canDump = false;

                if (!canDump)
                {
                    timer.ValueRW.Accum = 0f;
                    continue;
                }

                // --- First hold tick dumps immediately (no half-second silence) ---
                if (timer.ValueRO.Accum <= 0f)
                    timer.ValueRW.Accum = interval;

                timer.ValueRW.Accum += dt;

                int gemCapLevel = 0;
                if (SystemAPI.HasComponent<ShipAttributeUpgradeState>(entity))
                    gemCapLevel = SystemAPI.GetComponentRO<ShipAttributeUpgradeState>(entity).ValueRO.GemCapacity;

                float3 forward = math.forward(transform.ValueRO.Rotation);
                forward.y = 0f;
                if (math.lengthsq(forward) < 0.01f)
                    forward = new float3(0f, 0f, 1f);
                else
                    forward = math.normalize(forward);

                float3 shipVel = float3.zero;
                if (SystemAPI.HasComponent<ShipKinematics>(entity))
                    shipVel = SystemAPI.GetComponentRO<ShipKinematics>(entity).ValueRO.Velocity;

                int sourceNetworkId = 0;
                if (SystemAPI.HasComponent<GhostOwner>(entity))
                    sourceNetworkId = SystemAPI.GetComponentRO<GhostOwner>(entity).ValueRO.NetworkId;

                var collider = SystemAPI.HasComponent<PhysicsCollider>(entity)
                    ? SystemAPI.GetComponentRO<PhysicsCollider>(entity).ValueRO
                    : default;
                float3 nose = ShipGemExpulsion.ResolveNoseTipWorld(transform.ValueRO, collider);

                while (timer.ValueRO.Accum >= interval &&
                       shipState.ValueRO.CurrentGems >= GemEconomyConstants.MinGemSpawnValue)
                {
                    timer.ValueRW.Accum -= interval;

                    float amount = GemEconomyConstants.GetVoluntaryExpelAmount(
                        shipState.ValueRO.ShipLevel,
                        gemCapLevel,
                        shipState.ValueRO.CurrentGems);
                    if (amount < GemEconomyConstants.MinGemSpawnValue)
                        break;

                    var ship = shipState.ValueRO;
                    ship.CurrentGems -= amount;
                    float h = ship.Health;
                    float g = ship.CurrentGems;
                    bool dead = ship.IsDead;
                    ShipDamageLogic.TryMarkDeadIfHullAndGemsDepleted(ref h, ref g, ref dead);
                    ship.Health = h;
                    ship.CurrentGems = g;
                    ship.IsDead = dead;
                    shipState.ValueRW = ship;

                    ShipGemExpulsion.SpawnVoluntaryForward(
                        ecb,
                        gemPrefab,
                        nose,
                        forward,
                        shipVel,
                        amount,
                        salt: (uint)(entity.Index * 73856093) ^ (uint)(now * 1000.0),
                        spawnServerTime,
                        sourceNetworkId);

                    if (dead)
                        break;
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
