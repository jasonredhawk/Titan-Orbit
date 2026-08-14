using TitanOrbit.Data;
using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared multi-mount fire planner for server bullets and client anticipation VFX.
    /// <para>
    /// [TITAN-ORBIT] Shared energy pool (summed cap + regen). Sequencing follows
    /// <see cref="ShipWeaponFireMode"/> from the ship family (via <see cref="ShipWeaponConfig.FireMode"/>):
    /// <list type="bullet">
    /// <item><b>Energy Hybrid</b> — energy ≥ sum of every mount’s firePower <b>and</b> every mount’s
    /// cooldown is ready → all barrels fire in the same tick; otherwise only
    /// <see cref="ShipWeaponState.NextMountIndex"/> may spend energy (round-robin drip).</item>
    /// <item><b>Always Fire Together</b> — same full-volley gate only; never drip a single barrel.</item>
    /// <item><b>Always Round-Robin</b> — never volley; always the NextMountIndex energy queue.</item>
    /// </list>
    /// Each mount still keeps its own <see cref="ShipWeaponMountElement.FirePower"/> /
    /// <see cref="ShipWeaponMountElement.FireRate"/> / cooldown.
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
        /// Plans which mounts fire this tick according to <paramref name="fireMode"/>.
        /// Call after ticking mount cooldowns down by dt. Does not mutate mounts — caller applies
        /// energy spend, writes each shot’s <see cref="MountShot.CooldownSeconds"/>, and stores
        /// <paramref name="nextMountIndexAfter"/>.
        /// </summary>
        /// <param name="currentEnergy">Ship energy pool right now.</param>
        /// <param name="mounts">Weapon mount buffer (pose + combat + cooldown).</param>
        /// <param name="nextMountIndex">
        /// Current energy-queue cursor (<see cref="ShipWeaponState.NextMountIndex"/> or client mirror).
        /// </param>
        /// <param name="fallbackDamage">
        /// Used when a mount’s <c>FirePower</c> is unset (legacy / bake race).
        /// </param>
        /// <param name="fallbackFireRate">
        /// Used when a mount’s <c>FireRate</c> is unset.
        /// </param>
        /// <param name="fireMode">
        /// Hull-wide policy from <see cref="ShipWeaponConfig.FireMode"/> /
        /// <see cref="ShipFamilyDefinition.weaponFireMode"/>.
        /// </param>
        /// <param name="shots">
        /// Caller-owned output (≥ <see cref="MaxShotsPerTick"/> or mount count). Filled from index 0.
        /// </param>
        /// <param name="shotCount">How many entries in <paramref name="shots"/> are valid.</param>
        /// <param name="totalEnergySpend">Sum of energy costs for the planned shots.</param>
        /// <param name="nextMountIndexAfter">Cursor to write after this fire (or unchanged if none).</param>
        /// <returns>True when at least one mount should fire.</returns>
        public static bool TryPlanFire(
            float currentEnergy,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            int nextMountIndex,
            float fallbackDamage,
            float fallbackFireRate,
            ShipWeaponFireMode fireMode,
            MountShot[] shots,
            out int shotCount,
            out float totalEnergySpend,
            out int nextMountIndexAfter,
            float abilityEnergyPerShot = 0f)
        {
            shotCount = 0;
            totalEnergySpend = 0f;
            nextMountIndexAfter = nextMountIndex;

            if (mounts.Length <= 0 || shots == null || shots.Length <= 0)
                return false;

            int mountCount = mounts.Length;
            float abilityAdd = math.max(0f, abilityEnergyPerShot);

            // --- Sum every barrel’s cost + check all cooldowns ready ---
            float totalCost = 0f;
            bool allReady = true;
            for (int i = 0; i < mountCount; i++)
            {
                ResolveMountCombat(mounts[i], fallbackDamage, fallbackFireRate,
                    out float damage, out _, out float energyCost, abilityAdd);
                totalCost += energyCost;
                if (mounts[i].FireCooldown > 0f)
                    allReady = false;
            }

            // --- Mode: Always Round-Robin — skip volley entirely ---
            // [TITAN-ORBIT] Designer forced drip-fire even when the pool could afford a full bank.
            bool allowVolley = fireMode != ShipWeaponFireMode.AlwaysRoundRobin;

            // --- Full volley (pool covers every weapon and all are off cooldown) ---
            // [TITAN-ORBIT] Same-tick multi-fire only when energy can feed the whole bank at once.
            // EnergyHybrid and AlwaysFireTogether both use this gate; AlwaysRoundRobin never does.
            if (allowVolley && allReady && currentEnergy >= totalCost && totalCost > 0f)
            {
                int capacity = math.min(mountCount, math.min(shots.Length, MaxShotsPerTick));
                for (int i = 0; i < capacity; i++)
                {
                    ResolveMountCombat(mounts[i], fallbackDamage, fallbackFireRate,
                        out float damage, out float fireRate, out float energyCost, abilityAdd);
                    shots[shotCount++] = new MountShot
                    {
                        MountIndex = i,
                        Damage = damage,
                        EnergyCost = energyCost,
                        CooldownSeconds = 1f / fireRate,
                    };
                    totalEnergySpend += energyCost;
                }

                // Next drip after the pool drains starts at mount 0.
                nextMountIndexAfter = 0;
                return shotCount > 0;
            }

            // --- Always Fire Together: wait for full bank — no single-barrel drip ---
            // [TITAN-ORBIT] EnergyHybrid falls through to round-robin when volley is unaffordable.
            if (fireMode == ShipWeaponFireMode.AlwaysFireTogether)
                return false;

            // --- Energy queue — only NextMountIndex may spend / fire ---
            // [TITAN-ORBIT] Regen fills the shared pool, but other barrels must wait their turn.
            // That is what makes low-energy fire cycle 0→1→2→… instead of mount 0 monopolizing.
            int mountIdx = nextMountIndex;
            if (mountIdx < 0)
                mountIdx = 0;
            mountIdx %= mountCount;

            ShipWeaponMountElement mount = mounts[mountIdx];
            if (mount.FireCooldown > 0f)
                return false;

            ResolveMountCombat(mount, fallbackDamage, fallbackFireRate,
                out float dripDamage, out float dripRate, out float dripCost, abilityAdd);
            if (currentEnergy < dripCost)
                return false;

            shots[0] = new MountShot
            {
                MountIndex = mountIdx,
                Damage = dripDamage,
                EnergyCost = dripCost,
                CooldownSeconds = 1f / dripRate,
            };
            shotCount = 1;
            totalEnergySpend = dripCost;
            nextMountIndexAfter = (mountIdx + 1) % mountCount;
            return true;
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
                ShipWeaponMountElement m = mounts[i];
                if (m.FireCooldown <= 0f)
                    continue;
                m.FireCooldown = math.max(0f, m.FireCooldown - dt);
                mounts[i] = m;
            }
        }

        /// <summary>
        /// Resolves per-barrel damage, fire rate, and energy cost (energy = firePower + ability drain).
        /// </summary>
        static void ResolveMountCombat(
            in ShipWeaponMountElement mount,
            float fallbackDamage,
            float fallbackFireRate,
            out float damage,
            out float fireRate,
            out float energyCost,
            float abilityEnergyPerShot = 0f)
        {
            damage = mount.FirePower > 0.01f
                ? mount.FirePower
                : math.max(0.1f, fallbackDamage);
            fireRate = mount.FireRate > 0.01f
                ? mount.FireRate
                : math.max(0.1f, fallbackFireRate);
            damage = math.max(1f, damage);
            fireRate = math.max(0.1f, fireRate);
            energyCost = math.max(0.01f, damage + math.max(0f, abilityEnergyPerShot));
        }
    }
}
