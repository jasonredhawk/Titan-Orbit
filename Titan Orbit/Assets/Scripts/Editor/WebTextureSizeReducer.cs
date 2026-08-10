using UnityEditor;
using UnityEngine;
using TitanOrbit.Editor.Build;

namespace TitanOrbit.Editor
{
    /// <summary>
    /// Menu wrapper for WebGL ship/planet texture import settings. Implementation lives in
    /// <see cref="WebGLTextureImportBuildFix"/> so build menu, preprocess, and Tools menu stay in sync.
    /// </summary>
    public static class WebTextureSizeReducer
    {
        [MenuItem("Tools/Textures/Reduce Ship & Planet Textures For WebGL")]
        public static void ReduceShipAndPlanetTexturesForWebGL()
        {
            // --- ReduceShipAndPlanetTexturesForWebGL ---
            int processed = WebGLTextureImportBuildFix.ApplyWebGlGameplayTextureImports(log: true);

            EditorUtility.DisplayDialog(
                "WebGL Texture Reduction Complete",
                processed > 0
                    ? $"Updated WebGL import settings for {processed} ship/planet textures.\n\n" +
                      "Textures use WebGL RGBA32 (uncompressed). SRP Batcher is disabled on the WebGL pipeline asset."
                    : "Gameplay textures already use WebGL RGBA32 (uncompressed). SRP Batcher is off on the WebGL pipeline.",
                "OK");
        }
    }
}
