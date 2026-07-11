using TitanOrbit.Systems;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Creates legacy MonoBehaviour shop singletons if missing after scene load. Orbit-station UI
    /// still references <see cref="UpgradeSystem"/>, <see cref="CardShopSystem"/>, and
    /// <see cref="HomePlanetStoreSystem"/> for data queries and ECS RPC delegation. Runs once
    /// via RuntimeInitializeOnLoadMethod — client only.
    /// </summary>
    public static class OrbitStationBootstrap
    {
        /// <summary>
        /// [UNITY] AfterSceneLoad — ensures DontDestroyOnLoad shop helpers exist before orbit UI opens.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureShopSystems()
        {
            // --- Ensure setup ---
            EnsureSystem<UpgradeSystem>("UpgradeSystem");
            EnsureSystem<CardShopSystem>("CardShopSystem");
            EnsureSystem<HomePlanetStoreSystem>("HomePlanetStoreSystem");
        }

        /// <summary>
        /// Finds existing component or spawns a hidden GameObject with DontDestroyOnLoad.
        /// </summary>
        static void EnsureSystem<T>(string objectName) where T : Component
        {
            if (Object.FindFirstObjectByType<T>() != null)
                return;

            var go = new GameObject(objectName);
            Object.DontDestroyOnLoad(go);
            go.AddComponent<T>();
        }
    }
}
