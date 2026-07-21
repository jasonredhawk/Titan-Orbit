using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared multi-mount fire planner for server bullets and client anticipation VFX.
    /// <para>
    /// [TITAN-ORBIT] Each barrel is independent:
    /// <list type="bullet">
    /// <item>Own <c>FirePower</c> (damage + energy for that bullet)</item>
    /// <item>Own <c>FireRate</c> / <c>FireCooldown</c> (big guns can be slow while side guns are fast)</item>
    /// </list>
    /// While Fire is held, every mount whose cooldown is ready and whose energy cost fits the pool
    /// may shoot in the same tick. No hull-wide average damage and no shared single cooldown.
    /// </para>
    /// Paired with <see cref="BulletSimulationSystem"/> (server) and
    /// <c>ClientLocalBulletVfxBridge</c> (client cosmetics).
    /// </summary>
    public static class ShipWeaponFireLogic
    {
        /// <summary>
        /// One barrel that should spawn a bullet this tick (damage / energy / post-fire cooldown).
        /// </summary>
        public struct MountShot
        {
            /// <summary>Index into the ship <see cref="ShipWeaponMountElement"/> buffer.</summary>
            public int MountIndex;

            /// <summary>Damage written on the spawned bullet (this barrel’s firePower).</summary>
            public float Damage;

            /// <summary>Energy to subtract for this barrel’s shot.</summary>
            public float EnergyCost;

            /// <summary>Seconds to write into this mount’s <c>FireCooldown</c> after spawn.</summary>
            public float CooldownSeconds;
        }

        /// <summary>
        /// Maximum shots planned in one tick (hard cap — mount counts are tiny, usually ≤ 8).
        /// </summary>
        public const int MaxShotsPerTick = 16;

        /// <summary>
        /// Plans which mounts fire this tick from per-mount readiness and remaining energy.
        /// Call after ticking mount cooldowns down by dt. Does not mutate mounts — caller applies
        /// energy spend and writes each shot’s <see cref="MountShot.CooldownSeconds"/>.
        /// </summary>
        /// <param name="currentEnergy">Ship energy pool right now.</param>
        /// <param name="mounts">Weapon mount buffer (pose + combat + cooldown).</param>
        /// <param name="fallbackDamage">
        /// Used when a mount’s <c>FirePower</c> is unset (legacy / bake race).
        /// </param>
        /// <param name="fallbackFireRate">
        /// Used when a mount’s <c>FireRate</c> is unset.
        /// </param>
        /// <param name="shots">
        /// Caller-owned output (≥ <see cref="MaxShotsPerTick"/> or mount count). Filled from index 0.
        /// </param>
        /// <param name="shotCount">How many entries in <paramref name="shots"/> are valid.</param>
        /// <param name="totalEnergySpend">Sum of energy costs for the planned shots.</param>
        /// <returns>True when at least one mount should fire.</returns>
        public static bool TryPlanIndependentFire(
            float currentEnergy,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            float fallbackDamage,
            float fallbackFireRate,
            MountShot[] shots,
            out int shotCount,
            out float totalEnergySpend)
        {
            shotCount = 0;
            totalEnergySpend = 0f;

            if (mounts.Length <= 0 || shots == null || shots.Length <= 0)
                return false;

            float energyLeft = currentEnergy;
            int capacity = math.min(mounts.Length, math.min(shots.Length, MaxShotsPerTick));

            // --- Each ready barrel spends its own firePower and arms its own cooldown ---
            for (int i = 0; i < capacity; i++)
            {
                ShipWeaponMountElement mount = mounts[i];
                if (mount.FireCooldown > 0f)
                    continue;

                float damage = mount.FirePower > 0.01f
                    ? mount.FirePower
                    : math.max(0.1f, fallbackDamage);
                float fireRate = mount.FireRate > 0.01f
                    ? mount.FireRate
                    : math.max(0.1f, fallbackFireRate);
                float energyCost = math.max(0.01f, damage);

                // [TITAN-ORBIT] Skip this barrel if the pool cannot afford it — cheaper guns may
                // still fire later in the loop if they cost less.
                if (energyLeft < energyCost)
                    continue;

                shots[shotCount++] = new MountShot
                {
                    MountIndex = i,
                    Damage = math.max(1f, damage),
                    EnergyCost = energyCost,
                    CooldownSeconds = 1f / fireRate,
                };
                energyLeft -= energyCost;
                totalEnergySpend += energyCost;
            }

            return shotCount > 0;
        }

        /// <summary>
        /// Ticks every mount’s <see cref="ShipWeaponMountElement.FireCooldown"/> down by dt.
        /// Call once per frame before planning fire (server sim and client anticipation).
        /// </summary>
        public static void TickMountCooldowns(DynamicBuffer<ShipWeaponMountElement> mounts, float dt)
        {
            if (mounts.Length <= 0 || dt <= 0f)
                return;

            for (int i = 0; i < mounts.Length; i++)
            {
                ShipWeaponMountElement mount = mounts[i];
                if (mount.FireCooldown <= 0f)
                    continue;
                mount.FireCooldown = math.max(0f, mount.FireCooldown - dt);
                mounts[i] = mount;
            }
        }
    }
}
