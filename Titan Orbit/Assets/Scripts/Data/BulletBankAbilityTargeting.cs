namespace TitanOrbit.Data
{
    /// <summary>
    /// Maps bullet-bank damage-multiplier abilities to <see cref="BulletBankDamageTarget"/> for
    /// hit resolution in <see cref="ECS.Systems.BulletCollision"/>. Pure static helpers — no state.
    /// Server uses these when applying <see cref="BulletBankAbility"/> rows from
    /// <see cref="BulletBankProfile"/> at impact time.
    /// </summary>
    public static class BulletBankAbilityTargeting
    {
        /// <summary>
        /// True when the ability type is any damage-multiplier variant (vs asteroid, ship, gem, etc.).
        /// </summary>
        /// <param name="type">Ability enum from bullet bank profile.</param>
        public static bool IsDamageMultiplierType(BulletBankAbilityType type)
        {
            // --- IsDamageMultiplierType ---
            return type == BulletBankAbilityType.DamageMultiplierVsAsteroid
                   || type == BulletBankAbilityType.DamageMultiplierVsShip
                   || type == BulletBankAbilityType.DamageMultiplierVsGemMoon
                   || type == BulletBankAbilityType.DamageMultiplierVsGem
                   || type == BulletBankAbilityType.DamageMultiplier;
        }

        /// <summary>
        /// Resolves the effective damage target for an ability row. Legacy per-type enums map to
        /// fixed targets; <see cref="BulletBankAbilityType.DamageMultiplier"/> uses
        /// <see cref="BulletBankAbility.damageTarget"/> from the profile.
        /// </summary>
        /// <param name="ability">Ability row from bullet bank; null returns Asteroid default.</param>
        public static BulletBankDamageTarget GetEffectiveDamageTarget(BulletBankAbility ability)
        {
            // --- Compute value ---
            if (ability == null) return BulletBankDamageTarget.Asteroid;

            // [TITAN-ORBIT] Map legacy typed multipliers to canonical damage targets.
            return ability.type switch
            {
                BulletBankAbilityType.DamageMultiplierVsAsteroid => BulletBankDamageTarget.Asteroid,
                BulletBankAbilityType.DamageMultiplierVsShip => BulletBankDamageTarget.ShipOrDrone,
                BulletBankAbilityType.DamageMultiplierVsGemMoon => BulletBankDamageTarget.GemMoon,
                BulletBankAbilityType.DamageMultiplierVsGem => BulletBankDamageTarget.Gem,
                BulletBankAbilityType.DamageMultiplier => ability.damageTarget,
                _ => ability.damageTarget,
            };
        }

        /// <summary>
        /// True when <paramref name="ability"/> is a damage multiplier that applies to
        /// <paramref name="queryTarget"/> (Everything matches all targets).
        /// </summary>
        public static bool MatchesDamageTarget(BulletBankAbility ability, BulletBankDamageTarget queryTarget)
        {
            // --- MatchesDamageTarget ---
            if (ability == null || !IsDamageMultiplierType(ability.type)) return false;

            BulletBankDamageTarget effective = GetEffectiveDamageTarget(ability);
            if (effective == BulletBankDamageTarget.Everything) return true;
            return effective == queryTarget;
        }
    }
}
