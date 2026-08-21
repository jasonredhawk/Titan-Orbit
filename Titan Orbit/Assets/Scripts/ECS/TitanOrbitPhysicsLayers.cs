using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Collision layer bit masks and pre-built <see cref="CollisionFilter"/> values for Unity Physics.
    /// Baked onto ghost prefabs in *GhostAuthoring bakers. Ships are dynamic bodies: PhysX
    /// integrates <c>PhysicsVelocity</c> and owns ship↔world (planets, rocks, walls).
    /// Ship↔ship is a cheap hull-sphere resolve — remotes are interpolated, not predicted
    /// PhysX bodies, so PhysX ship pairs look like they pass through each other.
    /// Gems and people-transports stay scripted movers with world collision only.
    /// </summary>
    public static class TitanOrbitPhysicsLayers
    {
        /// <summary>Layer bit — player and AI ships (dynamic bodies).</summary>
        public const uint Ships = 1u << 0;

        /// <summary>Layer bit — planets, asteroids, and map-edge walls (static bodies).</summary>
        public const uint World = 1u << 1;

        /// <summary>Layer bit — gem pickups (scripted motion, world collision only).</summary>
        public const uint Gems = 1u << 2;

        /// <summary>Layer bit — people transport projectiles (scripted motion, world collision only).</summary>
        public const uint Transports = 1u << 3;

        /// <summary>
        /// Ship hull filter — PhysX pairs with world only. Ship↔ship is
        /// <c>ShipShipHullContactSystem</c> (two spheres). Used by baker and hull rebuild.
        /// </summary>
        public static readonly CollisionFilter Ship = new CollisionFilter
        {
            BelongsTo = Ships,
            CollidesWith = World,
            GroupIndex = 0,
        };

        /// <summary>
        /// Planet, asteroid, and edge-wall static bodies. Ships bounce here; gems and
        /// transports still collide with world. Used by planet/asteroid bakers and wall ensure.
        /// </summary>
        public static readonly CollisionFilter WorldStatic = new CollisionFilter
        {
            BelongsTo = World,
            CollidesWith = World | Ships | Gems | Transports,
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
