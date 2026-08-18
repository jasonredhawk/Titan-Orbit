using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Designer asset for client-only ship bank (roll-while-turning).
    /// Create via Assets → Create → Titan Orbit → Ship Bank Visual Settings.
    /// Assign on each <see cref="ShipFamilyDefinition.bankVisualSettings"/>, or on
    /// <see cref="MegaShipCatalog.bankVisualSettings"/> for every MEGA hull. Consumed by
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

        /// <summary>
        /// [UNITY] Resources name for the MEGA default
        /// (<c>Assets/Resources/MegaShipBankVisualSettings.asset</c>).
        /// </summary>
        public const string MegaResourcesName = "MegaShipBankVisualSettings";

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
            "Slow hulls (MEGAs) should also set Reference Turn so modest yaw still reaches peak roll.")]
        [Range(0.01f, 8f)]
        public float bankSensitivity = 1.35f;

        [Header("Smoothing")]
        [Tooltip(
            "How quickly roll catches up to the target bank angle. Higher = snappier, lower = floatier / heavier.")]
        [Range(0.01f, 24f)]
        public float bankSmoothing = 8f;

        [Header("Slow-hull reference")]
        [Tooltip(
            "Yaw rate (°/s) that reaches max bank when Sensitivity is 1. " +
            "0 = use the fleet's global max turn (regular ships). " +
            "Set a lower value for slow hulls so they bank hard without needing fighter turn speed.")]
        [Min(0f)]
        public float referenceTurnDegreesPerSecond = 0f;

        /// <summary>Peak roll (°), clamped for runtime consumers.</summary>
        public float ClampedMaxBankAngleDegrees => Mathf.Clamp(maxBankAngleDegrees, 1f, 180f);

        /// <summary>Turn-rate → bank multiplier, never negative.</summary>
        public float ClampedBankSensitivity => Mathf.Max(0f, bankSensitivity);

        /// <summary>Yaw-sample / roll lerp rate, kept above zero so exp smoothing stays defined.</summary>
        public float ClampedBankSmoothing => Mathf.Max(0.01f, bankSmoothing);

        /// <summary>
        /// Denominator for the bank curve (°/s). Authored reference when set;
        /// otherwise the fleet's global max turn.
        /// </summary>
        public float ResolveReferenceTurnDegreesPerSecond()
        {
            if (referenceTurnDegreesPerSecond > 0.01f)
                return referenceTurnDegreesPerSecond;
            return ShipPropulsionAggregation.GetGlobalMaxTurnSpeedDegreesPerSecond();
        }

        /// <summary>
        /// Loads the shared default from Resources (player builds). Editor can create one via the menu.
        /// </summary>
        public static ShipBankVisualSettings LoadDefault()
        {
            return Resources.Load<ShipBankVisualSettings>(DefaultResourcesName);
        }

        /// <summary>
        /// Loads the MEGA default from Resources when <see cref="MegaShipCatalog.bankVisualSettings"/> is empty.
        /// </summary>
        public static ShipBankVisualSettings LoadMegaDefault()
        {
            return Resources.Load<ShipBankVisualSettings>(MegaResourcesName);
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

        /// <summary>
        /// MEGA chassis uses <see cref="MegaShipCatalog.bankVisualSettings"/>;
        /// regular hulls use the family / shared default.
        /// </summary>
        /// <param name="chassisId">Live chassis id (<c>MEGA_007</c> or <c>AstroEagle_T2</c>).</param>
        /// <param name="family">Store-planet family (ignored for MEGA ids).</param>
        public static ShipBankVisualSettings ResolveForChassis(string chassisId, ShipFamilyDefinition family)
        {
            if (MegaShipCatalog.IsMegaChassisId(chassisId))
            {
                var catalog = MegaShipCatalog.Load();
                ShipBankVisualSettings mega = catalog != null
                    ? catalog.GetBankVisualSettings()
                    : LoadMegaDefault();
                if (mega != null)
                    return mega;
            }

            return ResolveForFamily(family);
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
            referenceTurnDegreesPerSecond = Mathf.Max(0f, referenceTurnDegreesPerSecond);
            // Only the shared Resources default drives the process-wide cache (EG regular hulls /
            // planetary turrets). MEGA and family-specific assets are sampled from the bound instance.
            if (name == DefaultResourcesName)
                ShipBankVisualSettingsCache.Publish(this);
        }
#endif
    }
}
