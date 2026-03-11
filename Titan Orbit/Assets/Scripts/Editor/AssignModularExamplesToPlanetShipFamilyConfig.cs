using UnityEngine;
using UnityEditor;
using System.IO;
using TitanOrbit.Data;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Populates PlanetShipFamilyConfig with one entry per ModularExamples subfolder. Each entry needs a ShipFamilyDefinition
    /// (assign in inspector or create via Titan Orbit > Ship Family Definition and "Build Upgrade Tree From Folder").
    /// Run: Titan Orbit > Assign ModularExamples to Planet Ship Family Config
    /// </summary>
    public static class AssignModularExamplesToPlanetShipFamilyConfig
    {
        private const string MODULAR_EXAMPLES_ROOT = "Assets/UltimateSpaceshipsCreator/Prefabs/ModularExamples";
        private const string CONFIG_PATH = "Assets/Data/PlanetShipFamilyConfig.asset";

        [MenuItem("Titan Orbit/Assign ModularExamples to Planet Ship Family Config")]
        public static void Assign()
        {
            if (!Directory.Exists(MODULAR_EXAMPLES_ROOT))
            {
                Debug.LogWarning($"ModularExamples folder not found at {MODULAR_EXAMPLES_ROOT}");
                return;
            }

            PlanetShipFamilyConfig config = AssetDatabase.LoadAssetAtPath<PlanetShipFamilyConfig>(CONFIG_PATH);
            if (config == null)
            {
                if (!Directory.Exists("Assets/Data"))
                    Directory.CreateDirectory("Assets/Data");
                config = ScriptableObject.CreateInstance<PlanetShipFamilyConfig>();
                AssetDatabase.CreateAsset(config, CONFIG_PATH);
            }

            config.families.Clear();
            string[] dirs = Directory.GetDirectories(MODULAR_EXAMPLES_ROOT);
            int planetId = 0;
            foreach (string dir in dirs)
            {
                string familyName = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(familyName)) continue;

                ShipFamilyDefinition definition = FindShipFamilyDefinitionByFamilyId(familyName);

                var entry = new PlanetShipFamilyConfig.ShipFamilyEntry
                {
                    planetId = planetId,
                    shipFamilyDefinition = definition,
                    familyName = definition != null ? null : familyName
                };

                config.families.Add(entry);
                planetId++;
                Debug.Log($"  {familyName}: planet {entry.planetId}, ShipFamilyDefinition {(definition != null ? "assigned" : "not found - assign in inspector")}");
            }

            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            Debug.Log($"PlanetShipFamilyConfig updated: {config.families.Count} families. Assign config to ShipUnlockTable and set ShipFamilyDefinition per entry if needed.");
        }

        private static ShipFamilyDefinition FindShipFamilyDefinitionByFamilyId(string familyId)
        {
            string[] guids = AssetDatabase.FindAssets("t:ShipFamilyDefinition");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var def = AssetDatabase.LoadAssetAtPath<ShipFamilyDefinition>(path);
                if (def != null && !string.IsNullOrEmpty(def.familyId) && def.familyId.Equals(familyId, System.StringComparison.OrdinalIgnoreCase))
                    return def;
            }
            return null;
        }
    }
}
