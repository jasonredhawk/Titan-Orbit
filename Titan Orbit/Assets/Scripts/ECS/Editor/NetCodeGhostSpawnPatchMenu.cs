#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TitanOrbit.ECS.Editor
{
    /// <summary>
    /// [EDITOR] Re-applies Titan Orbit's safe rate-limit patch to NetCode's <c>GhostSpawnSystem.cs</c>
    /// after Unity restores PackageCache (package update, reimport, clear Library, etc.).
    /// <para>
    /// Why this exists: late-join map ghosts share past spawn ticks, so stock NetCode Instantiates
    /// the entire delayed queue in one Burst frame and hard-crashes the Windows player. Our patch
    /// caps Instantiates per frame and bounds-checks ghost types. PackageCache edits are not
    /// durable — run this menu if the crash returns after a package restore.
    /// </para>
    /// </summary>
    public static class NetCodeGhostSpawnPatchMenu
    {
        /// <summary>Canonical patched source checked into the repo under tools/netcode-patches.</summary>
        const string PatchSourceRelative =
            "tools/netcode-patches/GhostSpawnSystem.cs";

        /// <summary>
        /// PackageCache path for the NetCode package used by this project.
        /// Update the hash folder if com.unity.netcode is upgraded.
        /// </summary>
        const string PackageGhostSpawnRelative =
            "Library/PackageCache/com.unity.netcode@6437771c174a/Runtime/Snapshot/GhostSpawnSystem.cs";

        /// <summary>
        /// Copies the checked-in patched <c>GhostSpawnSystem.cs</c> over PackageCache and asks
        /// Unity to reimport so Burst / NetCode recompile.
        /// </summary>
        [MenuItem("Titan Orbit/NetCode/Re-apply GhostSpawnSystem rate-limit patch")]
        public static void ReapplyGhostSpawnPatch()
        {
            // --- Resolve paths ---
            // [UNITY] Application.dataPath is .../Assets — project root is one level up.
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string sourcePath = Path.Combine(projectRoot, PatchSourceRelative);
            string destPath = Path.Combine(projectRoot, PackageGhostSpawnRelative);

            if (!File.Exists(sourcePath))
            {
                EditorUtility.DisplayDialog(
                    "GhostSpawn patch missing",
                    "Could not find:\n" + sourcePath +
                    "\n\nExpected the patched file under tools/netcode-patches/.",
                    "OK");
                return;
            }

            if (!File.Exists(destPath))
            {
                EditorUtility.DisplayDialog(
                    "NetCode package path missing",
                    "Could not find:\n" + destPath +
                    "\n\nIf you upgraded com.unity.netcode, update PackageGhostSpawnRelative in NetCodeGhostSpawnPatchMenu.cs.",
                    "OK");
                return;
            }

            // --- Already patched? ---
            string existing = File.ReadAllText(destPath);
            if (existing.Contains("k_MaxDelayedInstantiatesPerFrame") &&
                existing.Contains("TITAN-ORBIT"))
            {
                bool overwrite = EditorUtility.DisplayDialog(
                    "GhostSpawn patch already present",
                    "PackageCache GhostSpawnSystem.cs already contains the Titan Orbit markers.\n\nOverwrite from tools/netcode-patches anyway?",
                    "Overwrite",
                    "Cancel");
                if (!overwrite)
                    return;
            }

            // --- Copy + refresh ---
            File.Copy(sourcePath, destPath, overwrite: true);
            AssetDatabase.Refresh();
            Debug.Log(
                "[NetCodeGhostSpawnPatch] Re-applied GhostSpawnSystem rate-limit patch from " +
                PatchSourceRelative + ". Wait for script/Burst recompile, then rebuild the Windows client.");
            EditorUtility.DisplayDialog(
                "GhostSpawn patch applied",
                "Patched PackageCache GhostSpawnSystem.cs.\n\nWait for compile, then rebuild the Windows client (this system is client-only).",
                "OK");
        }

        /// <summary>
        /// Quick check: logs whether PackageCache currently has the Titan Orbit Instantiates cap.
        /// </summary>
        [MenuItem("Titan Orbit/NetCode/Check GhostSpawnSystem patch status")]
        public static void CheckGhostSpawnPatchStatus()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string destPath = Path.Combine(projectRoot, PackageGhostSpawnRelative);
            if (!File.Exists(destPath))
            {
                Debug.LogWarning("[NetCodeGhostSpawnPatch] Package GhostSpawnSystem.cs not found at " + destPath);
                return;
            }

            string text = File.ReadAllText(destPath);
            bool patched = text.Contains("k_MaxDelayedInstantiatesPerFrame") && text.Contains("TITAN-ORBIT");
            Debug.Log(patched
                ? "[NetCodeGhostSpawnPatch] OK — rate-limit patch is present in PackageCache."
                : "[NetCodeGhostSpawnPatch] MISSING — run Titan Orbit > NetCode > Re-apply GhostSpawnSystem rate-limit patch.");
        }
    }
}
#endif
