using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>Legacy scene hook retained until attack/defend markers are ported to NetCode for Entities.</summary>
    public class MinimapMarkerManager : MonoBehaviour
    {
        public static MinimapMarkerManager Instance { get; private set; }

        void Awake()
        {
            if (Instance == null)
                Instance = this;
            else if (Instance != this)
                Destroy(gameObject);
        }
    }
}
