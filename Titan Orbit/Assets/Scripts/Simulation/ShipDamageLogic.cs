using TitanOrbit.Core;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared hull + cargo damage rules.
    /// Hull absorbs first. At 0 HP leftover firepower dribbles cargo and can spin the hull.
    /// Death requires hull <b>and</b> gems empty — 0 HP alone does not kill.
    /// Pickup / fire / tractor treat a 0-HP wreck like dead. Pure math (no Entities / spawning).
    /// </summary>
    public static class ShipDamageLogic
    {
        /// <summary>Treat hull/gems at or below this as empty for death and spill tests.</summary>
        public const float DeathThreshold = 0.001f;

        /// <summary>
        /// Default leftover-firepower → cargo spill rate while hull is 0 (1:1).
        /// Designer override lives on <c>ShipImpactSpinSettings.LeftoverFirepowerGemRate</c>.
        /// </summary>
        public const float ExcessDamageGemExpulsionPerHullDamage = 1f;

        /// <summary>
        /// [LEGACY] Older bullet tuning (~50% of damage → gem value). Unused after 1:1 spill.
        /// </summary>
        public const float LegacyGemExpulsionPerDamage = 0.5f;

        /// <summary>
        /// [LEGACY] Former cap on gem spill on the hull-breaking bullet hit. Unused after wreck dribble.
        /// </summary>
        public const float MaxLethalExpulsionFraction = 0.6f;

        /// <summary>
        /// [LEGACY] Former per-hit cargo fraction while hull was already 0. Unused after wreck dribble.
        /// </summary>
        public const float MaxPostDeathExpulsionFraction = 0.4f;

        /// <summary>
        /// Outcome of one damage application. Callers write Health/Gems/IsDead back to
        /// <c>ShipState</c>, enter wreck when <see cref="BecameWrecked"/>, and spawn gems when
        /// <see cref="GemsToExpel"/> &gt; 0.
        /// </summary>
        public struct Result
        {
            /// <summary>Cargo to spawn this hit (0-HP leftover-firepower dribble).</summary>
            public float GemsToExpel;

            /// <summary>True when this call set IsDead because hull and gems are both empty.</summary>
            public bool BecameDead;

            /// <summary>True when hull is 0 with cargo still aboard — caller starts wreck spin.</summary>
            public bool BecameWrecked;

            /// <summary>True when Health decreased (regen delay should latch).</summary>
            public bool AppliedHullDamage;

            /// <summary>Signed health delta (negative when damaged) for floating-count UI.</summary>
            public float HealthDelta;

            /// <summary>
            /// Firepower not absorbed by remaining hull (crossing 0 HP leftover, or full hit on a wreck).
            /// Drives wreck yaw; 0 on living non-lethal hits.
            /// </summary>
            public float LeftoverFirepower;
        }

        /// <summary>
        /// Applies hull damage, 0-HP cargo dribble, and death only when hull and gems are empty.
        /// Friendly fire (matching non-None teams) and already-dead ships are no-ops.
        /// </summary>
        public static Result ApplyHullAndGemDamage(
            ref float health,
            ref float currentGems,
            ref bool isDead,
            float damage,
            TeamId shipTeam,
            TeamId attackerTeam,
            float gemExpulsionPerHullDamage,
            bool isImmune,
            bool isWrecked = false,
            float minGemSpawn = ShipImpactSpinLogic.DefaultMinGemSpawnValue)
        {
            var result = default(Result);

            if (attackerTeam != TeamId.None && attackerTeam == shipTeam)
                return result;
            if (isDead)
                return result;
            if (isImmune)
                return result;
            if (damage <= 0.0001f)
                return result;

            float minSpawn = minGemSpawn > 0f ? minGemSpawn : ShipImpactSpinLogic.DefaultMinGemSpawnValue;
            float gemRate = gemExpulsionPerHullDamage >= 0f
                ? gemExpulsionPerHullDamage
                : ExcessDamageGemExpulsionPerHullDamage;

            float healthBefore = health;
            bool hullWasUp = healthBefore > DeathThreshold;

            if (hullWasUp)
            {
                float newHealth = healthBefore - damage;
                if (newHealth < 0f)
                    newHealth = 0f;
                result.HealthDelta = newHealth - healthBefore;
                health = newHealth;
                result.AppliedHullDamage = true;
                if (newHealth <= DeathThreshold)
                    result.LeftoverFirepower = mathMax0(damage - healthBefore);
            }
            else
            {
                // Already 0 HP: the whole hit is leftover firepower into cargo + spin.
                result.LeftoverFirepower = damage;
            }

            if (health > DeathThreshold)
                return result;

            health = 0f;
            if (result.LeftoverFirepower > 0.0001f)
            {
                float expel = ShipImpactSpinLogic.ComputeGemsToExpel(
                    result.LeftoverFirepower, currentGems, gemRate, minSpawn);
                if (expel > 0f)
                {
                    currentGems -= expel;
                    if (currentGems < DeathThreshold)
                        currentGems = 0f;
                    result.GemsToExpel = expel;
                }
            }

            if (currentGems >= minSpawn)
            {
                result.BecameWrecked = !isWrecked || hullWasUp;
                isDead = false;
                return result;
            }

            isDead = true;
            result.BecameDead = true;
            return result;
        }

        static float mathMax0(float value) => value > 0f ? value : 0f;

        /// <summary>
        /// Sets <paramref name="isDead"/> only when hull <b>and</b> cargo are empty.
        /// 0 HP with leftover gems stays alive so later hits can keep expelling cargo.
        /// </summary>
        public static bool TryMarkDeadIfHullDepleted(
            ref float health,
            ref float currentGems,
            ref bool isDead,
            bool isWrecked = false,
            float minGemSpawn = ShipImpactSpinLogic.DefaultMinGemSpawnValue)
        {
            _ = isWrecked;
            if (isDead)
                return false;
            if (health > DeathThreshold)
                return false;

            float minSpawn = minGemSpawn > 0f ? minGemSpawn : ShipImpactSpinLogic.DefaultMinGemSpawnValue;
            if (currentGems >= minSpawn)
                return false;

            health = 0f;
            isDead = true;
            currentGems = 0f;
            return true;
        }

        /// <summary>
        /// Death when hull and gems are both empty. Same as <see cref="TryMarkDeadIfHullDepleted"/>.
        /// </summary>
        public static bool TryMarkDeadIfHullAndGemsDepleted(
            ref float health,
            ref float currentGems,
            ref bool isDead)
        {
            return TryMarkDeadIfHullDepleted(ref health, ref currentGems, ref isDead);
        }
    }
}
