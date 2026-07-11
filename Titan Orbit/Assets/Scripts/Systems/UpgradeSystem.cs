using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Legacy upgrade-tree ScriptableObject holder for <see cref="UI.OrbitStationUI"/>. ECS sim
    /// applies real stat changes server-side; this MonoBehaviour only supplies designer tree data
    /// from Resources. Spawned by <see cref="UI.OrbitStationBootstrap"/> if not in scene.
    /// </summary>
    public class UpgradeSystem : MonoBehaviour
    {
        /// <summary>Singleton for UI code that cannot reference scene instances directly.</summary>
        public static UpgradeSystem Instance { get; private set; }

        // [UNITY] Optional inspector reference; falls back to Resources/UpgradeTree at runtime.
        [SerializeField] UpgradeTree upgradeTree;

        /// <summary>Designer-authored node graph for orbit-station upgrade UI display.</summary>
        public UpgradeTree UpgradeTree => upgradeTree;

        /// <summary>[UNITY] Awake — singleton guard and Resources fallback load.</summary>
        void Awake()
        {
            // --- Unity lifecycle ---
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            if (upgradeTree == null)
                upgradeTree = Resources.Load<UpgradeTree>("UpgradeTree");
        }

        /// <summary>[UNITY] OnDestroy — release singleton when bootstrap object is destroyed.</summary>
        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }
    }
}
