using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Designer-tunable hull regen timing shared by every ship.
    /// Edit this asset in the Inspector. No code change is needed for the delay below.
    /// Sole asset: <c>Assets/Resources/ShipVitalsSettings.asset</c>
    /// (Create via Assets - Create - Titan Orbit - Ship Vitals Settings).
    /// Loaded at play by <see cref="ShipVitalsSettingsCache"/> via <c>Resources.Load</c>
    /// so the Editor and the dedicated server share one file.
    /// <para>
    /// [TITAN-ORBIT] Ship hull regen reads <see cref="healthRegenDelayAfterDamage"/> each tick.
    /// Regen stays off until that many seconds have passed since the last hull hit.
    /// Energy regen is not delayed. Docked-hull card regen still bypasses the delay.
    /// </para>
    /// </summary>
    [CreateAssetMenu(
        fileName = "ShipVitalsSettings",
        menuName = "Titan Orbit/Ship Vitals Settings",
        order = 52)]
    public class ShipVitalsSettings : ScriptableObject
    {
        /// <summary>Fallback when the Resources asset is missing. Matches the authored default.</summary>
        public const float DefaultHealthRegenDelayAfterDamage = 1f;

        /// <summary>[UNITY] Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        public const string ResourcesLoadName = "ShipVitalsSettings";

        [Header("Hull regen")]
        [Tooltip(
            "Seconds after hull damage before health regen resumes. " +
            "0 = regen never pauses. Energy regen is not affected. " +
            "Docked-hull card regen still heals during this window.")]
        [Min(0f)]
        public float healthRegenDelayAfterDamage = DefaultHealthRegenDelayAfterDamage;

        /// <summary>Sanitizes designer fields after Inspector edits.</summary>
        public void ClampValues()
        {
            healthRegenDelayAfterDamage = Mathf.Max(0f, healthRegenDelayAfterDamage);
        }

        /// <summary>[UNITY] Inspector edit. Keep the delay at or above zero.</summary>
        void OnValidate() => ClampValues();
    }
}
