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
    /// Server: MEGA cannon barrels burn a hitscan laser at firePower × fireRate DPS.
    /// Acquire comes from <see cref="MegaShipAutoFireSystem"/> (same in-range lock as
    /// other Titan guns; the turret slews onto that target first). Burn requires the
    /// barrel to sit inside the 33° cone after that slew. Beams stay on while
    /// Fire is held and energy remains.
    /// World: ServerSimulation. Map size from <see cref="MapStateSingleton"/>.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MegaShipAutoFireSystem))]
    [UpdateBefore(typeof(BulletSimulationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class CannonLaserCombatSystem : SystemBase
    {
        EntityQuery _megaQuery;

        /// <summary>Leftover cargo spill too small to spawn this tick (per victim).</summary>
        readonly Dictionary<Entity, float> _gemCarry = new Dictionary<Entity, float>(16);

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

        /// <summary>Drain energy and apply DPS for every locked cannon while Fire is held.</summary>
        protected override void OnUpdate()
        {
            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var map) ||
                !ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
                return;

            using var megas = _megaQuery.ToEntityArray(Allocator.Temp);
            bool anyCannon = false;
            for (int i = 0; i < megas.Length; i++)
            {
                if (!EntityManager.GetComponentData<MegaShipState>(megas[i]).IsMega)
                    continue;
                if (!HasCannonMount(megas[i]))
                    continue;
                anyCannon = true;
                break;
            }

            if (!anyCannon)
                return;

            float dt = SystemAPI.Time.DeltaTime;
            if (dt <= 0f)
                return;

            float mapW = map.MapWidth;
            float mapH = map.MapHeight;
            double serverElapsed = SystemAPI.Time.ElapsedTime;
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double moonElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : serverElapsed;

            Entity gemPrefab = Entity.Null;
            if (SystemAPI.TryGetSingleton<GamePrefabs>(out var gamePrefabs))
                gemPrefab = gamePrefabs.Gem;
            float gemSpawnServerTime = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                EntityManager, serverElapsed);

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            for (int i = 0; i < megas.Length; i++)
            {
                Entity mega = megas[i];
                if (!EntityManager.GetComponentData<MegaShipState>(mega).IsMega)
                    continue;

                TickMegaCannons(
                    mega, dt, mapW, mapH, moonElapsed, serverElapsed,
                    gemPrefab, gemSpawnServerTime, ecb);
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();
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
        /// Burns each locked cannon while Fire is held. Energy is shared: each
        /// beam costs its own DPS this tick. A barrel with no lock or an empty
        /// pool writes TargetDistance 0 so the beam hides. AutoFire tracking
        /// stays so turrets remain on target between ticks.
        /// </summary>
        void TickMegaCannons(
            Entity mega,
            float dt,
            float mapW,
            float mapH,
            double moonElapsed,
            double serverElapsed,
            Entity gemPrefab,
            float gemSpawnServerTime,
            EntityCommandBuffer ecb)
        {
            var ship = EntityManager.GetComponentData<ShipState>(mega);
            var megaState = EntityManager.GetComponentData<MegaShipState>(mega);
            if (ship.IsDead || ship.Team == TeamId.None)
            {
                if (megaState.CannonLaserLockout || megaState.CannonLaserPulseOn)
                {
                    megaState.CannonLaserLockout = false;
                    megaState.CannonLaserPulseOn = false;
                    EntityManager.SetComponentData(mega, megaState);
                }
                return;
            }

            var xf = EntityManager.GetComponentData<LocalTransform>(mega);
            var mounts = EntityManager.GetBuffer<ShipWeaponMountElement>(mega);
            var gunners = EntityManager.HasBuffer<MegaShipGunnerSlotElement>(mega)
                ? EntityManager.GetBuffer<MegaShipGunnerSlotElement>(mega)
                : default;
            ShipWeaponKind.RestoreMountKindsFromGhostedSlots(mounts, gunners);
            var aims = EntityManager.HasBuffer<MegaShipAutoAimSlotElement>(mega)
                ? EntityManager.GetBuffer<MegaShipAutoAimSlotElement>(mega)
                : default;

            bool heal = EntityManager.HasComponent<ShipLoadoutState>(mega) &&
                        EntityManager.GetComponentData<ShipLoadoutState>(mega).HealingBulletsActive;
            bool shocked = EntityManager.HasComponent<ShipElectricShockState>(mega) &&
                           EntityManager.GetComponentData<ShipElectricShockState>(mega)
                               .IsActive(serverElapsed);
            bool inOrbit = EntityManager.HasComponent<ShipOrbitState>(mega) &&
                           EntityManager.GetComponentData<ShipOrbitState>(mega).InOrbitRing;
            var input = EntityManager.HasComponent<ShipInput>(mega)
                ? EntityManager.GetComponentData<ShipInput>(mega)
                : default;
            bool wantsFire = EntityManager.HasComponent<ShipInput>(mega) &&
                             input.Fire.IsSet &&
                             !shocked &&
                             !inOrbit;
            bool ownerShift = EntityManager.HasComponent<ShipInput>(mega) && input.Overdrive;

            int attackerNet = 0;
            if (EntityManager.HasComponent<GhostOwner>(mega))
                attackerNet = EntityManager.GetComponentData<GhostOwner>(mega).NetworkId;

            var weapon = EntityManager.HasComponent<ShipWeaponConfig>(mega)
                ? EntityManager.GetComponentData<ShipWeaponConfig>(mega)
                : default;

            float energy = ship.CurrentEnergy;
            float maxEnergy = math.max(1f, ship.MaxEnergy);
            bool lockout = megaState.CannonLaserLockout;
            if (lockout && energy >= maxEnergy * CannonLaserMath.RechargeRatio)
                lockout = false;

            bool cycleActive = wantsFire && !lockout;

            bool wantedBurn = false;
            int mountCount = mounts.Length;
            for (int m = 0; m < mountCount; m++)
            {
                var mount = mounts[m];
                if (!ShipWeaponKind.IsCannonLaser(mount, gunners, m) || mount.FirePower <= 0.01f)
                    continue;

                float3 muzzle = ResolveMuzzle(xf, mount);
                float3 barrelFwd = MegaShipWeaponAim.GetBarrelForward(in xf, in mount);
                float acquireRange = ResolveRange(in mount, in weapon);
                float keepRange = CannonLaserMath.KeepRange(acquireRange);
                Entity target = Entity.Null;
                float3 aimPoint = muzzle;
                if (aims.IsCreated && m < aims.Length && aims[m].Target != Entity.Null
                    && aims[m].Target != mega)
                {
                    target = aims[m].Target;
                    aimPoint = aims[m].AimPoint;
                }

                bool haveLock = wantsFire
                    && !lockout
                    && target != Entity.Null
                    && TryValidateLock(
                        mega, target, ship.Team, heal, muzzle, barrelFwd, keepRange,
                        mapW, mapH, moonElapsed, out aimPoint);
                bool canBurn = haveLock && cycleActive && energy > 0.0001f;
                bool mouseStream = cycleActive && ownerShift && energy > 0.0001f;
                if (canBurn || mouseStream)
                    wantedBurn = true;

                float dps = CannonLaserMath.ComputeDps(mount.FirePower, mount.FireRate);
                float slice = dps * dt;
                if ((canBurn || mouseStream) && energy < slice)
                {
                    if (energy <= 0.0001f)
                    {
                        canBurn = false;
                        mouseStream = false;
                    }
                    else
                    {
                        slice = energy;
                        energy = 0f;
                    }
                }
                else if (canBurn || mouseStream)
                {
                    energy = math.max(0f, energy - slice);
                }

                if ((!canBurn && !mouseStream) || slice <= 0.0001f)
                {
                    // Keep AutoFire tracking so turrets stay on target. Shift
                    // mouse-aim still follows the cursor. Rewrite the ghost so
                    // predicted clients see AimWorld / GhostId on the first tick.
                    if (cycleActive && haveLock)
                    {
                        WriteLockAim(gunners, m, in xf, in mount, target, aimPoint, barrelFwd, mapW, mapH);
                        continue;
                    }
                    if (cycleActive && ownerShift)
                    {
                        WriteMouseAim(gunners, m, in xf, in mount, in input, barrelFwd, acquireRange, mapW, mapH);
                        continue;
                    }

                    WriteLaserOff(gunners, m, in mount);
                    continue;
                }

                if (!canBurn)
                {
                    WriteMouseAim(gunners, m, in xf, in mount, in input, barrelFwd, acquireRange, mapW, mapH);
                    continue;
                }

                if (!_gemCarry.TryGetValue(target, out float carry))
                    carry = 0f;

                var hit = CannonLaserHitApply.Apply(
                    EntityManager, ecb, target, ship.Team, attackerNet,
                    muzzle, slice, heal, acquireRange, mapW, mapH, moonElapsed, serverElapsed,
                    gemPrefab, gemSpawnServerTime, ref carry);
                _gemCarry[target] = carry;

                // Dead / mined-out locks must not keep publishing the corpse aim.
                // Next AutoFire tick re-acquires; writing the last hit pinned the beam.
                if (!IsLiveLockTarget(target))
                {
                    WriteLaserOff(gunners, m, in mount);
                    continue;
                }

                float3 ghostAim = math.lengthsq(hit.HitPoint) > 0.0001f
                    ? hit.HitPoint
                    : aimPoint;
                float3 offset = ToroidalMapEcs.ShortestOffsetXZ(muzzle, ghostAim, mapW, mapH);
                offset.y = 0f;
                float dist = math.length(offset);
                float3 fireDir = dist > 0.05f ? offset / dist : barrelFwd;
                int ghostId = MegaShipWeaponAim.ReadGhostId(EntityManager, target);
                MegaShipWeaponAim.WriteGhostedYaw(
                    gunners, m, in mount, ghostAim, dist, fireDir, ghostId);
            }

            if (!math.isfinite(energy))
                energy = 0f;
            energy = math.max(0f, energy);
            if (wantedBurn && energy <= 0.0001f)
                lockout = true;

            ship.CurrentEnergy = energy;
            EntityManager.SetComponentData(mega, ship);
            megaState.CannonLaserLockout = lockout;
            megaState.CannonLaserPulseOn = cycleActive;
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

        /// <summary>Closest hostile pad or moon on one planet (same rule as AutoFire).</summary>
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
            if (planetState.Ownership == TeamId.None || planetState.Ownership == ownerTeam)
                return false;

            float best = range;
            bool found = false;
            if (EntityManager.HasBuffer<PlanetaryDefenseSlotElement>(planet))
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

            if (EntityManager.HasComponent<PlanetGemMoonState>(planet)
                && !PlanetGemMoonCombatLogic.IsTeamFriendlyToMoon(planetState.Ownership, ownerTeam))
            {
                float3 moonPos = PlanetOrbitMath.GetMoonWorldPosition(
                    planetXf.Position,
                    math.max(0.25f, planetXf.Scale),
                    planetState.PlanetLevel,
                    planetState.PlanetId,
                    moonElapsed,
                    planetState.IsHomePlanet);
                float moonDist = ToroidalMapEcs.ToroidalDistance(from, moonPos, mapW, mapH);
                if (moonDist < best)
                {
                    aim = moonPos;
                    found = true;
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
    }
}
