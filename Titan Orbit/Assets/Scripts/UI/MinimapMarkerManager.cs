using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// [LEGACY] Scene singleton placeholder for attack/defend minimap markers. Full marker RPC
    /// flow is not yet ported to NetCode for Entities — <see cref="MinimapController"/> owns
    /// most blip logic today. Retained so scenes with this component do not break on load.
    /// </summary>
    public class MinimapMarkerManager : MonoBehaviour
    {
        /// <summary>First instance wins; duplicates self-destruct.</summary>
        public static MinimapMarkerManager Instance { get; private set; }

        /// <summary>[UNITY] Awake — register singleton for legacy marker UI hooks.</summary>
        void Awake()
        {
            // --- Unity lifecycle ---
            if (Instance == null)
                Instance = this;
            else if (Instance != this)
                Destroy(gameObject);
        }
    }
}
