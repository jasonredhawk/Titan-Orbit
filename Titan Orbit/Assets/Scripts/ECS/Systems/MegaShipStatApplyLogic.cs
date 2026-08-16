using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Writes static MEGA motor / weapon / vitals onto a ship. No Extra Level, no attribute
    /// upgrades, gem cap forced to 0. Bullet bank and fire mode come from the store planet's
    /// gameplay family (AstroEagle, CosmicShark, …), not from the MEGA visual line.
    /// Paired with <see cref="ShipStatApplyLogic.ApplyToShip"/> which routes here when
    /// <see cref="MegaShipState.IsMega"/> is true.
    /// </summary>
    public static class MegaShipStatApplyLogic
    {
        static readonly List<Transform> WeaponAssemblyScratch = new List<Transform>(16);

        /// <summary>
        /// Applies frozen MEGA stats and resizes the gunner-pad buffer to match weapon mounts.
        /// </summary>
        public static void ApplyToShip(
            EntityManager em,
            Entity shipEntity,
            in MegaShipState mega,
            int familyIndex,
            bool writeGhostedShipState)
        {
            var catalog = MegaShipCatalog.Load();
            if (catalog == null || !catalog.TryGetEntry(mega.CatalogIndex, out MegaShipCatalogEntry entry)
                || entry == null)
                return;

            string chassisId = MegaShipCatalog.FormatChassisId(mega.CatalogIndex);
            MegaShipStatsCalculator.SumFromEntry(entry, catalog, out ShipComponentAbilityStats effective);
            effective.maxGems = 0f;

            // --- Caps (server / authoritative only) ---
            if (writeGhostedShipState && em.HasComponent<ShipState>(shipEntity))
            {
                var ship = em.GetComponentData<ShipState>(shipEntity);
                float prevHealthRatio = ship.MaxHealth > 0.01f ? ship.Health / ship.MaxHealth : 1f;
                float prevEnergyRatio = ship.MaxEnergy > 0.01f ? ship.CurrentEnergy / ship.MaxEnergy : 1f;
                ship.MaxHealth = Mathf.Max(1f, effective.healthCap);
                ship.GemCapacity = 0f;
                ship.CurrentGems = 0f;
                ship.MaxEnergy = Mathf.Clamp(
                    effective.energyCap > 0.01f ? effective.energyCap : MegaShipCatalog.DefaultHullEnergy,
                    MegaShipCatalog.MinHullEnergy,
                    MegaShipCatalog.MaxHullEnergy);
                ship.PeopleCapacity = Mathf.Max(
                    Mathf.RoundToInt(MegaShipCatalog.MinHullPeople),
                    Mathf.RoundToInt(effective.maxPeople));
                ship.ShipLevel = 7;
                ship.BranchIndex = mega.MegaSlotIndex;
                ship.Health = ship.AwaitingTeamSelection || ship.Health <= 0.01f
                    ? ship.MaxHealth
                    : Mathf.Clamp(ship.MaxHealth * prevHealthRatio, 1f, ship.MaxHealth);
                ship.CurrentEnergy = ship.AwaitingTeamSelection
                    ? ship.MaxEnergy
                    : Mathf.Clamp(ship.MaxEnergy * prevEnergyRatio, 0f, ship.MaxEnergy);
                ship.CurrentPeople = Mathf.Min(ship.CurrentPeople, ship.PeopleCapacity);
                em.SetComponentData(shipEntity, ship);
            }

            // --- Weapon hull averages (HUD / display only — live shots use per-mount FirePower) ---
            // [TITAN-ORBIT] summedStats.firePower is the sum of every gun for power bars.
            // BulletSimulationSystem Phase B must NOT read BulletDamage
            // as a per-shot fallback — that inflated every bullet to the fleet total.
            if (em.HasComponent<ShipWeaponConfig>(shipEntity))
            {
                float firePower = Mathf.Max(0f, effective.firePower);
                float fireRate = Mathf.Max(0.1f, effective.fireRate);
                float bulletSpeed = Mathf.Max(0.1f, effective.bulletSpeed);
                var weapon = em.GetComponentData<ShipWeaponConfig>(shipEntity);
                weapon.FireRate = fireRate;
                weapon.BulletSpeed = bulletSpeed;
                weapon.BulletDamage = firePower;
                weapon.EnergyCostPerShot = firePower;
                weapon.BulletMaxDistance = Mathf.Max(
                    1f,
                    effective.bulletRange > 0.01f
                        ? effective.bulletRange
                        : MegaShipCatalog.DefaultBulletAcquireRange);
                weapon.BulletLifetime = Mathf.Max(0.25f, weapon.BulletMaxDistance / Mathf.Max(1f, bulletSpeed));
                weapon.ReferenceBulletDamage = firePower;
                weapon.ReferenceBulletSpeed = bulletSpeed;
                weapon.FireMode = ShipWeaponFireMode.EnergyHybrid;
                if (TryGetFamily(familyIndex, out ShipFamilyDefinition family) && family != null)
                    weapon.FireMode = family.weaponFireMode;
                em.SetComponentData(shipEntity, weapon);
            }

            // --- Family bullet bank (CosmicShark MEGA shoots CosmicShark rounds) ---
            if (writeGhostedShipState &&
                em.HasComponent<ShipLoadoutState>(shipEntity) &&
                TryGetFamily(familyIndex, out ShipFamilyDefinition bankFamily) &&
                bankFamily != null)
            {
                var loadout = em.GetComponentData<ShipLoadoutState>(shipEntity);
                loadout.RuntimeBulletIndex = BulletBankProfileUtility.ResolveBankIndexForFamily(bankFamily);
                loadout.BranchIndex = mega.MegaSlotIndex;
                loadout.ChassisIndex = mega.MegaSlotIndex;
                em.SetComponentData(shipEntity, loadout);
            }

            // --- Slow beast motor ---
            if (em.HasComponent<ShipMotorConfig>(shipEntity))
            {
                float moveVal = Mathf.Max(0.1f, effective.moveSpeed);
                float turnVal = ShipPropulsionAggregation.ConvertTurnDefinitionToDegreesPerSecond(effective.turnSpeed);
                float accel = Mathf.Max(0.1f, effective.accelerationCap > 0f
                    ? effective.accelerationCap
                    : moveVal * 0.25f);

                var motor = em.GetComponentData<ShipMotorConfig>(shipEntity);
                motor.MaxSpeed = moveVal;
                motor.EngineThrust = accel;
                motor.RotationSpeed = turnVal;
                motor.BrakeDeceleration = ShipMassLogic.DefaultBrakeDeceleration;
                motor.Mass = MegaShipCatalog.DefaultHullCollisionMass;
                motor.HullMassReference = Mathf.Max(
                    MegaShipCatalog.MinHullCollisionMass,
                    ShipMassLogic.ComputeHullMassReference(
                        Mathf.Max(MegaShipCatalog.MinHullCollisionMass, effective.healthCap * 0.35f),
                        MegaShipCatalog.DefaultHullCollisionMass));
                motor.ChassisReferenceHealth = Mathf.Max(1f, effective.healthCap);
                motor.RammingPower = Mathf.Max(0f, effective.rammingPower);
                motor.ThrustEnergyDrainPerSecond = 2f;
                motor.OverdriveSpeedMultiplier = 1.25f;
                motor.OverdriveThrustMultiplier = 1.25f;
                motor.OverdriveEnergyDrainMultiplier = 1f;
                motor.SkipMassTax = 1;
                em.SetComponentData(shipEntity, motor);
            }

            var vitals = new ShipVitalsConfig
            {
                HealthRegenPerSecond = Mathf.Max(0f, effective.healthRegen),
                EnergyRegenPerSecond = Mathf.Clamp(
                    effective.energyRegen > 0.01f ? effective.energyRegen : MegaShipCatalog.DefaultHullEnergyRegen,
                    MegaShipCatalog.MinHullEnergyRegen,
                    MegaShipCatalog.MaxHullEnergyRegen),
                HealthRegenDelayAfterDamage = 0.35f,
            };
            if (em.HasComponent<ShipVitalsConfig>(shipEntity))
                em.SetComponentData(shipEntity, vitals);
            else
                em.AddComponentData(shipEntity, vitals);

            // MEGA hull size: tier-7 baseline × catalog globalScale (default 0.2 ≈ 5× smaller).
            if (em.HasComponent<LocalTransform>(shipEntity))
            {
                var lt = em.GetComponentData<LocalTransform>(shipEntity);
                float hullScale = BodyCollisionMath.GetShipTierScale(7) * catalog.GetGlobalScale();
                if (!Mathf.Approximately(lt.Scale, hullScale))
                {
                    lt.Scale = hullScale;
                    em.SetComponentData(shipEntity, lt);
                }
            }

            var chassisState = new ShipChassisState
            {
                ChassisId = chassisId,
                AppliedShipLevel = 7,
                AppliedBranchIndex = mega.MegaSlotIndex,
                AppliedShipFamilyConfigIndex = (byte)familyIndex,
                AppliedAttributeSum = 0,
                AppliedEquipmentFingerprint = 0,
            };
            if (em.HasComponent<ShipChassisState>(shipEntity))
                em.SetComponentData(shipEntity, chassisState);
            else
                em.AddComponentData(shipEntity, chassisState);

            ResizeGunnerSlots(em, shipEntity);
            ApplyCatalogWeaponMountStats(em, shipEntity, catalog, entry);
        }

        /// <summary>
        /// Overwrites each MEGA mount's firePower / fireRate / bulletRange from the unique
        /// component named like that prefab child. Family combat apply runs first and would
        /// otherwise stamp regular-ship numbers onto MEGA barrels.
        /// </summary>
        public static void ApplyCatalogWeaponMountStats(
            EntityManager em,
            Entity shipEntity,
            MegaShipCatalog catalog,
            MegaShipCatalogEntry entry)
        {
            if (!em.HasBuffer<ShipWeaponMountElement>(shipEntity) || catalog == null || entry?.prefab == null)
                return;

            var mounts = em.GetBuffer<ShipWeaponMountElement>(shipEntity);
            var root = entry.prefab.transform;
            MegaShipPartClassifier.CollectWeaponAssemblies(root, WeaponAssemblyScratch);
            int w = 0;
            for (int i = 0; i < WeaponAssemblyScratch.Count && w < mounts.Length; i++)
            {
                Transform t = WeaponAssemblyScratch[i];
                if (!MegaShipComponentInventory.TryClassifyChild(t, root, out _, out bool isWeapon) || !isWeapon)
                    continue;

                string id = MegaShipPartClassifier.GetPrefabAssetName(t);
                if (!catalog.TryGetUniqueComponent(id, out MegaShipComponentEntry row) || row == null)
                    continue;

                // Resolve traverse / cadence / range through catalog defaults; firePower stays raw
                // so a 0 unique-component row is an unarmed mount (no 0.1 floor, no hull-sum stamp).
                MegaShipPartStats resolved = catalog.ResolveRuntimeStats(row.stats);
                var mount = mounts[w];
                mount.FirePower = math.max(0f, row.stats.firePower);
                mount.FireRate = math.max(0.15f, resolved.fireRate > 0.01f ? resolved.fireRate : row.stats.fireRate);
                mount.BulletRange = math.max(4f, resolved.bulletRange > 0.5f ? resolved.bulletRange : row.stats.bulletRange);
                mount.ReferenceFirePower = math.max(0f, row.stats.firePower);
                mount.WeaponRotationSpeed = 0f;
                mounts[w] = mount;
                if (em.HasBuffer<MegaShipGunnerSlotElement>(shipEntity))
                {
                    var gunners = em.GetBuffer<MegaShipGunnerSlotElement>(shipEntity);
                    MegaShipWeaponAim.WriteGhostedYaw(gunners, w, mount);
                }
                w++;
            }

            for (int m = w; m < mounts.Length; m++)
            {
                var mount = mounts[m];
                if (mount.BulletRange > 0.5f)
                    continue;
                mount.BulletRange = MegaShipCatalog.DefaultBulletAcquireRange;
                mounts[m] = mount;
            }
        }

        /// <summary>
        /// Restores the previous L6 hull after MEGA death: frees the planet slot, clears MEGA
        /// flags, and reapplies regular chassis stats.
        /// </summary>
        public static void RestorePreviousHull(EntityManager em, Entity shipEntity)
        {
            if (!em.HasComponent<MegaShipState>(shipEntity) || !em.HasComponent<ShipState>(shipEntity))
                return;

            var mega = em.GetComponentData<MegaShipState>(shipEntity);
            if (!mega.IsMega)
                return;

            MegaShipPlanetLogic.FreeSlot(em, mega.StorePlanetId, mega.MegaSlotIndex);
            MegaShipGunnerLogic.EjectAllGunners(em, shipEntity);

            int prevLevel = math.max(1, mega.PreviousLevel);
            int prevBranch = math.max(0, mega.PreviousBranch);
            byte prevFamily = mega.PreviousFamilyIndex;

            mega.IsMega = false;
            mega.GunsLocked = false;
            mega.CatalogIndex = 0;
            mega.StorePlanetId = 0;
            em.SetComponentData(shipEntity, mega);

            var ship = em.GetComponentData<ShipState>(shipEntity);
            ship.ShipLevel = prevLevel;
            ship.BranchIndex = prevBranch;
            ship.ShipFamilyConfigIndex = prevFamily;
            em.SetComponentData(shipEntity, ship);

            if (em.HasBuffer<MegaShipGunnerSlotElement>(shipEntity))
                em.GetBuffer<MegaShipGunnerSlotElement>(shipEntity).Clear();

            ShipStatApplyLogic.ApplyToShip(em, shipEntity, ship.Team, prevLevel, prevBranch);
        }

        /// <summary>Keeps gunner slots 1:1 with weapon mounts (empty occupancy).</summary>
        public static void ResizeGunnerSlots(EntityManager em, Entity shipEntity)
        {
            if (!em.HasBuffer<MegaShipGunnerSlotElement>(shipEntity))
                em.AddBuffer<MegaShipGunnerSlotElement>(shipEntity);

            int mountCount = 0;
            if (em.HasBuffer<ShipWeaponMountElement>(shipEntity))
                mountCount = em.GetBuffer<ShipWeaponMountElement>(shipEntity).Length;

            var gunners = em.GetBuffer<MegaShipGunnerSlotElement>(shipEntity);
            if (gunners.Length == mountCount)
                return;

            var mounts = em.HasBuffer<ShipWeaponMountElement>(shipEntity)
                ? em.GetBuffer<ShipWeaponMountElement>(shipEntity)
                : default;
            gunners.Clear();
            for (int i = 0; i < mountCount; i++)
            {
                float yaw = 0f;
                if (mounts.IsCreated && i < mounts.Length)
                    yaw = MegaShipWeaponAim.GetLocalYawDeg(mounts[i].LocalRotation);
                gunners.Add(new MegaShipGunnerSlotElement
                {
                    MountIndex = (byte)i,
                    OccupiedByNetworkId = 0,
                    CurrentYawDeg = yaw,
                });
            }
        }

        static bool TryGetFamily(int familyIndex, out ShipFamilyDefinition family)
        {
            family = null;
            var config = ShipStatApplyLogic.Config;
            if (config == null)
                return false;
            var entry = config.GetFamilyByConfigIndex(familyIndex);
            family = entry != null ? entry.shipFamilyDefinition : null;
            return family != null;
        }
    }
}
