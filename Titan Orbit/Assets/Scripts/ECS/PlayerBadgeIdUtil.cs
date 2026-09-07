namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared clamp for player profile badge ids used on the Main Menu and name RPCs.
    /// Stable ids match the number in <c>Badge (N).png</c>. <see cref="None"/> means no badge.
    /// The dedicated server has no sprites and still uses this so junk RPC ids never land on the roster.
    /// </summary>
    public static class PlayerBadgeIdUtil
    {
        /// <summary>No badge selected — nameplates hide the emblem.</summary>
        public const int None = 0;

        /// <summary>
        /// Highest <c>Badge (N)</c> id in <c>Assets/Scenes/Badges</c>.
        /// Unknown ids clamp to <see cref="None"/> so a future catalog shrink cannot crash clients.
        /// </summary>
        public const int MaxId = 516;

        /// <summary>
        /// Returns <see cref="None"/> when <paramref name="badgeId"/> is outside 0..<see cref="MaxId"/>.
        /// </summary>
        public static int Sanitize(int badgeId)
        {
            if (badgeId < None || badgeId > MaxId)
                return None;
            return badgeId;
        }

        /// <summary>True when this id should paint an emblem (catalog may still miss the sprite).</summary>
        public static bool HasBadge(int badgeId) => badgeId > None && badgeId <= MaxId;
    }
}
