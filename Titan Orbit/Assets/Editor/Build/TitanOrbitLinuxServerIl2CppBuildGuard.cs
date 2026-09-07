using System;
using System.IO;
using UnityEngine;

namespace TitanOrbit.Editor.Build
{
    /// <summary>
    /// [EDITOR] Manual cleanup for Linux Dedicated Server IL2CPP Bee artifacts.
    /// Do not rewrite PlayerSettings during preprocess — that plus Master (LTO) made every
    /// Edgegap incremental build spend ~28 minutes relinking <c>GameAssembly.so</c>.
    /// </summary>
    public static class TitanOrbitLinuxServerIl2CppBuildGuard
    {
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
