using UnityEngine;

using Shapes;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Some targets can omit or mishandle Shapes' GPU-instancing shader variants for
    /// immediate-mode draws, so orbit zone fills, planet level rings, lines, and other IM shapes vanish
    /// at runtime. As a reliability safeguard, force immediate-mode instancing off at startup.
    /// This is cheaper than missing gameplay-critical visuals.
    /// </summary>
    internal static class ShapesImmediateModePlatformFix
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Apply()
        {
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
        }
    }
}
