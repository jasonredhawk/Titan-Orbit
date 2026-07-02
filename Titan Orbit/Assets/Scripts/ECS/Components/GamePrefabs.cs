using Unity.Entities;

namespace TitanOrbit.ECS
{
    public struct GamePrefabs : IComponentData
    {
        public Entity Ship;
        public Entity Planet;
        public Entity Asteroid;
        public Entity Gem;
        public Entity PeopleTransport;
    }
}
