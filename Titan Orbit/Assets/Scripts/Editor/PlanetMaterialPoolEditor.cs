using UnityEngine;
using UnityEditor;
using TitanOrbit.Data;

namespace TitanOrbit.Editor
{
    public static class PlanetMaterialPoolEditor
    {
        const string PLANETS_MATERIALS_PATH = "Assets/Plugins/CW/SpaceGraphicsToolkit/Packs/PLANETS/Materials";

        [MenuItem("Titan Orbit/Populate Planet Material Pool From CW Pack")]
        public static void PopulateFromCWPack()
        {
            string[] guids = AssetDatabase.FindAssets("t:Material", new[] { PLANETS_MATERIALS_PATH });
            if (guids.Length == 0)
            {
                Debug.LogWarning("No materials found at " + PLANETS_MATERIALS_PATH);
                return;
            }

            var pool = AssetDatabase.LoadAssetAtPath<PlanetMaterialPool>("Assets/Data/PlanetMaterialPool.asset");
            if (pool == null)
            {
                if (!AssetDatabase.IsValidFolder("Assets/Data"))
                    AssetDatabase.CreateFolder("Assets", "Data");
                pool = ScriptableObject.CreateInstance<PlanetMaterialPool>();
                AssetDatabase.CreateAsset(pool, "Assets/Data/PlanetMaterialPool.asset");
            }

            pool.Materials.Clear();
            pool.WaterMaterials.Clear();

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) continue;
                pool.Materials.Add(mat);
            }

            // Home planets: WaterMaterials = exactly Tropical1, Tropical2, Tropical3 (Team A/B/C). Order matters.
            foreach (string name in new[] { "Tropical1", "Tropical2", "Tropical3" })
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(PLANETS_MATERIALS_PATH + "/" + name + ".mat");
                if (mat != null)
                    pool.WaterMaterials.Add(mat);
            }
            if (pool.WaterMaterials.Count == 0 && pool.Materials.Count > 0)
                pool.WaterMaterials.Add(pool.Materials[0]);

            EditorUtility.SetDirty(pool);
            AssetDatabase.SaveAssets();
            Debug.Log($"PlanetMaterialPool: {pool.Materials.Count} materials, {pool.WaterMaterials.Count} water (Tropical1/2/3 for home planets).");
        }
    }
}
