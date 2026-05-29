using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Core
{
    /// <summary>
    /// WebGL desktop browsers often render nothing for URP meshes that use MaterialPropertyBlock
    /// (Space Graphics Toolkit planets/asteroids via Graphics.DrawMesh, and some ship tint paths)
    /// while particles, trails, and UI still draw. Disabling SRP batching fixes this class of bug.
    /// </summary>
    internal static class WebGLGameplayRenderCompat
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Apply()
        {
            if (Application.platform != RuntimePlatform.WebGLPlayer)
                return;

            // Same mitigation documented on ScrollingSpaceBackground (MPB + SRP Batcher on GLES/WebGL).
            GraphicsSettings.useScriptableRenderPipelineBatching = false;
        }
    }
}
