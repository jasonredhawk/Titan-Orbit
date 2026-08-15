using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Static store item definitions: gem prices, display names, pack sizes, UI glyphs, and
    /// drone HP for moon-dock / home-planet store rows. Consumed by
    /// <see cref="Systems.HomePlanetStoreSystem"/> and orbit station equipment UI. Prices are
    /// code constants today — not ScriptableObject tunables.
    /// <para>
    /// [TITAN-ORBIT] Drones (fighter, mining, shield) and other leveled store goods are sold at
    /// <see cref="GetStorePurchaseLevel"/> — <c>min(ship, docked planet)</c>. A level-6 ship on a
    /// level-3 moon can only buy level-3 gear. Cost, visual size, and (for combat drones) damage
    /// scale with that purchase level — they do <b>not</b> copy the ship's live <c>BulletDamage</c>.
    /// Rough ship firepower thumb-rule is ~3 + 1 per level; combat drones deal one-sixth:
    /// <c>0.5 + (1/6)×level</c> (level 1 → ≈0.67, level 6 → 1.5 DPS at 1 shot/sec).
    /// Visual size uses the same relative curve with level 6 = prefab scale 1.0.
    /// </para>
    /// </summary>
    public static class StoreItemData
    {
        // --- Drone leveling (fighter + mining + shield) ---

        /// <summary>
        /// Design reference for “full size / full HP” drones. Matches the combat damage
        /// target (level 6 → 1.5 dmg). Levels above this clamp visual/HP scale at 1.0.
        /// </summary>
        public const int DroneReferenceMaxLevel = 6;

        /// <summary>
        /// Constant term in the combat-drone damage curve. [TITAN-ORBIT] One-sixth of the
        /// rough base ship firepower thumb-rule (3 ÷ 6 = 0.5).
        /// </summary>
        public const float CombatDroneBaseDamage = 0.5f;

        /// <summary>
        /// Damage added per purchase level. [TITAN-ORBIT] One-sixth of the rough ship
        /// +1 firepower-per-level thumb-rule. Level 6 → 0.5 + 1 = 1.5.
        /// </summary>
        public const float CombatDroneDamagePerLevel = 1f / 6f;

        /// <summary>
        /// Fighter / mining drone HP at <see cref="DroneReferenceMaxLevel"/>. Shield drones use
        /// <see cref="ShieldDroneHpMultiplier"/> × this value. Lower purchase levels scale down
        /// with the same relative curve as visual size.
        /// </summary>
        public const int DroneMaxHpAtReferenceLevel = 30;

        /// <summary>
        /// [TITAN-ORBIT] Shields tank more — 3× fighter/mining HP at the same purchase level
        /// (level 6 shield → 90 HP when combat drones are at 30).
        /// </summary>
        public const int ShieldDroneHpMultiplier = 3;

        /// <summary>
        /// True when fighter or mining — these fire leveled damage bolts.
        /// </summary>
        public static bool IsLeveledCombatDrone(StoreItemType item)
        {
            return item == StoreItemType.FighterDrone
                || item == StoreItemType.MiningDrone;
        }

        /// <summary>
        /// True when any autonomous drone sold at store purchase level (fighter, mining, shield).
        /// Cost, size, and ItemLevel apply to all of these.
        /// </summary>
        public static bool IsLeveledDrone(StoreItemType item)
        {
            return IsDrone(item);
        }

        /// <summary>
        /// Level the moon Orbit Menu may sell drones, components, and cards at.
        /// [TITAN-ORBIT] The docked planet is a hard cap: you cannot buy gear above that world's
        /// level even if the ship is higher. Same formula as card-spin tier.
        /// </summary>
        /// <param name="shipLevel">Current ship chassis tier (1-based).</param>
        /// <param name="planetLevel">Level of the planet whose moon the ship is docked at.</param>
        /// <returns>At least 1; never above the weaker of the two inputs.</returns>
        public static int GetStorePurchaseLevel(int shipLevel, int planetLevel)
        {
            // --- Limiting level ---
            // [TITAN-ORBIT] Example: ship 6 + planet 3 → buy level 3. Ship 2 + planet 6 → buy level 2.
            return Mathf.Min(Mathf.Max(1, shipLevel), Mathf.Max(1, planetLevel));
        }

        /// <summary>
        /// Per-shot damage for a fighter or mining drone bought at <paramref name="itemLevel"/>.
        /// Same curve for both types — fire rate / target filter differ in combat systems.
        /// </summary>
        /// <param name="itemLevel">Ship level at purchase time (clamped to ≥ 1).</param>
        /// <returns>
        /// <c>0.5 + (1/6)×level</c>. Level 1 ≈ 0.67; level 6 = 1.5 (at 1 shot/sec that is DPS).
        /// </returns>
        public static float GetCombatDroneDamage(int itemLevel)
        {
            // --- Level curve ---
            // [TITAN-ORBIT] Intentionally uses ×level (not ×(level−1)) so level 6 = 1.5 as designed.
            // Cost still anchors on GetCombatDroneDamage(1) so level-1 catalog prices stay original.
            int level = Mathf.Max(1, itemLevel);
            return CombatDroneBaseDamage + CombatDroneDamagePerLevel * level;
        }

        /// <summary>
        /// Shared level power used for cost / size / shield HP. 1.0 at level 1; grows with the
        /// combat damage curve so all drone kinds stay on one ladder.
        /// </summary>
        public static float GetDroneLevelPowerMul(int itemLevel)
        {
            float power = GetCombatDroneDamage(itemLevel);
            float powerL1 = GetCombatDroneDamage(1);
            return power / Mathf.Max(0.01f, powerL1);
        }

        /// <summary>
        /// Level size multiplier applied on top of the drone prefab's authored localScale.
        /// 1.0 at <see cref="DroneReferenceMaxLevel"/> (same visual size as before leveling);
        /// smaller at lower levels (~0.44 at level 1). Levels above reference clamp at 1.0.
        /// </summary>
        public static float GetDroneVisualScale(int itemLevel)
        {
            float power = GetCombatDroneDamage(itemLevel);
            float powerMax = GetCombatDroneDamage(DroneReferenceMaxLevel);
            return Mathf.Clamp(power / Mathf.Max(0.01f, powerMax), 0.25f, 1f);
        }

        /// <summary>
        /// Gem price for a store item. Leveled drones scale cost with
        /// <see cref="GetDroneLevelPowerMul"/> so level 1 keeps the original catalog price.
        /// </summary>
        /// <param name="item">Catalog item kind.</param>
        /// <param name="shipLevel">
        /// Store purchase level from <see cref="GetStorePurchaseLevel"/> — used for
        /// fighter/mining/shield drones. Ignored for rockets and mines.
        /// </param>
        public static float GetPrice(StoreItemType item, int shipLevel = 1)
        {
            // --- Base catalog price (level-1 / non-leveled) ---
            float basePrice = GetBasePrice(item);
            if (!IsLeveledDrone(item))
                return basePrice;

            // --- Scale cost with level power: cost(L) = base × power(L) / power(1) ---
            // [TITAN-ORBIT] Level 1 stays at the original 70g / 80g / 100g; higher levels pay more.
            return basePrice * GetDroneLevelPowerMul(shipLevel);
        }

        /// <summary>Original flat gem prices before combat-drone level scaling.</summary>
        public static float GetBasePrice(StoreItemType item)
        {
            // --- Compute value ---
            switch (item)
            {
                case StoreItemType.FighterDrone: return 80f;
                case StoreItemType.ShieldDrone: return 100f;
                case StoreItemType.MiningDrone: return 70f;
                case StoreItemType.SmallRockets: return 50f;
                case StoreItemType.LargeRockets: return 90f;
                case StoreItemType.SmallMines: return 45f;
                case StoreItemType.LargeMines: return 85f;
                default: return 999f;
            }
        }

        /// <summary>Long display name for store cards and purchase confirmation.</summary>
        public static string GetDisplayName(StoreItemType item)
        {
            // --- Compute value ---
            switch (item)
            {
                case StoreItemType.FighterDrone: return "Fighter Drone";
                case StoreItemType.ShieldDrone: return "Shield Drone";
                case StoreItemType.MiningDrone: return "Mining Drone";
                case StoreItemType.SmallRockets: return "Small Rockets (x4)";
                case StoreItemType.LargeRockets: return "Large Rockets (x2)";
                case StoreItemType.SmallMines: return "Small Mines (x4)";
                case StoreItemType.LargeMines: return "Large Mines (x2)";
                default: return item.ToString();
            }
        }

        /// <summary>
        /// Display name including purchase level for leveled drones (e.g. "Mining Drone Lv.6").
        /// Non-leveled items ignore <paramref name="itemLevel"/>.
        /// </summary>
        public static string GetDisplayName(StoreItemType item, int itemLevel)
        {
            string name = GetDisplayName(item);
            if (!IsLeveledDrone(item))
                return name;
            return $"{name} Lv.{Mathf.Max(1, itemLevel)}";
        }

        /// <summary>Pack size for rockets/mines; drones are 1 per purchase.</summary>
        public static int GetPackSize(StoreItemType item)
        {
            // --- Compute value ---
            switch (item)
            {
                case StoreItemType.SmallRockets:
                case StoreItemType.SmallMines: return 4;
                case StoreItemType.LargeRockets:
                case StoreItemType.LargeMines: return 2;
                default: return 1;
            }
        }

        /// <summary>Short title for compact moon-dock store rows.</summary>
        public static string GetShortDisplayName(StoreItemType item)
        {
            // --- Compute value ---
            switch (item)
            {
                case StoreItemType.FighterDrone: return "Fighter";
                case StoreItemType.ShieldDrone: return "Shield";
                case StoreItemType.MiningDrone: return "Mining";
                case StoreItemType.SmallRockets: return "Rockets S";
                case StoreItemType.LargeRockets: return "Rockets L";
                case StoreItemType.SmallMines: return "Mines S";
                case StoreItemType.LargeMines: return "Mines L";
                default: return item.ToString();
            }
        }

        /// <summary>
        /// Stat index (0–9) into <see cref="TitanOrbit.UI.ShipAbilityCategoryColors"/> for card tinting —
        /// same palette as the bottom ship upgrade bar.
        /// </summary>
        public static int GetAbilityColorStatIndex(StoreItemType item)
        {
            // --- Compute value ---
            switch (item)
            {
                case StoreItemType.FighterDrone: return 0; // Fire Power
                case StoreItemType.SmallRockets: return 1; // Bullet Speed
                case StoreItemType.LargeRockets: return 0;
                case StoreItemType.ShieldDrone: return 2; // Health Cap
                case StoreItemType.SmallMines: return 3; // Health Regen
                case StoreItemType.LargeMines: return 3;
                case StoreItemType.MiningDrone: return 8; // Gem Cap
                default: return 0;
            }
        }

        /// <summary>True for autonomous drone items (fighter, shield, mining).</summary>
        public static bool IsDrone(StoreItemType item)
        {
            // --- IsDrone ---
            return item == StoreItemType.FighterDrone
                || item == StoreItemType.ShieldDrone
                || item == StoreItemType.MiningDrone;
        }

        /// <summary>
        /// Max HP for a drone at reference max level (legacy callers / unequipped display).
        /// Prefer <see cref="GetDroneMaxHp(StoreItemType, int)"/> with purchase level.
        /// </summary>
        public static int GetDroneMaxHp(StoreItemType item)
        {
            return GetDroneMaxHp(item, DroneReferenceMaxLevel);
        }

        /// <summary>
        /// Max HP stored in equipment RemainingCharges. Scales with purchase level so a
        /// level-1 drone is weaker than a level-6 drone. Fighter/mining use
        /// <see cref="DroneMaxHpAtReferenceLevel"/> at max level; shields use
        /// <see cref="ShieldDroneHpMultiplier"/> × that (3× tougher at the same level).
        /// </summary>
        public static int GetDroneMaxHp(StoreItemType item, int itemLevel)
        {
            if (!IsDrone(item)) return 1;

            // --- Shared level scale (same curve as visual size) ---
            float scale = GetDroneVisualScale(itemLevel);
            int combatHp = Mathf.Max(1, Mathf.RoundToInt(DroneMaxHpAtReferenceLevel * scale));

            // --- Shields tank more ---
            // [TITAN-ORBIT] Block wall role: 3× fighter/mining HP at every purchase level.
            if (item == StoreItemType.ShieldDrone)
                return Mathf.Max(1, combatHp * ShieldDroneHpMultiplier);

            return combatHp;
        }

        /// <summary>True when item is an authored ship-family component row.</summary>
        public static bool IsShipComponent(StoreItemType item) => item == StoreItemType.ShipComponent;

        /// <summary>True for drones, rockets, and mines (non-chassis gear).</summary>
        public static bool IsSupportItem(StoreItemType item) => !IsShipComponent(item);

        /// <summary>Short description for equipment slot UI.</summary>
        public static string GetDescription(StoreItemType item)
        {
            return GetDescription(item, itemLevel: 1);
        }

        /// <summary>
        /// Short description for equipment / store UI. Leveled drones include level so
        /// players see they are buying store-capped gear (<c>min(ship, planet)</c>).
        /// </summary>
        public static string GetDescription(StoreItemType item, int itemLevel)
        {
            // --- Compute value ---
            int level = Mathf.Max(1, itemLevel);
            switch (item)
            {
                case StoreItemType.FighterDrone:
                {
                    // [TITAN-ORBIT] Asteroid-immune: fighter bolts only hurt ships (Starblast-style).
                    float dmg = GetCombatDroneDamage(level);
                    return $"Lv.{level} · {dmg:0.##} dmg/shot vs ships.";
                }
                case StoreItemType.ShieldDrone:
                {
                    int hp = GetDroneMaxHp(item, level);
                    return $"Lv.{level} · {hp} HP · blocks incoming fire.";
                }
                case StoreItemType.MiningDrone:
                {
                    // [TITAN-ORBIT] Ship-immune: mining bolts only hurt asteroids (Starblast-style).
                    float dmg = GetCombatDroneDamage(level);
                    return $"Lv.{level} · {dmg:0.##} dmg/shot vs rocks.";
                }
                case StoreItemType.SmallRockets: return "Q to fire · pack of 4.";
                case StoreItemType.LargeRockets: return "Q to fire · pack of 2.";
                case StoreItemType.SmallMines: return "E to place · pack of 4.";
                case StoreItemType.LargeMines: return "E to place · pack of 2.";
                default: return string.Empty;
            }
        }

        /// <summary>Large glyph shown in the card icon area when no sprite is assigned.</summary>
        public static string GetIconGlyph(StoreItemType item)
        {
            // --- Compute value ---
            switch (item)
            {
                case StoreItemType.FighterDrone: return "\u2694"; // crossed swords
                case StoreItemType.ShieldDrone: return "\u25C8"; // diamond
                case StoreItemType.MiningDrone: return "\u2692"; // pick
                case StoreItemType.SmallRockets:
                case StoreItemType.LargeRockets: return "\u25B2"; // triangle
                case StoreItemType.SmallMines:
                case StoreItemType.LargeMines: return "\u25CF"; // circle
                default: return "?";
            }
        }
    }
}
