using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Systems
{
    /// <summary>Legacy upgrade tree accessor for OrbitStationUI (ECS shim).</summary>
    public class UpgradeSystem : MonoBehaviour
    {
        public static UpgradeSystem Instance { get; private set; }

        [SerializeField] UpgradeTree upgradeTree;

        public UpgradeTree UpgradeTree => upgradeTree;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            if (upgradeTree == null)
                upgradeTree = Resources.Load<UpgradeTree>("UpgradeTree");
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }
    }
}
