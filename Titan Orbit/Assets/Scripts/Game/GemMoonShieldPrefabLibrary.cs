using System.Collections.Generic;
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

        /// <summary>Resources names if the designer copied the prefabs under Resources/.</summary>
        const string RedResourcesName = "MatrixShieldRed";
        const string BlueResourcesName = "MatrixShieldBlue";
        const string GreenResourcesName = "MatrixShieldGreen";
        const string ModularResourcesName = "MatrixShieldModular";

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

        /// <summary>
        /// Appends each unique loaded shield prefab into <paramref name="dst"/> (clears first).
        /// Join-load graphics warmup Instantiates these once so spawn LateUpdate does not.
        /// </summary>
        /// <param name="dst">Destination list owned by the warmup worker.</param>
        public static void CopyUniquePrefabs(List<GameObject> dst)
        {
            if (dst == null)
                return;

            dst.Clear();
            EnsureLoaded();
            TryAddUnique(dst, _red);
            TryAddUnique(dst, _blue);
            TryAddUnique(dst, _green);
            TryAddUnique(dst, _modular);
        }

        /// <summary>Adds <paramref name="prefab"/> once (same InstanceID is skipped).</summary>
        static void TryAddUnique(List<GameObject> dst, GameObject prefab)
        {
            if (prefab == null)
                return;
            for (int i = 0; i < dst.Count; i++)
            {
                if (dst[i] == prefab)
                    return;
            }

            dst.Add(prefab);
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
            // [UNITY] Player builds have no AssetDatabase — Resources copies win if present.
            if (_red == null)
                _red = Resources.Load<GameObject>(RedResourcesName);
            if (_blue == null)
                _blue = Resources.Load<GameObject>(BlueResourcesName);
            if (_green == null)
                _green = Resources.Load<GameObject>(GreenResourcesName);
            if (_modular == null)
                _modular = Resources.Load<GameObject>(ModularResourcesName);
        }
    }
}
