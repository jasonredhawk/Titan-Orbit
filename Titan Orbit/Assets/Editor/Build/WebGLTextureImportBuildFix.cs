using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace TitanOrbit.Editor.Build
{
    /// <summary>
    /// WebGL: Crunch-compressed textures on ship/planet albedos often fail in the browser (meshes look
    /// invisible while particles, trails, and UI still render). Disables Crunch on WebGL overrides
    /// before production builds and forces DXT (desktop) GPU compression.
    /// </summary>
    public sealed class WebGLTextureImportBuildFix : IPreprocessBuildWithReport
    {
        static readonly string[] TextureRootFolders =
        {
            "Assets/UltimateSpaceshipsCreator/Textures",
            "Assets/Plugins/CW/SpaceGraphicsToolkit/Packs/PLANETS/Textures",
        };

        public int callbackOrder => -200;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WebGL)
                return;

            ApplyWebGlTextureSubtarget();
            int fixedCount = DisableWebGlCrunchOnGameplayTextures(log: true);
            if (fixedCount > 0)
            {
                Debug.Log(
                    $"[WebGLTextureImportBuildFix] Disabled Crunch on WebGL for {fixedCount} texture(s). " +
                    "Reimport finished; continuing WebGL build.");
            }
        }

        [MenuItem("TitanOrbit/Build/Fix WebGL Texture Import (disable Crunch)")]
        public static void FixFromMenu()
        {
            ApplyWebGlTextureSubtarget();
            int n = DisableWebGlCrunchOnGameplayTextures(log: true);
            EditorUtility.DisplayDialog(
                "WebGL textures",
                n > 0
                    ? $"Updated {n} texture importer(s). Reimport finished."
                    : "No textures needed changes (WebGL Crunch already off).",
                "OK");
        }

        internal static void ApplyWebGlTextureSubtarget()
        {
            EditorUserBuildSettings.webGLBuildSubtarget = WebGLTextureSubtarget.DXT;
        }

        internal static int DisableWebGlCrunchOnGameplayTextures(bool log)
        {
            var pathsToReimport = new List<string>();
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", TextureRootFolders);

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                    continue;

                TextureImporterPlatformSettings webgl = importer.GetPlatformTextureSettings("WebGL");
                if (!webgl.overridden || !webgl.crunchedCompression)
                    continue;

                webgl.crunchedCompression = false;
                importer.SetPlatformTextureSettings(webgl);
                pathsToReimport.Add(path);
            }

            if (pathsToReimport.Count == 0)
                return 0;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string path in pathsToReimport)
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            if (log)
            {
                Debug.Log(
                    $"[WebGLTextureImportBuildFix] Reimported {pathsToReimport.Count} textures " +
                    $"({string.Join(", ", TextureRootFolders)}).");
            }

            return pathsToReimport.Count;
        }
    }
}
