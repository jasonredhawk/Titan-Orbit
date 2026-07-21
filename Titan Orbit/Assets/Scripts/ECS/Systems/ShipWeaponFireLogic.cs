using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared multi-mount fire planner for server bullets and client anticipation VFX.
    /// <para>
    /// [TITAN-ORBIT] Two modes:
    /// <list type="bullet">
    /// <item><b>Full volley</b> — energy covers every mount → all muzzles fire in the same tick.</item>
    /// <item><b>Round-robin drip</b> — energy cannot cover a full volley → fire <b>exactly one</b>
    /// mount from <c>NextMountIndex</c>, then advance +1 so the loop is always 0→1→2→…→0.
    /// Never fires a partial multi-mount burst in drip mode (that skipped every other barrel).</item>
    /// </list>
    /// </para>
    /// Paired with <see cref="BulletSimulationSystem"/> (server) and
    /// <c>ClientLocalBulletVfxBridge</c> (client cosmetics).
    /// <para>
    /// [TITAN-ORBIT] <c>ShipWeaponConfig.BulletDamage</c> and <c>EnergyCostPerShot</c> are
    /// <b>per barrel</b> (average scale-adjusted weapon firePower — not N× sum). Each bullet deals
    /// that damage; a full volley spends <c>EnergyCostPerShot × mountCount</c>.
    /// </para>
    /// </summary>
    public static class ShipWeaponFireLogic
    {
        /// <summary>
        /// Result of planning one fire tick: which mounts shoot and how much energy to spend.
        /// </summary>
        public struct FirePlan
        {
            /// <summary>True when at least one mount should fire this tick.</summary>
            public bool CanFire;

            /// <summary>
            /// True when every mount fires together (energy ≥ full volley cost).
            /// </summary>
            public bool IsFullVolley;

            /// <summary>
            /// First mount index to fire. Full volley starts at 0; drip is the round-robin cursor.
            /// </summary>
            public int StartMountIndex;

            /// <summary>
            /// How many mounts fire this tick (mountCount for full volley; always 1 for drip).
            /// </summary>
            public int FireCount;

            /// <summary>Energy to subtract from <c>ShipState.CurrentEnergy</c> after spawn.</summary>
            public float EnergySpend;

            /// <summary>Damage written on each spawned bullet (per-barrel firePower — not divided).</summary>
            public float DamagePerBullet;

            /// <summary>
            /// Value to write back to <c>ShipWeaponState.NextMountIndex</c> after this fire.
            /// Full volley resets to 0; drip advances by exactly one mount.
            /// </summary>
            public int NextMountIndexAfter;

            /// <summary>Seconds until the next fire tick (<c>1 / fireRate</c>).</summary>
            public float CooldownSeconds;
        }

        /// <summary>
        /// Decides full volley vs single-mount round-robin from current energy and mount count.
        /// Call only after cooldown / Fire-input gates have already passed.
        /// </summary>
        /// <param name="currentEnergy">Ship energy pool right now (server or replicated client).</param>
        /// <param name="energyCostPerBarrel">
        /// Energy for one barrel — usually <c>ShipWeaponConfig.EnergyCostPerShot</c> (per-bullet firePower).
        /// </param>
        /// <param name="bulletDamagePerBarrel">
        /// Damage per bullet — usually <c>ShipWeaponConfig.BulletDamage</c> (not a hull total to split).
        /// </param>
        /// <param name="fireRate">Ship fire rate from <c>ShipWeaponConfig.FireRate</c>.</param>
        /// <param name="mountCount">Number of <see cref="ShipWeaponMountElement"/> entries (≥ 1).</param>
        /// <param name="nextMountIndex">
        /// Current round-robin cursor from <c>ShipWeaponState.NextMountIndex</c> (or client mirror).
        /// </param>
        /// <param name="plan">Filled when returning; <see cref="FirePlan.CanFire"/> is false if energy is too low.</param>
        /// <returns>True when the ship should spawn at least one bullet this tick.</returns>
        public static bool TryPlanFire(
            float currentEnergy,
            float energyCostPerBarrel,
            float bulletDamagePerBarrel,
            float fireRate,
            int mountCount,
            int nextMountIndex,
            out FirePlan plan)
        {
            plan = default;

            // --- Guard degenerate mounts ---
            if (mountCount <= 0)
                return false;

            // --- Per-barrel economy ---
            // [TITAN-ORBIT] BulletDamage / EnergyCostPerShot are per barrel (averaged weapon firePower).
            // Gun count must not change per-hit damage — only XY scale and ship/attribute levels do.
            float energyCostPerMount = math.max(0.01f, energyCostPerBarrel);
            float damagePerBullet = math.max(1f, bulletDamagePerBarrel);
            float fullVolleyCost = energyCostPerMount * mountCount;
            float cooldownSeconds = 1f / math.max(0.1f, fireRate);

            // --- Mode A: full volley (energy covers every muzzle at once) ---
            if (currentEnergy >= fullVolleyCost)
            {
                plan.CanFire = true;
                plan.IsFullVolley = true;
                plan.StartMountIndex = 0;
                plan.FireCount = mountCount;
                plan.EnergySpend = fullVolleyCost;
                plan.DamagePerBullet = damagePerBullet;
                // Reset drip cursor so the first post-drain shot starts at mount 0.
                plan.NextMountIndexAfter = 0;
                plan.CooldownSeconds = cooldownSeconds;
                return true;
            }

            // --- Mode B: round-robin drip (not enough for a full same-tick volley) ---
            // [TITAN-ORBIT] Always exactly one mount per tick and +1 cursor. Firing multiple drip
            // mounts (advance by FireCount) made 4-gun ships oscillate pairs 0,1 ↔ 2,3.
            if (currentEnergy < energyCostPerMount)
                return false;

            int mountIdx = nextMountIndex;
            if (mountIdx < 0)
                mountIdx = 0;
            mountIdx %= mountCount;

            plan.CanFire = true;
            plan.IsFullVolley = false;
            plan.StartMountIndex = mountIdx;
            plan.FireCount = 1;
            plan.EnergySpend = energyCostPerMount;
            plan.DamagePerBullet = damagePerBullet;
            plan.NextMountIndexAfter = (mountIdx + 1) % mountCount;
            plan.CooldownSeconds = cooldownSeconds;
            return true;
        }

        /// <summary>
        /// Resolves the mount index for the <paramref name="shotOrdinal"/>-th bullet in a planned fire.
        /// Full volley uses 0..FireCount-1; drip is always <see cref="FirePlan.StartMountIndex"/>.
        /// </summary>
        /// <param name="plan">Plan from <see cref="TryPlanFire"/>.</param>
        /// <param name="shotOrdinal">0-based index within this fire tick (0 .. FireCount-1).</param>
        /// <param name="mountCount">Total mounts (for wrap safety).</param>
        /// <returns>Buffer index into <see cref="ShipWeaponMountElement"/>.</returns>
        public static int ResolveMountIndex(in FirePlan plan, int shotOrdinal, int mountCount)
        {
            if (mountCount <= 0)
                return 0;

            if (plan.IsFullVolley)
                return ((shotOrdinal % mountCount) + mountCount) % mountCount;

            return plan.StartMountIndex;
        }
    }
}
