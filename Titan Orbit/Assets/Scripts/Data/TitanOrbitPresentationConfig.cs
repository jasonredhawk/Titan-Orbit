using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Project-wide presentation mode. When <see cref="UseEntitiesGraphicsForShips"/> is true, ships render
    /// via Entities Graphics on the client and the hybrid <c>EcsWorldVisualizer</c> ship path is disabled.
    /// Planets, gems, and asteroids may remain hybrid until migrated.
    /// </summary>
    [CreateAssetMenu(fileName = "TitanOrbitPresentationConfig", menuName = "Titan Orbit/Presentation Config")]
    public class TitanOrbitPresentationConfig : ScriptableObject
    {
        static TitanOrbitPresentationConfig s_Instance;

        [SerializeField] bool useEntitiesGraphicsForShips = true;

        /// <summary>Loads <c>Resources/TitanOrbitPresentationConfig</c> once per session.</summary>
        public static TitanOrbitPresentationConfig Instance
        {
            get
            {
                if (s_Instance == null)
                    s_Instance = Resources.Load<TitanOrbitPresentationConfig>("TitanOrbitPresentationConfig");
                return s_Instance;
            }
        }

        /// <summary>True when ship hulls should render through Entities Graphics (pure ECS client path).</summary>
        public static bool UseEntitiesGraphicsForShips =>
            Instance == null || Instance.useEntitiesGraphicsForShips;
    }
}
