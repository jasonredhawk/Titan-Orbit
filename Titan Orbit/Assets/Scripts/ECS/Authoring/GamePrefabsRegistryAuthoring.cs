using TitanOrbit.Data;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    /// <summary>
    /// Scene singleton that bakes ghost prefab entity references into <see cref="GamePrefabs"/>
    /// and optional <see cref="MapGenerationConfig"/>. Server map generation and team spawn
    /// instantiate entities from these baked prefab handles.
    /// </summary>
    public class GamePrefabsRegistryAuthoring : MonoBehaviour
    {
        public GameObject ShipPrefab;
        public GameObject PlanetPrefab;
        public GameObject AsteroidPrefab;
        public GameObject GemPrefab;
        public GameObject PeopleTransportPrefab;
        [Tooltip("Procedural map bounds. Also assign on NceGameRoot > MapGenerationSettingsLoader for play-mode without subscene rebake.")]
        public MapGenerationSettings MapGenerationSettings;

        class Baker : Baker<GamePrefabsRegistryAuthoring>
        {
            public override void Bake(GamePrefabsRegistryAuthoring authoring)
            {
                // [ECS/DOTS] TransformUsageFlags.None — registry entity has no transform.
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new GamePrefabs
                {
                    Ship = GetEntity(authoring.ShipPrefab, TransformUsageFlags.Dynamic),
                    Planet = GetEntity(authoring.PlanetPrefab, TransformUsageFlags.Dynamic),
                    Asteroid = GetEntity(authoring.AsteroidPrefab, TransformUsageFlags.Dynamic),
                    Gem = GetEntity(authoring.GemPrefab, TransformUsageFlags.Dynamic),
                    PeopleTransport = GetEntity(authoring.PeopleTransportPrefab, TransformUsageFlags.Dynamic),
                });

                var settings = authoring.MapGenerationSettings;
                AddComponent(entity, settings != null
                    ? MapGenerationConfigUtility.FromSettings(settings)
                    : MapGenerationConfigUtility.Default());
            }
        }
    }
}
