using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TitanOrbit.Editor.Build
{
    /// <summary>
    /// [EDITOR] Unity menu items for Titan Orbit production builds — WebGL (Cloudflare), Windows
    /// client, Windows headless server, Linux GCE server, Linux Edgegap server, and Android APK.
    /// Centralizes output paths under BuildOutput/ (GCE) and Builds/EdgegapServer (Edgegap plugin).
    /// Day-to-day GCE publish with Unity open: <see cref="BuildHeadlessServerLinuxAndDeploy"/>.
    /// Closed-Editor / CI: <see cref="BuildHeadlessServerLinuxBatchMode"/> via
    /// <c>tools/gce/build_and_deploy_server_gce.bat</c>.
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
        /// <summary>
        /// Staging copy used when deploying while the Editor stays open.
        /// Leaf name stays <c>TitanOrbitLinux1</c> so GCE extract paths / systemd layout match production.
        /// </summary>
        private const string ServerLinuxDeployStagingFolder = "BuildOutput/Server/deploy-staging/TitanOrbitLinux1";
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

        /// <summary>
        /// WebGL production client for Cloudflare. Output under <see cref="WebBuildFolder"/>.
        /// <para>
        /// [TITAN-ORBIT] After a Linux Dedicated Server build the Editor stays on Linux Server.
        /// Building WebGL without switching first can bake EntityScenes / SubScenes with Server
        /// defines (same class of late-join / TeamChoice Crash!!! as a contaminated Windows client).
        /// We switch to WebGL first and resume BuildPlayer after domain reload when needed —
        /// same pattern as <see cref="BuildWindowsClient"/>.
        /// </para>
        /// </summary>
        [MenuItem("TitanOrbit/Build/WebGL Production")]
        public static void BuildWebGLProduction()
        {
            // --- Ensure active Editor platform is WebGL (not leftover Linux Server) ---
            if (!IsWebGlActiveTarget())
            {
                QueueWebGlBuildAfterPlatformSwitch();
                return;
            }

            ExecuteWebGlProductionBuild(restoreTargetAfter: null);
        }

        /// <summary>True when the Editor has already switched to WebGL.</summary>
        static bool IsWebGlActiveTarget()
        {
            return EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL;
        }

        /// <summary>
        /// Saves a pending WebGL build request (plus optional restore target), switches to WebGL,
        /// and returns. <see cref="ResumePendingWebGlBuildIfAny"/> runs BuildPlayer after reload.
        /// </summary>
        static void QueueWebGlBuildAfterPlatformSwitch()
        {
            // --- Persist request + prior target across domain reload ---
            // [STANDARD] SwitchActiveBuildTarget reloads assemblies; static locals die. Temp JSON survives.
            var pending = new PendingWebGlBuild
            {
                requested = true,
                previousTarget = (int)EditorUserBuildSettings.activeBuildTarget,
                previousSubtarget = (int)EditorUserBuildSettings.standaloneBuildSubtarget
            };

            try
            {
                File.WriteAllText(GetPendingWebGlBuildPath(), JsonUtility.ToJson(pending));
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    "[TitanOrbitBuild] Could not write pending WebGL build state. " +
                    "Switch to WebGL in Build Profiles manually, then retry.\n" + ex);
                return;
            }

            Debug.Log(
                "[TitanOrbitBuild] Active Editor target is not WebGL " +
                $"(now: {EditorUserBuildSettings.activeBuildTarget} / {EditorUserBuildSettings.standaloneBuildSubtarget}). " +
                "Switching platform so EntityScenes bake for the WebGL client, then resuming the " +
                "WebGL production build after scripts recompile.");

            // --- Switch platform (triggers domain reload) ---
            // [UNITY] WebGL is a client Player target — not Dedicated Server — so UNITY_SERVER
            // is not defined during SubScene / EntityScene bake.
            bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(
                NamedBuildTarget.WebGL,
                BuildTarget.WebGL);

            if (!switched)
            {
                ClearPendingWebGlBuild();
                Debug.LogError(
                    "[TitanOrbitBuild] SwitchActiveBuildTarget to WebGL failed. " +
                    "Open File → Build Profiles, select WebGL, Switch Platform, then retry.");
            }
        }

        /// <summary>
        /// After every domain reload, resume a queued WebGL production build if a pending request exists.
        /// </summary>
        [InitializeOnLoadMethod]
        static void ResumePendingWebGlBuildIfAny()
        {
            EditorApplication.delayCall += TryExecutePendingWebGlBuild;
        }

        /// <summary>
        /// Loads and clears the pending WebGL build file, then runs the WebGL BuildPlayer.
        /// </summary>
        static void TryExecutePendingWebGlBuild()
        {
            string path = GetPendingWebGlBuildPath();
            if (!File.Exists(path))
                return;

            PendingWebGlBuild pending = null;
            try
            {
                pending = JsonUtility.FromJson<PendingWebGlBuild>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                ClearPendingWebGlBuild();
                Debug.LogError("[TitanOrbitBuild] Corrupt pending WebGL build file; deleted. Retry the menu item.\n" + ex);
                return;
            }

            ClearPendingWebGlBuild();

            if (pending == null || !pending.requested)
            {
                Debug.LogError("[TitanOrbitBuild] Pending WebGL build was empty; retry the menu item.");
                return;
            }

            if (!IsWebGlActiveTarget())
            {
                Debug.LogError(
                    "[TitanOrbitBuild] Expected WebGL after platform switch, but Editor is still " +
                    $"{EditorUserBuildSettings.activeBuildTarget} / {EditorUserBuildSettings.standaloneBuildSubtarget}. " +
                    "Open File → Build Profiles → WebGL → Switch Platform, then retry.");
                return;
            }

            // --- Optional restore target after BuildPlayer ---
            // [TITAN-ORBIT] Never restore back to Dedicated Server / Linux Server — that re-contaminates
            // the next client bake. Only restore when the prior target was a normal client Player.
            BuildTarget? restoreAfter = null;
            var previousTarget = (BuildTarget)pending.previousTarget;
            var previousSub = (StandaloneBuildSubtarget)pending.previousSubtarget;
            if (previousTarget != BuildTarget.WebGL &&
                previousSub != StandaloneBuildSubtarget.Server &&
                previousTarget != BuildTarget.StandaloneLinux64)
            {
                restoreAfter = previousTarget;
            }

            Debug.Log("[TitanOrbitBuild] Resuming queued WebGL production build after platform switch.");
            ExecuteWebGlProductionBuild(restoreAfter);
        }

        /// <summary>
        /// Runs WebGL <see cref="BuildPipeline.BuildPlayer"/> with DXT texture subtarget.
        /// Caller must already have WebGL as the active Editor target so SubScenes bake correctly.
        /// </summary>
        /// <param name="restoreTargetAfter">
        /// Optional Editor target to restore after BuildPlayer (e.g. Windows Player). Null leaves
        /// WebGL active — preferred after switching away from Linux Dedicated Server.
        /// </param>
        static void ExecuteWebGlProductionBuild(BuildTarget? restoreTargetAfter)
        {
            // --- WebGL player settings that prevent startup OOB after deploy ---
            // [TITAN-ORBIT] Hashed Build/* names: IndexedDB / CDN cannot mix old .data with new .wasm.
            // Data caching OFF: stack traces with IndexedDB transaction.oncomplete → _main OOB were
            // from UnityCache replaying a corrupt prior download (Content-Encoding mishap).
            //
            // Memory budget (Chrome console [WebGLBoot] 2026-08-09):
            //   SystemInfo.systemMemorySize reported **360 MB** on WebGLPlayer, then Crash!!!
            //   "memory access out of bounds" at Module._main BEFORE BeforeSceneLoad.
            //   256 MiB initial heap left almost no room for ECS/NetCode boot inside that budget.
            //   128 MiB initial + geometric growth + 8 MiB stack. Keep decompressionFallback ON so
            //   Build/* stay *.unityweb (GCS deploy / Content-Encoding:br pipeline).
            PlayerSettings.WebGL.nameFilesAsHashes = true;
            PlayerSettings.WebGL.dataCaching = false;
            PlayerSettings.WebGL.initialMemorySize = 128;
            PlayerSettings.WebGL.maximumMemorySize = 2048;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.emscriptenArgs = "-sSTACK_SIZE=8388608";
            // [TITAN-ORBIT] App UI ships via com.unity.ai.inference. Standalone strips it with
            // APP_UI_EDITOR_ONLY; WebGL must match (smaller player + no InitializeInPlayer at boot).
            EnsureWebGlScriptingDefine("APP_UI_EDITOR_ONLY");
            // [TITAN-ORBIT] Do NOT set HYBRID_RENDERER_DISABLED on WebGL — BuildPlayer then hits
            // EntitiesGraphicsSystemUtility.RootsHandlerDelegate NRE (registeredAssets) ×N during
            // EntitiesAssetGC, which can corrupt SubScene/UnityObjectRef bake. Runtime instead
            // filters Unity.Rendering.* out of CreateClientWorld (see TitanOrbitBootstrap).
            RemoveWebGlScriptingDefine("HYBRID_RENDERER_DISABLED");

            // --- Stamp bundleVersion so any leftover cache key still misses ---
            // [UNITY] companyName+productName+productVersion participate in UnityCache identity.
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd.HHmm");
            PlayerSettings.bundleVersion = stamp;
            Debug.Log("[TitanOrbitBuild] WebGL PlayerSettings: nameFilesAsHashes=true dataCaching=false " +
                      "initialMemorySize=128 stack=8MiB APP_UI_EDITOR_ONLY bundleVersion=" + stamp);

            // --- Wipe prior output so stale Build/* cannot ship beside the new index ---
            CleanWebGlOutputFolder();

            // --- Build data ---
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

            Debug.Log(
                "[TitanOrbitBuild] WebGL production build: texture subtarget=DXT (desktop browsers), " +
                "nameFilesAsHashes=true (IndexedDB cache-bust).");

            // PrepareWebGlBuild runs in IPreprocessBuildWithReport.
            BuildReport report = BuildPipeline.BuildPlayer(options);

            if (restoreTargetAfter.HasValue)
                WebGLTextureImportBuildFix.RestoreBuildTargetAfterProductionBuild(restoreTargetAfter.Value);
            else if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL)
                Debug.Log("[TitanOrbitBuild] Leaving Editor on WebGL (did not restore Dedicated Server / Linux).");

            if (report.summary.result == BuildResult.Succeeded)
            {
                Debug.Log(
                    "[TitanOrbitBuild] WebGL production OK → " + GetWebGlOutputPath() +
                    "\nNext: tools/gcs/deploy_webgl_gcs.bat → purge Cloudflare → clear browser site data once " +
                    "(hashed Build/* names prevent future IndexedDB mix-ups).");
            }
            else
                Debug.LogError($"[TitanOrbitBuild] WebGL build failed: {report.summary.result} — {report.summary.totalErrors} error(s).");
        }

        /// <summary>
        /// Deletes <see cref="WebBuildFolder"/> so a new WebGL build cannot leave orphan
        /// <c>Build/*.unityweb</c> files next to a fresh <c>index.html</c> (mixed-artifact crash).
        /// </summary>
        static void CleanWebGlOutputFolder()
        {
            // --- Resolve absolute path (Editor cwd = project root parent of Assets) ---
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", WebBuildFolder));
            if (!Directory.Exists(root))
                return;

            Debug.Log("[TitanOrbitBuild] Cleaning prior WebGL output → " + root);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[TitanOrbitBuild] Could not fully delete prior WebGL output (file locked?). " +
                    "Close any local server using that folder and retry.\n" + ex.Message);
            }

            Directory.CreateDirectory(root);
        }

        /// <summary>
        /// Ensures a scripting define is present for the WebGL named build target.
        /// Used to strip editor-only packages (e.g. App UI) from the browser player.
        /// </summary>
        /// <param name="define">Define symbol to add if missing (e.g. <c>APP_UI_EDITOR_ONLY</c>).</param>
        static void EnsureWebGlScriptingDefine(string define)
        {
            // --- Read current WebGL defines ---
            // [UNITY] NamedBuildTarget.WebGL — same store as ProjectSettings scriptingDefineSymbols.WebGL.
            string current = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.WebGL);
            if (string.IsNullOrEmpty(current))
            {
                PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.WebGL, define);
                Debug.Log("[TitanOrbitBuild] WebGL scripting defines → " + define);
                return;
            }

            // --- Already present? ---
            foreach (string part in current.Split(';'))
            {
                if (string.Equals(part.Trim(), define, StringComparison.Ordinal))
                    return;
            }

            // --- Append ---
            string next = current.TrimEnd(';') + ";" + define;
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.WebGL, next);
            Debug.Log("[TitanOrbitBuild] WebGL scripting defines → " + next);
        }


        /// <summary>
        /// Removes a scripting define from the WebGL named build target if present.
        /// </summary>
        /// <param name="define">Define symbol to remove (e.g. <c>HYBRID_RENDERER_DISABLED</c>).</param>
        static void RemoveWebGlScriptingDefine(string define)
        {
            // --- Read current WebGL defines ---
            string current = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.WebGL);
            if (string.IsNullOrEmpty(current))
                return;

            // --- Rebuild list without the target define ---
            var parts = new List<string>();
            bool removed = false;
            foreach (string part in current.Split(';'))
            {
                string trimmed = part.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;
                if (string.Equals(trimmed, define, StringComparison.Ordinal))
                {
                    removed = true;
                    continue;
                }
                parts.Add(trimmed);
            }

            if (!removed)
                return;

            string next = string.Join(";", parts);
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.WebGL, next);
            Debug.Log("[TitanOrbitBuild] WebGL scripting defines removed " + define + " → " + next);
        }

        static string GetPendingWebGlBuildPath()
        {
            return Path.Combine(Path.GetTempPath(), "TitanOrbitPendingWebGlBuild.json");
        }

        static void ClearPendingWebGlBuild()
        {
            string path = GetPendingWebGlBuildPath();
            if (File.Exists(path))
                File.Delete(path);
        }

        /// <summary>
        /// Windows standalone <b>client</b> (Player subtarget) for Join→GCE feel tests.
        /// Output: <c>BuildOutput/Client/windows/TitanOrbit.exe</c>. Debug log writes beside the exe.
        /// <para>
        /// [TITAN-ORBIT] After a Linux Dedicated Server build the Editor stays on Linux Server.
        /// Building the Windows client without switching first can bake EntityScenes / SubScenes
        /// with the wrong platform (Server defines, missing client Pending components) and produce
        /// a player that late-joins badly. We switch to Windows Player first (same pattern as the
        /// Linux server build) and resume BuildPlayer after domain reload when needed.
        /// </para>
        /// </summary>
        [MenuItem("TitanOrbit/Build/Windows Client (Player)")]
        public static void BuildWindowsClient()
        {
            // --- Ensure active Editor platform is Windows Player (not leftover Linux Server) ---
            if (!IsWindowsClientPlayerActiveTarget())
            {
                QueueWindowsClientBuildAfterPlatformSwitch();
                return;
            }

            ExecuteWindowsClientBuild();
        }

        /// <summary>
        /// True when the Editor is already on Windows standalone Player (client) subtarget.
        /// </summary>
        static bool IsWindowsClientPlayerActiveTarget()
        {
            return EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64
                   && EditorUserBuildSettings.standaloneBuildSubtarget == StandaloneBuildSubtarget.Player;
        }

        /// <summary>
        /// Writes a pending Windows client build marker, switches to Windows Player, and returns.
        /// <see cref="ResumePendingWindowsClientBuildIfAny"/> runs BuildPlayer after reload.
        /// </summary>
        static void QueueWindowsClientBuildAfterPlatformSwitch()
        {
            // --- Persist request across domain reload ---
            string path = GetPendingWindowsClientBuildPath();
            try
            {
                File.WriteAllText(path, JsonUtility.ToJson(new PendingWindowsClientBuild { requested = true }));
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    "[TitanOrbitBuild] Could not write pending Windows client build state. " +
                    "Switch to Windows Player in Build Profiles manually, then retry.\n" + ex);
                return;
            }

            Debug.Log(
                "[TitanOrbitBuild] Active Editor target is not Windows Player " +
                $"(now: {EditorUserBuildSettings.activeBuildTarget} / {EditorUserBuildSettings.standaloneBuildSubtarget}). " +
                "Switching platform so EntityScenes bake for the client, then resuming the " +
                "Windows client build after scripts recompile.");

            // --- Switch platform (triggers domain reload) ---
            // [UNITY] Player subtarget — not Server — so UNITY_SERVER is not defined during bake.
            EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
            bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(
                NamedBuildTarget.Standalone,
                BuildTarget.StandaloneWindows64);

            if (!switched)
            {
                ClearPendingWindowsClientBuild();
                Debug.LogError(
                    "[TitanOrbitBuild] SwitchActiveBuildTarget to Windows Player failed. " +
                    "Open File → Build Profiles, select Windows Player, Switch Platform, then retry.");
            }
        }

        /// <summary>
        /// After every domain reload, resume a queued Windows client build if a pending request exists.
        /// </summary>
        [InitializeOnLoadMethod]
        static void ResumePendingWindowsClientBuildIfAny()
        {
            EditorApplication.delayCall += TryExecutePendingWindowsClientBuild;
        }

        /// <summary>
        /// Loads and clears the pending Windows client build file, then runs the client BuildPlayer.
        /// </summary>
        static void TryExecutePendingWindowsClientBuild()
        {
            string path = GetPendingWindowsClientBuildPath();
            if (!File.Exists(path))
                return;

            ClearPendingWindowsClientBuild();

            if (!IsWindowsClientPlayerActiveTarget())
            {
                Debug.LogError(
                    "[TitanOrbitBuild] Expected Windows Player after platform switch, but Editor is still " +
                    $"{EditorUserBuildSettings.activeBuildTarget} / {EditorUserBuildSettings.standaloneBuildSubtarget}. " +
                    "Open File → Build Profiles → Windows → Player → Switch Platform, then retry.");
                return;
            }

            Debug.Log("[TitanOrbitBuild] Resuming queued Windows client build after platform switch.");
            ExecuteWindowsClientBuild();
        }

        /// <summary>
        /// Runs <see cref="BuildPipeline.BuildPlayer"/> for the Windows client. Caller must already
        /// have Windows Player as the active Editor target so SubScenes bake correctly.
        /// </summary>
        static void ExecuteWindowsClientBuild()
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

        static string GetPendingWindowsClientBuildPath()
        {
            return Path.Combine(Path.GetTempPath(), "TitanOrbitPendingWindowsClientBuild.json");
        }

        static void ClearPendingWindowsClientBuild()
        {
            string path = GetPendingWindowsClientBuildPath();
            if (File.Exists(path))
                File.Delete(path);
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
        [MenuItem("TitanOrbit/Build/Headless Server (Linux — Google Cloud)", false, 50)]
        public static void BuildHeadlessServerLinux()
        {
            // --- Interactive Editor menu (build only) ---
            // [TITAN-ORBIT] Menu path keeps the Editor open after build so you can inspect Console output.
            BuildLinuxDedicatedServer(
                GetLinuxServerOutputBasePath(),
                "GCE",
                "tools\\gce\\deploy_server_gce.bat",
                exitEditorWhenDone: false,
                deployToGceAfterSuccess: false);
        }

        /// <summary>
        /// Fast day-to-day path: build the Linux GCE server in the already-open Editor, then launch
        /// <c>deploy_server_gce.bat freeDisk useGcs</c> against a staging copy of the output.
        /// Prefer this over <c>build_and_deploy_server_gce.bat</c> when Unity is already open —
        /// batchmode must close/reopen the project and is much slower.
        /// </summary>
        [MenuItem("TitanOrbit/Build/Headless Server (Linux — Google Cloud) + Deploy", false, 51)]
        public static void BuildHeadlessServerLinuxAndDeploy()
        {
            // --- Interactive Editor menu (build + deploy, Unity stays open) ---
            // [TITAN-ORBIT] Deploy tars a staging copy so upload does not race Editor file locks on
            // the live BuildOutput folder (truncated IL2CPP metadata was a real failure mode).
            Debug.Log(
                "[TitanOrbitBuild] Linux GCE build + deploy starting (Editor stays open). " +
                "After BuildPlayer succeeds, a console window runs deploy_server_gce.bat freeDisk useGcs.");
            BuildLinuxDedicatedServer(
                GetLinuxServerOutputBasePath(),
                "GCE",
                "tools\\gce\\deploy_server_gce.bat",
                exitEditorWhenDone: false,
                deployToGceAfterSuccess: true);
        }

        /// <summary>
        /// CLI / PowerShell entry point for a Linux GCE dedicated-server build.
        /// Invoked by <c>tools/gce/build_and_deploy_server_gce.bat</c> via Unity
        /// <c>-batchmode -executeMethod …BuildHeadlessServerLinuxBatchMode</c> (no <c>-quit</c> —
        /// this method calls <see cref="EditorApplication.Exit"/> so a platform-switch + domain
        /// reload can finish the build first).
        /// </summary>
        /// <remarks>
        /// [UNITY] Do not pass <c>-quit</c> on the command line for this method: if the Editor must
        /// switch to Linux Dedicated Server first, <c>-quit</c> would exit before the deferred
        /// <see cref="BuildPipeline.BuildPlayer"/> runs after domain reload.
        /// Prefer <see cref="BuildHeadlessServerLinuxAndDeploy"/> when the Editor is already open.
        /// </remarks>
        public static void BuildHeadlessServerLinuxBatchMode()
        {
            // --- Batchmode build (PowerShell / CI when Editor is closed) ---
            // [TITAN-ORBIT] Same output folder as the Google Cloud menu item so deploy scripts find it.
            Debug.Log("[TitanOrbitBuild] Batchmode Linux GCE server build starting (exit Editor when done).");
            BuildLinuxDedicatedServer(
                GetLinuxServerOutputBasePath(),
                "GCE",
                "tools\\gce\\build_and_deploy_server_gce.bat",
                exitEditorWhenDone: true,
                deployToGceAfterSuccess: false);
        }

        /// <summary>
        /// Linux dedicated server for Edgegap Docker (output: <c>ServerBuild.x86_64</c> under <see cref="ServerEdgegapBuildFolder"/>).
        /// Use with Tools → Edgegap Hosting or <c>tools/edgegap/Dockerfile</c>.
        /// </summary>
        [MenuItem("TitanOrbit/Build/Headless Server (Linux — Edgegap)", false, 60)]
        public static void BuildHeadlessServerLinuxEdgegap()
        {
            BuildLinuxDedicatedServer(
                GetEdgegapServerOutputBasePath(),
                "Edgegap",
                "tools\\edgegap\\README.md",
                exitEditorWhenDone: false,
                deployToGceAfterSuccess: false);
        }

        /// <summary>
        /// Shared IL2CPP Linux Dedicated Server build used by GCE and Edgegap menu items, and by
        /// the batchmode PowerShell pipeline.
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
        /// <param name="exitEditorWhenDone">
        /// When true (batchmode CLI), call <see cref="EditorApplication.Exit"/> with 0/1 after the
        /// build finishes — including after a deferred build that resumes post platform-switch.
        /// </param>
        /// <param name="deployToGceAfterSuccess">
        /// When true (Editor + Deploy menu), copy the build to staging and launch
        /// <c>deploy_server_gce.bat freeDisk useGcs</c> without closing Unity.
        /// </param>
        static void BuildLinuxDedicatedServer(
            string outputBasePath,
            string label,
            string nextStepDocPath,
            bool exitEditorWhenDone,
            bool deployToGceAfterSuccess)
        {
            // --- Guard: Linux module installed in this Editor ---
            // [UNITY] Hub module "Linux Dedicated Server Build Support" / Linux IL2CPP must be present.
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64))
            {
                Debug.LogError(
                    "[TitanOrbitBuild] StandaloneLinux64 is not available in this Editor. " +
                    "Install Linux Build Support (IL2CPP) / Linux Dedicated Server via Unity Hub, then retry.");
                ExitEditorIfRequested(exitEditorWhenDone, exitCode: 1);
                return;
            }

            // --- Ensure active Editor platform is Linux Dedicated Server ---
            // [UNITY] SwitchActiveBuildTarget recompiles with UNITY_STANDALONE_LINUX_API so sysroot
            // packages implement UnityEditor.LinuxStandalone.Sysroot and Bee can find clang + sysroot.
            if (!IsLinuxDedicatedServerActiveTarget())
            {
                // Do not Exit here — domain reload must complete, then ResumePending builds + exits.
                QueueLinuxServerBuildAfterPlatformSwitch(
                    outputBasePath,
                    label,
                    nextStepDocPath,
                    exitEditorWhenDone,
                    deployToGceAfterSuccess);
                return;
            }

            // --- Already on Linux Dedicated Server: build now ---
            bool ok = ExecuteLinuxDedicatedServerBuild(outputBasePath, label, nextStepDocPath);
            if (ok && deployToGceAfterSuccess)
                StartGceDeployFromEditorBuildFolder(Path.GetDirectoryName(outputBasePath) ?? outputBasePath);
            ExitEditorIfRequested(exitEditorWhenDone, exitCode: ok ? 0 : 1);
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
        /// <param name="exitEditorWhenDone">
        /// Persisted into the pending JSON so the post-reload resume can still exit batchmode Unity.
        /// </param>
        /// <param name="deployToGceAfterSuccess">
        /// Persisted so + Deploy still launches <c>deploy_server_gce.bat</c> after a platform-switch resume.
        /// </param>
        static void QueueLinuxServerBuildAfterPlatformSwitch(
            string outputBasePath,
            string label,
            string nextStepDocPath,
            bool exitEditorWhenDone,
            bool deployToGceAfterSuccess)
        {
            // --- Persist request across domain reload ---
            // [STANDARD] SwitchActiveBuildTarget reloads assemblies; static locals die. Temp JSON survives.
            var pending = new PendingLinuxServerBuild
            {
                outputBasePath = outputBasePath,
                label = label,
                nextStepDocPath = nextStepDocPath,
                exitEditorWhenDone = exitEditorWhenDone,
                deployToGceAfterSuccess = deployToGceAfterSuccess
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
                ExitEditorIfRequested(exitEditorWhenDone, exitCode: 1);
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
                ExitEditorIfRequested(exitEditorWhenDone, exitCode: 1);
            }
            // Success path: return without Exit — InitializeOnLoad resume builds after reload.
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
        /// In batchmode, exits the Editor with 0/1 when the pending request asked for it.
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
                // Cannot know exitEditorWhenDone if JSON was corrupt — only exit when already in batchmode.
                ExitEditorIfRequested(Application.isBatchMode, exitCode: 1);
                return;
            }

            // Clear before BuildPlayer so a failed build cannot loop on every subsequent domain reload.
            bool exitEditorWhenDone = pending != null && pending.exitEditorWhenDone;
            bool deployToGceAfterSuccess = pending != null && pending.deployToGceAfterSuccess;
            ClearPendingLinuxServerBuild();

            if (pending == null || string.IsNullOrEmpty(pending.outputBasePath))
            {
                Debug.LogError("[TitanOrbitBuild] Pending Linux server build was empty; retry the menu item.");
                ExitEditorIfRequested(exitEditorWhenDone, exitCode: 1);
                return;
            }

            // --- Verify platform switch stuck ---
            if (!IsLinuxDedicatedServerActiveTarget())
            {
                Debug.LogError(
                    "[TitanOrbitBuild] Expected Linux Dedicated Server after platform switch, but Editor is still " +
                    $"{EditorUserBuildSettings.activeBuildTarget} / {EditorUserBuildSettings.standaloneBuildSubtarget}. " +
                    "Open File → Build Profiles → Linux Dedicated Server → Switch Platform, then retry.");
                ExitEditorIfRequested(exitEditorWhenDone, exitCode: 1);
                return;
            }

            Debug.Log($"[TitanOrbitBuild] Resuming queued Linux server build ({pending.label}) after platform switch.");
            bool ok = ExecuteLinuxDedicatedServerBuild(pending.outputBasePath, pending.label, pending.nextStepDocPath);
            if (ok && deployToGceAfterSuccess)
                StartGceDeployFromEditorBuildFolder(Path.GetDirectoryName(pending.outputBasePath) ?? pending.outputBasePath);
            ExitEditorIfRequested(exitEditorWhenDone, exitCode: ok ? 0 : 1);
        }

        /// <summary>
        /// Runs the actual IL2CPP Linux Dedicated Server <see cref="BuildPipeline.BuildPlayer"/> call.
        /// Caller must already have Linux Dedicated Server as the active Editor target.
        /// </summary>
        /// <param name="outputBasePath">Path without extension.</param>
        /// <param name="label">Log label (GCE / Edgegap).</param>
        /// <param name="nextStepDocPath">Deploy docs path on success.</param>
        /// <returns>True when <see cref="BuildResult.Succeeded"/>.</returns>
        static bool ExecuteLinuxDedicatedServerBuild(string outputBasePath, string label, string nextStepDocPath)
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
                return true;
            }

            Debug.LogError(
                $"[TitanOrbitBuild] Linux server build failed ({label}): {report.summary.result} — " +
                $"{report.summary.totalErrors} error(s). See Console / Build steps. " +
                "If errors mention missing Linux sysroot/toolchain packages, confirm " +
                "com.unity.toolchain.win-x86_64-linux and com.unity.sdk.linux-x86_64 are in Packages/manifest.json, " +
                "then use File → Build Profiles → Linux Dedicated Server → Switch Platform and retry.");
            return false;
        }

        /// <summary>
        /// In batchmode CLI builds, quit Unity with a process exit code so PowerShell can chain deploy.
        /// Interactive Editor menu builds never set <paramref name="exitEditorWhenDone"/>, so this is a no-op there.
        /// </summary>
        /// <param name="exitEditorWhenDone">True only for <see cref="BuildHeadlessServerLinuxBatchMode"/> pipeline.</param>
        /// <param name="exitCode">0 = success, non-zero = failure (PowerShell <c>$LASTEXITCODE</c>).</param>
        static void ExitEditorIfRequested(bool exitEditorWhenDone, int exitCode)
        {
            // --- Guard: only quit when the batch pipeline asked for it ---
            if (!exitEditorWhenDone)
                return;

            // [UNITY] Never call EditorApplication.Exit from an interactive menu click — that would
            // close the user's Editor. Batchmode is the only supported Exit path.
            if (!Application.isBatchMode)
            {
                Debug.LogWarning(
                    "[TitanOrbitBuild] exitEditorWhenDone was set but Editor is not in batchmode — " +
                    "skipping EditorApplication.Exit so the interactive Editor stays open.");
                return;
            }

            Debug.Log($"[TitanOrbitBuild] Batchmode build finished — EditorApplication.Exit({exitCode}).");
            EditorApplication.Exit(exitCode);
        }

        /// <summary>
        /// After a successful Editor Linux GCE build, copy the output to a staging folder and start
        /// the existing <c>deploy_server_gce.bat</c> pipeline in a visible console window.
        /// Unity stays open — this is the fast day-to-day publish path.
        /// </summary>
        /// <param name="buildFolder">
        /// Live build folder (usually <c>BuildOutput/Server/TitanOrbitLinux1</c>). We do not tar this
        /// path directly while the Editor is open; we copy first.
        /// </param>
        static void StartGceDeployFromEditorBuildFolder(string buildFolder)
        {
            // --- Resolve paths ---
            // [STANDARD] Application.dataPath is …/Assets; parent is the Unity project root.
            string projectRoot = Path.GetDirectoryName(Application.dataPath) ?? Directory.GetCurrentDirectory();
            string stagingFolder = Path.GetFullPath(Path.Combine(projectRoot, ServerLinuxDeployStagingFolder));
            string gceDir = Path.GetFullPath(Path.Combine(projectRoot, "tools", "gce"));
            string deployBat = Path.Combine(gceDir, "deploy_server_gce.bat");

            if (!Directory.Exists(buildFolder))
            {
                Debug.LogError("[TitanOrbitBuild] Cannot deploy: build folder missing: " + buildFolder);
                return;
            }

            if (!File.Exists(deployBat))
            {
                Debug.LogError("[TitanOrbitBuild] Cannot deploy: missing " + deployBat);
                return;
            }

            // --- Staging copy (Editor stays open safely) ---
            // [TITAN-ORBIT] tar/upload of the live BuildOutput tree while Unity holds the project has
            // produced 0-byte global-metadata.dat on the VM. Staging isolates deploy from Editor locks.
            try
            {
                Debug.Log($"[TitanOrbitBuild] Copying build to deploy staging:\n  from: {buildFolder}\n  to:   {stagingFolder}");
                CopyDirectoryReplace(buildFolder, stagingFolder);
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    "[TitanOrbitBuild] Staging copy failed — deploy aborted. " +
                    "If files are locked, retry the + Deploy menu after a few seconds.\n" + ex);
                return;
            }

            // --- Launch existing deploy bat in cmd so the window stays open on failure ---
            // [STANDARD] Project path contains a space ("Titan Orbit"). Launch via cmd /c with quoted
            // paths. Trailing pause keeps the console readable if upload/restart fails.
            // cmd.exe quoting: cmd /c ""bat" args..."  (leading doubled quote is intentional on Windows).
            string cmdArgs =
                "/c \"\"" + deployBat + "\" freeDisk useGcs \"" + stagingFolder +
                "\" & echo. & echo ===== deploy finished (exit %ERRORLEVEL%) ===== & pause\"";

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArgs,
                WorkingDirectory = gceDir,
                UseShellExecute = true,
                CreateNoWindow = false
            };

            try
            {
                Process.Start(psi);
                Debug.Log(
                    "[TitanOrbitBuild] Started deploy_server_gce.bat freeDisk useGcs (staging copy). " +
                    "Watch the new console window for upload progress. Unity can stay open. " +
                    "Window pauses at the end so errors are visible.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[TitanOrbitBuild] Failed to start deploy_server_gce.bat:\n" + ex);
            }
        }

        /// <summary>
        /// Deletes <paramref name="destination"/> if it exists, then recursively copies
        /// <paramref name="source"/> into it. Used for deploy staging only.
        /// </summary>
        /// <param name="source">Live build folder to copy from.</param>
        /// <param name="destination">Staging folder path (created fresh).</param>
        static void CopyDirectoryReplace(string source, string destination)
        {
            // --- Wipe previous staging ---
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);

            Directory.CreateDirectory(destination);

            // --- Copy files ---
            foreach (string filePath in Directory.GetFiles(source))
            {
                string fileName = Path.GetFileName(filePath);
                File.Copy(filePath, Path.Combine(destination, fileName), overwrite: true);
            }

            // --- Copy subfolders ---
            foreach (string dirPath in Directory.GetDirectories(source))
            {
                string dirName = Path.GetFileName(dirPath);
                CopyDirectoryReplace(dirPath, Path.Combine(destination, dirName));
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
        /// Serializable marker for a Windows client build that must survive domain reload after
        /// switching away from Linux Dedicated Server (or any non-Windows-Player target).
        /// </summary>
        [Serializable]
        class PendingWindowsClientBuild
        {
            /// <summary>True when a Windows client BuildPlayer should run after platform switch.</summary>
            public bool requested;
        }

        /// <summary>
        /// Serializable request for a WebGL production build that must survive domain reload after
        /// switching away from Linux Dedicated Server (or any non-WebGL target).
        /// </summary>
        [Serializable]
        class PendingWebGlBuild
        {
            /// <summary>True when a WebGL BuildPlayer should run after platform switch.</summary>
            public bool requested;

            /// <summary>
            /// <see cref="BuildTarget"/> the Editor was on before the switch (cast to int for JSON).
            /// Used only to restore a prior <b>client</b> target — never Dedicated Server.
            /// </summary>
            public int previousTarget;

            /// <summary>
            /// <see cref="StandaloneBuildSubtarget"/> before the switch (cast to int for JSON).
            /// Server subtarget → do not restore (leave WebGL).
            /// </summary>
            public int previousSubtarget;
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

            /// <summary>
            /// When true, resume path calls <see cref="EditorApplication.Exit"/> after BuildPlayer
            /// (batchmode PowerShell pipeline). Menu builds leave this false.
            /// </summary>
            public bool exitEditorWhenDone;

            /// <summary>
            /// When true, resume path starts <c>deploy_server_gce.bat</c> after a successful build
            /// (Editor + Deploy menu). Batchmode leaves this false (the .bat chains deploy itself).
            /// </summary>
            public bool deployToGceAfterSuccess;
        }
    }
}
