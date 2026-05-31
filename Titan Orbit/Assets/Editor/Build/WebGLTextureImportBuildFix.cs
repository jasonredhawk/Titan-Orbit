using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Editor.Build
{
    /// <summary>
    /// WebGL: GPU-compressed gameplay textures (DXT/ASTC/Crunch) and SRP Batcher + MaterialPropertyBlock
    /// often produce invisible ship/planet/asteroid meshes in desktop browsers while VFX still renders.
    /// Forces uncompressed RGBA WebGL imports for gameplay albedos, DXT data subtarget, SRP batcher off,
    /// and includes the SGT Planet shader in the build.
    /// </summary>
    [InitializeOnLoad]
    public sealed class WebGLTextureImportBuildFix : IPreprocessBuildWithReport
    {
        const string SgtPlanetShaderPath =
            "Assets/Plugins/CW/SpaceGraphicsToolkit/Features/Planet/Required/Shaders/Planet.shader";

        static readonly string[] TextureRootFolders =
        {
            "Assets/UltimateSpaceshipsCreator/Textures",
            "Assets/Plugins/CW/SpaceGraphicsToolkit/Packs/PLANETS/Textures",
            "Assets/StarSparrow/Textures/BonusContent/Asteroids",
            "Assets/StarSparrow/Textures",
            "Assets/HiRezSpaceshipsCreatorFree/Textures",
        };

        public int callbackOrder => -200;

        static WebGLTextureImportBuildFix()
        {
            ApplyWebGlTextureSubtarget();
            BuildPlayerWindow.RegisterBuildPlayerHandler(OnBuildPlayerFromWindow);
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WebGL)
                return;

            PrepareWebGlBuild(log: true);
        }

        /// <summary>
        /// Intercepts File → Build and Build Profile builds so WebGL always uses DXT + RGBA32 gameplay imports.
        /// Direct calls to <see cref="BuildPipeline.BuildPlayer"/> (e.g. production menu) are unchanged.
        /// </summary>
        static void OnBuildPlayerFromWindow(BuildPlayerOptions options)
        {
            if (options.target == BuildTarget.WebGL)
            {
                PrepareWebGlBuild(log: true);
                options.subtarget = (int)WebGLTextureSubtarget.DXT;
            }

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError(
                    $"[WebGLTextureImportBuildFix] Build failed: {report.summary.result} — {report.summary.totalErrors} error(s).");
            }
        }

        internal static void PrepareWebGlBuild(bool log)
        {
            ApplyWebGlTextureSubtarget();
            EnsureSgtPlanetShaderIncluded();
            DisableSrpBatcherOnWebGlPipelineAsset();

            int fixedCount = ApplyWebGlGameplayTextureImports(log: log);
            if (log)
            {
                Debug.Log(
                    fixedCount > 0
                        ? $"[WebGLTextureImportBuildFix] Updated WebGL import settings for {fixedCount} texture(s). Continuing WebGL build."
                        : "[WebGLTextureImportBuildFix] Gameplay textures already use WebGL RGBA32 (uncompressed). Continuing WebGL build.");
            }

            if (EditorUserBuildSettings.webGLBuildSubtarget != WebGLTextureSubtarget.DXT)
            {
                throw new BuildFailedException(
                    "[WebGLTextureImportBuildFix] WebGL texture subtarget must be DXT for desktop browsers. " +
                    "Use TitanOrbit → Build → WebGL Production or Fix WebGL Texture Import.");
            }
        }

        [MenuItem("TitanOrbit/Build/Fix WebGL Texture Import (disable Crunch)")]
        public static void FixFromMenu()
        {
            PrepareWebGlBuild(log: true);
            EditorUtility.DisplayDialog(
                "WebGL textures",
                "Gameplay textures configured for WebGL (RGBA32 uncompressed, no Crunch). " +
                "SRP Batcher disabled on WebGL pipeline. Build subtarget set to DXT.",
                "OK");
        }

        internal static void ApplyWebGlTextureSubtarget()
        {
            EditorUserBuildSettings.webGLBuildSubtarget = WebGLTextureSubtarget.DXT;
        }

        internal static int ApplyWebGlGameplayTextureImports(bool log)
        {
            ApplyWebGlTextureSubtarget();

            var pathsToReimport = new List<string>();
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", TextureRootFolders);

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                    continue;

                bool isPlanetTexture = path.StartsWith(
                    "Assets/Plugins/CW/SpaceGraphicsToolkit/Packs/PLANETS/Textures",
                    System.StringComparison.OrdinalIgnoreCase);
                int maxSize = isPlanetTexture ? 1024 : 512;

                TextureImporterPlatformSettings webgl = importer.GetPlatformTextureSettings("WebGL");
                if (webgl == null || string.IsNullOrEmpty(webgl.name))
                    webgl = new TextureImporterPlatformSettings { name = "WebGL" };

                bool changed = false;
                if (!webgl.overridden)
                {
                    webgl.overridden = true;
                    changed = true;
                }

                if (webgl.maxTextureSize > maxSize)
                {
                    webgl.maxTextureSize = maxSize;
                    changed = true;
                }

                if (webgl.crunchedCompression)
                {
                    webgl.crunchedCompression = false;
                    changed = true;
                }

                // Uncompressed RGBA avoids DXT/ASTC mismatch on desktop WebGL (invisible albedo, not magenta).
                if (webgl.textureCompression != TextureImporterCompression.Uncompressed)
                {
                    webgl.textureCompression = TextureImporterCompression.Uncompressed;
                    changed = true;
                }

                if (webgl.format != TextureImporterFormat.RGBA32)
                {
                    webgl.format = TextureImporterFormat.RGBA32;
                    changed = true;
                }

                if (!changed)
                    continue;

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
                    $"[WebGLTextureImportBuildFix] Reimported {pathsToReimport.Count} textures as WebGL RGBA32 " +
                    $"(folders: {string.Join(", ", TextureRootFolders)}).");
            }

            return pathsToReimport.Count;
        }

        static void EnsureSgtPlanetShaderIncluded()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(SgtPlanetShaderPath);
            if (shader == null)
            {
                Debug.LogWarning("[WebGLTextureImportBuildFix] SGT Planet shader not found at: " + SgtPlanetShaderPath);
                return;
            }

            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (assets == null || assets.Length == 0)
                return;

            var so = new SerializedObject(assets[0]);
            SerializedProperty prop = so.FindProperty("m_AlwaysIncludedShaders");
            if (prop == null)
                return;

            for (int i = 0; i < prop.arraySize; i++)
            {
                if (prop.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                    return;
            }

            int index = prop.arraySize;
            prop.InsertArrayElementAtIndex(index);
            prop.GetArrayElementAtIndex(index).objectReferenceValue = shader;
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log("[WebGLTextureImportBuildFix] Added Space Graphics Toolkit/Planet to Always Included Shaders.");
        }

        static void DisableSrpBatcherOnWebGlPipelineAsset()
        {
            const string webGlPipelinePath = "Assets/Settings/Mobile_RPAsset.asset";
            var pipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(webGlPipelinePath);
            if (pipeline == null)
                return;

            var so = new SerializedObject(pipeline);
            SerializedProperty useBatch = so.FindProperty("m_UseSRPBatcher");
            if (useBatch != null && useBatch.boolValue)
            {
                useBatch.boolValue = false;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(pipeline);
                Debug.Log("[WebGLTextureImportBuildFix] Disabled SRP Batcher on Mobile_RPAsset (WebGL quality pipeline).");
            }
        }

        /// <summary>Legacy entry point used by older call sites.</summary>
        internal static int DisableWebGlCrunchOnGameplayTextures(bool log) =>
            ApplyWebGlGameplayTextureImports(log);
    }
}
