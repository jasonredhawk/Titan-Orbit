using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Pure cargo → mobility tax math shared by <see cref="ECS.ShipStatApplyLogic"/> (capacity →
    /// <c>ShipMotorConfig</c>), the Burst drive job (current load → MaxSpeed / turn each tick),
    /// and HUD previews. Capacity path may use managed settings; load multipliers are Burst-safe
    /// float math so server and client prediction stay matched.
    /// <para>
    /// [TITAN-ORBIT] Two layers:
    /// (1) Capacity tax — GemCapacity / PeopleCapacity at stat apply (empty-hold identity).
    /// (2) Current-load tax — CurrentGems / CurrentPeople each motor tick on MaxSpeed and turn
    /// (accel already slows via mass F/m when you pick up cargo).
    /// Same weight fields drive both layers. Formula:
    /// <c>value' = value × max(minMultiplier, 1 / (1 + penalty))</c>.
    /// </para>
    /// </summary>
    public static class ShipMobilityResolution
    {
        /// <summary>
        /// Result of applying the capacity tax to one chassis's untaxed motor inputs.
        /// Units match what <c>ShipStatApplyLogic</c> writes: move speed definition units,
        /// EngineThrust (already × visibility), RotationSpeed in °/s.
        /// </summary>
        public struct TaxedMotorStats
        {
            /// <summary>Top speed after capacity tax (same units as untaxed MaxSpeed).</summary>
            public float MaxSpeed;

            /// <summary>Engine thrust / acceleration force after capacity tax.</summary>
            public float EngineThrust;

            /// <summary>Yaw rate in degrees per second after capacity tax.</summary>
            public float RotationSpeed;

            /// <summary>Multiplier applied to MaxSpeed (1 = no tax, floor = settings min).</summary>
            public float SpeedMultiplier;

            /// <summary>Multiplier applied to EngineThrust.</summary>
            public float AccelMultiplier;

            /// <summary>Multiplier applied to RotationSpeed.</summary>
            public float TurnMultiplier;
        }

        /// <summary>
        /// Burst-safe MaxSpeed / turn multipliers from cargo currently aboard.
        /// Accel is intentionally omitted — current load already slows ramp via movement mass.
        /// </summary>
        public struct CurrentLoadMultipliers
        {
            /// <summary>Multiply capacity-taxed MaxSpeed by this (1 when empty).</summary>
            public float SpeedMultiplier;

            /// <summary>Multiply capacity-taxed RotationSpeed by this (1 when empty).</summary>
            public float TurnMultiplier;
        }

        /// <summary>
        /// Applies capacity tax using the cached settings asset (or code defaults).
        /// Call after propulsion aggregation, level mobility scale, and attribute multipliers.
        /// </summary>
        public static TaxedMotorStats ApplyCapacityTax(
            float untaxedMaxSpeed,
            float untaxedEngineThrust,
            float untaxedRotationSpeedDeg,
            float gemCapacity,
            float peopleCapacity)
        {
            return ApplyCapacityTax(
                untaxedMaxSpeed,
                untaxedEngineThrust,
                untaxedRotationSpeedDeg,
                gemCapacity,
                peopleCapacity,
                ShipCargoMobilitySettingsCache.ResolveOrDefault());
        }

        /// <summary>
        /// Applies capacity tax with an explicit settings instance (tests / editor previews).
        /// </summary>
        public static TaxedMotorStats ApplyCapacityTax(
            float untaxedMaxSpeed,
            float untaxedEngineThrust,
            float untaxedRotationSpeedDeg,
            float gemCapacity,
            float peopleCapacity,
            ShipCargoMobilitySettings settings)
        {
            // --- Guard ---
            if (settings == null)
            {
                return new TaxedMotorStats
                {
                    MaxSpeed = Mathf.Max(0.1f, untaxedMaxSpeed),
                    EngineThrust = Mathf.Max(0.1f, untaxedEngineThrust),
                    RotationSpeed = Mathf.Max(1f, untaxedRotationSpeedDeg),
                    SpeedMultiplier = 1f,
                    AccelMultiplier = 1f,
                    TurnMultiplier = 1f,
                };
            }

            float gems = Mathf.Max(0f, gemCapacity);
            float people = Mathf.Max(0f, peopleCapacity);

            // --- Capacity penalties (ALWAYS MaxSpeed + accel + turn) ---
            float speedPenalty = gems * settings.speedWeightPerGem
                                 + people * settings.speedWeightPerPerson;
            float accelPenalty = gems * settings.accelWeightPerGem
                                 + people * settings.accelWeightPerPerson;
            float turnPenalty = gems * settings.turnWeightPerGem
                                + people * settings.turnWeightPerPerson;

            float speedMul = MultiplierFromPenalty(speedPenalty, settings.minSpeedMultiplier);
            float accelMul = MultiplierFromPenalty(accelPenalty, settings.minAccelMultiplier);
            float turnMul = MultiplierFromPenalty(turnPenalty, settings.minTurnMultiplier);

            return new TaxedMotorStats
            {
                MaxSpeed = Mathf.Max(0.1f, untaxedMaxSpeed * speedMul),
                EngineThrust = Mathf.Max(0.1f, untaxedEngineThrust * accelMul),
                RotationSpeed = Mathf.Max(1f, untaxedRotationSpeedDeg * turnMul),
                SpeedMultiplier = speedMul,
                AccelMultiplier = accelMul,
                TurnMultiplier = turnMul,
            };
        }

        /// <summary>
        /// Current-load MaxSpeed / turn multipliers from cached settings (HUD / main thread).
        /// </summary>
        public static CurrentLoadMultipliers ApplyCurrentLoadTax(float currentGems, float currentPeople)
        {
            return ApplyCurrentLoadTax(
                currentGems,
                currentPeople,
                ShipCargoMobilitySettingsCache.ResolveOrDefault());
        }

        /// <summary>
        /// Current-load MaxSpeed / turn multipliers with explicit settings (HUD / tests).
        /// </summary>
        public static CurrentLoadMultipliers ApplyCurrentLoadTax(
            float currentGems,
            float currentPeople,
            ShipCargoMobilitySettings settings)
        {
            if (settings == null)
            {
                return new CurrentLoadMultipliers
                {
                    SpeedMultiplier = 1f,
                    TurnMultiplier = 1f,
                };
            }

            return ComputeCurrentLoadMultipliers(
                currentGems,
                currentPeople,
                settings.speedWeightPerGem,
                settings.speedWeightPerPerson,
                settings.turnWeightPerGem,
                settings.turnWeightPerPerson,
                settings.minSpeedMultiplier,
                settings.minTurnMultiplier);
        }

        /// <summary>
        /// [ECS/DOTS] Burst-safe current-load multipliers — same formula as capacity MaxSpeed/turn
        /// tax, but using CurrentGems / CurrentPeople. Called from the drive job each tick.
        /// </summary>
        public static CurrentLoadMultipliers ComputeCurrentLoadMultipliers(
            float currentGems,
            float currentPeople,
            float speedWeightPerGem,
            float speedWeightPerPerson,
            float turnWeightPerGem,
            float turnWeightPerPerson,
            float minSpeedMultiplier,
            float minTurnMultiplier)
        {
            float gems = math.max(0f, currentGems);
            float people = math.max(0f, currentPeople);

            float speedPenalty = gems * speedWeightPerGem + people * speedWeightPerPerson;
            float turnPenalty = gems * turnWeightPerGem + people * turnWeightPerPerson;

            return new CurrentLoadMultipliers
            {
                SpeedMultiplier = MultiplierFromPenaltyBurst(speedPenalty, minSpeedMultiplier),
                TurnMultiplier = MultiplierFromPenaltyBurst(turnPenalty, minTurnMultiplier),
            };
        }

        /// <summary>
        /// Converts a non-negative penalty into a multiplier: <c>1 / (1 + penalty)</c>, floored.
        /// Zero penalty → 1 (no change). Large holds asymptote toward <paramref name="minMultiplier"/>.
        /// </summary>
        public static float MultiplierFromPenalty(float penalty, float minMultiplier)
        {
            float p = Mathf.Max(0f, penalty);
            float mul = 1f / (1f + p);
            return Mathf.Max(minMultiplier, mul);
        }

        /// <summary>Burst-safe sibling of <see cref="MultiplierFromPenalty"/>.</summary>
        public static float MultiplierFromPenaltyBurst(float penalty, float minMultiplier)
        {
            float p = math.max(0f, penalty);
            float mul = 1f / (1f + p);
            return math.max(minMultiplier, mul);
        }
    }
}
