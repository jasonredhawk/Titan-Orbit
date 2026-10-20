using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Match-scoped family multipliers. A new game rolls these once from the map seed.
    /// Home-planet ships stay at exactly 1×. Every other planet's family slot gets its
    /// own bell-curve roll in [0.5, 2.0]: most stats land around 1×, and the rare
    /// tails reach half and double.
    /// <para>
    /// The numbers on <see cref="ShipFamilyDefinition.specialBonuses"/> are the editor
    /// preview. Once a match seed is published, combat, motors, and the fleet-bonus UI
    /// read <see cref="Resolve"/> instead, so a family is not locked to the asset.
    /// Planets that roll the same family share that family's roll for the match.
    /// Server and client both hash <c>matchSeed + family index</c>, so nothing extra
    /// is replicated.
    /// </para>
    /// </summary>
    public static class ShipFamilyMatchBonuses
    {
        /// <summary>Lowest multiplier a non-home family can roll (half).</summary>
        public const float MinMultiplier = 0.5f;

        /// <summary>Highest multiplier a non-home family can roll (double).</summary>
        public const float MaxMultiplier = 2f;

        /// <summary>
        /// Salt mixed into the family-bonus stream so it does not reuse the planet
        /// placement or bullet-bank random sequence.
        /// </summary>
        const uint StreamSalt = 0xF4A11B05u;

        /// <summary>
        /// Width of the bell, in natural-log units. Half (0.5) and double (2) are
        /// the same factor away from 1, and <c>ln(2) / 3</c> places both limits
        /// three standard deviations out. About two-thirds of rolls then sit
        /// between roughly 0.79× and 1.26×. Anything past the limits is clamped.
        /// </summary>
        const float LogSigma = 0.23104906f;

        /// <summary>Map seed for the live match. Zero means "no match yet" (editor / menu).</summary>
        static uint s_MatchSeed;

        /// <summary>Seed published for the current match, or 0 before the map exists.</summary>
        public static uint ActiveMatchSeed => s_MatchSeed;

        /// <summary>True after map generation (or the client recipe) has published a seed.</summary>
        public static bool HasMatch => s_MatchSeed != 0;

        /// <summary>
        /// Latches the match seed and drops cached power-bar sums so the next read
        /// uses this game's rolls. Same seed again is a no-op (recipe retries).
        /// </summary>
        /// <param name="matchSeed">
        /// <c>MapStateSingleton.BlueprintSeed</c> as a uint. Zero is ignored.
        /// </param>
        public static void PublishMatchSeed(uint matchSeed)
        {
            if (matchSeed == 0 || matchSeed == s_MatchSeed)
                return;

            s_MatchSeed = matchSeed;
            // Live bars were summed with the previous seed (or the asset). Drop them once.
            ShipFamilyPowerBarNorm.InvalidateCache();
            Debug.Log(
                "[ShipFamilyMatchBonuses] Match seed " + matchSeed +
                " — home ships stay 1×, other families use a bell curve around 1× (tails " +
                MinMultiplier.ToString("0.0") + "–" + MaxMultiplier.ToString("0.0") + ").");
        }

        /// <summary>
        /// Drops the match seed (disconnect / leaving Play Mode). The next game
        /// publishes a new one. Editor tools fall back to the asset multipliers.
        /// </summary>
        public static void ClearMatch()
        {
            if (s_MatchSeed == 0)
                return;

            s_MatchSeed = 0;
            ShipFamilyPowerBarNorm.InvalidateCache();
        }

        /// <summary>
        /// Bonuses for one family right now.
        /// Home slot (config index 0) is always identity during a match.
        /// Other slots are the deterministic roll for <see cref="ActiveMatchSeed"/>.
        /// With no match, this returns the asset's authored <c>specialBonuses</c>
        /// so the inspector and power-bar bake stay stable.
        /// </summary>
        public static ShipFamilySpecialBonuses Resolve(ShipFamilyDefinition family)
        {
            if (family == null)
                return ShipFamilySpecialBonuses.Identity;

            if (s_MatchSeed == 0)
                return family.specialBonuses;

            int index = FindConfigIndex(family);
            if (index < 0)
                index = FallbackNonHomeSlot(family);

            // --- Home worlds ---
            // [TITAN-ORBIT] Starter ships on every team home are exactly 1×, even if
            // the Astro Eagle asset is later given a trade-off.
            if (index == PlanetShipFamilyAssignment.HomeFamilyConfigIndex)
                return ShipFamilySpecialBonuses.Identity;

            return Roll(s_MatchSeed, index);
        }

        /// <summary>
        /// One family's multipliers for a known config-list index.
        /// Index 0 is home (all 1s). A zero seed returns identity so callers
        /// do not invent a roll before the map exists.
        /// </summary>
        public static ShipFamilySpecialBonuses ResolveIndex(int familyConfigIndex)
        {
            if (s_MatchSeed == 0 || familyConfigIndex <= PlanetShipFamilyAssignment.HomeFamilyConfigIndex)
                return ShipFamilySpecialBonuses.Identity;

            return Roll(s_MatchSeed, familyConfigIndex);
        }

        /// <summary>
        /// Deterministic bonuses for one non-home family slot.
        /// Each multiplier is a thousandth in [0.5, 2.0], drawn from a bell
        /// curve centered on 1× (<see cref="NextMul"/>). Field order is part of
        /// the seed contract — do not reorder the assigns.
        /// </summary>
        public static ShipFamilySpecialBonuses Roll(uint matchSeed, int familyConfigIndex)
        {
            if (matchSeed == 0 || familyConfigIndex <= PlanetShipFamilyAssignment.HomeFamilyConfigIndex)
                return ShipFamilySpecialBonuses.Identity;

            // Nearby family indices (1, 2, 3) must not cluster on the same few values.
            uint indexSalt = unchecked((uint)familyConfigIndex * 2654435761u);
            var rng = Random.CreateFromIndex(matchSeed ^ indexSalt ^ StreamSalt);
            return new ShipFamilySpecialBonuses
            {
                moveSpeedMul = NextMul(ref rng),
                accelerationMul = NextMul(ref rng),
                turnSpeedMul = NextMul(ref rng),
                firePowerMul = NextMul(ref rng),
                fireRateMul = NextMul(ref rng),
                bulletSpeedMul = NextMul(ref rng),
                bulletRangeMul = NextMul(ref rng),
                rammingMul = NextMul(ref rng),
                healthCapMul = NextMul(ref rng),
                healthRegenMul = NextMul(ref rng),
                energyCapMul = NextMul(ref rng),
                energyRegenMul = NextMul(ref rng),
                extraSpeedPercentMul = NextMul(ref rng),
                extraSpeedEnergyDrainMul = NextMul(ref rng),
                maxGemsMul = NextMul(ref rng),
                maxPeopleMul = NextMul(ref rng),
                tractorDistanceMul = NextMul(ref rng),
                tractorPowerMul = NextMul(ref rng),
                cameraHeightMul = NextMul(ref rng),
            };
        }

        /// <summary>
        /// One fleet multiplier for the current match.
        /// A flat <c>NextInt</c> across [0.5, 2] made a 2× roll as common as 1×
        /// and pulled the average up to 1.25. This draws a bell instead: the
        /// center (median) is 1×, and the low and high limits are the tails.
        /// </summary>
        /// <returns>A thousandth in [<see cref="MinMultiplier"/>, <see cref="MaxMultiplier"/>].</returns>
        static float NextMul(ref Random rng)
        {
            // --- Bell in "times" space ---
            // [STANDARD] Box-Muller turns two uniform draws into one normal sample
            // (mean 0, standard deviation 1). Two draws per stat keep the server
            // and the client on the same sequence.
            // [TITAN-ORBIT] Exponentiating that sample makes a bell whose median
            // is exactly 1×. Because ln(0.5) = -ln(2), three standard deviations
            // land on half and on double together.
            float u1 = math.max(rng.NextFloat(), 1e-6f);
            float u2 = rng.NextFloat();
            float z = math.sqrt(-2f * math.log(u1)) * math.cos((2f * math.PI) * u2);
            float mul = math.exp(z * LogSigma);

            // --- Hard limits ---
            // A normal curve never truly ends. Clamp the far tails so a roll
            // cannot leave the 0.5–2 band the rest of the game already assumes.
            mul = math.clamp(mul, MinMultiplier, MaxMultiplier);

            // --- Thousandths ---
            // Same discrete grid the old flat roll used, so the HUD percent and
            // the server/client hash cannot drift by a float crumb.
            int thousandths = (int)math.round(mul * 1000f);
            thousandths = math.clamp(thousandths, 500, 2000);
            return thousandths / 1000f;
        }

        /// <summary>
        /// Config-list index for this family asset (0 = home). -1 when the catalog
        /// does not list it.
        /// </summary>
        static int FindConfigIndex(ShipFamilyDefinition family)
        {
            PlanetShipFamilyConfig config = PlanetShipFamilyConfig.LoadDefault();
            if (config?.families == null || family == null)
                return -1;

            for (int i = 0; i < config.families.Count; i++)
            {
                ShipFamilyDefinition listed = config.families[i]?.shipFamilyDefinition;
                if (listed == family)
                    return i;
            }

            if (string.IsNullOrWhiteSpace(family.familyId))
                return -1;

            string want = family.familyId.Trim();
            for (int i = 0; i < config.families.Count; i++)
            {
                ShipFamilyDefinition listed = config.families[i]?.shipFamilyDefinition;
                if (listed == null || string.IsNullOrWhiteSpace(listed.familyId))
                    continue;
                if (string.Equals(listed.familyId.Trim(), want, System.StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Stable non-home slot when a family is missing from the planet catalog.
        /// Never returns 0, so an unknown hull does not inherit the home 1× rule
        /// or the asset's old trade-offs.
        /// </summary>
        static int FallbackNonHomeSlot(ShipFamilyDefinition family)
        {
            int hash = 17;
            string id = family != null ? family.familyId : null;
            if (!string.IsNullOrEmpty(id))
            {
                for (int i = 0; i < id.Length; i++)
                    hash = unchecked(hash * 31 + id[i]);
            }

            int span = math.max(1, PlanetShipFamilyAssignment.NonHomeFamilySlotCount);
            int slot = (hash & 0x7fffffff) % span;
            return slot + 1;
        }

#if UNITY_EDITOR
        /// <summary>Play Mode enter with domain reload — do not keep the previous match.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_MatchSeed = 0;
        }
#endif
    }
}
