using TitanOrbit;
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
    /// Server: unoccupied MEGA mounts auto-aim like planetary turrets, but only fire when
    /// the MEGA owner's <see cref="ShipInput.Fire"/> is held. Occupied mounts are skipped
    /// (the gunner's Fire drives those via <see cref="MegaShipPlayerCombatSystem"/>).
    /// Damage mode aims at the nearest enemy ship, planetary turret, or enemy moon.
    /// Heal mode aims at the nearest friendly ship. Asteroids are targeted only when
    /// <see cref="TitanOrbitDebugFlags.MegaShipsAutoFireAsteroids"/> is on (Editor / MPPM host).
    /// <para>
    /// Every unoccupied weapon yaws toward its own target. Shots follow the current barrel
    /// (even if traverse is still catching up). Damage and energy cost are that mount's
    /// <c>FirePower</c> only — never the hull firepower sum.
    /// </para>
    /// Map size comes from <see cref="MapStateSingleton"/>. Distances use
    /// <see cref="ToroidalMapEcs.ToroidalDistance"/>.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial class MegaShipAutoFireSystem : SystemBase
    {
        readonly System.Collections.Generic.Dictionary<int, float> _nextMountFireTime =
            new System.Collections.Generic.Dictionary<int, float>(64);

        EntityQuery _megaQuery;
        EntityQuery _shipQuery;
        EntityQuery _planetQuery;
        EntityQuery _asteroidQuery;

        /// <summary>Cache queries used every tick.</summary>
        protected override void OnCreate()
        {
            RequireForUpdate<ActiveBulletsTag>();
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

        /// <summary>Auto-aim every unoccupied MEGA mount; fire those mounts only while the owner holds Fire.</summary>
        protected override void OnUpdate()
        {
            if (!SystemAPI.TryGetSingletonEntity<ActiveBulletsTag>(out var bulletEntity))
                return;
            if (!EntityManager.HasBuffer<BulletElement>(bulletEntity) ||
                !EntityManager.HasBuffer<BulletSpawnEventElement>(bulletEntity))
                return;
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

            float mapW = map.MapWidth;
            float mapH = map.MapHeight;
            float now = (float)SystemAPI.Time.ElapsedTime;
            float dt = SystemAPI.Time.DeltaTime;
            var bullets = EntityManager.GetBuffer<BulletElement>(bulletEntity);
            var spawnEvents = EntityManager.GetBuffer<BulletSpawnEventElement>(bulletEntity);

            using var ships = _shipQuery.ToEntityArray(Allocator.Temp);
            using var shipStates = _shipQuery.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var shipXfs = _shipQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            using var planets = _planetQuery.ToEntityArray(Allocator.Temp);

            // --- Debug: asteroid pool only when the Inspector toggle is on ---
            // [TITAN-ORBIT] Dedicated server keeps MegaShipsAutoFireAsteroids false. Heal mode
            // never mines rocks. mapW/mapH come from MapStateSingleton (toroidal acquire).
            bool debugAsteroids = TitanOrbitDebugFlags.MegaShipsAutoFireAsteroids;
            NativeArray<AsteroidState> asteroidStates = default;
            NativeArray<LocalTransform> asteroidXfs = default;
            if (debugAsteroids)
            {
                asteroidStates = _asteroidQuery.ToComponentDataArray<AsteroidState>(Allocator.Temp);
                asteroidXfs = _asteroidQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            }

            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double moonElapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : (float)SystemAPI.Time.ElapsedTime;

            var ecb = new EntityCommandBuffer(Allocator.Temp);
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

                bool heal = false;
                int bankIndex = 0;
                if (EntityManager.HasComponent<ShipLoadoutState>(mega))
                {
                    var loadout = EntityManager.GetComponentData<ShipLoadoutState>(mega);
                    heal = loadout.HealingBulletsActive;
                    bankIndex = BulletBankFireResolve.ResolveFireBankIndex(loadout);
                }

                var weapon = EntityManager.HasComponent<ShipWeaponConfig>(mega)
                    ? EntityManager.GetComponentData<ShipWeaponConfig>(mega)
                    : default;

                int ownerNet = 0;
                if (EntityManager.HasComponent<GhostOwner>(mega))
                    ownerNet = EntityManager.GetComponentData<GhostOwner>(mega).NetworkId;

                bool ownerWantsFire = EntityManager.HasComponent<ShipInput>(mega)
                    && EntityManager.GetComponentData<ShipInput>(mega).Fire.IsSet;
                if (ownerWantsFire
                    && EntityManager.HasComponent<ShipOrbitState>(mega)
                    && EntityManager.GetComponentData<ShipOrbitState>(mega).InOrbitRing)
                    ownerWantsFire = false;

                float hullScanRange = ResolveHullScanRange(mounts);
                if (!AnyHostileInRange(
                        xf.Position, ship.Team, mega, heal, hullScanRange, mapW, mapH, moonElapsed,
                        ships, shipStates, shipXfs, planets,
                        debugAsteroids, asteroidStates, asteroidXfs))
                    continue;

                int mountCount = mounts.Length;
                for (int m = 0; m < mountCount; m++)
                {
                    if (gunners.IsCreated && m < gunners.Length && gunners[m].OccupiedByNetworkId != 0)
                        continue;

                    var mount = mounts[m];
                    float3 muzzle = MegaShipGunnerLogic.GetMountWorldPosition(xf, mount);
                    float range = ResolveMountRange(in mount, in weapon);
                    if (!TryFindTarget(
                            muzzle, ship.Team, mega, heal, range, mapW, mapH, moonElapsed,
                            ships, shipStates, shipXfs, planets,
                            debugAsteroids, asteroidStates, asteroidXfs,
                            out float3 targetPos))
                    {
                        MegaShipWeaponAim.WriteGhostedYaw(gunners, m, mount, 0f);
                        continue;
                    }

                    // --- Toroidal aim (never wrap the hull; never use raw target - muzzle) ---
                    float3 offset = ToroidalMapEcs.ShortestOffsetXZ(muzzle, targetPos, mapW, mapH);
                    offset.y = 0f;
                    float dist = math.length(offset);
                    if (dist < 0.05f)
                    {
                        MegaShipWeaponAim.WriteGhostedYaw(gunners, m, mount, 0f);
                        continue;
                    }

                    float3 desiredAim = offset / dist;
                    MegaShipWeaponAim.RotateMountTowardWorldDir(xf, ref mount, desiredAim, dt);
                    mounts[m] = mount;
                    MegaShipWeaponAim.WriteGhostedYaw(gunners, m, mount, dist);

                    // --- Owner Fire only: occupied mounts are skipped above ---
                    if (!ownerWantsFire)
                        continue;

                    // --- Turret fire: along the barrel, even if traverse is still catching up ---
                    float fireRate = math.max(0.25f, mount.FireRate > 0.01f ? mount.FireRate : weapon.FireRate);
                    int cooldownKey = (mega.Index << 16) ^ (m & 0xFFFF);
                    if (_nextMountFireTime.TryGetValue(cooldownKey, out float next) && now < next)
                        continue;

                    // Per-mount firepower only — 0 means unarmed (do not fall back to hull sum).
                    float damage = mount.FirePower;
                    if (damage <= 0.01f)
                        continue;

                    var liveShip = EntityManager.GetComponentData<ShipState>(mega);
                    if (liveShip.CurrentEnergy < damage)
                        continue;

                    liveShip.CurrentEnergy = math.max(0f, liveShip.CurrentEnergy - damage);
                    EntityManager.SetComponentData(mega, liveShip);

                    float3 aim = MegaShipWeaponAim.GetBarrelForward(xf, mount);
                    float bulletSpeed = math.max(4f, weapon.BulletSpeed > 0.1f ? weapon.BulletSpeed : 14f);
                    uint sequence = BulletVfxBridge.NextSequence();
                    var spawn = new BulletElement
                    {
                        Position = muzzle,
                        Velocity = aim * bulletSpeed,
                        MaxDistance = range,
                        Lifetime = 0f,
                        Damage = damage,
                        OwnerNetworkId = ownerNet,
                        OwnerTeam = (byte)ship.Team,
                        Sequence = sequence,
                        BankIndex = math.max(0, bankIndex),
                        ScaleMultiplier = 1.2f,
                        DamageFilter = BulletDamageFilter.Everything,
                    };

                    spawnEvents.Add(new BulletSpawnEventElement
                    {
                        SpawnPosition = spawn.Position,
                        Velocity = spawn.Velocity,
                        Lifetime = spawn.Lifetime,
                        MaxDistance = spawn.MaxDistance,
                        Damage = spawn.Damage,
                        OwnerTeam = spawn.OwnerTeam,
                        Sequence = spawn.Sequence,
                        BankIndex = spawn.BankIndex,
                        ScaleMultiplier = spawn.ScaleMultiplier,
                    });
                    BulletNetNotify.SendSpawn(ref ecb, spawn, mountIndex: (byte)m);
                    bullets.Add(spawn);
                    _nextMountFireTime[cooldownKey] = now + (1f / fireRate);
                }
            }

            ecb.Playback(EntityManager);
            ecb.Dispose();
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

        /// <summary>Widest gun range on this hull plus a small pad so the cheap hull reject stays correct.</summary>
        static float ResolveHullScanRange(DynamicBuffer<ShipWeaponMountElement> mounts)
        {
            float maxRange = MegaShipCatalog.DefaultBulletAcquireRange;
            for (int i = 0; i < mounts.Length; i++)
            {
                float r = mounts[i].BulletRange;
                if (r > maxRange)
                    maxRange = r;
            }

            return maxRange + 8f;
        }

        /// <summary>True when any valid auto-fire target is inside the hull scan radius.</summary>
        bool AnyHostileInRange(
            float3 hullPos,
            TeamId ownerTeam,
            Entity self,
            bool heal,
            float range,
            float mapW,
            float mapH,
            double moonElapsed,
            NativeArray<Entity> ships,
            NativeArray<ShipState> shipStates,
            NativeArray<LocalTransform> shipXfs,
            NativeArray<Entity> planets,
            bool debugAsteroids,
            NativeArray<AsteroidState> asteroidStates,
            NativeArray<LocalTransform> asteroidXfs)
        {
            return TryFindTarget(
                hullPos, ownerTeam, self, heal, range, mapW, mapH, moonElapsed,
                ships, shipStates, shipXfs, planets,
                debugAsteroids, asteroidStates, asteroidXfs, out _);
        }

        /// <summary>
        /// Nearest living ship (heal = friendly, damage = enemy). Damage mode also considers
        /// enemy planetary turret pads and enemy gem moons. Distances are toroidal.
        /// Debug <see cref="TitanOrbitDebugFlags.MegaShipsAutoFireAsteroids"/> also considers
        /// living asteroids (damage mode only). mapW/mapH from <see cref="MapStateSingleton"/>.
        /// </summary>
        bool TryFindTarget(
            float3 muzzle,
            TeamId ownerTeam,
            Entity self,
            bool heal,
            float range,
            float mapW,
            float mapH,
            double moonElapsed,
            NativeArray<Entity> ships,
            NativeArray<ShipState> shipStates,
            NativeArray<LocalTransform> shipXfs,
            NativeArray<Entity> planets,
            bool debugAsteroids,
            NativeArray<AsteroidState> asteroidStates,
            NativeArray<LocalTransform> asteroidXfs,
            out float3 targetPos)
        {
            targetPos = default;
            float best = range;
            bool found = false;

            for (int i = 0; i < ships.Length; i++)
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

                float3 aim = MegaShipCombatAim.GetAimPoint(EntityManager, ships[i], shipXfs[i]);
                float d = ToroidalMapEcs.ToroidalDistance(muzzle, aim, mapW, mapH);
                if (d >= best)
                    continue;
                best = d;
                targetPos = aim;
                found = true;
            }

            if (heal)
                return found;

            for (int p = 0; p < planets.Length; p++)
            {
                Entity planet = planets[p];
                var planetState = EntityManager.GetComponentData<PlanetState>(planet);
                var planetXf = EntityManager.GetComponentData<LocalTransform>(planet);
                if (planetState.Ownership == TeamId.None || planetState.Ownership == ownerTeam)
                    continue;

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
                        float d = ToroidalMapEcs.ToroidalDistance(muzzle, pad, mapW, mapH);
                        if (d >= best)
                            continue;
                        best = d;
                        targetPos = pad;
                        found = true;
                    }
                }

                if (!EntityManager.HasComponent<PlanetGemMoonState>(planet))
                    continue;
                if (PlanetGemMoonCombatLogic.IsTeamFriendlyToMoon(planetState.Ownership, ownerTeam))
                    continue;

                float3 moonPos = PlanetOrbitMath.GetMoonWorldPosition(
                    planetXf.Position,
                    math.max(0.25f, planetXf.Scale),
                    planetState.PlanetLevel,
                    planetState.PlanetId,
                    moonElapsed,
                    planetState.IsHomePlanet);
                float moonDist = ToroidalMapEcs.ToroidalDistance(muzzle, moonPos, mapW, mapH);
                if (moonDist >= best)
                    continue;
                best = moonDist;
                targetPos = moonPos;
                found = true;
            }

            if (debugAsteroids && asteroidStates.IsCreated && asteroidXfs.IsCreated)
            {
                int rockCount = math.min(asteroidStates.Length, asteroidXfs.Length);
                for (int a = 0; a < rockCount; a++)
                {
                    var rock = asteroidStates[a];
                    if (rock.IsDestroyed || rock.Health <= 0.01f)
                        continue;

                    float3 rockPos = asteroidXfs[a].Position;
                    float d = ToroidalMapEcs.ToroidalDistance(muzzle, rockPos, mapW, mapH);
                    if (d >= best)
                        continue;
                    best = d;
                    targetPos = rockPos;
                    found = true;
                }
            }

            return found;
        }
    }
}
