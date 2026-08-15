using System.Globalization;
using System.Text;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Simulation;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Bottom-HUD / speedometer copy for the ship's live bullet type (Fireballs, Rift, …).
    /// Shows authored multipliers and Extra-Level-scaled abilities (burn, pull, push, …).
    /// Presentation-only — never writes ECS.
    /// </summary>
    public static class BulletBankHudCopy
    {
        const string HexMute = "5B7A94";
        const string HexResult = "AAEEDD";
        const string HexAccent = "FFAA66";

        /// <summary>B-key / heal bank currently fired by the local ship (0 when unknown).</summary>
        public static int ResolveLiveFireBankIndex()
        {
            if (!EcsGameBridge.TryGetLocalShipLoadout(out ShipLoadoutState loadout))
                return 0;
            return BulletBankFireResolve.ResolveFireBankIndex(in loadout);
        }

        /// <summary>Fingerprint bit for chip / tip snapshot keys (bank index + heal flag).</summary>
        public static int SnapshotKey()
        {
            if (!EcsGameBridge.TryGetLocalShipLoadout(out ShipLoadoutState loadout))
                return 0;
            int bank = BulletBankFireResolve.ResolveFireBankIndex(in loadout);
            return (bank << 1) | (loadout.HealingBulletsActive ? 1 : 0);
        }

        /// <summary>Writes the resolved fire bank onto a live HUD snapshot.</summary>
        public static void ApplyLoadout(ref ShipSpeedometerStatTooltips.LiveContext live)
        {
            if (!EcsGameBridge.TryGetLocalShipLoadout(out ShipLoadoutState loadout))
                return;
            live.FireBankIndex = BulletBankFireResolve.ResolveFireBankIndex(in loadout);
            live.HealingBulletsActive = loadout.HealingBulletsActive;
        }

        /// <summary>One-line chip glance, e.g. <c>Fireballs</c> or <c>EnergySpheres  HEAL</c>.</summary>
        public static string FormatChipTypeLine(in ShipSpeedometerStatTooltips.LiveContext live)
        {
            var bank = BulletBankCombatLogic.Bank;
            if (bank == null)
                return null;
            string name = bank.GetCategoryName(live.FireBankIndex);
            if (string.IsNullOrEmpty(name))
                return null;
            return live.HealingBulletsActive ? name + "  HEAL" : name;
        }

        /// <summary>
        /// Full ORDNANCE block: type name, bank multipliers, and every ability at current
        /// Fire Power Extra Levels (same numbers combat uses).
        /// </summary>
        public static void AppendFullSection(
            StringBuilder sb,
            in ShipSpeedometerStatTooltips.LiveContext live,
            in ShipAttributeUpgradeState attrs)
        {
            if (sb == null)
                return;

            int firePowerLv = attrs.FirePower > 0 ? attrs.FirePower : live.FirePowerAbilityLevel;
            int extras = BulletBankCombatLogic.CountFirePowerExtraLevels(
                live.Ship.ShipLevel, firePowerLv);
            var bank = BulletBankCombatLogic.Bank;
            if (bank == null || !bank.TryGetProfile(live.FireBankIndex, out BulletBankProfile profile) ||
                profile == null)
            {
                ShipStatTooltipChrome.AppendSectionBanner(sb, "ORDNANCE", HexAccent);
                sb.Append("<color=#").Append(HexMute).Append(">No bullet bank loaded.</color>").AppendLine();
                return;
            }

            string typeName = bank.GetCategoryName(live.FireBankIndex);
            if (string.IsNullOrEmpty(typeName))
                typeName = "Bank " + live.FireBankIndex.ToString(CultureInfo.InvariantCulture);

            ShipStatTooltipChrome.AppendSectionBanner(sb, "ORDNANCE", HexAccent);
            sb.Append("<b><color=#E8F4FF>").Append(typeName).Append("</color></b>");
            if (live.HealingBulletsActive)
                sb.Append("  <color=#7DFFB2>HEAL</color>");
            sb.AppendLine();

            sb.Append("<color=#").Append(HexMute).Append(">Extra Levels  </color>");
            AppendTint(sb, HexResult, extras.ToString(CultureInfo.InvariantCulture));
            sb.Append("  <color=#").Append(HexMute).Append(">(ship ")
                .Append(Mathf.Max(1, live.Ship.ShipLevel).ToString(CultureInfo.InvariantCulture))
                .Append(" + FP ")
                .Append(Mathf.Max(0, firePowerLv).ToString(CultureInfo.InvariantCulture))
                .Append(")</color>")
                .AppendLine();

            AppendStatModifiers(sb, profile.statModifiers, live.Weapon.BulletDamage);

            if (profile.abilities == null || profile.abilities.Count == 0)
            {
                sb.Append("<color=#").Append(HexMute).Append(">No special abilities on this type.</color>")
                    .AppendLine();
                return;
            }

            for (int i = 0; i < profile.abilities.Count; i++)
            {
                BulletBankAbility authored = profile.abilities[i];
                if (authored == null)
                    continue;
                AppendAbility(sb, authored, extras);
            }

            float drain = profile.GetTotalAbilityEnergyDrain(extras);
            if (drain > 0.0001f)
            {
                sb.Append("Energy drain  ");
                AppendTint(sb, HexResult, F(drain));
                sb.Append("<color=#").Append(HexMute).Append(">/shot  (on top of Fire Power)</color>")
                    .AppendLine();
            }
        }

        static void AppendStatModifiers(
            StringBuilder sb,
            BulletBankStatModifiers m,
            float hullDamage)
        {
            float fp = SafeMul(m.firePowerMultiplier);
            float spd = SafeMul(m.bulletSpeedMultiplier);
            float rate = SafeMul(m.fireRateMultiplier);
            float range = SafeMul(m.bulletRangeMultiplier);
            float ram = SafeMul(m.rammingPowerMultiplier);

            sb.Append("Muls  ");
            AppendMulToken(sb, "FP", fp);
            sb.Append("  ");
            AppendMulToken(sb, "Spd", spd);
            sb.Append("  ");
            AppendMulToken(sb, "Rate", rate);
            sb.Append("  ");
            AppendMulToken(sb, "Range", range);
            sb.Append("  ");
            AppendMulToken(sb, "Ram", ram);
            sb.AppendLine();

            if (hullDamage > 0.01f && !Mathf.Approximately(fp, 1f))
            {
                sb.Append("Shot  hull ").Append(F(hullDamage))
                    .Append(" x FP ").Append(F(fp))
                    .Append(" = ");
                AppendTint(sb, HexResult, F(hullDamage * fp));
                sb.Append("/hit").AppendLine();
            }
        }

        static void AppendAbility(StringBuilder sb, BulletBankAbility authored, int extras)
        {
            BulletBankAbility now = authored.Resolved(extras);
            switch (authored.type)
            {
                case BulletBankAbilityType.ElectricShockDisable:
                    sb.Append("Shock  stun ");
                    AppendTint(sb, HexResult, F(now.duration));
                    sb.Append("s");
                    AppendPerExtra(sb, authored.durationPerExtra, "s");
                    sb.AppendLine();
                    break;

                case BulletBankAbilityType.BurnOverTime:
                    sb.Append("Burn DoT  ");
                    AppendTint(sb, HexResult, F(now.magnitude));
                    sb.Append("/s  for ");
                    AppendTint(sb, HexResult, F(now.duration));
                    sb.Append("s  tick ");
                    AppendTint(sb, HexResult, F(now.tickInterval));
                    sb.Append("s");
                    sb.AppendLine();
                    AppendAbilityPerExtras(sb, authored, showMagnitude: true, showDuration: true,
                        showTick: true, showRadius: authored.radius > 0.001f || authored.radiusPerExtra != 0f,
                        magnitudeUnit: "/s", radiusLabel: "extra range");
                    break;

                case BulletBankAbilityType.HealFriendly:
                    sb.Append("Heal  ");
                    AppendTint(sb, HexResult, F(now.magnitude));
                    sb.Append("/hit");
                    AppendPerExtra(sb, authored.magnitudePerExtra, "/hit");
                    sb.AppendLine();
                    break;

                case BulletBankAbilityType.ConcussivePush:
                    sb.Append("Push  force ");
                    AppendTint(sb, HexResult, F(now.magnitude));
                    sb.Append("  blast ");
                    AppendTint(sb, HexResult, F(now.radius));
                    sb.AppendLine();
                    sb.Append("Splash dmg  full at center, 0 at edge");
                    sb.AppendLine();
                    AppendAbilityPerExtras(sb, authored, showMagnitude: true, showDuration: false,
                        showTick: false, showRadius: true, magnitudeUnit: " force", radiusLabel: "blast");
                    break;

                case BulletBankAbilityType.GravityPull:
                    sb.Append("Pull  radius ");
                    AppendTint(sb, HexResult, F(now.radius));
                    sb.Append("  force ");
                    AppendTint(sb, HexResult, F(now.magnitude));
                    sb.Append("  field ");
                    AppendTint(sb, HexResult, F(now.duration));
                    sb.Append("s");
                    sb.AppendLine();
                    AppendAbilityPerExtras(sb, authored, showMagnitude: true, showDuration: true,
                        showTick: false, showRadius: true, magnitudeUnit: " force", radiusLabel: "radius");
                    break;

                case BulletBankAbilityType.DamageMultiplier:
                case BulletBankAbilityType.DamageMultiplierVsAsteroid:
                case BulletBankAbilityType.DamageMultiplierVsShip:
                case BulletBankAbilityType.DamageMultiplierVsGemMoon:
                case BulletBankAbilityType.DamageMultiplierVsGem:
                    sb.Append("Dmg x  ");
                    AppendTint(sb, HexResult, F(now.magnitude));
                    sb.Append("  vs ").Append(DamageTargetLabel(authored));
                    AppendPerExtra(sb, authored.magnitudePerExtra, "");
                    sb.AppendLine();
                    break;

                case BulletBankAbilityType.StretchLengthInFlight:
                    sb.Append("Stretch  ");
                    AppendTint(sb, HexResult, F(now.radius));
                    sb.Append(" -> ");
                    AppendTint(sb, HexResult, F(now.magnitude));
                    sb.Append(" length");
                    sb.AppendLine();
                    break;

                default:
                    sb.Append(authored.type.ToString()).Append("  mag ");
                    AppendTint(sb, HexResult, F(now.magnitude));
                    sb.AppendLine();
                    break;
            }
        }

        static void AppendAbilityPerExtras(
            StringBuilder sb,
            BulletBankAbility authored,
            bool showMagnitude,
            bool showDuration,
            bool showTick,
            bool showRadius,
            string magnitudeUnit,
            string radiusLabel)
        {
            bool any = (showMagnitude && authored.magnitudePerExtra != 0f)
                || (showDuration && authored.durationPerExtra != 0f)
                || (showTick && authored.tickIntervalPerExtra != 0f)
                || (showRadius && authored.radiusPerExtra != 0f);
            if (!any)
                return;

            sb.Append("  <color=#").Append(HexMute).Append(">/extra  ");
            bool wrote = false;
            if (showMagnitude && authored.magnitudePerExtra != 0f)
            {
                sb.Append(Signed(authored.magnitudePerExtra)).Append(magnitudeUnit);
                wrote = true;
            }

            if (showDuration && authored.durationPerExtra != 0f)
            {
                if (wrote) sb.Append("  ");
                sb.Append(Signed(authored.durationPerExtra)).Append("s");
                wrote = true;
            }

            if (showTick && authored.tickIntervalPerExtra != 0f)
            {
                if (wrote) sb.Append("  ");
                sb.Append(Signed(authored.tickIntervalPerExtra)).Append("s tick");
                wrote = true;
            }

            if (showRadius && authored.radiusPerExtra != 0f)
            {
                if (wrote) sb.Append("  ");
                sb.Append(Signed(authored.radiusPerExtra)).Append(" ").Append(radiusLabel);
            }

            sb.Append("</color>").AppendLine();
        }

        static string DamageTargetLabel(BulletBankAbility authored)
        {
            if (authored.type == BulletBankAbilityType.DamageMultiplierVsAsteroid)
                return "asteroids";
            if (authored.type == BulletBankAbilityType.DamageMultiplierVsShip)
                return "ships";
            if (authored.type == BulletBankAbilityType.DamageMultiplierVsGemMoon)
                return "moons";
            if (authored.type == BulletBankAbilityType.DamageMultiplierVsGem)
                return "gems";
            return authored.damageTarget.ToString();
        }

        static void AppendMulToken(StringBuilder sb, string label, float mul)
        {
            bool highlight = !Mathf.Approximately(mul, 1f);
            sb.Append("<color=#").Append(HexMute).Append('>').Append(label).Append(" x</color>");
            AppendTint(sb, highlight ? HexResult : HexMute, F(mul));
        }

        static void AppendPerExtra(StringBuilder sb, float perExtra, string unit)
        {
            if (perExtra == 0f)
                return;
            sb.Append("  <color=#").Append(HexMute).Append(">/extra ")
                .Append(Signed(perExtra)).Append(unit).Append("</color>");
        }

        static float SafeMul(float authored) => authored > 0f ? authored : 1f;

        static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        static string Signed(float v)
        {
            string body = F(v);
            return v > 0f && body[0] != '+' ? "+" + body : body;
        }

        static void AppendTint(StringBuilder sb, string hex, string text)
        {
            sb.Append("<color=#").Append(hex).Append('>').Append(text).Append("</color>");
        }
    }
}
