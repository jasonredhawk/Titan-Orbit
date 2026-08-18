using System.Collections.Generic;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Procedural <see cref="CardData"/> factory when a <see cref="ShipFamilyDefinition"/> has no
    /// <see cref="CardDeckDefinition"/> assigned. Card IDs are prefixed by family so different families
    /// do not collide in save data. Numbers come from <see cref="CardDeckBalance"/> — same formulas as
    /// editor-generated assets and <see cref="CardDataRuntimeRestore"/>.
    /// </summary>
    public static class CardDeckRuntimeDefaults
    {
        /// <summary>
        /// Builds unique per-family overlay cards (3 archetypes × 7 levels, plus Family Crest when bonuses ≠ 1).
        /// Runtime-only ScriptableObjects when no authored <see cref="CardDeckDefinition"/> is assigned.
        /// </summary>
        public static List<CardData> CreateUniqueDeck(string familyId, ShipFamilySpecialBonuses bonuses)
        {
            var list = new List<CardData>();
            var archetypes = ShipFamilyUniqueCardDeckTable.GetArchetypes(familyId, bonuses);
            for (int a = 0; a < archetypes.Count; a++)
            {
                var arch = archetypes[a];
                for (int level = 1; level <= 7; level++)
                {
                    float mag = ShipFamilyUniqueCardDeckTable.MagnitudeAtLevel(arch, level);
                    var c = ScriptableObject.CreateInstance<CardData>();
                    c.cardId = ShipFamilyUniqueCardDeckTable.FormatCardId(familyId, arch.idSuffix, level);
                    c.displayName = arch.displayName + " " + level;
                    c.description = arch.description;
                    c.cardLevel = level;
                    c.rarity = arch.rarity;
                    c.slotType = SlotType.Ship;
                    c.minHomePlanetLevel = 1;
                    c.gemCost = 0f;
                    c.damageMultiplier = 1f;
                    c.fireRateMultiplier = 1f;
                    c.bulletSpeedMultiplier = 1f;
                    c.gemDepositSpeedMultiplier = 1f;
                    c.peopleTransferSpeedMultiplier = 1f;
                    c.familyBonusOverlay = ShipFamilySpecialBonuses.Identity;
                    c.effects = new List<CardEffect>();
                    if (arch.usesFamilyBonusOverlay)
                        c.familyBonusOverlay = bonuses.ScaleNonIdentity(mag);
                    else if (arch.kind != CardEffectKind.None)
                        c.effects.Add(new CardEffect { kind = arch.kind, magnitude = mag });
                    list.Add(c);
                }
            }

            return list;
        }

        /// <summary>Legacy name — now builds the unique family deck (identity bonuses).</summary>
        public static List<CardData> CreateProceduralDeck(string familyIdForCardPrefix)
        {
            return CreateUniqueDeck(familyIdForCardPrefix, ShipFamilySpecialBonuses.Identity);
        }
    }

    /// <summary>
    /// Authored unique card archetypes per ship family. The editor generator expands each
    /// archetype across levels 1–7. When <see cref="ShipFamilySpecialBonuses"/> is not identity,
    /// a fourth "Family Crest" archetype is added from those ≠1 muls.
    /// Lives in this file so TitanOrbit.Data always compiles the table with the runtime factory.
    /// </summary>
    public static class ShipFamilyUniqueCardDeckTable
    {
        /// <summary>One signature card type that scales with spin tier.</summary>
        public struct Archetype
        {
            public string idSuffix;
            public string displayName;
            public string description;
            public CardEffectKind kind;
            public float magnitudeAtLevel1;
            public float magnitudePerLevel;
            public CardRarity rarity;
            public bool usesFamilyBonusOverlay;
        }

        /// <summary>Resolved deck recipe for one family (3 unique + optional family-crest).</summary>
        public static List<Archetype> GetArchetypes(string familyId, in ShipFamilySpecialBonuses bonuses)
        {
            var list = new List<Archetype>(4);
            string id = Sanitize(familyId);

            switch (id)
            {
                case "AstroEagle":
                    list.Add(Mul("RangeLattice", "Range Lattice", "Weapon shots travel farther.", CardEffectKind.BulletRangeMul, 1.08f, 0.02f, CardRarity.Uncommon));
                    list.Add(Mul("SalvoCadence", "Salvo Cadence", "Guns cycle faster.", CardEffectKind.FireRateMul, 1.06f, 0.02f, CardRarity.Rare));
                    list.Add(Mul("LockWindow", "Lock Window", "Rockets acquire targets sooner.", CardEffectKind.RocketDamageMul, 1.08f, 0.025f, CardRarity.Epic));
                    break;
                case "CosmicShark":
                    list.Add(Mul("ReefYield", "Reef Yield", "Asteroids drop more gems.", CardEffectKind.AsteroidGemYieldMul, 1.10f, 0.03f, CardRarity.Rare));
                    list.Add(Mul("JawMill", "Jaw Mill", "Mining chips rocks faster.", CardEffectKind.MiningRateMul, 1.12f, 0.03f, CardRarity.Uncommon));
                    list.Add(Mul("RammingJaw", "Ramming Jaw", "Hull rams hit harder.", CardEffectKind.RammingMul, 1.10f, 0.03f, CardRarity.Epic));
                    break;
                case "ForceBadger":
                    list.Add(Add("DockRegen", "Dock Regen", "Hull knits while gem-moon docked.", CardEffectKind.DockedHullRegenAdd, 0.4f, 0.15f, CardRarity.Uncommon));
                    list.Add(Mul("PlateResist", "Plate Resist", "Incoming shots hurt less.", CardEffectKind.IncomingDamageTakenMul, 0.94f, -0.01f, CardRarity.Rare));
                    list.Add(Mul("BraceRam", "Brace Ram", "Ramming deals more impact.", CardEffectKind.RammingMul, 1.08f, 0.02f, CardRarity.Epic));
                    break;
                case "GalaxyRaptor":
                    list.Add(Mul("RaptorWarhead", "Raptor Warhead", "Rockets hit harder.", CardEffectKind.RocketDamageMul, 1.12f, 0.03f, CardRarity.Rare));
                    list.Add(Mul("WingDrone", "Wing Drone", "Fighter drones punch harder.", CardEffectKind.DroneDamageMul, 1.10f, 0.03f, CardRarity.Uncommon));
                    list.Add(Add("LockRack", "Lock Rack", "Rocket packs carry extra shots.", CardEffectKind.RocketPackSizeAdd, 1f, 0.25f, CardRarity.Epic));
                    break;
                case "HyperFalcon":
                    list.Add(Mul("ThinBurn", "Thin Burn", "OVERDRIVE spends less energy.", CardEffectKind.OverdriveDrainMul, 0.90f, -0.02f, CardRarity.Rare));
                    list.Add(Mul("SlashBoost", "Slash Boost", "OVERDRIVE speed bonus grows.", CardEffectKind.OverdriveSpeedMul, 1.08f, 0.025f, CardRarity.Epic));
                    list.Add(Mul("BankTurn", "Bank Turn", "Turn while boosting stays sharp.", CardEffectKind.OverdriveSpeedMul, 1.05f, 0.015f, CardRarity.Uncommon));
                    break;
                case "LightFox":
                    list.Add(Mul("VaultRun", "Vault Run", "Gem deposits clear faster.", CardEffectKind.GemDepositSpeedMul, 1.10f, 0.03f, CardRarity.Rare));
                    list.Add(Add("ScoopHalo", "Scoop Halo", "Gems magnetize from farther.", CardEffectKind.GemPickupRadiusAdd, 1.5f, 0.4f, CardRarity.Uncommon));
                    list.Add(Mul("FoxBeam", "Fox Beam", "Tractor reach grows.", CardEffectKind.TractorRangeMul, 1.10f, 0.03f, CardRarity.Epic));
                    break;
                case "MeteorMantis":
                    list.Add(Mul("SwarmHull", "Swarm Hull", "Drones survive more hits.", CardEffectKind.DroneHitPointsMul, 1.12f, 0.03f, CardRarity.Uncommon));
                    list.Add(Mul("BurstMine", "Burst Mine", "Mine blasts cover more area.", CardEffectKind.MineBlastRadiusMul, 1.12f, 0.03f, CardRarity.Rare));
                    list.Add(Add("ClusterRack", "Cluster Rack", "Mine packs include extras.", CardEffectKind.MinePackSizeAdd, 1f, 0.3f, CardRarity.Epic));
                    break;
                case "NightAye":
                    list.Add(Mul("NightFerry", "Night Ferry", "Troops load and unload faster.", CardEffectKind.PeopleTransferSpeedMul, 1.12f, 0.03f, CardRarity.Rare));
                    list.Add(Mul("OrbitLift", "Orbit Lift", "Orbit-ring transfers run hotter.", CardEffectKind.PeopleTransferSpeedMul, 1.08f, 0.02f, CardRarity.Uncommon));
                    list.Add(Mul("AyeTow", "Aye Tow", "Tractor pull is stronger.", CardEffectKind.TractorPowerMul, 1.12f, 0.03f, CardRarity.Epic));
                    break;
                case "ProtonLegacy":
                    list.Add(Mul("ArcWell", "Arc Well", "Energy returns faster.", CardEffectKind.WeaponEnergyCostMul, 0.92f, -0.015f, CardRarity.Rare));
                    list.Add(Mul("CheapShot", "Cheap Shot", "Guns spend less energy per shot.", CardEffectKind.WeaponEnergyCostMul, 0.90f, -0.02f, CardRarity.Uncommon));
                    list.Add(Mul("LongBoost", "Long Boost", "OVERDRIVE lasts on less drain.", CardEffectKind.OverdriveDrainMul, 0.88f, -0.02f, CardRarity.Epic));
                    break;
                case "SpaceExcalibur":
                    list.Add(Mul("BladeRam", "Blade Ram", "Ramming cuts deeper.", CardEffectKind.RammingMul, 1.14f, 0.03f, CardRarity.Rare));
                    list.Add(Mul("ReflectPlate", "Reflect Plate", "Incoming damage is reduced.", CardEffectKind.IncomingDamageTakenMul, 0.93f, -0.012f, CardRarity.Uncommon));
                    list.Add(Mul("AfterRam", "After Ram", "OVERDRIVE snap after impact.", CardEffectKind.OverdriveSpeedMul, 1.06f, 0.02f, CardRarity.Epic));
                    break;
                case "StarForce":
                    list.Add(Add("Magazine", "Magazine", "Rocket packs carry more.", CardEffectKind.RocketPackSizeAdd, 1f, 0.3f, CardRarity.Uncommon));
                    list.Add(Mul("MineYield", "Mine Yield", "Mines deal more blast damage.", CardEffectKind.MineDamageMul, 1.12f, 0.03f, CardRarity.Rare));
                    list.Add(Mul("AegisDrone", "Aegis Drone", "Shield drones absorb more.", CardEffectKind.ShieldDroneAbsorbMul, 1.12f, 0.03f, CardRarity.Epic));
                    break;
                case "StriderOx":
                    list.Add(Mul("CattleCar", "Cattle Car", "Troop transfers run faster.", CardEffectKind.PeopleTransferSpeedMul, 1.14f, 0.03f, CardRarity.Rare));
                    list.Add(Add("WideRamp", "Wide Ramp", "Each unload sphere carries more troops.", CardEffectKind.PeopleUnloadChunkAdd, 1f, 0.25f, CardRarity.Uncommon));
                    list.Add(Mul("OxVault", "Ox Vault", "Gem deposits clear faster.", CardEffectKind.GemDepositSpeedMul, 1.10f, 0.025f, CardRarity.Epic));
                    break;
                default:
                    list.Add(Mul("GenericDeposit", "Refinery Link", "Gem deposits clear faster.", CardEffectKind.GemDepositSpeedMul, 1.08f, 0.02f, CardRarity.Uncommon));
                    list.Add(Mul("GenericMine", "Survey Laser", "Mining chips rocks faster.", CardEffectKind.MiningRateMul, 1.08f, 0.02f, CardRarity.Rare));
                    list.Add(Mul("GenericFerry", "Transit Link", "Troops load and unload faster.", CardEffectKind.PeopleTransferSpeedMul, 1.08f, 0.02f, CardRarity.Epic));
                    break;
            }

            if (!bonuses.IsIdentity)
            {
                list.Add(new Archetype
                {
                    idSuffix = "FamilyCrest",
                    displayName = "Family Crest",
                    description = "Stacks this family's special bonuses.",
                    kind = CardEffectKind.None,
                    magnitudeAtLevel1 = 1f,
                    magnitudePerLevel = 0.03f,
                    rarity = CardRarity.Legendary,
                    usesFamilyBonusOverlay = true,
                });
            }

            return list;
        }

        /// <summary>Magnitude at spin tier L (1–7).</summary>
        public static float MagnitudeAtLevel(in Archetype arch, int level)
        {
            int l = Mathf.Max(1, level);
            return arch.magnitudeAtLevel1 + arch.magnitudePerLevel * (l - 1);
        }

        /// <summary>Stable card id: {family}_{suffix}_L{level}.</summary>
        public static string FormatCardId(string familyId, string idSuffix, int level) =>
            Sanitize(familyId) + "_" + idSuffix + "_L" + Mathf.Max(1, level);

        static Archetype Mul(string id, string name, string desc, CardEffectKind kind, float l1, float per, CardRarity rarity) =>
            new Archetype
            {
                idSuffix = id,
                displayName = name,
                description = desc,
                kind = kind,
                magnitudeAtLevel1 = l1,
                magnitudePerLevel = per,
                rarity = rarity,
            };

        static Archetype Add(string id, string name, string desc, CardEffectKind kind, float l1, float per, CardRarity rarity) =>
            Mul(id, name, desc, kind, l1, per, rarity);

        static string Sanitize(string familyId)
        {
            if (string.IsNullOrWhiteSpace(familyId))
                return "Family";
            return familyId.Trim().Replace(" ", "");
        }
    }
}
