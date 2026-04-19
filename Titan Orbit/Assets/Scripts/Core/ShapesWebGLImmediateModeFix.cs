using UnityEngine;

#if !UNITY_EDITOR
using Shapes;
#endif

namespace TitanOrbit.Core
{
    /// <summary>
    /// WebGL and many mobile GPUs can omit or mishandle Shapes' GPU-instancing shader variants for
    /// immediate-mode draws, so orbit zone fills, planet level rings, lines, and other IM shapes vanish
    /// on device while the Editor looks fine. Project Settings (Graphics) can use Instancing Variants
    /// Keep All; this disables immediate-mode instancing on those players as a safeguard.
    /// </summary>
    internal static class ShapesImmediateModePlatformFix
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Apply()
        {
#if UNITY_EDITOR
            return;
#else
            bool disableInstancing =
#if UNITY_WEBGL
                true ||
#endif
                Application.isMobilePlatform;
            if (!disableInstancing)
                return;

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
