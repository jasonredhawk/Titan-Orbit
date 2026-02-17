using UnityEngine;
using SpaceGraphicsToolkit.LightAndShadow;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Ensures every Light in the scene has SgtLight so SGT atmosphere and planets render correctly.
    /// Runs once at load so existing scenes work without re-running editor setup.
    /// </summary>
    public static class SgtLightEnsurer
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureSgtLightOnLights()
        {
            Light[] lights = Object.FindObjectsOfType<Light>();
            foreach (Light light in lights)
            {
                if (light.GetComponent<SgtLight>() == null)
                    light.gameObject.AddComponent<SgtLight>();
            }
        }
    }
}
