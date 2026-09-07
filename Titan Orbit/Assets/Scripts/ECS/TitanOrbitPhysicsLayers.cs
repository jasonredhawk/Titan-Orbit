using TitanOrbit.Core;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Collision layer bit masks and pre-built <see cref="CollisionFilter"/> values for Unity Physics.
    /// Every gameplay body is one sphere: ships, planets, moons, moon shields, asteroids.
    /// Gems and people-transports are scripted movers with world collision only.
    /// </summary>
    public static class TitanOrbitPhysicsLayers
    {
        // --- Type members ---
        /// <summary>Layer bit — player and AI ships (dynamic bodies).</summary>
        public const uint Ships = 1u << 0;

        /// <summary>Layer bit — planets (static bodies). Asteroids use <see cref="Asteroids"/>.</summary>
        public const uint World = 1u << 1;

        /// <summary>Layer bit — gem pickups (scripted motion, world collision only).</summary>
        public const uint Gems = 1u << 2;

        /// <summary>Layer bit — people transport projectiles (scripted motion, world collision only).</summary>
        public const uint Transports = 1u << 3;

        /// <summary>Layer bit — asteroids (static spheres). Ships collide via PhysX.</summary>
        public const uint Asteroids = 1u << 4;

        /// <summary>Unowned / neutral moon shields — every ship collides.</summary>
        public const uint NeutralShields = 1u << 5;

        /// <summary>Team A moon shields. Team A ships omit this bit so they pass through.</summary>
        public const uint TeamAShields = 1u << 6;

        public const uint TeamBShields = 1u << 7;
        public const uint TeamCShields = 1u << 8;
        public const uint TeamDShields = 1u << 9;
        public const uint TeamEShields = 1u << 10;

        /// <summary>All moon-shield bits (neutral + Team A–E).</summary>
        public const uint AllShields =
            NeutralShields | TeamAShields | TeamBShields | TeamCShields | TeamDShields | TeamEShields;

        /// <summary>
        /// Bake / unassigned ship — collides with every shield (no friendly team yet).
        /// Live ships use <see cref="ShipForTeam"/>.
        /// </summary>
        public static readonly CollisionFilter Ship = ShipForTeam(TeamId.None);

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
        /// Planet static bodies — ships, gems, and transports may collide with world.
        /// Asteroids use <see cref="AsteroidStatic"/>.
        /// </summary>
        public static readonly CollisionFilter WorldStatic = new CollisionFilter
        {
            BelongsTo = World,
            CollidesWith = Ships | World | Gems | Transports,
            GroupIndex = 0,
        };

        /// <summary>Asteroid static spheres — ships, gems, and transports collide.</summary>
        public static readonly CollisionFilter AsteroidStatic = new CollisionFilter
        {
            BelongsTo = Asteroids,
            CollidesWith = Ships | Gems | Transports,
            GroupIndex = 0,
        };

        /// <summary>
        /// Gem pickup collider — world + asteroids so pickups rest on rocks; ships collect via proximity.
        /// </summary>
        public static readonly CollisionFilter Gem = new CollisionFilter
        {
            BelongsTo = Gems,
            CollidesWith = World | Asteroids,
            GroupIndex = 0,
        };

        /// <summary>
        /// People transport projectile — world + asteroids; ships pass through by design.
        /// </summary>
        public static readonly CollisionFilter Transport = new CollisionFilter
        {
            BelongsTo = Transports,
            CollidesWith = World | Asteroids,
            GroupIndex = 0,
        };

        /// <summary>Shield layer bit for this planet owner. Neutral / None → <see cref="NeutralShields"/>.</summary>
        public static uint ShieldBitForOwner(TeamId owner)
        {
            switch (owner)
            {
                case TeamId.TeamA: return TeamAShields;
                case TeamId.TeamB: return TeamBShields;
                case TeamId.TeamC: return TeamCShields;
                case TeamId.TeamD: return TeamDShields;
                case TeamId.TeamE: return TeamEShields;
                default: return NeutralShields;
            }
        }

        /// <summary>
        /// Ship sphere filter. Friendly moon shields are omitted from CollidesWith so PhysX
        /// never generates the pair (collect-skip is not enough — the solver would still shove).
        /// Written when team is assigned, not every tick.
        /// </summary>
        public static CollisionFilter ShipForTeam(TeamId team)
        {
            uint shields = AllShields;
            if (team != TeamId.None)
                shields &= ~ShieldBitForOwner(team);

            return new CollisionFilter
            {
                BelongsTo = Ships,
                CollidesWith = Ships | World | Asteroids | shields,
                GroupIndex = 0,
            };
        }

        /// <summary>Moon-shield sphere for this planet owner. Collides with ships only.</summary>
        public static CollisionFilter MoonShieldForOwner(TeamId owner)
        {
            return new CollisionFilter
            {
                BelongsTo = ShieldBitForOwner(owner),
                CollidesWith = Ships,
                GroupIndex = 0,
            };
        }

        /// <summary>True when two filters have the same BelongsTo / CollidesWith / GroupIndex.</summary>
        public static bool FiltersEqual(in CollisionFilter a, in CollisionFilter b)
        {
            return a.BelongsTo == b.BelongsTo
                && a.CollidesWith == b.CollidesWith
                && a.GroupIndex == b.GroupIndex;
        }
    }
}
