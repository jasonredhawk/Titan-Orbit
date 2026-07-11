using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Scene singleton that holds designer-tunable debug flags for the client build.
    /// Lives on a persistent GameObject in menu/game scenes. Orbit-station ship-tree UI
    /// reads <see cref="DebugFreeShipUpgradeTree"/> to bypass gem costs during local testing.
    /// Not used by the dedicated server — server authority ignores client debug toggles.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        /// <summary>Global access for UI and tools that need debug flags without scene references.</summary>
        public static GameManager Instance { get; private set; }

        // [UNITY] Inspector toggle — when true, ship upgrade tree treats all nodes as free.
        [SerializeField] bool debugFreeShipUpgradeTree;

        /// <summary>True when designers enabled free upgrades in the Inspector (client-only convenience).</summary>
        public bool DebugFreeShipUpgradeTree => debugFreeShipUpgradeTree;

        /// <summary>
        /// [UNITY] Awake — enforces a single GameManager instance across scene loads.
        /// Duplicate objects self-destruct so UI always reads one source of debug flags.
        /// </summary>
        void Awake()
        {
            // --- Unity lifecycle ---
            // [STANDARD] Classic singleton guard — destroy late duplicates, keep the first.
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        /// <summary>
        /// [UNITY] OnDestroy — clears the static reference so a reloaded scene can register anew.
        /// </summary>
        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }
    }
}
