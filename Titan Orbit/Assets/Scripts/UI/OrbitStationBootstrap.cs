using TitanOrbit.Systems;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>Ensures legacy shop singletons exist for OrbitStationUI.</summary>
    public static class OrbitStationBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureShopSystems()
        {
            EnsureSystem<UpgradeSystem>("UpgradeSystem");
            EnsureSystem<CardShopSystem>("CardShopSystem");
            EnsureSystem<HomePlanetStoreSystem>("HomePlanetStoreSystem");
        }

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
