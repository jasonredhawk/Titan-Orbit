using Unity.Mathematics;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Match-long top-of-team roles (Killer / Miner / Troops) and the 5% command bonuses
    /// those titles grant. The comms Command Deck uses the same winners
    /// (<see cref="TeamCommanderRules.HoldsCommandSeat"/>) — a seat is earned one
    /// category at a time: most kills, most gems deposited, most troops delivered.
    /// <para>
    /// Shared by the server snapshot (<c>ShipCommandRoleSnapshot</c>), ship nameplates,
    /// and the minimap so the badge you see is the same player who gets the bonus.
    /// Zero scores never win; ties go to the lowest owner NetworkId.
    /// </para>
    /// </summary>
    public static class TeamCommandRoleRules
    {
        /// <summary>
        /// Extra yield / fire power / troop chunk granted to the living category winner.
        /// 0.05 = +5%. Designer-facing constant — keep the three bonuses identical.
        /// </summary>
        public const float BonusFraction = 0.05f;

        /// <summary>Fire-power multiplier for the team's top killer (1.05).</summary>
        public static float FirePowerMul => 1f + BonusFraction;

        /// <summary>
        /// Scales gun damage for the top killer. Energy cost is left alone so the
        /// bonus is extra punch, not a more expensive shot.
        /// </summary>
        /// <param name="firePower">Authored / mount damage before the command bonus.</param>
        /// <param name="isTopKiller">True when this NetworkId holds the killer title.</param>
        public static float ScaleFirePower(float firePower, bool isTopKiller)
        {
            if (!isTopKiller || firePower <= 0f)
                return firePower;
            return firePower * FirePowerMul;
        }

        /// <summary>
        /// People packed into one load or unload sphere after the troop-commander bonus.
        /// Round-to-nearest so L1×L1 (chunk 1) stays 1 — a ceil would double a +1 hop.
        /// Throughput still rises on larger chunks (L3×L4 → 13 instead of 12).
        /// </summary>
        /// <param name="chunk">Base <c>shipLevel × planetLevel</c> pack size.</param>
        /// <param name="isTopTransporter">True when this NetworkId holds the troop title.</param>
        public static int ScaleTransferChunk(int chunk, bool isTopTransporter)
        {
            int baseChunk = math.max(1, chunk);
            if (!isTopTransporter)
                return baseChunk;

            int scaled = (int)math.round(baseChunk * (1f + BonusFraction));
            return math.max(1, scaled);
        }

        /// <summary>
        /// Extra gem value the top miner Instantiates as its own blue crystals.
        /// Separate from the yellow triangle bonus — callers spawn this as a third burst.
        /// </summary>
        /// <param name="baseValue">Mined chip or destroy leftover (red gems), not the yellow extra.</param>
        /// <param name="isTopMiner">True when this NetworkId holds the miner title.</param>
        public static float GemBonusValue(float baseValue, bool isTopMiner)
        {
            if (!isTopMiner || baseValue <= 0f)
                return 0f;
            return baseValue * BonusFraction;
        }

        /// <summary>
        /// Higher score wins a category. Equal score → lower NetworkId (stable, no flicker).
        /// Same rule as nameplates and the minimap role dots.
        /// </summary>
        /// <param name="candidateScore">This ship's kills / gems / people.</param>
        /// <param name="candidateId">This ship's owner NetworkId.</param>
        /// <param name="currentScore">Best score already recorded for the team.</param>
        /// <param name="currentId">Winner NetworkId already recorded (0 = none).</param>
        public static bool IsBetterTop(int candidateScore, int candidateId, int currentScore, int currentId)
        {
            if (currentId <= 0)
                return candidateScore > 0;
            if (candidateScore > currentScore)
                return true;
            if (candidateScore < currentScore)
                return false;
            return candidateId < currentId;
        }
    }
}
