using TitanOrbit.Core;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Shared server hull + cargo damage rules ported from the pre-ECS <c>Starship.ApplyDamageOnServer</c>.
    /// Hull absorbs damage first, and only up to the hull it has left. Asteroid rams then
    /// expel the leftover as gems (10 HP and 100 damage → 10 hull and 90 gems). Bullets,
    /// mines, and rockets still expel a gem worth the full hit once hull is 0
    /// (50-damage bullet → gem 50), clamped by remaining cargo.
    /// Death requires both hull and carried gems depleted — not hull alone — for every combat source
    /// (bullets, burn, mines, rockets, ram). A living 0-HP ship (cargo still aboard,
    /// <c>IsDead</c> false) may still tractor and scoop gems. Pickup is blocked only when
    /// <c>IsDead</c>. Pure math (no Entities / spawning); callers spawn world gems from
    /// <see cref="Result.GemsToExpel"/>.
    /// </summary>
    public static class ShipDamageLogic
    {
        /// <summary>Treat hull/gems at or below this as empty for death and spill tests.</summary>
        public const float DeathThreshold = 0.001f;

        /// <summary>
        /// 1:1 cargo spill: gem value equals incoming damage once hull is 0.
        /// Death still needs leftover cargo emptied by later hits.
        /// </summary>
        public const float ExcessDamageGemExpulsionPerHullDamage = 1f;

        /// <summary>
        /// [LEGACY] Older bullet tuning (~50% of damage → gem value). Unused after 1:1 spill.
        /// </summary>
        public const float LegacyGemExpulsionPerDamage = 0.5f;

        /// <summary>
        /// [LEGACY] Former cap on gem spill on the hull-breaking hit. Unused — spill is 1:1
        /// with incoming damage once hull is 0 (clamped only by remaining cargo).
        /// </summary>
        public const float MaxLethalExpulsionFraction = 0.6f;

        /// <summary>
        /// [LEGACY] Former per-hit cargo fraction while hull was already 0. Unused — same 1:1 rule.
        /// </summary>
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
        /// Cargo value per unit of incoming damage once hull is 0. ≤ 0 is treated as
        /// <see cref="ExcessDamageGemExpulsionPerHullDamage"/> (1:1). Clamped only by remaining cargo.
        /// </param>
        /// <param name="isImmune">True when fully moon-docked — no damage or spill.</param>
        /// <param name="spillLeftoverDamageOnly">
        /// True for asteroid ram/grind self-chips: hull absorbs what it can, and only the
        /// leftover (damage − hull absorbed) expels gems on that same hit. A 100-damage
        /// ram into 10 HP takes 10 hull and expels 90 gems. False (default) keeps
        /// bullet/mine/rocket 1:1 full-hit gems once hull is already 0.
        /// </param>
        /// <returns>Expulsion amount and death/hull flags for the caller.</returns>
        public static Result ApplyHullAndGemDamage(
            ref float health,
            ref float currentGems,
            ref bool isDead,
            float damage,
            TeamId shipTeam,
            TeamId attackerTeam,
            float gemExpulsionPerHullDamage,
            bool isImmune,
            bool spillLeftoverDamageOnly = false)
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

            float expulsionRate = gemExpulsionPerHullDamage > 0f
                ? gemExpulsionPerHullDamage
                : ExcessDamageGemExpulsionPerHullDamage;
            float healthBefore = health;
            bool wasAlive = healthBefore > DeathThreshold;

            // --- Hull phase ---
            // Hull only absorbs what it still has. The rest is leftover for cargo.
            float hullAbsorbed = 0f;
            if (wasAlive && damage > 0.0001f)
            {
                hullAbsorbed = damage < healthBefore ? damage : healthBefore;
                float newHealth = healthBefore - hullAbsorbed;
                result.HealthDelta = newHealth - healthBefore;
                health = newHealth;
                result.AppliedHullDamage = true;
            }

            // --- Gem spill once hull is gone ---
            // [TITAN-ORBIT] Bullets / mines keep full-hit gems once hull is 0
            // (50-damage bullet → gem 50), including the breaking hit.
            // Asteroid self-chips split the same hit: 10 HP left and 100 damage
            // takes 10 hull and expels the leftover 90 (clamped by cargo).
            float gemBasis = spillLeftoverDamageOnly ? damage - hullAbsorbed : damage;
            if (gemBasis < 0f)
                gemBasis = 0f;

            float gemsToExpel = 0f;
            if (currentGems > 0.0001f
                && gemBasis > 0.0001f
                && health <= DeathThreshold)
            {
                gemsToExpel = gemBasis * expulsionRate;
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
