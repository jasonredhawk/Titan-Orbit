using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Loads Archanor MatrixShield prefabs for gem-moon presentation.</summary>
    static class GemMoonShieldPrefabLibrary
    {
        const string RedPath = "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Combat/Shield/MatrixShield/MatrixShieldRed.prefab";
        const string BluePath = "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Combat/Shield/MatrixShield/MatrixShieldBlue.prefab";
        const string GreenPath = "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Combat/Shield/MatrixShield/MatrixShieldGreen.prefab";
        const string ModularPath = "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Combat/Shield/MatrixShield/MatrixShieldModular.prefab";

        static GameObject _red;
        static GameObject _blue;
        static GameObject _green;
        static GameObject _modular;
        static bool _loaded;

        public static GameObject GetPrefab(TeamId team)
        {
            // --- Compute value ---
            EnsureLoaded();
            switch (team)
            {
                case TeamId.TeamA: return _red != null ? _red : _modular;
                case TeamId.TeamB: return _blue != null ? _blue : _modular;
                case TeamId.TeamC: return _green != null ? _green : _modular;
                default: return _modular;
            }
        }

        static void EnsureLoaded()
        {
            // --- Ensure setup ---
            if (_loaded)
                return;
            _loaded = true;

#if UNITY_EDITOR
            _red = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(RedPath);
            _blue = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(BluePath);
            _green = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(GreenPath);
            _modular = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(ModularPath);
#endif
        }
    }
}
