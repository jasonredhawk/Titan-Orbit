#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace TitanOrbit.Input
{
    /// <summary>
    /// [UNITY] WebGL-only Alt press. Chrome keeps Alt for the browser menu, so
    /// <c>Keyboard.leftAltKey</c> often never goes true in a WebGL player.
    /// The page listener in the WebGL template (and <c>TitanOrbitWebGLKeys.jslib</c>)
    /// records one non-repeat keydown. Editor and standalone keep using the Input System.
    /// </summary>
    static class WebGlAltKeyCapture
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        static extern void TitanOrbit_InstallAltCapture();

        [DllImport("__Internal")]
        static extern int TitanOrbit_ConsumeAltPressed();
#endif

        /// <summary>Installs the browser listener once. No-op in the Editor and standalone players.</summary>
        public static void Install()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            TitanOrbit_InstallAltCapture();
#endif
        }

        /// <summary>
        /// True once per Alt keydown on WebGL. Clears the edge so a held Alt
        /// does not fire a rocket or mine every frame.
        /// </summary>
        public static bool ConsumePressed()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return TitanOrbit_ConsumeAltPressed() != 0;
#else
            return false;
#endif
        }
    }
}
