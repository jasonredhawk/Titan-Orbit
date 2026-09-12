using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Loads <see cref="ShipImpactSpinSettings"/> at play time so Inspector edits apply without
    /// rebaking SubScenes. Place on NceGameRoot (same pattern as ShipRammingSettingsLoader).
    /// Sole asset: <c>Assets/Resources/ShipImpactSpinSettings.asset</c>.
    /// </summary>
    public class ShipImpactSpinSettingsLoader : MonoBehaviour
    {
        const string ResourcesLoadName = "ShipImpactSpinSettings";

        [SerializeField] ShipImpactSpinSettings settings;

        public ShipImpactSpinSettings Settings => settings;

        void Awake()
        {
            if (settings == null)
                settings = Resources.Load<ShipImpactSpinSettings>(ResourcesLoadName);

            if (settings != null)
            {
                settings.ClampValues();
                ShipImpactSpinSettingsCache.Settings = settings;
                return;
            }

            Debug.LogWarning(
                "[ShipImpactSpinSettingsLoader] No ShipImpactSpinSettings found. " +
                $"Create one via Assets → Create → Titan Orbit → Ship Impact Spin Settings " +
                $"(expected at Assets/Resources/{ResourcesLoadName}.asset). Using code defaults until then.");
            ShipImpactSpinSettingsCache.ResolveOrDefault();
        }
    }
}
