using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Designer asset for client-only ship bank (roll-while-turning).
    /// Create via Assets → Create → Titan Orbit → Ship Bank Visual Settings.
    /// Assign on each <see cref="ShipFamilyDefinition.bankVisualSettings"/> so families can share
    /// one profile today and swap unique lean later. Consumed by
    /// <see cref="TitanOrbit.Game.ShipBankVisualApplier"/> and published into
    /// <see cref="ShipBankVisualSettingsCache"/> for Entities Graphics / turret bank — no sim impact.
    /// </summary>
    [CreateAssetMenu(
        fileName = "ShipBankVisualSettings",
        menuName = "Titan Orbit/Ship Bank Visual Settings",
        order = 46)]
    public class ShipBankVisualSettings : ScriptableObject
    {
        /// <summary>
        /// [UNITY] Resources name for the shared default asset
        /// (<c>Assets/Resources/ShipBankVisualSettings.asset</c>).
        /// </summary>
        public const string DefaultResourcesName = "ShipBankVisualSettings";

        [Header("Bank Angle")]
        [Tooltip(
            "Peak roll angle in degrees at full turn (when Bank Sensitivity is 1). " +
            "Raise for a deeper lean; does not affect turn rate or physics.")]
        [Range(15f, 160f)]
        public float maxBankAngleDegrees = ShipPropulsionAggregation.VisualBankReferenceMaxAngleDegrees;

        [Header("Sensitivity")]
        [Tooltip(
            "How sensitive banking is to yaw rate. 1 = linear (old feel). " +
            "Higher values lean harder at partial turn stick. " +
            "Lower toward 0.8–1.0 if banking feels too twitchy.")]
        [Range(0.25f, 3f)]
        public float bankSensitivity = 1.35f;

        [Header("Smoothing")]
        [Tooltip(
            "How quickly roll catches up to the target bank angle. Higher = snappier, lower = floatier.")]
        [Range(1f, 24f)]
        public float bankSmoothing = 8f;

        /// <summary>Peak roll (°), clamped for runtime consumers.</summary>
        public float ClampedMaxBankAngleDegrees => Mathf.Clamp(maxBankAngleDegrees, 1f, 180f);

        /// <summary>Turn-rate → bank multiplier, never negative.</summary>
        public float ClampedBankSensitivity => Mathf.Max(0f, bankSensitivity);

        /// <summary>Yaw-sample / roll lerp rate, kept above zero so exp smoothing stays defined.</summary>
        public float ClampedBankSmoothing => Mathf.Max(0.01f, bankSmoothing);

        /// <summary>
        /// Loads the shared default from Resources (player builds). Editor can create one via the menu.
        /// </summary>
        public static ShipBankVisualSettings LoadDefault()
        {
            return Resources.Load<ShipBankVisualSettings>(DefaultResourcesName);
        }

        /// <summary>
        /// Family profile when assigned; otherwise the shared Resources default.
        /// </summary>
        /// <param name="family">Ship family that may point at a unique or shared asset.</param>
        public static ShipBankVisualSettings ResolveForFamily(ShipFamilyDefinition family)
        {
            if (family != null && family.bankVisualSettings != null)
                return family.bankVisualSettings;
            return LoadDefault();
        }

#if UNITY_EDITOR
        /// <summary>
        /// [EDITOR] Keep knobs in range while scrubbing, and republish so Play Mode EG / turrets update live.
        /// </summary>
        void OnValidate()
        {
            maxBankAngleDegrees = Mathf.Clamp(maxBankAngleDegrees, 1f, 180f);
            bankSensitivity = Mathf.Max(0f, bankSensitivity);
            bankSmoothing = Mathf.Max(0.01f, bankSmoothing);
            ShipBankVisualSettingsCache.Publish(this);
        }
#endif
    }
}
