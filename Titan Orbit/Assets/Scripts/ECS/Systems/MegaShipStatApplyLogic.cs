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
    /// upgrades, gem cap forced to 0. Each mount fires the catalog unique-component (or
    /// type-table) bullet bank — not the store planet's gameplay family. Fire mode is
    /// Energy Hybrid; Phase B uses <see cref="ShipWeaponFireLogic.TryPlanMegaFire"/>.
    /// Paired with <see cref="ShipStatApplyLogic.ApplyToShip"/> which routes here when
    /// <see cref="MegaShipState.IsMega"/> is true.
    /// </summary>
    public static class MegaShipStatApplyLogic
    {
        static readonly List<Transform> WeaponAssemblyScratch = new List<Transform>(16);

        /// <summary>One-shot dedicated log when MEGA barrels had to use type-table / count fallback.</summary>
        static bool s_LoggedDedicatedWeaponFallback;

        /// <summary>
        /// Applies frozen MEGA stats and resizes the ghosted aim-slot buffer to match weapon mounts.
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

            // --- Weapon hull averages (HUD / display only) ---
            // [TITAN-ORBIT] summedStats.firePower is the sum of every gun for power bars.
            // Live shots use per-mount FirePower / FireRate / BulletSpeed / BulletRange.
            // BulletSimulationSystem Phase B must NOT read hull BulletDamage or BulletSpeed
            // as a per-shot fallback — that inflated every bullet to the fleet total / fastest gun.
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
                weapon.BulletMaxDistance = Mathf.Min(
                    MegaShipCatalog.MaxBulletTravelDistance,
                    Mathf.Max(
                        1f,
                        effective.bulletRange > 0.01f
                            ? effective.bulletRange
                            : MegaShipCatalog.DefaultBulletAcquireRange));
                weapon.BulletLifetime = Mathf.Max(0.25f, weapon.BulletMaxDistance / Mathf.Max(1f, bulletSpeed));
                weapon.ReferenceBulletDamage = firePower;
                weapon.ReferenceBulletSpeed = bulletSpeed;
                // [TITAN-ORBIT] MEGA Phase B volleys when the pool covers the whole bank.
                weapon.FireMode = ShipWeaponFireMode.EnergyHybrid;
                em.SetComponentData(shipEntity, weapon);
            }

            // --- Loadout display bank (first catalog weapon) — live shots use per-mount banks ---
            if (writeGhostedShipState && em.HasComponent<ShipLoadoutState>(shipEntity))
            {
                var loadout = em.GetComponentData<ShipLoadoutState>(shipEntity);
                if (catalog.TryGetFirstWeaponBankIndex(entry, out int firstBank))
                    loadout.RuntimeBulletIndex = firstBank;
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
                // [TITAN-ORBIT] MEGAs have no overdrive. Shift locks heading and aims
                // all MEGA guns at the mouse — keep OD multipliers at 1 so HUD / motor stay flat.
                motor.OverdriveSpeedMultiplier = 1f;
                motor.OverdriveThrustMultiplier = 1f;
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

            // MEGA hull size: tier-7 baseline × this visual family's catalog scale.
            if (em.HasComponent<LocalTransform>(shipEntity))
            {
                var lt = em.GetComponentData<LocalTransform>(shipEntity);
                float hullScale = BodyCollisionMath.GetShipTierScale(7) * catalog.GetScaleForEntry(entry);
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
        /// Overwrites each MEGA mount's firePower / fireRate / bulletRange / bulletSpeed from
        /// the unique component named like that prefab child. Family combat apply runs first
        /// and would otherwise stamp regular-ship numbers onto MEGA barrels.
        /// <para>
        /// Dedicated IL2CPP (Docker / Edgegap) cannot use Editor PrefabUtility names. If the
        /// unique-row lookup misses, type-table stats (or <c>componentCounts</c> when the
        /// hull prefab was stripped) still arm the barrels so Phase B can spawn damage.
        /// </para>
        /// </summary>
        public static void ApplyCatalogWeaponMountStats(
            EntityManager em,
            Entity shipEntity,
            MegaShipCatalog catalog,
            MegaShipCatalogEntry entry)
        {
            if (!em.HasBuffer<ShipWeaponMountElement>(shipEntity) || catalog == null || entry == null)
                return;

            var mounts = em.GetBuffer<ShipWeaponMountElement>(shipEntity);
            if (mounts.Length == 0)
                return;

            int armed = 0;
            bool usedFallback = false;
            if (entry.prefab != null)
                armed = ApplyWeaponStatsFromPrefabAssemblies(
                    em, shipEntity, catalog, entry, mounts, out usedFallback);

            if (armed <= 0)
            {
                armed = ApplyWeaponStatsFromComponentCounts(em, shipEntity, catalog, entry, mounts);
                usedFallback = usedFallback || armed > 0;
            }

            if (usedFallback && !s_LoggedDedicatedWeaponFallback)
            {
                s_LoggedDedicatedWeaponFallback = true;
                Debug.Log(
                    "[MegaShip] Dedicated/player weapon name miss — armed " + armed +
                    "/" + mounts.Length + " barrels from type-table or componentCounts (chassis " +
                    MegaShipCatalog.FormatChassisId(entry.catalogIndex) + ").");
            }

            for (int m = 0; m < mounts.Length; m++)
            {
                var mount = mounts[m];
                if (mount.BulletRange > 0.5f)
                    continue;
                mount.BulletRange = math.min(
                    MegaShipCatalog.MaxBulletTravelDistance,
                    MegaShipCatalog.DefaultBulletAcquireRange);
                mounts[m] = mount;
            }
        }

        /// <summary>
        /// Walks tagged weapon assemblies on the hull prefab. Unique-row lookup uses
        /// prefab-source name in Editor and instance name on dedicated; type-table
        /// fills misses so FirePower is not left at 0.
        /// </summary>
        static int ApplyWeaponStatsFromPrefabAssemblies(
            EntityManager em,
            Entity shipEntity,
            MegaShipCatalog catalog,
            MegaShipCatalogEntry entry,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            out bool usedTypeTableFallback)
        {
            usedTypeTableFallback = false;
            var root = entry.prefab.transform;
            MegaShipPartClassifier.CollectWeaponAssemblies(root, WeaponAssemblyScratch);
            int w = 0;
            for (int i = 0; i < WeaponAssemblyScratch.Count && w < mounts.Length; i++)
            {
                Transform t = WeaponAssemblyScratch[i];
                if (!MegaShipComponentInventory.TryClassifyChild(t, root, out _, out bool isWeapon) || !isWeapon)
                    continue;

                if (!catalog.TryResolveWeaponCombat(
                        t, out MegaShipComponentEntry row, out MegaShipPartStats raw, out string partType))
                    continue;

                if (row == null)
                    usedTypeTableFallback = true;

                WriteMountCombat(catalog, em, shipEntity, mounts, w, row, raw, partType);
                w++;
            }

            return w;
        }

        /// <summary>
        /// Prefab-free arming from the catalogued part list. Used when Dedicated Server
        /// stripping leaves <c>entry.prefab</c> null, or the prefab walk armed nothing.
        /// </summary>
        static int ApplyWeaponStatsFromComponentCounts(
            EntityManager em,
            Entity shipEntity,
            MegaShipCatalog catalog,
            MegaShipCatalogEntry entry,
            DynamicBuffer<ShipWeaponMountElement> mounts)
        {
            if (entry.componentCounts == null)
                return 0;

            int w = 0;
            for (int i = 0; i < entry.componentCounts.Count && w < mounts.Length; i++)
            {
                MegaShipComponentCount count = entry.componentCounts[i];
                if (count == null || count.count <= 0 || string.IsNullOrEmpty(count.displayName))
                    continue;
                if (!catalog.TryGetUniqueComponent(count.displayName, out MegaShipComponentEntry row)
                    || row == null)
                    continue;
                if (!row.isWeapon && !ShipFamilyPartTypes.IsWeapon(row.partType))
                    continue;

                int copies = math.max(1, count.count);
                for (int c = 0; c < copies && w < mounts.Length; c++)
                {
                    WriteMountCombat(
                        catalog, em, shipEntity, mounts, w, row, row.stats, row.partType);
                    w++;
                }
            }

            return w;
        }

        /// <summary>Writes one barrel's catalog / type-table combat fields and ghosted park yaw.</summary>
        static void WriteMountCombat(
            MegaShipCatalog catalog,
            EntityManager em,
            Entity shipEntity,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            int mountIndex,
            MegaShipComponentEntry row,
            in MegaShipPartStats raw,
            string partType)
        {
            // Unique-row firePower stays raw (0 = authored unarmed). Type-table fallback
            // uses the resolved type numbers so dedicated barrels are never mute.
            MegaShipPartStats resolved = catalog.ResolveRuntimeStats(raw);
            var mount = mounts[mountIndex];
            mount.FirePower = math.max(0f, raw.firePower);
            mount.FireRate = math.max(0.15f, resolved.fireRate > 0.01f ? resolved.fireRate : raw.fireRate);
            mount.BulletRange = math.min(
                MegaShipCatalog.MaxBulletTravelDistance,
                math.max(4f, resolved.bulletRange > 0.5f ? resolved.bulletRange : raw.bulletRange));
            float partSpeed = resolved.bulletSpeed > 0.01f ? resolved.bulletSpeed : raw.bulletSpeed;
            mount.BulletSpeed = math.max(0.1f, partSpeed);
            mount.ReferenceFirePower = math.max(0f, raw.firePower);
            mount.WeaponRotationSpeed = 0f;
            mount.BulletBankIndex = row != null
                ? catalog.ResolveWeaponBankIndex(row)
                : catalog.GetTypeTableBankIndex(partType);
            mounts[mountIndex] = mount;
            if (em.HasBuffer<MegaShipGunnerSlotElement>(shipEntity))
            {
                var gunners = em.GetBuffer<MegaShipGunnerSlotElement>(shipEntity);
                MegaShipWeaponAim.WriteGhostedYaw(gunners, mountIndex, mount);
            }
        }

        /// <summary>
        /// On MEGA death: free the planet slot, but keep
        /// <see cref="MegaShipState.IsMega"/> so clients still show the MEGA hull for the
        /// cosmetic breakup. Call <see cref="RestorePreviousHull"/> on respawn.
        /// </summary>
        public static void ReleaseMegaOccupancy(EntityManager em, Entity shipEntity)
        {
            if (!em.HasComponent<MegaShipState>(shipEntity))
                return;

            var mega = em.GetComponentData<MegaShipState>(shipEntity);
            if (!mega.IsMega)
                return;

            MegaShipPlanetLogic.FreeSlot(em, mega.StorePlanetId, mega.MegaSlotIndex);
            if (em.HasBuffer<MegaShipGunnerSlotElement>(shipEntity))
                em.GetBuffer<MegaShipGunnerSlotElement>(shipEntity).Clear();
        }

        /// <summary>
        /// Leaves a live MEGA for a regular family hull (debug tree pick or same-tier family
        /// swap after L7). Frees the unique planet slot and clears
        /// <see cref="MegaShipState.IsMega"/> so <see cref="ShipStatApplyLogic.ApplyToShip"/>
        /// writes the new chassis instead of stamping ShipLevel 7 / MEGA stats again.
        /// Death uses <see cref="RestorePreviousHull"/> instead — that path keeps the old L6.
        /// </summary>
        /// <param name="em">Server entity manager (occupancy is authoritative).</param>
        /// <param name="shipEntity">The ship that is leaving its MEGA hull.</param>
        public static void ClearMegaHull(EntityManager em, Entity shipEntity)
        {
            // --- Leave MEGA without restoring the previous L6 ---
            // [TITAN-ORBIT] Purchase already chose the next family / level / branch.
            // We only drop occupancy + the IsMega flag. ApplyToShip runs after this.
            if (!em.HasComponent<MegaShipState>(shipEntity))
                return;

            var mega = em.GetComponentData<MegaShipState>(shipEntity);
            if (!mega.IsMega)
                return;

            ReleaseMegaOccupancy(em, shipEntity);
            mega = em.GetComponentData<MegaShipState>(shipEntity);
            mega.IsMega = false;
            mega.CatalogIndex = 0;
            mega.StorePlanetId = 0;
            mega.MegaSlotIndex = 0;
            em.SetComponentData(shipEntity, mega);
        }

        /// <summary>
        /// Restores the previous L6 hull after MEGA death: frees the planet slot (idempotent),
        /// clears MEGA flags, and reapplies regular chassis stats.
        /// </summary>
        public static void RestorePreviousHull(EntityManager em, Entity shipEntity)
        {
            if (!em.HasComponent<MegaShipState>(shipEntity) || !em.HasComponent<ShipState>(shipEntity))
                return;

            var mega = em.GetComponentData<MegaShipState>(shipEntity);
            if (!mega.IsMega)
                return;

            ReleaseMegaOccupancy(em, shipEntity);
            mega = em.GetComponentData<MegaShipState>(shipEntity);

            int prevLevel = math.max(1, mega.PreviousLevel);
            int prevBranch = math.max(0, mega.PreviousBranch);
            byte prevFamily = mega.PreviousFamilyIndex;

            mega.IsMega = false;
            mega.CatalogIndex = 0;
            mega.StorePlanetId = 0;
            em.SetComponentData(shipEntity, mega);

            var ship = em.GetComponentData<ShipState>(shipEntity);
            ship.ShipLevel = prevLevel;
            ship.BranchIndex = prevBranch;
            ship.ShipFamilyConfigIndex = prevFamily;
            em.SetComponentData(shipEntity, ship);

            ShipStatApplyLogic.ApplyToShip(em, shipEntity, ship.Team, prevLevel, prevBranch);
        }

        /// <summary>Keeps ghosted MEGA aim slots 1:1 with weapon mounts.</summary>
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
                    CurrentYawDeg = yaw,
                    TargetDistance = 0f,
                    AimWorldX = 0f,
                    AimWorldZ = 0f,
                    TargetGhostId = 0,
                });
            }
        }
    }
}
