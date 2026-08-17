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

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: unoccupied MEGA mounts auto-aim like planetary turrets, but only fire when
    /// the MEGA owner's <see cref="ShipInput.Fire"/> is held. Occupied mounts are skipped
    /// (the gunner's Fire drives those in <see cref="BulletSimulationSystem"/> Phase B)
    /// unless the owner holds Shift — then unoccupied auto-guns aim at the mouse
    /// (occupied gunner mounts keep their own aim).
    /// Damage mode aims at the nearest enemy ship, planetary turret, or enemy moon.
    /// Heal mode aims at the nearest friendly ship. Asteroids are targeted only when
    /// <see cref="TitanOrbitDebugFlags.MegaShipsAutoFireAsteroids"/> is on (Editor / MPPM host).
    /// <para>
    /// Each unoccupied gun searches from its own muzzle for the closest in-range target
    /// when Fire is pressed. Locks stay until that target dies, leaves that gun's range,
    /// or the owner releases Fire (release clears locks so the next press re-acquires).
    /// If a gun finds nothing, it fires along hull forward until Fire is released.
    /// Aim points are lead intercepts from <see cref="MegaShipLeadAim"/> — target velocity
    /// minus this hull's velocity — so inherited <c>shipVel</c> on the bullet does not
    /// undershoot. Unoccupied turrets slew toward that lock (or hull forward) before
    /// Phase B so shots leave along the barrel — the same ray regular ships use.
    /// <see cref="BulletSimulationSystem"/> Phase B fires ready mounts along
    /// <see cref="ShipWeaponPose"/> barrel forward.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] MEGAs have no overdrive. <see cref="ShipInput.Overdrive"/> (Shift)
    /// locks hull heading and points each unoccupied auto-gun at the mouse
    /// world point (<see cref="ShipInput.AimPlanarDir"/> × <see cref="ShipInput.AimDistance"/>).
    /// Occupied mounts stay with their gunner.
    /// Fire is still required to spend energy.
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

        /// <summary>Cache queries used every tick.</summary>
        protected override void OnCreate()
        {
            RequireForUpdate<MapStateSingleton>();
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

                // Shift = heading lock + mouse-aim unoccupied auto-guns (no overdrive on MEGAs).
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
                float leadBulletSpeed = ResolveLeadBulletSpeed(in weapon, bankIndex);

                bool ownerShift = IsOwnerShiftHeld(mega);
                bool ownerWantsFire = OwnerWantsFire(mega);

                // --- Shift: unoccupied auto-guns look at the mouse ---
                // [TITAN-ORBIT] Owner Shift is the MEGA "strafe / lock heading" mode.
                // Occupied mounts are left for MegaShipPlayerCombatSystem + the gunner.
                // Auto-locks stay cleared so releasing Shift re-acquires from scratch.
                if (ownerShift)
                {
                    ClearAimSlots(mega);
                    AimUnoccupiedMountsAtMouse(mega, xf, mounts, gunners, mapW, mapH, dt);
                    continue;
                }

                if (!ownerWantsFire)
                    continue;

                if (!EntityManager.HasBuffer<MegaShipAutoAimSlotElement>(mega))
                    continue;

                var aims = EntityManager.GetBuffer<MegaShipAutoAimSlotElement>(mega);
                int mountCount = mounts.Length;
                ResizeAimSlots(aims, mountCount);

                int emptySlots = 0;
                for (int m = 0; m < mountCount; m++)
                {
                    if (gunners.IsCreated && m < gunners.Length && gunners[m].OccupiedByNetworkId != 0)
                    {
                        aims[m] = default;
                        continue;
                    }

                    var slot = aims[m];
                    if (slot.Target == mega)
                        continue;

                    float3 muzzle = ResolveMuzzle(xf, mounts[m]);
                    float mountRange = ResolveMountRange(mounts[m], in weapon) + 8f;
                    if (TryKeepStickyTarget(
                        mega, ship.Team, heal, muzzle, shooterVel, leadBulletSpeed, mountRange,
                        mapW, mapH, moonElapsed, ref slot))
                    {
                        aims[m] = slot;
                        continue;
                    }

                    aims[m] = default;
                    emptySlots++;
                }

                if (emptySlots > 0)
                {
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

                    if (!heal && debugAsteroids && !asteroidsLoaded)
                    {
                        asteroidEntities = _asteroidQuery.ToEntityArray(Allocator.Temp);
                        asteroidStates = _asteroidQuery.ToComponentDataArray<AsteroidState>(Allocator.Temp);
                        asteroidXfs = _asteroidQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                        asteroidsLoaded = true;
                    }

                    for (int m = 0; m < mountCount; m++)
                    {
                        if (gunners.IsCreated && m < gunners.Length && gunners[m].OccupiedByNetworkId != 0)
                            continue;
                        if (aims[m].Target != Entity.Null)
                            continue;

                        float3 muzzle = ResolveMuzzle(xf, mounts[m]);
                        float mountRange = ResolveMountRange(mounts[m], in weapon) + 8f;
                        if (TryAcquireClosestTarget(
                            mega, ship.Team, heal, muzzle, shooterVel, leadBulletSpeed, mountRange,
                            mapW, mapH, moonElapsed,
                            ships, shipStates, shipXfs, planets,
                            debugAsteroids, asteroidEntities, asteroidStates, asteroidXfs,
                            out Entity target, out float3 aimPoint, out float interceptDistance))
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
                            aims[m] = new MegaShipAutoAimSlotElement
                            {
                                Target = mega,
                                AimPoint = default,
                            };
                        }
                    }
                }

                // Aim the gun, then Phase B fires along the barrel (regular-ship ray).
                RotateUnoccupiedMountsTowardAim(mega, xf, mounts, aims, gunners, mapW, mapH, dt);
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
        /// Slews each unoccupied mount toward its sticky lock (toroidal muzzle→AimPoint)
        /// or hull forward when the slot is parked. Occupied mounts are slewed by
        /// <see cref="MegaShipPlayerCombatSystem"/>.
        /// </summary>
        static void RotateUnoccupiedMountsTowardAim(
            Entity mega,
            in LocalTransform xf,
            DynamicBuffer<ShipWeaponMountElement> mounts,
            DynamicBuffer<MegaShipAutoAimSlotElement> aims,
            DynamicBuffer<MegaShipGunnerSlotElement> gunners,
            float mapW,
            float mapH,
            float dt)
        {
            float3 hullForward = math.rotate(xf.Rotation, new float3(0f, 0f, 1f));
            hullForward.y = 0f;
            if (math.lengthsq(hullForward) < 0.0001f)
                hullForward = new float3(0f, 0f, 1f);
            else
                hullForward = math.normalize(hullForward);

            int mountCount = mounts.Length;
            for (int m = 0; m < mountCount; m++)
            {
                if (gunners.IsCreated && m < gunners.Length && gunners[m].OccupiedByNetworkId != 0)
                    continue;
                if (m >= aims.Length || aims[m].Target == Entity.Null)
                    continue;

                var mount = mounts[m];
                float3 desired = hullForward;
                float targetDist = 0f;
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
                }

                MegaShipWeaponAim.RotateMountTowardWorldDir(in xf, ref mount, desired, dt);
                mounts[m] = mount;
                MegaShipWeaponAim.WriteGhostedYaw(gunners, m, in mount, targetDist);
            }
        }

        static void ResizeAimSlots(DynamicBuffer<MegaShipAutoAimSlotElement> aims, int mountCount)
        {
            while (aims.Length < mountCount)
                aims.Add(default);
            while (aims.Length > mountCount)
                aims.RemoveAt(aims.Length - 1);
        }

        /// <summary>Drop all sticky locks. Next Fire press runs a fresh per-muzzle search.</summary>
        void ClearAimSlots(Entity mega)
        {
            if (!EntityManager.HasBuffer<MegaShipAutoAimSlotElement>(mega))
                return;

            var aims = EntityManager.GetBuffer<MegaShipAutoAimSlotElement>(mega);
            for (int i = 0; i < aims.Length; i++)
                aims[i] = default;
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
            ref MegaShipAutoAimSlotElement aim)
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
                if (ToroidalMapEcs.ToroidalDistance(from, pos, mapW, mapH) > range)
                {
                    aim = default;
                    return false;
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

                WriteLeadAim(from, shooterVel, planetAim, planetVel, bulletSpeed,
                    range, mapW, mapH, ref aim);
                return true;
            }

            if (!heal
                && EntityManager.HasComponent<AsteroidState>(target)
                && EntityManager.HasComponent<LocalTransform>(target))
            {
                var rock = EntityManager.GetComponentData<AsteroidState>(target);
                if (rock.IsDestroyed || rock.Health <= 0.01f)
                {
                    aim = default;
                    return false;
                }

                float3 pos = EntityManager.GetComponentData<LocalTransform>(target).Position;
                if (ToroidalMapEcs.ToroidalDistance(from, pos, mapW, mapH) > range)
                {
                    aim = default;
                    return false;
                }

                WriteLeadAim(from, shooterVel, pos, ReadPlanarVelocity(target), bulletSpeed,
                    range, mapW, mapH, ref aim);
                return true;
            }

            aim = default;
            return false;
        }

        /// <summary>
        /// Closest in-range target from this muzzle. Ships, hostile pads/moons, and (debug)
        /// asteroids compete by toroidal distance — two guns may lock the same entity.
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
            out float interceptDistance)
        {
            target = Entity.Null;
            aimPoint = default;
            interceptDistance = 0f;
            float best = range;
            float3 nowPos = default;
            float3 nowVel = float3.zero;

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
                best = d;
                target = ships[i];
                nowPos = pos;
                nowVel = ReadPlanarVelocity(ships[i]);
            }

            if (heal)
            {
                if (target == Entity.Null)
                    return false;
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
                    best = d;
                    target = planet;
                    nowPos = planetAim;
                    nowVel = planetVel;
                }
            }

            if (debugAsteroids && asteroidEntities.IsCreated && asteroidStates.IsCreated && asteroidXfs.IsCreated)
            {
                int rockCount = math.min(
                    asteroidEntities.Length, math.min(asteroidStates.Length, asteroidXfs.Length));
                for (int a = 0; a < rockCount; a++)
                {
                    var rock = asteroidStates[a];
                    if (rock.IsDestroyed || rock.Health <= 0.01f)
                        continue;

                    float3 rockPos = asteroidXfs[a].Position;
                    float d = ToroidalMapEcs.ToroidalDistance(from, rockPos, mapW, mapH);
                    if (d >= best)
                        continue;
                    best = d;
                    target = asteroidEntities[a];
                    nowPos = rockPos;
                    nowVel = ReadPlanarVelocity(asteroidEntities[a]);
                }
            }

            if (target == Entity.Null)
                return false;

            WriteLeadAimValues(from, shooterVel, nowPos, nowVel, bulletSpeed, range,
                mapW, mapH, out aimPoint, out interceptDistance);
            return true;
        }

        /// <summary>
        /// Best hostile pad or moon on one planet, or the planet hull if those are farther.
        /// Returns false when the planet is friendly, empty, or out of range.
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
                    aimVel = float3.zero;
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
                    // Moons ride the orbit ring — lead with the same orbital velocity
                    // the dock motor uses so shots intercept a moving moon.
                    aimVel = PlanetOrbitMath.GetMoonOrbitalVelocity(
                        math.max(0.25f, planetXf.Scale),
                        planetState.PlanetLevel,
                        planetState.PlanetId,
                        moonElapsed);
                    found = true;
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
        /// Snap each unoccupied auto-gun toward the owner's mouse <b>point</b>, not a
        /// shared world direction. Occupied gunner mounts are skipped.
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

            float3 aimPoint = xf.Position + hullForward * 100f;
            bool haveMousePoint = false;
            if (EntityManager.HasComponent<ShipInput>(mega))
            {
                var input = EntityManager.GetComponentData<ShipInput>(mega);
                float2 aim2 = input.AimPlanarDir;
                float3 mouseDir = new float3(aim2.x, 0f, aim2.y);
                float dist = input.AimDistance;
                if (math.lengthsq(mouseDir) >= 0.01f && dist > 0.05f)
                {
                    mouseDir = math.normalize(mouseDir);
                    aimPoint = xf.Position + mouseDir * dist;
                    aimPoint.y = xf.Position.y;
                    haveMousePoint = true;
                }
                else if (math.lengthsq(mouseDir) >= 0.01f)
                {
                    // Distance missing (old client) — still better than hull forward,
                    // but barrels stay parallel.
                    hullForward = math.normalize(mouseDir);
                }
            }

            int mountCount = mounts.Length;
            for (int m = 0; m < mountCount; m++)
            {
                if (gunners.IsCreated && m < gunners.Length && gunners[m].OccupiedByNetworkId != 0)
                    continue;

                var mount = mounts[m];
                float3 desired = hullForward;
                float targetDist = 0f;
                if (haveMousePoint)
                {
                    float3 muzzle = ResolveMuzzle(xf, mount);
                    float3 offset = ToroidalMapEcs.ShortestOffsetXZ(muzzle, aimPoint, mapW, mapH);
                    offset.y = 0f;
                    float dist = math.length(offset);
                    if (dist >= 0.05f)
                    {
                        desired = offset / dist;
                        targetDist = dist;
                    }
                }

                MegaShipWeaponAim.RotateMountTowardWorldDir(in xf, ref mount, desired, dt);
                mounts[m] = mount;
                MegaShipWeaponAim.WriteGhostedYaw(gunners, m, in mount, targetDist);
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
        /// </summary>
        static float ResolveLeadBulletSpeed(in ShipWeaponConfig weapon, int bankIndex)
        {
            float speed = math.max(PlanetaryDefenseAimMath.MinBulletSpeed, weapon.BulletSpeed);
            float damage = 1f;
            float maxDistance = 1f;
            float lifetime = 0f;
            float fireRate = 1f;
            BulletBankCombatLogic.ApplyFireModifiers(
                bankIndex, ref damage, ref speed, ref maxDistance, ref lifetime, ref fireRate);
            return math.max(PlanetaryDefenseAimMath.MinBulletSpeed, speed);
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
