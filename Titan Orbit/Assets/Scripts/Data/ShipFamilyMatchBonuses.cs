using System;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Match-scoped family multipliers. A new game rolls these once from the map seed.
    /// Home-planet ships stay at exactly 1×. Every other planet's family keeps the
    /// multipliers authored on its asset, then replaces a random handful of them —
    /// uniformly 3, 4, 5, or 6 different fields — with a bell-curve roll in [0.5, 2.0]
    /// (50% to 200% of the stat). Every field, including ones this match did not
    /// roll, is clamped into that same band so an authored ×5 cannot show as +400%.
    /// Zero / unset still plays as 1×. Most rolls land around 1×; the tails reach
    /// half and twice.
    /// <para>
    /// With no match seed, combat and the fleet-bonus UI read the asset as-is.
    /// Once a seed is published they read <see cref="Resolve"/>, which mixes that
    /// asset with this match's subset roll. Planets that share a family share that
    /// family's roll. Server and client both hash <c>matchSeed + family index</c>,
    /// so nothing extra is replicated.
    /// </para>
    /// </summary>
    public static class ShipFamilyMatchBonuses
    {
        /// <summary>Lowest fleet multiplier (50% of the stat, half).</summary>
        public const float MinMultiplier = 0.5f;

        /// <summary>Highest fleet multiplier (200% of the stat, twice).</summary>
        public const float MaxMultiplier = 2f;

        /// <summary>Fewest fleet-bonus fields a non-home family randomizes in one match.</summary>
        public const int MinRolledFieldCount = 3;

        /// <summary>Most fleet-bonus fields a non-home family randomizes in one match.</summary>
        public const int MaxRolledFieldCount = 6;

        /// <summary>
        /// How many multipliers <see cref="ShipFamilySpecialBonuses"/> has.
        /// Index order matches <see cref="WriteRolledField"/> and the old full-roll
        /// assign order. Do not insert a field in the middle of that switch.
        /// </summary>
        const int BonusFieldCount = 19;

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
                " — home ships stay 1×; other families keep authored bonuses and roll " +
                MinRolledFieldCount + "–" + MaxRolledFieldCount +
                " of them on a bell curve (tails " +
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
        /// Other slots keep <c>specialBonuses</c> and overwrite
        /// <see cref="MinRolledFieldCount"/>–<see cref="MaxRolledFieldCount"/> fields
        /// with the deterministic roll for <see cref="ActiveMatchSeed"/>.
        /// With no match, this returns the asset's authored <c>specialBonuses</c>
        /// so the inspector and power-bar bake stay stable.
        /// </summary>
        public static ShipFamilySpecialBonuses Resolve(ShipFamilyDefinition family)
        {
            if (family == null)
                return ShipFamilySpecialBonuses.Identity;

            if (s_MatchSeed == 0)
                return ClampToRollRange(family.specialBonuses);

            int index = FindConfigIndex(family);
            if (index < 0)
                index = FallbackNonHomeSlot(family);

            // --- Home worlds ---
            // [TITAN-ORBIT] Starter ships on every team home are exactly 1×, even if
            // the Astro Eagle asset is later given a trade-off.
            if (index == PlanetShipFamilyAssignment.HomeFamilyConfigIndex)
                return ShipFamilySpecialBonuses.Identity;

            return Roll(s_MatchSeed, index, family.specialBonuses);
        }

        /// <summary>
        /// One family's multipliers for a known config-list index.
        /// Index 0 is home (all 1s). A zero seed returns identity so callers
        /// do not invent a roll before the map exists. When the catalog has no
        /// asset for this slot, unrolled fields stay at 1×.
        /// </summary>
        public static ShipFamilySpecialBonuses ResolveIndex(int familyConfigIndex)
        {
            if (s_MatchSeed == 0 || familyConfigIndex <= PlanetShipFamilyAssignment.HomeFamilyConfigIndex)
                return ShipFamilySpecialBonuses.Identity;

            return Roll(s_MatchSeed, familyConfigIndex);
        }

        /// <summary>
        /// Deterministic bonuses for one non-home family slot, starting from 1×.
        /// The other overload keeps a family's authored multipliers on the fields
        /// this match does not roll.
        /// </summary>
        public static ShipFamilySpecialBonuses Roll(uint matchSeed, int familyConfigIndex)
        {
            return Roll(matchSeed, familyConfigIndex, ShipFamilySpecialBonuses.Identity);
        }

        /// <summary>
        /// Deterministic bonuses for one non-home family slot.
        /// Copies <paramref name="baseline"/> (the family's authored multipliers),
        /// then replaces a uniform count in
        /// [<see cref="MinRolledFieldCount"/>, <see cref="MaxRolledFieldCount"/>]
        /// distinct fields with a thousandth in [0.5, 2.0] from the bell curve
        /// (<see cref="NextMul"/>). Every other field is left on the baseline.
        /// Which fields, and the order <see cref="NextMul"/> is drawn, are part of
        /// the seed contract — do not reorder <see cref="WriteRolledField"/>.
        /// </summary>
        public static ShipFamilySpecialBonuses Roll(
            uint matchSeed,
            int familyConfigIndex,
            in ShipFamilySpecialBonuses baseline)
        {
            if (matchSeed == 0 || familyConfigIndex <= PlanetShipFamilyAssignment.HomeFamilyConfigIndex)
                return ShipFamilySpecialBonuses.Identity;

            // Nearby family indices (1, 2, 3) must not cluster on the same few values.
            uint indexSalt = unchecked((uint)familyConfigIndex * 2654435761u);
            var rng = Random.CreateFromIndex(matchSeed ^ indexSalt ^ StreamSalt);

            // --- How many, then which ---
            // [TITAN-ORBIT] Count is inclusive on both ends. NextInt's upper bound
            // is exclusive, so +1 reaches MaxRolledFieldCount.
            int count = rng.NextInt(MinRolledFieldCount, MaxRolledFieldCount + 1);
            Span<int> fields = stackalloc int[BonusFieldCount];
            for (int i = 0; i < BonusFieldCount; i++)
                fields[i] = i;

            // Partial Fisher–Yates. The first `count` slots are a uniform subset,
            // and NextMul runs in that shuffled order so the same seed always
            // paints the same stats.
            for (int i = 0; i < count; i++)
            {
                int j = i + rng.NextInt(BonusFieldCount - i);
                int swap = fields[i];
                fields[i] = fields[j];
                fields[j] = swap;
            }

            ShipFamilySpecialBonuses bonuses = baseline;
            for (int i = 0; i < count; i++)
                WriteRolledField(ref bonuses, fields[i], NextMul(ref rng));

            // Rolled fields already land in [0.5, 2]. Authored leftovers (a ×5
            // that the wheel prints as +400%) are pulled into that same band.
            return ClampToRollRange(bonuses);
        }

        /// <summary>
        /// Every fleet multiplier in [0.5, 2]: 50% to 200% of the stat.
        /// Zero or unset stays 1× so a fresh asset does not collapse to half.
        /// </summary>
        static ShipFamilySpecialBonuses ClampToRollRange(in ShipFamilySpecialBonuses bonuses)
        {
            ShipFamilySpecialBonuses copy = bonuses;
            copy.moveSpeedMul = ClampFleetMultiplier(bonuses.moveSpeedMul);
            copy.accelerationMul = ClampFleetMultiplier(bonuses.accelerationMul);
            copy.turnSpeedMul = ClampFleetMultiplier(bonuses.turnSpeedMul);
            copy.firePowerMul = ClampFleetMultiplier(bonuses.firePowerMul);
            copy.fireRateMul = ClampFleetMultiplier(bonuses.fireRateMul);
            copy.bulletSpeedMul = ClampFleetMultiplier(bonuses.bulletSpeedMul);
            copy.bulletRangeMul = ClampFleetMultiplier(bonuses.bulletRangeMul);
            copy.rammingMul = ClampFleetMultiplier(bonuses.rammingMul);
            copy.healthCapMul = ClampFleetMultiplier(bonuses.healthCapMul);
            copy.healthRegenMul = ClampFleetMultiplier(bonuses.healthRegenMul);
            copy.energyCapMul = ClampFleetMultiplier(bonuses.energyCapMul);
            copy.energyRegenMul = ClampFleetMultiplier(bonuses.energyRegenMul);
            copy.extraSpeedPercentMul = ClampFleetMultiplier(bonuses.extraSpeedPercentMul);
            copy.extraSpeedEnergyDrainMul = ClampFleetMultiplier(bonuses.extraSpeedEnergyDrainMul);
            copy.maxGemsMul = ClampFleetMultiplier(bonuses.maxGemsMul);
            copy.maxPeopleMul = ClampFleetMultiplier(bonuses.maxPeopleMul);
            copy.tractorDistanceMul = ClampFleetMultiplier(bonuses.tractorDistanceMul);
            copy.tractorPowerMul = ClampFleetMultiplier(bonuses.tractorPowerMul);
            copy.cameraHeightMul = ClampFleetMultiplier(bonuses.cameraHeightMul);
            return copy;
        }

        /// <summary>One multiplier into the shared 50%–200% band. Unset stays 1.</summary>
        static float ClampFleetMultiplier(float mul)
        {
            float m = mul > 0.0001f ? mul : 1f;
            return math.clamp(m, MinMultiplier, MaxMultiplier);
        }

        /// <summary>
        /// Writes one rolled multiplier. Case numbers are the fleet-bonus field
        /// order (move speed first, camera height last). Inserting a case in the
        /// middle would move every later stat onto a different roll.
        /// </summary>
        static void WriteRolledField(ref ShipFamilySpecialBonuses bonuses, int field, float mul)
        {
            switch (field)
            {
                case 0: bonuses.moveSpeedMul = mul; break;
                case 1: bonuses.accelerationMul = mul; break;
                case 2: bonuses.turnSpeedMul = mul; break;
                case 3: bonuses.firePowerMul = mul; break;
                case 4: bonuses.fireRateMul = mul; break;
                case 5: bonuses.bulletSpeedMul = mul; break;
                case 6: bonuses.bulletRangeMul = mul; break;
                case 7: bonuses.rammingMul = mul; break;
                case 8: bonuses.healthCapMul = mul; break;
                case 9: bonuses.healthRegenMul = mul; break;
                case 10: bonuses.energyCapMul = mul; break;
                case 11: bonuses.energyRegenMul = mul; break;
                case 12: bonuses.extraSpeedPercentMul = mul; break;
                case 13: bonuses.extraSpeedEnergyDrainMul = mul; break;
                case 14: bonuses.maxGemsMul = mul; break;
                case 15: bonuses.maxPeopleMul = mul; break;
                case 16: bonuses.tractorDistanceMul = mul; break;
                case 17: bonuses.tractorPowerMul = mul; break;
                case 18: bonuses.cameraHeightMul = mul; break;
            }
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
            int minThousandths = (int)math.round(MinMultiplier * 1000f);
            int maxThousandths = (int)math.round(MaxMultiplier * 1000f);
            thousandths = math.clamp(thousandths, minThousandths, maxThousandths);
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
