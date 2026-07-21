using TitanOrbit;
using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>
    /// Scene singleton that holds designer-tunable debug flags for local play.
    /// Lives on <c>NceGameRoot</c> in SampleScene (Inspector → Game Manager).
    /// Moon orbit ship-tree UI reads <see cref="DebugFreeShipUpgradeTree"/> so you can click any
    /// upgrade-tree node for free during testing. Publishes the same value to
    /// <see cref="TitanOrbitDebugFlags"/> so the ECS server store can honor free selects without
    /// referencing this Core assembly. Dedicated server builds normally leave the flag false.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        /// <summary>Global access for UI and tools that need debug flags without scene references.</summary>
        public static GameManager Instance { get; private set; }

        // [UNITY] Inspector toggle — when true, ship upgrade tree treats all nodes as free / clickable.
        [Header("Debug — Ship Upgrade Tree")]
        [Tooltip("When enabled, the moon orbit ship upgrade tree unlocks every node. Click any ship to try it for free (local Editor / development only).")]
        [SerializeField] bool debugFreeShipUpgradeTree;

        [Header("Debug — Asteroid Destroy Hitch")]
        [Tooltip("Logs [AsteroidDestroy] timings in the Console when an asteroid explodes (local gem Instantiates + urgent gem proxies). Filter the Console with that tag.")]
        [SerializeField] bool debugLogAsteroidDestroyPerf = true;

        /// <summary>True when designers enabled free upgrades in the Inspector (client + local-host convenience).</summary>
        public bool DebugFreeShipUpgradeTree => debugFreeShipUpgradeTree;

        /// <summary>True when asteroid-destroy hitch logging is enabled in the Inspector.</summary>
        public bool DebugLogAsteroidDestroyPerf => debugLogAsteroidDestroyPerf;

        /// <summary>
        /// Safe static check used by moon orbit UI. Also true when the Shared flag was published
        /// (covers the brief window before Instance is set, and keeps UI/server in sync).
        /// </summary>
        public static bool IsDebugFreeShipUpgradeTreeActive =>
            TitanOrbitDebugFlags.FreeShipUpgradeTree
            || (Instance != null && Instance.debugFreeShipUpgradeTree);

        /// <summary>
        /// Ensures a GameManager exists for Play Mode. Prefer the component on NceGameRoot so you can
        /// toggle the flag in the Inspector. Never creates a second empty GameManager that would
        /// steal Instance and wipe the Inspector toggle.
        /// </summary>
        public static GameManager EnsureExists()
        {
            if (Instance != null)
            {
                Instance.PublishDebugFlags();
                return Instance;
            }

            var existing = Object.FindFirstObjectByType<GameManager>();
            if (existing != null)
            {
                // Awake may not have run yet — adopt this scene object and publish its Inspector value.
                Instance = existing;
                existing.PublishDebugFlags();
                return existing;
            }

            var go = new GameObject("GameManager");
            return go.AddComponent<GameManager>();
        }

        /// <summary>
        /// [UNITY] Awake — enforces a single GameManager instance and publishes debug flags to Shared.
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
            PublishDebugFlags();
        }

        /// <summary>
        /// [UNITY] OnValidate — keeps Shared flags in sync when you flip the Inspector checkbox in Edit Mode
        /// (and again when entering Play Mode after a domain reload).
        /// </summary>
        void OnValidate()
        {
            // Only the live singleton (or this object before Awake) should publish.
            if (Instance != null && Instance != this)
                return;
            PublishDebugFlags();
        }

        /// <summary>
        /// [UNITY] OnDestroy — clears the static reference and Shared flag so a reloaded scene starts clean.
        /// </summary>
        void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
                TitanOrbitDebugFlags.FreeShipUpgradeTree = false;
                TitanOrbitDebugFlags.LogAsteroidDestroyPerf = false;
            }
        }

        /// <summary>
        /// Copies Inspector fields into <see cref="TitanOrbitDebugFlags"/> for ECS / other assemblies.
        /// </summary>
        public void PublishDebugFlags()
        {
            // [TITAN-ORBIT] ECS MoonOrbitStoreSystem cannot reference TitanOrbit.Core — Shared bridge.
            TitanOrbitDebugFlags.FreeShipUpgradeTree = debugFreeShipUpgradeTree;
            TitanOrbitDebugFlags.LogAsteroidDestroyPerf = debugLogAsteroidDestroyPerf;
        }
    }
}
