using TitanOrbit.Data;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// [UNITY] Scene singleton MonoBehaviour that bakes ghost prefab entity references into
    /// <see cref="GamePrefabs"/> and optional <see cref="MapGenerationConfig"/>. Server map
    /// generation and team spawn instantiate entities from these baked prefab handles via ECB.
    /// Place on NceGameRoot or equivalent bootstrap object in the NetCode SubScene.
    /// </summary>
    public class GamePrefabsRegistryAuthoring : MonoBehaviour
    {
        /// <summary>[NETCODE] Starship ghost prefab GameObject (must have StarshipGhostAuthoring).</summary>
        public GameObject ShipPrefab;

        /// <summary>[NETCODE] Planet ghost prefab GameObject (must have PlanetGhostAuthoring).</summary>
        public GameObject PlanetPrefab;

        /// <summary>[NETCODE] Asteroid ghost prefab GameObject (must have AsteroidGhostAuthoring).</summary>
        public GameObject AsteroidPrefab;

        /// <summary>[NETCODE] Gem ghost prefab GameObject (must have GemGhostAuthoring).</summary>
        public GameObject GemPrefab;

        /// <summary>[NETCODE] People transport ghost prefab GameObject.</summary>
        public GameObject PeopleTransportPrefab;

        /// <summary>
        /// [TITAN-ORBIT] Procedural map bounds and counts. Also assign on NceGameRoot >
        /// MapGenerationSettingsLoader for play-mode without subscene rebake.
        /// </summary>
        [Tooltip("Procedural map bounds. Also assign on NceGameRoot > MapGenerationSettingsLoader for play-mode without subscene rebake.")]
        public MapGenerationSettings MapGenerationSettings;

        /// <summary>[ECS/DOTS] Nested Baker — writes singleton GamePrefabs + MapGenerationConfig.</summary>
        class Baker : Baker<GamePrefabsRegistryAuthoring>
        {
            /// <summary>
            /// [ECS/DOTS] Bakes prefab entity references and map generation config onto a registry singleton.
            /// </summary>
            public override void Bake(GamePrefabsRegistryAuthoring authoring)
            {
                // --- Registry entity (no transform) ---
                // [ECS/DOTS] TransformUsageFlags.None — this entity is a data singleton, not in world space.
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new GamePrefabs
                {
                    Ship = GetEntity(authoring.ShipPrefab, TransformUsageFlags.Dynamic),
                    Planet = GetEntity(authoring.PlanetPrefab, TransformUsageFlags.Dynamic),
                    Asteroid = GetEntity(authoring.AsteroidPrefab, TransformUsageFlags.Dynamic),
                    Gem = GetEntity(authoring.GemPrefab, TransformUsageFlags.Dynamic),
                    PeopleTransport = GetEntity(authoring.PeopleTransportPrefab, TransformUsageFlags.Dynamic),
                });

                // --- Map generation config from ScriptableObject or defaults ---
                var settings = authoring.MapGenerationSettings;
                AddComponent(entity, settings != null
                    ? MapGenerationConfigUtility.FromSettings(settings)
                    : MapGenerationConfigUtility.Default());
            }
        }
    }
}
