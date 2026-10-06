using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// [LEGACY] Scene singleton leftover from the old Attack/Defend minimap pins.
    /// Team orders now go through <see cref="ShipCommsPanel"/> Comms Matrix.
    /// <see cref="MinimapController"/> owns blip logic. Retained so scenes with this
    /// component do not break on load.
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
