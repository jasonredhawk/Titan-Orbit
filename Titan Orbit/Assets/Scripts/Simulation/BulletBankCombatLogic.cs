using TitanOrbit.Data;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Fire-time and hit-time helpers for <see cref="BulletVfxBank"/> profiles.
    /// Looks up the Resources bank once and applies stat modifiers / damage multipliers.
    /// On-hit status (shock, burn, push, gravity) is applied by ECS systems.
    /// </summary>
    public static class BulletBankCombatLogic
    {
        static BulletVfxBank s_Bank;

        /// <summary>Cached <see cref="BulletVfxBank.LoadDefault"/> (null when the asset is missing).</summary>
        public static BulletVfxBank Bank
        {
            get
            {
                if (s_Bank == null)
                    s_Bank = BulletVfxBank.LoadDefault();
                return s_Bank;
            }
        }

        /// <summary>Resolves the profile for <paramref name="bankIndex"/>; false when missing.</summary>
        public static bool TryGetProfile(int bankIndex, out BulletBankProfile profile)
        {
            profile = null;
            var bank = Bank;
            return bank != null && bank.TryGetProfile(bankIndex, out profile) && profile != null;
        }

        /// <summary>Authored 0 is treated as 1 (unset / identity).</summary>
        public static float SafeMul(float authored) => authored > 0f ? authored : 1f;

        /// <summary>
        /// Multiplies fire-time combat numbers by the category profile.
        /// When <paramref name="lifetime"/> is positive, it is rebuilt as range/speed (PD uses 0).
        /// </summary>
        public static void ApplyFireModifiers(
            int bankIndex,
            ref float damage,
            ref float speed,
            ref float maxDistance,
            ref float lifetime,
            ref float fireRate)
        {
            if (!TryGetProfile(bankIndex, out BulletBankProfile profile))
                return;

            BulletBankStatModifiers s = profile.statModifiers;
            damage *= SafeMul(s.firePowerMultiplier);
            speed *= SafeMul(s.bulletSpeedMultiplier);
            fireRate *= SafeMul(s.fireRateMultiplier);

            float rangeMul = SafeMul(s.bulletRangeMultiplier);
            if (profile.HasBurn)
                rangeMul *= profile.GetBurnBulletRangeMultiplier();
            maxDistance *= rangeMul;

            if (lifetime > 0.001f)
                lifetime = math.max(0.1f, maxDistance / math.max(1f, speed));
        }

        /// <summary>Fire-power / cooldown scale for a planned ship volley (0 → 1).</summary>
        public static void GetShotScales(int bankIndex, out float firePowerMul, out float fireRateMul)
        {
            firePowerMul = 1f;
            fireRateMul = 1f;
            if (!TryGetProfile(bankIndex, out BulletBankProfile profile))
                return;
            firePowerMul = SafeMul(profile.statModifiers.firePowerMultiplier);
            fireRateMul = SafeMul(profile.statModifiers.fireRateMultiplier);
        }

        /// <summary>Ramming offense multiplier for the ship's current bank (0 → 1).</summary>
        public static float GetRammingPowerMultiplier(int bankIndex)
        {
            if (!TryGetProfile(bankIndex, out BulletBankProfile profile))
                return 1f;
            return SafeMul(profile.statModifiers.rammingPowerMultiplier);
        }

        /// <summary>Base damage × stacked damage-multiplier abilities for this target class.</summary>
        public static float ResolveHitDamage(BulletBankProfile profile, BulletBankDamageTarget target, float baseDamage)
        {
            if (profile == null)
                return baseDamage;
            return baseDamage * profile.GetDamageMultiplier(target);
        }

        /// <summary>Looks up the profile then applies <see cref="ResolveHitDamage"/>.</summary>
        public static float ResolveHitDamage(int bankIndex, byte hitKind, float baseDamage)
        {
            if (!TryGetProfile(bankIndex, out BulletBankProfile profile))
                return baseDamage;
            return ResolveHitDamage(profile, BulletBankAbilityTargeting.FromHitKind(hitKind), baseDamage);
        }

        /// <summary>True when the profile heals same-team ships on contact.</summary>
        public static bool HasHealFriendly(BulletBankProfile profile) =>
            profile != null && profile.HasAbility(BulletBankAbilityType.HealFriendly);

        /// <summary>Heal amount per ally hit (0 when the ability is missing).</summary>
        public static float GetHealFriendlyAmount(BulletBankProfile profile)
        {
            if (profile == null || !profile.TryGetAbility(BulletBankAbilityType.HealFriendly, out BulletBankAbility ability) ||
                ability == null)
                return 0f;
            return math.max(0f, ability.magnitude);
        }
    }
}
