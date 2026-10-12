using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;

namespace TitanOrbit.Editor.Build
{
    /// <summary>
    /// [EDITOR] Post-process step for WebGL production builds — copies Cloudflare Pages
    /// <c>_headers</c> (CSP, caching), <c>ads.txt</c> (Google Ad Manager / IAB), and the
    /// public <c>/privacy</c> and <c>/terms</c> pages into the build output folder.
    /// Required for browser security headers, rewarded-ad fill, and Google sign-in branding
    /// when deploying to Cloudflare Pages. Safe to skip if a source file is missing (logs warning).
    /// </summary>
    public static class CloudflarePagesPostBuild
    {
        /// <summary>Repo-relative path to the headers template committed with the project.</summary>
        private const string HeadersSourcePath = "Assets/CloudflarePages/_headers";

        /// <summary>
        /// AdSense requires <c>/ads.txt</c> at the site root.
        /// The publisher line lives in <c>Assets/CloudflarePages/ads.txt</c>.
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
            CopySitePage(pathToBuiltProject, "privacy");
            CopySitePage(pathToBuiltProject, "terms");
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

        /// <summary>
        /// Writes <c>Assets/CloudflarePages/{page}/index.html</c> as a build-root file named
        /// <paramref name="page"/> with no extension. The public site is a GCS bucket behind
        /// Cloudflare, and the URL path is the object name: <c>/privacy</c> is the object
        /// <c>privacy</c>, not <c>privacy/index.html</c>.
        /// </summary>
        static void CopySitePage(string pathToBuiltProject, string page)
        {
            string source = Path.Combine("Assets", "CloudflarePages", page, "index.html");
            if (!File.Exists(source))
            {
                UnityEngine.Debug.LogWarning("[CloudflarePagesPostBuild] Missing site page: " + source);
                return;
            }

            string dest = Path.Combine(pathToBuiltProject, page);
            if (Directory.Exists(dest))
                Directory.Delete(dest, recursive: true);
            File.Copy(source, dest, overwrite: true);
        }
    }
}
