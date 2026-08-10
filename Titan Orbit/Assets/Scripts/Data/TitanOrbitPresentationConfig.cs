using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Project-wide presentation mode. When <see cref="UseEntitiesGraphicsForShips"/> is true, ships render
    /// via Entities Graphics on the client and the hybrid <c>EcsWorldVisualizer</c> ship path is disabled.
    /// Planets, gems, and asteroids may remain hybrid until migrated.
    /// <para>
    /// [UNITY] BatchRendererGroup (Entities Graphics) requires URP SRP Batcher ON. When Batcher is
    /// off, this property returns false so hybrid GameObject ship proxies stay active — otherwise
    /// hulls are invisible and the console spams "Trying to render a BatchRendererGroup with SRP Batcher OFF".
    /// </para>
    /// </summary>
    [CreateAssetMenu(fileName = "TitanOrbitPresentationConfig", menuName = "Titan Orbit/Presentation Config")]
    public class TitanOrbitPresentationConfig : ScriptableObject
    {
        static TitanOrbitPresentationConfig s_Instance;

        /// <summary>
        /// Designer toggle — still gated at runtime by WebGL and SRP Batcher.
        /// Default false: thrusters / nameplates / muzzle live on hybrid GameObject proxies.
        /// EG + hybrid together created a stuck cosmetic hull and a second choppy EG mesh.
        /// </summary>
        [SerializeField] bool useEntitiesGraphicsForShips = false;

        /// <summary>One-time log when we fall back to hybrid because SRP Batcher is off.</summary>
        static bool s_LoggedSrpBatcherFallback;

        /// <summary>Loads <c>Resources/TitanOrbitPresentationConfig</c> once per session.</summary>
        public static TitanOrbitPresentationConfig Instance
        {
            get
            {
                if (s_Instance == null)
                    s_Instance = Resources.Load<TitanOrbitPresentationConfig>("TitanOrbitPresentationConfig");
                return s_Instance;
            }
        }

        /// <summary>
        /// True when ship hulls should render through Entities Graphics (pure ECS client path).
        /// [TITAN-ORBIT] WebGL always uses hybrid GameObject proxies — Entities Graphics needs compute
        /// shaders, which WebGL does not expose; forcing EG on WebGL participated in CreateClientWorld OOB.
        /// Also false when the active URP asset has SRP Batcher disabled.
        /// </summary>
        public static bool UseEntitiesGraphicsForShips
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return false;
#else
                // --- Asset opt-out ---
                if (Instance != null && !Instance.useEntitiesGraphicsForShips)
                    return false;

                // --- SRP Batcher required for BatchRendererGroup ---
                if (!IsSrpBatcherEnabled())
                {
                    if (!s_LoggedSrpBatcherFallback)
                    {
                        s_LoggedSrpBatcherFallback = true;
                        Debug.LogWarning(
                            "[Presentation] SRP Batcher is OFF on the active URP asset — " +
                            "using hybrid GameObject ship proxies instead of Entities Graphics. " +
                            "Enable SRP Batcher on PC_RPAsset / Mobile_RPAsset to use EG ships.");
                    }

                    return false;
                }

                return true;
#endif
            }
        }

        /// <summary>
        /// Reads the active URP asset's <c>useSRPBatcher</c> flag (Quality override, else Graphics).
        /// </summary>
        public static bool IsSrpBatcherEnabled()
        {
            // --- Active pipeline (Quality can override Graphics default) ---
            var pipeline = QualitySettings.renderPipeline != null
                ? QualitySettings.renderPipeline
                : GraphicsSettings.currentRenderPipeline;

            if (pipeline is UniversalRenderPipelineAsset urp)
                return urp.useSRPBatcher;

            return false;
        }

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_Instance = null;
            s_LoggedSrpBatcherFallback = false;
        }
#endif
    }
}
