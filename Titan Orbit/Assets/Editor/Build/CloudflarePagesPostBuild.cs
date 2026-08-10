using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;

namespace TitanOrbit.Editor.Build
{
    /// <summary>
    /// [EDITOR] Post-process step for WebGL production builds — copies Cloudflare Pages
    /// <c>_headers</c> (COOP/COEP, caching) into the build output folder. Required for
    /// correct browser security headers when deploying to Cloudflare Pages. Safe to skip if
    /// the source file is missing (logs warning only).
    /// </summary>
    public static class CloudflarePagesPostBuild
    {
        /// <summary>Repo-relative path to the headers template committed with the project.</summary>
        private const string HeadersSourcePath = "Assets/CloudflarePages/_headers";

        /// <summary>
        /// [UNITY] PostProcessBuild — runs after WebGL player build completes.
        /// </summary>
        [PostProcessBuild(0)]
        public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
        {
            // --- OnPostProcessBuild ---
            if (target != BuildTarget.WebGL)
                return;

            if (string.IsNullOrWhiteSpace(pathToBuiltProject))
                return;

            string source = HeadersSourcePath;
            if (!File.Exists(source))
            {
                UnityEngine.Debug.LogWarning("[CloudflarePagesPostBuild] Missing headers source file: " + source);
                return;
            }

            string dest = Path.Combine(pathToBuiltProject, "_headers");
            File.Copy(source, dest, overwrite: true);
        }
    }
}
