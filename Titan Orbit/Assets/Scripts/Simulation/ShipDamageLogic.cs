using TitanOrbit.Core;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared server hull + cargo damage rules ported from the pre-ECS <c>Starship.ApplyDamageOnServer</c>.
    /// Hull absorbs damage first; gems spill only from excess on the breaking hit or while hull is already 0.
    /// Death requires both hull and carried gems depleted — not hull alone.
    /// Pure math (no Entities / spawning); callers spawn world gems from <see cref="Result.GemsToExpel"/>.
    /// </summary>
    public static class ShipDamageLogic
    {
        /// <summary>Treat hull/gems at or below this as empty for death and spill tests.</summary>
        public const float DeathThreshold = 0.001f;

        /// <summary>
        /// [TITAN-ORBIT] Bullet path: after hull is 0, ~50% of damage converts to expelled gem value
        /// (legacy NGO tuning).
        /// </summary>
        public const float LegacyGemExpulsionPerDamage = 0.5f;

        /// <summary>Cap on gem spill when the hull-breaking hit still has cargo.</summary>
        public const float MaxLethalExpulsionFraction = 0.6f;

        /// <summary>Cap on gem spill per hit while hull is already 0 (bullets only).</summary>
        public const float MaxPostDeathExpulsionFraction = 0.4f;

        /// <summary>
        /// Outcome of one damage application. Callers write Health/Gems/IsDead back to
        /// <c>ShipState</c> and spawn gems when <see cref="GemsToExpel"/> &gt; 0.
        /// </summary>
        public struct Result
        {
            /// <summary>Cargo value to spawn as world gems (already deducted from CurrentGems).</summary>
            public float GemsToExpel;

            /// <summary>True when this call set IsDead because hull and gems are both empty.</summary>
            public bool BecameDead;

            /// <summary>True when Health decreased (regen delay should latch).</summary>
            public bool AppliedHullDamage;

            /// <summary>Signed health delta (negative when damaged) for floating-count UI.</summary>
            public float HealthDelta;
        }

        /// <summary>
        /// Applies hull damage and computes gem expulsion. Does not spawn entities.
        /// Friendly fire (matching non-None teams) and already-dead ships are no-ops.
        /// </summary>
        /// <param name="health">Current hull; written when damage applies.</param>
        /// <param name="currentGems">Cargo hold; reduced when gems spill.</param>
        /// <param name="isDead">Lethal flag; set only when hull and gems are both depleted.</param>
        /// <param name="damage">Incoming damage amount (must be &gt; 0 to matter).</param>
        /// <param name="shipTeam">Target ship team.</param>
        /// <param name="attackerTeam">Attacker team; <see cref="TeamId.None"/> skips friendly check (ram/self).</param>
        /// <param name="gemExpulsionPerHullDamage">
        /// When &gt; 0 (ram/grind): 1:1 gem value from excess / post-zero damage.
        /// When ≤ 0 (bullets): legacy 50% rules with per-hit cargo fraction caps.
        /// </param>
        /// <param name="isImmune">True when fully moon-docked — no damage or spill.</param>
        /// <returns>Expulsion amount and death/hull flags for the caller.</returns>
        public static Result ApplyHullAndGemDamage(
            ref float health,
            ref float currentGems,
            ref bool isDead,
            float damage,
            TeamId shipTeam,
            TeamId attackerTeam,
            float gemExpulsionPerHullDamage,
            bool isImmune)
        {
            var result = default(Result);

            // --- Early outs ---
            // [TITAN-ORBIT] Friendly fire only when both have a real team and they match.
            if (attackerTeam != TeamId.None && attackerTeam == shipTeam)
                return result;
            if (isDead)
                return result;
            if (isImmune)
                return result;

            bool ramGrindGemExpulsion = gemExpulsionPerHullDamage > 0f;
            float healthBefore = health;
            bool wasAlive = healthBefore > DeathThreshold;

            // --- Hull phase ---
            if (wasAlive && damage > 0.0001f)
            {
                float newHealth = healthBefore - damage;
                if (newHealth < 0f)
                    newHealth = 0f;
                result.HealthDelta = newHealth - healthBefore;
                health = newHealth;
                result.AppliedHullDamage = true;
            }

            // --- Gem spill (only after hull is 0, plus excess on the breaking hit) ---
            float gemsToExpel = 0f;
            if (currentGems > 0.0001f)
            {
                if (ramGrindGemExpulsion)
                {
                    // Ram/grind: 1:1 gem value with damage (excess on breaking hit; full damage when hull already 0).
                    if (wasAlive)
                    {
                        float excessDamage = damage - healthBefore;
                        if (excessDamage > 0f)
                            gemsToExpel = excessDamage * gemExpulsionPerHullDamage;
                    }
                    else
                    {
                        gemsToExpel = damage * gemExpulsionPerHullDamage;
                    }
                }
                else if (wasAlive)
                {
                    float excessDamage = damage - healthBefore;
                    if (excessDamage > 0f)
                    {
                        float desired = excessDamage * LegacyGemExpulsionPerDamage;
                        float maxForThisHit = currentGems * MaxLethalExpulsionFraction;
                        gemsToExpel = desired < maxForThisHit ? desired : maxForThisHit;
                    }
                }
                else
                {
                    float desired = damage * LegacyGemExpulsionPerDamage;
                    float maxForThisHit = currentGems * MaxPostDeathExpulsionFraction;
                    gemsToExpel = desired < maxForThisHit ? desired : maxForThisHit;
                }

                if (gemsToExpel > currentGems)
                    gemsToExpel = currentGems;
            }

            if (gemsToExpel > 0.0001f)
            {
                currentGems -= gemsToExpel;
                if (currentGems < 0f)
                    currentGems = 0f;
                result.GemsToExpel = gemsToExpel;
            }

            // --- Dual-resource death ---
            if (TryMarkDeadIfHullAndGemsDepleted(ref health, ref currentGems, ref isDead))
                result.BecameDead = true;

            return result;
        }

        /// <summary>
        /// Sets <paramref name="isDead"/> when both hull and cargo are at/below
        /// <see cref="DeathThreshold"/>. Call after deposit / upgrade gem spends and before hull regen
        /// so a 0/0 frame cannot heal out of death.
        /// </summary>
        /// <returns>True when this call newly marked the ship dead.</returns>
        public static bool TryMarkDeadIfHullAndGemsDepleted(
            ref float health,
            ref float currentGems,
            ref bool isDead)
        {
            if (isDead)
                return false;
            if (health > DeathThreshold || currentGems > DeathThreshold)
                return false;

            // Clamp tiny leftovers so ghost snapshots stay clean.
            health = 0f;
            currentGems = 0f;
            isDead = true;
            return true;
        }

    }
}
