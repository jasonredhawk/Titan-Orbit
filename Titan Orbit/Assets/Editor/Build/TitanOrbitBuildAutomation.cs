using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace TitanOrbit.Editor.Build
{
    /// <summary>
    /// [EDITOR] Unity menu items for Titan Orbit production builds — WebGL (Cloudflare), Windows
    /// client, Windows headless server, Linux GCE server, Linux Edgegap server, and Android APK.
    /// Centralizes output paths under BuildOutput/ (GCE) and Builds/EdgegapServer (Edgegap plugin).
    /// Not compiled into player or dedicated-server binaries.
    /// </summary>
    public static class TitanOrbitBuildAutomation
    {
        private const string WebBuildFolder = "BuildOutput/WebGL/production";
        /// <summary>Windows client (non-server) for Join→GCE smoothness checks (H64).</summary>
        private const string ClientWindowsBuildFolder = "BuildOutput/Client/windows";
        private const string ServerWindowsBuildFolder = "BuildOutput/Server/headless-windows";
        /// <summary>Folder name must stay <c>TitanOrbitLinux1</c> so <c>tools/gce/*.bat</c> defaults and VM <c>REMOTE_DIR</c> match after upload.</summary>
        private const string ServerLinuxBuildFolder = "BuildOutput/Server/TitanOrbitLinux1";
        /// <summary>Edgegap plugin default build folder; binary name <c>ServerBuild</c> matches their Dockerfile.</summary>
        private const string ServerEdgegapBuildFolder = "Builds/EdgegapServer";
        private const string AndroidApkFolder = "BuildOutput/Android";
        private const string AndroidApkFileName = "TitanOrbit.apk";

        /// <summary>
        /// [TITAN-ORBIT] Pending Linux server build request written before a platform switch.
        /// Survives domain reload so we can resume <see cref="BuildPipeline.BuildPlayer"/> after
        /// Unity recompiles with <c>UNITY_STANDALONE_LINUX_API</c> (required for IL2CPP sysroot discovery).
        /// </summary>
        const string PendingLinuxServerBuildFileName = "TitanOrbitPendingLinuxServerBuild.json";

        [MenuItem("TitanOrbit/Build/WebGL Production")]
        public static void BuildWebGLProduction()
        {
            // --- Build data ---
            BuildTarget previousTarget = EditorUserBuildSettings.activeBuildTarget;

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

            // PrepareWebGlBuild runs in IPreprocessBuildWithReport; restore Standalone/PC target after if needed.
            BuildReport report = BuildPipeline.BuildPlayer(options);
            WebGLTextureImportBuildFix.RestoreBuildTargetAfterProductionBuild(previousTarget);

            if (report.summary.result != BuildResult.Succeeded)
                Debug.LogError($"[TitanOrbitBuild] WebGL build failed: {report.summary.result} — {report.summary.totalErrors} error(s).");
        }

        /// <summary>
        /// Windows standalone <b>client</b> (Player subtarget) for Join→GCE feel tests.
        /// Output: <c>BuildOutput/Client/windows/TitanOrbit.exe</c>. Debug log writes beside the exe.
        /// </summary>
        [MenuItem("TitanOrbit/Build/Windows Client (Player)")]
        public static void BuildWindowsClient()
        {
            // --- Build data ---
            // [EDITOR] Player subtarget (not Server) — full client UI + graphics for H64 FPS test.
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ClientWindowsBuildFolder));
            Directory.CreateDirectory(folder);
            string exePath = Path.Combine(folder, "TitanOrbit.exe");

            var options = new BuildPlayerOptions
            {
                scenes = GetEnabledScenes(),
                locationPathName = exePath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
                subtarget = (int)StandaloneBuildSubtarget.Player
            };

            Debug.Log("[TitanOrbitBuild] Windows client (Player) → " + exePath);
            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result == BuildResult.Succeeded)
                Debug.Log("[TitanOrbitBuild] Windows client OK. Run TitanOrbit.exe, Join→GCE.");
            else
                Debug.LogError($"[TitanOrbitBuild] Windows client failed: {report.summary.result} — {report.summary.totalErrors} error(s).");
        }

        [MenuItem("TitanOrbit/Build/Headless Server (Windows)")]
        public static void BuildHeadlessServer()
        {
            // --- Build data ---
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
            BuildLinuxDedicatedServer(GetLinuxServerOutputBasePath(), "GCE", "tools\\gce\\deploy_server_gce.bat");
        }

        /// <summary>
        /// Linux dedicated server for Edgegap Docker (output: <c>ServerBuild.x86_64</c> under <see cref="ServerEdgegapBuildFolder"/>).
        /// Use with Tools → Edgegap Hosting or <c>tools/edgegap/Dockerfile</c>.
        /// </summary>
        [MenuItem("TitanOrbit/Build/Headless Server (Linux — Edgegap)")]
        public static void BuildHeadlessServerLinuxEdgegap()
        {
            BuildLinuxDedicatedServer(GetEdgegapServerOutputBasePath(), "Edgegap", "tools\\edgegap\\README.md");
        }

        /// <summary>
        /// Shared IL2CPP Linux Dedicated Server build used by GCE and Edgegap menu items.
        /// </summary>
        /// <remarks>
        /// [TITAN-ORBIT] After a Windows client build, the Editor active target is Windows and
        /// <c>UNITY_STANDALONE_LINUX_API</c> is undefined. Unity's sysroot packages
        /// (<c>com.unity.toolchain.win-x86_64-linux</c>, <c>com.unity.sdk.linux-x86_64</c>) only
        /// register with the Linux IL2CPP Bee pipeline when that define is present. Calling
        /// <see cref="BuildPipeline.BuildPlayer"/> for Linux while Windows is still active then
        /// fails with "No Linux sysroot found" / "No Toolchain found" until an Editor restart.
        /// We avoid that by switching the active target to Linux Dedicated Server first (which
        /// reloads scripts with the correct defines) and resuming the build after domain reload.
        /// </remarks>
        /// <param name="outputBasePath">Path without extension; Unity writes <c>.x86_64</c> + <c>_Data</c>.</param>
        /// <param name="label">Human label for console logs (GCE / Edgegap).</param>
        /// <param name="nextStepDocPath">Deploy docs path printed on success.</param>
        static void BuildLinuxDedicatedServer(string outputBasePath, string label, string nextStepDocPath)
        {
            // --- Guard: Linux module installed in this Editor ---
            // [UNITY] Hub module "Linux Dedicated Server Build Support" / Linux IL2CPP must be present.
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64))
            {
                Debug.LogError(
                    "[TitanOrbitBuild] StandaloneLinux64 is not available in this Editor. " +
                    "Install Linux Build Support (IL2CPP) / Linux Dedicated Server via Unity Hub, then retry.");
                return;
            }

            // --- Ensure active Editor platform is Linux Dedicated Server ---
            // [UNITY] SwitchActiveBuildTarget recompiles with UNITY_STANDALONE_LINUX_API so sysroot
            // packages implement UnityEditor.LinuxStandalone.Sysroot and Bee can find clang + sysroot.
            if (!IsLinuxDedicatedServerActiveTarget())
            {
                QueueLinuxServerBuildAfterPlatformSwitch(outputBasePath, label, nextStepDocPath);
                return;
            }

            ExecuteLinuxDedicatedServerBuild(outputBasePath, label, nextStepDocPath);
        }

        /// <summary>
        /// True when the Editor has already switched to Linux + Dedicated Server subtarget.
        /// </summary>
        /// <returns>True if active target/subtarget match a Linux headless server build.</returns>
        static bool IsLinuxDedicatedServerActiveTarget()
        {
            // --- Compare Editor platform state ---
            // [UNITY] activeBuildTarget is what File → Build Profiles currently has selected.
            // standaloneBuildSubtarget distinguishes Player (client) from Server (headless).
            return EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneLinux64
                   && EditorUserBuildSettings.standaloneBuildSubtarget == StandaloneBuildSubtarget.Server;
        }

        /// <summary>
        /// Saves the pending Linux build request, switches the Editor to Linux Dedicated Server,
        /// and returns. After domain reload, <see cref="ResumePendingLinuxServerBuildIfAny"/> runs the build.
        /// </summary>
        /// <param name="outputBasePath">Pending output path (no extension).</param>
        /// <param name="label">Pending log label.</param>
        /// <param name="nextStepDocPath">Pending deploy-docs path.</param>
        static void QueueLinuxServerBuildAfterPlatformSwitch(string outputBasePath, string label, string nextStepDocPath)
        {
            // --- Persist request across domain reload ---
            // [STANDARD] SwitchActiveBuildTarget reloads assemblies; static locals die. Temp JSON survives.
            var pending = new PendingLinuxServerBuild
            {
                outputBasePath = outputBasePath,
                label = label,
                nextStepDocPath = nextStepDocPath
            };

            try
            {
                File.WriteAllText(GetPendingLinuxServerBuildPath(), JsonUtility.ToJson(pending));
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    "[TitanOrbitBuild] Could not write pending Linux server build state. " +
                    "Switch to Linux Dedicated Server in Build Profiles manually, then retry.\n" + ex);
                return;
            }

            Debug.Log(
                "[TitanOrbitBuild] Active Editor target is not Linux Dedicated Server " +
                $"(now: {EditorUserBuildSettings.activeBuildTarget} / {EditorUserBuildSettings.standaloneBuildSubtarget}). " +
                "Switching platform so IL2CPP Linux sysroot packages register, then resuming the " +
                label + " server build after scripts recompile. No Unity restart needed.");

            // --- Switch platform (triggers domain reload) ---
            // [UNITY] Set Server subtarget before Switch so Dedicated Server scripting defines apply.
            EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Server;
            bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(
                NamedBuildTarget.Server,
                BuildTarget.StandaloneLinux64);

            if (!switched)
            {
                ClearPendingLinuxServerBuild();
                Debug.LogError(
                    "[TitanOrbitBuild] SwitchActiveBuildTarget to Linux Dedicated Server failed. " +
                    "Open File → Build Profiles, select Linux Dedicated Server, Switch Platform, then retry the menu item.");
            }
        }

        /// <summary>
        /// After every domain reload, resume a queued Linux server build if a pending request file exists.
        /// </summary>
        [InitializeOnLoadMethod]
        static void ResumePendingLinuxServerBuildIfAny()
        {
            // --- Schedule after Editor settles ---
            // [UNITY] delayCall runs once the Editor is idle post-reload (imports/compiles finished).
            // Building inside InitializeOnLoad itself is too early — platform modules may still be settling.
            EditorApplication.delayCall += TryExecutePendingLinuxServerBuild;
        }

        /// <summary>
        /// Loads and clears the pending Linux build file, then runs <see cref="ExecuteLinuxDedicatedServerBuild"/> if valid.
        /// </summary>
        static void TryExecutePendingLinuxServerBuild()
        {
            // --- Load pending request (if any) ---
            string path = GetPendingLinuxServerBuildPath();
            if (!File.Exists(path))
                return;

            PendingLinuxServerBuild pending;
            try
            {
                pending = JsonUtility.FromJson<PendingLinuxServerBuild>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                ClearPendingLinuxServerBuild();
                Debug.LogError("[TitanOrbitBuild] Corrupt pending Linux server build file; deleted. Retry the menu item.\n" + ex);
                return;
            }

            // Clear before BuildPlayer so a failed build cannot loop on every subsequent domain reload.
            ClearPendingLinuxServerBuild();

            if (pending == null || string.IsNullOrEmpty(pending.outputBasePath))
            {
                Debug.LogError("[TitanOrbitBuild] Pending Linux server build was empty; retry the menu item.");
                return;
            }

            // --- Verify platform switch stuck ---
            if (!IsLinuxDedicatedServerActiveTarget())
            {
                Debug.LogError(
                    "[TitanOrbitBuild] Expected Linux Dedicated Server after platform switch, but Editor is still " +
                    $"{EditorUserBuildSettings.activeBuildTarget} / {EditorUserBuildSettings.standaloneBuildSubtarget}. " +
                    "Open File → Build Profiles → Linux Dedicated Server → Switch Platform, then retry.");
                return;
            }

            Debug.Log($"[TitanOrbitBuild] Resuming queued Linux server build ({pending.label}) after platform switch.");
            ExecuteLinuxDedicatedServerBuild(pending.outputBasePath, pending.label, pending.nextStepDocPath);
        }

        /// <summary>
        /// Runs the actual IL2CPP Linux Dedicated Server <see cref="BuildPipeline.BuildPlayer"/> call.
        /// Caller must already have Linux Dedicated Server as the active Editor target.
        /// </summary>
        /// <param name="outputBasePath">Path without extension.</param>
        /// <param name="label">Log label (GCE / Edgegap).</param>
        /// <param name="nextStepDocPath">Deploy docs path on success.</param>
        static void ExecuteLinuxDedicatedServerBuild(string outputBasePath, string label, string nextStepDocPath)
        {
            // --- Scripting backend ---
            // GCE Debian images often fail to load MonoBleedingEdge native libs ("Unable to load mono library" / exit 1).
            // Dedicated Server player target supports IL2CPP — no Mono .so chain on the VM/container.
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Server, ScriptingImplementation.IL2CPP);
            Debug.Log("[TitanOrbitBuild] Dedicated Server scripting backend set to IL2CPP for this Linux server build (" + label + ").");

            // --- BuildPlayer ---
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
                Debug.Log($"[TitanOrbitBuild] Linux server build OK ({label}). Next: {nextStepDocPath}\nOutput folder: {folder}");
            }
            else
            {
                Debug.LogError(
                    $"[TitanOrbitBuild] Linux server build failed ({label}): {report.summary.result} — " +
                    $"{report.summary.totalErrors} error(s). See Console / Build steps. " +
                    "If errors mention missing Linux sysroot/toolchain packages, confirm " +
                    "com.unity.toolchain.win-x86_64-linux and com.unity.sdk.linux-x86_64 are in Packages/manifest.json, " +
                    "then use File → Build Profiles → Linux Dedicated Server → Switch Platform and retry.");
            }
        }

        /// <summary>Absolute path to the Temp JSON that queues a Linux server build across domain reload.</summary>
        /// <returns>Full path under the project Temp folder.</returns>
        static string GetPendingLinuxServerBuildPath()
        {
            // --- Resolve Temp path ---
            // [UNITY] Application.dataPath is …/Assets; parent is the project root.
            string projectRoot = Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
            string tempDir = Path.Combine(projectRoot, "Temp");
            Directory.CreateDirectory(tempDir);
            return Path.Combine(tempDir, PendingLinuxServerBuildFileName);
        }

        /// <summary>Deletes the pending Linux server build request file if it exists.</summary>
        static void ClearPendingLinuxServerBuild()
        {
            // --- Cleanup ---
            string path = GetPendingLinuxServerBuildPath();
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TitanOrbitBuild] Could not delete pending Linux server build file: " + ex.Message);
            }
        }

        /// <summary>Android player APK at <c>BuildOutput/Android/TitanOrbit.apk</c>. Requires Android Build Support and JDK in Edit → Preferences → External Tools.</summary>
        [MenuItem("TitanOrbit/Build/Android APK")]
        public static void BuildAndroidApk()
        {
            // --- Build data ---
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
            // --- Compute value ---
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
            // --- Compute value ---
            string root = Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
            string dir = Path.Combine(root, ServerWindowsBuildFolder);
            Directory.CreateDirectory(dir);
            // Unity expects a "file without extension" for Windows by convention.
            return Path.Combine(dir, "TitanOrbitServer");
        }

        /// <summary>Path without extension; build produces <c>TitanOrbitServer.x86_64</c> and <c>TitanOrbitServer_Data</c>.</summary>
        private static string GetLinuxServerOutputBasePath()
        {
            return GetLinuxServerOutputBasePath(ServerLinuxBuildFolder, "TitanOrbitServer");
        }

        /// <summary>Edgegap plugin expects <c>Builds/EdgegapServer/ServerBuild</c> (+ <c>_Data</c>, <c>.x86_64</c>).</summary>
        private static string GetEdgegapServerOutputBasePath()
        {
            return GetLinuxServerOutputBasePath(ServerEdgegapBuildFolder, "ServerBuild");
        }

        static string GetLinuxServerOutputBasePath(string folderRelativeToProject, string binaryBaseName)
        {
            string root = Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
            string dir = Path.Combine(root, folderRelativeToProject);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, binaryBaseName);
        }

        private static string GetAndroidApkOutputPath()
        {
            // --- Compute value ---
            string root = Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
            string dir = Path.Combine(root, AndroidApkFolder);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, AndroidApkFileName);
        }

        /// <summary>
        /// Serializable request for a Linux Dedicated Server build that must survive domain reload
        /// after <see cref="EditorUserBuildSettings.SwitchActiveBuildTarget"/>.
        /// </summary>
        [Serializable]
        class PendingLinuxServerBuild
        {
            /// <summary>Output path without extension (Unity appends <c>.x86_64</c>).</summary>
            public string outputBasePath;

            /// <summary>Console label (GCE / Edgegap).</summary>
            public string label;

            /// <summary>Docs/script path printed after a successful build.</summary>
            public string nextStepDocPath;
        }
    }
}
