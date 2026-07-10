using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Collision layers for the Unity Physics world. Ships bounce off other ships and world bodies
    /// (planets/asteroids) only. Gems and people-transports are gameplay-scripted movers, so they do
    /// not physically collide with ships (ships collect gems / pass through transports by design).
    /// </summary>
    /// <summary>
    /// Collision layer bit masks and pre-built CollisionFilter values for Unity Physics.
    /// Baked onto ghost prefabs in *GhostAuthoring bakers. See ship-simulation rule for matrix.
    /// </summary>
    public static class TitanOrbitPhysicsLayers
    {
        /// <summary>Layer bit — player and AI ships (dynamic bodies).</summary>
        public const uint Ships = 1u << 0;
        /// <summary>Layer bit — planets and asteroids (static bodies).</summary>
        public const uint World = 1u << 1;
        /// <summary>Layer bit — gem pickups (scripted motion, world collision only).</summary>
        public const uint Gems = 1u << 2;
        /// <summary>Layer bit — people transport projectiles.</summary>
        public const uint Transports = 1u << 3;

        /// <summary>[TITAN-ORBIT] Ships collide with other ships and world static geometry only.</summary>
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
