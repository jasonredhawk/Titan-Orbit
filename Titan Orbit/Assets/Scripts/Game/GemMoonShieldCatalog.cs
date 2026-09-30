using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// References the Archanor MatrixShield prefabs so player builds include them.
    /// <see cref="GemMoonShieldPrefabLibrary"/> loads this from Resources. The Editor
    /// can still open the prefabs by path; WebGL has no AssetDatabase, and a bare
    /// <c>Resources.Load</c> name does not pull an unreferenced prefab into the build.
    /// </summary>
    [CreateAssetMenu(fileName = "GemMoonShieldCatalog", menuName = "Titan Orbit/Gem Moon Shield Catalog", order = 49)]
    public class GemMoonShieldCatalog : ScriptableObject
    {
        public const string ResourcesLoadName = "GemMoonShieldCatalog";

        public GameObject red;
        public GameObject blue;
        public GameObject green;
        public GameObject modular;
    }
}
