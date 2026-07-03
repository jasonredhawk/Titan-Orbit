using UnityEngine;

namespace TitanOrbit.Core
{
    /// <summary>Minimal debug flags shim for OrbitStationUI ship-tree helpers.</summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        [SerializeField] bool debugFreeShipUpgradeTree;

        public bool DebugFreeShipUpgradeTree => debugFreeShipUpgradeTree;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }
    }
}
