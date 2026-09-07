using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Planet population cap and passive growth formulas shared by server ECS
    /// (<see cref="ECS.Systems.PlanetPopulationGrowthSystem"/>) and legacy planet code.
    /// Max population scales with planet size and level; passive growth is a fixed fraction of that
    /// cap so every planet takes the same time to refill from empty (see
    /// <see cref="FullRefillSeconds"/>). Pure math — Burst-friendly via Unity.Mathematics where possible.
    /// </summary>
    public static class PlanetPopulationMath
    {
        /// <summary>
        /// [TITAN-ORBIT] Seconds to pause passive growth after hostile people unload on a planet.
        /// </summary>
        public const float PopulationGrowthPauseAfterAttackSeconds = 1f;

        /// <summary>
        /// [TITAN-ORBIT] Seconds for a planet to grow from 0 to its current max population at the
        /// passive rate. Growth = maxPop / FullRefillSeconds people per second, so larger / higher-level
        /// planets add more population per second but still take this long to refill.
        /// Longer refill leaves a freshly captured (empty) planet vulnerable to recapture before it
        /// can stock defenders again.
        /// </summary>
        public const float FullRefillSeconds = 120f;

        /// <summary>
        /// [TITAN-ORBIT] Exponent on planet level in the max-population formula
        /// (<c>size × level^PopulationLevelExponent</c>). Higher than the old 1.5 so late-game
        /// planets hold more population relative to a few fully loaded high-level ships.
        /// </summary>
        public const float PopulationLevelExponent = 1.7f;

        /// <summary>
        /// Max population: size × level^<see cref="PopulationLevelExponent"/> (rounded to int).
        /// Example at level 6 (6^1.7 ≈ 21.0): size 15 → ~315, size 20 home → ~421.
        /// This is the <b>base</b> cap before triangle connection bonuses — see
        /// <see cref="GetEffectiveMaxPopulation"/> for the live gameplay ceiling.
        /// </summary>
        /// <param name="planetSize">World/visual scale of the planet (minimum 0.25).</param>
        /// <param name="planetLevel">Planet level (minimum 1).</param>
        public static int GetMaxPopulation(float planetSize, int planetLevel)
        {
            // --- Cap formula: size × level^1.7 ---
            // [TITAN-ORBIT] Raised from level^1.5 so top planets outscale a small late-game raid fleet.
            planetSize = Mathf.Max(0.25f, planetSize);
            int level = Mathf.Max(1, planetLevel);
            return Mathf.RoundToInt(planetSize * Mathf.Pow(level, PopulationLevelExponent));
        }

        /// <summary>
        /// Effective population cap after stacking triangle connection bonuses.
        /// Matches server growth: <c>round(baseMax × (1 + bonusFraction))</c>, at least 1.
        /// </summary>
        /// <param name="planetSize">World/visual scale of the planet (minimum 0.25).</param>
        /// <param name="planetLevel">Planet level (minimum 1).</param>
        /// <param name="connectionBonusFraction">
        /// Stacked corner bonus from territory triangles (0 = no bonus). Same value as
        /// <c>PlanetGrowthState.ConnectionBonusFraction</c> on the server.
        /// </param>
        /// <returns>Gameplay max population the planet can hold right now.</returns>
        public static int GetEffectiveMaxPopulation(
            float planetSize,
            int planetLevel,
            float connectionBonusFraction)
        {
            // --- Base cap × (1 + triangle bonus) ---
            // [TITAN-ORBIT] Same formula as PlanetPopulationGrowthSystem — keep label + sim in sync.
            int baseMax = GetMaxPopulation(planetSize, planetLevel);
            float bonus = math.max(0f, connectionBonusFraction);
            return math.max(1, (int)math.round(baseMax * (1f + bonus)));
        }

        /// <summary>
        /// Splits effective max into base size/level cap and the additive bonus people from triangles.
        /// Used by world planet labels: current on top, then <c>base + bonus</c> underneath.
        /// </summary>
        /// <param name="planetSize">World/visual scale of the planet.</param>
        /// <param name="planetLevel">Planet level.</param>
        /// <param name="connectionBonusFraction">Stacked triangle bonus fraction (0 = none).</param>
        /// <param name="baseMax">Size × level cap with no territory bonus.</param>
        /// <param name="bonusAmount">Extra people from connections (<c>effective − base</c>, ≥ 0).</param>
        public static void GetMaxPopulationBreakdown(
            float planetSize,
            int planetLevel,
            float connectionBonusFraction,
            out int baseMax,
            out int bonusAmount)
        {
            // --- Breakdown for HUD: base + bonus people ---
            baseMax = GetMaxPopulation(planetSize, planetLevel);
            int effective = GetEffectiveMaxPopulation(planetSize, planetLevel, connectionBonusFraction);
            bonusAmount = math.max(0, effective - baseMax);
        }

        /// <summary>
        /// Passive growth rate so empty → full always takes <see cref="FullRefillSeconds"/>.
        /// Pass the effective max (after territory / connection bonuses) so a boosted cap still
        /// refills in the same wall-clock time — rate scales with that larger max.
        /// </summary>
        /// <param name="maxPopulation">Effective population cap for this planet (minimum 1).</param>
        /// <returns>People added per second toward the cap.</returns>
        public static float GetGrowthRatePerSecond(int maxPopulation)
        {
            // --- Percent-of-cap growth: 1/FullRefillSeconds of max per second ---
            // [TITAN-ORBIT] Replaces the old 2^(level-1)/5 curve, which made high levels refill in seconds.
            int maxPop = math.max(1, maxPopulation);
            return maxPop / FullRefillSeconds;
        }

        /// <summary>
        /// Rounds fractional sim population to integer for HUD labels and UI display.
        /// </summary>
        public static int FractionalToDisplayPopulation(float fractionalPopulation, int maxPopulation)
        {
            // --- Round and clamp for HUD labels ---
            return math.clamp((int)math.round(fractionalPopulation), 0, maxPopulation);
        }
    }
}
