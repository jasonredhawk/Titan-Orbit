using UnityEngine;

namespace TitanOrbit.Services
{
    /// <summary>
    /// Optional host for <see cref="TitanOrbitIapManager"/> and <see cref="TitanOrbitGrowIntegration"/> on one GameObject.
    /// Mark the GameObject DontDestroyOnLoad in the Inspector or add this component to persist across scenes.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class TitanOrbitServiceHub : MonoBehaviour
    {
        [SerializeField] bool dontDestroyOnLoad = true;

        void Awake()
        {
            if (dontDestroyOnLoad)
                DontDestroyOnLoad(gameObject);
        }
    }
}
