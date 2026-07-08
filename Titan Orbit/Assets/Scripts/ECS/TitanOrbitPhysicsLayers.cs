using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Collision layers for the Unity Physics world. Ships bounce off other ships and world bodies
    /// (planets/asteroids) only. Gems and people-transports are gameplay-scripted movers, so they do
    /// not physically collide with ships (ships collect gems / pass through transports by design).
    /// </summary>
    public static class TitanOrbitPhysicsLayers
    {
        public const uint Ships = 1u << 0;
        public const uint World = 1u << 1;
        public const uint Gems = 1u << 2;
        public const uint Transports = 1u << 3;

        public static readonly CollisionFilter Ship = new CollisionFilter
        {
            BelongsTo = Ships,
            CollidesWith = Ships | World,
            GroupIndex = 0,
        };

        public static readonly CollisionFilter WorldStatic = new CollisionFilter
        {
            BelongsTo = World,
            CollidesWith = Ships | World | Gems | Transports,
            GroupIndex = 0,
        };

        public static readonly CollisionFilter Gem = new CollisionFilter
        {
            BelongsTo = Gems,
            CollidesWith = World,
            GroupIndex = 0,
        };

        public static readonly CollisionFilter Transport = new CollisionFilter
        {
            BelongsTo = Transports,
            CollidesWith = World,
            GroupIndex = 0,
        };
    }
}
