#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace TitanOrbit.ECS.Editor
{
    /// <summary>
    /// [EDITOR] Keeps Titan Orbit's GhostSpawnSystem patch applied to the <b>embedded</b>
    /// NetCode package under <c>Packages/com.unity.netcode</c> (version-controlled).
    /// <para>
    /// Root cause we fix: Windows player Crash!!! in TrySpawnFromDelayedQueue when
    /// ResizeUninitialized FreeTracked a stale Instantiated SnapshotDataBuffer pointer.
    /// PackageCache-only edits were wiped by Unity — that is why the crash kept returning.
    /// </para>
    /// </summary>
    public static class NetCodeGhostSpawnPatchMenu
    {
        /// <summary>Canonical patched source checked into the repo under tools/netcode-patches.</summary>
        public const string PatchSourceRelative =
            "tools/netcode-patches/GhostSpawnSystem.cs";

        /// <summary>
        /// Embedded package path (preferred). Stays in git via file:com.unity.netcode.
        /// </summary>
        public const string EmbeddedGhostSpawnRelative =
            "Packages/com.unity.netcode/Runtime/Snapshot/GhostSpawnSystem.cs";

        /// <summary>
        /// Legacy PackageCache path — only used as a fallback warning if embed is missing.
        /// </summary>
        public const string PackageCacheGhostSpawnRelative =
            "Library/PackageCache/com.unity.netcode@6437771c174a/Runtime/Snapshot/GhostSpawnSystem.cs";

        /// <summary>IL-surviving patch id declared on GhostSpawnSystem.</summary>
        public const string PatchIdMarker = "TO_GhostSpawn_v11_createAll_managedLtw";

        /// <summary>Older markers that must not be the only evidence of a “good” patch.</summary>
        public const string SafeCopyMarker = "TryCopySnapshotBufferSafe";

        /// <summary>v8 keeps Instantiates-per-frame cap (must remain; do not require re-queue).</summary>
        public const string InstantiatesCapMarker = "delayedInstantiatesThisFrame";

        /// <summary>
        /// [EDITOR] On editor load — ensure embedded package has the Titan Orbit GhostSpawn patch.
        /// </summary>
        [InitializeOnLoadMethod]
        static void AutoApplyPatchOnEditorLoad()
        {
            try
            {
                if (TryEnsurePatched(out string detail, forceRefresh: true))
                {
                    if (detail.IndexOf("Applied", StringComparison.OrdinalIgnoreCase) >= 0)
                        Debug.Log("[NetCodeGhostSpawnPatch] " + detail);
                }
                else
                    Debug.LogWarning("[NetCodeGhostSpawnPatch] " + detail);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NetCodeGhostSpawnPatch] Auto-apply failed: " + e.Message);
            }
        }

        /// <summary>
        /// Copies the checked-in patched GhostSpawnSystem over the embedded NetCode package.
        /// </summary>
        [MenuItem("Titan Orbit/NetCode/Re-apply GhostSpawnSystem patch")]
        public static void ReapplyGhostSpawnPatch()
        {
            if (!TryEnsurePatched(out string detail, forceRefresh: true, overwriteEvenIfPresent: true))
            {
                EditorUtility.DisplayDialog("GhostSpawn patch failed", detail, "OK");
                return;
            }

            Debug.Log("[NetCodeGhostSpawnPatch] " + detail);
            EditorUtility.DisplayDialog(
                "GhostSpawn patch applied",
                detail + "\n\nWait for script recompile, then rebuild the Windows client.\n\n" +
                "After build, Unity.NetCode.dll must contain: " + PatchIdMarker,
                "OK");
        }

        /// <summary>Logs whether the embedded package currently has the Titan Orbit GhostSpawn patch.</summary>
        [MenuItem("Titan Orbit/NetCode/Check GhostSpawnSystem patch status")]
        public static void CheckGhostSpawnPatchStatus()
        {
            if (!TryGetDestPath(out string destPath, out string error))
            {
                Debug.LogWarning("[NetCodeGhostSpawnPatch] " + error);
                return;
            }

            string text = File.ReadAllText(destPath);
            bool ok = IsPatched(text);
            Debug.Log(ok
                ? "[NetCodeGhostSpawnPatch] OK — " + PatchIdMarker + " @ " + destPath
                : "[NetCodeGhostSpawnPatch] MISSING/STALE — run Titan Orbit > NetCode > Re-apply GhostSpawnSystem patch.");
        }

        /// <summary>
        /// Ensures embedded GhostSpawnSystem matches the checked-in Titan Orbit patch.
        /// </summary>
        public static bool TryEnsurePatched(out string detail, bool forceRefresh, bool overwriteEvenIfPresent = false)
        {
            detail = string.Empty;
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string sourcePath = Path.Combine(projectRoot, PatchSourceRelative);
            if (!File.Exists(sourcePath))
            {
                detail = "Could not find patch source:\n" + sourcePath;
                return false;
            }

            if (!TryGetDestPath(out string destPath, out string error))
            {
                detail = error;
                return false;
            }

            string existing = File.ReadAllText(destPath);
            if (!overwriteEvenIfPresent && IsPatched(existing))
            {
                detail = "Embedded NetCode already has " + PatchIdMarker + ".";
                return true;
            }

            File.Copy(sourcePath, destPath, overwrite: true);
            File.SetLastWriteTimeUtc(destPath, DateTime.UtcNow);

            if (forceRefresh)
                AssetDatabase.Refresh();

            string after = File.ReadAllText(destPath);
            if (!IsPatched(after))
            {
                detail = "Copied patch but markers still missing at:\n" + destPath;
                return false;
            }

            detail = "Applied GhostSpawn patch (" + PatchIdMarker + ") to embedded package.";
            return true;
        }

        /// <summary>
        /// True when source text has the current Titan Orbit GhostSpawn patch markers.
        /// Do not require obsolete id substrings (e.g. v8 <c>ghostMapSafe</c>) — v9+ ids differ.
        /// </summary>
        public static bool IsPatched(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            // --- Required markers (must all be present) ---
            // PatchIdMarker — IL-surviving id (currently TO_GhostSpawn_v9_transformsAlwaysOn).
            // SafeCopyMarker — TryCopySnapshotBufferSafe (Windows Instantiates crash fix).
            // InstantiatesCapMarker — 1 Instantiates/frame drain.
            // Intentionally NOT [BurstCompile] — managed OnUpdate.
            return text.Contains(PatchIdMarker, StringComparison.Ordinal) &&
                   text.Contains(SafeCopyMarker, StringComparison.Ordinal) &&
                   text.Contains(InstantiatesCapMarker, StringComparison.Ordinal) &&
                   text.Contains("Intentionally NOT [BurstCompile]", StringComparison.Ordinal);
        }

        static bool TryGetDestPath(out string destPath, out string error)
        {
            destPath = null;
            error = null;
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            destPath = Path.Combine(projectRoot, EmbeddedGhostSpawnRelative);
            if (File.Exists(destPath))
                return true;

            string cachePath = Path.Combine(projectRoot, PackageCacheGhostSpawnRelative);
            error =
                "Embedded NetCode GhostSpawnSystem.cs missing at:\n" + destPath +
                "\n\nExpected Packages/com.unity.netcode (file: dependency in manifest.json)." +
                (File.Exists(cachePath)
                    ? "\n\nPackageCache copy still exists but is NOT used once file: embed is configured."
                    : string.Empty);
            destPath = null;
            return false;
        }
    }

    /// <summary>
    /// [EDITOR] Before any player build, ensure embedded GhostSpawnSystem is patched.
    /// </summary>
    public sealed class NetCodeGhostSpawnPatchBuildGuard : IPreprocessBuildWithReport
    {
        /// <summary>Run early so a missing patch fails before a long compile.</summary>
        public int callbackOrder => -100;

        /// <summary>Applies the Titan Orbit GhostSpawn patch and aborts if still wrong.</summary>
        public void OnPreprocessBuild(BuildReport report)
        {
            if (!NetCodeGhostSpawnPatchMenu.TryEnsurePatched(out string detail, forceRefresh: true, overwriteEvenIfPresent: true))
            {
                throw new BuildFailedException(
                    "[NetCodeGhostSpawnPatch] Refusing build — GhostSpawnSystem patch missing.\n" + detail);
            }

            Debug.Log("[NetCodeGhostSpawnPatch] Pre-build OK — " + detail);
        }
    }
}
#endif
