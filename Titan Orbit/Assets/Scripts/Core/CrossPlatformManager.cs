using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Handles cross-platform configuration and optimizations (frame rate, VSync, mobile quality).
    /// Runs once at <see cref="Start"/>. Dedicated Relay joins re-assert VSync on in
    /// <c>TitanOrbitSessionManager</c> so tear-free presents stick after this Start runs.
    /// </summary>
    public class CrossPlatformManager : MonoBehaviour
    {
        /// <summary>Desktop/WebGL target FPS when not overridden by session / mobile paths.</summary>
        [Header("Platform Settings")]
        [SerializeField] private int targetFrameRate = 60;

        /// <summary>
        /// When true, enables VSync on desktop at scene start.
        /// Dedicated online clients also keep VSync on from session code after join
        /// (prevents tearing while the camera pans over map bodies).
        /// </summary>
        [SerializeField] private bool enableVSync = true;

        [Header("Mobile Optimizations")]
        [SerializeField] private bool reduceQualityOnMobile = true;

        /// <summary>Mobile FPS cap (battery). Desktop keeps <see cref="targetFrameRate"/>.</summary>
        [SerializeField] private int mobileTargetFrameRate = 30;

        /// <summary>[UNITY] Apply platform defaults after the scene loads.</summary>
        private void Start()
        {
            ConfigurePlatform();
        }

        /// <summary>
        /// Sets <see cref="Application.targetFrameRate"/> and VSync for this platform.
        /// </summary>
        private void ConfigurePlatform()
        {
#if UNITY_SERVER
            // Same contract as TitanOrbitSessionManager.ApplyDedicatedServerFramePace (Core cannot
            // reference NetCode). Uncapped player loop — NCE BusyWait paces 60 Hz ticks.
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            if (Time.maximumDeltaTime > 0.1f)
                Time.maximumDeltaTime = 0.1f;
            return;
#endif
            if (Application.isBatchMode)
            {
                Application.targetFrameRate = 60;
                QualitySettings.vSyncCount = 0;
                return;
            }

            // --- Mobile ---
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

            // --- Desktop / WebGL VSync ---
            // [UNITY] vSyncCount 1 = present on every vertical blank (no tearing).
            // Session manager re-asserts this for dedicated online clients after join.
            QualitySettings.vSyncCount = enableVSync ? 1 : 0;

            if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                Application.targetFrameRate = 60;
            }
        }

        /// <summary>Inspector / runtime quality preset change.</summary>
        public void SetQualityLevel(int level)
        {
            QualitySettings.SetQualityLevel(level);
        }

        /// <summary>Overrides <see cref="Application.targetFrameRate"/>.</summary>
        public void SetTargetFrameRate(int frameRate)
        {
            Application.targetFrameRate = frameRate;
        }
    }
}
