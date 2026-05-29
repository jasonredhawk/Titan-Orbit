using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace TitanOrbit.Editor.Build
{
    public static class TitanOrbitBuildAutomation
    {
        private const string WebBuildFolder = "BuildOutput/WebGL/production";
        private const string ServerWindowsBuildFolder = "BuildOutput/Server/headless-windows";
        /// <summary>Folder name must stay <c>TitanOrbitLinux1</c> so <c>tools/gce/*.bat</c> defaults and VM <c>REMOTE_DIR</c> match after upload.</summary>
        private const string ServerLinuxBuildFolder = "BuildOutput/Server/TitanOrbitLinux1";
        private const string AndroidApkFolder = "BuildOutput/Android";
        private const string AndroidApkFileName = "TitanOrbit.apk";

        [MenuItem("TitanOrbit/Build/WebGL Production")]
        public static void BuildWebGLProduction()
        {
            WebGLTextureImportBuildFix.ApplyWebGlGameplayTextureImports(log: true);

            var options = new BuildPlayerOptions
            {
                scenes = GetEnabledScenes(),
                locationPathName = GetWebGlOutputPath(),
                target = BuildTarget.WebGL,
                options = BuildOptions.None,
                // Unity 6: subtarget on BuildPlayerOptions is required; EditorUserBuildSettings alone
                // can leave the data file as ASTC while desktop browsers need DXT (invisible ship/planet meshes).
                subtarget = (int)WebGLTextureSubtarget.DXT
            };

            Debug.Log("[TitanOrbitBuild] WebGL production build: texture subtarget=DXT (desktop browsers).");

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
                Debug.LogError($"[TitanOrbitBuild] WebGL build failed: {report.summary.result} — {report.summary.totalErrors} error(s).");
        }

        [MenuItem("TitanOrbit/Build/Headless Server (Windows)")]
        public static void BuildHeadlessServer()
        {
            BuildPipeline.BuildPlayer(
                new BuildPlayerOptions
                {
                    scenes = GetEnabledScenes(),
                    locationPathName = GetWindowsServerOutputPath(),
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.EnableHeadlessMode
                }
            );
        }

        /// <summary>Linux dedicated server for GCE (output: <c>TitanOrbitServer.x86_64</c> under <see cref="ServerLinuxBuildFolder"/>). Requires Linux Dedicated Server module in Unity Hub.</summary>
        [MenuItem("TitanOrbit/Build/Headless Server (Linux — Google Cloud)")]
        public static void BuildHeadlessServerLinux()
        {
            string outputBasePath = GetLinuxServerOutputBasePath();

            // GCE Debian images often fail to load MonoBleedingEdge native libs ("Unable to load mono library" / exit 1).
            // Dedicated Server player target supports IL2CPP — no Mono .so chain on the VM (see Player.log on failure).
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Server, ScriptingImplementation.IL2CPP);
            Debug.Log("[TitanOrbitBuild] Dedicated Server scripting backend set to IL2CPP for this Linux server build.");

            var options = new BuildPlayerOptions
            {
                scenes = GetEnabledScenes(),
                locationPathName = outputBasePath,
                target = BuildTarget.StandaloneLinux64,
                options = BuildOptions.EnableHeadlessMode,
                subtarget = (int)StandaloneBuildSubtarget.Server
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result == BuildResult.Succeeded)
            {
                string folder = Path.GetDirectoryName(outputBasePath) ?? outputBasePath;
                Debug.Log($"[TitanOrbitBuild] Linux server build OK. Deploy: tools\\gce\\deploy_server_gce.bat\nOutput folder: {folder}");
            }
            else
            {
                Debug.LogError($"[TitanOrbitBuild] Linux server build failed: {report.summary.result} — {report.summary.totalErrors} error(s). See Console / Build steps.");
            }
        }

        /// <summary>Android player APK at <c>BuildOutput/Android/TitanOrbit.apk</c>. Requires Android Build Support and JDK in Edit → Preferences → External Tools.</summary>
        [MenuItem("TitanOrbit/Build/Android APK")]
        public static void BuildAndroidApk()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
            {
                Debug.LogError("[TitanOrbitBuild] Android build target is not available. Install Android Build Support via Unity Hub for this editor version.");
                return;
            }

            string apkPath = GetAndroidApkOutputPath();
            bool previousBuildAppBundle = EditorUserBuildSettings.buildAppBundle;
            EditorUserBuildSettings.buildAppBundle = false;
            Debug.Log($"[TitanOrbitBuild] Android APK build started → {apkPath} (App Bundle disabled for this build).");

            try
            {
                var options = new BuildPlayerOptions
                {
                    scenes = GetEnabledScenes(),
                    locationPathName = apkPath,
                    target = BuildTarget.Android,
                    options = BuildOptions.None
                };

                BuildReport report = BuildPipeline.BuildPlayer(options);
                if (report.summary.result == BuildResult.Succeeded)
                    Debug.Log($"[TitanOrbitBuild] Android APK build OK.\nOutput: {apkPath}");
                else
                    Debug.LogError($"[TitanOrbitBuild] Android APK build failed: {report.summary.result} — {report.summary.totalErrors} error(s). See Console / Build steps. If JDK is missing, set it under Edit → Preferences → External Tools.");
            }
            finally
            {
                EditorUserBuildSettings.buildAppBundle = previousBuildAppBundle;
            }
        }

        private static string[] GetEnabledScenes()
        {
            var scenes = new System.Collections.Generic.List<string>();
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (s.enabled)
                    scenes.Add(s.path);
            }
            return scenes.ToArray();
        }

        private static string GetWebGlOutputPath()
        {
            Directory.CreateDirectory(WebBuildFolder);
            return Path.Combine(WebBuildFolder, "TitanOrbitWebGL");
        }

        private static string GetWindowsServerOutputPath()
        {
            string root = Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
            string dir = Path.Combine(root, ServerWindowsBuildFolder);
            Directory.CreateDirectory(dir);
            // Unity expects a "file without extension" for Windows by convention.
            return Path.Combine(dir, "TitanOrbitServer");
        }

        /// <summary>Path without extension; build produces <c>TitanOrbitServer.x86_64</c> and <c>TitanOrbitServer_Data</c>.</summary>
        private static string GetLinuxServerOutputBasePath()
        {
            string root = Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
            string dir = Path.Combine(root, ServerLinuxBuildFolder);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "TitanOrbitServer");
        }

        private static string GetAndroidApkOutputPath()
        {
            string root = Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
            string dir = Path.Combine(root, AndroidApkFolder);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, AndroidApkFileName);
        }
    }
}

