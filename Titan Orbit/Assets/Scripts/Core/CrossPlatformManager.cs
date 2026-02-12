using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Handles cross-platform configuration and optimizations
    /// </summary>
    public class CrossPlatformManager : MonoBehaviour
    {
        [Header("Platform Settings")]
        [SerializeField] private int targetFrameRate = 60;
        [SerializeField] private bool enableVSync = true;

        [Header("Mobile Optimizations")]
        [SerializeField] private bool reduceQualityOnMobile = true;
        [SerializeField] private int mobileTargetFrameRate = 30;

        private void Start()
        {
            ConfigurePlatform();
        }

        private void ConfigurePlatform()
        {
            // Set target frame rate
            if (Application.isMobilePlatform)
            {
                Application.targetFrameRate = mobileTargetFrameRate;
                
                if (reduceQualityOnMobile)
                {
                    QualitySettings.SetQualityLevel(1); // Low quality on mobile
                }
            }
            else
            {
                Application.targetFrameRate = targetFrameRate;
            }

            // VSync
            QualitySettings.vSyncCount = enableVSync ? 1 : 0;

            // Platform-specific optimizations
            if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                // WebGL specific settings
                Application.targetFrameRate = 60;
            }
        }

        public void SetQualityLevel(int level)
        {
            QualitySettings.SetQualityLevel(level);
        }

        public void SetTargetFrameRate(int frameRate)
        {
            Application.targetFrameRate = frameRate;
        }
    }
}
