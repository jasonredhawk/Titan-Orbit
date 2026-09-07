using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace TitanOrbit.Editor.Build
{
    /// <summary>
    /// [EDITOR] Linux Dedicated Server IL2CPP safety net for GCE, Edgegap plugin, and menu builds.
    /// Edgegap's <c>Build server</c> button calls <c>BuildPipeline.BuildPlayer</c> directly and does
    /// not apply Titan Orbit's Linux server settings. Without this hook, Bee/clang can die while
    /// compiling large <c>GenericMethods__*.cpp</c> files and report an empty
    /// <c>failed with output:</c> (no C++ diagnostics).
    /// </summary>
    public sealed class TitanOrbitLinuxServerIl2CppBuildGuard : IPreprocessBuildWithReport
    {
        /// <summary>Run before asset/script compile so IL2CPP flags apply to this player build.</summary>
        public int callbackOrder => -50;

        /// <summary>
        /// Applies Linux-server IL2CPP settings when the incoming player build is a headless Linux server.
        /// </summary>
        /// <param name="report">Unity build report (platform + options).</param>
        public void OnPreprocessBuild(BuildReport report)
        {
            if (!IsLinuxDedicatedServerBuild(report))
                return;

            ApplyDedicatedServerIl2CppSettings("preprocess");
        }

        /// <summary>
        /// True when this player build is Linux Dedicated Server (Edgegap plugin or TitanOrbit menu).
        /// </summary>
        internal static bool IsLinuxDedicatedServerBuild(BuildReport report)
        {
            if (report == null || report.summary.platform != BuildTarget.StandaloneLinux64)
                return false;

            // [UNITY] Server subtarget is the Dedicated Server player (UNITY_SERVER). EnableHeadlessMode
            // is the older flag still set by TitanOrbitBuildAutomation.
            return EditorUserBuildSettings.standaloneBuildSubtarget == StandaloneBuildSubtarget.Server
                   || (report.summary.options & BuildOptions.EnableHeadlessMode) != 0;
        }

        /// <summary>
        /// Sets IL2CPP options that keep Linux clang from being OOM-killed during Bee's parallel compile.
        /// Safe to call from menu builds and from <see cref="OnPreprocessBuild"/>.
        /// </summary>
        /// <param name="reason">Short log tag (menu vs preprocess).</param>
        internal static void ApplyDedicatedServerIl2CppSettings(string reason)
        {
            // --- Scripting backend ---
            // [UNITY] GCE/Edgegap Linux images must not ship MonoBleedingEdge.
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Server, ScriptingImplementation.IL2CPP);

            // --- C++ compiler configuration ---
            // Release uses -O3 -g. clang++ then dies on large GenericMethods files under parallel Bee
            // load and Unity prints "failed with output:" with no stderr. Master drops debug info.
            PlayerSettings.SetIl2CppCompilerConfiguration(
                NamedBuildTarget.Server,
                Il2CppCompilerConfiguration.Master);

            // --- Code generation ---
            // [UNITY] OptimizeSize emits less template-heavy C++ (this project generated ~1.7 GB of
            // cpp on the failed Edgegap build). Burst jobs remain the gameplay hot path.
            PlayerSettings.SetIl2CppCodeGeneration(
                NamedBuildTarget.Server,
                Il2CppCodeGeneration.OptimizeSize);

            // --- Parallel clang cap ---
            // Burst AOT (bcl.exe) and IL2CPP clang run in the same Bee graph. Default worker count
            // can spawn dozens of clang++ processes; Windows then kills some with exit 1 and no output.
            int jobs = Mathf.Clamp(SystemInfo.processorCount / 2, 2, 6);
            Environment.SetEnvironmentVariable("BEE_JOBS", jobs.ToString());
            Environment.SetEnvironmentVariable("UNITY_BUILD_PARALLEL_JOBS", jobs.ToString());

            Debug.Log(
                "[TitanOrbitBuild] Linux Dedicated Server IL2CPP settings (" + reason + "): " +
                "backend=IL2CPP, compiler=Master, codegen=OptimizeSize, beeJobs<=" + jobs + ". " +
                "If Bee still reports empty 'failed with output', close other apps and retry, or delete " +
                "Library/Bee/artifacts/LinuxPlayerBuildProgram and use TitanOrbit → Build → " +
                "Headless Server (Linux — Edgegap).");
        }

        /// <summary>
        /// Deletes stale Linux IL2CPP object/codegen folders so a retry does not reuse a half-written graph.
        /// </summary>
        internal static void CleanLinuxIl2CppBeeArtifacts()
        {
            string root = Path.Combine("Library", "Bee", "artifacts", "LinuxPlayerBuildProgram");
            if (!Directory.Exists(root))
                return;

            try
            {
                Directory.Delete(root, recursive: true);
                Debug.Log("[TitanOrbitBuild] Cleared " + root + " before Linux server IL2CPP compile.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[TitanOrbitBuild] Could not clear Linux IL2CPP Bee artifacts (files may be locked). " +
                    "Quit Unity and delete that folder if the next build fails.\n" + ex.Message);
            }
        }
    }
}
