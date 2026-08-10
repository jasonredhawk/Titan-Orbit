using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Process-wide cosmetic bank (roll-while-turning) knobs for hybrid ship proxies and
    /// Entities Graphics bank pivots. Lives in <c>TitanOrbit.Data</c> so both
    /// <c>TitanOrbit.Game</c> and <c>TitanOrbit.ECS</c> can read it (ECS cannot reference Game).
    /// Published from <c>EcsWorldVisualizer</c> Inspector fields — no sim or NetCode impact.
    /// Paired with <c>ShipBankVisualApplier</c> and <c>ShipEntitiesGraphicsBankSystem</c>.
    /// </summary>
    public static class ShipBankVisualSettingsCache
    {
        /// <summary>
        /// Roll degrees at full turn when sensitivity is 1. Matches the legacy reference angle
        /// until <c>EcsWorldVisualizer</c> publishes Inspector values.
        /// </summary>
        public static float MaxBankAngleDegrees =
            ShipPropulsionAggregation.VisualBankReferenceMaxAngleDegrees;

        /// <summary>
        /// How quickly bank builds for a given yaw rate. 1 = linear with turn fraction;
        /// higher reaches max bank at lower turn rates (feels more sensitive).
        /// </summary>
        public static float BankSensitivity = 1.35f;

        /// <summary>
        /// Exponential catch-up rate for yaw-rate sampling and roll angle (higher = snappier).
        /// </summary>
        public static float BankSmoothing = 8f;

        /// <summary>
        /// Copies designer values from the visualizer into the static cache used by both bank paths.
        /// Called from <c>EcsWorldVisualizer</c> Awake / OnValidate / OnEnable.
        /// </summary>
        /// <param name="maxBankAngleDegrees">Peak roll at full turn (°).</param>
        /// <param name="bankSensitivity">Multiplier on turn-rate → bank mapping (≥ 0).</param>
        /// <param name="bankSmoothing">Smoothing rate for yaw sample and roll lerp.</param>
        public static void Publish(float maxBankAngleDegrees, float bankSensitivity, float bankSmoothing)
        {
            // --- Clamp and publish ---
            // [TITAN-ORBIT] Cosmetic only — keep values sane if the Inspector is dragged wild.
            MaxBankAngleDegrees = Mathf.Clamp(maxBankAngleDegrees, 1f, 180f);
            BankSensitivity = Mathf.Max(0f, bankSensitivity);
            BankSmoothing = Mathf.Max(0.01f, bankSmoothing);
        }
    }
}
