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
    /// Writes MEGA / Titan motor / weapon / vitals onto a ship. Hull parts stay frozen
    /// (no Extra Level, no attribute upgrades). Equipped moon-store ship components in
    /// LOADOUT slots add PerExtra × shipLevel only (no Base) onto those frozen totals.
    /// Gem cap stays 0. Cannon / missile / sniper mounts fire the catalog unique-component
    /// (or type-table) bank. Titan Bullet mounts follow the B-key hull cycle
    /// (<see cref="ShipLoadoutState.RuntimeBulletIndex"/>): original catalog
    /// "Bullets" keep that barrel's catalog firepower (no Extra Level). The
    /// ship-family weapon and purchased planet guns use
    /// <see cref="TryResolveTitanFamilyWeaponFirepower"/> instead.
    /// Fire mode is Energy Hybrid;
    /// Phase B uses <see cref="ShipWeaponFireLogic.TryPlanMegaFire"/>.
    /// The planet's ship-family fleet bonuses (home 1×, other families this match's
    /// half-to-double roll) multiply the frozen hull and each barrel. Shift stays
    /// heading-lock — Titans do not gain overdrive from the family's overdrive roll.
    /// Paired with <see cref="ShipStatApplyLogic.ApplyToShip"/> which routes here when
    /// <see cref="MegaShipState.IsMega"/> is true.
    /// </summary>
    public static class MegaShipStatApplyLogic
    {
        static readonly List<Transform> WeaponAssemblyScratch = new List<Transform>(16);

        /// <summary>One-shot dedicated log when MEGA barrels had to use type-table / count fallback.</summary>
        static bool s_LoggedDedicatedWeaponFallback;

        /// <summary>
        /// Chassis tier plugged into the family-weapon Extra Level formula.
        /// Regular ships stop at 6. Titans evaluate family and gear guns as a
        /// fully maxed level-7 ship.
        /// </summary>
        public const int TitanFamilyWeaponShipLevel = 7;

        /// <summary>
        /// Fire Power ability purchases on that maxed level-7 ship.
        /// Together with <see cref="TitanFamilyWeaponShipLevel"/> the step count is 14.
        /// </summary>
        public const int TitanFamilyWeaponAbilityLevel = 7;

        /// <summary>
        /// Last family-weapon firepower for one Titan and one B-key bank.
        /// Holding fire must not re-walk gear ids (those strings allocate).
        /// </summary>
        struct FamilyWeaponFirepowerCache
        {
            public World World;
            public Entity Ship;
            public int Bank;
            public int AbilityLevel;
            public int Fingerprint;
            public bool Ok;
            public float Firepower;
            public float Base;
            public float PerExtra;
            public float FireRate;
            public float BulletSpeed;
            public float BulletRange;
        }

        static FamilyWeaponFirepowerCache s_FamilyWeaponFirepowerCache;

        /// <summary>
        /// Firepower for a Titan gun barrel that is not on the catalog default bullet.
        /// Regular ships never call this — their mounts already Extra-Level at ship level 1–6.
        /// </summary>
        /// <remarks>
        /// [TITAN-ORBIT] The default Titan gun keeps the firepower already on its catalog asset.
        /// No Extra Level formula is applied, and this method does not write a firepower number
        /// for that bank. Toggling to a ship-family weapon or a gear-slot weapon uses that
        /// weapon's own base and per-extra, evaluated as a fully maxed level-7 ship:
        /// <c>base + perExtra × (7 + 7) = base + perExtra × 14</c>.
        /// Base 3 and per-extra 0.75 is 13.5. Bullet speed, fire rate, and bullet range
        /// use that same maxed ship (<see cref="ShipAbilityLevelCounts.Maxed"/> at level 7)
        /// through each stat's own Extra Level rule. Regular ships never call this.
        /// </remarks>
        /// <param name="em">World that owns <paramref name="shipEntity"/>.</param>
        /// <param name="shipEntity">Titan whose B-key bank is firing.</param>
        /// <param name="bankIndex">Bullet bank this shot will use.</param>
        /// <param name="firepower">Level-7 family firepower, or 0 when this shot keeps the catalog gun.</param>
        /// <param name="baseFirePower">Authored family-weapon base (before Extra Level).</param>
        /// <param name="perExtraLevel">Authored fire power per Extra Level.</param>
        /// <param name="fireRate">Maxed level-7 fire rate. 0 when this bank keeps the asset rate.</param>
        /// <param name="bulletSpeed">Maxed level-7 muzzle speed. 0 when this bank keeps the asset speed.</param>
        /// <param name="bulletRange">Maxed level-7 travel range. 0 when this bank keeps the asset range.</param>
        /// <returns>True when <paramref name="bankIndex"/> is a family weapon and not the Titan's own default gun.</returns>
        public static bool TryResolveTitanFamilyWeaponFirepower(
            EntityManager em,
            Entity shipEntity,
            int bankIndex,
            out float firepower,
            out float baseFirePower,
            out float perExtraLevel,
            out float fireRate,
            out float bulletSpeed,
            out float bulletRange)
        {
            firepower = 0f;
            baseFirePower = 0f;
            perExtraLevel = 0f;
            fireRate = 0f;
            bulletSpeed = 0f;
            bulletRange = 0f;

            // Regular hulls, and a Titan still on its catalog Bullets, keep the mount value.
            if (!em.Exists(shipEntity)
                || !em.HasComponent<MegaShipState>(shipEntity)
                || !em.GetComponentData<MegaShipState>(shipEntity).IsMega)
                return false;

            if (BulletBankOwnership.TryResolveMegaOriginalGunBank(em, shipEntity, out int titanGun)
                && bankIndex == titanGun)
                return false;

            // Fully maxed: ship level 7 plus 7 upgrade abilities. Not the Titan's live purchases.
            const int abilityLevel = TitanFamilyWeaponAbilityLevel;

            int fingerprint = ShipStatApplyLogic.ComputeEquippedLoadoutFingerprint(em, shipEntity);
            if (s_FamilyWeaponFirepowerCache.World == em.World
                && s_FamilyWeaponFirepowerCache.Ship == shipEntity
                && s_FamilyWeaponFirepowerCache.Bank == bankIndex
                && s_FamilyWeaponFirepowerCache.AbilityLevel == abilityLevel
                && s_FamilyWeaponFirepowerCache.Fingerprint == fingerprint)
            {
                if (!s_FamilyWeaponFirepowerCache.Ok)
                    return false;

                firepower = s_FamilyWeaponFirepowerCache.Firepower;
                baseFirePower = s_FamilyWeaponFirepowerCache.Base;
                perExtraLevel = s_FamilyWeaponFirepowerCache.PerExtra;
                fireRate = s_FamilyWeaponFirepowerCache.FireRate;
                bulletSpeed = s_FamilyWeaponFirepowerCache.BulletSpeed;
                bulletRange = s_FamilyWeaponFirepowerCache.BulletRange;
                return true;
            }

            bool ok = BulletBankOwnership.TryGetFamilyWeaponCatalogStats(
                em, shipEntity, bankIndex, out ShipComponentAbilityStats raw);
            if (ok)
            {
                baseFirePower = raw.firePower;
                perExtraLevel = raw.firePowerPerExtraLevel;
                // Level 7 with every ability at 7. Fire power is base + perExtra × 14.
                // Bullet speed uses ability level only. Fire rate and range use ship level.
                var maxed = ShipAbilityLevelCounts.Maxed(TitanFamilyWeaponAbilityLevel);
                ShipComponentAbilityStats leveled = ShipComponentExtraLevelMath.EvaluatePool(
                    raw,
                    1,
                    TitanFamilyWeaponShipLevel,
                    in maxed,
                    isWeaponPool: true,
                    includeBase: true);
                firepower = math.max(0.1f, leveled.firePower);
                fireRate = leveled.fireRate;
                bulletSpeed = leveled.bulletSpeed;
                bulletRange = leveled.bulletRange;
            }

            s_FamilyWeaponFirepowerCache = new FamilyWeaponFirepowerCache
            {
                World = em.World,
                Ship = shipEntity,
                Bank = bankIndex,
                AbilityLevel = abilityLevel,
                Fingerprint = fingerprint,
                Ok = ok,
                Firepower = firepower,
                Base = baseFirePower,
                PerExtra = perExtraLevel,
                FireRate = fireRate,
                BulletSpeed = bulletSpeed,
                BulletRange = bulletRange,
            };
            return ok;
        }

        /// <summary>
        /// Applies frozen MEGA hull stats plus PerExtra-only moon-store gear, multiplies
        /// by the planet family's fleet bonuses, then resizes the ghosted aim-slot buffer
        /// to match weapon mounts. <paramref name="familyIndex"/> is the store planet's
        /// <c>ShipFamilyConfigIndex</c> copied onto the ship at purchase.
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

            // --- Frozen hull + PerExtra-only LOADOUT gear ---
            // [TITAN-ORBIT] Catalog unique-parts keep Base. Moon-store ShipComponent rows
            // add PerExtra × shipLevel only (no second Base) so a purchased cockpit raises
            // Titan health by Extra Level steps, not by copying another catalog Base.
            int shipLevel = 7;
            if (em.HasComponent<ShipState>(shipEntity))
                shipLevel = math.max(1, em.GetComponentData<ShipState>(shipEntity).ShipLevel);
            ShipComponentStoreVisualScaleLogic.CollectExtraComponentIds(
                em, shipEntity, out List<string> extraIds);
            MegaShipStatsCalculator.SumFromEntry(
                entry, catalog, extraIds, shipLevel, out ShipComponentAbilityStats effective);
            // --- Planet family fleet bonuses ---
            // [TITAN-ORBIT] The Titan was added on a planet that owns a ship family.
            // Home worlds stay 1×. Every other family uses this match's roll (0.5–2.0)
            // on health, energy, speed, guns, ram, and troop cap. Gem cap stays 0.
            ShipFamilySpecialBonuses fleet = MegaShipStatsCalculator.ResolveFleetBonuses(familyIndex);
            effective = fleet.Apply(effective);
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
                // Widen the catalog energy band by the family roll so a 2× energy
                // family is not clipped back to MaxHullEnergy, and a 0.5× family
                // can sit under MinHullEnergy. A 1× home world keeps the old band.
                float energyMul = FleetMul(fleet.energyCapMul);
                ship.MaxEnergy = Mathf.Clamp(
                    effective.energyCap > 0.01f ? effective.energyCap : MegaShipCatalog.DefaultHullEnergy,
                    MegaShipCatalog.MinHullEnergy * math.min(1f, energyMul),
                    MegaShipCatalog.MaxHullEnergy * math.max(1f, energyMul));
                // SumFromEntry already resolved catalog defaults/mins. Do not clamp
                // to MinHullPeople (400) — that hid cockpit+wing sums of 40–300.
                ship.PeopleCapacity = Mathf.Max(0, Mathf.RoundToInt(effective.maxPeople));
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
                // Hull display range is the longest barrel. Do not clamp to
                // MaxBulletTravelDistance — that cap is only extra lead on short guns.
                weapon.BulletMaxDistance = Mathf.Max(
                    1f,
                    effective.bulletRange > 0.01f
                        ? effective.bulletRange
                        : MegaShipCatalog.DefaultBulletAcquireRange);
                weapon.BulletLifetime = Mathf.Max(0.25f, weapon.BulletMaxDistance / Mathf.Max(1f, bulletSpeed));
                weapon.ReferenceBulletDamage = firePower;
                weapon.ReferenceBulletSpeed = bulletSpeed;
                // [TITAN-ORBIT] MEGA Phase B volleys when the pool covers the whole bank.
                weapon.FireMode = ShipWeaponFireMode.EnergyHybrid;
                em.SetComponentData(shipEntity, weapon);
            }

            // --- Loadout cycle bank (Titan Bullet mounts only) ---
            // [TITAN-ORBIT] Spawn on the original catalog Bullets type. B-key / HUD
            // then reach the ship-family weapon, then purchases. Only
            // WeaponKind.Gun barrels adopt RuntimeBulletIndex. Cannons, missiles,
            // and snipers keep the catalog banks written on each mount. Reset the
            // index on chassis / slot change; keep B-key across extra-part applies.
            if (writeGhostedShipState && em.HasComponent<ShipLoadoutState>(shipEntity))
            {
                var loadout = em.GetComponentData<ShipLoadoutState>(shipEntity);
                int defaultBank = BulletBankOwnership.ResolveDefaultSelectedBank(em, shipEntity);

                bool adoptMegaGunDefault = true;
                if (em.HasComponent<ShipChassisState>(shipEntity))
                {
                    var prevForBank = em.GetComponentData<ShipChassisState>(shipEntity);
                    var chassisKeyForBank = new FixedString64Bytes(chassisId);
                    adoptMegaGunDefault = !prevForBank.ChassisId.Equals(chassisKeyForBank)
                        || prevForBank.AppliedBranchIndex != mega.MegaSlotIndex;
                }

                if (adoptMegaGunDefault)
                {
                    loadout.RuntimeBulletIndex = defaultBank;
                }
                else
                {
                    int[] owned = new int[16];
                    int ownedCount = BulletBankOwnership.CollectOwnedDamageBanks(em, shipEntity, owned);
                    bool stillOwned = false;
                    for (int i = 0; i < ownedCount; i++)
                    {
                        if (owned[i] == loadout.RuntimeBulletIndex)
                        {
                            stillOwned = true;
                            break;
                        }
                    }

                    if (!stillOwned)
                        loadout.RuntimeBulletIndex = defaultBank;
                }

                loadout.BranchIndex = mega.MegaSlotIndex;
                loadout.ChassisIndex = mega.MegaSlotIndex;
                em.SetComponentData(shipEntity, loadout);
            }

            // --- Slow beast motor ---
            if (em.HasComponent<ShipMotorConfig>(shipEntity))
            {
                float moveVal = Mathf.Max(0.1f, effective.moveSpeed);
                // turnSpeed is already degrees per second (same unit as regular hulls).
                float turnVal = Mathf.Max(0f, effective.turnSpeed);
                float accel = Mathf.Max(0.1f, effective.accelerationCap > 0f
                    ? effective.accelerationCap
                    : moveVal * 0.25f);

                var motor = em.GetComponentData<ShipMotorConfig>(shipEntity);
                motor.MaxSpeed = moveVal;
                // Titans have no Move Speed ability. Stock cruise is the live cruise.
                motor.BaseMaxSpeed = moveVal;
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
                // SumFromEntry already resolved catalog defaults/mins. Do not clamp to the
                // old 22–50 band — that made every Titan read 22/s.
                EnergyRegenPerSecond = Mathf.Max(0f, effective.energyRegen),
                HealthRegenDelayAfterDamage = ShipVitalsSettingsCache.HealthRegenDelayAfterDamage,
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
                // Must match ShipStatApplySystem's local-owner poll. Writing 0 here made
                // every client tick dirty (empty loadout hash is never 0) and re-apply
                // parked MegaShipGunnerSlotElement — tracers flew hull-forward while
                // the server kept auto-aiming planetary defense turrets.
                AppliedEquipmentFingerprint = ShipStatApplyLogic.ComputeEquippedLoadoutFingerprint(
                    em, shipEntity),
                AppliedMatchBonusSeed = ShipFamilyMatchBonuses.ActiveMatchSeed,
            };
            if (em.HasComponent<ShipChassisState>(shipEntity))
                em.SetComponentData(shipEntity, chassisState);
            else
                em.AddComponentData(shipEntity, chassisState);

            ResizeGunnerSlots(em, shipEntity);
            ApplyCatalogWeaponMountStats(em, shipEntity, catalog, entry);
        }

        /// <summary>
        /// Overwrites each MEGA mount's firePower / fireRate / bulletRange / bulletSpeed /
        /// bank / tracer scale from the unique component named like that prefab child.
        /// Family combat apply runs first and would otherwise stamp regular-ship numbers
        /// onto MEGA barrels. After that catalog write, the planet family's fleet rolls
        /// scale fire power, fire rate, bullet speed, and range on each barrel.
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
                mount.BulletRange = MegaShipCatalog.DefaultBulletAcquireRange;
                mounts[m] = mount;
            }

            // --- Family fleet bonuses on the barrels that actually fire ---
            // [TITAN-ORBIT] Hull ShipWeaponConfig is the HUD average. Live shots read
            // each mount's FirePower / FireRate / BulletSpeed / BulletRange. The
            // chassis catalog re-stamps these from the unique-part library, so the
            // scale has to live here or the next visual apply would wipe it.
            // Reference fire power scales with the live number so tracer size stays
            // the catalog ratio — the family roll changes damage, not bullet art size.
            int familyIndex = PlanetShipFamilyAssignment.HomeFamilyConfigIndex;
            if (em.HasComponent<ShipState>(shipEntity))
                familyIndex = em.GetComponentData<ShipState>(shipEntity).ShipFamilyConfigIndex;
            ApplyFleetBonusesToMounts(mounts, MegaShipStatsCalculator.ResolveFleetBonuses(familyIndex));
        }

        /// <summary>
        /// Multiplies each Titan barrel by the planet family's combat rolls.
        /// Identity (home world, or a 1× roll) leaves the catalog numbers alone.
        /// Called at the end of catalog weapon apply so a later chassis refresh
        /// still includes the bonus.
        /// </summary>
        static void ApplyFleetBonusesToMounts(
            DynamicBuffer<ShipWeaponMountElement> mounts,
            in ShipFamilySpecialBonuses bonuses)
        {
            float firePower = FleetMul(bonuses.firePowerMul);
            float fireRate = FleetMul(bonuses.fireRateMul);
            float bulletSpeed = FleetMul(bonuses.bulletSpeedMul);
            float bulletRange = FleetMul(bonuses.bulletRangeMul);
            bool identity = math.abs(firePower - 1f) < 0.0001f
                && math.abs(fireRate - 1f) < 0.0001f
                && math.abs(bulletSpeed - 1f) < 0.0001f
                && math.abs(bulletRange - 1f) < 0.0001f;
            if (identity)
                return;

            for (int i = 0; i < mounts.Length; i++)
            {
                ShipWeaponMountElement mount = mounts[i];
                mount.FirePower *= firePower;
                mount.ReferenceFirePower *= firePower;
                mount.FireRate = math.max(0.15f, mount.FireRate * fireRate);
                mount.ReferenceFireRate = mount.FireRate;
                mount.BulletSpeed = math.max(0.1f, mount.BulletSpeed * bulletSpeed);
                mount.BulletRange = math.max(0.5f, mount.BulletRange * bulletRange);
                mounts[i] = mount;
            }
        }

        /// <summary>
        /// Same zero-as-one rule as <see cref="ShipFamilySpecialBonuses.Apply"/>.
        /// An unset authored multiplier must not zero a Titan barrel.
        /// </summary>
        static float FleetMul(float value) => value > 0.0001f ? value : 1f;

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

                WriteMountCombat(
                    catalog, em, shipEntity, mounts, w, row, raw, partType,
                    MegaShipPartClassifier.GetPrefabAssetName(t));
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
            string partType,
            string instanceName = null)
        {
            // Unique-row firePower stays raw (0 = authored unarmed). Type-table fallback
            // uses the resolved type numbers so dedicated barrels are never mute.
            MegaShipPartStats resolved = catalog.ResolveRuntimeStats(raw);
            var mount = mounts[mountIndex];
            mount.FirePower = math.max(0f, raw.firePower);
            mount.FireRate = math.max(0.15f, resolved.fireRate > 0.01f ? resolved.fireRate : raw.fireRate);
            mount.BulletRange = math.max(
                4f, resolved.bulletRange > 0.5f ? resolved.bulletRange : raw.bulletRange);
            float partSpeed = resolved.bulletSpeed > 0.01f ? resolved.bulletSpeed : raw.bulletSpeed;
            // Weapon Missile Stats (type table / unique row) stay as authored.
            // ResolveRuntimeStats would raise 6 → runtimeMinimumStats.bulletSpeed (8).
            if (string.Equals(partType, ShipFamilyPartTypes.WeaponMissile, System.StringComparison.OrdinalIgnoreCase))
            {
                float missileSpeed = raw.bulletSpeed > 0.01f
                    ? raw.bulletSpeed
                    : catalog.weaponMissileStats.bulletSpeed;
                if (missileSpeed > 0.01f)
                    partSpeed = missileSpeed;
            }
            mount.BulletSpeed = math.max(0.1f, partSpeed);
            mount.ReferenceFirePower = math.max(0f, raw.firePower);
            mount.ReferenceFireRate = mount.FireRate;
            mount.FirePowerPerExtraLevel = 0f;
            mount.WeaponRotationSpeed = 0f;
            mount.BulletBankIndex = row != null
                ? catalog.ResolveWeaponBankIndex(row)
                : catalog.GetTypeTableBankIndex(partType);
            mount.BulletScale = row != null
                ? catalog.ResolveWeaponBankScale(row)
                : catalog.GetTypeTableBankScale(partType);
            mount.WeaponKind = ShipWeaponKind.Resolve(
                row,
                partType,
                row != null ? row.displayName : null,
                instanceName);
            mounts[mountIndex] = mount;
            if (em.HasBuffer<MegaShipGunnerSlotElement>(shipEntity))
            {
                var gunners = em.GetBuffer<MegaShipGunnerSlotElement>(shipEntity);
                if (mountIndex < 0 || mountIndex >= gunners.Length)
                    return;

                // Combat apply only refreshes barrel stats. Do not park a live auto-aim
                // lock — the owner-predicted client has no MegaShipAutoFireSystem to
                // write it back, so a wipe left tracers on hull forward.
                // WriteGhostedYaw also skips WeaponKind 0 (Gun) so a prediction rollback
                // cannot wipe a live cannon. This apply is the chassis stamp, so Gun
                // must still overwrite the previous Titan's cannon / missile / sniper.
                if (!MegaShipWeaponAim.IsTrackingAim(gunners[mountIndex]))
                    MegaShipWeaponAim.WriteGhostedYaw(gunners, mountIndex, mount);

                var slot = gunners[mountIndex];
                if (slot.WeaponKind == mount.WeaponKind
                    && (mount.WeaponKind == ShipWeaponKind.Cannon || slot.CannonLaserRampSeconds == 0f))
                    return;

                if (mount.WeaponKind != ShipWeaponKind.Cannon)
                    slot.CannonLaserRampSeconds = 0f;
                slot.WeaponKind = mount.WeaponKind;
                gunners[mountIndex] = slot;
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
            ClearLeftoverMegaMotor(em, shipEntity);
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
            mega.MegaSlotIndex = 0;
            em.SetComponentData(shipEntity, mega);
            ClearLeftoverMegaMotor(em, shipEntity);

            var ship = em.GetComponentData<ShipState>(shipEntity);
            ship.ShipLevel = prevLevel;
            ship.BranchIndex = prevBranch;
            ship.ShipFamilyConfigIndex = prevFamily;
            em.SetComponentData(shipEntity, ship);

            ShipStatApplyLogic.ApplyToShip(em, shipEntity, ship.Team, prevLevel, prevBranch);
        }

        /// <summary>
        /// Drops Titan ram / collision-mass leftovers so a failed family re-apply cannot
        /// keep MEGA <see cref="ShipMotorConfig.RammingPower"/> on the restored hull.
        /// </summary>
        static void ClearLeftoverMegaMotor(EntityManager em, Entity shipEntity)
        {
            if (!em.HasComponent<ShipMotorConfig>(shipEntity))
                return;

            var motor = em.GetComponentData<ShipMotorConfig>(shipEntity);
            motor.RammingPower = 0f;
            motor.Mass = ShipMassLogic.DefaultBaseMass;
            motor.SkipMassTax = 0;
            em.SetComponentData(shipEntity, motor);
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
            var mounts = em.HasBuffer<ShipWeaponMountElement>(shipEntity)
                ? em.GetBuffer<ShipWeaponMountElement>(shipEntity)
                : default;

            // Same barrel count still needs a kind refresh. A laser Titan swapped for a
            // bullet Titan keeps the old slot count, and Gun is 0, so the ghosted slot
            // would otherwise stay Cannon. RestoreMountKindsFromGhostedSlots then paints
            // the previous lasers back onto the new hull.
            if (gunners.Length == mountCount)
            {
                if (!mounts.IsCreated)
                    return;

                for (int i = 0; i < mountCount; i++)
                {
                    byte kind = mounts[i].WeaponKind;
                    var slot = gunners[i];
                    if (slot.WeaponKind == kind)
                        continue;
                    if (kind != ShipWeaponKind.Cannon)
                        slot.CannonLaserRampSeconds = 0f;
                    slot.WeaponKind = kind;
                    gunners[i] = slot;
                }

                return;
            }
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
                    CannonLaserRampSeconds = 0f,
                    WeaponKind = mounts.IsCreated && i < mounts.Length
                        ? mounts[i].WeaponKind
                        : (byte)0,
                });
            }
        }
    }
}
