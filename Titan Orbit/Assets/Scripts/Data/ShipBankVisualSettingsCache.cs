namespace TitanOrbit.Data
{
    /// <summary>
    /// Process-wide cosmetic bank (roll-while-turning) and pitch (accel + collision)
    /// knobs for hybrid ship proxies and Entities Graphics bank pivots. Lives in
    /// <c>TitanOrbit.Data</c> so both <c>TitanOrbit.Game</c> and <c>TitanOrbit.ECS</c>
    /// can read it (ECS cannot reference Game).
    /// Published from <see cref="ShipBankVisualSettings"/> (shared Resources default, or a
    /// family-specific asset). No sim or NetCode impact.
    /// Paired with <c>ShipBankVisualApplier</c> and <c>ShipEntitiesGraphicsBankSystem</c>.
    /// </summary>
    public static class ShipBankVisualSettingsCache
    {
        static ShipBankVisualSettings _active;

        /// <summary>
        /// Roll degrees at full turn when sensitivity is 1. Reads the published asset when set.
        /// </summary>
        public static float MaxBankAngleDegrees =>
            _active != null
                ? _active.ClampedMaxBankAngleDegrees
                : ShipPropulsionAggregation.VisualBankReferenceMaxAngleDegrees;

        /// <summary>
        /// How quickly bank builds for a given yaw rate. 1 = linear with turn fraction;
        /// higher reaches max bank at lower turn rates (feels more sensitive).
        /// </summary>
        public static float BankSensitivity =>
            _active != null ? _active.ClampedBankSensitivity : 1.35f;

        /// <summary>
        /// Exponential catch-up rate for yaw-rate sampling and roll angle (higher = snappier).
        /// </summary>
        public static float BankSmoothing =>
            _active != null ? _active.ClampedBankSmoothing : 8f;

        /// <summary>
        /// Yaw rate (°/s) treated as “full turn” for the bank curve.
        /// Authored on the published asset, else the fleet global max.
        /// </summary>
        public static float ReferenceTurnDegreesPerSecond =>
            _active != null
                ? _active.ResolveReferenceTurnDegreesPerSecond()
                : ShipPropulsionAggregation.GetGlobalMaxTurnSpeedDegreesPerSecond();

        /// <summary>Peak nose-down pitch (°). Reads the published asset when set.</summary>
        public static float MaxPitchDownDegrees =>
            _active != null
                ? _active.ClampedMaxPitchDownDegrees
                : ShipPropulsionAggregation.VisualPitchDefaultMaxDownDegrees;

        /// <summary>Peak nose-up pitch (°). Reads the published asset when set.</summary>
        public static float MaxPitchUpDegrees =>
            _active != null
                ? _active.ClampedMaxPitchUpDegrees
                : ShipPropulsionAggregation.VisualPitchDefaultMaxUpDegrees;

        /// <summary>Forward accel (u/s²) treated as full cruise pitch.</summary>
        public static float ReferenceAccel =>
            _active != null
                ? _active.ClampedReferenceAccel
                : ShipPropulsionAggregation.VisualPitchReferenceAccel;

        /// <summary>Cruise-accel → pitch multiplier.</summary>
        public static float PitchSensitivity =>
            _active != null ? _active.ClampedPitchSensitivity : 1f;

        /// <summary>Exponential catch-up rate for cruise pitch.</summary>
        public static float PitchSmoothing =>
            _active != null ? _active.ClampedPitchSmoothing : 6f;

        /// <summary>|Δv| (u/s) that counts as a collision punch.</summary>
        public static float ImpactDeltaSpeed =>
            _active != null ? _active.ClampedImpactDeltaSpeed : 1.75f;

        /// <summary>Impact pitch (° per u/s of sudden heading-speed change).</summary>
        public static float ImpactDegreesPerSpeed =>
            _active != null ? _active.ClampedImpactDegreesPerSpeed : 2f;

        /// <summary>Impact spring-back rate.</summary>
        public static float ImpactDecay =>
            _active != null ? _active.ClampedImpactDecay : 8f;

        /// <summary>
        /// Points the cache at a designer asset so EG / turret bank and unset hybrid proxies
        /// share one feel. Hybrid proxies that bound a family asset read that asset directly.
        /// </summary>
        /// <param name="settings">Shared or family bank profile. Null keeps the last published asset.</param>
        public static void Publish(ShipBankVisualSettings settings)
        {
            if (settings != null)
                _active = settings;
        }

        /// <summary>
        /// Loads <see cref="ShipBankVisualSettings.LoadDefault"/> when nothing is published yet.
        /// Safe to call from Awake / OnEnable.
        /// </summary>
        public static void PublishDefaultIfNeeded()
        {
            if (_active != null)
                return;

            ShipBankVisualSettings defaults = ShipBankVisualSettings.LoadDefault();
            if (defaults != null)
                _active = defaults;
        }
    }
}
