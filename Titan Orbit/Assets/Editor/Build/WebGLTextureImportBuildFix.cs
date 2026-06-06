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

        const string NeedsWebGlTextureRefreshKey = "TitanOrbit_NeedsWebGlTextureRefresh";

        internal static readonly string[] TextureRootFolders =
        {
            "Assets/UltimateSpaceshipsCreator/Textures",
            "Assets/Plugins/CW/SpaceGraphicsToolkit/Packs/PLANETS/Textures",
            "Assets/StarSparrow/Textures/BonusContent/Asteroids",
            "Assets/StarSparrow/Textures",
            "Assets/HiRezSpaceshipsCreatorFree/Textures",
            "Assets/DinV/Dynamic Space Background/Sprites",
            "Assets/Textures",
        };

        const string BuildProfilesFolder = "Assets/Settings/Build Profiles";
        const int WebGlBuildTargetEnum = 20; // BuildTarget.WebGL
        const int DxtSubtarget = 0; // WebGLTextureSubtarget.DXT
        const int WebGlGenericTextureCompression = 0; // PlayerSettings WebGL: Generic / no global override

        public int callbackOrder => -200;

        static WebGLTextureImportBuildFix()
        {
            ApplyWebGlTextureSubtarget();
            EnsureWebGlBuildProfilesUseDxt(log: false);
            EnsureWebGlPlayerDefaultTextureCompression(log: false);
            BuildPlayerWindow.RegisterBuildPlayerHandler(OnBuildPlayerFromWindow);

            EditorUserBuildSettings.activeBuildTargetChanged += OnActiveBuildTargetChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        static void OnActiveBuildTargetChanged()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL)
            {
                ApplyWebGlTextureSubtarget();
                EnsureWebGlBuildProfilesUseDxt(log: false);
                return;
            }

            MarkWebGlTextureRefreshNeeded();
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
                MarkWebGlTextureRefreshNeeded();
        }

        internal static void MarkWebGlTextureRefreshNeeded()
        {
            SessionState.SetBool(NeedsWebGlTextureRefreshKey, true);
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
            EnsureWebGlBuildProfilesUseDxt(log);
            EnsureWebGlPlayerDefaultTextureCompression(log);
            EnsureSgtPlanetShaderIncluded();
            DisableSrpBatcherOnWebGlPipelineAsset();

            bool forceReimport = SessionState.GetBool(NeedsWebGlTextureRefreshKey, true);
            int fixedCount = ApplyWebGlGameplayTextureImports(log: log, forceReimport: forceReimport);

            ValidateGameplayTextureWebGlImports();

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

            SessionState.SetBool(NeedsWebGlTextureRefreshKey, false);
        }

        [MenuItem("TitanOrbit/Build/Fix WebGL Texture Import (disable Crunch)")]
        public static void FixFromMenu()
        {
            MarkWebGlTextureRefreshNeeded();
            PrepareWebGlBuild(log: true);
            EditorUtility.DisplayDialog(
                "WebGL textures",
                "Gameplay textures configured for WebGL (RGBA32 uncompressed, no Crunch). " +
                "SRP Batcher disabled on the WebGL pipeline. Build subtarget set to DXT.",
                "OK");
        }

        internal static void ApplyWebGlTextureSubtarget()
        {
            EditorUserBuildSettings.webGLBuildSubtarget = WebGLTextureSubtarget.DXT;
        }

        internal static bool IsGameplayTexturePath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;

            string normalized = assetPath.Replace('\\', '/');
            foreach (string root in TextureRootFolders)
            {
                if (normalized.StartsWith(root + "/", System.StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals(root, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static int GetGameplayTextureMaxSize(string path)
        {
            if (path.StartsWith(
                    "Assets/Plugins/CW/SpaceGraphicsToolkit/Packs/PLANETS/Textures",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return 1024;
            }

            if (path.StartsWith(
                    "Assets/DinV/Dynamic Space Background/Sprites",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return 1024;
            }

            return 512;
        }

        /// <summary>
        /// Applies WebGL RGBA32 import overrides. Returns true when importer settings were changed.
        /// </summary>
        internal static bool ApplyWebGlSettingsToImporter(TextureImporter importer, string path)
        {
            if (importer == null)
                return false;

            int maxSize = GetGameplayTextureMaxSize(path);
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
                return false;

            importer.SetPlatformTextureSettings(webgl);
            return true;
        }

        static void ValidateGameplayTextureWebGlImports()
        {
            var invalid = new List<string>();
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", TextureRootFolders);

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                    continue;

                TextureImporterPlatformSettings webgl = importer.GetPlatformTextureSettings("WebGL");
                if (webgl == null ||
                    !webgl.overridden ||
                    webgl.textureCompression != TextureImporterCompression.Uncompressed ||
                    webgl.format != TextureImporterFormat.RGBA32 ||
                    webgl.crunchedCompression)
                {
                    invalid.Add(path);
                }
            }

            if (invalid.Count == 0)
                return;

            throw new BuildFailedException(
                "[WebGLTextureImportBuildFix] Gameplay textures still have invalid WebGL import settings " +
                $"(expected RGBA32 uncompressed). Examples: {string.Join(", ", invalid.GetRange(0, System.Math.Min(5, invalid.Count)))}. " +
                "Run TitanOrbit → Build → Fix WebGL Texture Import (disable Crunch), then rebuild.");
        }

        /// <summary>
        /// Build Profiles store their own WebGL texture subtarget (often ASTC). Unity 6 applies that
        /// over EditorUserBuildSettings, producing invisible ship/planet meshes on desktop browsers.
        /// </summary>
        internal static void EnsureWebGlBuildProfilesUseDxt(bool log)
        {
            if (!AssetDatabase.IsValidFolder(BuildProfilesFolder))
                return;

            bool anyChanged = false;
            string[] guids = AssetDatabase.FindAssets("t:BuildProfile", new[] { BuildProfilesFolder });

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
                if (assets == null || assets.Length == 0)
                    continue;

                bool profileChanged = false;
                foreach (Object asset in assets)
                {
                    if (asset == null)
                        continue;

                    var so = new SerializedObject(asset);
                    bool changed = false;

                    SerializedProperty buildTarget = so.FindProperty("m_BuildTarget");
                    if (buildTarget != null && buildTarget.intValue == WebGlBuildTargetEnum)
                    {
                        SerializedProperty subtarget = so.FindProperty("m_Subtarget");
                        if (subtarget != null && subtarget.intValue != DxtSubtarget)
                        {
                            subtarget.intValue = DxtSubtarget;
                            changed = true;
                        }
                    }

                    SerializedProperty webGlTextureSubtarget = so.FindProperty("m_WebGLTextureSubtarget");
                    if (webGlTextureSubtarget != null && webGlTextureSubtarget.intValue != DxtSubtarget)
                    {
                        webGlTextureSubtarget.intValue = DxtSubtarget;
                        changed = true;
                    }

                    if (!changed)
                        continue;

                    so.ApplyModifiedPropertiesWithoutUndo();
                    profileChanged = true;
                }

                if (!profileChanged)
                    continue;

                EditorUtility.SetDirty(AssetDatabase.LoadMainAssetAtPath(path));
                anyChanged = true;
                if (log)
                {
                    Debug.Log(
                        "[WebGLTextureImportBuildFix] WebGL Build Profile texture subtarget set to DXT (desktop browsers): "
                        + path);
                }
            }

            if (anyChanged)
                AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// PlayerSettings can default WebGL to BC/ASTC; per-texture overrides should win, but keep Generic
        /// so switching build targets in the Editor does not recompress gameplay albedos unexpectedly.
        /// </summary>
        internal static void EnsureWebGlPlayerDefaultTextureCompression(bool log)
        {
            const string projectSettingsPath = "ProjectSettings/ProjectSettings.asset";
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(projectSettingsPath);
            if (assets == null || assets.Length == 0)
                return;

            var so = new SerializedObject(assets[0]);
            SerializedProperty formats = so.FindProperty("m_BuildTargetDefaultTextureCompressionFormat");
            if (formats == null)
                return;

            bool changed = false;
            for (int i = 0; i < formats.arraySize; i++)
            {
                SerializedProperty entry = formats.GetArrayElementAtIndex(i);
                SerializedProperty buildTarget = entry.FindPropertyRelative("m_BuildTarget");
                SerializedProperty formatList = entry.FindPropertyRelative("m_Formats");
                if (buildTarget == null || formatList == null || buildTarget.intValue != WebGlBuildTargetEnum)
                    continue;

                if (formatList.arraySize == 0)
                {
                    formatList.InsertArrayElementAtIndex(0);
                    formatList.GetArrayElementAtIndex(0).intValue = WebGlGenericTextureCompression;
                    changed = true;
                    continue;
                }

                if (formatList.GetArrayElementAtIndex(0).intValue == WebGlGenericTextureCompression)
                    continue;

                formatList.GetArrayElementAtIndex(0).intValue = WebGlGenericTextureCompression;
                changed = true;
            }

            if (!changed)
                return;

            so.ApplyModifiedPropertiesWithoutUndo();
            if (log)
            {
                Debug.Log(
                    "[WebGLTextureImportBuildFix] PlayerSettings WebGL default texture compression set to Generic.");
            }
        }

        internal static int ApplyWebGlGameplayTextureImports(bool log, bool forceReimport = false)
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

                bool changed = ApplyWebGlSettingsToImporter(importer, path);
                if (changed || forceReimport)
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

            AssetDatabase.SaveAssets();

            if (log)
            {
                Debug.Log(
                    forceReimport
                        ? $"[WebGLTextureImportBuildFix] Refreshed {pathsToReimport.Count} gameplay textures for WebGL " +
                          $"(Editor/Play Mode session detected — rebuilding Library WebGL variants as RGBA32)."
                        : $"[WebGLTextureImportBuildFix] Reimported {pathsToReimport.Count} textures as WebGL RGBA32 " +
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
                AssetDatabase.SaveAssets();
                Debug.Log("[WebGLTextureImportBuildFix] Disabled SRP Batcher on Mobile_RPAsset (WebGL quality pipeline).");
            }
        }

        /// <summary>Legacy entry point used by older call sites.</summary>
        internal static int DisableWebGlCrunchOnGameplayTextures(bool log) =>
            ApplyWebGlGameplayTextureImports(log);
    }

    /// <summary>
    /// Keeps gameplay texture WebGL import settings stable when Unity reimports after Editor Play Mode
    /// or active build target switches (common cause of recurring invisible ship/planet WebGL builds).
    /// </summary>
    sealed class WebGLGameplayTextureImportPostprocessor : AssetPostprocessor
    {
        void OnPreprocessTexture()
        {
            if (!WebGLTextureImportBuildFix.IsGameplayTexturePath(assetPath))
                return;

            var importer = assetImporter as TextureImporter;
            if (importer == null)
                return;

            WebGLTextureImportBuildFix.ApplyWebGlSettingsToImporter(importer, assetPath);
        }
    }
}
