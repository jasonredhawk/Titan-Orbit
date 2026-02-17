using UnityEngine;
using SpaceGraphicsToolkit;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Ensures the main camera has SgtCamera so Space Graphics Toolkit planets (SgtPlanet) render.
    /// Runs once at load so existing scenes work without re-running editor setup.
    /// </summary>
    public static class SgtCameraEnsurer
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureSgtCameraOnMainCamera()
        {
            UnityEngine.Camera main = UnityEngine.Camera.main;
            if (main == null) return;
            if (main.GetComponent<SgtCamera>() != null) return;
            main.gameObject.AddComponent<SgtCamera>();
        }
    }
}
