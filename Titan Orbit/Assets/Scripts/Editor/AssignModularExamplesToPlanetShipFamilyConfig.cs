using UnityEngine;
using UnityEditor;
using System.IO;
using TitanOrbit.Data;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Scans ModularExamples subfolders and populates PlanetShipFamilyConfig. Each subfolder becomes a ship family for a planet.
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

                var entry = new PlanetShipFamilyConfig.ShipFamilyEntry
                {
                    familyName = familyName,
                    planetId = planetId,
                    prefabs = new GameObject[20]
                };

                int assigned = 0;
                for (int i = 1; i <= 20; i++)
                {
                    string path1 = $"{dir}/{familyName}{i}.prefab";
                    string path2 = $"{dir}/{familyName}{i:D2}.prefab";
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path1);
                    if (prefab == null) prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path2);
                    if (prefab != null)
                    {
                        entry.prefabs[i - 1] = prefab;
                        assigned++;
                    }
                }
                config.families.Add(entry);
                planetId++;
                Debug.Log($"  {familyName}: {assigned} prefabs (planet {planetId - 1})");
            }

            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            Debug.Log($"PlanetShipFamilyConfig updated: {config.families.Count} families from ModularExamples. Assign this config to ShipUnlockTable.planetShipFamilyConfig.");
        }
    }
}
