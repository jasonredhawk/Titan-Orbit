using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.Editor
{
    public static class WebTextureSizeReducer
    {
        private const string WebGlPlatformName = "WebGL";

        // Folders that contain ship textures
        private static readonly string[] ShipTextureFolders =
        {
            "Assets/UltimateSpaceshipsCreator/Textures"
        };

        // Folders that contain planet textures
        private static readonly string[] PlanetTextureFolders =
        {
            "Assets/Plugins/CW/SpaceGraphicsToolkit/Packs/PLANETS/Textures"
        };

        [MenuItem("Tools/Textures/Reduce Ship & Planet Textures For WebGL")]
        public static void ReduceShipAndPlanetTexturesForWebGL()
        {
            var allFolders = new List<string>();
            allFolders.AddRange(ShipTextureFolders);
            allFolders.AddRange(PlanetTextureFolders);

            var guids = AssetDatabase.FindAssets("t:Texture2D", allFolders.ToArray());
            if (guids == null || guids.Length == 0)
            {
                EditorUtility.DisplayDialog(
                    "No Textures Found",
                    "No Texture2D assets were found in the configured ship/planet texture folders.",
                    "OK");
                return;
            }

            int processed = 0;

            try
            {
                AssetDatabase.StartAssetEditing();

                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path))
                        continue;

                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer == null)
                        continue;

                    bool isPlanetTexture = IsUnderAnyFolder(path, PlanetTextureFolders);
                    ApplyWebGlSettings(importer, isPlanetTexture);
                    processed++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "WebGL Texture Reduction Complete",
                $"Updated WebGL import settings for {processed} ship/planet textures.\n\n" +
                "All processed textures now use aggressive max sizes and compressed Crunch for WebGL builds.",
                "OK");
        }

        private static bool IsUnderAnyFolder(string assetPath, IReadOnlyList<string> folders)
        {
            if (folders == null)
                return false;

            foreach (var folder in folders)
            {
                if (string.IsNullOrEmpty(folder))
                    continue;

                if (assetPath.StartsWith(folder, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static void ApplyWebGlSettings(TextureImporter importer, bool isPlanetTexture)
        {
            const int shipMaxSize = 512;
            const int planetMaxSize = 1024;

            int maxSize = isPlanetTexture ? planetMaxSize : shipMaxSize;

            var webSettings = importer.GetPlatformTextureSettings(WebGlPlatformName);

            if (webSettings == null || string.IsNullOrEmpty(webSettings.name))
            {
                webSettings = new TextureImporterPlatformSettings
                {
                    name = WebGlPlatformName
                };
            }

            webSettings.overridden = true;
            webSettings.maxTextureSize = maxSize;

            // Force compressed + Crunch for significant size savings.
            webSettings.textureCompression = TextureImporterCompression.Compressed;
            webSettings.crunchedCompression = true;
            webSettings.compressionQuality = 30;

            importer.SetPlatformTextureSettings(webSettings);
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }
    }
}

