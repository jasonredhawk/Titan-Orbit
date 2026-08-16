using TitanOrbit;
using TitanOrbit.Data;
using TitanOrbit.Entities;
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
    /// Server-authoritative ALT rocket fire. Consumes one charge from the first rocket
    /// equipment slot (unless <see cref="TitanOrbitDebugFlags.InfiniteRockets"/>), then
    /// appends a homing <see cref="BulletElement"/> that uses the reserved Rockets bank.
    /// <para>
    /// [TITAN-ORBIT] Reload starts at 3s and grows 0.5s per level
    /// (<see cref="RocketCatalog.DefaultFireCooldownSeconds"/>).
    /// <see cref="ShipLoadoutState.NextRocketFireTime"/> is ghosted so the HUD can show the wait.
    /// Infinite-rocket debug skips the charge consume, keeps the cooldown, and fires at
    /// the ship's live level (not the pack's stamped <c>ItemLevel</c>).
    /// </para>
    /// Paired with <see cref="BulletSimulationSystem"/> (homing + hits) and
    /// <c>RocketLoadoutHUD</c> (client readout). World: ServerSimulation.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedFixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(BulletSimulationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipRocketFireSystem : ISystem
    {
        /// <summary>Caches the reserved Rockets bank index (or 0 if the category is missing).</summary>
        int _rocketBankIndex;

        /// <summary>Warm the bank index once — categories do not change at runtime.</summary>
        public void OnCreate(ref SystemState state)
        {
            int found = BulletBankProfileUtility.FindRocketBankIndex();
            _rocketBankIndex = found >= 0 ? found : 0;
            state.RequireForUpdate<NetworkStreamInGame>();
            state.RequireForUpdate<ActiveBulletsTag>();
        }

        /// <summary>
        /// For each ship with FireRocket this tick: validate, consume, spawn a homing rocket.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity))
                return;
            if (!state.EntityManager.HasBuffer<BulletElement>(bulletEntity) ||
                !state.EntityManager.HasBuffer<BulletSpawnEventElement>(bulletEntity))
                return;

            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState) ||
                !ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight))
                return;

            double serverElapsed = SystemAPI.Time.ElapsedTime;
            var bullets = state.EntityManager.GetBuffer<BulletElement>(bulletEntity);
            var spawnEvents = state.EntityManager.GetBuffer<BulletSpawnEventElement>(bulletEntity);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (input, shipState, loadout, transform, ghostOwner, entity) in SystemAPI
                         .Query<RefRO<ShipInput>, RefRO<ShipState>, RefRW<ShipLoadoutState>,
                             RefRO<LocalTransform>, RefRO<GhostOwner>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (!input.ValueRO.FireRocket.IsSet)
                    continue;
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                // --- Possession / orbit gates (same as ship guns) ---
                if (SystemAPI.HasComponent<ShipTurretControlState>(entity) &&
                    SystemAPI.GetComponentRO<ShipTurretControlState>(entity).ValueRO.IsControlling)
                    continue;
                if (SystemAPI.HasComponent<ShipMegaGunControlState>(entity) &&
                    SystemAPI.GetComponentRO<ShipMegaGunControlState>(entity).ValueRO.IsControlling)
                    continue;
                if (SystemAPI.HasComponent<ShipOrbitState>(entity) &&
                    SystemAPI.GetComponentRO<ShipOrbitState>(entity).ValueRO.InOrbitRing)
                    continue;

                bool infinite = TitanOrbitDebugFlags.InfiniteRockets;
                if (!TryResolveRocketShot(
                        state.EntityManager, entity, infinite,
                        input.ValueRO.SelectedRocketSlot,
                        out int itemLevel, out int consumeSlot))
                    continue;

                RocketShotMath.Resolve(
                    itemLevel,
                    out var stats,
                    out float damage,
                    out float bulletSpeed,
                    out float maxDistance,
                    out float lifetime,
                    out float visualScale,
                    out int resolvedBank,
                    out int extras);
                if (resolvedBank >= 0)
                    _rocketBankIndex = resolvedBank;

                if (loadout.ValueRO.NextRocketFireTime > serverElapsed + 0.0001)
                    continue;

                // --- Consume one charge (debug infinite skips this) ---
                if (!infinite && consumeSlot >= 0)
                    ConsumeRocketCharge(state.EntityManager, entity, consumeSlot);

                loadout.ValueRW.NextRocketFireTime = serverElapsed + stats.fireCooldown;

                // --- Spawn a Rockets-bank homing bullet from the hull nose ---
                float3 fireForward = math.forward(transform.ValueRO.Rotation);
                fireForward.y = 0f;
                if (math.lengthsq(fireForward) < 0.0001f)
                    fireForward = new float3(0f, 0f, 1f);
                else
                    fireForward = math.normalize(fireForward);

                float3 fireOrigin = transform.ValueRO.Position;
                fireOrigin.y = transform.ValueRO.Position.y;

                // Catalog speed only — do not add ship velocity (guns do; rockets do not).
                float3 bulletVel = fireForward * math.max(1f, bulletSpeed);
                uint sequence = BulletVfxBridge.NextSequence();
                var spawn = new BulletElement
                {
                    Position = fireOrigin,
                    Velocity = bulletVel,
                    MaxDistance = math.max(10f, maxDistance),
                    Lifetime = math.max(0.1f, lifetime),
                    Damage = math.max(0.1f, damage),
                    OwnerNetworkId = ghostOwner.ValueRO.NetworkId,
                    OwnerTeam = (byte)shipState.ValueRO.Team,
                    Sequence = sequence,
                    BankIndex = _rocketBankIndex,
                    ScaleMultiplier = visualScale,
                    FirePowerExtraLevels = extras,
                    StrengthScale = 1f,
                    DamageFilter = BulletDamageFilter.Everything,
                    Homing = 1,
                    TurnSpeedDeg = stats.turnSpeedDegreesPerSecond,
                    AcquireRange = stats.acquireRange > 0.01f
                        ? stats.acquireRange
                        : RocketCatalog.DefaultAcquireRange,
                };

                spawnEvents.Add(new BulletSpawnEventElement
                {
                    SpawnPosition = spawn.Position,
                    Velocity = spawn.Velocity,
                    Lifetime = spawn.Lifetime,
                    MaxDistance = spawn.MaxDistance,
                    Damage = spawn.Damage,
                    OwnerTeam = spawn.OwnerTeam,
                    Sequence = spawn.Sequence,
                    BankIndex = spawn.BankIndex,
                    ScaleMultiplier = spawn.ScaleMultiplier,
                });

                BulletNetNotify.SendSpawn(ref ecb, spawn, DroneSwarmLogic.NoWeaponMountReproject);
                bullets.Add(spawn);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>
        /// Picks the HUD-selected rocket pack. Infinite debug skips the charge consume;
        /// a selected pack still fires at that pack's stamped level. Empty loadout + infinite
        /// uses the ship's live level.
        /// </summary>
        static bool TryResolveRocketShot(
            EntityManager em,
            Entity shipEntity,
            bool infinite,
            int selectedHudIndex,
            out int itemLevel,
            out int consumeSlot)
        {
            itemLevel = 1;
            consumeSlot = -1;

            int shipLevel = 1;
            if (em.HasComponent<ShipState>(shipEntity))
                shipLevel = math.max(1, em.GetComponentData<ShipState>(shipEntity).ShipLevel);

            if (!em.HasBuffer<EquippedEquipmentElement>(shipEntity))
            {
                if (!infinite)
                    return false;
                itemLevel = shipLevel;
                return true;
            }

            var buffer = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
            int want = math.max(0, selectedHudIndex);
            int rocketCount = 0;
            int lastBuffer = -1;
            int lastLevel = 1;
            int matchBuffer = -1;
            int matchLevel = 1;
            for (int i = 0; i < buffer.Length; i++)
            {
                var entry = buffer[i];
                if (!StoreItemData.IsRocket((StoreItemType)entry.ItemType))
                    continue;
                if (entry.RemainingCharges <= 0)
                    continue;

                int packLevel = math.max(1, entry.ItemLevel);
                lastBuffer = i;
                lastLevel = packLevel;
                if (rocketCount == want)
                {
                    matchBuffer = i;
                    matchLevel = packLevel;
                }

                rocketCount++;
            }

            if (rocketCount > 0)
            {
                // Past-the-end caret clamps to the last pack.
                itemLevel = matchBuffer >= 0 ? matchLevel : lastLevel;
                consumeSlot = matchBuffer >= 0 ? matchBuffer : lastBuffer;
                return true;
            }

            if (!infinite)
                return false;

            itemLevel = shipLevel;
            return true;
        }

        /// <summary>Decrements charges; removes the slot when the pack is empty so it frees.</summary>
        static void ConsumeRocketCharge(EntityManager em, Entity shipEntity, int slotIndex)
        {
            var buffer = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
            if (slotIndex < 0 || slotIndex >= buffer.Length)
                return;

            var entry = buffer[slotIndex];
            entry.RemainingCharges = math.max(0, entry.RemainingCharges - 1);
            if (entry.RemainingCharges <= 0)
                buffer.RemoveAt(slotIndex);
            else
                buffer[slotIndex] = entry;
        }
    }
}
