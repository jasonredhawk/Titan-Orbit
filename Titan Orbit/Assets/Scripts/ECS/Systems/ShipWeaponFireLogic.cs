using System;
using TitanOrbit.Data;
using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared multi-mount fire planner for server bullets and client anticipation VFX.
    /// <para>
    /// [TITAN-ORBIT] Shared energy pool stacked onto clips in arsenal-strip order
    /// (gun, laser, missile, sniper — same walk as <c>ShipWeaponArmHUD</c>). A barrel
    /// whose clip is full and off cooldown fires; leftover energy spills into the
    /// next clip. Two full clips at 20 energy both shoot. A half-full clip does not.
    /// <see cref="ShipWeaponFireMode"/> still special-cases:
    /// <list type="bullet">
    /// <item><b>Energy Hybrid</b> — fire every full, ready clip this tick.</item>
    /// <item><b>Always Fire Together</b> — fire only when every armed clip is full and ready.</item>
    /// <item><b>Always Round-Robin</b> — fire only the first full, ready clip.</item>
    /// </list>
    /// Each mount still keeps its own <see cref="ShipWeaponMountElement.FirePower"/> /
    /// <see cref="ShipWeaponMountElement.FireRate"/> / cooldown.
    /// </para>
    /// MEGA hulls use the same clip stack; cannon lasers reserve energy in the stack
    /// but burn via <see cref="CannonLaserCombatSystem"/> (no bullet spawn).
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
        /// Maximum shots planned in one tick. Regular hulls are ≤ 8; MEGAs can exceed 40.
        /// </summary>
        public const int MaxShotsPerTick = 96;

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
            float abilityEnergyPerShot = 0f,
            float chargeCooldown = 0f)
        {
            var allOn = ShipWeaponArmState.AllOn;
            return TryPlanFire(
                currentEnergy, mounts, nextMountIndex, fallbackDamage, fallbackFireRate,
                fireMode, shots, out shotCount, out totalEnergySpend, out nextMountIndexAfter,
                abilityEnergyPerShot, chargeCooldown, in allOn);
        }

        /// <summary>
        /// Same as <see cref="TryPlanFire(float,DynamicBuffer{ShipWeaponMountElement},int,float,float,ShipWeaponFireMode,MountShot[],out int,out float,out int,float,float)"/>
        /// but skips barrels the player muted on the arsenal HUD.
        /// </summary>
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
            float abilityEnergyPerShot,
            float chargeCooldown,
            in ShipWeaponArmState arm)
        {
            shotCount = 0;
            totalEnergySpend = 0f;
            nextMountIndexAfter = nextMountIndex;

            if (mounts.Length <= 0 || shots == null || shots.Length <= 0)
                return false;

            // chargeCooldown is leftover from the old drip gate. The clip stack already
            // refuses a barrel that does not have a full clip — do not block a ready one.
            _ = chargeCooldown;

            Span<int> order = stackalloc int[MaxShotsPerTick];
            int orderCount = BuildArmedStripOrder(mounts, in arm, order, skipCannonLasers: false);
            if (orderCount <= 0)
                return false;

            return TryPlanClipStack(
                currentEnergy, mounts, in arm, order, orderCount,
                fallbackDamage, fallbackFireRate, abilityEnergyPerShot,
                fireMode, skipCannonLaserShots: false,
                shots, out shotCount, out totalEnergySpend, out nextMountIndexAfter);
        }

        /// <summary>
        /// MEGA clip stack: same pour as the arsenal HUD. Full projectile clips
        /// fire; cannon lasers reserve energy in the stack and burn via
        /// <see cref="CannonLaserCombatSystem"/>.
        /// </summary>
        public static bool TryPlanMegaFire(
            float currentEnergy,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            int nextMountIndex,
            float fallbackFireRate,
            MountShot[] shots,
            out int shotCount,
            out float totalEnergySpend,
            out int nextMountIndexAfter,
            float chargeCooldown = 0f)
        {
            var allOn = ShipWeaponArmState.AllOn;
            return TryPlanMegaFire(
                currentEnergy, mounts, nextMountIndex, fallbackFireRate, shots,
                out shotCount, out totalEnergySpend, out nextMountIndexAfter,
                chargeCooldown, in allOn);
        }

        /// <summary>
        /// Same as <see cref="TryPlanMegaFire(float,DynamicBuffer{ShipWeaponMountElement},int,float,MountShot[],out int,out float,out int,float)"/>
        /// but skips barrels the player muted (and still skips cannon lasers).
        /// </summary>
        public static bool TryPlanMegaFire(
            float currentEnergy,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            int nextMountIndex,
            float fallbackFireRate,
            MountShot[] shots,
            out int shotCount,
            out float totalEnergySpend,
            out int nextMountIndexAfter,
            float chargeCooldown,
            in ShipWeaponArmState arm)
        {
            shotCount = 0;
            totalEnergySpend = 0f;
            nextMountIndexAfter = nextMountIndex;

            if (mounts.Length <= 0 || shots == null || shots.Length <= 0)
                return false;

            _ = chargeCooldown;

            Span<int> order = stackalloc int[MaxShotsPerTick];
            int orderCount = BuildArmedStripOrder(mounts, in arm, order, skipCannonLasers: false);
            if (orderCount <= 0)
                return false;

            return TryPlanClipStack(
                currentEnergy, mounts, in arm, order, orderCount,
                fallbackDamage: 0f, fallbackFireRate, abilityEnergyPerShot: 0f,
                ShipWeaponFireMode.EnergyHybrid, skipCannonLaserShots: true,
                shots, out shotCount, out totalEnergySpend, out nextMountIndexAfter);
        }

        /// <summary>
        /// Armed barrels in the same order the arsenal HUD paints (kind, then buffer
        /// index). HUD and fire planning must share this walk so a bright clip is a
        /// clip that may actually shoot.
        /// </summary>
        /// <param name="skipCannonLasers">True to omit hitscan cannons from the list.</param>
        /// <returns>How many slots in <paramref name="order"/> were written.</returns>
        public static int BuildArmedStripOrder(
            DynamicBuffer<ShipWeaponMountElement> mounts,
            in ShipWeaponArmState arm,
            Span<int> order,
            bool skipCannonLasers)
        {
            int written = 0;
            if (!mounts.IsCreated || order.Length <= 0)
                return 0;

            int mountCount = mounts.Length;
            for (byte kind = 0; kind <= ShipWeaponKind.Sniper; kind++)
            {
                for (int i = 0; i < mountCount && written < order.Length; i++)
                {
                    if (mounts[i].WeaponKind != kind)
                        continue;
                    if (!ShipWeaponArmState.IsArmed(in arm, i))
                        continue;
                    if (skipCannonLasers && ShipWeaponKind.IsCannonLaser(mounts[i]))
                        continue;
                    order[written++] = i;
                }
            }

            return written;
        }

        /// <summary>
        /// Pours <paramref name="currentEnergy"/> onto strip-order clips. A full clip
        /// that is off cooldown becomes a shot (unless the fire mode says otherwise).
        /// An earlier full-but-cooling clip still reserves its cost so a later gun
        /// cannot steal it — same stack the HUD shows.
        /// </summary>
        static bool TryPlanClipStack(
            float currentEnergy,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            in ShipWeaponArmState arm,
            Span<int> order,
            int orderCount,
            float fallbackDamage,
            float fallbackFireRate,
            float abilityEnergyPerShot,
            ShipWeaponFireMode fireMode,
            bool skipCannonLaserShots,
            MountShot[] shots,
            out int shotCount,
            out float totalEnergySpend,
            out int nextMountIndexAfter)
        {
            shotCount = 0;
            totalEnergySpend = 0f;
            nextMountIndexAfter = orderCount > 0 ? order[0] : 0;

            float remaining = math.max(0f, currentEnergy);
            float abilityAdd = math.max(0f, abilityEnergyPerShot);
            int capacity = math.min(shots.Length, MaxShotsPerTick);
            int firstPartial = -1;
            int fullClips = 0;
            int readyClips = 0;
            int considered = 0;

            for (int n = 0; n < orderCount; n++)
            {
                int i = order[n];
                ShipWeaponMountElement mount = mounts[i];
                bool laser = ShipWeaponKind.IsCannonLaser(mount);
                ResolveMountCombat(mount, fallbackDamage, fallbackFireRate,
                    out float damage, out float fireRate, out float energyCost, abilityAdd);
                if (skipCannonLaserShots && laser)
                    energyCost = math.max(0.01f, mount.FirePower);

                considered++;
                if (remaining + 0.001f < energyCost)
                {
                    if (firstPartial < 0)
                        firstPartial = i;
                    break;
                }

                remaining -= energyCost;
                fullClips++;
                bool cooling = mount.FireCooldown > 0.001f;
                if (cooling)
                    continue;

                readyClips++;
                if (skipCannonLaserShots && laser)
                    continue;
                if (shotCount >= capacity)
                    continue;

                shots[shotCount++] = skipCannonLaserShots
                    ? BuildMegaShot(i, mount, fallbackFireRate)
                    : new MountShot
                    {
                        MountIndex = i,
                        Damage = damage,
                        EnergyCost = energyCost,
                        CooldownSeconds = 1f / fireRate,
                    };
                totalEnergySpend += skipCannonLaserShots ? mount.FirePower : energyCost;
            }

            nextMountIndexAfter = firstPartial >= 0
                ? firstPartial
                : (orderCount > 0 ? order[0] : 0);

            // --- Fire-mode gates ---
            // Together: every armed clip must be full and ready, or nobody shoots.
            // Round-robin: keep only the first planned shot.
            if (fireMode == ShipWeaponFireMode.AlwaysFireTogether)
            {
                if (fullClips < considered || readyClips < considered || shotCount <= 0)
                {
                    shotCount = 0;
                    totalEnergySpend = 0f;
                    return false;
                }
            }
            else if (fireMode == ShipWeaponFireMode.AlwaysRoundRobin && shotCount > 1)
            {
                totalEnergySpend = shots[0].EnergyCost;
                shotCount = 1;
            }

            return shotCount > 0;
        }

        /// <summary>
        /// Seconds the next drip barrel must charge at hull regen. Zero when regen
        /// is unset. Callers skip this wait when a full-bank volley is affordable.
        /// </summary>
        public static float ComputeEnergyChargeSeconds(float nextShotCost, float energyRegenPerSecond)
        {
            if (nextShotCost <= 0.01f || energyRegenPerSecond < 0.01f)
                return 0f;
            return nextShotCost / energyRegenPerSecond;
        }

        /// <summary>Energy cost of one regular-hull barrel (firePower + ability drain).</summary>
        public static float GetMountEnergyCost(
            in ShipWeaponMountElement mount,
            float fallbackDamage,
            float fallbackFireRate,
            float abilityEnergyPerShot = 0f)
        {
            ResolveMountCombat(mount, fallbackDamage, fallbackFireRate,
                out _, out _, out float energyCost, abilityEnergyPerShot);
            return energyCost;
        }

        /// <summary>FirePower of the next armed MEGA barrel at or after <paramref name="startIndex"/>.</summary>
        public static float GetNextArmedMegaShotCost(
            DynamicBuffer<ShipWeaponMountElement> mounts,
            int startIndex)
        {
            var allOn = ShipWeaponArmState.AllOn;
            return GetNextArmedMegaShotCost(mounts, in allOn, startIndex);
        }

        /// <summary>FirePower of the next HUD-armed MEGA barrel at or after <paramref name="startIndex"/>.</summary>
        public static float GetNextArmedMegaShotCost(
            DynamicBuffer<ShipWeaponMountElement> mounts,
            in ShipWeaponArmState arm,
            int startIndex)
        {
            if (!TryGetNextArmedMegaMount(mounts, in arm, startIndex, out int mountIdx))
                return 0f;
            return mounts[mountIdx].FirePower;
        }

        /// <summary>Next armed MEGA mount index, wrapping. False when the hull is unarmed.</summary>
        public static bool TryGetNextArmedMegaMount(
            DynamicBuffer<ShipWeaponMountElement> mounts,
            int startIndex,
            out int mountIndex)
        {
            var allOn = ShipWeaponArmState.AllOn;
            return TryGetNextArmedMegaMount(mounts, in allOn, startIndex, out mountIndex);
        }

        /// <summary>
        /// Next MEGA projectile barrel that is catalog-armed and HUD-armed, wrapping.
        /// Cannon lasers stay out of this queue — they burn in <see cref="CannonLaserCombatSystem"/>.
        /// </summary>
        public static bool TryGetNextArmedMegaMount(
            DynamicBuffer<ShipWeaponMountElement> mounts,
            in ShipWeaponArmState arm,
            int startIndex,
            out int mountIndex)
        {
            mountIndex = 0;
            int mountCount = mounts.Length;
            if (mountCount <= 0)
                return false;

            int start = startIndex;
            if (start < 0)
                start = 0;
            start %= mountCount;

            for (int n = 0; n < mountCount; n++)
            {
                int i = (start + n) % mountCount;
                if (!IsMegaProjectileArmed(mounts[i], in arm, i))
                    continue;
                mountIndex = i;
                return true;
            }

            return false;
        }

        /// <summary>Wrap-around index of the next armed MEGA barrel, or 0 when none.</summary>
        public static int NextArmedMegaMountIndex(
            DynamicBuffer<ShipWeaponMountElement> mounts,
            int startIndex)
        {
            var allOn = ShipWeaponArmState.AllOn;
            return NextArmedMegaMountIndex(mounts, in allOn, startIndex);
        }

        /// <summary>Wrap-around index of the next HUD-armed MEGA barrel, or 0 when none.</summary>
        public static int NextArmedMegaMountIndex(
            DynamicBuffer<ShipWeaponMountElement> mounts,
            in ShipWeaponArmState arm,
            int startIndex)
        {
            if (!TryGetNextArmedMegaMount(mounts, in arm, startIndex, out int mountIndex))
                return 0;
            return mountIndex;
        }

        /// <summary>
        /// Next regular-hull barrel the energy queue may visit (HUD-armed, wrapping).
        /// False when every mount is muted.
        /// </summary>
        public static bool TryGetNextArmedRegularMount(
            DynamicBuffer<ShipWeaponMountElement> mounts,
            in ShipWeaponArmState arm,
            int startIndex,
            out int mountIndex)
        {
            mountIndex = 0;
            int mountCount = mounts.Length;
            if (mountCount <= 0)
                return false;

            int start = startIndex;
            if (start < 0)
                start = 0;
            start %= mountCount;

            for (int n = 0; n < mountCount; n++)
            {
                int i = (start + n) % mountCount;
                if (!ShipWeaponArmState.IsArmed(in arm, i))
                    continue;
                mountIndex = i;
                return true;
            }

            return false;
        }

        /// <summary>Wrap-around index of the next HUD-armed regular barrel, or 0 when none.</summary>
        public static int NextArmedRegularMountIndex(
            DynamicBuffer<ShipWeaponMountElement> mounts,
            in ShipWeaponArmState arm,
            int startIndex)
        {
            if (!TryGetNextArmedRegularMount(mounts, in arm, startIndex, out int mountIndex))
                return 0;
            return mountIndex;
        }

        /// <summary>
        /// MEGA projectile that may join the energy queue: catalog firePower, not a
        /// cannon laser, and not muted on the arsenal HUD.
        /// </summary>
        static bool IsMegaProjectileArmed(
            in ShipWeaponMountElement mount,
            in ShipWeaponArmState arm,
            int mountIndex)
        {
            if (!ShipWeaponArmState.IsArmed(in arm, mountIndex))
                return false;
            if (mount.FirePower <= 0.01f || ShipWeaponKind.IsCannonLaser(mount))
                return false;
            return true;
        }

        /// <summary>One MEGA shot — energy cost is that barrel’s firePower.</summary>
        static MountShot BuildMegaShot(
            int mountIndex,
            in ShipWeaponMountElement mount,
            float fallbackFireRate)
        {
            float fireRate = math.max(0.15f, mount.FireRate > 0.01f ? mount.FireRate : fallbackFireRate);
            return new MountShot
            {
                MountIndex = mountIndex,
                Damage = mount.FirePower,
                EnergyCost = mount.FirePower,
                CooldownSeconds = 1f / fireRate,
            };
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
                // NaN / +Inf cooldown never ticks down — MEGA Shift-aim once wrote a bad
                // value and the barrel stayed mute until respawn.
                if (!math.isfinite(m.FireCooldown) || m.FireCooldown > 60f)
                {
                    m.FireCooldown = 0f;
                    mounts[i] = m;
                    continue;
                }

                if (m.FireCooldown <= 0f)
                    continue;
                m.FireCooldown = math.max(0f, m.FireCooldown - dt);
                mounts[i] = m;
            }
        }

        /// <summary>
        /// Clears every barrel’s <see cref="ShipWeaponMountElement.FireCooldown"/>.
        /// B-key / HUD bank changes must do this — cooldown lives on the mount, not the
        /// bank, so a slow Lightning / cannon shot would otherwise mute every owned gun
        /// until that leftover timer expired.
        /// </summary>
        public static void ResetMountCooldowns(DynamicBuffer<ShipWeaponMountElement> mounts)
        {
            if (mounts.Length <= 0)
                return;

            for (int i = 0; i < mounts.Length; i++)
            {
                ShipWeaponMountElement m = mounts[i];
                if (m.FireCooldown == 0f)
                    continue;
                m.FireCooldown = 0f;
                mounts[i] = m;
            }
        }

        /// <summary>
        /// MEGA B-key / HUD: clear leftover timers on Titan Bullet mounts only.
        /// Cannons, missiles, and snipers keep cadence — their bank does not change.
        /// </summary>
        public static void ResetCycledBulletMountCooldowns(DynamicBuffer<ShipWeaponMountElement> mounts)
        {
            if (mounts.Length <= 0)
                return;

            for (int i = 0; i < mounts.Length; i++)
            {
                ShipWeaponMountElement m = mounts[i];
                if (m.FireCooldown == 0f || ShipWeaponKind.KeepsAuthoredBulletBank(m.WeaponKind))
                    continue;
                m.FireCooldown = 0f;
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
