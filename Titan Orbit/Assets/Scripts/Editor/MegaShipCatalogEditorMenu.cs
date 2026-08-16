#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Editor tools that scan <c>Assets/Prefabs/MEGA_Ships/</c> and write
    /// <c>Resources/MegaShipCatalog.asset</c>. Runtime never depends on this file.
    /// </summary>
    public static class MegaShipCatalogEditorMenu
    {
        const string CatalogAssetPath = "Assets/Resources/MegaShipCatalog.asset";
        const string MegaRoot = "Assets/Prefabs/MEGA_Ships";

        static readonly (MegaShipVisualFamily family, string folder)[] VisualFolders =
        {
            (MegaShipVisualFamily.CraizanStar, "CraizanStar (Mega)"),
            (MegaShipVisualFamily.GalacticLeopard, "GalacticLeopard (Mega)"),
            (MegaShipVisualFamily.GalacticOkamoto, "GalacticOkamoto (Mega)"),
        };

        /// <summary>Creates or refreshes the MEGA catalog from the three visual-family folders.</summary>
        [MenuItem("Titan Orbit/MEGA Ships/Rebuild Catalog From Folders")]
        public static void RebuildCatalogFromFolders()
        {
            var catalog = LoadOrCreateCatalog();
            if (catalog.weaponBulletStats.firePower <= 0.01f
                && catalog.hullStats.healthCap <= 0.01f)
            {
                catalog.ApplyDefaultStaticStats();
            }

            var previousSprites = new Dictionary<string, Sprite>(System.StringComparer.OrdinalIgnoreCase);
            var previousTeamSprites = new Dictionary<string, List<ShipFamilyTeamMenuPreview>>(
                System.StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < catalog.entries.Count; i++)
            {
                MegaShipCatalogEntry existing = catalog.entries[i];
                if (existing?.prefab == null)
                    continue;
                string keepPath = AssetDatabase.GetAssetPath(existing.prefab);
                if (string.IsNullOrEmpty(keepPath))
                    continue;
                if (existing.menuPreviewSprite != null)
                    previousSprites[keepPath] = existing.menuPreviewSprite;
                if (existing.teamMenuPreviewSprites != null && existing.teamMenuPreviewSprites.Count > 0)
                    previousTeamSprites[keepPath] = existing.teamMenuPreviewSprites;
            }

            catalog.entries.Clear();
            ushort nextIndex = 0;

            for (int f = 0; f < VisualFolders.Length; f++)
            {
                string folder = Path.Combine(MegaRoot, VisualFolders[f].folder).Replace('\\', '/');
                string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
                var paths = new List<string>(guids.Length);
                for (int i = 0; i < guids.Length; i++)
                    paths.Add(AssetDatabase.GUIDToAssetPath(guids[i]));
                paths.Sort(System.StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < paths.Count; i++)
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(paths[i]);
                    if (prefab == null)
                        continue;

                    previousSprites.TryGetValue(paths[i], out Sprite keepSprite);
                    previousTeamSprites.TryGetValue(paths[i], out List<ShipFamilyTeamMenuPreview> keepTeams);
                    catalog.entries.Add(new MegaShipCatalogEntry
                    {
                        catalogIndex = nextIndex,
                        visualFamily = VisualFolders[f].family,
                        displayName = prefab.name,
                        prefab = prefab,
                        menuPreviewSprite = keepSprite,
                        teamMenuPreviewSprites = keepTeams ?? new List<ShipFamilyTeamMenuPreview>(),
                    });
                    nextIndex++;
                }
            }

            int components = MegaShipComponentInventory.RefreshAll(catalog, keepManualStats: false);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            MegaShipCatalog.InvalidateCache();
            Debug.Log(
                $"[MegaShipCatalog] Rebuilt {catalog.entries.Count} MEGA hulls " +
                $"({components} unique components) into {CatalogAssetPath}.");
        }

        /// <summary>Writes designer-default shared type-table stats (short gun ranges 16 / 20).</summary>
        [MenuItem("Titan Orbit/MEGA Ships/Apply Default Type-Table Stats")]
        public static void ApplyDefaultTypeTableStats()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<MegaShipCatalog>(CatalogAssetPath);
            if (catalog == null)
            {
                Debug.LogError($"[MegaShipCatalog] Missing {CatalogAssetPath}. Run Rebuild Catalog From Folders first.");
                return;
            }

            Undo.RecordObject(catalog, "Apply Default MEGA Type-Table Stats");
            catalog.ApplyDefaultStaticStats();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            MegaShipCatalog.InvalidateCache();
            Debug.Log("[MegaShipCatalog] Applied designer default type-table stats (short gun ranges).");
        }

        /// <summary>
        /// Re-scans every hull prefab into the unique component library. Matching names keep
        /// hand-edited stats; new names get the type-table defaults; all hull sums are rewritten.
        /// </summary>
        [MenuItem("Titan Orbit/MEGA Ships/Refresh Unique Components")]
        public static void RefreshComponentStatsForAllShips()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<MegaShipCatalog>(CatalogAssetPath);
            if (catalog == null)
            {
                Debug.LogError($"[MegaShipCatalog] Missing {CatalogAssetPath}. Run Rebuild Catalog From Folders first.");
                return;
            }

            Undo.RecordObject(catalog, "Refresh MEGA Unique Components");
            int components = MegaShipComponentInventory.RefreshAll(catalog, keepManualStats: true);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            MegaShipCatalog.InvalidateCache();
            Debug.Log(
                $"[MegaShipCatalog] Unique components={components}; " +
                $"recalculated {catalog.entries.Count} hull sums. Hand-edited stats kept.");
        }

        /// <summary>
        /// Rebuilds the unique library and overwrites every row from the type table, then
        /// rewrites all hull sums. Use after changing the type-table defaults.
        /// </summary>
        [MenuItem("Titan Orbit/MEGA Ships/Reset Unique Components From Type Table")]
        public static void ResetComponentStatsFromTypeTable()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<MegaShipCatalog>(CatalogAssetPath);
            if (catalog == null)
            {
                Debug.LogError($"[MegaShipCatalog] Missing {CatalogAssetPath}. Run Rebuild Catalog From Folders first.");
                return;
            }

            Undo.RecordObject(catalog, "Reset MEGA Unique Components From Type Table");
            int components = MegaShipComponentInventory.RefreshAll(catalog, keepManualStats: false);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            MegaShipCatalog.InvalidateCache();
            Debug.Log(
                $"[MegaShipCatalog] Reset {components} unique components from the type table.");
        }

        /// <summary>Renders 5-team theatrical 3/4 hero thumbs and assigns teamMenuPreviewSprites.</summary>
        [MenuItem("Titan Orbit/MEGA Ships/Generate Theatrical Menu Previews")]
        public static void GenerateTheatricalMenuPreviews()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<MegaShipCatalog>(CatalogAssetPath);
            if (catalog == null)
            {
                Debug.LogError($"[MegaShipCatalog] Missing {CatalogAssetPath}. Run Rebuild Catalog From Folders first.");
                return;
            }

            Undo.RecordObject(catalog, "Generate MEGA Theatrical Menu Previews");
            MegaShipMenuPreviewGenerator.GenerateTheatricalForCatalog(catalog);
        }

        /// <summary>Bakes MEGA chassis into the Entities Graphics visual catalog after a rebuild.</summary>
        [MenuItem("Titan Orbit/MEGA Ships/Bake MEGA Visual Catalog Entries")]
        public static void BakeMegaVisualCatalogEntries()
        {
            RebuildCatalogFromFolders();

            var mega = AssetDatabase.LoadAssetAtPath<MegaShipCatalog>(CatalogAssetPath);
            var visual = AssetDatabase.LoadAssetAtPath<ShipChassisVisualCatalog>(
                "Assets/Resources/ShipChassisVisualCatalog.asset");
            if (mega == null || visual == null)
            {
                Debug.LogError("[MegaShipCatalog] MegaShipCatalog or ShipChassisVisualCatalog missing.");
                return;
            }

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
                    TitanOrbit.Core.TeamId.TeamA);
                visual.UpsertEntry(visualEntry);
                baked++;
            }

            EditorUtility.SetDirty(visual);
            AssetDatabase.SaveAssets();
            Debug.Log($"[MegaShipCatalog] Baked {baked} MEGA visual entries.");
        }

        static MegaShipCatalog LoadOrCreateCatalog()
        {
            var existing = AssetDatabase.LoadAssetAtPath<MegaShipCatalog>(CatalogAssetPath);
            if (existing != null)
                return existing;

            Directory.CreateDirectory("Assets/Resources");
            var created = ScriptableObject.CreateInstance<MegaShipCatalog>();
            created.ApplyDefaultStaticStats();
            AssetDatabase.CreateAsset(created, CatalogAssetPath);
            return created;
        }
    }
}
#endif
