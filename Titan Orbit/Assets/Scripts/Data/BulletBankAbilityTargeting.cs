namespace TitanOrbit.Data
{
    /// <summary>Maps bullet-bank damage multiplier abilities to <see cref="BulletBankDamageTarget"/>.</summary>
    public static class BulletBankAbilityTargeting
    {
        public static bool IsDamageMultiplierType(BulletBankAbilityType type)
        {
            return type == BulletBankAbilityType.DamageMultiplierVsAsteroid
                   || type == BulletBankAbilityType.DamageMultiplierVsShip
                   || type == BulletBankAbilityType.DamageMultiplierVsGemMoon
                   || type == BulletBankAbilityType.DamageMultiplierVsGem
                   || type == BulletBankAbilityType.DamageMultiplier;
        }

        public static BulletBankDamageTarget GetEffectiveDamageTarget(BulletBankAbility ability)
        {
            if (ability == null) return BulletBankDamageTarget.Asteroid;
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

        public static bool MatchesDamageTarget(BulletBankAbility ability, BulletBankDamageTarget queryTarget)
        {
            if (ability == null || !IsDamageMultiplierType(ability.type)) return false;
            BulletBankDamageTarget effective = GetEffectiveDamageTarget(ability);
            if (effective == BulletBankDamageTarget.Everything) return true;
            return effective == queryTarget;
        }
    }
}
