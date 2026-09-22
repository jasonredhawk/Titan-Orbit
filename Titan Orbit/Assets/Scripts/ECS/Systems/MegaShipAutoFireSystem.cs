using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: MEGA mounts auto-aim like planetary turrets, but only fire when the MEGA
    /// owner's <see cref="ShipInput.Fire"/> is held. Only the ship owner aims these guns —
    /// there is no remote Take Control path. Cannon barrels stay parked and lock anyone
    /// Cannon lasers use the same in-range auto-aim, then the turret slews onto
    /// that lock; <see cref="CannonLaserCombatSystem"/> burns once the barrel faces it.
    /// Damage mode treats enemy ships, enemy planetary defense turrets, and enemy
    /// moon shields as one priority — closest in range wins. Unowned worlds are skipped. Asteroids are second
    /// (only when no combat target is in that gun's range). Heal mode aims at the
    /// nearest friendly ship. Cannon lasers also acquire asteroids (lowest
    /// priority) so a destroyed rock does not leave the beam stuck. Projectile
    /// guns only auto-aim rocks when
    /// <see cref="TitanOrbitDebugFlags.MegaShipsAutoFireAsteroids"/> is on
    /// (Editor / MPPM host).
    /// <para>
    /// Each gun searches from its own muzzle when Fire is pressed. A live lock
    /// sticks — a closer ship will not steal it. If that target dies or leaves
    /// range, the barrel grabs the next closest. Releasing Fire clears locks so
    /// the next press re-acquires (to switch off a still-valid target).
    /// If a gun finds nothing, it fires along hull forward until someone enters range.
    /// Aim points are lead intercepts from <see cref="MegaShipLeadAim"/> — target velocity
    /// minus this hull's velocity — so inherited <c>shipVel</c> on the bullet does not
    /// undershoot. Turrets slew toward that lock (or hull forward) before
    /// Phase B so shots leave along the barrel — the same ray regular ships use.
    /// <see cref="BulletSimulationSystem"/> Phase B fires ready mounts along
    /// <see cref="ShipWeaponPose"/> barrel forward.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] MEGAs have no overdrive. <see cref="ShipInput.Overdrive"/> (Shift)
    /// locks hull heading and points every gun and cannon at the mouse
    /// world point (<see cref="ShipInput.AimPlanarDir"/> × <see cref="ShipInput.AimDistance"/>).
    /// Cannons hitscan the 33° cone along that aim and keep a sticky lock in
    /// that cone. Fire is still required to spend energy.
    /// </para>
    /// Map size comes from <see cref="MapStateSingleton"/>. Distances use
    /// <see cref="ToroidalMapEcs.ToroidalDistance"/>.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedFixedStepSimulationSystemGroup))]
    [UpdateBefore(typeof(BulletSimulationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class MegaShipAutoFireSystem : SystemBase
    {

        EntityQuery _megaQuery;
        EntityQuery _shipQuery;
        EntityQuery _planetQuery;
        EntityQuery _asteroidQuery;

        static bool s_LoggedDisable;

        /// <summary>Cache queries used every tick.</summary>
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
            _shipQuery = GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<LocalTransform>());
            _planetQuery = GetEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>());
            _asteroidQuery = GetEntityQuery(
                ComponentType.ReadOnly<AsteroidTag>(),
                ComponentType.ReadOnly<AsteroidState>(),
                ComponentType.ReadOnly<LocalTransform>());
        }

        /// <summary>Refresh sticky auto-aim while the owner holds Fire. Does not spawn bullets.</summary>
        protected override void OnUpdate()
        {
            if (TitanOrbitDebugFlags.DisableMegaShipAutoFire)
            {
                if (!s_LoggedDisable)
                {
                    s_LoggedDisable = true;
                    Debug.Log("[MegaShipAutoFire] disabled (GameManager Debug — Disable MEGA Auto-Fire).");
                }

                return;
            }

            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var map) ||
                !ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
                return;

            using var megas = _megaQuery.ToEntityArray(Allocator.Temp);
            bool anyLive = false;
            for (int i = 0; i < megas.Length; i++)
            {
                if (EntityManager.GetComponentData<MegaShipState>(megas[i]).IsMega)
                {
                    anyLive = true;
                    break;
                }
            }

            if (!anyLive)
                return;

            bool anyActive = false;
            for (int i = 0; i < megas.Length; i++)
            {
                if (!EntityManager.GetComponentData<MegaShipState>(megas[i]).IsMega)
                    continue;

                // Shift = heading lock + mouse-aim all MEGA guns (no overdrive on MEGAs).
                // Keep barrels tracking the cursor even before Fire is held.
                bool ownerShift = IsOwnerShiftHeld(megas[i]);
                bool wantsFire = OwnerWantsFire(megas[i]);
                if (!wantsFire && !ownerShift)
                {
                    ClearAimSlots(megas[i]);
                    continue;
                }

                anyActive = true;
            }

            if (!anyActive)
                return;

            float dt = SystemAPI.Time.DeltaTime;
            float mapW = map.MapWidth;
            float mapH = map.MapHeight;

            NativeArray<Entity> ships = default;
            NativeArray<ShipState> shipStates = default;
            NativeArray<LocalTransform> shipXfs = default;
            bool shipsLoaded = false;
            NativeArray<Entity> planets = default;
            bool planetsLoaded = false;

            bool debugAsteroids = TitanOrbitDebugFlags.MegaShipsAutoFireAsteroids;
            NativeArray<Entity> asteroidEntities = default;
            NativeArray<AsteroidState> asteroidStates = default;
            NativeArray<LocalTransform> asteroidXfs = default;
            bool asteroidsLoaded = false;

            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double moonElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : (float)SystemAPI.Time.ElapsedTime;

            for (int i = 0; i < megas.Length; i++)
            {
                Entity mega = megas[i];
                var megaState = EntityManager.GetComponentData<MegaShipState>(mega);
                if (!megaState.IsMega)
                    continue;

                var ship = EntityManager.GetComponentData<ShipState>(mega);
                if (ship.IsDead || ship.Team == TeamId.None)
                    continue;

                var xf = EntityManager.GetComponentData<LocalTransform>(mega);
                var mounts = EntityManager.GetBuffer<ShipWeaponMountElement>(mega);
                var gunners = EntityManager.HasBuffer<MegaShipGunnerSlotElement>(mega)
                    ? EntityManager.GetBuffer<MegaShipGunnerSlotElement>(mega)
                    : default;
                ShipWeaponKind.RestoreMountKindsFromGhostedSlots(mounts, gunners);

                bool heal = EntityManager.HasComponent<ShipLoadoutState>(mega) &&
                            EntityManager.GetComponentData<ShipLoadoutState>(mega).HealingBulletsActive;

                var weapon = EntityManager.HasComponent<ShipWeaponConfig>(mega)
                    ? EntityManager.GetComponentData<ShipWeaponConfig>(mega)
                    : default;

                int bankIndex = 0;
                if (EntityManager.HasComponent<ShipLoadoutState>(mega))
                {
                    bankIndex = BulletBankFireResolve.ResolveFireBankIndex(
                        EntityManager.GetComponentData<ShipLoadoutState>(mega));
                }

                float3 shooterVel = ReadPlanarVelocity(mega);
                int shipLevel = math.max(1, ship.ShipLevel);

                bool ownerShift = IsOwnerShiftHeld(mega);
                bool ownerWantsFire = OwnerWantsFire(mega);

                // --- Shift: every Titan barrel looks at the mouse ---
                // [TITAN-ORBIT] Owner Shift is the MEGA "strafe / lock heading" mode.
                // Projectile auto-locks clear so releasing Shift re-acquires.
                // Cannon lasers keep a sticky cone lock — wiping it every tick
                // re-focused the beam and restarted the hum.
                if (ownerShift)
                {
                    ClearProjectileAimSlots(mega);
                    AimUnoccupiedMountsAtMouse(mega, xf, mounts, gunners, mapW, mapH, dt);
                }

                if (!ownerWantsFire)
                {
                    // Next Fire press must search again — do not keep a lock across a release.
                    ClearAutoAimTargetSlots(mega);
                    continue;
                }

                if (!EntityManager.HasBuffer<MegaShipAutoAimSlotElement>(mega))
                    continue;

                var aims = EntityManager.GetBuffer<MegaShipAutoAimSlotElement>(mega);
                int mountCount = mounts.Length;
                ResizeAimSlots(aims, mountCount);

                if (!shipsLoaded)
                {
                    ships = _shipQuery.ToEntityArray(Allocator.Temp);
                    shipStates = _shipQuery.ToComponentDataArray<ShipState>(Allocator.Temp);
                    shipXfs = _shipQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                    shipsLoaded = true;
                }

                if (!heal && !planetsLoaded)
                {
                    planets = _planetQuery.ToEntityArray(Allocator.Temp);
                    planetsLoaded = true;
                }

                bool cannonsWantAsteroids = false;
                if (!heal)
                {
                    for (int m = 0; m < mounts.Length; m++)
                    {
                        if (!ShipWeaponKind.IsCannonLaser(mounts[m], gunners, m))
                            continue;
                        cannonsWantAsteroids = true;
                        break;
                    }
                }

                if (!heal && (debugAsteroids || cannonsWantAsteroids) && !asteroidsLoaded)
                {
                    asteroidEntities = _asteroidQuery.ToEntityArray(Allocator.Temp);
                    asteroidStates = _asteroidQuery.ToComponentDataArray<AsteroidState>(Allocator.Temp);
                    asteroidXfs = _asteroidQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                    asteroidsLoaded = true;
                }

                if (ownerShift)
                {
                    // Barrel already faces the cursor. Keep the last cone lock;
                    // only search again when that target leaves the cone / range.
                    for (int m = 0; m < mountCount; m++)
                    {
                        if (!ShipWeaponKind.IsCannonLaser(mounts[m], gunners, m))
                            continue;

                        var slot = aims[m];
                        var mount = mounts[m];
                        float3 muzzle = ResolveMuzzle(xf, mount);
                        float3 barrelFwd = MegaShipWeaponAim.GetBarrelForward(in xf, in mount);
                        float acquireRange = ResolveMountRange(mount, in weapon);
                        float keepRange = CannonLaserMath.KeepRange(acquireRange);
                        bool hadLock = slot.Target != Entity.Null && slot.Target != mega;
                        if (hadLock && TryKeepStickyTarget(
                            mega, ship.Team, heal, muzzle, shooterVel, 0f, keepRange,
                            mapW, mapH, moonElapsed, ref slot,
                            coneLock: true, barrelFwd))
                        {
                            aims[m] = slot;
                            continue;
                        }

                        if (TryAcquireClosestTarget(
                            mega, ship.Team, heal, muzzle, shooterVel, 0f, acquireRange,
                            mapW, mapH, moonElapsed, MegaShipAutoAimClass.None,
                            ships, shipStates, shipXfs, planets,
                            true, asteroidEntities, asteroidStates, asteroidXfs,
                            out Entity coneTarget, out float3 coneAim, out float coneDist,
                            coneLock: true, barrelFwd))
                        {
                            aims[m] = new MegaShipAutoAimSlotElement
                            {
                                Target = coneTarget,
                                AimPoint = coneAim,
                                InterceptDistance = coneDist,
                            };
                        }
                        else
                        {
                            // Shift cone is intentional aim. An empty cone parks;
                            // do not keep cooking a target the barrel no longer faces.
                            aims[m] = new MegaShipAutoAimSlotElement
                            {
                                Target = mega,
                                AimPoint = default,
                            };
                        }
                    }

                    continue;
                }

                for (int m = 0; m < mountCount; m++)
                {
                    bool isCannon = ShipWeaponKind.IsCannonLaser(mounts[m], gunners, m);

                    var slot = aims[m];
                    var mount = mounts[m];
                    float3 muzzle = ResolveMuzzle(xf, mount);
                    float3 barrelFwd = MegaShipWeaponAim.GetBarrelForward(in xf, in mount);
                    float acquireRange = ResolveMountRange(mount, in weapon);
                    // Projectiles must acquire inside travel range. A +8 pad here
                    // locked rocks the sniper could not reach — no hit, no float.
                    float keepRange = isCannon
                        ? CannonLaserMath.KeepRange(acquireRange)
                        : acquireRange;
                    int mountBank = BulletBankFireResolve.ResolveMegaMountFireBank(
                        in mount, bankIndex);
                    float leadBulletSpeed = isCannon
                        ? 0f
                        : ResolveLeadBulletSpeed(
                            in weapon, in mount, mountBank, shipLevel);
                    // Live lock sticks (no closer-target steal). Dead / out of
                    // keep-range falls through to a fresh closest-target search.
                    bool hadLock = slot.Target != Entity.Null && slot.Target != mega;
                    bool kept = hadLock && TryKeepStickyTarget(
                        mega, ship.Team, heal, muzzle, shooterVel, leadBulletSpeed, keepRange,
                        mapW, mapH, moonElapsed, ref slot,
                        coneLock: false, barrelFwd);
                    if (kept)
                    {
                        aims[m] = slot;
                        continue;
                    }

                    if (TryAcquireClosestTarget(
                        mega, ship.Team, heal, muzzle, shooterVel, leadBulletSpeed, acquireRange,
                        mapW, mapH, moonElapsed, MegaShipAutoAimClass.None,
                        ships, shipStates, shipXfs, planets,
                        debugAsteroids || isCannon, asteroidEntities, asteroidStates, asteroidXfs,
                        out Entity target, out float3 aimPoint, out float interceptDistance,
                        coneLock: false, barrelFwd))
                    {
                        aims[m] = new MegaShipAutoAimSlotElement
                        {
                            Target = target,
                            AimPoint = aimPoint,
                            InterceptDistance = interceptDistance,
                        };
                    }
                    else
                    {
                        // Cannons hold the last entity through a one-tick miss so
                        // keep-range hysteresis can recover and the DPS ramp stays.
                        aims[m] = isCannon
                            ? HoldLastLockOrPark(mega, hadLock, slot)
                            : new MegaShipAutoAimSlotElement
                            {
                                Target = mega,
                                AimPoint = default,
                            };
                    }
                }

                // Aim the gun, then Phase B fires along the barrel (regular-ship ray).
                RotateUnoccupiedMountsTowardAim(
                    EntityManager, mega, xf, mounts, aims, gunners, mapW, mapH, moonElapsed, dt);
            }

            if (ships.IsCreated)
                ships.Dispose();
            if (shipStates.IsCreated)
                shipStates.Dispose();
            if (shipXfs.IsCreated)
                shipXfs.Dispose();
            if (planets.IsCreated)
                planets.Dispose();
            if (asteroidEntities.IsCreated)
                asteroidEntities.Dispose();
            if (asteroidStates.IsCreated)
                asteroidStates.Dispose();
            if (asteroidXfs.IsCreated)
                asteroidXfs.Dispose();
        }

        /// <summary>
        /// Keep a live cannon lock through a one-tick keep/acquire miss so the
        /// barrel can recover inside keep-range and the DPS ramp does not restart.
        /// </summary>
        static MegaShipAutoAimSlotElement HoldLastLockOrPark(
            Entity mega,
            bool hadLock,
            in MegaShipAutoAimSlotElement slot)
        {
            if (hadLock && slot.Target != Entity.Null && slot.Target != mega)
                return slot;
            return new MegaShipAutoAimSlotElement
            {
                Target = mega,
                AimPoint = default,
            };
        }

        /// <summary>Per-barrel acquire range from catalog component stats; short fallback if unset.</summary>
        static float ResolveMountRange(in ShipWeaponMountElement mount, in ShipWeaponConfig weapon)
        {
            if (mount.BulletRange > 0.5f)
                return mount.BulletRange;
            if (weapon.BulletMaxDistance > 0.5f && weapon.BulletMaxDistance <= MegaShipCatalog.DefaultCannonAcquireRange + 0.01f)
                return weapon.BulletMaxDistance;
            return MegaShipCatalog.DefaultBulletAcquireRange;
        }

        /// <summary>World muzzle for this mount (unbounded hull + bake local). Falls back to hull origin.</summary>
        static float3 ResolveMuzzle(in LocalTransform xf, in ShipWeaponMountElement mount)
        {
            if (ShipWeaponPose.TryResolve(xf, mount, out float3 muzzle, out _))
                return muzzle;
            return xf.Position;
        }

        /// <summary>
        /// Slews each mount toward its sticky lock (toroidal muzzle→AimPoint)
        /// or hull forward when the slot is parked.
        /// </summary>
        void RotateUnoccupiedMountsTowardAim(
            EntityManager em,
            Entity mega,
            in LocalTransform xf,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            DynamicBuffer<MegaShipAutoAimSlotElement> aims,
            DynamicBuffer<MegaShipGunnerSlotElement> gunners,
            float mapW,
            float mapH,
            double moonElapsed,
            float dt)
        {
            float3 hullForward = math.rotate(xf.Rotation, new float3(0f, 0f, 1f));
            hullForward.y = 0f;
            if (math.lengthsq(hullForward) < 0.0001f)
                hullForward = new float3(0f, 0f, 1f);
            else
                hullForward = math.normalize(hullForward);

            var ship = em.HasComponent<ShipState>(mega)
                ? em.GetComponentData<ShipState>(mega)
                : default;
            bool heal = em.HasComponent<ShipLoadoutState>(mega)
                && em.GetComponentData<ShipLoadoutState>(mega).HealingBulletsActive;
            var weapon = em.HasComponent<ShipWeaponConfig>(mega)
                ? em.GetComponentData<ShipWeaponConfig>(mega)
                : default;

            int mountCount = mounts.Length;
            for (int m = 0; m < mountCount; m++)
            {
                if (m >= aims.Length || aims[m].Target == Entity.Null)
                    continue;

                var mount = mounts[m];
                float3 desired = hullForward;
                float targetDist = 0f;
                float3 ghostAim = xf.Position;
                if (aims[m].Target != mega)
                {
                    if (!ShipWeaponPose.TryResolve(xf, mount, out float3 muzzle, out _))
                        muzzle = xf.Position;

                    float3 offset = ToroidalMapEcs.ShortestOffsetXZ(muzzle, aims[m].AimPoint, mapW, mapH);
                    offset.y = 0f;
                    float dist = math.length(offset);
                    if (dist >= 0.05f)
                    {
                        desired = offset / dist;
                        targetDist = dist;
                    }
                    else
                    {
                        targetDist = CannonLaserMath.MinTrackingDistance;
                    }

                    // Mesh LookAt uses the target's current point, not the lead intercept.
                    // Lead stays on AimPoint / LocalRotation so bullets do not change.
                    float mountRange = ResolveMountRange(mount, in weapon) + 8f;
                    if (!TryGetCurrentTargetPos(
                            em, aims[m].Target, ship.Team, heal, muzzle, mountRange,
                            mapW, mapH, moonElapsed, out ghostAim))
                    {
                        if (!em.Exists(aims[m].Target))
                        {
                            ghostAim = xf.Position;
                            targetDist = 0f;
                            desired = hullForward;
                        }
                        else
                        {
                            // Live lock, aim helper missed this tick — keep the last
                            // point so a close / pad flicker does not park the barrel.
                            ghostAim = aims[m].AimPoint;
                            if (targetDist < 0.05f)
                                targetDist = CannonLaserMath.MinTrackingDistance;
                        }
                    }
                }

                MegaShipWeaponAim.RotateMountTowardWorldDir(in xf, ref mount, desired, dt);
                mounts[m] = mount;
                int targetGhost = targetDist > 0.05f
                    ? MegaShipWeaponAim.ReadGhostId(em, aims[m].Target)
                    : 0;
                MegaShipWeaponAim.WriteGhostedYaw(
                    gunners, m, in mount, ghostAim, targetDist, desired, targetGhost);
            }
        }

        /// <summary>
        /// Current (not lead) aim point for hybrid turret LookAt. Ships use combat aim,
        /// planets use the locked pad/moon, asteroids use the rock center.
        /// </summary>
        bool TryGetCurrentTargetPos(
            EntityManager em,
            Entity target,
            TeamId ownerTeam,
            bool heal,
            float3 from,
            float range,
            float mapW,
            float mapH,
            double moonElapsed,
            out float3 pos)
        {
            pos = default;
            if (target == Entity.Null || !em.Exists(target))
                return false;

            if (em.HasComponent<ShipState>(target) && em.HasComponent<LocalTransform>(target))
            {
                pos = MegaShipCombatAim.GetAimPoint(
                    em, target, em.GetComponentData<LocalTransform>(target));
                return true;
            }

            if (!heal
                && em.HasComponent<PlanetState>(target)
                && em.HasComponent<LocalTransform>(target))
            {
                return TryResolvePlanetAim(
                    target, ownerTeam, from, range, mapW, mapH, moonElapsed,
                    out pos, out _);
            }

            if (!heal
                && em.HasComponent<AsteroidState>(target)
                && em.HasComponent<LocalTransform>(target))
            {
                if (!em.GetComponentData<AsteroidState>(target).IsAliveForCombat)
                    return false;
                pos = em.GetComponentData<LocalTransform>(target).Position;
                return true;
            }

            return false;
        }

        static void ResizeAimSlots(DynamicBuffer<MegaShipAutoAimSlotElement> aims, int mountCount)
        {
            while (aims.Length < mountCount)
                aims.Add(default);
            while (aims.Length > mountCount)
                aims.RemoveAt(aims.Length - 1);
        }

        /// <summary>
        /// Drop projectile auto-locks only. Cannon laser slots stay so Shift
        /// mouse-aim does not wipe a live cone lock every tick.
        /// </summary>
        void ClearProjectileAimSlots(Entity mega)
        {
            var mounts = EntityManager.HasBuffer<ShipWeaponMountElement>(mega)
                ? EntityManager.GetBuffer<ShipWeaponMountElement>(mega)
                : default;
            var gunners = EntityManager.HasBuffer<MegaShipGunnerSlotElement>(mega)
                ? EntityManager.GetBuffer<MegaShipGunnerSlotElement>(mega)
                : default;
            if (mounts.IsCreated)
                ShipWeaponKind.RestoreMountKindsFromGhostedSlots(mounts, gunners);

            if (EntityManager.HasBuffer<MegaShipAutoAimSlotElement>(mega))
            {
                var aims = EntityManager.GetBuffer<MegaShipAutoAimSlotElement>(mega);
                for (int i = 0; i < aims.Length; i++)
                {
                    if (mounts.IsCreated && i < mounts.Length
                        && ShipWeaponKind.IsCannonLaser(mounts[i], gunners, i))
                        continue;
                    aims[i] = default;
                }
            }

            if (!gunners.IsCreated)
                return;

            for (int i = 0; i < gunners.Length; i++)
            {
                if (mounts.IsCreated && i < mounts.Length
                    && ShipWeaponKind.IsCannonLaser(mounts[i], gunners, i))
                    continue;
                var slot = gunners[i];
                slot.TargetDistance = 0f;
                slot.AimWorldX = 0f;
                slot.AimWorldZ = 0f;
                slot.TargetGhostId = 0;
                if (mounts.IsCreated && i < mounts.Length)
                    slot.CurrentYawDeg = MegaShipWeaponAim.GetLocalYawDeg(mounts[i].LocalRotation);
                gunners[i] = slot;
            }
        }

        /// <summary>
        /// Drop sticky target entities only. Gunner yaw is left alone (Shift mouse-aim
        /// still writes those slots). Next Fire press searches again.
        /// </summary>
        void ClearAutoAimTargetSlots(Entity mega)
        {
            if (!EntityManager.HasBuffer<MegaShipAutoAimSlotElement>(mega))
                return;

            var aims = EntityManager.GetBuffer<MegaShipAutoAimSlotElement>(mega);
            for (int i = 0; i < aims.Length; i++)
                aims[i] = default;
        }

        /// <summary>
        /// Drop all sticky locks and clear ghosted aim so clients park barrels on the hull.
        /// Next Fire press runs a fresh per-muzzle search.
        /// </summary>
        void ClearAimSlots(Entity mega)
        {
            if (EntityManager.HasBuffer<MegaShipAutoAimSlotElement>(mega))
            {
                var aims = EntityManager.GetBuffer<MegaShipAutoAimSlotElement>(mega);
                for (int i = 0; i < aims.Length; i++)
                    aims[i] = default;
            }

            if (!EntityManager.HasBuffer<MegaShipGunnerSlotElement>(mega))
                return;

            var gunners = EntityManager.GetBuffer<MegaShipGunnerSlotElement>(mega);
            var mounts = EntityManager.HasBuffer<ShipWeaponMountElement>(mega)
                ? EntityManager.GetBuffer<ShipWeaponMountElement>(mega)
                : default;
            MegaShipWeaponAim.ClearUnoccupiedTracking(gunners, mounts);
        }

        /// <summary>
        /// Keep the last lock if it still exists, is a valid team, and is inside range
        /// from this gun's muzzle. mapW/mapH from <see cref="MapStateSingleton"/>.
        /// Range uses the target's current position; <see cref="MegaShipAutoAimSlotElement.AimPoint"/>
        /// is rewritten to the lead intercept each tick.
        /// </summary>
        bool TryKeepStickyTarget(
            Entity self,
            TeamId ownerTeam,
            bool heal,
            float3 from,
            float3 shooterVel,
            float bulletSpeed,
            float range,
            float mapW,
            float mapH,
            double moonElapsed,
            ref MegaShipAutoAimSlotElement aim,
            bool coneLock = false,
            float3 coneFwd = default)
        {
            Entity target = aim.Target;
            if (target == Entity.Null || target == self || !EntityManager.Exists(target))
            {
                aim = default;
                return false;
            }

            if (EntityManager.HasComponent<ShipState>(target)
                && EntityManager.HasComponent<LocalTransform>(target))
            {
                var other = EntityManager.GetComponentData<ShipState>(target);
                if (other.IsDead || other.Team == TeamId.None
                    || (heal ? other.Team != ownerTeam : other.Team == ownerTeam))
                {
                    aim = default;
                    return false;
                }

                var xf = EntityManager.GetComponentData<LocalTransform>(target);
                float3 pos = MegaShipCombatAim.GetAimPoint(EntityManager, target, xf);
                if (!PassesRangeAndCone(from, coneFwd, pos, range, mapW, mapH, coneLock))
                    return false;

                if (coneLock)
                {
                    WriteHitscanAim(pos, from, mapW, mapH, ref aim);
                    return true;
                }

                WriteLeadAim(from, shooterVel, pos, ReadPlanarVelocity(target), bulletSpeed,
                    range, mapW, mapH, ref aim);
                return true;
            }

            if (!heal
                && EntityManager.HasComponent<PlanetState>(target)
                && EntityManager.HasComponent<LocalTransform>(target))
            {
                if (!TryResolvePlanetAim(
                        target, ownerTeam, from, range, mapW, mapH, moonElapsed,
                        out float3 planetAim, out float3 planetVel))
                {
                    aim = default;
                    return false;
                }

                if (!PassesRangeAndCone(from, coneFwd, planetAim, range, mapW, mapH, coneLock))
                    return false;

                if (coneLock)
                {
                    WriteHitscanAim(planetAim, from, mapW, mapH, ref aim);
                    return true;
                }

                WriteLeadAim(from, shooterVel, planetAim, planetVel, bulletSpeed,
                    range, mapW, mapH, ref aim);
                return true;
            }

            if (!heal
                && EntityManager.HasComponent<AsteroidState>(target)
                && EntityManager.HasComponent<LocalTransform>(target))
            {
                var rock = EntityManager.GetComponentData<AsteroidState>(target);
                if (!rock.IsAliveForCombat)
                {
                    aim = default;
                    return false;
                }

                float3 pos = EntityManager.GetComponentData<LocalTransform>(target).Position;
                if (!PassesRangeAndCone(from, coneFwd, pos, range, mapW, mapH, coneLock))
                    return false;

                if (coneLock)
                {
                    WriteHitscanAim(pos, from, mapW, mapH, ref aim);
                    return true;
                }

                WriteLeadAim(from, shooterVel, pos, ReadPlanarVelocity(target), bulletSpeed,
                    range, mapW, mapH, ref aim);
                return true;
            }

            aim = default;
            return false;
        }

        /// <summary>
        /// Auto-aim class order. Combat (ships, pads, moon shields) beats asteroids
        /// even when the rock is closer.
        /// </summary>
        enum MegaShipAutoAimClass : byte
        {
            None = 0,
            Asteroid = 1,
            Combat = 2,
        }

        /// <summary>
        /// Closest in-range target from this muzzle. Enemy ships, enemy pads, and enemy
        /// moon shields compete by toroidal distance; (debug) asteroids are only used when
        /// none of those are in range. Two guns may lock the same entity.
        /// Used when a barrel has no live lock (first press, or the last target died / left range).
        /// <paramref name="betterThan"/> is kept so a first search can skip a lower class.
        /// mapW/mapH from <see cref="MapStateSingleton"/>.
        /// </summary>
        bool TryAcquireClosestTarget(
            Entity self,
            TeamId ownerTeam,
            bool heal,
            float3 from,
            float3 shooterVel,
            float bulletSpeed,
            float range,
            float mapW,
            float mapH,
            double moonElapsed,
            MegaShipAutoAimClass betterThan,
            NativeArray<Entity> ships,
            NativeArray<ShipState> shipStates,
            NativeArray<LocalTransform> shipXfs,
            NativeArray<Entity> planets,
            bool debugAsteroids,
            NativeArray<Entity> asteroidEntities,
            NativeArray<AsteroidState> asteroidStates,
            NativeArray<LocalTransform> asteroidXfs,
            out Entity target,
            out float3 aimPoint,
            out float interceptDistance,
            bool coneLock = false,
            float3 coneFwd = default)
        {
            target = Entity.Null;
            aimPoint = default;
            interceptDistance = 0f;
            float3 nowPos = default;
            float3 nowVel = float3.zero;

            if (MegaShipAutoAimClass.Combat > betterThan)
            {
                float best = range;
                if (ships.IsCreated)
                {
                    int shipCount = math.min(ships.Length, math.min(shipStates.Length, shipXfs.Length));
                    for (int i = 0; i < shipCount; i++)
                    {
                        if (ships[i] == self)
                            continue;
                        var other = shipStates[i];
                        if (other.IsDead || other.Team == TeamId.None)
                            continue;
                        if (heal)
                        {
                            if (other.Team != ownerTeam)
                                continue;
                        }
                        else if (other.Team == ownerTeam)
                            continue;

                        // Acquire on current position (who is closest now). Lead is applied after.
                        float3 pos = MegaShipCombatAim.GetAimPoint(EntityManager, ships[i], shipXfs[i]);
                        float d = ToroidalMapEcs.ToroidalDistance(from, pos, mapW, mapH);
                        if (d >= best)
                            continue;
                        if (!PassesRangeAndCone(from, coneFwd, pos, range, mapW, mapH, coneLock))
                            continue;
                        best = d;
                        target = ships[i];
                        nowPos = pos;
                        nowVel = ReadPlanarVelocity(ships[i]);
                    }
                }

                if (heal)
                {
                    if (target == Entity.Null)
                        return false;
                    if (coneLock)
                    {
                        WriteHitscanAimValues(nowPos, from, mapW, mapH, out aimPoint, out interceptDistance);
                        return true;
                    }
                    WriteLeadAimValues(from, shooterVel, nowPos, nowVel, bulletSpeed, range,
                        mapW, mapH, out aimPoint, out interceptDistance);
                    return true;
                }

                if (planets.IsCreated)
                {
                    for (int p = 0; p < planets.Length; p++)
                    {
                        Entity planet = planets[p];
                        if (!TryResolvePlanetAim(
                                planet, ownerTeam, from, best, mapW, mapH, moonElapsed,
                                out float3 planetAim, out float3 planetVel))
                            continue;

                        float d = ToroidalMapEcs.ToroidalDistance(from, planetAim, mapW, mapH);
                        if (d >= best)
                            continue;
                        if (!PassesRangeAndCone(from, coneFwd, planetAim, range, mapW, mapH, coneLock))
                            continue;
                        best = d;
                        target = planet;
                        nowPos = planetAim;
                        nowVel = planetVel;
                    }
                }

                if (target != Entity.Null)
                {
                    if (coneLock)
                    {
                        WriteHitscanAimValues(nowPos, from, mapW, mapH, out aimPoint, out interceptDistance);
                        return true;
                    }
                    WriteLeadAimValues(from, shooterVel, nowPos, nowVel, bulletSpeed, range,
                        mapW, mapH, out aimPoint, out interceptDistance);
                    return true;
                }
            }

            if (heal)
                return false;

            if (debugAsteroids
                && MegaShipAutoAimClass.Asteroid > betterThan
                && asteroidEntities.IsCreated
                && asteroidStates.IsCreated
                && asteroidXfs.IsCreated)
            {
                float best = range;
                int rockCount = math.min(
                    asteroidEntities.Length, math.min(asteroidStates.Length, asteroidXfs.Length));
                for (int a = 0; a < rockCount; a++)
                {
                    var rock = asteroidStates[a];
                    if (!rock.IsAliveForCombat)
                        continue;

                    float3 rockPos = asteroidXfs[a].Position;
                    float d = ToroidalMapEcs.ToroidalDistance(from, rockPos, mapW, mapH);
                    if (d >= best)
                        continue;
                    if (!PassesRangeAndCone(from, coneFwd, rockPos, range, mapW, mapH, coneLock))
                        continue;
                    best = d;
                    target = asteroidEntities[a];
                    nowPos = rockPos;
                    nowVel = ReadPlanarVelocity(asteroidEntities[a]);
                }
            }

            if (target == Entity.Null)
                return false;

            if (coneLock)
            {
                WriteHitscanAimValues(nowPos, from, mapW, mapH, out aimPoint, out interceptDistance);
                return true;
            }

            WriteLeadAimValues(from, shooterVel, nowPos, nowVel, bulletSpeed, range,
                mapW, mapH, out aimPoint, out interceptDistance);
            return true;
        }

        /// <summary>
        /// Best enemy pad or enemy moon shield on one planet — they compete
        /// by distance to the pad or the near side of the shield (body when the
        /// barrier is down). Returns false when the planet is friendly, unowned,
        /// empty, or out of range.
        /// </summary>
        bool TryResolvePlanetAim(
            Entity planet,
            TeamId ownerTeam,
            float3 from,
            float range,
            float mapW,
            float mapH,
            double moonElapsed,
            out float3 aim,
            out float3 aimVel)
        {
            aim = default;
            aimVel = float3.zero;
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
                    aimVel = float3.zero;
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
                        out float3 moonVel))
                {
                    float moonDist = ToroidalMapEcs.ToroidalDistance(from, moonAim, mapW, mapH);
                    if (moonDist < best)
                    {
                        aim = moonAim;
                        // Shield surface rides the orbit ring — lead with the moon's
                        // orbital velocity so shots meet the moving barrier.
                        aimVel = moonVel;
                        found = true;
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Owner Fire, ignoring the press while the hull is in a planet orbit ring
        /// (weapons stay locked there — same gate as Phase B).
        /// </summary>
        bool OwnerWantsFire(Entity mega)
        {
            if (!EntityManager.HasComponent<ShipInput>(mega)
                || !EntityManager.GetComponentData<ShipInput>(mega).Fire.IsSet)
                return false;
            if (EntityManager.HasComponent<ShipOrbitState>(mega)
                && EntityManager.GetComponentData<ShipOrbitState>(mega).InOrbitRing)
                return false;
            return true;
        }

        /// <summary>
        /// True while the MEGA owner holds Shift. Reuses <see cref="ShipInput.Overdrive"/>
        /// as the heading-lock / mouse-aim flag — MEGAs never apply overdrive burst.
        /// </summary>
        bool IsOwnerShiftHeld(Entity mega)
        {
            return EntityManager.HasComponent<ShipInput>(mega)
                && EntityManager.GetComponentData<ShipInput>(mega).Overdrive;
        }

        /// <summary>
        /// Snap each MEGA gun toward the owner's mouse <b>point</b>, not a
        /// shared world direction.
        /// <para>
        /// [TITAN-ORBIT] A single hull-center direction makes every barrel fire
        /// parallel — fine on a tiny fighter, wrong on a wide MEGA. Reconstruct the
        /// cursor as <c>hull + AimPlanarDir × AimDistance</c> (same space as the
        /// unbounded hull), then aim each muzzle along the toroidal shortest path
        /// to that point so streams converge on the cursor.
        /// </para>
        /// </summary>
        void AimUnoccupiedMountsAtMouse(
            Entity mega,
            in LocalTransform xf,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            DynamicBuffer<MegaShipGunnerSlotElement> gunners,
            float mapW,
            float mapH,
            float dt)
        {
            float3 hullForward = math.rotate(xf.Rotation, new float3(0f, 0f, 1f));
            hullForward.y = 0f;
            hullForward = math.normalizesafe(hullForward, new float3(0f, 0f, 1f));

            bool haveInput = EntityManager.HasComponent<ShipInput>(mega);
            var input = haveInput
                ? EntityManager.GetComponentData<ShipInput>(mega)
                : default;
            float3 aimPoint = xf.Position;
            bool haveMousePoint = haveInput
                && MegaShipWeaponAim.TryGetOwnerMouseAimPoint(in xf, in input, out aimPoint);
            if (!haveMousePoint)
                aimPoint = xf.Position;

            int mountCount = mounts.Length;
            for (int m = 0; m < mountCount; m++)
            {
                var mount = mounts[m];
                float3 desired = hullForward;
                float targetDist = 0f;
                if (haveInput
                    && MegaShipWeaponAim.TryGetMuzzleDirToMousePoint(
                        in xf, in mount, in input, mapW, mapH, out float3 toCursor))
                {
                    desired = toCursor;
                    if (haveMousePoint)
                    {
                        float3 muzzle = ResolveMuzzle(xf, mount);
                        targetDist = math.length(
                            ToroidalMapEcs.ShortestOffsetXZ(muzzle, aimPoint, mapW, mapH));
                    }
                }

                MegaShipWeaponAim.RotateMountTowardWorldDir(in xf, ref mount, desired, dt);
                mounts[m] = mount;
                float3 ghostAim = haveMousePoint ? aimPoint : xf.Position;
                MegaShipWeaponAim.WriteGhostedYaw(gunners, m, in mount, ghostAim, targetDist, desired);
            }
        }

        /// <summary>
        /// Planar world velocity from <see cref="ShipKinematics"/>, or Physics when that
        /// ghost is missing (asteroids). Y is forced to 0.
        /// </summary>
        float3 ReadPlanarVelocity(Entity entity)
        {
            if (EntityManager.HasComponent<ShipKinematics>(entity))
            {
                float3 vel = EntityManager.GetComponentData<ShipKinematics>(entity).Velocity;
                vel.y = 0f;
                return vel;
            }

            if (EntityManager.HasComponent<PhysicsVelocity>(entity))
            {
                float3 vel = EntityManager.GetComponentData<PhysicsVelocity>(entity).Linear;
                vel.y = 0f;
                return vel;
            }

            return float3.zero;
        }

        /// <summary>
        /// Muzzle-relative bullet speed after the same bank modifiers Phase B applies.
        /// Lead must use this value or the intercept systematically under- or over-leads.
        /// Titan missiles use the mount's catalog Weapon Missile <c>bulletSpeed</c>
        /// (same as <see cref="RocketHomingFire"/>).
        /// </summary>
        static float ResolveLeadBulletSpeed(
            in ShipWeaponConfig weapon,
            in ShipWeaponMountElement mount,
            int bankIndex,
            int shipLevel)
        {
            if (RocketHomingFire.IsRocketBank(bankIndex))
            {
                return math.max(
                    PlanetaryDefenseAimMath.MinBulletSpeed,
                    RocketHomingFire.ResolveFlightSpeed(shipLevel, mount.BulletSpeed));
            }

            float speed = math.max(
                PlanetaryDefenseAimMath.MinBulletSpeed,
                BulletShotMath.ResolveMuzzleSpeed(mount.BulletSpeed, weapon.BulletSpeed));
            float damage = 1f;
            float maxDistance = 1f;
            float lifetime = 0f;
            float fireRate = 1f;
            BulletBankCombatLogic.ApplyFireModifiers(
                bankIndex, ref damage, ref speed, ref maxDistance, ref lifetime, ref fireRate);
            return math.max(PlanetaryDefenseAimMath.MinBulletSpeed, speed);
        }

        /// <summary>
        /// Range plus optional 33° barrel cone. Projectile guns ignore the cone.
        /// </summary>
        static bool PassesRangeAndCone(
            float3 from,
            float3 coneFwd,
            float3 targetPos,
            float range,
            float mapW,
            float mapH,
            bool coneLock)
        {
            if (coneLock)
                return CannonLaserMath.IsInRangeAndCone(
                    from, coneFwd, targetPos, range, mapW, mapH, out _);

            return ToroidalMapEcs.ToroidalDistance(from, targetPos, mapW, mapH) <= range;
        }

        /// <summary>Hitscan lock — current point, no projectile lead.</summary>
        static void WriteHitscanAim(
            float3 targetPos,
            float3 muzzle,
            float mapW,
            float mapH,
            ref MegaShipAutoAimSlotElement aim)
        {
            WriteHitscanAimValues(targetPos, muzzle, mapW, mapH, out float3 aimPoint, out float dist);
            aim.AimPoint = aimPoint;
            aim.InterceptDistance = dist;
        }

        /// <summary>Current target point and toroidal muzzle distance.</summary>
        static void WriteHitscanAimValues(
            float3 targetPos,
            float3 muzzle,
            float mapW,
            float mapH,
            out float3 aimPoint,
            out float interceptDistance)
        {
            aimPoint = targetPos;
            interceptDistance = ToroidalMapEcs.ToroidalDistance(muzzle, targetPos, mapW, mapH);
        }

        /// <summary>Writes lead intercept onto a sticky slot (keeps <see cref="MegaShipAutoAimSlotElement.Target"/>).</summary>
        static void WriteLeadAim(
            float3 muzzle,
            float3 shooterVel,
            float3 targetPos,
            float3 targetVel,
            float bulletSpeed,
            float engageRange,
            float mapW,
            float mapH,
            ref MegaShipAutoAimSlotElement aim)
        {
            WriteLeadAimValues(
                muzzle, shooterVel, targetPos, targetVel, bulletSpeed, engageRange,
                mapW, mapH, out float3 aimPoint, out float interceptDistance);
            aim.AimPoint = aimPoint;
            aim.InterceptDistance = interceptDistance;
        }

        /// <summary>
        /// Lead intercept for a known current position / velocity. Falls back to the
        /// current point when the quadratic has no solution (coincident muzzle).
        /// </summary>
        static void WriteLeadAimValues(
            float3 muzzle,
            float3 shooterVel,
            float3 targetPos,
            float3 targetVel,
            float bulletSpeed,
            float engageRange,
            float mapW,
            float mapH,
            out float3 aimPoint,
            out float interceptDistance)
        {
            // Hitscan weapons (cannon lasers pass 0). A 0-speed lead solve is a
            // hull-rendezvous point — while the Titan moves that sits well off
            // the target and the 33° burn cone fails every tick.
            if (bulletSpeed <= 0.01f)
            {
                WriteHitscanAimValues(targetPos, muzzle, mapW, mapH, out aimPoint, out interceptDistance);
                return;
            }

            if (MegaShipLeadAim.TryComputeFireSolution(
                    muzzle, shooterVel, targetPos, targetVel, bulletSpeed,
                    mapW, mapH, engageRange,
                    out _, out _, out interceptDistance, out aimPoint))
                return;

            aimPoint = targetPos;
            interceptDistance = 0f;
        }
    }
}
