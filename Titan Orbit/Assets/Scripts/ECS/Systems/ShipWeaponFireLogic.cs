using System;
using TitanOrbit.Data;
using Unity.Entities;
using Unity.Mathematics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared multi-mount fire planner for server bullets.
    /// <para>
    /// [TITAN-ORBIT] The hull pool paints the arsenal strip left to right.
    /// Square 0 takes one shot cost, leftover energy fills square 1, and so on.
    /// Three guns at 25 with a pool of 30 show 25 + 5 + 0. Regen raises the
    /// same bar: at 50 two squares are full and may fire together. A square
    /// that is still filling does not give its leftover to a later cheaper gun.
    /// </para>
    /// After a paid shot the barrel waits <c>1 / fireRate</c> before it can
    /// fire again. That delay does not empty the square — energy still sits
    /// left to right — it only blocks that barrel. Lasers pay one interval of
    /// beam DPS in the same strip walk.
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
                abilityEnergyPerShot, chargeCooldown, in allOn, lastFiredMountIndex: -1);
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
            in ShipWeaponArmState arm,
            int lastFiredMountIndex = -1)
        {
            shotCount = 0;
            totalEnergySpend = 0f;
            nextMountIndexAfter = nextMountIndex;

            if (mounts.Length <= 0 || shots == null || shots.Length <= 0)
                return false;

            Span<int> order = stackalloc int[MaxShotsPerTick];
            int orderCount = BuildArmedStripOrder(mounts, in arm, order, skipCannonLasers: false);
            if (orderCount <= 0)
                return false;

            return TryPlanClipStack(
                currentEnergy, mounts, in arm, order, orderCount, nextMountIndex,
                lastFiredMountIndex,
                fallbackDamage, fallbackFireRate, abilityEnergyPerShot,
                fireMode, skipCannonLaserShots: false, chargeCooldown,
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
                chargeCooldown, in allOn, lastFiredMountIndex: -1);
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
            in ShipWeaponArmState arm,
            int lastFiredMountIndex = -1)
        {
            shotCount = 0;
            totalEnergySpend = 0f;
            nextMountIndexAfter = nextMountIndex;

            if (mounts.Length <= 0 || shots == null || shots.Length <= 0)
                return false;

            Span<int> order = stackalloc int[MaxShotsPerTick];
            int orderCount = BuildArmedStripOrder(mounts, in arm, order, skipCannonLasers: false);
            if (orderCount <= 0)
                return false;

            return TryPlanClipStack(
                currentEnergy, mounts, in arm, order, orderCount, nextMountIndex,
                lastFiredMountIndex,
                fallbackDamage: 0f, fallbackFireRate, abilityEnergyPerShot: 0f,
                ShipWeaponFireMode.EnergyHybrid, skipCannonLaserShots: true, chargeCooldown,
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
        /// Where the energy queue sits in <paramref name="order"/>. Exact mount match
        /// wins. A muted or stale cursor snaps to the next armed strip slot, then
        /// wraps to the first square. The arsenal HUD uses the same slot so the
        /// charging bar is the barrel that may shoot next.
        /// </summary>
        /// <param name="order">Armed mount indices in strip order (kind, then buffer index).</param>
        /// <param name="orderCount">How many entries in <paramref name="order"/> are live.</param>
        /// <param name="cursorMountIndex">
        /// <see cref="ShipWeaponState.NextMountIndex"/> or the client mirror.
        /// </param>
        /// <returns>Index into <paramref name="order"/>, or 0 when the strip is empty.</returns>
        public static int FindEnergyQueueSlot(Span<int> order, int orderCount, int cursorMountIndex)
        {
            if (orderCount <= 0 || order.Length <= 0)
                return 0;

            int count = math.min(orderCount, order.Length);
            int later = -1;
            for (int n = 0; n < count; n++)
            {
                int mount = order[n];
                if (mount == cursorMountIndex)
                    return n;
                // Strip order follows buffer index within each class, so the first
                // armed mount past a muted cursor is that cursor's successor.
                if (later < 0 && mount > cursorMountIndex)
                    later = n;
            }

            return later >= 0 ? later : 0;
        }

        /// <summary>
        /// Slot of the barrel that should charge now. Exact cursor match wins.
        /// MEGA cannon lasers burn on their own, so the queue skips them.
        /// Wraps to the first fireable square when the cursor is past the end
        /// or sitting on a muted barrel.
        /// </summary>
        public static int ResolveCycleSlot(
            Span<int> order,
            int orderCount,
            int cursorMountIndex,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            bool skipCannonLasers)
        {
            int slot = FindEnergyQueueSlot(order, orderCount, cursorMountIndex);
            if (!skipCannonLasers || orderCount <= 0)
                return slot;

            for (int n = 0; n < orderCount; n++)
            {
                int s = (slot + n) % orderCount;
                if (!ShipWeaponKind.IsCannonLaser(mounts[order[s]]))
                    return s;
            }

            return slot;
        }

        /// <summary>
        /// Next square after <paramref name="slot"/>, wrapping from the last
        /// weapon back to the first. MEGA cannon lasers are skipped so the
        /// queue cannot stall on a hitscan barrel.
        /// </summary>
        public static int NextCycleSlot(
            Span<int> order,
            int orderCount,
            int slot,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            bool skipCannonLasers)
        {
            if (orderCount <= 0)
                return 0;

            for (int n = 1; n <= orderCount; n++)
            {
                int s = (slot + n) % orderCount;
                if (skipCannonLasers && ShipWeaponKind.IsCannonLaser(mounts[order[s]]))
                    continue;
                return s;
            }

            return slot;
        }

        /// <summary>
        /// True when the pool can pay every armed clip in strip order. Energy
        /// Hybrid volleys in that case; otherwise it drips one square at a time.
        /// </summary>
        public static bool CanAffordEveryArmedClip(
            float energy,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            Span<int> order,
            int orderCount,
            float fallbackDamage,
            float fallbackFireRate,
            float abilityEnergyPerShot,
            bool skipCannonLaserShots)
        {
            float remaining = math.max(0f, energy);
            float abilityAdd = math.max(0f, abilityEnergyPerShot);
            for (int n = 0; n < orderCount; n++)
            {
                float cost = ResolveClipEnergyCost(
                    mounts[order[n]], fallbackDamage, fallbackFireRate,
                    abilityAdd, skipCannonLaserShots);
                if (remaining + 0.001f < cost)
                    return false;
                remaining -= cost;
            }

            return orderCount > 0;
        }

        /// <summary>
        /// Plans shots for the current square. Always Fire Together can volley.
        /// Every other mode energizes only the cursor square, fires that barrel,
        /// then steps to the next square. A charge clock still running, a barrel
        /// cadence, or a short tank all wait on that same square.
        /// </summary>
        static bool TryPlanClipStack(
            float currentEnergy,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            in ShipWeaponArmState arm,
            Span<int> order,
            int orderCount,
            int queueMountIndex,
            int lastFiredMountIndex,
            float fallbackDamage,
            float fallbackFireRate,
            float abilityEnergyPerShot,
            ShipWeaponFireMode fireMode,
            bool skipCannonLaserShots,
            float chargeCooldown,
            MountShot[] shots,
            out int shotCount,
            out float totalEnergySpend,
            out int nextMountIndexAfter)
        {
            _ = arm;
            _ = lastFiredMountIndex;
            shotCount = 0;
            totalEnergySpend = 0f;
            int cycleSlot = ResolveCycleSlot(
                order, orderCount, queueMountIndex, mounts, skipCannonLaserShots);
            nextMountIndexAfter = orderCount > 0 ? order[cycleSlot] : queueMountIndex;

            float abilityAdd = math.max(0f, abilityEnergyPerShot);
            bool together = fireMode == ShipWeaponFireMode.AlwaysFireTogether;
            if (!together)
            {
                return TryPlanCycleDrip(
                    currentEnergy, mounts, order, orderCount, cycleSlot,
                    fallbackDamage, fallbackFireRate, abilityAdd,
                    skipCannonLaserShots, chargeCooldown, shots,
                    out shotCount, out totalEnergySpend, out nextMountIndexAfter);
            }

            return TryPlanFullBank(
                currentEnergy, mounts, order, orderCount,
                fallbackDamage, fallbackFireRate, abilityAdd,
                together, skipCannonLaserShots, shots,
                out shotCount, out totalEnergySpend, out nextMountIndexAfter);
        }

        /// <summary>
        /// One square only — the cursor. Does not skip a cooling barrel to a
        /// later chip; that was handing regen back to whichever gun came off
        /// cooldown first. After a shot the cursor is the next square.
        /// </summary>
        static bool TryPlanCycleDrip(
            float currentEnergy,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            Span<int> order,
            int orderCount,
            int cycleSlot,
            float fallbackDamage,
            float fallbackFireRate,
            float abilityAdd,
            bool skipCannonLaserShots,
            float chargeCooldown,
            MountShot[] shots,
            out int shotCount,
            out float totalEnergySpend,
            out int nextMountIndexAfter)
        {
            shotCount = 0;
            totalEnergySpend = 0f;
            nextMountIndexAfter = orderCount > 0 ? order[cycleSlot] : 0;
            if (orderCount <= 0 || shots == null || shots.Length <= 0)
                return false;
            if (cycleSlot < 0 || cycleSlot >= orderCount)
                return false;

            // Still energizing this square. Later chips stay dark.
            if (chargeCooldown > 0.001f)
                return false;

            int i = order[cycleSlot];
            ShipWeaponMountElement mount = mounts[i];
            if (skipCannonLaserShots && ShipWeaponKind.IsCannonLaser(mount))
                return false;
            // This barrel's own shot cadence. Wait here instead of firing the next gun.
            if (mount.FireCooldown > 0.001f)
                return false;

            ResolveMountCombat(mount, fallbackDamage, fallbackFireRate,
                out float damage, out float fireRate, out float energyCost, abilityAdd);

            if (currentEnergy + 0.001f < energyCost)
                return false;

            shots[0] = skipCannonLaserShots
                ? BuildMegaShot(i, mount, fallbackFireRate)
                : new MountShot
                {
                    MountIndex = i,
                    Damage = damage,
                    EnergyCost = energyCost,
                    CooldownSeconds = 1f / fireRate,
                };
            shotCount = 1;
            totalEnergySpend = shots[0].EnergyCost;
            nextMountIndexAfter = order[NextCycleSlot(
                order, orderCount, cycleSlot, mounts, skipCannonLaserShots)];
            return true;
        }

        /// <summary>
        /// Whole bank: Energy Hybrid fires every ready clip; Together waits
        /// until every armed clip is full and ready. Cursor returns to the
        /// first square after a volley.
        /// </summary>
        static bool TryPlanFullBank(
            float currentEnergy,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            Span<int> order,
            int orderCount,
            float fallbackDamage,
            float fallbackFireRate,
            float abilityAdd,
            bool together,
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
            int capacity = math.min(shots.Length, MaxShotsPerTick);
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
                    break;

                remaining -= energyCost;
                fullClips++;
                if (mount.FireCooldown > 0.001f)
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

            if (together)
            {
                if (fullClips < considered || readyClips < considered || shotCount <= 0)
                {
                    shotCount = 0;
                    totalEnergySpend = 0f;
                    return false;
                }
            }

            return shotCount > 0;
        }

        /// <summary>Energy one clip must hold before it can fire.</summary>
        static float ResolveClipEnergyCost(
            in ShipWeaponMountElement mount,
            float fallbackDamage,
            float fallbackFireRate,
            float abilityAdd,
            bool skipCannonLaserShots)
        {
            if (skipCannonLaserShots && ShipWeaponKind.IsCannonLaser(mount))
                return math.max(0.01f, mount.FirePower);

            ResolveMountCombat(mount, fallbackDamage, fallbackFireRate,
                out _, out _, out float energyCost, abilityAdd);
            return energyCost;
        }

        /// <summary>
        /// Cost of one laser pulse: one fire interval of beam DPS
        /// (<c>DPS / fireRate</c>). <see cref="CannonLaserMath.ComputeDps"/> is
        /// fire power times fire rate, so the pulse cost is that barrel's fire power.
        /// </summary>
        public static float LaserPulseCost(in ShipWeaponMountElement mount)
        {
            return math.max(0.01f, mount.FirePower);
        }

        /// <summary>
        /// Energy one shot (or laser pulse) takes from the hull pool.
        /// Regular barrels are fire power plus ability drain. MEGA projectiles
        /// and lasers are that mount's fire power.
        /// </summary>
        public static float GetShotCost(
            in ShipWeaponMountElement mount,
            bool isMega,
            float fallbackDamage,
            float fallbackFireRate,
            float abilityEnergy)
        {
            if (isMega || ShipWeaponKind.IsCannonLaser(mount))
                return math.max(0.01f, mount.FirePower);
            return GetMountEnergyCost(mount, fallbackDamage, fallbackFireRate, abilityEnergy);
        }

        /// <summary>
        /// How full this square is when <paramref name="remainingEnergy"/> is
        /// the pool left after earlier squares. 30 energy onto a 25-cost square
        /// is 1; the leftover 5 belongs to the next square.
        /// </summary>
        public static float SequentialSquareFill(float remainingEnergy, float shotCost)
        {
            float cost = math.max(0.01f, shotCost);
            return math.saturate(math.max(0f, remainingEnergy) / cost);
        }

        /// <summary>
        /// True when the hull pool paints this barrel's square full, after
        /// earlier armed squares have taken their shot cost. Same walk the
        /// HUD and laser beams use so a later cannon cannot look live on
        /// energy that still belongs to a gun on its left.
        /// </summary>
        public static bool IsSequentialSlotFull(
            DynamicBuffer<ShipWeaponMountElement> mounts,
            in ShipWeaponArmState arm,
            bool isMega,
            float currentEnergy,
            float fallbackDamage,
            float fallbackFireRate,
            float abilityEnergy,
            int mountIndex)
        {
            if (!mounts.IsCreated || mountIndex < 0 || mountIndex >= mounts.Length)
                return false;

            Span<int> order = stackalloc int[MaxShotsPerTick];
            int orderCount = BuildArmedStripOrder(mounts, in arm, order, skipCannonLasers: false);
            float walk = math.max(0f, currentEnergy);
            for (int n = 0; n < orderCount; n++)
            {
                int i = order[n];
                float cost = GetShotCost(
                    mounts[i], isMega, fallbackDamage, fallbackFireRate, abilityEnergy);
                bool full = TryTakeSequentialSlot(ref walk, cost);
                if (i == mountIndex)
                    return full;
                if (!full)
                    return false;
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="remainingEnergy"/> covers this shot.
        /// Subtracts one shot cost on success, or the leftover crumbs on
        /// failure so later squares stay empty.
        /// </summary>
        public static bool TryTakeSequentialSlot(ref float remainingEnergy, float shotCost)
        {
            float cost = math.max(0.01f, shotCost);
            float have = math.max(0f, remainingEnergy);
            if (have + 0.001f < cost)
            {
                remainingEnergy = 0f;
                return false;
            }

            remainingEnergy = have - cost;
            return true;
        }

        /// <summary>
        /// Seconds for this barrel's square to go from empty to ready.
        /// </summary>
        public static float ReadyInterval(in ShipWeaponMountElement mount, float fallbackFireRate)
        {
            float rate = mount.FireRate > 0.01f ? mount.FireRate : fallbackFireRate;
            rate = math.max(0.1f, rate);
            return 1f / rate;
        }

        /// <summary>
        /// Copies each mount's ready delay into the ghosted
        /// <see cref="ShipWeaponReadyElement"/> buffer so the owner's HUD
        /// matches the server. Resizes when the chassis mount count changes.
        /// </summary>
        public static void PublishReadyTimers(
            DynamicBuffer<ShipWeaponMountElement> mounts,
            DynamicBuffer<ShipWeaponReadyElement> ready)
        {
            if (!mounts.IsCreated || !ready.IsCreated)
                return;

            int count = mounts.Length;
            if (ready.Length != count)
                ready.ResizeUninitialized(count);

            for (int i = 0; i < count; i++)
            {
                float cooldown = mounts[i].FireCooldown;
                if (!math.isfinite(cooldown) || cooldown < 0f)
                    cooldown = 0f;
                ready[i] = new ShipWeaponReadyElement { FireCooldown = cooldown };
            }
        }

        /// <summary>
        /// Walks the arsenal strip left to right. Each square that the pool
        /// can fill is reserved; a reserved square fires only when its ready
        /// delay has finished. A partial square keeps the leftover crumbs and
        /// later squares stay empty. Cannon lasers are left for the MEGA walk
        /// so they sit between guns and missiles in the same bar.
        /// </summary>
        public static bool TryPlanReadyShots(
            ref float currentEnergy,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            in ShipWeaponArmState arm,
            bool isMega,
            float fallbackDamage,
            float fallbackFireRate,
            float abilityEnergy,
            MountShot[] shots,
            out int shotCount)
        {
            shotCount = 0;
            if (!mounts.IsCreated || mounts.Length <= 0 || shots == null || shots.Length <= 0)
                return false;

            Span<int> order = stackalloc int[MaxShotsPerTick];
            int orderCount = BuildArmedStripOrder(mounts, in arm, order, skipCannonLasers: false);
            float abilityAdd = math.max(0f, abilityEnergy);
            float walk = math.max(0f, currentEnergy);
            float spend = walk;
            int capacity = math.min(shots.Length, MaxShotsPerTick);
            for (int n = 0; n < orderCount && shotCount < capacity; n++)
            {
                int i = order[n];
                ShipWeaponMountElement mount = mounts[i];
                if (ShipWeaponKind.IsCannonLaser(mount))
                {
                    if (!TryTakeSequentialSlot(ref walk, LaserPulseCost(mount)))
                        break;
                    continue;
                }

                float damage;
                float fireRate;
                float cost;
                if (isMega)
                {
                    fireRate = math.max(0.15f, mount.FireRate > 0.01f ? mount.FireRate : fallbackFireRate);
                    cost = math.max(0.01f, mount.FirePower);
                    damage = mount.FirePower;
                }
                else
                {
                    ResolveMountCombat(mount, fallbackDamage, fallbackFireRate,
                        out damage, out fireRate, out cost, abilityAdd);
                }

                if (!TryTakeSequentialSlot(ref walk, cost))
                    break;
                if (mount.FireCooldown > 0.001f)
                    continue;

                float interval = 1f / math.max(0.1f, fireRate);
                mount.FireCooldown = interval;
                mounts[i] = mount;
                spend -= cost;
                shots[shotCount++] = new MountShot
                {
                    MountIndex = i,
                    Damage = damage,
                    EnergyCost = cost,
                    CooldownSeconds = interval,
                };
            }

            currentEnergy = math.max(0f, spend);
            return shotCount > 0;
        }

        /// <summary>Spends a full bar. The pool already paid for this charge.</summary>
        public static void ConsumeWeaponCharge(DynamicBuffer<ShipWeaponMountElement> mounts, int mountIndex)
        {
            if (!mounts.IsCreated || mountIndex < 0 || mountIndex >= mounts.Length)
                return;
            ShipWeaponMountElement mount = mounts[mountIndex];
            if (mount.EnergyCharge == 0f)
                return;
            mount.EnergyCharge = 0f;
            mounts[mountIndex] = mount;
        }

        /// <summary>How much this bar can still accept this tick, capped at one fire interval of energy.</summary>
        static void ResolveChargeStep(
            in ShipWeaponMountElement mount,
            bool isMega,
            float fallbackDamage,
            float fallbackFireRate,
            float abilityAdd,
            float dt,
            out float step)
        {
            float cost = ResolveShotCost(mount, isMega, fallbackDamage, fallbackFireRate, abilityAdd);
            float charge = mount.EnergyCharge;
            if (!math.isfinite(charge) || charge < 0f)
                charge = 0f;
            float room = cost - charge;
            if (room <= 0.001f)
            {
                step = 0f;
                return;
            }

            ResolveMountCombat(mount, fallbackDamage, fallbackFireRate,
                out _, out float fireRate, out _, abilityAdd);
            if (isMega)
                fireRate = math.max(0.15f, mount.FireRate > 0.01f ? mount.FireRate : fallbackFireRate);
            // One full bar per fire interval: energy per second = shotCost × shots per second.
            float perSecond = cost * math.max(0.1f, fireRate);
            step = math.min(room, perSecond * math.max(0f, dt));
        }

        /// <summary>Energy one full bar holds for this barrel.</summary>
        static float ResolveShotCost(
            in ShipWeaponMountElement mount,
            bool isMega,
            float fallbackDamage,
            float fallbackFireRate,
            float abilityAdd)
        {
            if (isMega)
                return math.max(0.01f, mount.FirePower);
            ResolveMountCombat(mount, fallbackDamage, fallbackFireRate,
                out _, out _, out float cost, abilityAdd);
            return cost;
        }

        /// <summary>Adds pool energy into one bar and clamps it to a full shot.</summary>
        static void AddCharge(DynamicBuffer<ShipWeaponMountElement> mounts, int mountIndex, float add)
        {
            if (add <= 0f || mountIndex < 0 || mountIndex >= mounts.Length)
                return;
            ShipWeaponMountElement mount = mounts[mountIndex];
            float charge = mount.EnergyCharge;
            if (!math.isfinite(charge) || charge < 0f)
                charge = 0f;
            mount.EnergyCharge = charge + add;
            mounts[mountIndex] = mount;
        }

        /// <summary>
        /// Seconds this square takes to fill at hull regen. Zero when regen is unset
        /// (the tank gate alone decides the shot).
        /// </summary>
        public static float ComputeEnergyChargeSeconds(float nextShotCost, float energyRegenPerSecond)
        {
            if (nextShotCost <= 0.01f || energyRegenPerSecond < 0.01f)
                return 0f;
            return nextShotCost / energyRegenPerSecond;
        }

        /// <summary>
        /// Counts the current square's energize clock down. Call only while Fire is held
        /// so releasing the button pauses the bar instead of emptying it.
        /// </summary>
        public static void TickSquareCharge(ref float chargeRemaining, float dt)
        {
            if (chargeRemaining <= 0f || dt <= 0f)
                return;
            chargeRemaining = math.max(0f, chargeRemaining - dt);
        }

        /// <summary>
        /// Starts this square's energize clock the first time it is the cursor.
        /// A running clock (<paramref name="chargeRemaining"/> &gt; 0) and a finished
        /// clock (<paramref name="chargeDuration"/> &gt; 0) are left alone so a full
        /// bar can wait for energy without restarting.
        /// </summary>
        /// <returns>True when a new clock was armed this call.</returns>
        public static bool TryBeginSquareCharge(
            ref float chargeRemaining,
            ref float chargeDuration,
            float shotCost,
            float energyRegenPerSecond)
        {
            if (chargeDuration > 0.0001f || chargeRemaining > 0.0001f)
                return false;

            ArmNextSquareCharge(ref chargeRemaining, ref chargeDuration, shotCost, energyRegenPerSecond);
            return true;
        }

        /// <summary>
        /// Restarts the energize clock for the square that just became current.
        /// The arsenal bar reads this as empty and fills until the barrel fires.
        /// </summary>
        public static void ArmNextSquareCharge(
            ref float chargeRemaining,
            ref float chargeDuration,
            float shotCost,
            float energyRegenPerSecond)
        {
            float dur = ComputeEnergyChargeSeconds(shotCost, energyRegenPerSecond);
            if (dur <= 0.001f)
            {
                // No regen clock — the tank check is the only gate, so the bar is already full.
                chargeDuration = 0.001f;
                chargeRemaining = 0f;
                return;
            }

            chargeDuration = dur;
            chargeRemaining = dur;
        }

        /// <summary>
        /// Energy the cursor square must hold before its barrel may fire.
        /// Uses the same strip slot as <see cref="TryPlanFire"/> so the bar and the shot match.
        /// </summary>
        public static float GetArmedCursorShotCost(
            DynamicBuffer<ShipWeaponMountElement> mounts,
            in ShipWeaponArmState arm,
            int cursorMountIndex,
            bool isMega,
            float fallbackDamage,
            float fallbackFireRate,
            float abilityEnergyPerShot)
        {
            Span<int> order = stackalloc int[MaxShotsPerTick];
            int orderCount = BuildArmedStripOrder(mounts, in arm, order, skipCannonLasers: false);
            if (orderCount <= 0)
                return 0f;

            int slot = ResolveCycleSlot(order, orderCount, cursorMountIndex, mounts, skipCannonLasers: isMega);
            int mountIndex = order[slot];
            if (mountIndex < 0 || mountIndex >= mounts.Length)
                return 0f;

            if (isMega || ShipWeaponKind.IsCannonLaser(mounts[mountIndex]))
                return math.max(0.01f, mounts[mountIndex].FirePower);

            return GetMountEnergyCost(
                mounts[mountIndex], fallbackDamage, fallbackFireRate, abilityEnergyPerShot);
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
                if (m.FireCooldown == 0f && m.EnergyCharge == 0f)
                    continue;
                m.FireCooldown = 0f;
                m.EnergyCharge = 0f;
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
                if (ShipWeaponKind.KeepsAuthoredBulletBank(m.WeaponKind))
                    continue;
                if (m.FireCooldown == 0f && m.EnergyCharge == 0f)
                    continue;
                m.FireCooldown = 0f;
                m.EnergyCharge = 0f;
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
