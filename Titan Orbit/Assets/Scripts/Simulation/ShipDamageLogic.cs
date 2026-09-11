using TitanOrbit.Core;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared server hull + cargo damage rules ported from the pre-ECS <c>Starship.ApplyDamageOnServer</c>.
    /// Hull absorbs damage first. Death is hull depleted — remaining cargo stays on the ship so
    /// <c>ShipDeathRecordingSystem</c> can burst it as world gems (random count + random values).
    /// Mid-fight hits do not dribble cargo. Pickup is blocked when <c>IsDead</c>.
    /// Pure math (no Entities / spawning).
    /// </summary>
    public static class ShipDamageLogic
    {
        /// <summary>Treat hull/gems at or below this as empty for death and spill tests.</summary>
        public const float DeathThreshold = 0.001f;

        /// <summary>
        /// [LEGACY] Former 1:1 cargo spill rate after hull-zero. Combat no longer dribbles
        /// gems; leftover hold bursts from death recording.
        /// </summary>
        public const float ExcessDamageGemExpulsionPerHullDamage = 1f;

        /// <summary>
        /// [LEGACY] Older bullet tuning (~50% of damage → gem value). Unused after 1:1 spill.
        /// </summary>
        public const float LegacyGemExpulsionPerDamage = 0.5f;

        /// <summary>
        /// [LEGACY] Former cap on gem spill on the hull-breaking bullet hit. Unused after full dump.
        /// </summary>
        public const float MaxLethalExpulsionFraction = 0.6f;

        /// <summary>
        /// [LEGACY] Former per-hit cargo fraction while hull was already 0. Unused after full dump.
        /// </summary>
        public const float MaxPostDeathExpulsionFraction = 0.4f;

        /// <summary>
        /// Outcome of one damage application. Callers write Health/Gems/IsDead back to
        /// <c>ShipState</c> and spawn gems when <see cref="GemsToExpel"/> &gt; 0.
        /// </summary>
        public struct Result
        {
            /// <summary>
            /// [LEGACY] Combat no longer deducts cargo here. Death recording bursts leftover hold.
            /// </summary>
            public float GemsToExpel;

            /// <summary>True when this call set IsDead because hull is empty.</summary>
            public bool BecameDead;

            /// <summary>True when Health decreased (regen delay should latch).</summary>
            public bool AppliedHullDamage;

            /// <summary>Signed health delta (negative when damaged) for floating-count UI.</summary>
            public float HealthDelta;
        }

        /// <summary>
        /// Applies hull damage and marks death when hull is empty. Does not spawn entities
        /// and does not deduct cargo — leftover gems explode from death recording.
        /// Friendly fire (matching non-None teams) and already-dead ships are no-ops.
        /// </summary>
        /// <param name="health">Current hull; written when damage applies.</param>
        /// <param name="currentGems">Cargo hold; left intact for the death burst.</param>
        /// <param name="isDead">Lethal flag; set when hull is depleted.</param>
        /// <param name="damage">Incoming damage amount (must be &gt; 0 to matter).</param>
        /// <param name="shipTeam">Target ship team.</param>
        /// <param name="attackerTeam">Attacker team; <see cref="TeamId.None"/> skips friendly check (ram/self).</param>
        /// <param name="gemExpulsionPerHullDamage">
        /// Unused. Kept so existing combat callers compile. Death cargo uses the death burst.
        /// </param>
        /// <param name="isImmune">True when fully moon-docked — no damage or death.</param>
        /// <returns>Death/hull flags for the caller. <see cref="Result.GemsToExpel"/> stays 0.</returns>
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

            // Cargo is left on the hull for the death burst. Rate kept for caller signature.
            _ = currentGems;
            _ = gemExpulsionPerHullDamage;

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

            // --- Death (hull empty). Cargo stays for ShipDeathRecordingSystem. ---
            if (TryMarkDeadIfHullDepleted(ref health, ref currentGems, ref isDead))
                result.BecameDead = true;

            return result;
        }

        /// <summary>
        /// Sets <paramref name="isDead"/> when hull is at/below <see cref="DeathThreshold"/>.
        /// Leaves <paramref name="currentGems"/> so death recording can burst leftover cargo.
        /// Call after deposit / upgrade gem spends and before hull regen so a 0-HP frame
        /// cannot heal out of death.
        /// </summary>
        /// <returns>True when this call newly marked the ship dead.</returns>
        public static bool TryMarkDeadIfHullDepleted(
            ref float health,
            ref float currentGems,
            ref bool isDead)
        {
            if (isDead)
                return false;
            if (health > DeathThreshold)
                return false;

            health = 0f;
            isDead = true;
            _ = currentGems;
            return true;
        }

        /// <summary>
        /// [LEGACY name] Hull-empty death. Remaining cargo is not cleared.
        /// Prefer <see cref="TryMarkDeadIfHullDepleted"/>.
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
