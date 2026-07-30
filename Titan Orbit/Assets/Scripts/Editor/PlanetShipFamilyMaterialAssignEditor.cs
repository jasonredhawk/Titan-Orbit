using UnityEditor;
using UnityEngine;
using TitanOrbit.Data;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// [EDITOR] Assigns one <see cref="PlanetMaterialPool"/> surface material per
    /// <see cref="PlanetShipFamilyConfig"/> family entry so neutrals that roll a family index
    /// share a recognizable planet skin with that ship tree. Homes (index 0) keep tropical water
    /// from the pool by team — the AstroEagle entry material is optional documentation only.
    /// Run after populating the material pool: <c>Titan Orbit → Assign Planet Materials To Ship Families</c>.
    /// </summary>
    public static class PlanetShipFamilyMaterialAssignEditor
    {
        const string FamilyConfigPath = "Assets/Resources/PlanetShipFamilyConfig.asset";
        const string PoolAssetPath = "Assets/Resources/PlanetMaterialPool.asset";

        /// <summary>
        /// Copies Materials[i] onto families[i].planetMaterial (wrapping the pool if needed).
        /// Overwrites existing assignments so designers can re-roll after pool edits.
        /// </summary>
        [MenuItem("Titan Orbit/Assign Planet Materials To Ship Families")]
        public static void AssignMaterialsFromPool()
        {
            // --- Load assets ---
            var config = AssetDatabase.LoadAssetAtPath<PlanetShipFamilyConfig>(FamilyConfigPath);
            var pool = AssetDatabase.LoadAssetAtPath<PlanetMaterialPool>(PoolAssetPath);
            if (config == null)
            {
                Debug.LogError($"[PlanetShipFamily] Missing config at {FamilyConfigPath}.");
                return;
            }

            if (pool == null || pool.Materials == null || pool.Materials.Count == 0)
            {
                Debug.LogError(
                    $"[PlanetShipFamily] PlanetMaterialPool empty at {PoolAssetPath}. " +
                    "Run Titan Orbit → Populate Planet Material Pool From CW Pack first.");
                return;
            }

            if (config.families == null || config.families.Count == 0)
            {
                Debug.LogError("[PlanetShipFamily] PlanetShipFamilyConfig.families is empty.");
                return;
            }

            // --- Assign one distinct surface per family slot ---
            int assigned = 0;
            for (int i = 0; i < config.families.Count; i++)
            {
                var entry = config.families[i];
                if (entry == null)
                    continue;

                // Skip null pool slots by wrapping.
                Material mat = pool.Materials[i % pool.Materials.Count];
                entry.planetMaterial = mat;
                assigned++;
            }

            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            Debug.Log(
                $"[PlanetShipFamily] Assigned {assigned} planetMaterial(s) from PlanetMaterialPool " +
                $"({pool.Materials.Count} surfaces) onto {config.families.Count} family entries. " +
                "Homes still use WaterMaterials by team at runtime; neutrals use these skins.");
        }
    }
}
