using TitanOrbit.Data;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Fuse HP for a deployed mine. Health is remaining lifetime ×
    /// <see cref="MineCatalog.DefaultMaxHealth"/> — not a ghosted per-tick field.
    /// Server explode and client bar share this so the fill hits 0 when the fuse does.
    /// </summary>
    public static class MineHealthMath
    {
        /// <summary>
        /// Catalog 0 / missing stamp → default 100. Never return 0 (bar divide).
        /// </summary>
        public static int ResolveMaxHealth(int catalogMaxHealth)
        {
            return math.max(1, catalogMaxHealth > 0 ? catalogMaxHealth : MineCatalog.DefaultMaxHealth);
        }

        /// <summary>
        /// 1 at place, 0 at expire. Saturates so late-join / clock jitter cannot go negative.
        /// </summary>
        public static float Remaining01(double placeTime, double expireTime, double now)
        {
            float life = (float)(expireTime - placeTime);
            if (life < 0.001f)
                return now >= expireTime ? 0f : 1f;
            return math.saturate((float)(expireTime - now) / life);
        }

        /// <summary>Linear fuse HP. Contact zeros this on the server before <c>ExplodeMine</c>.</summary>
        public static float CurrentHealth(double placeTime, double expireTime, double now, int maxHealth)
        {
            int max = math.max(1, maxHealth);
            return Remaining01(placeTime, expireTime, now) * max;
        }

        /// <summary>True when remaining fuse HP is 0 (self-destruct).</summary>
        public static bool IsFuseExpired(double placeTime, double expireTime, double now)
        {
            // Unset / default (0,0) stamps must not self-destruct — that ate every mine after the first.
            if (expireTime <= placeTime + 0.05)
                return false;
            return Remaining01(placeTime, expireTime, now) <= 0f;
        }
    }
}
