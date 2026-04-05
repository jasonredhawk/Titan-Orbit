using UnityEngine;

#if UNITY_WEBGL && !UNITY_EDITOR
using Shapes;
#endif

namespace TitanOrbit.Core
{
    /// <summary>
    /// WebGL builds can omit or mishandle Shapes' GPU-instancing shader variants for immediate-mode draws,
    /// so rings, orbit zones, lines, and UI shapes vanish in the browser while the Editor looks fine.
    /// Project Settings (Graphics) sets Instancing Variants to Keep All; this disables immediate-mode
    /// instancing on WebGL only as an extra safeguard (vendor-documented workaround).
    /// </summary>
    internal static class ShapesWebGLImmediateModeFix
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Apply()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                var config = ShapesConfig.Instance;
                if (config != null)
                    config.useImmediateModeInstancing = false;
            }
            catch
            {
                // Avoid startup failures if Shapes config isn't ready yet on some players/CDN paths.
            }
#endif
        }
    }
}
