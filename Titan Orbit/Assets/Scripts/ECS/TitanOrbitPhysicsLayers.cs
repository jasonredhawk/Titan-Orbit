using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Collision layer bit masks and pre-built <see cref="CollisionFilter"/> values for Unity Physics.
    /// Baked onto ghost prefabs in *GhostAuthoring bakers. Ships bounce off ships and world bodies
    /// (planets/asteroids) via Unity.Physics once movers wrap onto the canonical chart.
    /// Gems and people-transports are scripted movers with world collision only — see ship-simulation rule.
    /// </summary>
    public static class TitanOrbitPhysicsLayers
    {
        // --- Type members ---
        /// <summary>Layer bit — player and AI ships (dynamic bodies).</summary>
        public const uint Ships = 1u << 0;

        /// <summary>Layer bit — planets and asteroids (static bodies).</summary>
        public const uint World = 1u << 1;

        /// <summary>Layer bit — gem pickups (scripted motion, world collision only).</summary>
        public const uint Gems = 1u << 2;

        /// <summary>Layer bit — people transport projectiles (scripted motion, world collision only).</summary>
        public const uint Transports = 1u << 3;

        /// <summary>
        /// [TITAN-ORBIT] Ship hull filter — collides with other ships and world static geometry only.
        /// Used by <c>StarshipGhostAuthoring</c> baker.
        /// </summary>
        public static readonly CollisionFilter Ship = new CollisionFilter
        {
            BelongsTo = Ships,
            CollidesWith = Ships | World,
            GroupIndex = 0,
        };

        /// <summary>
        /// Broadphase-only: other ships, not planets/asteroids. Kept for wrap / aim queries
        /// that must ignore world statics.
        /// </summary>
        public static readonly CollisionFilter ShipVsShips = new CollisionFilter
        {
            BelongsTo = Ships,
            CollidesWith = Ships,
            GroupIndex = 0,
        };

        /// <summary>
        /// Planet and asteroid static bodies — ships, gems, and transports may collide with world.
        /// Used by <c>PlanetGhostAuthoring</c> and <c>AsteroidGhostAuthoring</c>.
        /// </summary>
        public static readonly CollisionFilter WorldStatic = new CollisionFilter
        {
            BelongsTo = World,
            CollidesWith = Ships | World | Gems | Transports,
            GroupIndex = 0,
        };

        /// <summary>
        /// Gem pickup collider — world only so ships collect via proximity logic, not hull bounce.
        /// </summary>
        public static readonly CollisionFilter Gem = new CollisionFilter
        {
            BelongsTo = Gems,
            CollidesWith = World,
            GroupIndex = 0,
        };

        /// <summary>
        /// People transport projectile — world only; ships pass through by design.
        /// </summary>
        public static readonly CollisionFilter Transport = new CollisionFilter
        {
            BelongsTo = Transports,
            CollidesWith = World,
            GroupIndex = 0,
        };
    }
}
