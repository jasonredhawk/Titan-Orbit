#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TitanOrbit.Data;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Scans <c>Assets/Scenes/Badges</c> and writes <c>Resources/PlayerBadgeCatalog.asset</c>
    /// so player builds include every selectable sprite. Runtime never depends on this file.
    /// </summary>
    public static class PlayerBadgeCatalogEditor
    {
        const string CatalogAssetPath = "Assets/Resources/PlayerBadgeCatalog.asset";
        const string BadgeFolder = "Assets/Scenes/Badges";

        static readonly Regex BadgeFileRegex = new Regex(
            @"^Badge \((\d+)\)\.png$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>Creates or refreshes the player-badge catalog from the Badges folder.</summary>
        [MenuItem("Titan Orbit/Data/Rebuild Player Badge Catalog")]
        public static void RebuildCatalog()
        {
            if (!AssetDatabase.IsValidFolder(BadgeFolder))
            {
                Debug.LogError("[PlayerBadgeCatalog] Folder missing: " + BadgeFolder);
                return;
            }

            string[] guids = AssetDatabase.FindAssets("t:Sprite", new[] { BadgeFolder });
            var rows = new List<PlayerBadgeCatalog.Entry>(guids.Length);

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path))
                    continue;

                string fileName = System.IO.Path.GetFileName(path);
                Match match = BadgeFileRegex.Match(fileName);
                if (!match.Success)
                    continue;

                if (!int.TryParse(match.Groups[1].Value, out int badgeId) || badgeId <= 0)
                    continue;

                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null)
                    continue;

                rows.Add(new PlayerBadgeCatalog.Entry
                {
                    badgeId = badgeId,
                    sprite = sprite,
                });
            }

            rows.Sort((a, b) => a.badgeId.CompareTo(b.badgeId));

            var catalog = AssetDatabase.LoadAssetAtPath<PlayerBadgeCatalog>(CatalogAssetPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<PlayerBadgeCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogAssetPath);
            }

            catalog.entries = rows.ToArray();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[PlayerBadgeCatalog] Rebuilt " + rows.Count + " badges at " + CatalogAssetPath);
        }

        /// <summary>
        /// Mipmaps + full-rect mesh so world nameplates stay a clean circle in flight
        /// instead of a bilinear-smeared tight mesh.
        /// </summary>
        [MenuItem("Titan Orbit/Data/Fix Player Badge Sprite Import")]
        public static void ApplyNameplateImportSettings()
        {
            if (!AssetDatabase.IsValidFolder(BadgeFolder))
                return;

            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { BadgeFolder });
            int changed = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer == null)
                        continue;

                    bool dirty = false;
                    if (!importer.mipmapEnabled)
                    {
                        importer.mipmapEnabled = true;
                        dirty = true;
                    }

                    if (!importer.mipMapsPreserveCoverage)
                    {
                        importer.mipMapsPreserveCoverage = true;
                        dirty = true;
                    }

                    if (importer.filterMode != FilterMode.Trilinear)
                    {
                        importer.filterMode = FilterMode.Trilinear;
                        dirty = true;
                    }

                    if (importer.spriteImportMode != SpriteImportMode.Single)
                    {
                        importer.spriteImportMode = SpriteImportMode.Single;
                        dirty = true;
                    }

                    var settings = new TextureImporterSettings();
                    importer.ReadTextureSettings(settings);
                    if (settings.spriteMeshType != SpriteMeshType.FullRect)
                    {
                        settings.spriteMeshType = SpriteMeshType.FullRect;
                        importer.SetTextureSettings(settings);
                        dirty = true;
                    }

                    if (importer.textureCompression != TextureImporterCompression.CompressedHQ)
                    {
                        importer.textureCompression = TextureImporterCompression.CompressedHQ;
                        dirty = true;
                    }

                    if (dirty)
                    {
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                        changed++;
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            Debug.Log("[PlayerBadgeCatalog] Updated import settings on " + changed + " badge textures.");
        }
    }
}
#endif
