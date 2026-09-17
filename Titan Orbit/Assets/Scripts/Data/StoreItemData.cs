using TitanOrbit.Core;
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
    /// level-3 moon can only buy level-3 gear. Cost, visual size, HP, and (for combat drones)
    /// fire power scale with that purchase level — they do <b>not</b> copy the ship's live
    /// <c>BulletDamage</c>. Combat fire power and fighter/mining HP lerp Level 1 → Level 6
    /// (same style as planetary-defense turrets). Level 6 is 4× Level 1 on both ladders.
    /// Visual size tracks the fire-power curve with level 6 = prefab scale 1.0.
    /// </para>
    /// </summary>
    public static class StoreItemData
    {
        // --- Drone leveling (fighter + mining + shield) ---

        /// <summary>
        /// Design reference for “full size / full stats” drones. Level-1 → Level-6 fire
        /// power and HP lerp toward these tops. Levels above this keep growing one extra
        /// step (same idea as planetary-defense Level 7).
        /// </summary>
        public const int DroneReferenceMaxLevel = 6;

        /// <summary>
        /// Fighter / mining damage per shot at purchase Level 1 (before family-bank muls).
        /// [TITAN-ORBIT] Still about one-sixth of a Level-1 ship gun (~3 fire power).
        /// </summary>
        public const float CombatDroneDamageAtLevel1 = 0.6f;

        /// <summary>
        /// Fighter / mining damage per shot at purchase Level 6. 4× Level 1 so a late
        /// drone is clearly stronger than an early one (0.6 → 2.4).
        /// </summary>
        public const float CombatDroneDamageAtLevel6 = 2.4f;

        /// <summary>
        /// Legacy alias for the Level-1 fire-power end. Prefer
        /// <see cref="CombatDroneDamageAtLevel1"/>.
        /// </summary>
        public const float CombatDroneBaseDamage = CombatDroneDamageAtLevel1;

        /// <summary>
        /// Legacy per-level step if someone still adds <c>base + per × level</c>.
        /// Live combat uses the Level-1 → Level-6 lerp instead.
        /// </summary>
        public const float CombatDroneDamagePerLevel =
            (CombatDroneDamageAtLevel6 - CombatDroneDamageAtLevel1) / (DroneReferenceMaxLevel - 1);

        /// <summary>
        /// Fighter / mining max HP at purchase Level 1. Shield drones use
        /// <see cref="ShieldDroneHpMultiplier"/> × this ladder.
        /// </summary>
        public const int DroneHpAtLevel1 = 10;

        /// <summary>
        /// Fighter / mining max HP at purchase Level 6. 4× Level 1 (10 → 40).
        /// </summary>
        public const int DroneHpAtLevel6 = 40;

        /// <summary>
        /// Fighter / mining HP at <see cref="DroneReferenceMaxLevel"/>. Prefer
        /// <see cref="DroneHpAtLevel6"/>.
        /// </summary>
        public const int DroneMaxHpAtReferenceLevel = DroneHpAtLevel6;

        /// <summary>
        /// [TITAN-ORBIT] Shields tank more — 3× fighter/mining HP at the same purchase level
        /// (level 6 shield → 120 HP when combat drones are at 40).
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
        /// 0 at Level 1, 1 at Level 6. Levels above 6 step past 1 (Level 7 = 1.2).
        /// Shared by fire power, HP, and any other drone ladder that should stay in lockstep.
        /// </summary>
        /// <param name="itemLevel">Purchase level (clamped to ≥ 1).</param>
        /// <returns>Linear t for <c>LerpUnclamped(level1, level6, t)</c>.</returns>
        public static float GetDroneLevelT(int itemLevel)
        {
            // --- Ladder t ---
            // [TITAN-ORBIT] Same 1→6 lerp planetary defense uses. Denominator is 5
            // (six rungs minus one) so Level 1 = 0 and Level 6 = 1.
            int level = Mathf.Max(1, itemLevel);
            float denom = Mathf.Max(1, DroneReferenceMaxLevel - 1);
            return (level - 1) / denom;
        }

        /// <summary>
        /// Per-shot damage for a fighter or mining drone bought at <paramref name="itemLevel"/>.
        /// Same curve for both types — fire rate / target filter differ in combat systems.
        /// </summary>
        /// <param name="itemLevel">Ship level at purchase time (clamped to ≥ 1).</param>
        /// <returns>
        /// Level 1 = <see cref="CombatDroneDamageAtLevel1"/> (0.6);
        /// Level 6 = <see cref="CombatDroneDamageAtLevel6"/> (2.4). 4× from first to last rung.
        /// </returns>
        public static float GetCombatDroneDamage(int itemLevel)
        {
            // --- Level curve ---
            // [TITAN-ORBIT] Linear lerp, not the old 0.5 + (1/6)×level (that only grew 2.25×).
            // Cost still anchors on GetCombatDroneDamage(1) so level-1 catalog prices stay original.
            float t = GetDroneLevelT(itemLevel);
            return Mathf.Max(0.05f, Mathf.LerpUnclamped(
                CombatDroneDamageAtLevel1, CombatDroneDamageAtLevel6, t));
        }

        /// <summary>
        /// Unique-ability Extra Levels for a drone bought at <paramref name="itemLevel"/>.
        /// Level 1 = 0 (authored bank numbers); Level 6 = 5 extra rungs so burn / heal /
        /// damage multipliers grow with the same purchase rung as fire power and HP.
        /// Does <b>not</b> include the ship's live Fire Power attribute purchases.
        /// </summary>
        /// <param name="itemLevel">Stamped purchase level (clamped to ≥ 1).</param>
        public static int GetDroneFirePowerExtraLevels(int itemLevel)
        {
            // --- Purchase extras ---
            // [TITAN-ORBIT] Same (level − 1) ships use for chassis Extra Levels, but locked
            // to the drone's ItemLevel so a Level-3 drone stays Level-3 after the ship ranks up.
            return Mathf.Max(0, Mathf.Max(1, itemLevel) - 1);
        }

        /// <summary>
        /// Shared level power used for cost and visual size. 1.0 at level 1; grows with the
        /// combat damage curve so all drone kinds stay on one price / size ladder.
        /// HP uses <see cref="GetDroneMaxHp(StoreItemType, int)"/> (its own 4× lerp).
        /// </summary>
        public static float GetDroneLevelPowerMul(int itemLevel)
        {
            // --- Cost / size vs Level 1 ---
            // [TITAN-ORBIT] Level 1 keeps the original catalog gem price; Level 6 costs 4×.
            float power = GetCombatDroneDamage(itemLevel);
            float powerL1 = GetCombatDroneDamage(1);
            return power / Mathf.Max(0.01f, powerL1);
        }

        /// <summary>
        /// Level size multiplier applied on top of the drone prefab's authored localScale.
        /// 1.0 at <see cref="DroneReferenceMaxLevel"/> (same visual size as before leveling);
        /// smaller at lower levels (0.25 at level 1 — the clamp floor). Levels above
        /// reference clamp at 1.0.
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
        /// fighter/mining/shield drones, rockets, and mines.
        /// </param>
        public static float GetPrice(StoreItemType item, int shipLevel = 1)
        {
            // --- Base catalog price (level-1 / non-leveled) ---
            float basePrice = GetBasePrice(item);
            if (!IsLeveledStoreGood(item))
                return basePrice;

            // --- Scale cost with level power: cost(L) = base × power(L) / power(1) ---
            // [TITAN-ORBIT] Level 1 stays at the original catalog price; higher levels pay more.
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
                case StoreItemType.SmallRockets: return "Rockets (x2)";
                case StoreItemType.LargeRockets: return "Rockets (x2)";
                case StoreItemType.SmallMines: return "Mines (x4)";
                case StoreItemType.LargeMines: return "Mines (x4)";
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
            if (!IsLeveledStoreGood(item))
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
                case StoreItemType.LargeRockets: return 2;
                case StoreItemType.SmallMines:
                case StoreItemType.LargeMines: return 4;
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
                case StoreItemType.SmallRockets: return "Rockets";
                case StoreItemType.LargeRockets: return "Rockets";
                case StoreItemType.SmallMines: return "Mines";
                case StoreItemType.LargeMines: return "Mines";
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
        /// True for store rocket packs. Canonical SKU is <see cref="StoreItemType.SmallRockets"/>;
        /// <see cref="StoreItemType.LargeRockets"/> stays valid if an old slot is still equipped.
        /// </summary>
        public static bool IsRocket(StoreItemType item)
        {
            return item == StoreItemType.SmallRockets
                || item == StoreItemType.LargeRockets;
        }

        /// <summary>
        /// Rockets stamp <c>ItemLevel = min(ship, planet)</c> and scale price / fire power from it.
        /// </summary>
        public static bool IsLeveledRocket(StoreItemType item) => IsRocket(item);

        /// <summary>
        /// True for store mine packs. Canonical SKU is <see cref="StoreItemType.SmallMines"/>;
        /// <see cref="StoreItemType.LargeMines"/> stays valid if an old slot is still equipped.
        /// </summary>
        public static bool IsMine(StoreItemType item)
        {
            return item == StoreItemType.SmallMines
                || item == StoreItemType.LargeMines;
        }

        /// <summary>
        /// Mines stamp <c>ItemLevel = min(ship, planet)</c> and scale price / blast from it.
        /// </summary>
        public static bool IsLeveledMine(StoreItemType item) => IsMine(item);

        /// <summary>
        /// True for store cards that show a purchase level (drones, rockets, and mines).
        /// </summary>
        public static bool IsLeveledStoreGood(StoreItemType item) =>
            IsLeveledDrone(item) || IsLeveledRocket(item) || IsLeveledMine(item);

        /// <summary>
        /// Orbit Menu catalog filter. Hides the legacy Large Rockets / Large Mines SKUs
        /// and the ship-component enum.
        /// </summary>
        public static bool IsSoldInOrbitMenu(StoreItemType item)
        {
            return !IsShipComponent(item)
                && item != StoreItemType.LargeRockets
                && item != StoreItemType.LargeMines;
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
        /// level-1 drone is weaker than a level-6 drone. Fighter/mining lerp
        /// <see cref="DroneHpAtLevel1"/> → <see cref="DroneHpAtLevel6"/>; shields use
        /// <see cref="ShieldDroneHpMultiplier"/> × that (3× tougher at the same level).
        /// </summary>
        public static int GetDroneMaxHp(StoreItemType item, int itemLevel)
        {
            if (!IsDrone(item)) return 1;

            // --- Combat HP ladder (independent of mesh scale) ---
            // [TITAN-ORBIT] Same GetDroneLevelT as fire power so HP and damage climb together.
            // Visual size still uses GetDroneVisualScale; we do not reuse that ratio here
            // because it clamped at 1.0 and only grew ~2.3× (13 → 30).
            float t = GetDroneLevelT(itemLevel);
            int combatHp = Mathf.Max(1, Mathf.RoundToInt(Mathf.LerpUnclamped(
                DroneHpAtLevel1, DroneHpAtLevel6, t)));

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
                    int hp = GetDroneMaxHp(item, level);
                    return $"Lv.{level} · {dmg:0.##} FP · {hp} HP · vs ships.";
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
                    int hp = GetDroneMaxHp(item, level);
                    return $"Lv.{level} · {dmg:0.##} FP · {hp} HP · vs rocks.";
                }
                case StoreItemType.SmallRockets:
                case StoreItemType.LargeRockets:
                {
                    float cd = RocketCatalog.Get(level).fireCooldown;
                    return $"ALT to fire · 2 per slot · Lv.{level} · {cd:0.#}s reload.";
                }
                case StoreItemType.SmallMines:
                case StoreItemType.LargeMines:
                {
                    float cd = MineCatalog.Get(level).deployCooldown;
                    return $"E to place · 4 per slot · Lv.{level} · {cd:0.##}s drop.";
                }
                default: return string.Empty;
            }
        }

        /// <summary>
        /// Theatrical team thumb from <see cref="StoreItemPreviewCatalog"/>, or null for glyph fallback.
        /// Large rockets/mines share the small-pack preview.
        /// </summary>
        public static Sprite GetMenuPreviewSprite(
            StoreItemType item,
            TeamManager.Team team,
            ShipFamilyDefinition family = null)
        {
            StoreItemType key = item;
            if (item == StoreItemType.LargeRockets)
                key = StoreItemType.SmallRockets;
            if (item == StoreItemType.LargeMines)
                key = StoreItemType.SmallMines;
            StoreItemPreviewCatalog catalog = StoreItemPreviewCatalog.LoadDefault();
            return catalog != null ? catalog.GetSprite(key, team) : null;
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
