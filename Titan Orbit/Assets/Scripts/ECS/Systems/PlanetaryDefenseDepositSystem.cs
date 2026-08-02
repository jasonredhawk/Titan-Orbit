using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: when a friendly living ship sits in a planetary defense slot zone, automatically
    /// drains cargo gems into that slot's build/upgrade bar (metronome chunks).
    /// <para>
    /// [TITAN-ORBIT] Gems go <b>only</b> into the slot — never planet treasury / Bank
    /// (<see cref="PlanetEconomyMath.DepositGems"/> is intentionally not called).
    /// No deposit button and no moon dock required. Fully moon-docked ships skip this path so
    /// moon treasury deposit remains the docked flow.
    /// </para>
    /// <para>
    /// [ECS/DOTS] <see cref="SystemBase"/> (not <c>ISystem</c>) because we keep managed
    /// <see cref="PlanetShipFamilyConfig"/> cache + a persistent NativeHashMap — same pattern as
    /// <see cref="DroneSwarmCombatSystem"/>.
    /// </para>
    /// World: ServerSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PlanetaryDefenseSlotSyncSystem))]
    public partial class PlanetaryDefenseDepositSystem : SystemBase
    {
        /// <summary>Per-ship metronome accumulator (seconds), keyed by ship entity index.</summary>
        NativeHashMap<int, float> _beatTimers;

        PlanetShipFamilyConfig _familyConfig;
        bool _familyResolved;

        /// <summary>Allocate beat timers; require map + planets.</summary>
        protected override void OnCreate()
        {
            RequireForUpdate<PlanetTag>();
            RequireForUpdate<MapStateSingleton>();
            _beatTimers = new NativeHashMap<int, float>(64, Allocator.Persistent);
        }

        /// <summary>Dispose persistent map.</summary>
        protected override void OnDestroy()
        {
            if (_beatTimers.IsCreated)
                _beatTimers.Dispose();
        }

        /// <summary>Drain cargo into nearby friendly defense slots.</summary>
        protected override void OnUpdate()
        {
            EnsureFamilyConfig();

            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var map) ||
                !ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
                return;

            float mapW = map.MapWidth;
            float mapH = map.MapHeight;
            float dt = SystemAPI.Time.DeltaTime;
            var em = EntityManager;
            var defaultConfig = PlanetaryDefenseConfig.LoadDefault();

            foreach (var (shipState, shipTransform, shipEntity) in SystemAPI
                         .Query<RefRW<ShipState>, RefRO<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                var ship = shipState.ValueRO;
                if (ship.IsDead || ship.AwaitingTeamSelection || ship.Team == TeamId.None)
                    continue;
                if (ship.CurrentGems <= 0.001f)
                    continue;

                // --- Skip fully moon-docked ships (moon treasury owns that flow) ---
                if (em.HasComponent<ShipMoonDockState>(shipEntity))
                {
                    var dock = em.GetComponentData<ShipMoonDockState>(shipEntity);
                    if (dock.MoonPlanetId != 0 &&
                        dock.LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold)
                        continue;
                }

                float3 shipPos = shipTransform.ValueRO.Position;
                shipPos.y = PlanetaryDefenseMath.FixedY;

                // --- Find closest friendly slot zone this ship is inside ---
                if (!TryFindClosestSlotInZone(
                        em, ship.Team, shipPos, mapW, mapH, _familyConfig, defaultConfig,
                        out Entity planetEntity, out int slotIndex, out PlanetaryDefenseConfig config))
                {
                    _beatTimers[shipEntity.Index] = 0f;
                    continue;
                }

                float interval = math.max(0.1f, config.depositChunkIntervalSeconds);
                float timer = 0f;
                _beatTimers.TryGetValue(shipEntity.Index, out timer);
                timer += dt;
                if (timer < interval)
                {
                    _beatTimers[shipEntity.Index] = timer;
                    continue;
                }

                // Spend whole beats if frame hitch piled up (same idea as gem deposit).
                int beats = (int)math.floor(timer / interval);
                timer -= beats * interval;
                _beatTimers[shipEntity.Index] = timer;

                var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
                if (slotIndex < 0 || slotIndex >= buffer.Length)
                    continue;

                var planet = em.GetComponentData<PlanetState>(planetEntity);
                int maxTurretLevel = PlanetaryDefenseMath.GetMaxTurretLevelForPlanet(planet.PlanetLevel);

                for (int b = 0; b < beats; b++)
                {
                    // Re-read — prior beat may have activated/upgraded.
                    ship = shipState.ValueRO;
                    if (ship.CurrentGems <= 0.001f)
                        break;

                    var slot = buffer[slotIndex];
                    if (slot.TurretLevel >= maxTurretLevel)
                    {
                        // Cap: refuse gems and keep progress clear so the UI does not "loop".
                        if (slot.BuildProgress > 0f)
                        {
                            slot.BuildProgress = 0f;
                            buffer[slotIndex] = slot;
                        }
                        break;
                    }

                    float cost = config.GetGemsToNextLevel(slot.TurretLevel);
                    if (cost <= 0.001f)
                        break;

                    float need = cost - slot.BuildProgress;
                    // Already full (or overshot from a prior hitch) — activate/upgrade first.
                    if (need <= 0.001f)
                    {
                        ApplyLevelUpsWhileFull(ref slot, maxTurretLevel, config);
                        buffer[slotIndex] = slot;
                        continue;
                    }

                    float chunk = GemEconomyConstants.GetDepositChunkAmount(ship.ShipLevel, ship.CurrentGems);
                    chunk = math.min(chunk, need);
                    if (chunk <= 0.001f)
                        break;

                    // --- Cargo ↓, slot progress ↑ (no treasury / Bank) ---
                    ship.CurrentGems -= chunk;
                    shipState.ValueRW = ship;

                    slot.BuildProgress += chunk;
                    ApplyLevelUpsWhileFull(ref slot, maxTurretLevel, config);
                    buffer[slotIndex] = slot;
                }
            }
        }

        /// <summary>
        /// While <see cref="PlanetaryDefenseSlotElement.BuildProgress"/> covers the next rung cost,
        /// activate/upgrade and subtract that cost. Stops at the planet's max turret level.
        /// Prevents the progress UI from wrapping back to 0 without a real level change.
        /// </summary>
        static void ApplyLevelUpsWhileFull(
            ref PlanetaryDefenseSlotElement slot,
            int maxTurretLevel,
            PlanetaryDefenseConfig config)
        {
            // Guard against pathological overfill — at most one rung per planet max level.
            for (int guard = 0; guard < 8; guard++)
            {
                if (slot.TurretLevel >= maxTurretLevel)
                {
                    slot.BuildProgress = 0f;
                    return;
                }

                float cost = config.GetGemsToNextLevel(slot.TurretLevel);
                if (cost <= 0.001f || slot.BuildProgress + 0.0001f < cost)
                    return;

                if (!TryApplyLevelUp(ref slot, maxTurretLevel, config))
                    return;

                slot.BuildProgress = math.max(0f, slot.BuildProgress - cost);
                if (slot.TurretLevel >= maxTurretLevel)
                    slot.BuildProgress = 0f;
            }
        }

        /// <summary>
        /// Activates level 1 or upgrades by one rung; full-heals to the new max HP.
        /// </summary>
        /// <returns>True when the slot level increased.</returns>
        static bool TryApplyLevelUp(
            ref PlanetaryDefenseSlotElement slot,
            int maxTurretLevel,
            PlanetaryDefenseConfig config)
        {
            int next = slot.TurretLevel + 1;
            if (next < 1 || next > maxTurretLevel)
                return false;

            var stats = config.GetLevelStats(next);
            slot.TurretLevel = (byte)next;
            slot.MaxHealth = math.max(1f, stats.maxHealth);
            slot.Health = slot.MaxHealth; // [TITAN-ORBIT] Full heal on activate/upgrade (v1).
            return true;
        }

        /// <summary>
        /// Finds the closest defense slot zone the ship is inside among friendly owned planets.
        /// </summary>
        static bool TryFindClosestSlotInZone(
            EntityManager em,
            TeamId shipTeam,
            float3 shipPos,
            float mapW,
            float mapH,
            PlanetShipFamilyConfig familyConfig,
            PlanetaryDefenseConfig defaultConfig,
            out Entity bestPlanet,
            out int bestSlot,
            out PlanetaryDefenseConfig bestConfig)
        {
            bestPlanet = Entity.Null;
            bestSlot = -1;
            bestConfig = defaultConfig;
            float bestDistSq = float.MaxValue;

            using var planets = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<PlanetaryDefenseSlotElement>());

            using var entities = planets.ToEntityArray(Allocator.Temp);
            for (int p = 0; p < entities.Length; p++)
            {
                Entity planetEntity = entities[p];
                var planet = em.GetComponentData<PlanetState>(planetEntity);
                if (planet.Ownership == TeamId.None || planet.Ownership != shipTeam)
                    continue;
                if (!em.HasBuffer<PlanetaryDefenseSlotElement>(planetEntity))
                    continue;

                var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
                if (buffer.Length == 0)
                    continue;

                var config = PlanetaryDefenseConfig.ResolveForFamily(
                    familyConfig, planet.ShipFamilyConfigIndex);
                float zoneR = math.max(0.25f, config.depositZoneRadius);
                float zoneRSq = zoneR * zoneR;

                var planetXf = em.GetComponentData<LocalTransform>(planetEntity);
                float3 planetPos = planetXf.Position;
                float planetSize = math.max(0.25f, planetXf.Scale);
                int slotCount = buffer.Length;

                for (int i = 0; i < slotCount; i++)
                {
                    var slot = buffer[i];
                    int maxLvl = PlanetaryDefenseMath.GetMaxTurretLevelForPlanet(planet.PlanetLevel);
                    if (slot.TurretLevel >= maxLvl)
                        continue;

                    float3 slotPos = PlanetaryDefenseMath.GetSlotWorldPositionNear(
                        shipPos, planetPos, planetSize, planet.PlanetLevel,
                        i, slotCount, mapW, mapH);

                    float3 delta = ToroidalMapEcs.ShortestOffsetXZ(shipPos, slotPos, mapW, mapH);
                    float distSq = math.lengthsq(new float3(delta.x, 0f, delta.z));
                    if (distSq > zoneRSq || distSq >= bestDistSq)
                        continue;

                    bestDistSq = distSq;
                    bestPlanet = planetEntity;
                    bestSlot = i;
                    bestConfig = config;
                }
            }

            return bestPlanet != Entity.Null && bestSlot >= 0;
        }

        void EnsureFamilyConfig()
        {
            if (_familyResolved)
                return;
            _familyConfig = UnityEngine.Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            _familyResolved = true;
        }
    }
}
