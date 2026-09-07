using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Pure cargo + ComponentSize → mobility tax shared by drive (live each tick), HUD, and tests.
    /// <para>
    /// [TITAN-ORBIT] One mass, then subtract:
    /// <c>totalMass = gems×MassPerGem + people×MassPerPerson + componentSize×MassPerComponentSize</c>
    /// <c>stat' = max(floor, untaxed − totalMass × WeightPerMass)</c>.
    /// Motor stores <b>untaxed</b> baselines; drive/HUD apply this live so collecting cargo updates
    /// Speed / Accel / Turn immediately. Burst overloads take plain floats (no ScriptableObject).
    /// </para>
    /// </summary>
    public static class ShipMobilityResolution
    {
        /// <summary>
        /// Result of subtractive mass tax on untaxed motor inputs.
        /// Units: MaxSpeed (world units/s), EngineThrust (= acceleration), RotationSpeed (°/s).
        /// </summary>
        public struct TaxedMotorStats
        {
            /// <summary>Top speed after mass tax.</summary>
            public float MaxSpeed;

            /// <summary>Acceleration after mass tax (stored on motor as EngineThrust).</summary>
            public float EngineThrust;

            /// <summary>Yaw rate in degrees per second after mass tax.</summary>
            public float RotationSpeed;

            /// <summary>totalMass used for this tax (for HUD breakdowns).</summary>
            public float TotalMass;
        }

        /// <summary>
        /// Builds totalMass from current gems/people and ComponentSize using cached settings.
        /// </summary>
        public static float ComputeTotalMass(float gems, float people, float componentSize)
        {
            return ComputeTotalMass(gems, people, componentSize, ShipCargoMobilitySettingsCache.ResolveOrDefault());
        }

        /// <summary>
        /// Builds totalMass with an explicit settings instance (tests / editor / HUD).
        /// </summary>
        public static float ComputeTotalMass(
            float gems,
            float people,
            float componentSize,
            ShipCargoMobilitySettings settings)
        {
            if (settings == null)
            {
                return ComputeTotalMassBurst(
                    gems, people, componentSize,
                    massPerGem: 0.01f,
                    massPerPerson: 0.15f,
                    massPerComponentSize: 1f);
            }

            return ComputeTotalMassBurst(
                gems,
                people,
                componentSize,
                settings.massPerGem,
                settings.massPerPerson,
                settings.massPerComponentSize);
        }

        /// <summary>
        /// [ECS/DOTS] Burst-safe totalMass — same formula as the managed overload.
        /// </summary>
        public static float ComputeTotalMassBurst(
            float gems,
            float people,
            float componentSize,
            float massPerGem,
            float massPerPerson,
            float massPerComponentSize)
        {
            float g = math.max(0f, gems);
            float p = math.max(0f, people);
            float size = math.max(0f, componentSize);
            return g * math.max(0f, massPerGem)
                   + p * math.max(0f, massPerPerson)
                   + size * math.max(0f, massPerComponentSize);
        }

        /// <summary>
        /// Applies subtractive mass tax using cached settings.
        /// </summary>
        public static TaxedMotorStats ApplyMassTax(
            float untaxedMaxSpeed,
            float untaxedAccel,
            float untaxedRotationSpeedDeg,
            float totalMass)
        {
            return ApplyMassTax(
                untaxedMaxSpeed,
                untaxedAccel,
                untaxedRotationSpeedDeg,
                totalMass,
                ShipCargoMobilitySettingsCache.ResolveOrDefault());
        }

        /// <summary>
        /// Applies subtractive mass tax with an explicit settings instance.
        /// </summary>
        public static TaxedMotorStats ApplyMassTax(
            float untaxedMaxSpeed,
            float untaxedAccel,
            float untaxedRotationSpeedDeg,
            float totalMass,
            ShipCargoMobilitySettings settings)
        {
            if (settings == null)
            {
                return ApplyMassTaxBurst(
                    untaxedMaxSpeed,
                    untaxedAccel,
                    untaxedRotationSpeedDeg,
                    totalMass,
                    speedWeightPerMass: 0.1f,
                    accelWeightPerMass: 0.1f,
                    turnWeightPerMass: 0.5f,
                    minSpeed: 0.1f,
                    minAccel: 0.1f,
                    minTurn: 1f);
            }

            return ApplyMassTaxBurst(
                untaxedMaxSpeed,
                untaxedAccel,
                untaxedRotationSpeedDeg,
                totalMass,
                settings.speedWeightPerMass,
                settings.accelWeightPerMass,
                settings.turnWeightPerMass,
                settings.minSpeed,
                settings.minAccel,
                settings.minTurn);
        }

        /// <summary>
        /// Live motor numbers for drive / HUD. MEGAs pass <paramref name="skipMassTax"/> and
        /// keep chassis speed / accel / turn with totalMass 0.
        /// </summary>
        public static TaxedMotorStats ResolveLiveMotorStats(
            float untaxedMaxSpeed,
            float untaxedAccel,
            float untaxedRotationSpeedDeg,
            float gems,
            float people,
            float componentSize,
            bool skipMassTax,
            ShipCargoMobilitySettings settings = null)
        {
            if (skipMassTax)
            {
                return new TaxedMotorStats
                {
                    MaxSpeed = math.max(0f, untaxedMaxSpeed),
                    EngineThrust = math.max(0f, untaxedAccel),
                    RotationSpeed = math.max(0f, untaxedRotationSpeedDeg),
                    TotalMass = 0f,
                };
            }

            return ApplyMassTaxFromCargo(
                untaxedMaxSpeed,
                untaxedAccel,
                untaxedRotationSpeedDeg,
                gems,
                people,
                componentSize,
                settings);
        }

        /// <summary>
        /// Convenience: compute totalMass then tax (managed / HUD).
        /// </summary>
        public static TaxedMotorStats ApplyMassTaxFromCargo(
            float untaxedMaxSpeed,
            float untaxedAccel,
            float untaxedRotationSpeedDeg,
            float gems,
            float people,
            float componentSize,
            ShipCargoMobilitySettings settings = null)
        {
            settings ??= ShipCargoMobilitySettingsCache.ResolveOrDefault();
            float totalMass = ComputeTotalMass(gems, people, componentSize, settings);
            TaxedMotorStats taxed = ApplyMassTax(
                untaxedMaxSpeed, untaxedAccel, untaxedRotationSpeedDeg, totalMass, settings);
            taxed.TotalMass = totalMass;
            return taxed;
        }

        /// <summary>
        /// [ECS/DOTS] Burst-safe subtractive tax — same formula as the managed overload.
        /// </summary>
        public static TaxedMotorStats ApplyMassTaxBurst(
            float untaxedMaxSpeed,
            float untaxedAccel,
            float untaxedRotationSpeedDeg,
            float totalMass,
            float speedWeightPerMass,
            float accelWeightPerMass,
            float turnWeightPerMass,
            float minSpeed,
            float minAccel,
            float minTurn)
        {
            float mass = math.max(0f, totalMass);
            float speedDrag = mass * math.max(0f, speedWeightPerMass);
            float accelDrag = mass * math.max(0f, accelWeightPerMass);
            float turnDrag = mass * math.max(0f, turnWeightPerMass);

            return new TaxedMotorStats
            {
                MaxSpeed = math.max(minSpeed, untaxedMaxSpeed - speedDrag),
                EngineThrust = math.max(minAccel, untaxedAccel - accelDrag),
                RotationSpeed = math.max(minTurn, untaxedRotationSpeedDeg - turnDrag),
                TotalMass = mass,
            };
        }
    }
}
