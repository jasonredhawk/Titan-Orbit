using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;

namespace TitanOrbit.Editor.Build
{
    /// <summary>
    /// [EDITOR] Post-process step for WebGL production builds — copies Cloudflare Pages
    /// <c>_headers</c> (CSP, caching) and <c>ads.txt</c> (AppLixir / IAB) into the build
    /// output folder. Required for browser security headers and rewarded-ad fill when
    /// deploying to Cloudflare Pages. Safe to skip if a source file is missing (logs warning).
    /// </summary>
    public static class CloudflarePagesPostBuild
    {
        /// <summary>Repo-relative path to the headers template committed with the project.</summary>
        private const string HeadersSourcePath = "Assets/CloudflarePages/_headers";

        /// <summary>
        /// AppLixir (and other web ad networks) require <c>/ads.txt</c> at the site root.
        /// Paste dashboard rows into this file before a production WebGL build.
        /// </summary>
        private const string AdsTxtSourcePath = "Assets/CloudflarePages/ads.txt";

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
                UnityEngine.Debug.LogWarning("[CloudflarePagesPostBuild] Missing headers source file: " + source);
            else
                File.Copy(source, Path.Combine(pathToBuiltProject, "_headers"), overwrite: true);

            CopyAdsTxt(pathToBuiltProject);
        }

        /// <summary>Copies <c>ads.txt</c> next to index.html so Cloudflare serves it at the origin root.</summary>
        static void CopyAdsTxt(string pathToBuiltProject)
        {
            if (!File.Exists(AdsTxtSourcePath))
            {
                UnityEngine.Debug.LogWarning("[CloudflarePagesPostBuild] Missing ads.txt source file: " + AdsTxtSourcePath);
                return;
            }

            string dest = Path.Combine(pathToBuiltProject, "ads.txt");
            File.Copy(AdsTxtSourcePath, dest, overwrite: true);
        }
    }
}
