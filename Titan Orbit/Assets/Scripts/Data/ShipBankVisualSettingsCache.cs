namespace TitanOrbit.Data
{
    /// <summary>
    /// Process-wide cosmetic bank (roll-while-turning) knobs for hybrid ship proxies and
    /// Entities Graphics bank pivots. Lives in <c>TitanOrbit.Data</c> so both
    /// <c>TitanOrbit.Game</c> and <c>TitanOrbit.ECS</c> can read it (ECS cannot reference Game).
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
