#if UNITY_EDITOR
using System.IO;
using TitanOrbit.Core;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.ECS.Editor
{
    /// <summary>
    /// Editor tools for pure ECS ship presentation: bake chassis visuals into
    /// <see cref="ShipChassisVisualCatalog"/> and ensure presentation config assets exist.
    /// </summary>
    public static class ShipChassisVisualCatalogBakeMenu
    {
        const string CatalogResourcePath = "Assets/Resources/ShipChassisVisualCatalog.asset";
        const string PresentationConfigPath = "Assets/Resources/TitanOrbitPresentationConfig.asset";
        const string PlanetConfigResourcePath = "Resources/PlanetShipFamilyConfig";

        [MenuItem("Titan Orbit/Presentation/Ensure Entities Graphics Assets")]
        public static void EnsurePresentationAssets()
        {
            EnsurePresentationConfig();
            EnsureCatalog();
            Debug.Log("[ShipChassisVisualCatalogBakeMenu] Presentation config + catalog assets ready under Assets/Resources/.");
        }

        [MenuItem("Titan Orbit/Presentation/Bake Ship Chassis Visual Catalog")]
        public static void BakeShipChassisVisualCatalog()
        {
            EnsurePresentationAssets();

            var catalog = AssetDatabase.LoadAssetAtPath<ShipChassisVisualCatalog>(CatalogResourcePath);
            var planetConfig = Resources.Load<PlanetShipFamilyConfig>(PlanetConfigResourcePath)
                ?? Resources.Load<PlanetShipFamilyConfig>("PlanetShipFamilyConfig");

            if (planetConfig == null)
            {
                Debug.LogError("[ShipChassisVisualCatalogBakeMenu] PlanetShipFamilyConfig not found in Resources.");
                return;
            }

            int baked = 0;
            if (planetConfig.families != null)
            {
                for (int f = 0; f < planetConfig.families.Count; f++)
                {
                    var familyEntry = planetConfig.families[f];
                    var family = familyEntry?.shipFamilyDefinition;
                    if (family?.upgradeTree == null)
                        continue;

                    for (int t = 0; t < family.upgradeTree.Count; t++)
                    {
                        var tier = family.upgradeTree[t];
                        if (tier == null || tier.prefab == null || string.IsNullOrEmpty(tier.chassisId))
                            continue;

                        var visualEntry = ShipChassisPrefabBakeUtility.BakeVisualEntry(
                            tier.prefab,
                            tier.chassisId,
                            family,
                            TeamId.TeamA);

                        catalog.UpsertEntry(visualEntry);
                        baked++;
                    }
                }
            }

            baked += BakeMegaVisualEntries(catalog);

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            Debug.Log("[ShipChassisVisualCatalogBakeMenu] Baked " + baked + " chassis entries into ShipChassisVisualCatalog.");
        }

        /// <summary>Upserts MEGA_### visual entries from <see cref="MegaShipCatalog"/>.</summary>
        static int BakeMegaVisualEntries(ShipChassisVisualCatalog catalog)
        {
            var mega = AssetDatabase.LoadAssetAtPath<MegaShipCatalog>("Assets/Resources/MegaShipCatalog.asset");
            if (mega?.entries == null || catalog == null)
                return 0;

            int baked = 0;
            for (int i = 0; i < mega.entries.Count; i++)
            {
                var entry = mega.entries[i];
                if (entry == null || entry.prefab == null)
                    continue;

                string chassisId = MegaShipCatalog.FormatChassisId(i);
                var visualEntry = ShipChassisPrefabBakeUtility.BakeVisualEntry(
                    entry.prefab,
                    chassisId,
                    family: null,
                    TeamId.TeamA);
                catalog.UpsertEntry(visualEntry);
                baked++;
            }

            return baked;
        }

        static void EnsurePresentationConfig()
        {
            var existing = AssetDatabase.LoadAssetAtPath<TitanOrbitPresentationConfig>(PresentationConfigPath);
            if (existing != null)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(PresentationConfigPath) ?? "Assets/Resources");
            var asset = ScriptableObject.CreateInstance<TitanOrbitPresentationConfig>();
            AssetDatabase.CreateAsset(asset, PresentationConfigPath);
        }

        static void EnsureCatalog()
        {
            var existing = AssetDatabase.LoadAssetAtPath<ShipChassisVisualCatalog>(CatalogResourcePath);
            if (existing != null)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(CatalogResourcePath) ?? "Assets/Resources");
            var asset = ScriptableObject.CreateInstance<ShipChassisVisualCatalog>();
            AssetDatabase.CreateAsset(asset, CatalogResourcePath);
        }
    }
}
#endif
