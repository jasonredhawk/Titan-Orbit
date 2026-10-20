using System.Text;
using TitanOrbit;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Hull stamp for one opening of the NOTES screen. Taken from the local ship's
    /// replicated ghosts (the same numbers the HUD shows). There is no clock — the
    /// ship, gun, level, branch, and added ability levels are the stamp. Built once
    /// when the screen opens, then sent with whatever the tester types.
    /// </summary>
    public struct BetaFeedbackStamp
    {
        /// <summary>Nameplate name.</summary>
        public string Player;

        /// <summary>Family and chassis, such as "Astro Eagle — Falcon".</summary>
        public string Ship;

        /// <summary>Gun they are firing, with " HEAL" when heal mode is on.</summary>
        public string Gun;

        /// <summary>Upgrade-ladder level. 0 when they have no hull.</summary>
        public int ShipLevel;

        /// <summary>Branch inside that level.</summary>
        public int Branch;

        /// <summary>Purchased ability levels above zero, or "none".</summary>
        public string Abilities;

        /// <summary>Four-line card text. Not sent as its own field — the columns above are.</summary>
        public string Summary;

        /// <summary>True when a local ship ghost was readable.</summary>
        public bool HasHull;
    }

    /// <summary>
    /// Reads the local ship once and fills a <see cref="BetaFeedbackStamp"/>.
    /// Presentation only — does not write ECS. Call when NOTES opens, not every frame.
    /// </summary>
    public static class BetaFeedbackSnapshot
    {
        /// <summary>
        /// Stamps whatever the local player owns right now.
        /// A missing ship becomes "No hull" so they can still write a note about the menu.
        /// </summary>
        /// <returns>The snapshot the SEND button will attach.</returns>
        public static BetaFeedbackStamp Capture()
        {
            var stamp = new BetaFeedbackStamp
            {
                Player = LocalPlayerDisplayName.Get(),
                Ship = "No hull",
                Gun = "None",
                ShipLevel = 0,
                Branch = 0,
                Abilities = "none",
                HasHull = false,
            };

            // --- Hull identity ---
            // [HYBRID] These ghosts are already on the client. We do not scan the map.
            if (EcsGameBridge.TryGetLocalShipState(out ShipState state))
            {
                stamp.HasHull = true;
                stamp.ShipLevel = state.ShipLevel;
                stamp.Branch = state.BranchIndex;
                stamp.Ship = ResolveShipName(state);
                stamp.Gun = ResolveGunName(state);
            }

            if (EcsGameBridge.TryGetLocalShipAttributeUpgrades(out ShipAttributeUpgradeState attrs))
                stamp.Abilities = FormatAddedAbilities(in attrs);

            stamp.Summary = BuildSummary(stamp);
            return stamp;
        }

        /// <summary>Family display name plus the upgrade-tree chassis name when the ladder slot resolves.</summary>
        static string ResolveShipName(ShipState state)
        {
            PlanetShipFamilyConfig config = PlanetShipFamilyConfig.LoadDefault();
            if (config == null)
                return "Unknown hull";

            string family = config.GetFamilyDisplayName(state.ShipFamilyConfigIndex);
            string chassis = null;
            PlanetShipFamilyConfig.ShipFamilyEntry entry =
                config.GetFamilyByConfigIndex(state.ShipFamilyConfigIndex);
            int ladder = PlanetShipFamilyConfig.GetLadderLinearIndex(state.ShipLevel, state.BranchIndex);
            var tree = entry != null && entry.shipFamilyDefinition != null
                ? entry.shipFamilyDefinition.upgradeTree
                : null;
            if (tree != null && ladder >= 0 && ladder < tree.Count && tree[ladder] != null)
                chassis = tree[ladder].ResolveUpgradeTreeShipName();

            if (string.IsNullOrEmpty(family))
                return string.IsNullOrEmpty(chassis) ? "Unknown hull" : chassis;
            if (string.IsNullOrEmpty(chassis))
                return family;
            return family + " — " + chassis;
        }

        /// <summary>
        /// The bank the ship actually fires (B-key, or the heal bank). Falls back to the
        /// hull-default bank when the loadout ghost is missing.
        /// </summary>
        static string ResolveGunName(ShipState state)
        {
            int bankIndex = state.HullBulletBankIndex;
            bool heal = false;
            if (EcsGameBridge.TryGetLocalShipLoadout(out ShipLoadoutState loadout))
            {
                bankIndex = BulletBankFireResolve.ResolveFireBankIndex(in loadout);
                // Cycle-all debug uses the raw B-key index, including when it lands on heal.
                heal = loadout.HealingBulletsActive && !TitanOrbitDebugFlags.CycleAllBulletBanks;
            }

            BulletVfxBank bank = BulletVfxBank.LoadDefault();
            string name = bank != null ? bank.GetCategoryName(bankIndex) : string.Empty;
            if (string.IsNullOrEmpty(name))
                name = "Bank " + bankIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return heal ? name + "  HEAL" : name;
        }

        /// <summary>
        /// Only abilities they spent levels on. A zero is not "added", so it is left out.
        /// Names match the bottom upgrade bar.
        /// </summary>
        static string FormatAddedAbilities(in ShipAttributeUpgradeState attrs)
        {
            var sb = new StringBuilder(64);
            Append(sb, "Fire Power", attrs.FirePower);
            Append(sb, "Bullet Speed", attrs.BulletSpeed);
            Append(sb, "Health Cap", attrs.MaxHealth);
            Append(sb, "Health Regen", attrs.HealthRegen);
            Append(sb, "Energy Cap", attrs.EnergyCapacity);
            Append(sb, "Energy Regen", attrs.EnergyRegen);
            Append(sb, "Move Speed", attrs.MovementSpeed);
            Append(sb, "Turn Speed", attrs.RotationSpeed);
            Append(sb, "Gem Cap", attrs.GemCapacity);
            Append(sb, "Troop Cap", attrs.PeopleCapacity);
            return sb.Length == 0 ? "none" : sb.ToString();
        }

        /// <summary>Adds "Name N" when <paramref name="level"/> is at least 1.</summary>
        static void Append(StringBuilder sb, string name, int level)
        {
            if (level <= 0)
                return;
            if (sb.Length > 0)
                sb.Append(", ");
            sb.Append(name).Append(' ').Append(level.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Card body. Plain text, no rich-text tags, so a ship name cannot restyle the panel.</summary>
        static string BuildSummary(BetaFeedbackStamp stamp)
        {
            var sb = new StringBuilder(160);
            sb.Append("PILOT  ").Append(string.IsNullOrEmpty(stamp.Player) ? "Pilot" : stamp.Player);
            sb.Append('\n').Append("HULL   ").Append(stamp.Ship);
            if (stamp.HasHull)
            {
                sb.Append("    LV ").Append(stamp.ShipLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.Append("    BRANCH ").Append(stamp.Branch.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            sb.Append('\n').Append("GUN    ").Append(stamp.Gun);
            sb.Append('\n').Append("ADDED  ").Append(stamp.Abilities);
            return sb.ToString();
        }
    }
}
