using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;

namespace TitanOrbit.Editor.Build
{
    public static class CloudflarePagesPostBuild
    {
        private const string HeadersSourcePath = "Assets/CloudflarePages/_headers";

        [PostProcessBuild(0)]
        public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
        {
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

