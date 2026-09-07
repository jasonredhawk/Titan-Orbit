using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Entities;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: while a ship has <see cref="ShipTurretControlState.IsControlling"/>, Aim + Fire from
    /// ghosted <see cref="ShipInput"/> drive that pad's muzzle. AI combat skips occupied slots
    /// (<see cref="PlanetaryDefenseCombatSystem"/>). Uses the same bullet append path and
    /// <see cref="PlanetaryDefenseConfig.GetCombatLevelStats"/> bank-scaled recipe as AI
    /// turrets, but aim direction is the player's planar aim (no lead solve).
    /// <para>
    /// World: ServerSimulation. Runs after AI combat so player shots resolve next tick with
    /// the shared bullet buffer.
    /// </para>
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PlanetaryDefenseCombatSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class PlanetaryDefensePlayerCombatSystem : SystemBase
    {
        /// <summary>Per-ship next-fire time keyed by ship entity index.</summary>
        readonly Dictionary<int, float> _nextFireTime = new Dictionary<int, float>(32);

        PlanetShipFamilyConfig _familyConfig;
        BulletVfxBank _vfxBank;
        bool _warmed;

        /// <summary>Require bullets + map.</summary>
        protected override void OnCreate()
        {
            RequireForUpdate<ActiveBulletsTag>();
            RequireForUpdate<MapStateSingleton>();
        }

        /// <summary>Fire occupied turrets from controlling ship input.</summary>
        protected override void OnUpdate()
        {
            if (!SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity))
                return;
            if (!EntityManager.HasBuffer<BulletElement>(bulletEntity) ||
                !EntityManager.HasBuffer<BulletSpawnEventElement>(bulletEntity))
                return;

            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var map) ||
                !ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
                return;

            EnsureWarmed();

            // Map size validated above — player aim is planar and does not need lead/toroidal solve.
            float now = (float)SystemAPI.Time.ElapsedTime;

            var bullets = EntityManager.GetBuffer<BulletElement>(bulletEntity);
            var spawnEvents = EntityManager.GetBuffer<BulletSpawnEventElement>(bulletEntity);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            float categoryUpgradeScale = 1f;
            if (_vfxBank != null)
                BulletVisualScale.ActiveUpgradeVisualScaleMultiplier = _vfxBank.UpgradeVisualScaleMultiplier;

            foreach (var (control, input, ghostOwner, shipEntity) in SystemAPI
                         .Query<RefRO<ShipTurretControlState>, RefRO<ShipInput>, RefRO<GhostOwner>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (!control.ValueRO.IsControlling)
                    continue;
                if (!input.ValueRO.Fire.IsSet)
                    continue;

                if (!PlanetaryDefenseTurretControlLogic.TryFindPlanetById(
                        EntityManager, control.ValueRO.PlanetId, out Entity planetEntity))
                    continue;
                if (!EntityManager.HasComponent<PlanetState>(planetEntity) ||
                    !EntityManager.HasComponent<LocalTransform>(planetEntity) ||
                    !EntityManager.HasBuffer<PlanetaryDefenseSlotElement>(planetEntity))
                    continue;

                var planet = EntityManager.GetComponentData<PlanetState>(planetEntity);
                var buffer = EntityManager.GetBuffer<PlanetaryDefenseSlotElement>(planetEntity);
                int slotIndex = control.ValueRO.SlotIndex;
                if (slotIndex < 0 || slotIndex >= buffer.Length)
                    continue;

                var slot = buffer[slotIndex];
                if (slot.TurretLevel == 0 || slot.Health <= 0f)
                    continue;
                if (slot.OccupiedByNetworkId != ghostOwner.ValueRO.NetworkId)
                    continue;

                var config = PlanetaryDefenseConfig.ResolveForFamily(
                    _familyConfig, planet.ShipFamilyConfigIndex);
                ShipFamilyDefinition familyDef = PlanetaryDefenseConfig.ResolveFamilyDefinition(
                    _familyConfig, planet.ShipFamilyConfigIndex);
                int bankIndex = config.ResolveBulletBankIndex(familyDef);

                // --- Recipe defaults × bank profile (same as AI combat) ---
                var stats = config.GetCombatLevelStats(slot.TurretLevel, bankIndex);
                float fireRate = math.max(0.05f, stats.fireRate);
                int cooldownKey = shipEntity.Index;

                // --- Aim from player ShipInput (planar) ---
                float2 aim2 = input.ValueRO.AimPlanarDir;
                if (math.lengthsq(aim2) < 0.0001f)
                    continue;
                float3 aim = math.normalize(new float3(aim2.x, 0f, aim2.y));

                var xf = EntityManager.GetComponentData<LocalTransform>(planetEntity);
                float3 muzzle = PlanetaryDefenseMath.GetSlotWorldPosition(
                    xf.Position,
                    math.max(0.25f, xf.Scale),
                    planet.PlanetLevel,
                    slotIndex,
                    buffer.Length);
                muzzle.y = PlanetaryDefenseMath.FixedY;

                float bulletSpeed = math.max(1f, stats.bulletSpeed);
                float damage = math.max(0.05f, stats.damage);
                float engageRange = math.max(0.5f, stats.engageRange);
                int firePowerAbilityLv = 0;
                if (EntityManager.HasComponent<ShipAttributeUpgradeState>(shipEntity))
                    firePowerAbilityLv = EntityManager.GetComponentData<ShipAttributeUpgradeState>(shipEntity).FirePower;
                int firePowerExtras = 0;
                if (EntityManager.HasComponent<ShipState>(shipEntity))
                {
                    firePowerExtras = BulletBankCombatLogic.CountFirePowerExtraLevels(
                        EntityManager.GetComponentData<ShipState>(shipEntity).ShipLevel, firePowerAbilityLv);
                }

                if (_nextFireTime.TryGetValue(cooldownKey, out float next) && now < next)
                    continue;
                if (_vfxBank != null)
                    categoryUpgradeScale = _vfxBank.GetCategoryUpgradeVisualScaleMultiplier(bankIndex);

                float referenceDamage = math.max(0.1f, config.GetLevelStats(1).damage);
                float visualScale = BulletVisualScale.ComputePerShotScale(
                    config.bulletVisualScale,
                    damage,
                    bulletSpeed,
                    referenceDamage,
                    bulletSpeed,
                    categoryUpgradeScale);

                float3 bulletVel = aim * bulletSpeed;
                uint sequence = BulletVfxBridge.NextSequence();
                // Manual aim — flight budget is engage range (no lead intercept extension).
                float burnRangeMul = BulletBankCombatLogic.GetBurnBulletRangeMultiplier(
                    bankIndex, firePowerExtras);
                float maxDistance = engageRange * burnRangeMul;
                byte ownerTeam = (byte)planet.Ownership;

                var spawn = new BulletElement
                {
                    Position = muzzle,
                    Velocity = bulletVel,
                    MaxDistance = maxDistance,
                    Lifetime = 0f,
                    Damage = damage,
                    // [TITAN-ORBIT] Credit the piloting player for kills / attribution.
                    OwnerNetworkId = ghostOwner.ValueRO.NetworkId,
                    OwnerTeam = ownerTeam,
                    Sequence = sequence,
                    BankIndex = math.max(0, bankIndex),
                    ScaleMultiplier = math.max(0.1f, visualScale),
                    DamageFilter = BulletDamageFilter.ShipsAndTransports,
                    FirePowerExtraLevels = firePowerExtras,
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

                BulletNetNotify.SendSpawn(
                    ref ecb, spawn, mountIndex: DroneSwarmLogic.NoWeaponMountReproject);
                bullets.Add(spawn);
                _nextFireTime[cooldownKey] = now + (1f / fireRate);
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();
        }

        void EnsureWarmed()
        {
            if (_warmed)
                return;
            _familyConfig = Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");
            _vfxBank = BulletVfxBank.LoadDefault();
            _warmed = true;
        }

    }
}
