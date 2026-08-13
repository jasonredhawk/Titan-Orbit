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
using Random = Unity.Mathematics.Random;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Tunable constants for gem mining, pickup, deposit, and asteroid hit radii.
    /// Shared by <see cref="MiningSystem"/>, <see cref="GemPickupSystem"/>, and bullet collision.
    /// </summary>
    public static class GemEconomyConstants
    {
        /// <summary>World units — ship must be within this toroidal distance to mine an asteroid.</summary>
        public const float MiningRange = 6f;

        /// <summary>Gem value mined per second while in range.</summary>
        public const float MiningRate = 5f;

        /// <summary>
        /// Hull-center pickup radius when ship has no wing tractor buffers.
        /// Runtime override: <see cref="TractorBeamSettings.HullPickupRange"/> (asset default 2.5).
        /// </summary>
        public const float GemPickupRange = 2.5f;

        /// <summary>
        /// Collect gems near the wing tip when tractor-pulled or when a tip flies over a gem.
        /// Effective radius = WingCollectRadius + gem size pad (see TractorBeamSettings).
        /// Runtime override: <see cref="TractorBeamSettings.WingCollectRadius"/> /
        /// <see cref="TractorBeamSettings.GemSizeCollectFactor"/>.
        /// </summary>
        public const float GemWingCollectRadius = 0.65f;

        /// <summary>Legacy planet interaction radius (deposit uses moon dock instead).</summary>
        public const float PlanetInteractionRange = 20f;

        /// <summary>Multiplier on moon dock zone relative to moon visual size.</summary>
        public const float MoonDockRangeMultiplier = 2.2f;

        /// <summary>Landing progress threshold — 1.0 means fully docked on the gem moon.</summary>
        public const float MoonLandingCompleteThreshold = 0.999f;

        /// <summary>Stillness time required before moon landing progress begins or resumes.</summary>
        public const float MoonLandingApproachDelaySeconds = 0.5f;

        /// <summary>
        /// Historical gems/sec factor (<c>ShipLevel × 2</c>). Kept so docs and design notes still
        /// match the discrete beat math: one full chunk every <see cref="GemDepositBeatIntervalSeconds"/>.
        /// </summary>
        public const float DepositRatePerShipLevel = 2f;

        /// <summary>
        /// Server + client deposit metronome period in seconds.
        /// [TITAN-ORBIT] Each beat moves one gem-value chunk (= <c>ShipLevel</c>, or the leftover
        /// cargo if smaller). Average rate stays <c>ShipLevel × DepositRatePerShipLevel</c> gems/sec.
        /// </summary>
        public const float GemDepositBeatIntervalSeconds = 0.5f;

        /// <summary>
        /// Gems transferred on one deposit beat for this ship.
        /// Full loads use <paramref name="shipLevel"/>; the last leftover uses remaining cargo so
        /// pitch / floating counts / Bank UI all show the true amount — never a fake full chunk.
        /// </summary>
        /// <param name="shipLevel">Ship level (gem-value of one full deposit load).</param>
        /// <param name="currentGems">Cargo remaining on the ship right now.</param>
        /// <returns>Chunk size to move this beat (0 when empty).</returns>
        public static float GetDepositChunkAmount(float shipLevel, float currentGems)
        {
            // --- One metronome load ---
            // [TITAN-ORBIT] Level 5 with 50 cargo → 5. Level 5 with 3 left → 3 (correct leftover pitch).
            float fullChunk = math.max(1f, shipLevel);
            return math.min(fullChunk, math.max(0f, currentGems));
        }

        /// <summary>
        /// Max toroidal distance (world units) at which a client still hears another ship's
        /// gem-deposit metronome. Beyond this the beat is silent — keeps distant moons quiet.
        /// </summary>
        public const float GemDepositHearRange = 48f;

        /// <summary>
        /// Within this toroidal distance deposit beats play at full volume; between this and
        /// <see cref="GemDepositHearRange"/> volume falls off linearly.
        /// </summary>
        public const float GemDepositHearFullVolumeRange = 18f;

        /// <summary>Smallest gem chunk worth spawning as an entity.</summary>
        public const float MinGemSpawnValue = 0.25f;

        /// <summary>
        /// Fallback explosion speed when <see cref="GemExplosionSettings"/> is missing.
        /// Prefer the ScriptableObject (Assets/Resources/GemExplosionSettings.asset).
        /// </summary>
        public const float AsteroidExplosionSpeed = GemExplosionMath.DefaultExplosionSpeed;

        /// <summary>Fallback spawn offset radius — prefer GemExplosionSettings.</summary>
        public const float AsteroidExplosionRadius = GemExplosionMath.DefaultExplosionRadius;

        /// <summary>
        /// Fallback linear damping — prefer GemExplosionSettings.LinearDamping (original 0.5).
        /// </summary>
        public const float GemDragPerSecond = GemExplosionMath.DefaultLinearDamping;

        /// <summary>SgtPlanet base radius on <c>Asteroid.prefab</c>.</summary>
        public const float AsteroidMeshBaseRadius = 0.5f;

        /// <summary>Padding over mesh radius for displacement and slight aim forgiveness.</summary>
        public const float AsteroidHitRadiusScale = 1.1f;

        /// <summary>Floor for bullet segment tests against small asteroids.</summary>
        public const float MinAsteroidHitRadius = 0.15f;
    }

    /// <summary>
    /// Server: ships near asteroids mine gems over time, spawning gem entities when chunks break off.
    /// Destroys asteroids when RemainingGems reaches zero.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AsteroidTerritorySystem))]
    public partial struct MiningSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GamePrefabs>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs) || prefabs.Gem == Entity.Null)
                return;

            float dt = SystemAPI.Time.DeltaTime;
            // [NETCODE] ServerTick seconds — matches client shrink / despawn (not World.Time).
            float spawnServerTime = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                state.EntityManager, SystemAPI.Time.ElapsedTime);
            // --- Map period (seam mining) ---
            // [TITAN-ORBIT] Prefer MapStateSingleton; skip tick if size is missing (never invent 1000).
            float preferredW = 0f;
            float preferredH = 0f;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState) &&
                ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight))
            {
                preferredW = mapState.MapWidth;
                preferredH = mapState.MapHeight;
            }

            if (!ToroidalMapEcs.ResolveMapSize(preferredW, preferredH, out float mapW, out float mapH))
                return;
            if (ToroidalMapEcs.IsValidMapSize(preferredW, preferredH))
                ToroidalMapEcs.SetMapSize(mapW, mapH);

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Home planet levels for territory gem bonus (1 + 0.05 × homeLevel) ---
            // [TITAN-ORBIT] Same formula as NGO MiningSystem when ship's team is in TerritoryTeamsMask.
            var homeLevels = new NativeArray<int>(6, Allocator.Temp);
            foreach (var planet in SystemAPI.Query<RefRO<PlanetState>>().WithAll<PlanetTag, HomePlanetTag>())
            {
                if (planet.ValueRO.Ownership == TeamId.None || !planet.ValueRO.IsHomePlanet)
                    continue;
                int idx = (int)planet.ValueRO.Ownership;
                if (idx >= 0 && idx < homeLevels.Length)
                    homeLevels[idx] = math.max(1, planet.ValueRO.PlanetLevel);
            }

            // --- Each ship mines every asteroid in range this tick ---
            foreach (var (shipTransform, shipState, shipEntity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<ShipState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                foreach (var (asteroidState, asteroidTransform, asteroidEntity) in SystemAPI
                             .Query<RefRW<AsteroidState>, RefRO<LocalTransform>>()
                             .WithAll<AsteroidTag>()
                             .WithEntityAccess())
                {
                    if (asteroidState.ValueRO.IsDestroyed)
                        continue;

                    if (ToroidalMapEcs.ToroidalDistance(
                            shipTransform.ValueRO.Position,
                            asteroidTransform.ValueRO.Position,
                            mapW,
                            mapH) > GemEconomyConstants.MiningRange)
                        continue;

                    var a = asteroidState.ValueRO;
                    float mined = GemEconomyConstants.MiningRate * dt;
                    mined = math.min(mined, a.RemainingGems);
                    if (mined < GemEconomyConstants.MinGemSpawnValue)
                        continue;

                    // --- Friendly territory gem bonus ---
                    // [TITAN-ORBIT] Base mined chunk is red; bonus portion spawns as a separate yellow
                    // gem (NGO isBonusGem). Asteroid loses RemainingGems at the base rate only.
                    // Mask (not strongest-wins TerritoryTeam): overlap still grants bonus to each owner.
                    int homeLevel = PlanetConnectionGraphLogic.GetHomePlanetLevel(
                        shipState.ValueRO.Team, homeLevels);
                    float gemMult = PlanetConnectionGraphLogic.FriendlyTerritoryGemMultiplier(
                        shipState.ValueRO.Team, a.TerritoryTeamsMask, homeLevel);
                    float bonusValue = mined * (gemMult - 1f);

                    a.RemainingGems -= mined;
                    // [TITAN-ORBIT] Record miner team so destroy-burst yellow gems stay friendly-only.
                    a.LastInteractTeam = shipState.ValueRO.Team;
                    if (a.RemainingGems <= 0f)
                    {
                        a.RemainingGems = 0f;
                        a.IsDestroyed = true;
                        // [PHYSICS] Mining kill — drop the hull this tick so the ship cannot
                        // keep grinding an empty volume while DestroyEntity is still pending.
                        AsteroidDeathPhysics.QueueStripColliders(ecb, state.EntityManager, asteroidEntity);
                    }

                    asteroidState.ValueRW = a;
                    GemSpawning.Spawn(
                        ecb,
                        prefabs.Gem,
                        asteroidTransform.ValueRO.Position,
                        mined,
                        (uint)asteroidEntity.Index,
                        burst: false,
                        spawnServerTime,
                        isBonusGem: false);
                    if (bonusValue >= GemEconomyConstants.MinGemSpawnValue)
                    {
                        GemSpawning.Spawn(
                            ecb,
                            prefabs.Gem,
                            asteroidTransform.ValueRO.Position,
                            bonusValue,
                            (uint)asteroidEntity.Index + 7919u,
                            burst: false,
                            spawnServerTime,
                            isBonusGem: true);
                    }
                }
            }

            homeLevels.Dispose();

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    /// <summary>
    /// Server: applies original-style linear/angular damping and integrates gem pose from
    /// <see cref="GemKinematics"/>. Gems are scripted movers — not Unity Physics bodies.
    /// Runs <b>after</b> <see cref="GemTractorBeamSystem"/> so tractor velocity and pose stay
    /// same-tick coherent. Skips linear damping only while a live tractor lock is active
    /// (<see cref="GemMotionState.PhaseTractor"/> and non-zero <see cref="GemMotionState.TractorShipId"/>).
    /// Tunables: <see cref="GemExplosionSettings"/> (Editor).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GemTractorBeamSystem))]
    [UpdateAfter(typeof(MiningSystem))]
    public partial struct GemMotionSystem : ISystem
    {
        /// <summary>Integrates velocity + tumble with PhysX-like damping (unbounded XZ).</summary>
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;
            var settings = GemExplosionSettingsCache.ResolveOrDefault();
            float linearDamping = settings.LinearDamping;
            float angularDamping = settings.AngularDamping;
            float stopSpeed = settings.StopSpeedThreshold;

            foreach (var (kinematics, transform, entity) in SystemAPI
                         .Query<RefRW<GemKinematics>, RefRW<LocalTransform>>()
                         .WithAll<GemTag>()
                         .WithEntityAccess())
            {
                var kin = kinematics.ValueRO;
                bool underTractor = false;
                bool hasMotion = SystemAPI.HasComponent<GemMotionState>(entity);
                GemMotionState motionRo = default;
                if (hasMotion)
                {
                    motionRo = SystemAPI.GetComponent<GemMotionState>(entity);
                    // [TITAN-ORBIT] Skip damping only for a live lock (phase + ship id).
                    // PhaseTractor alone with ShipId 0 was a stuck state: constant velocity forever.
                    underTractor = motionRo.Phase == GemMotionState.PhaseTractor &&
                                   motionRo.TractorShipId != 0;
                    if (motionRo.Phase == GemMotionState.PhaseTractor && motionRo.TractorShipId == 0)
                    {
                        motionRo.Phase = GemMotionState.PhaseCoast;
                        SystemAPI.SetComponent(entity, motionRo);
                    }
                }

                // --- Linear velocity ---
                // [TITAN-ORBIT] Live tractor owns constant pull speed — damping would fight the beam.
                float3 vel = underTractor
                    ? kin.Velocity
                    : GemExplosionMath.IntegrateLinearVelocity(
                        kin.Velocity, linearDamping, stopSpeed, dt);
                float3 ang = GemExplosionMath.IntegrateAngularVelocity(
                    kin.AngularVelocity, angularDamping, dt);

                // --- Integrate in unbounded space (same as ships); toroidal math is for reach only ---
                var lt = transform.ValueRO;
                lt.Position += vel * dt;
                if (math.lengthsq(ang) > 0.0001f)
                {
                    // AngularVelocity is rad/s — quaternion integrate in world space.
                    float angle = math.length(ang) * dt;
                    float3 axis = math.normalizesafe(ang, new float3(0f, 1f, 0f));
                    lt.Rotation = math.mul(quaternion.AxisAngle(axis, angle), lt.Rotation);
                }

                transform.ValueRW = lt;
                kinematics.ValueRW = new GemKinematics { Velocity = vel, AngularVelocity = ang };

                // --- Phase: Coast → Idle when stopped (never steal Tractor phase here) ---
                if (!hasMotion || underTractor)
                    continue;

                if (math.lengthsq(vel) < stopSpeed * stopSpeed)
                {
                    motionRo.Phase = GemMotionState.PhaseIdle;
                    SystemAPI.SetComponent(entity, motionRo);
                }
                else if (motionRo.Phase == GemMotionState.PhaseIdle)
                {
                    // Nudged / re-launched somehow — treat as coast again.
                    motionRo.Phase = GemMotionState.PhaseCoast;
                    SystemAPI.SetComponent(entity, motionRo);
                }
            }
        }
    }

    /// <summary>
    /// Server: despawns loose gems after their lifetime (original Gem.lifetimeSeconds = 20).
    /// Runs after motion so expired gems are removed before pickup that same tick is irrelevant.
    /// Shrink is presentation-only on clients via <see cref="GemState.SpawnServerTime"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GemMotionSystem))]
    [UpdateAfter(typeof(AsteroidDestructionSystem))]
    public partial struct GemLifetimeDespawnSystem : ISystem
    {
        /// <summary>Destroys gems whose elapsed life exceeds <see cref="GemExplosionSettings.GemLifetimeSeconds"/>.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var settings = GemExplosionSettingsCache.ResolveOrDefault();
            settings.ClampCounts();
            float lifetime = settings.GemLifetimeSeconds;
            // [NETCODE] Same ServerTick timeline stamped at spawn (late-join safe).
            float now = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                state.EntityManager, SystemAPI.Time.ElapsedTime);

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Expire uncollected gems ---
            // [TITAN-ORBIT] Matches NGO Gem.FixedUpdate: elapsed >= lifetimeSeconds → Despawn.
            foreach (var (gemState, gemEntity) in SystemAPI
                         .Query<RefRO<GemState>>()
                         .WithAll<GemTag>()
                         .WithEntityAccess())
            {
                float spawnTime = gemState.ValueRO.SpawnServerTime;
                // Baked prefab default is 0 until spawn overwrites — skip unset stamps.
                if (spawnTime <= 0f)
                    continue;

                if (now - spawnTime < lifetime)
                    continue;

                ecb.DestroyEntity(gemEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    /// <summary>
    /// Server: collects gems into ship cargo when a gem center is inside a wing-tip or hull
    /// absorb sphere. Tractor beams only <b>pull</b> gems; this system is the actual consume.
    /// No tractor lock is required — flying a wing tip or the hull over a gem is enough.
    /// Radii come from <see cref="TractorBeamSettings"/> (Wing Collect Radius / Hull Pickup Range).
    /// Runs after <see cref="GemMotionSystem"/> so same-tick tractor pull can land in the zone.
    /// Skips ships with Health &lt;= <see cref="ShipDamageLogic.DeathThreshold"/> so a 0-HP hull
    /// cannot magnet-sip cargo and stay undead under dual-resource death rules.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AsteroidDestructionSystem))]
    [UpdateAfter(typeof(GemLifetimeDespawnSystem))]
    [UpdateAfter(typeof(GemMotionSystem))]
    public partial struct GemPickupSystem : ISystem
    {
        /// <summary>
        /// [ECS/DOTS] Cached gem query so we snapshot once per tick instead of nesting
        /// <c>SystemAPI.Query</c> inside the ship loop (nested foreach queries are unsupported).
        /// Server-only — full gem <c>ToEntityArray</c> is join-crash safe here.
        /// </summary>
        EntityQuery _gemQuery;

        /// <summary>
        /// [ECS/DOTS] Builds the gem snapshot query before the first pickup tick.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            // --- Gem snapshot query ---
            // [ECS/DOTS] Read-only: we write cargo on the ship and destroy/update gems via ECB.
            _gemQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<GemTag>(),
                ComponentType.ReadOnly<GemState>(),
                ComponentType.ReadOnly<LocalTransform>());
        }

        /// <summary>
        /// Each server tick: snapshot gems, then for every living ship with cargo space absorb
        /// any gem whose center is inside a wing-tip or hull pickup sphere (toroidal XZ).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Map period for wing / hull collect radius ---
            // [TITAN-ORBIT] Same prefer pattern as MiningSystem / GemTractorBeamSystem — missing
            // size → skip (never invent 1000; wrong period breaks wrap-seam pickup).
            // Resolve map size BEFORE the EntityCommandBuffer so a missing map does not leak an ECB.
            float preferredW = 0f;
            float preferredH = 0f;
            if (SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState) &&
                ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight))
            {
                preferredW = mapState.MapWidth;
                preferredH = mapState.MapHeight;
            }

            if (!ToroidalMapEcs.ResolveMapSize(preferredW, preferredH, out float mapW, out float mapH))
                return;
            if (ToroidalMapEcs.IsValidMapSize(preferredW, preferredH))
                ToroidalMapEcs.SetMapSize(mapW, mapH);

            // --- Same timeline as GemState.SpawnServerTime / self-pickup block stamps ---
            float nowServerTime = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                state.EntityManager, SystemAPI.Time.ElapsedTime);

            // --- Snapshot gems once (no nested SystemAPI.Query) ---
            // [ECS/DOTS] Copies stay valid while we iterate ships. Destroy/update goes through ECB
            // so the arrays are not invalidated mid-tick. We write leftover value/size back into
            // these copies (not `using var` — C# forbids mutating a using variable's indexer).
            int gemCount = _gemQuery.CalculateEntityCount();
            if (gemCount <= 0)
                return;

            var pickupSettings = TractorBeamSettingsCache.ResolveOrDefault();
            var gemEntities = _gemQuery.ToEntityArray(Allocator.Temp);
            var gemStates = _gemQuery.ToComponentDataArray<GemState>(Allocator.Temp);
            var gemTransforms = _gemQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var gemConsumed = new NativeArray<bool>(gemCount, Allocator.Temp);

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (shipTransform, shipState, shipEntity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRW<ShipState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                // [TITAN-ORBIT] Mirror GemTractorBeamSystem — 0-HP hull cannot magnet-sip gems
                // that would keep dual-resource death from firing.
                if (shipState.ValueRO.IsDead ||
                    shipState.ValueRO.Health <= ShipDamageLogic.DeathThreshold ||
                    shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                float capacityLeft = shipState.ValueRO.GemCapacity - shipState.ValueRO.CurrentGems;
                if (capacityLeft <= 0.001f)
                    continue;

                int shipNetworkId = 0;
                if (state.EntityManager.HasComponent<GhostOwner>(shipEntity))
                    shipNetworkId = state.EntityManager.GetComponentData<GhostOwner>(shipEntity).NetworkId;

                bool hasWings = state.EntityManager.HasBuffer<ShipWingTractorBeamElement>(shipEntity) &&
                                state.EntityManager.GetBuffer<ShipWingTractorBeamElement>(shipEntity).Length > 0;

                for (int gi = 0; gi < gemCount; gi++)
                {
                    if (gemConsumed[gi])
                        continue;

                    var gemState = gemStates[gi];
                    var gemTransform = gemTransforms[gi];
                    Entity gemEntity = gemEntities[gi];

                    // [TITAN-ORBIT] Damage-spill penalty — source ship cannot reclaim yet.
                    if (GemSelfPickupBlock.IsBlockedForShip(gemState, shipNetworkId, nowServerTime))
                        continue;

                    if (!IsWithinPickupRange(
                            state.EntityManager,
                            shipEntity,
                            shipTransform.ValueRO,
                            gemTransform,
                            gemState,
                            hasWings,
                            pickupSettings,
                            mapW,
                            mapH))
                        continue;

                    float take = math.min(gemState.Value, capacityLeft);
                    if (take <= 0.001f)
                        continue;

                    var ship = shipState.ValueRO;
                    ship.CurrentGems += take;
                    shipState.ValueRW = ship;
                    capacityLeft -= take;

                    float remainder = gemState.Value - take;
                    if (remainder > 0.001f)
                    {
                        // --- Partial take (cargo filled mid-gem) ---
                        // Write the leftover into the snapshot so a later ship this tick sees
                        // the reduced value, not the original (would double-credit cargo).
                        gemState.Value = remainder;
                        float scale = math.clamp(math.sqrt(remainder) * 0.2f, 0.2f, 0.5f);
                        gemState.Size = scale;
                        var leftoverXf = LocalTransform.FromPositionRotationScale(
                            gemTransform.Position,
                            gemTransform.Rotation,
                            scale);
                        gemStates[gi] = gemState;
                        gemTransforms[gi] = leftoverXf;
                        ecb.SetComponent(gemEntity, gemState);
                        ecb.SetComponent(gemEntity, leftoverXf);
                    }
                    else
                    {
                        gemConsumed[gi] = true;
                        ecb.DestroyEntity(gemEntity);
                    }

                    if (capacityLeft <= 0.001f)
                        break;
                }
            }

            gemConsumed.Dispose();
            gemEntities.Dispose();
            gemStates.Dispose();
            gemTransforms.Dispose();
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>
        /// True when the gem is inside a cargo absorb zone for this ship.
        /// Uses <see cref="TractorBeamSettings"/> wing-tip and optional hull radii so designers
        /// can widen fly-over scoop without changing tractor search reach.
        /// No tractor lock is required — distance alone consumes.
        /// </summary>
        static bool IsWithinPickupRange(
            EntityManager em,
            Entity shipEntity,
            in LocalTransform shipTransform,
            in LocalTransform gemTransform,
            in GemState gemState,
            bool hasWings,
            TractorBeamSettings pickupSettings,
            float mapW,
            float mapH)
        {
            float3 gemPos = gemTransform.Position;

            // --- Wing-tip collect (tractor destination + tip fly-over) ---
            // [TITAN-ORBIT] Effective radius = WingCollectRadius + gem size pad.
            // Tractor beams pull gems into this zone; flying a tip over a gem also absorbs.
            if (hasWings)
            {
                var wings = em.GetBuffer<ShipWingTractorBeamElement>(shipEntity);
                float collectRadius = pickupSettings.ResolveWingCollectRadius(gemState.Size);
                for (int wi = 0; wi < wings.Length; wi++)
                {
                    float3 wingPos = ShipWingTractorBeamPose.GetWorldPosition(shipTransform, wings[wi]);
                    if (GemTractorBeamMath.ToroidalDistance(gemPos, wingPos, mapW, mapH) <= collectRadius)
                        return true;
                }

                // --- Optional hull scoop while wings exist ---
                // [TITAN-ORBIT] When ON, flying the body over gem piles absorbs without waiting
                // for a wing tip / tractor lock. When OFF, only tip zones collect (tight old feel).
                if (pickupSettings.AlsoUseHullPickupWithWings)
                    return IsWithinHullPickupRange(shipTransform, gemPos, gemState.Size, pickupSettings, mapW, mapH);

                return false;
            }

            // --- No wings: hull-center only ---
            return IsWithinHullPickupRange(shipTransform, gemPos, gemState.Size, pickupSettings, mapW, mapH);
        }

        /// <summary>
        /// Hull-center absorb test. Designer <see cref="TractorBeamSettings.HullPickupRange"/> is
        /// measured from the ship origin; we also floor at collision-hull radius + gem half-size
        /// so overlapping the visible hull always collects even if the Inspector range is tiny.
        /// </summary>
        static bool IsWithinHullPickupRange(
            in LocalTransform shipTransform,
            float3 gemPos,
            float gemSize,
            TractorBeamSettings pickupSettings,
            float mapW,
            float mapH)
        {
            float designed = pickupSettings.ResolveHullPickupRange(gemSize);
            float hullFloor = BodyCollisionMath.GetShipHullRadiusWorld(shipTransform.Scale) +
                              math.max(0f, gemSize) * 0.5f;
            float hullRange = math.max(designed, hullFloor);
            return GemTractorBeamMath.ToroidalDistance(gemPos, shipTransform.Position, mapW, mapH) <=
                   hullRange;
        }
    }

    /// <summary>
    /// Server: deposits ship cargo gems into friendly planets while docked at the gem moon.
    /// Transfers happen on a <b>discrete metronome</b> (one ship-level chunk every
    /// <see cref="GemEconomyConstants.GemDepositBeatIntervalSeconds"/>), matching client deposit SFX.
    /// Planet treasury levels up via <see cref="PlanetEconomyMath"/>; the player's spendable
    /// Bank (contributed gems) is always credited on the team's <b>home</b> planet ledger so
    /// orbit-store purchases work after depositing at any friendly moon.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GemPickupSystem))]
    public partial struct GemDepositSystem : ISystem
    {
        /// <summary>
        /// Fixed-step deposit pass: for each docked ship with deposit intent and cargo gems,
        /// transfer one metronome chunk into the docked planet and credit the home Bank.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Fixed timestep ---
            // [UNITY] Same dt as other sim systems — we accumulate it into ShipDepositBeatTimer.
            float dt = SystemAPI.Time.DeltaTime;
            float beatInterval = GemEconomyConstants.GemDepositBeatIntervalSeconds;

            foreach (var (shipState, shipInput, moonDock, shipEntity) in SystemAPI
                         .Query<RefRW<ShipState>, RefRO<ShipInput>, RefRO<ShipMoonDockState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                // --- Skip ships that cannot deposit ---
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;
                if (shipState.ValueRO.Team == TeamId.None || shipState.ValueRO.CurrentGems <= 0f)
                    continue;

                // --- Deposit intent: prefer ShipDepositIntent (survives prediction rollback) ---
                bool wantDeposit = shipInput.ValueRO.WantDepositGems;
                if (state.EntityManager.HasComponent<ShipDepositIntent>(shipEntity))
                    wantDeposit = state.EntityManager.GetComponentData<ShipDepositIntent>(shipEntity).WantDepositGems;

                // --- Server metronome timer (separate from ghosted intent — StarshipGhost hash safe) ---
                bool hasTimer = state.EntityManager.HasComponent<ShipDepositBeatTimer>(shipEntity);
                ShipDepositBeatTimer timer = hasTimer
                    ? state.EntityManager.GetComponentData<ShipDepositBeatTimer>(shipEntity)
                    : default;

                // --- GhostOwner.NetworkId keys the contributed-gems Bank row ---
                int ownerNetworkId = 0;
                if (state.EntityManager.HasComponent<GhostOwner>(shipEntity))
                    ownerNetworkId = state.EntityManager.GetComponentData<GhostOwner>(shipEntity).NetworkId;

                foreach (var (planetState, _, planetEntity) in SystemAPI
                             .Query<RefRW<PlanetState>, RefRO<LocalTransform>>()
                             .WithAll<PlanetTag>()
                             .WithEntityAccess())
                {
                    if (planetState.ValueRO.Ownership != shipState.ValueRO.Team)
                        continue;

                    if (!CanDepositAtPlanet(
                            shipInput.ValueRO,
                            wantDeposit,
                            moonDock.ValueRO,
                            planetState.ValueRO))
                        continue;

                    // --- Metronome accumulator (first eligible tick deposits immediately) ---
                    // [TITAN-ORBIT] Priming Accum to beatInterval matches the client metronome, which
                    // fires on the first frame WantDepositGems is true (no half-second silence).
                    if (timer.Accum <= 0f)
                        timer.Accum = beatInterval;

                    timer.Accum += dt;

                    // Catch-up while: usually 0–1 beat per fixed step; rare hitch may run 2+.
                    while (timer.Accum >= beatInterval &&
                           shipState.ValueRO.CurrentGems > 0.001f)
                    {
                        timer.Accum -= beatInterval;

                        // --- One gem-value chunk (or leftover cargo) ---
                        float amount = GemEconomyConstants.GetDepositChunkAmount(
                            shipState.ValueRO.ShipLevel,
                            shipState.ValueRO.CurrentGems);
                        if (amount <= 0.001f)
                            break;

                        // --- Ship cargo ↓ ---
                        var ship = shipState.ValueRO;
                        ship.CurrentGems -= amount;
                        // [TITAN-ORBIT] Depositing the last gems while hull is already 0 is lethal.
                        float h = ship.Health;
                        float g = ship.CurrentGems;
                        bool dead = ship.IsDead;
                        ShipDamageLogic.TryMarkDeadIfHullAndGemsDepleted(ref h, ref g, ref dead);
                        ship.Health = h;
                        ship.CurrentGems = g;
                        ship.IsDead = dead;
                        shipState.ValueRW = ship;

                        // --- Planet treasury ↑ (level-up math) ---
                        var planet = planetState.ValueRO;
                        int level = planet.PlanetLevel;
                        float gems = planet.CurrentGems;
                        PlanetEconomyMath.DepositGems(ref level, ref gems, amount);
                        planet.PlanetLevel = level;
                        planet.CurrentGems = gems;
                        planetState.ValueRW = planet;

                        // --- Personal Bank ↑ on the team's HOME ledger (store spend currency) ---
                        // [TITAN-ORBIT] Do not gate on planet.IsHomePlanet alone — captured moons must
                        // still credit Bank. MoonOrbitStoreSystem always spends from the home buffer.
                        if (ownerNetworkId > 0 &&
                            TryFindHomePlanetEntity(
                                state.EntityManager,
                                shipState.ValueRO.Team,
                                planet.IsHomePlanet ? planetEntity : Entity.Null,
                                out Entity homeEntity))
                        {
                            ContributedGemsLogic.Add(state.EntityManager, homeEntity, ownerNetworkId, amount);
                        }

                        // --- Match-long miner score (minimap top miner badge) ---
                        // [TITAN-ORBIT] Cumulative gems deposited this match — not live cargo hold.
                        // Integer floor matches the "score" feel; fractional leftovers round down.
                        if (state.EntityManager.HasComponent<ShipMatchStats>(shipEntity))
                        {
                            int gemsScore = (int)amount;
                            if (gemsScore > 0)
                            {
                                var matchStats = state.EntityManager.GetComponentData<ShipMatchStats>(shipEntity);
                                matchStats.GemsDeposited += gemsScore;
                                state.EntityManager.SetComponentData(shipEntity, matchStats);
                            }
                        }

                        // --- Ghosted presentation beat (clients SFX / Ship↓ / Bank↑ from this) ---
                        // [NETCODE] BeatSequence++ tells every client a real chunk transferred.
                        if (state.EntityManager.HasComponent<ShipDepositFeedback>(shipEntity))
                        {
                            var feedback = state.EntityManager.GetComponentData<ShipDepositFeedback>(shipEntity);
                            feedback.LastChunkAmount = amount;
                            feedback.BeatSequence += 1u;
                            state.EntityManager.SetComponentData(shipEntity, feedback);
                        }
                    }

                    // Only one friendly docked moon can accept deposits for this ship.
                    break;
                }

                // --- Persist beat timer / clear when deposit stopped ---
                if (hasTimer)
                {
                    if (!wantDeposit || shipState.ValueRO.CurrentGems <= 0.001f)
                        timer.Accum = 0f;

                    state.EntityManager.SetComponentData(shipEntity, timer);
                }
            }
        }

        /// <summary>
        /// True when the ship is fully landed on this planet's moon, wants to deposit, and is not thrusting.
        /// </summary>
        static bool CanDepositAtPlanet(
            in ShipInput input,
            bool wantDepositGems,
            in ShipMoonDockState moonDock,
            in PlanetState planet)
        {
            if (input.Thrust || !wantDepositGems)
                return false;

            if (moonDock.MoonPlanetId != planet.PlanetId || moonDock.MoonPlanetId == 0)
                return false;

            return moonDock.LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold;
        }

        /// <summary>
        /// Resolves the team's home planet entity for contributed-gems credit.
        /// When <paramref name="dockedIfHome"/> is already the home entity, uses it; otherwise
        /// searches <see cref="HomePlanetTag"/> on the server world.
        /// </summary>
        static bool TryFindHomePlanetEntity(
            EntityManager em,
            TeamId team,
            Entity dockedIfHome,
            out Entity homeEntity)
        {
            homeEntity = Entity.Null;
            if (team == TeamId.None)
                return false;

            // Fast path: deposit moon is already the home capital.
            if (dockedIfHome != Entity.Null)
            {
                homeEntity = dockedIfHome;
                return true;
            }

            // Server-only tag — safe here (this system is ServerSimulation).
            using var query = em.CreateEntityQuery(typeof(HomePlanetTag), typeof(PlanetState));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].Ownership != team)
                    continue;
                homeEntity = entities[i];
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Server: when an asteroid is destroyed, spawns N gem entities (Editor min–max on
    /// <see cref="GemExplosionSettings"/>) that sum to leftover <see cref="AsteroidState.RemainingGems"/>,
    /// with original NGO explosion speed, damping, and tumble. Schedules a timed respawn
    /// (<see cref="AsteroidSpawning.ScheduleRespawn"/>) then destroys the entity.
    /// Clients present gems only after gem ghosts Instantiates, driven by ghosted
    /// <see cref="GemKinematics"/> / LocalTransform (no client-invented burst VFX).
    /// <para>
    /// [TITAN-ORBIT] Despawn triggers on <see cref="AsteroidState.IsDestroyed"/> <b>or</b>
    /// <c>Health &lt;= 0</c> (belt-and-suspenders for bullet kills). Missing Gem prefab must not
    /// leave 0-HP zombies — we still destroy the entity and only skip the gem burst.
    /// Runs after predicted ram damage and bullet sim so a same-tick kill is despawned
    /// before the next physics step.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedFixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    [UpdateAfter(typeof(MiningSystem))]
    public partial struct AsteroidDestructionSystem : ISystem
    {
        /// <summary>
        /// One dead rock copied out of the query so we can strip ghost identity (structural)
        /// after the foreach, then destroy via ECB.
        /// </summary>
        struct PendingDestroy
        {
            public Entity Entity;
            public float3 Position;
            public float Scale;
            public float RemainingGems;
            public float MaxGems;
            public float Health;
            public float MaxHealth;
            public float Size;
            public TeamId LastInteractTeam;
            public byte TerritoryTeamsMask;
        }

        /// <summary>Ensures the respawn queue exists. Gem prefab is optional for despawn.</summary>
        public void OnCreate(ref SystemState state)
        {
            AsteroidSpawning.EnsureRespawnQueue(state.EntityManager);
            state.RequireForUpdate<AsteroidRespawnQueueTag>();
        }

        /// <summary>
        /// For each destroyed asteroid: burst leftover gems, enqueue respawn, strip collision,
        /// strip leftover ghost identity, destroy entity.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // [TITAN-ORBIT] Gem prefab is optional for despawn — never skip DestroyEntity when
            // GamePrefabs / Gem is missing (that left Health=0 rocks alive forever).
            Entity gemPrefab = Entity.Null;
            if (SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs))
                gemPrefab = prefabs.Gem;
            bool canSpawnGems = gemPrefab != Entity.Null;
            var settings = GemExplosionSettingsCache.ResolveOrDefault();
            settings.ClampCounts();
            float spawnTime = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                state.EntityManager, SystemAPI.Time.ElapsedTime);
            // Respawn queue is server-only — World.Time is fine (not replicated to clients).
            double now = SystemAPI.Time.ElapsedTime;
            var respawnBuffer = SystemAPI.GetSingletonBuffer<PendingAsteroidRespawnElement>();
            var em = state.EntityManager;

            // --- Phase 1: copy dead rocks (no structural changes inside the query) ---
            var pending = new NativeList<PendingDestroy>(8, Allocator.Temp);
            foreach (var (asteroidState, asteroidTransform, entity) in SystemAPI
                         .Query<RefRO<AsteroidState>, RefRO<LocalTransform>>()
                         .WithAll<AsteroidTag>()
                         .WithEntityAccess())
            {
                var a = asteroidState.ValueRO;
                // Bullet path sets IsDestroyed with Health=0; also accept Health<=0 / NaN alone.
                // 0-HP zombies that skip this destroy still raise PhysX ram contacts.
                bool shouldDestroy = a.IsDestroyed || !(a.Health > 0.01f);
                if (!shouldDestroy)
                    continue;

                var lt = asteroidTransform.ValueRO;
                pending.Add(new PendingDestroy
                {
                    Entity = entity,
                    Position = lt.Position,
                    Scale = lt.Scale,
                    RemainingGems = a.RemainingGems,
                    MaxGems = a.MaxGems,
                    Health = a.Health,
                    MaxHealth = a.MaxHealth,
                    Size = a.Size,
                    LastInteractTeam = a.LastInteractTeam,
                    TerritoryTeamsMask = a.TerritoryTeamsMask,
                });
            }

            if (pending.Length == 0)
            {
                pending.Dispose();
                return;
            }

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            for (int i = 0; i < pending.Length; i++)
            {
                PendingDestroy dead = pending[i];
                Entity entity = dead.Entity;
                if (!em.Exists(entity))
                    continue;

                float3 pos = dead.Position;
                pos.y = 0f;
                float remaining = dead.RemainingGems;
                float rpcScale = dead.Scale;
                if (rpcScale <= AsteroidDeathPhysics.CulledTransformScale + 0.001f)
                    rpcScale = 1f;

                float bonusExtra = 0f;
                // --- Territory gem bonus on destroy burst ---
                // [TITAN-ORBIT] Yellow gems only when the last miner/shooter's team owns this rock
                // (mask bit). Enemy-tinted asteroids must not dump bonus gems on kill.
                // Legacy bug: FriendlyTerritoryGemMultiplier(TerritoryTeam, TerritoryTeam) always
                // matched for any non-None tint — ignored the destroyer.
                if (dead.LastInteractTeam != TeamId.None &&
                    remaining >= GemEconomyConstants.MinGemSpawnValue)
                {
                    int homeLevel = PlanetConnectionGraphCache.GetHomePlanetLevel(dead.LastInteractTeam);
                    float mult = PlanetConnectionGraphLogic.FriendlyTerritoryGemMultiplier(
                        dead.LastInteractTeam, dead.TerritoryTeamsMask, homeLevel);
                    bonusExtra = remaining * (mult - 1f);
                }

                if (canSpawnGems && remaining >= GemEconomyConstants.MinGemSpawnValue)
                {
                    // Deterministic seed so client immediate burst can match count/feel closely.
                    uint seed = math.hash(new uint2((uint)entity.Index, math.hash(pos)));
                    SpawnAsteroidDestructionGems(
                        ecb, gemPrefab, pos, remaining, seed, settings, spawnTime, isBonusGem: false);
                    if (bonusExtra >= GemEconomyConstants.MinGemSpawnValue)
                    {
                        SpawnAsteroidDestructionGems(
                            ecb, gemPrefab, pos, bonusExtra, seed + 1337u, settings, spawnTime,
                            isBonusGem: true);
                    }
                }

                // --- Schedule respawn (original AsteroidRespawnManager.ScheduleRespawn) ---
                // Prefer MaxGems / MaxHealth. Fallbacks cover older rocks that only had HP=gems.
                float restoreGems = dead.MaxGems;
                if (restoreGems < GemEconomyConstants.MinGemSpawnValue)
                    restoreGems = math.max(dead.Health, remaining);
                if (restoreGems < GemEconomyConstants.MinGemSpawnValue)
                    restoreGems = 1f;

                float restoreHealth = dead.MaxHealth;
                if (restoreHealth < 1f)
                    restoreHealth = math.max(dead.Health, restoreGems);
                if (restoreHealth < 1f)
                    restoreHealth = 1f;

                AsteroidSpawning.ScheduleRespawn(
                    respawnBuffer,
                    pos,
                    rpcScale,
                    restoreGems,
                    restoreHealth,
                    dead.Size,
                    now,
                    settings.AsteroidRespawnDelaySeconds);

                // --- Clients: destroy seed-hydrated local rock now ---
                // [NETCODE] Asteroids are not ghost-relevant. HitRpc alone misses mining/ram kills
                // and can miss large rocks (surface hit far from center). Broadcast pose+scale.
                Entity destroyRpc = ecb.CreateEntity();
                ecb.AddComponent(destroyRpc, new AsteroidDestroyedRpc
                {
                    Position = pos,
                    Scale = rpcScale,
                });
                ecb.AddComponent(destroyRpc, new SendRpcCommandRequest());

                // --- Drop the hull before DestroyEntity ---
                // [PHYSICS] GhostCleanup zombies / stale static worlds kept colliding after
                // DestroyEntity. Strip first; then strip leftover GhostInstance so destroy is real.
                AsteroidDeathPhysics.QueueStripAndDisable(ecb, em, entity);
                ClientLocalMapBodySpawn.StripGhostNetworking(em, entity);
                ecb.DestroyEntity(entity);
            }

            pending.Dispose();
            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>
        /// Spawns gems whose values sum to <paramref name="remaining"/>.
        /// Count starts from designer min–max, then rises when needed so each piece stays at or
        /// below <see cref="GemExplosionSettings.MaxGemUnitValue"/> (musical piano-width cap).
        /// Multi-gem bursts use <see cref="GemChordValues"/> so pickups form a C-major chord
        /// (dyad / triad / maj7) instead of N copies of the same pitch.
        /// </summary>
        static void SpawnAsteroidDestructionGems(
            EntityCommandBuffer ecb,
            Entity gemPrefab,
            float3 pos,
            float remaining,
            uint seed,
            GemExplosionSettings settings,
            float spawnServerTime,
            bool isBonusGem)
        {
            var rng = Random.CreateFromIndex(seed);
            // [TITAN-ORBIT] Unit cap keeps each pickup on the 88-key chromatic SFX ladder.
            int count = GemExplosionMath.ResolveGemCountForUnitCap(
                remaining,
                settings.MinGemCount,
                settings.MaxGemCount,
                settings.MaxGemUnitValue,
                ref rng);

            // --- Chord-tone values (C / C+G / C+E+G / …) summing to remaining ---
            // [TITAN-ORBIT] Equal split made every gem the same note; chord fill makes consume SFX harmonic.
            var chordValues = new float[count];
            GemChordValues.Fill(remaining, count, settings.MaxGemUnitValue, chordValues);

            for (int i = 0; i < count; i++)
            {
                float value = chordValues[i];
                if (value < GemEconomyConstants.MinGemSpawnValue)
                    continue;
                GemSpawning.Spawn(
                    ecb,
                    gemPrefab,
                    pos,
                    value,
                    seed + (uint)(i + 1) * 97u,
                    burst: true,
                    spawnServerTime,
                    settings,
                    burstIndex: (byte)i,
                    isBonusGem: isBonusGem);
            }
        }
    }

    /// <summary>Shared gem entity spawn helper for mining and asteroid destruction bursts.</summary>
    public static class GemSpawning
    {
        /// <summary>
        /// Instantiates a gem prefab with value, optional burst velocity/tumble, offset, and lifetime stamp.
        /// </summary>
        /// <param name="spawnServerTime">ServerTick seconds — drives lifetime despawn and client shrink.</param>
        /// <param name="burstIndex">
        /// Asteroid-burst slot (0..N-1) for client VFX handoff. Mining nuggets leave 0.
        /// </param>
        /// <param name="burstIntensity">
        /// 0..1 launch hardness for damage expulsion (1 = asteroid-burst default). Ignored when
        /// <paramref name="burst"/> is false.
        /// </param>
        /// <param name="excludePickupNetworkId">
        /// Damage-spill source ship NetworkId, or 0 for free gems (mining / asteroid burst).
        /// </param>
        /// <param name="excludePickupUntilServerTime">
        /// ServerTick-timeline seconds when that ship may collect again (0 = no exclusion).
        /// </param>
        public static void Spawn(
            EntityCommandBuffer ecb,
            Entity gemPrefab,
            float3 position,
            float value,
            uint salt,
            bool burst,
            float spawnServerTime,
            GemExplosionSettings settings = null,
            byte burstIndex = 0,
            bool isBonusGem = false,
            float burstIntensity = 1f,
            int excludePickupNetworkId = 0,
            float excludePickupUntilServerTime = 0f)
        {
            if (value <= 0f)
                return;

            settings ??= GemExplosionSettingsCache.ResolveOrDefault();
            var rng = Random.CreateFromIndex(math.hash(position) + salt + 17u);
            float3 spawnDir = GemExplosionMath.RandomUnitXZ(ref rng);

            float radius = burst ? settings.AsteroidExplosionRadius : 0.8f;
            float3 offset = spawnDir * radius * rng.NextFloat(0.3f, 1f);
            float scale = math.clamp(math.sqrt(value) * 0.2f, 0.2f, 0.5f);

            Entity gem = ecb.Instantiate(gemPrefab);
            ecb.SetComponent(gem, LocalTransform.FromPositionRotationScale(position + offset, quaternion.identity, scale));
            ecb.SetComponent(gem, new GemState
            {
                Value = value,
                Size = scale,
                DepositTeam = TeamId.None,
                SpawnServerTime = spawnServerTime,
                IsBonusGem = isBonusGem,
                // [TITAN-ORBIT] Damage-spill self-pickup penalty (ghosted — client hides beams too).
                // Mining / asteroid bursts leave these 0.
                ExcludePickupNetworkId = excludePickupNetworkId,
                ExcludePickupUntilServerTime = excludePickupUntilServerTime,
            });

            // --- Motion phase + burst slot (ghosted for client handoff / tractor lock) ---
            ecb.SetComponent(gem, new GemMotionState
            {
                Phase = GemMotionState.PhaseCoast,
                BurstIndex = burstIndex,
                TractorShipId = 0,
                TractorWingIndex = 0,
                TractorLockTick = 0,
                TractorExtendDuration = 0f,
            });

            if (burst)
            {
                // --- Original NGO GemSpawner launch + tumble (intensity scales ship-damage spills) ---
                float3 vel = GemExplosionMath.BurstVelocity(
                    spawnDir,
                    settings.AsteroidExplosionSpeed,
                    settings.SpeedRandomMin,
                    settings.SpeedRandomMax,
                    burstIntensity,
                    ref rng);
                float3 ang = GemExplosionMath.BurstAngularVelocity(settings.AngularSpeedMax, ref rng);
                ecb.SetComponent(gem, new GemKinematics { Velocity = vel, AngularVelocity = ang });
            }
            else
            {
                // Small outward nudge so mined gems are not stuck inside the asteroid mesh.
                float speed = rng.NextFloat(settings.MiningNudgeSpeedMin, settings.MiningNudgeSpeedMax);
                float3 ang = GemExplosionMath.BurstAngularVelocity(settings.AngularSpeedMax * 0.35f, ref rng);
                ecb.SetComponent(gem, new GemKinematics { Velocity = spawnDir * speed, AngularVelocity = ang });
            }
        }
    }

    /// <summary>
    /// Server helper: spawn one world gem from ship cargo after <see cref="ShipDamageLogic"/>
    /// deducted the value. Legacy NGO spawned a single gem sized to the hit's expelled value
    /// (larger damage → larger gem via sqrt(value) scale in <see cref="GemSpawning"/>).
    /// </summary>
    public static class ShipGemExpulsion
    {
        /// <summary>
        /// Instantiates a burst gem at the ship position. No-op when value or prefab is invalid.
        /// Cargo must already be deducted by the damage/deposit caller.
        /// </summary>
        /// <param name="ecb">Structural changes for this sim tick.</param>
        /// <param name="gemPrefab">From <see cref="GamePrefabs.Gem"/>.</param>
        /// <param name="shipPosition">Ship sim center (logical / unbounded).</param>
        /// <param name="gemValue">Expelled cargo value (&gt; MinGemSpawnValue).</param>
        /// <param name="intensity">0..1 launch hardness (ram impact high, grind / bullets softer).</param>
        /// <param name="salt">Deterministic RNG salt (entity index + tick).</param>
        /// <param name="spawnServerTime">Server elapsed seconds for lifetime.</param>
        /// <param name="sourceShipNetworkId">
        /// <see cref="GhostOwner.NetworkId"/> of the spilling ship — blocked from reclaim until
        /// <see cref="GemExplosionSettings.SelfPickupBlockSeconds"/> elapses.
        /// </param>
        public static void SpawnFromDamage(
            EntityCommandBuffer ecb,
            Entity gemPrefab,
            float3 shipPosition,
            float gemValue,
            float intensity,
            uint salt,
            float spawnServerTime,
            int sourceShipNetworkId)
        {
            if (gemPrefab == Entity.Null || gemValue < GemEconomyConstants.MinGemSpawnValue)
                return;

            // --- Self-pickup penalty stamp ---
            // [TITAN-ORBIT] Other ships may take the gem immediately; the source cannot until delay.
            var settings = GemExplosionSettingsCache.ResolveOrDefault();
            settings.ClampCounts();
            float blockUntil = 0f;
            int excludeId = 0;
            if (sourceShipNetworkId > 0 && settings.SelfPickupBlockSeconds > 0f)
            {
                excludeId = sourceShipNetworkId;
                blockUntil = spawnServerTime + settings.SelfPickupBlockSeconds;
            }

            // [TITAN-ORBIT] One gem per hit — value drives size (sqrt) and musical pitch.
            GemSpawning.Spawn(
                ecb,
                gemPrefab,
                shipPosition,
                gemValue,
                salt,
                burst: true,
                spawnServerTime,
                settings: settings,
                burstIndex: 0,
                isBonusGem: false,
                burstIntensity: math.saturate(intensity),
                excludePickupNetworkId: excludeId,
                excludePickupUntilServerTime: blockUntil);
        }
    }

    /// <summary>
    /// Shared check: damage-spilled gems block the source ship from pickup / tractor until the
    /// stamped <see cref="GemState.ExcludePickupUntilServerTime"/>.
    /// </summary>
    public static class GemSelfPickupBlock
    {
        /// <summary>
        /// True when <paramref name="shipNetworkId"/> is the expelling ship and the penalty window
        /// has not elapsed yet on the SpawnServerTime timeline.
        /// </summary>
        public static bool IsBlockedForShip(in GemState gem, int shipNetworkId, float nowServerTime)
        {
            if (shipNetworkId <= 0 || gem.ExcludePickupNetworkId <= 0)
                return false;
            if (shipNetworkId != gem.ExcludePickupNetworkId)
                return false;
            if (gem.ExcludePickupUntilServerTime <= 0f)
                return false;
            return nowServerTime < gem.ExcludePickupUntilServerTime;
        }
    }
}
