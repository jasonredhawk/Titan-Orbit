using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.ECS.Authoring
{
    public class GamePrefabsRegistryAuthoring : MonoBehaviour
    {
        public GameObject ShipPrefab;
        public GameObject PlanetPrefab;
        public GameObject AsteroidPrefab;
        public GameObject GemPrefab;
        public GameObject PeopleTransportPrefab;

        class Baker : Baker<GamePrefabsRegistryAuthoring>
        {
            public override void Bake(GamePrefabsRegistryAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new GamePrefabs
                {
                    Ship = GetEntity(authoring.ShipPrefab, TransformUsageFlags.Dynamic),
                    Planet = GetEntity(authoring.PlanetPrefab, TransformUsageFlags.Dynamic),
                    Asteroid = GetEntity(authoring.AsteroidPrefab, TransformUsageFlags.Dynamic),
                    Gem = GetEntity(authoring.GemPrefab, TransformUsageFlags.Dynamic),
                    PeopleTransport = GetEntity(authoring.PeopleTransportPrefab, TransformUsageFlags.Dynamic),
                });
            }
        }
    }
}
