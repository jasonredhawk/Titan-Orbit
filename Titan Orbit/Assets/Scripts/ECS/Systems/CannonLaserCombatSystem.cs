using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: MEGA cannon barrels burn a continuous hitscan at
    /// firePower × fireRate DPS, ramped 50%→300% on the same lock.
    /// Energy drains every tick while the beam is on. After the pool
    /// drops to 10% of max or below, lasers stay off until it rises
    /// strictly above 10%. <see cref="BulletSimulationSystem"/> calls
    /// <see cref="TryContinuousBurn"/> in strip order after the guns.
    /// World: ServerSimulation. Map size from <see cref="MapStateSingleton"/>.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class CannonLaserCombatSystem : SystemBase
    {
        EntityQuery _megaQuery;

        /// <summary>Leftover cargo spill too small to spawn this tick (per victim).</summary>
        readonly Dictionary<Entity, float> _gemCarry = new Dictionary<Entity, float>(16);

        /// <summary>Last lock entity per cannon so a new target restarts the DPS ramp.</summary>
        readonly Dictionary<LaserRampKey, Entity> _lastLaserLock = new Dictionary<LaserRampKey, Entity>(16);

        /// <summary>MEGAs whose lasers were walked by the bullet strip this tick.</summary>
        readonly HashSet<Entity> _handledShips = new HashSet<Entity>(8);

        bool _passLockout;
        bool _passWantedBurn;

        /// <summary>Laser mounts whose sequential energy slot was full this tick.</summary>
        readonly HashSet<int> _reservedMounts = new HashSet<int>(8);

        struct LaserRampKey : System.IEquatable<LaserRampKey>
        {
            public Entity Ship;
            public int Mount;

            public bool Equals(LaserRampKey other) => Ship == other.Ship && Mount == other.Mount;
            public override bool Equals(object obj) => obj is LaserRampKey other && Equals(other);
            public override int GetHashCode() => unchecked(Ship.GetHashCode() * 397 ^ Mount);
        }

        /// <summary>Cache the MEGA query.</summary>
        protected override void OnCreate()
        {
            RequireForUpdate<MapStateSingleton>();
            RequireForUpdate<ShipTag>();
            _megaQuery = GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<MegaShipState>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadWrite<ShipWeaponMountElement>());
        }

        /// <summary>
        /// Hides beams on MEGAs the bullet strip did not walk (not firing, dead,
        /// or missing from that pass). Burns themselves run from
        /// <see cref="TryContinuousBurn"/> so they share strip order with the guns.
        /// </summary>
        protected override void OnUpdate()
        {
            using var megas = _megaQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < megas.Length; i++)
            {
                Entity mega = megas[i];
                if (!EntityManager.HasComponent<MegaShipState>(mega)
                    || !EntityManager.GetComponentData<MegaShipState>(mega).IsMega)
                    continue;
                if (_handledShips.Contains(mega))
                    continue;
                if (!HasCannonMount(mega))
                    continue;
                Quench(mega);
            }

            _handledShips.Clear();
        }

        /// <summary>True when this hull has at least one cannon-laser barrel.</summary>
        bool HasCannonMount(Entity mega)
        {
            if (!EntityManager.HasBuffer<ShipWeaponMountElement>(mega))
                return false;
            var mounts = EntityManager.GetBuffer<ShipWeaponMountElement>(mega);
            var gunners = EntityManager.HasBuffer<MegaShipGunnerSlotElement>(mega)
                ? EntityManager.GetBuffer<MegaShipGunnerSlotElement>(mega)
                : default;
            ShipWeaponKind.RestoreMountKindsFromGhostedSlots(mounts, gunners);
            for (int m = 0; m < mounts.Length; m++)
            {
                if (ShipWeaponKind.IsCannonLaser(mounts[m], gunners, m))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Marks this MEGA as handled by the bullet strip. Lockout clears only
        /// when the pool is strictly above 10% of max. Muted lasers are turned
        /// off so a silenced barrel cannot keep a beam.
        /// </summary>
        public void BeginLaserTick(Entity mega, float energy, float maxEnergy)
        {
            _handledShips.Add(mega);
            _passWantedBurn = false;
            _reservedMounts.Clear();
            var megaState = EntityManager.GetComponentData<MegaShipState>(mega);
            _passLockout = megaState.CannonLaserLockout;
            if (_passLockout && CannonLaserMath.IsLaserPoolReady(energy, maxEnergy))
                _passLockout = false;

            if (!EntityManager.HasBuffer<ShipWeaponMountElement>(mega))
                return;

            var mounts = EntityManager.GetBuffer<ShipWeaponMountElement>(mega);
            var gunners = EntityManager.HasBuffer<MegaShipGunnerSlotElement>(mega)
                ? EntityManager.GetBuffer<MegaShipGunnerSlotElement>(mega)
                : default;
            ShipWeaponKind.RestoreMountKindsFromGhostedSlots(mounts, gunners);
            var arm = ShipWeaponArmState.Resolve(EntityManager, mega);
            for (int m = 0; m < mounts.Length; m++)
            {
                if (!ShipWeaponKind.IsCannonLaser(mounts[m], gunners, m))
                    continue;
                if (!ShipWeaponArmState.IsArmed(in arm, m))
                    WriteLaserOff(gunners, m, mounts[m]);
            }
        }

        /// <summary>
        /// Marks this laser's energy square as full this tick so the beam
        /// may stay during its fire-rate wait. A skipped square is turned
        /// off in <see cref="EndLaserTick"/>.
        /// </summary>
        public void NoteReservedLaser(int mountIndex)
        {
            _reservedMounts.Add(mountIndex);
        }

        /// <summary>
        /// Writes lockout and the pulse flag after every laser in the strip
        /// has had its chance. Lockout latches only when the pool is empty.
        /// Energy itself is written by the bullet walk.
        /// </summary>
        public void EndLaserTick(Entity mega, float energy)
        {
            if (!math.isfinite(energy))
                energy = 0f;
            if (CannonLaserMath.IsLaserPoolEmpty(energy))
                _passLockout = true;

            if (EntityManager.HasBuffer<ShipWeaponMountElement>(mega))
            {
                var mounts = EntityManager.GetBuffer<ShipWeaponMountElement>(mega);
                var gunners = EntityManager.HasBuffer<MegaShipGunnerSlotElement>(mega)
                    ? EntityManager.GetBuffer<MegaShipGunnerSlotElement>(mega)
                    : default;
                ShipWeaponKind.RestoreMountKindsFromGhostedSlots(mounts, gunners);
                for (int m = 0; m < mounts.Length; m++)
                {
                    if (!ShipWeaponKind.IsCannonLaser(mounts[m], gunners, m))
                        continue;
                    if (_reservedMounts.Contains(m))
                        continue;
                    WriteLaserOff(gunners, m, mounts[m]);
                    ForgetLaserLock(mega, m);
                }
            }

            var megaState = EntityManager.GetComponentData<MegaShipState>(mega);
            megaState.CannonLaserLockout = _passLockout;
            megaState.CannonLaserPulseOn = _passWantedBurn;
            EntityManager.SetComponentData(mega, megaState);
            _reservedMounts.Clear();
        }

        /// <summary>
        /// One laser barrel. While unlocked, drains authored DPS this tick and
        /// applies that slice × the lock ramp. No fire-rate wait — the beam is
        /// continuous. An empty pool latches lockout until energy is above 10%.
        /// </summary>
        public void TryContinuousBurn(
            Entity mega,
            int mountIndex,
            in ShipWeaponMountElement mount,
            ref float energy,
            float dt,
            float mapW,
            float mapH,
            double moonElapsed,
            double serverElapsed,
            Entity gemPrefab,
            float gemSpawnServerTime,
            bool topKiller,
            ref EntityCommandBuffer ecb)
        {
            var gunners = EntityManager.HasBuffer<MegaShipGunnerSlotElement>(mega)
                ? EntityManager.GetBuffer<MegaShipGunnerSlotElement>(mega)
                : default;
            if (!ShipWeaponKind.IsCannonLaser(mount, gunners, mountIndex) || mount.FirePower <= 0.01f)
            {
                WriteLaserOff(gunners, mountIndex, in mount);
                return;
            }

            var ship = EntityManager.GetComponentData<ShipState>(mega);
            if (_passLockout)
            {
                WriteLaserOff(gunners, mountIndex, in mount);
                return;
            }

            if (CannonLaserMath.IsLaserPoolEmpty(energy))
            {
                WriteLaserOff(gunners, mountIndex, in mount);
                _passLockout = true;
                return;
            }
            var xf = EntityManager.GetComponentData<LocalTransform>(mega);
            var weapon = EntityManager.HasComponent<ShipWeaponConfig>(mega)
                ? EntityManager.GetComponentData<ShipWeaponConfig>(mega)
                : default;
            var input = EntityManager.HasComponent<ShipInput>(mega)
                ? EntityManager.GetComponentData<ShipInput>(mega)
                : default;
            bool heal = EntityManager.HasComponent<ShipLoadoutState>(mega)
                        && EntityManager.GetComponentData<ShipLoadoutState>(mega).HealingBulletsActive;
            bool ownerShift = input.Overdrive;
            int attackerNet = EntityManager.HasComponent<GhostOwner>(mega)
                ? EntityManager.GetComponentData<GhostOwner>(mega).NetworkId
                : 0;

            float3 muzzle = ResolveMuzzle(xf, mount);
            float3 barrelFwd = MegaShipWeaponAim.GetBarrelForward(in xf, in mount);
            float acquireRange = ResolveRange(in mount, in weapon);
            float keepRange = CannonLaserMath.KeepRange(acquireRange);
            var aims = EntityManager.HasBuffer<MegaShipAutoAimSlotElement>(mega)
                ? EntityManager.GetBuffer<MegaShipAutoAimSlotElement>(mega)
                : default;
            Entity target = Entity.Null;
            float3 aimPoint = muzzle;
            if (aims.IsCreated && mountIndex < aims.Length && aims[mountIndex].Target != Entity.Null
                && aims[mountIndex].Target != mega)
            {
                target = aims[mountIndex].Target;
                aimPoint = aims[mountIndex].AimPoint;
            }

            bool haveLock = target != Entity.Null
                            && TryValidateLock(
                                mega, target, ship.Team, heal, muzzle, barrelFwd, keepRange,
                                mapW, mapH, moonElapsed, out aimPoint);
            bool canBurn = haveLock;
            bool mouseStream = ownerShift;
            if (!canBurn && !mouseStream)
            {
                WriteLaserOff(gunners, mountIndex, in mount);
                return;
            }

            float dps = CannonLaserMath.ComputeDps(mount.FirePower, mount.FireRate);
            float slice = dps * math.max(0f, dt);
            if (energy + 0.001f < slice)
            {
                if (energy > 0.0001f)
                {
                    slice = energy;
                    energy = 0f;
                }
                else
                {
                    WriteLaserOff(gunners, mountIndex, in mount);
                    _passLockout = true;
                    return;
                }
            }
            else
            {
                energy = math.max(0f, energy - slice);
            }

            _passWantedBurn = true;
            _reservedMounts.Add(mountIndex);
            if (CannonLaserMath.IsLaserPoolEmpty(energy))
                _passLockout = true;

            if (!canBurn)
            {
                WriteMouseAim(gunners, mountIndex, in xf, in mount, in input, barrelFwd, acquireRange, mapW, mapH);
                WriteLaserRamp(gunners, mountIndex, 0f);
                return;
            }

            float rampSeconds = ResolveLockRampSeconds(
                mega, mountIndex, target, canBurn: true, cycleActive: true, gunners);
            float rampMul = CannonLaserMath.ComputeRampMultiplier(rampSeconds);
            if (!_gemCarry.TryGetValue(target, out float carry))
                carry = 0f;

            var hit = CannonLaserHitApply.Apply(
                EntityManager, ecb, target, ship.Team, attackerNet,
                muzzle, TeamCommandRoleRules.ScaleFirePower(slice * rampMul, topKiller),
                heal, acquireRange, mapW, mapH, moonElapsed, serverElapsed,
                gemPrefab, gemSpawnServerTime, ref carry, mega);
            _gemCarry[target] = carry;

            if (!IsLiveLockTarget(target))
            {
                if (aims.IsCreated && mountIndex < aims.Length)
                    aims[mountIndex] = default;
                ForgetLaserLock(mega, mountIndex);
                WriteLaserOff(gunners, mountIndex, in mount);
                return;
            }

            float3 ghostAim = math.lengthsq(hit.HitPoint) > 0.0001f ? hit.HitPoint : aimPoint;
            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(muzzle, ghostAim, mapW, mapH);
            offset.y = 0f;
            float dist = math.length(offset);
            float3 fireDir = dist > 0.05f ? offset / dist : barrelFwd;
            int ghostId = MegaShipWeaponAim.ReadGhostId(EntityManager, target);
            MegaShipWeaponAim.WriteGhostedYaw(
                gunners, mountIndex, in mount, ghostAim, dist, fireDir, ghostId);
            WriteLaserRamp(
                gunners,
                mountIndex,
                CannonLaserMath.StepRampSeconds(rampSeconds, dt, reset: false, charging: true));
        }

        /// <summary>
        /// Hides every cannon beam when Fire is up. Lockout stays until the
        /// pool is above 10% of max — releasing Fire does not reset it.
        /// </summary>
        void Quench(Entity mega)
        {
            int mountCount = 0;
            if (EntityManager.HasBuffer<ShipWeaponMountElement>(mega))
            {
                var mounts = EntityManager.GetBuffer<ShipWeaponMountElement>(mega);
                var gunners = EntityManager.HasBuffer<MegaShipGunnerSlotElement>(mega)
                    ? EntityManager.GetBuffer<MegaShipGunnerSlotElement>(mega)
                    : default;
                ShipWeaponKind.RestoreMountKindsFromGhostedSlots(mounts, gunners);
                mountCount = mounts.Length;
                for (int m = 0; m < mountCount; m++)
                {
                    if (ShipWeaponKind.IsCannonLaser(mounts[m], gunners, m))
                        WriteLaserOff(gunners, m, mounts[m]);
                }
            }

            ForgetLaserLocks(mega, mountCount);
            if (!EntityManager.HasComponent<MegaShipState>(mega))
                return;

            var megaState = EntityManager.GetComponentData<MegaShipState>(mega);
            if (EntityManager.HasComponent<ShipState>(mega)
                && CannonLaserMath.IsLaserPoolEmpty(
                    EntityManager.GetComponentData<ShipState>(mega).CurrentEnergy))
                megaState.CannonLaserLockout = true;

            megaState.CannonLaserPulseOn = false;
            EntityManager.SetComponentData(mega, megaState);
        }

        /// <summary>Sticky lock still exists, is a valid team, and stays in keep-range.</summary>
        bool TryValidateLock(
            Entity self,
            Entity target,
            TeamId ownerTeam,
            bool heal,
            float3 muzzle,
            float3 barrelFwd,
            float range,
            float mapW,
            float mapH,
            double moonElapsed,
            out float3 aimPoint)
        {
            aimPoint = muzzle;
            if (target == Entity.Null || target == self || !EntityManager.Exists(target))
                return false;

            if (EntityManager.HasComponent<ShipState>(target)
                && EntityManager.HasComponent<LocalTransform>(target))
            {
                var other = EntityManager.GetComponentData<ShipState>(target);
                if (other.IsDead || other.Team == TeamId.None)
                    return false;
                if (heal ? other.Team != ownerTeam : other.Team == ownerTeam)
                    return false;

                var xf = EntityManager.GetComponentData<LocalTransform>(target);
                aimPoint = MegaShipCombatAim.GetAimPoint(EntityManager, target, xf);
                // Range only — the turret already snapped onto this lock. A 33°
                // cone vs a moving hull dropped the beam every time the muzzle
                // swept off the lead intercept.
                return CannonLaserMath.IsInRange(
                    muzzle, aimPoint, range, mapW, mapH, out _);
            }

            if (!heal
                && EntityManager.HasComponent<PlanetState>(target)
                && EntityManager.HasComponent<LocalTransform>(target))
            {
                if (!TryResolvePlanetAim(
                        target, ownerTeam, muzzle, range, mapW, mapH, moonElapsed, out aimPoint))
                    return false;
                return CannonLaserMath.IsInRange(
                    muzzle, aimPoint, range, mapW, mapH, out _);
            }

            if (!heal
                && EntityManager.HasComponent<AsteroidState>(target)
                && EntityManager.HasComponent<LocalTransform>(target))
            {
                var rock = EntityManager.GetComponentData<AsteroidState>(target);
                if (!rock.IsAliveForCombat)
                    return false;
                aimPoint = EntityManager.GetComponentData<LocalTransform>(target).Position;
                return CannonLaserMath.IsInRange(
                    muzzle, aimPoint, range, mapW, mapH, out _);
            }

            return false;
        }

        /// <summary>
        /// Closest enemy pad or enemy moon shield on one planet
        /// (same rule as AutoFire). Unowned and friendly worlds do not lock.
        /// </summary>
        bool TryResolvePlanetAim(
            Entity planet,
            TeamId ownerTeam,
            float3 from,
            float range,
            float mapW,
            float mapH,
            double moonElapsed,
            out float3 aim)
        {
            aim = default;
            if (!EntityManager.HasComponent<PlanetState>(planet)
                || !EntityManager.HasComponent<LocalTransform>(planet))
                return false;

            var planetState = EntityManager.GetComponentData<PlanetState>(planet);
            var planetXf = EntityManager.GetComponentData<LocalTransform>(planet);
            if (planetState.Ownership == ownerTeam || planetState.Ownership == TeamId.None)
                return false;

            float best = range;
            bool found = false;
            if (planetState.Ownership != TeamId.None
                && EntityManager.HasBuffer<PlanetaryDefenseSlotElement>(planet))
            {
                var slots = EntityManager.GetBuffer<PlanetaryDefenseSlotElement>(planet);
                int slotCount = slots.Length;
                for (int s = 0; s < slotCount; s++)
                {
                    var slot = slots[s];
                    if (slot.TurretLevel == 0 || slot.Health <= 0f)
                        continue;

                    float3 pad = PlanetaryDefenseMath.GetSlotWorldPosition(
                        planetXf.Position,
                        math.max(0.25f, planetXf.Scale),
                        planetState.PlanetLevel,
                        s,
                        slotCount);
                    float d = ToroidalMapEcs.ToroidalDistance(from, pad, mapW, mapH);
                    if (d >= best)
                        continue;
                    best = d;
                    aim = pad;
                    found = true;
                }
            }

            if (EntityManager.HasComponent<PlanetGemMoonState>(planet))
            {
                var moon = EntityManager.GetComponentData<PlanetGemMoonState>(planet);
                if (PlanetGemMoonCombatLogic.TryGetNonFriendlyMoonAim(
                        planetState.Ownership,
                        ownerTeam,
                        planetXf.Position,
                        planetXf.Scale,
                        planetState.PlanetLevel,
                        planetState.PlanetId,
                        planetState.IsHomePlanet,
                        moon.CurrentShield,
                        from,
                        mapW,
                        mapH,
                        moonElapsed,
                        out float3 moonAim,
                        out _))
                {
                    float moonDist = ToroidalMapEcs.ToroidalDistance(from, moonAim, mapW, mapH);
                    if (moonDist < best)
                    {
                        aim = moonAim;
                        found = true;
                    }
                }
            }

            return found;
        }

        static float3 ResolveMuzzle(in LocalTransform xf, in ShipWeaponMountElement mount)
        {
            if (ShipWeaponPose.TryResolve(xf, mount, out float3 muzzle, out _))
                return muzzle;
            return xf.Position;
        }

        static float ResolveRange(in ShipWeaponMountElement mount, in ShipWeaponConfig weapon)
        {
            if (mount.BulletRange > 0.5f)
                return mount.BulletRange;
            if (weapon.BulletMaxDistance > 0.5f)
                return weapon.BulletMaxDistance;
            return MegaShipCatalog.DefaultCannonAcquireRange;
        }

        /// <summary>True when the lock can still take damage this tick.</summary>
        bool IsLiveLockTarget(Entity target)
        {
            if (target == Entity.Null || !EntityManager.Exists(target))
                return false;
            if (EntityManager.HasComponent<ShipState>(target))
                return !EntityManager.GetComponentData<ShipState>(target).IsDead;
            if (EntityManager.HasComponent<AsteroidState>(target))
                return EntityManager.GetComponentData<AsteroidState>(target).IsAliveForCombat;
            return true;
        }

        /// <summary>Publishes a live lock without applying another damage slice.</summary>
        void WriteLockAim(
            DynamicBuffer<MegaShipGunnerSlotElement> gunners,
            int mountIndex,
            in LocalTransform xf,
            in ShipWeaponMountElement mount,
            Entity target,
            float3 aimPoint,
            float3 barrelFwd,
            float mapW,
            float mapH)
        {
            if (!gunners.IsCreated || mountIndex < 0 || mountIndex >= gunners.Length)
                return;

            float3 muzzle = ResolveMuzzle(in xf, in mount);
            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(muzzle, aimPoint, mapW, mapH);
            offset.y = 0f;
            float dist = math.length(offset);
            float3 fireDir = dist > 0.05f ? offset / dist : barrelFwd;
            int ghostId = MegaShipWeaponAim.ReadGhostId(EntityManager, target);
            MegaShipWeaponAim.WriteGhostedYaw(
                gunners, mountIndex, in mount, aimPoint, dist, fireDir, ghostId);
        }

        /// <summary>
        /// Shift mouse-aim with no lock: keep the beam on along the cursor ray
        /// (clamped to weapon range) so it matches the projectile guns.
        /// </summary>
        static void WriteMouseAim(
            DynamicBuffer<MegaShipGunnerSlotElement> gunners,
            int mountIndex,
            in LocalTransform xf,
            in ShipWeaponMountElement mount,
            in ShipInput input,
            float3 barrelFwd,
            float range,
            float mapW,
            float mapH)
        {
            if (!gunners.IsCreated || mountIndex < 0 || mountIndex >= gunners.Length)
                return;

            float3 muzzle = ResolveMuzzle(in xf, in mount);
            float3 fireDir = barrelFwd;
            float dist = math.max(0.05f, range);
            float3 ghostAim = muzzle + fireDir * dist;
            if (MegaShipWeaponAim.TryGetOwnerMouseAimPoint(in xf, in input, out float3 mouse)
                && ToroidalMapEcs.IsValidMapSize(mapW, mapH))
            {
                float3 offset = ToroidalMapEcs.ShortestOffsetXZ(muzzle, mouse, mapW, mapH);
                offset.y = 0f;
                float mouseDist = math.length(offset);
                if (mouseDist > 0.05f)
                {
                    fireDir = offset / mouseDist;
                    dist = math.min(mouseDist, range);
                    ghostAim = muzzle + fireDir * dist;
                }
            }

            MegaShipWeaponAim.WriteGhostedYaw(
                gunners, mountIndex, in mount, ghostAim, dist, fireDir);
        }

        static void WriteLaserOff(
            DynamicBuffer<MegaShipGunnerSlotElement> gunners,
            int mountIndex,
            in ShipWeaponMountElement mount)
        {
            if (!gunners.IsCreated || mountIndex < 0 || mountIndex >= gunners.Length)
                return;

            MegaShipWeaponAim.WriteGhostedYaw(gunners, mountIndex, in mount);
        }

        /// <summary>
        /// Current barrel ramp. A different lock entity restarts at 0 (50% DPS).
        /// Fire release / lockout also restarts. Same lock after a one-tick gap keeps charge.
        /// </summary>
        float ResolveLockRampSeconds(
            Entity mega,
            int mountIndex,
            Entity target,
            bool canBurn,
            bool cycleActive,
            DynamicBuffer<MegaShipGunnerSlotElement> gunners)
        {
            float current = 0f;
            if (gunners.IsCreated && mountIndex >= 0 && mountIndex < gunners.Length)
                current = math.max(0f, gunners[mountIndex].CannonLaserRampSeconds);

            if (!cycleActive)
            {
                ForgetLaserLock(mega, mountIndex);
                return 0f;
            }

            if (!canBurn || target == Entity.Null)
                return current;

            var key = new LaserRampKey { Ship = mega, Mount = mountIndex };
            if (!_lastLaserLock.TryGetValue(key, out Entity previous) || previous != target)
            {
                _lastLaserLock[key] = target;
                return 0f;
            }

            return current;
        }

        static void WriteLaserRamp(
            DynamicBuffer<MegaShipGunnerSlotElement> gunners,
            int mountIndex,
            float rampSeconds)
        {
            if (!gunners.IsCreated || mountIndex < 0 || mountIndex >= gunners.Length)
                return;

            var slot = gunners[mountIndex];
            slot.CannonLaserRampSeconds = math.max(0f, rampSeconds);
            gunners[mountIndex] = slot;
        }

        void ForgetLaserLock(Entity mega, int mountIndex)
        {
            _lastLaserLock.Remove(new LaserRampKey { Ship = mega, Mount = mountIndex });
        }

        void ForgetLaserLocks(Entity mega, int mountCount)
        {
            for (int m = 0; m < mountCount; m++)
                ForgetLaserLock(mega, m);
        }
    }
}
