using System.Collections.Generic;
using System.Linq;
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
    public sealed class WebGLTextureImportBuildFix : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        const string SgtPlanetShaderPath =
            "Assets/Plugins/CW/SpaceGraphicsToolkit/Features/Planet/Required/Shaders/Planet.shader";

        internal static readonly string[] TextureRootFolders =
        {
            "Assets/UltimateSpaceshipsCreator/Textures",
            "Assets/Plugins/CW/SpaceGraphicsToolkit/Packs/PLANETS/Textures",
            // Planet materials also sample detail/normal/noise maps from SGT feature examples (not Packs/Textures).
            "Assets/Plugins/CW/SpaceGraphicsToolkit/Features/Planet/Examples/Textures",
            "Assets/Plugins/CW/SpaceGraphicsToolkit/Features/Planet/Required",
            "Assets/StarSparrow/Textures/BonusContent/Asteroids",
            "Assets/StarSparrow/Textures",
            "Assets/HiRezSpaceshipsCreatorFree/Textures",
            "Assets/DinV/Dynamic Space Background/Sprites",
            "Assets/Textures",
        };

        static readonly string[] GameplayMaterialRoots =
        {
            "Assets/Plugins/CW/SpaceGraphicsToolkit/Packs/PLANETS/Materials",
            "Assets/Data/PlanetMaterialPool.asset",
            "Assets/Prefabs/Starship.prefab",
            "Assets/Prefabs/Asteroid.prefab",
            "Assets/Prefabs/Ships",
            "Assets/Prefabs/Planets",
        };

        const string BuildProfilesFolder = "Assets/Settings/Build Profiles";
        const int WebGlBuildTargetEnum = 20; // BuildTarget.WebGL
        const int DxtSubtarget = 0; // WebGLTextureSubtarget.DXT
        const int WebGlGenericTextureCompression = 0; // PlayerSettings WebGL: Generic / no global override

        static BuildTarget? s_buildTargetToRestore;

        /// <summary>Unity 6 stores <c>m_BuildTarget</c> as a string in ProjectSettings; older assets use the enum int.</summary>
        static bool IsWebGlBuildTargetProperty(SerializedProperty buildTarget)
        {
            if (buildTarget == null)
                return false;

            switch (buildTarget.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.Enum:
                    return buildTarget.intValue == WebGlBuildTargetEnum;
                case SerializedPropertyType.String:
                    return string.Equals(buildTarget.stringValue, "WebGL", System.StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }

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
            }
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // Editor Play Mode uses desktop graphics; next WebGL build must rebuild WebGL texture variants.
            if (state == PlayModeStateChange.ExitingEditMode)
                ApplyWebGlTextureSubtarget();
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WebGL)
                return;

            PrepareWebGlBuild(log: true, restoreBuildTargetAfter: false);
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WebGL)
                return;

            RestoreBuildTargetIfNeeded();
        }

        /// <summary>
        /// Intercepts File → Build and Build Profile builds so WebGL always uses DXT + RGBA32 gameplay imports.
        /// Direct calls to <see cref="BuildPipeline.BuildPlayer"/> (e.g. production menu) are unchanged.
        /// </summary>
        static void OnBuildPlayerFromWindow(BuildPlayerOptions options)
        {
            BuildTarget? previousTarget = null;
            if (options.target == BuildTarget.WebGL)
            {
                previousTarget = EditorUserBuildSettings.activeBuildTarget;
                PrepareWebGlBuild(log: true, restoreBuildTargetAfter: false);
                options.subtarget = (int)WebGLTextureSubtarget.DXT;
            }

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (options.target == BuildTarget.WebGL)
                RestoreBuildTargetIfNeeded(previousTarget);

            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError(
                    $"[WebGLTextureImportBuildFix] Build failed: {report.summary.result} — {report.summary.totalErrors} error(s).");
            }
        }

        internal static void PrepareWebGlBuild(bool log, bool restoreBuildTargetAfter = false)
        {
            ApplyWebGlTextureSubtarget();
            EnsureWebGlBuildProfilesUseDxt(log);
            EnsureWebGlPlayerDefaultTextureCompression(log);
            EnsureSgtPlanetShaderIncluded();
            DisableSrpBatcherOnWebGlPipelineAsset();

            EnsureActiveBuildTargetIsWebGl();

            // Always refresh WebGL texture variants before packaging — meta can look correct while the
            // Library still holds stale Standalone/Editor variants after Play Mode (recurring invisible meshes).
            int fixedCount = ApplyWebGlGameplayTextureImports(log: log, forceReimport: true);

            ValidateGameplayTextureWebGlImports();

            if (log)
            {
                Debug.Log(
                    fixedCount > 0
                        ? $"[WebGLTextureImportBuildFix] Refreshed {fixedCount} gameplay texture(s) for WebGL RGBA32. Continuing WebGL build."
                        : "[WebGLTextureImportBuildFix] Gameplay textures validated for WebGL RGBA32 (uncompressed). Continuing WebGL build.");
            }

            if (EditorUserBuildSettings.webGLBuildSubtarget != WebGLTextureSubtarget.DXT)
            {
                throw new BuildFailedException(
                    "[WebGLTextureImportBuildFix] WebGL texture subtarget must be DXT for desktop browsers. " +
                    "Use TitanOrbit → Build → WebGL Production or Fix WebGL Texture Import.");
            }

            if (restoreBuildTargetAfter)
                RestoreBuildTargetIfNeeded();
        }

        [MenuItem("TitanOrbit/Build/Fix WebGL Texture Import (disable Crunch)")]
        public static void FixFromMenu()
        {
            BuildTarget previous = EditorUserBuildSettings.activeBuildTarget;
            try
            {
                PrepareWebGlBuild(log: true, restoreBuildTargetAfter: false);
            }
            finally
            {
                RestoreBuildTargetIfNeeded(previous);
            }

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

        static void EnsureActiveBuildTargetIsWebGl()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL)
                return;

            if (!s_buildTargetToRestore.HasValue)
                s_buildTargetToRestore = EditorUserBuildSettings.activeBuildTarget;

            EditorUserBuildSettings.SwitchActiveBuildTarget(
                BuildTargetGroup.WebGL,
                BuildTarget.WebGL);
        }

        static void RestoreBuildTargetIfNeeded(BuildTarget? explicitPrevious = null)
        {
            BuildTarget restore = explicitPrevious ?? s_buildTargetToRestore ?? EditorUserBuildSettings.activeBuildTarget;
            if (!explicitPrevious.HasValue && !s_buildTargetToRestore.HasValue)
                return;

            if (EditorUserBuildSettings.activeBuildTarget == restore)
            {
                s_buildTargetToRestore = null;
                return;
            }

            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(restore);
            EditorUserBuildSettings.SwitchActiveBuildTarget(group, restore);
            s_buildTargetToRestore = null;
        }

        /// <summary>Called by <see cref="TitanOrbitBuildAutomation.BuildWebGLProduction"/> after BuildPlayer.</summary>
        internal static void RestoreBuildTargetAfterProductionBuild(BuildTarget previousTarget)
        {
            RestoreBuildTargetIfNeeded(previousTarget);
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
                    "Assets/Plugins/CW/SpaceGraphicsToolkit/Features/Planet",
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
        /// SGT height/displacement maps and cookie offsets store height in texture alpha; WebGL R8 keeps only red
        /// so mesh displacement, bump detail, and water (heightMap.a) flatten on planets/asteroids/moons.
        /// </summary>
        internal static bool NeedsAlphaPreservingWebGlImport(TextureImporter importer, string path)
        {
            if (importer == null)
                return false;

            if (!string.IsNullOrEmpty(path) &&
                path.IndexOf("_Height.", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(path) &&
                path.EndsWith("PlanetWaterOffset.png", System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return importer.textureType == TextureImporterType.Cookie;
        }

        /// <summary>
        /// Applies WebGL uncompressed import overrides. Returns true when importer settings were changed.
        /// </summary>
        internal static bool ApplyWebGlSettingsToImporter(TextureImporter importer, string path)
        {
            if (importer == null)
                return false;

            int maxSize = GetGameplayTextureMaxSize(path);
            TextureImporterPlatformSettings webgl = importer.GetPlatformTextureSettings("WebGL");
            if (webgl == null || string.IsNullOrEmpty(webgl.name))
                webgl = new TextureImporterPlatformSettings { name = "WebGL" };

            bool preserveAlpha = NeedsAlphaPreservingWebGlImport(importer, path);
            TextureImporterFormat targetFormat = GetWebGlUncompressedFormat(importer, path);

            bool changed = false;
            if (preserveAlpha && importer.textureType == TextureImporterType.Cookie)
            {
                importer.textureType = TextureImporterType.Default;
                changed = true;
            }

            if (preserveAlpha &&
                path.IndexOf("_Height.", System.StringComparison.OrdinalIgnoreCase) >= 0 &&
                !importer.isReadable)
            {
                importer.isReadable = true;
                changed = true;
            }
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

            // Uncompressed avoids DXT/ASTC mismatch on desktop WebGL (invisible albedo, not magenta).
            if (webgl.textureCompression != TextureImporterCompression.Uncompressed)
            {
                webgl.textureCompression = TextureImporterCompression.Uncompressed;
                changed = true;
            }

            if (webgl.format != targetFormat)
            {
                webgl.format = targetFormat;
                changed = true;
            }

            if (!changed)
                return false;

            importer.SetPlatformTextureSettings(webgl);
            return true;
        }

        /// <summary>
        /// WebGL uncompressed format depends on <see cref="TextureImporter.textureType"/> (RGBA32 is invalid for SingleChannel).
        /// </summary>
        internal static TextureImporterFormat GetWebGlUncompressedFormat(TextureImporter importer, string path = null)
        {
            if (NeedsAlphaPreservingWebGlImport(importer, path))
                return TextureImporterFormat.RGBA32;

            switch (importer.textureType)
            {
                case TextureImporterType.SingleChannel:
                    return TextureImporterFormat.R8;
                case TextureImporterType.Cookie:
                    return TextureImporterFormat.R8;
                case TextureImporterType.NormalMap:
                case TextureImporterType.Default:
                case TextureImporterType.Sprite:
                case TextureImporterType.Lightmap:
                case TextureImporterType.DirectionalLightmap:
                case TextureImporterType.Shadowmask:
                    return TextureImporterFormat.RGBA32;
                default:
                    return TextureImporterFormat.RGBA32;
            }
        }

        static bool HasValidWebGlUncompressedSettings(TextureImporter importer, string path)
        {
            TextureImporterPlatformSettings webgl = importer.GetPlatformTextureSettings("WebGL");
            if (webgl == null || !webgl.overridden || webgl.crunchedCompression)
                return false;

            if (webgl.textureCompression != TextureImporterCompression.Uncompressed)
                return false;

            if (NeedsAlphaPreservingWebGlImport(importer, path) &&
                importer.textureType == TextureImporterType.Cookie)
            {
                return false;
            }

            return webgl.format == GetWebGlUncompressedFormat(importer, path);
        }

        static HashSet<string> CollectGameplayTexturePaths()
        {
            var paths = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            string[] folderGuids = AssetDatabase.FindAssets("t:Texture2D", TextureRootFolders);
            foreach (string guid in folderGuids)
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));

            foreach (string root in GameplayMaterialRoots)
            {
                if (string.IsNullOrEmpty(root))
                    continue;

                if (root.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase) ||
                    root.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (!System.IO.File.Exists(root))
                        continue;

                    foreach (string dep in AssetDatabase.GetDependencies(root, true))
                    {
                        if (IsManagedGameplayTexturePath(dep))
                            paths.Add(dep);
                    }

                    continue;
                }

                if (!AssetDatabase.IsValidFolder(root))
                    continue;

                string[] materialGuids = AssetDatabase.FindAssets("t:Material", new[] { root });
                foreach (string guid in materialGuids)
                {
                    string matPath = AssetDatabase.GUIDToAssetPath(guid);
                    foreach (string dep in AssetDatabase.GetDependencies(matPath, true))
                    {
                        if (IsManagedGameplayTexturePath(dep))
                            paths.Add(dep);
                    }
                }
            }

            return paths;
        }

        static bool IsTextureAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            string ext = System.IO.Path.GetExtension(path);
            return ext.Equals(".png", System.StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".jpg", System.StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".jpeg", System.StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".tga", System.StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".psd", System.StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".tif", System.StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".tiff", System.StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".exr", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Project gameplay textures only — excludes Package/editor gizmo assets pulled in via deep prefab deps.</summary>
        static bool IsManagedGameplayTexturePath(string path)
        {
            if (!IsTextureAssetPath(path))
                return false;

            string normalized = path.Replace('\\', '/');
            if (!normalized.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
                return false;

            if (normalized.IndexOf("/Editor/", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalized.IndexOf("/Editor Resources/", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            if (normalized.IndexOf("/MenuPreviews/", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            if (IsGameplayTexturePath(normalized))
                return true;

            // SGT planet materials reference detail maps outside Packs/PLANETS/Textures.
            return normalized.StartsWith(
                "Assets/Plugins/CW/SpaceGraphicsToolkit/",
                System.StringComparison.OrdinalIgnoreCase);
        }

        static void ValidateGameplayTextureWebGlImports()
        {
            var invalid = new List<string>();
            foreach (string path in CollectGameplayTexturePaths())
            {
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                    continue;

                TextureImporterPlatformSettings webgl = importer.GetPlatformTextureSettings("WebGL");
                if (!HasValidWebGlUncompressedSettings(importer, path))
                {
                    invalid.Add(path);
                }
            }

            if (invalid.Count == 0)
                return;

            throw new BuildFailedException(
                "[WebGLTextureImportBuildFix] Gameplay textures still have invalid WebGL import settings " +
                $"(expected uncompressed WebGL overrides). Examples: {string.Join(", ", invalid.Take(5))}. " +
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
                    if (IsWebGlBuildTargetProperty(buildTarget))
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
                if (buildTarget == null || formatList == null || !IsWebGlBuildTargetProperty(buildTarget))
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
            foreach (string path in CollectGameplayTexturePaths())
            {
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
                          "(rebuilt Library WebGL variants as RGBA32 after Editor session)."
                        : $"[WebGLTextureImportBuildFix] Reimported {pathsToReimport.Count} textures as WebGL RGBA32.");
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
