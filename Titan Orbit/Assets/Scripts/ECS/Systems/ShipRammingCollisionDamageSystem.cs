using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-authoritative ramming damage from <b>real collisions only</b>:
    /// PhysX collision-event pairs after movers wrap onto the canonical chart.
    /// No proximity skin — flying past an asteroid does not chip hull.
    /// <para>
    /// [TITAN-ORBIT] The RAM chip is grind HP/s on an empty hull; mass stays in both products:
    /// grindDps = rating × (totalMass / this hull's ComponentSize);
    /// impact = grindDps × (1 + closingSpeed / RamClosingSpeedForDouble).
    /// Empty high-level hulls stay at 1× the RAM chip — they do not take 3–7× self-chip
    /// just because they are bigger than the starter MassReference.
    /// Grind pulses while in contact and thrusting or moving — nose into the rock is not required.
    /// Same helpers as the HUD (<see cref="ShipComponentRammingSuggestions"/>).
    /// Bounce / PhysX still use <see cref="ShipMassLogic.ComputeRammingMass"/> elsewhere.
    /// </para>
    /// <para>
    /// Targets: asteroids (impact + grind) and enemy ships (impact reciprocal damage).
    /// MEGA hulls plow asteroids: first contact instantly destroys the rock and applies
    /// remaining rock Health × <see cref="MegaShipCatalog.asteroidPlowDamageMultiplier"/>
    /// (default 1) — no grind, so a field does not stall the hull.
    /// Plow impact VFX is remaining rock HP vs a mid-size rock, not hull mass or cannon scale.
    /// Hull/gem rules use <see cref="ShipDamageLogic"/>. Clients never predict this.
    /// Dead / 0-HP asteroids are ignored even if PhysX still emits a contact (phantom grind).
    /// Each asteroid impact and grind pulse broadcasts <see cref="BulletHitRpc"/> (Sequence 0)
    /// so every client applies Health on seed-hydrated rocks (not ghost-relevant) and culls
    /// kills the same way bullets do. Skipping those pulses made rams look like a tunnel
    /// through an undamaged mesh. Client VFX still throttles the explosion prefab.
    /// Grind pulses at 4 Hz (<see cref="AsteroidSettings.GrindPulseIntervalSeconds"/>):
    /// each pulse applies that interval's ship damage, then spawns one gem worth that pulse's
    /// expelled cargo.
    /// </para>
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(ShipCanonicalWrapSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipRammingCollisionDamageSystem : ISystem
    {
        /// <summary>Keep grind sticky this many ticks after the last real collision event.</summary>
        const byte MaxMissedTicks = 3;

        /// <summary>Minimum closing speed (u/s) to fire an impact pulse on contact enter.</summary>
        const float ImpactMinClosingSpeed = 0.35f;

        /// <summary>Require queue + ships.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ShipTag>();
            state.RequireForUpdate<GamePrefabs>();

            if (!SystemAPI.TryGetSingletonEntity<RamContactQueueTag>(out _))
            {
                var e = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(e, new RamContactQueueTag());
                state.EntityManager.AddBuffer<PendingRamContactElement>(e);
            }
        }

        /// <summary>
        /// Consumes pending real contacts, applies asteroid / enemy-ship damage, updates sticky
        /// grind bookkeeping, then clears the queue.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingletonBuffer<PendingRamContactElement>(out var queue))
                return;

            if (!SystemAPI.TryGetSingleton<GamePrefabs>(out var prefabs))
                return;

            Entity gemPrefab = prefabs.Gem;
            double now = SystemAPI.Time.ElapsedTime;
            float fixedDt = SystemAPI.Time.DeltaTime;
            if (fixedDt <= 0f)
                fixedDt = 1f / 60f;

            float spawnServerTime = PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(
                state.EntityManager, now);

            // --- Ensure sticky buffers on ships that still lack them ---
            // [ECS/DOTS] WithNone so we do not allocate an ECB every grind tick for ships that
            // already have the buffer (all of them after the first spawn).
            {
                bool anyMissing = false;
                var ensureEcb = new EntityCommandBuffer(Allocator.Temp);
                foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>()
                             .WithNone<ShipRamContactElement>()
                             .WithEntityAccess())
                {
                    ensureEcb.AddBuffer<ShipRamContactElement>(entity);
                    anyMissing = true;
                }

                if (anyMissing)
                    ensureEcb.Playback(state.EntityManager);
                ensureEcb.Dispose();
            }

            // --- Mark which (ship, target) pairs collided this tick ---
            var hitThisTick = new NativeHashSet<long>(math.max(8, queue.Length * 2), Allocator.Temp);

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            for (int i = 0; i < queue.Length; i++)
            {
                PendingRamContactElement pending = queue[i];
                if (!TryNormalizePair(ref state, ref pending, out Entity shipEntity, out Entity other,
                        out bool otherIsShip, out float3 normalShipFromOther))
                    continue;

                // Dead rocks must not grind the hull. HitRpc already hid the mesh on clients;
                // a 0-HP server zombie that missed DestroyEntity still raised PhysX contacts and
                // ApplyShipSelfDamage kept pulsing — fly-through + phantom ram damage.
                if (!otherIsShip && IsDeadAsteroid(ref state, other))
                    continue;

                if (!state.EntityManager.HasComponent<ShipState>(shipEntity) ||
                    !state.EntityManager.HasComponent<ShipMotorConfig>(shipEntity) ||
                    !state.EntityManager.HasComponent<ShipInput>(shipEntity) ||
                    !state.EntityManager.HasComponent<LocalTransform>(shipEntity))
                    continue;

                var ship = state.EntityManager.GetComponentData<ShipState>(shipEntity);
                if (ship.IsDead || ship.AwaitingTeamSelection)
                    continue;

                int interactNet = 0;
                if (state.EntityManager.HasComponent<GhostOwner>(shipEntity))
                    interactNet = state.EntityManager.GetComponentData<GhostOwner>(shipEntity).NetworkId;

                // --- Stowed in planetary defense turret: hull is removed from play ---
                if (state.EntityManager.HasComponent<ShipTurretControlState>(shipEntity) &&
                    state.EntityManager.GetComponentData<ShipTurretControlState>(shipEntity).IsControlling)
                    continue;

                if (IsMoonDockImmune(ref state, shipEntity))
                    continue;

                if (!state.EntityManager.HasBuffer<ShipRamContactElement>(shipEntity))
                    continue;

                var motor = state.EntityManager.GetComponentData<ShipMotorConfig>(shipEntity);
                var input = state.EntityManager.GetComponentData<ShipInput>(shipEntity);
                float3 shipPos = state.EntityManager.GetComponentData<LocalTransform>(shipEntity).Position;

                // --- Mobility totalMass + after-tax accel (same tax as ShipPhysicsDriveLogic) ---
                ResolveMobilityRamInputs(
                    in ship, in motor, out float totalMass, out float taxedAccel, out float hullMassRef);

                // [TITAN-ORBIT] Rating from ShipFamilyDefinition component rammingPower (summed +
                // Extra Level in ShipStatApplyLogic → motor.RammingPower). Fire Power purchases
                // are the ability stand-in — there is no Ramming chip. Not a flat constant.
                float familyRam = motor.RammingPower > 0.001f
                    ? motor.RammingPower
                    : ShipFamilyDefaultFallbackStats.CreateBaseline().rammingPower;
                int ramBankIndex = 0;
                if (state.EntityManager.HasComponent<ShipLoadoutState>(shipEntity))
                    ramBankIndex = BulletBankFireResolve.ResolveFireBankIndex(
                        state.EntityManager.GetComponentData<ShipLoadoutState>(shipEntity));
                familyRam *= BulletBankCombatLogic.GetRammingPowerMultiplier(ramBankIndex);
                float ramRating = ShipComponentRammingSuggestions.ComputeDamageRatingFromFamilyPower(familyRam);

                // Closing speed: measured approach preferred; impulse only as a clamped hint.
                float closing = ShipComponentRammingSuggestions.ResolveClosingSpeedForDamage(
                    pending.ClosingSpeed,
                    pending.EstimatedImpulse,
                    totalMass);

                long key = PackKey(shipEntity, other);
                hitThisTick.Add(key);
                if (otherIsShip)
                    hitThisTick.Add(PackKey(other, shipEntity));

                var contacts = state.EntityManager.GetBuffer<ShipRamContactElement>(shipEntity);
                int contactIndex = FindContact(contacts, other);
                // [TITAN-ORBIT] Impact only on a brand-new pair. WasColliding flicker (sticky miss)
                // used to re-fire impact every few ticks and spray 1-value gems.
                bool isNewContact = contactIndex < 0;

                if (contactIndex < 0)
                {
                    float pulse = ShipComponentRammingSuggestions.GrindPulseIntervalSeconds;
                    contacts.Add(new ShipRamContactElement
                    {
                        Target = other,
                        // First grind waits one pulse so contact-enter impact is the only burst.
                        NextGrindTime = now + pulse,
                        WasColliding = 0,
                        MissedTicks = 0,
                    });
                    contactIndex = contacts.Length - 1;
                }

                var contact = contacts[contactIndex];

                bool isMega = MegaShipCatalog.PlowsAsteroids
                              && state.EntityManager.HasComponent<MegaShipState>(shipEntity)
                              && state.EntityManager.GetComponentData<MegaShipState>(shipEntity).IsMega;

                // --- MEGA plow: instant kill + rock-HP hull chip, no grind ---
                // Asteroids are no match for a MEGA hull. Movement bounce/friction are skipped
                // elsewhere. Self-damage is remaining rock Health × catalog plow slider (default 1).
                if (isMega && !otherIsShip)
                {
                    if (isNewContact && !IsDeadAsteroid(ref state, other))
                    {
                        float remainingHp = 0f;
                        if (state.EntityManager.HasComponent<AsteroidState>(other))
                            remainingHp = math.max(
                                0f,
                                state.EntityManager.GetComponentData<AsteroidState>(other).Health);

                        if (remainingHp > 0.01f)
                            ApplyAsteroidDamage(ref state, other, remainingHp, ship.Team, interactNet);
                        if (IsDeadAsteroid(ref state, other))
                            AsteroidDeathPhysics.QueueStripColliders(ecb, state.EntityManager, other);

                        float plowMul = MegaShipCatalog.DefaultAsteroidPlowDamageMultiplier;
                        float plowVfxMul = MegaShipCatalog.DefaultAsteroidPlowImpactVisualScale;
                        var megaCatalog = MegaShipCatalog.Load();
                        if (megaCatalog != null)
                        {
                            plowMul = megaCatalog.GetAsteroidPlowDamageMultiplier();
                            plowVfxMul = megaCatalog.GetAsteroidPlowImpactVisualScale();
                        }

                        // Boom from remaining rock HP (damage actually applied), not hull mass
                        // or cannon fire-power. ComputePerShotScale treats HP as bullet damage
                        // vs a ref of 8 and then ×1.75 kill — mid rocks became ~12× explosions.
                        float plowVfxScale = MegaShipCatalog.ComputeAsteroidPlowImpactVisualScale(
                            remainingHp, plowVfxMul);
                        NotifyRamAsteroidHit(
                            ref state, ref ecb, shipEntity, other, normalShipFromOther,
                            math.max(remainingHp, 0.01f), ship.Team, plowVfxScale);

                        float selfDamage = MegaShipCatalog.ComputeAsteroidPlowSelfDamage(
                            remainingHp, plowMul);
                        float intensity = ShipComponentRammingSuggestions.ComputeRamImpactGemExpulsionIntensity(
                            remainingHp, selfDamage);
                        ApplyShipSelfDamage(
                            ref state, ref ship, shipEntity, selfDamage, intensity,
                            gemPrefab, shipPos, spawnServerTime, ecb, now,
                            damagerNetworkId: 0,
                            impulseXZ: new float2(normalShipFromOther.x, normalShipFromOther.z),
                            impulsePower: selfDamage,
                            sourceEntity: other);
                        state.EntityManager.SetComponentData(shipEntity, ship);
                    }

                    contact.WasColliding = 1;
                    contact.MissedTicks = 0;
                    contacts[contactIndex] = contact;
                    continue;
                }

                // --- Impact on contact enter: grindDps × (1 + closing / ram-double speed) ---
                if (isNewContact && closing >= ImpactMinClosingSpeed)
                {
                    if (!otherIsShip)
                    {
                        float asteroidDamage = ShipComponentRammingSuggestions.ComputeImpactDamage(
                            ramRating, totalMass, closing, hullMassRef);
                        float selfDamage = ShipComponentRammingSuggestions.ComputeImpactSelfDamage(
                            ramRating, totalMass, closing, hullMassRef);

                        // Gem VFX intensity only — not part of the damage product.
                        float impactForceN = (totalMass * closing) / math.max(1e-4f, fixedDt);
                        float intensity = ShipComponentRammingSuggestions.ComputeRamImpactGemExpulsionIntensity(
                            impactForceN, selfDamage);

                        ApplyAsteroidDamage(ref state, other, asteroidDamage, ship.Team, interactNet);
                        if (IsDeadAsteroid(ref state, other))
                        {
                            // [PHYSICS] Drop the hull this tick so the next physics step cannot
                            // ram an invisible 0-HP zombie while DestroyEntity is still pending.
                            AsteroidDeathPhysics.QueueStripColliders(ecb, state.EntityManager, other);
                        }
                        NotifyRamAsteroidHit(
                            ref state, ref ecb, shipEntity, other, normalShipFromOther,
                            asteroidDamage, ship.Team);
                        // [TITAN-ORBIT] Asteroid self-damage — no player damager (network id 0).
                        ApplyShipSelfDamage(
                            ref state, ref ship, shipEntity, selfDamage, intensity,
                            gemPrefab, shipPos, spawnServerTime, ecb, now,
                            damagerNetworkId: 0,
                            impulseXZ: new float2(normalShipFromOther.x, normalShipFromOther.z),
                            impulsePower: selfDamage,
                            sourceEntity: other);
                        state.EntityManager.SetComponentData(shipEntity, ship);
                    }
                    else
                    {
                        // Enemy ship: reciprocal hull damage from each ship's ramming power.
                        ApplyShipVsShipImpact(
                            ref state, shipEntity, other, closing, fixedDt,
                            gemPrefab, spawnServerTime, ecb, now);
                        ship = state.EntityManager.GetComponentData<ShipState>(shipEntity);
                        // Sticky bookkeeping on the other hull too.
                        MarkColliding(ref state, other, shipEntity, now);
                    }
                }
                else if (otherIsShip)
                {
                    MarkColliding(ref state, other, shipEntity, now);
                }

                // --- Asteroid grind: 4 Hz while in contact and moving / thrusting ---
                // [TITAN-ORBIT] Nose alignment is not required. A jammed push often has
                // velocity 0 and a glancing forward — those still grind when Thrust is held.
                // Skip if impact already killed the rock this tick (would double the kill boom).
                if (!otherIsShip &&
                    !IsDeadAsteroid(ref state, other) &&
                    ShipComponentRammingSuggestions.ShouldGrindFromMotion(
                        input.Thrust, ReadPlanarSpeed(ref state, shipEntity)))
                {
                    TryPulseAsteroidGrind(
                        ref state, ref ship, shipEntity, other, normalShipFromOther,
                        ramRating, totalMass, hullMassRef, taxedAccel,
                        shipPos, gemPrefab, spawnServerTime, ecb, now, ref contact);
                    state.EntityManager.SetComponentData(shipEntity, ship);
                }

                contact.WasColliding = 1;
                contact.MissedTicks = 0;
                contacts[contactIndex] = contact;
            }

            // --- Sticky miss / prune, or keep grinding while still overlapping ---
            // PhysX often goes quiet on a resting contact. Distance overlap keeps the 4 Hz
            // pulse going so the player does not have to yaw to generate new events.
            foreach (var (_, entity) in SystemAPI.Query<RefRO<ShipTag>>().WithEntityAccess())
            {
                if (!state.EntityManager.HasBuffer<ShipRamContactElement>(entity))
                    continue;

                var contacts = state.EntityManager.GetBuffer<ShipRamContactElement>(entity);
                for (int c = contacts.Length - 1; c >= 0; c--)
                {
                    var contact = contacts[c];
                    long key = PackKey(entity, contact.Target);
                    if (hitThisTick.Contains(key))
                        continue;

                    bool stillOnRock = !IsDeadAsteroid(ref state, contact.Target)
                                       && IsStillOverlappingAsteroid(ref state, entity, contact.Target);
                    if (stillOnRock)
                    {
                        contact.MissedTicks = 0;
                        contact.WasColliding = 1;
                        TryStickyAsteroidGrind(
                            ref state, entity, contact.Target, gemPrefab, spawnServerTime,
                            ecb, now, ref contact);
                        contacts[c] = contact;
                        continue;
                    }

                    contact.MissedTicks = (byte)math.min(255, contact.MissedTicks + 1);
                    if (contact.MissedTicks > MaxMissedTicks)
                    {
                        contacts.RemoveAt(c);
                        continue;
                    }

                    contact.WasColliding = 0;
                    contacts[c] = contact;
                }
            }

            queue.Clear();
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            hitThisTick.Dispose();
        }

        /// <summary>
        /// Ensures pending.Ship is a ship and pending.Other is asteroid or enemy ship.
        /// Swaps EntityA/B from the physics event as needed.
        /// </summary>
        static bool TryNormalizePair(
            ref SystemState state,
            ref PendingRamContactElement pending,
            out Entity shipEntity,
            out Entity other,
            out bool otherIsShip,
            out float3 normalShipFromOther)
        {
            shipEntity = Entity.Null;
            other = Entity.Null;
            otherIsShip = false;
            normalShipFromOther = pending.NormalShipFromOther;

            Entity a = pending.Ship;
            Entity b = pending.Other;
            if (a == Entity.Null || b == Entity.Null)
                return false;

            bool aShip = state.EntityManager.HasComponent<ShipTag>(a);
            bool bShip = state.EntityManager.HasComponent<ShipTag>(b);
            bool aRock = state.EntityManager.HasComponent<AsteroidTag>(a);
            bool bRock = state.EntityManager.HasComponent<AsteroidTag>(b);

            if (aShip && bRock)
            {
                shipEntity = a;
                other = b;
                otherIsShip = false;
                // Normal was A-from-B = ship-from-asteroid — already correct.
                return true;
            }

            if (bShip && aRock)
            {
                shipEntity = b;
                other = a;
                otherIsShip = false;
                // Normal was A-from-B = rock-from-ship — flip to ship-from-rock.
                normalShipFromOther = -pending.NormalShipFromOther;
                return true;
            }

            if (aShip && bShip)
            {
                // Process from A's perspective; ship-vs-ship impact applies both sides once
                // using a canonical order (lower index first) to avoid double-processing.
                if (a.Index > b.Index || (a.Index == b.Index && a.Version > b.Version))
                {
                    // Swap so we only handle each unordered pair once (A index <= B).
                    (a, b) = (b, a);
                    normalShipFromOther = -pending.NormalShipFromOther;
                }

                var shipA = state.EntityManager.GetComponentData<ShipState>(a);
                var shipB = state.EntityManager.GetComponentData<ShipState>(b);
                if (shipA.IsDead || shipB.IsDead)
                    return false;
                if (shipA.Team == TeamId.None || shipB.Team == TeamId.None)
                    return false;
                if (shipA.Team == shipB.Team)
                    return false; // friendly — bounce only, no ram damage

                shipEntity = a;
                other = b;
                otherIsShip = true;
                return true;
            }

            // Planet / gem / unrelated — ignore.
            return false;
        }

        /// <summary>Reciprocal impact damage between two enemy ships (one unordered pair).</summary>
        static void ApplyShipVsShipImpact(
            ref SystemState state,
            Entity shipA,
            Entity shipB,
            float closing,
            float fixedDt,
            Entity gemPrefab,
            float spawnServerTime,
            EntityCommandBuffer ecb,
            double now)
        {
            ApplyOneShipOffense(
                ref state, shipA, shipB, closing, fixedDt, gemPrefab, spawnServerTime, ecb, now);
            ApplyOneShipOffense(
                ref state, shipB, shipA, closing, fixedDt, gemPrefab, spawnServerTime, ecb, now);
        }

        /// <summary>Offender's ramming stats deal hull damage to the victim.</summary>
        static void ApplyOneShipOffense(
            ref SystemState state,
            Entity offender,
            Entity victim,
            float closing,
            float fixedDt,
            Entity gemPrefab,
            float spawnServerTime,
            EntityCommandBuffer ecb,
            double now)
        {
            if (!state.EntityManager.HasComponent<ShipState>(offender) ||
                !state.EntityManager.HasComponent<ShipState>(victim) ||
                !state.EntityManager.HasComponent<ShipMotorConfig>(offender) ||
                !state.EntityManager.HasComponent<LocalTransform>(victim))
                return;

            if (IsMoonDockImmune(ref state, victim))
                return;

            var offShip = state.EntityManager.GetComponentData<ShipState>(offender);
            var offMotor = state.EntityManager.GetComponentData<ShipMotorConfig>(offender);
            var vicShip = state.EntityManager.GetComponentData<ShipState>(victim);

            ResolveMobilityRamInputs(in offShip, in offMotor, out float totalMass, out _, out float hullMassRef);
            float ramPower = offMotor.RammingPower;
            int ramBankIndex = 0;
            if (state.EntityManager.HasComponent<ShipLoadoutState>(offender))
                ramBankIndex = BulletBankFireResolve.ResolveFireBankIndex(
                    state.EntityManager.GetComponentData<ShipLoadoutState>(offender));
            ramPower *= BulletBankCombatLogic.GetRammingPowerMultiplier(ramBankIndex);
            float ramRating = ShipComponentRammingSuggestions.ComputeDamageRatingFromFamilyPower(ramPower);

            float damage = ShipComponentRammingSuggestions.ComputeImpactDamage(
                ramRating, totalMass, closing, hullMassRef);

            float impactForceN = (totalMass * closing) / math.max(1e-4f, fixedDt);
            float intensity = ShipComponentRammingSuggestions.ComputeRamImpactGemExpulsionIntensity(
                impactForceN, damage);

            float3 vicPos = state.EntityManager.GetComponentData<LocalTransform>(victim).Position;
            // [TITAN-ORBIT] Credit the offender as last damager for kill stats.
            int offenderNetworkId = 0;
            if (state.EntityManager.HasComponent<GhostOwner>(offender))
                offenderNetworkId = state.EntityManager.GetComponentData<GhostOwner>(offender).NetworkId;

            float2 ramImpulse = float2.zero;
            if (state.EntityManager.HasComponent<LocalTransform>(offender))
            {
                float3 offPos = state.EntityManager.GetComponentData<LocalTransform>(offender).Position;
                if (ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                {
                    float3 off = ToroidalMapEcs.ShortestOffsetXZ(offPos, vicPos, mapW, mapH);
                    ramImpulse = new float2(off.x, off.z);
                }
                else
                {
                    ramImpulse = new float2(vicPos.x - offPos.x, vicPos.z - offPos.z);
                }
            }

            ApplyShipSelfDamage(
                ref state, ref vicShip, victim, damage, intensity,
                gemPrefab, vicPos, spawnServerTime, ecb, now,
                damagerNetworkId: offenderNetworkId,
                impulseXZ: ramImpulse,
                impulsePower: damage,
                sourceEntity: offender);
            state.EntityManager.SetComponentData(victim, vicShip);
        }

        /// <summary>
        /// Mobility totalMass + after-tax Accel — same subtractive tax as
        /// <see cref="ShipPhysicsDriveLogic"/> / the speedometer.
        /// </summary>
        /// <param name="ship">Current vitals (gems / people).</param>
        /// <param name="motor">Untaxed chassis baselines + HullMassReference (ComponentSize).</param>
        /// <param name="totalMass">Gems×mG + people×mP + size×mCS. MEGA skip-tax reports 0 (plow ignores this).</param>
        /// <param name="taxedAccel">After-tax acceleration used only for the grind push gate.</param>
        /// <param name="hullMassReference">ComponentSize used as the 1× ram mass reference.</param>
        static void ResolveMobilityRamInputs(
            in ShipState ship,
            in ShipMotorConfig motor,
            out float totalMass,
            out float taxedAccel,
            out float hullMassReference)
        {
            float baseMass = motor.Mass > 0f ? motor.Mass : ShipMassLogic.DefaultBaseMass;
            float componentSize = motor.HullMassReference > 0f
                ? motor.HullMassReference
                : math.max(ShipMassLogic.MinMass, baseMass * ShipMassLogic.HullMassScale);
            hullMassReference = componentSize;

            // [TITAN-ORBIT] Same live tax as drive / speedometer. MEGAs skip mobility tax.
            ShipMobilityResolution.TaxedMotorStats taxed = ShipMobilityResolution.ResolveLiveMotorStats(
                motor.MaxSpeed,
                motor.EngineThrust,
                motor.RotationSpeed,
                ship.CurrentGems,
                ship.CurrentPeople,
                componentSize,
                skipMassTax: motor.SkipMassTax != 0);
            totalMass = taxed.TotalMass;
            taxedAccel = taxed.EngineThrust;
        }

        /// <summary>Planar XZ speed from kinematics (solver may zero PhysicsVelocity on a jam).</summary>
        static float ReadPlanarSpeed(ref SystemState state, Entity shipEntity)
        {
            if (state.EntityManager.HasComponent<ShipKinematics>(shipEntity))
            {
                float3 v = state.EntityManager.GetComponentData<ShipKinematics>(shipEntity).Velocity;
                return math.length(new float2(v.x, v.z));
            }

            return 0f;
        }

        /// <summary>
        /// Resting overlap after PhysX events go quiet. Uses covering hull + rock radius
        /// so a jammed L6 ellipsoid still counts.
        /// </summary>
        static bool IsStillOverlappingAsteroid(ref SystemState state, Entity shipEntity, Entity asteroid)
        {
            if (!state.EntityManager.HasComponent<LocalTransform>(shipEntity) ||
                !state.EntityManager.HasComponent<LocalTransform>(asteroid))
                return false;
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return false;

            var shipLt = state.EntityManager.GetComponentData<LocalTransform>(shipEntity);
            var rockLt = state.EntityManager.GetComponentData<LocalTransform>(asteroid);
            float d = ToroidalMapEcs.ToroidalDistance(shipLt.Position, rockLt.Position, mapW, mapH);
            float shipR = ReadShipGrindRadius(ref state, shipEntity, shipLt.Scale);
            float rockR = BodyCollisionMath.GetAsteroidBodyRadiusWorld(rockLt.Scale);
            return d <= shipR + rockR + ShipComponentRammingSuggestions.GrindOverlapSkin;
        }

        static float ReadShipGrindRadius(ref SystemState state, Entity shipEntity, float transformScale)
        {
            if (state.EntityManager.HasComponent<ShipHullColliderState>(shipEntity))
            {
                var hull = state.EntityManager.GetComponentData<ShipHullColliderState>(shipEntity);
                float localR = math.max(
                    hull.AppliedCoveringRadius,
                    math.cmax(new float3(
                        hull.AppliedCoveringExtentX,
                        hull.AppliedCoveringExtentY,
                        hull.AppliedCoveringExtentZ)));
                if (localR > 0.05f)
                    return localR * math.max(0.25f, transformScale);
            }

            return BodyCollisionMath.GetShipHullRadiusWorld(transformScale);
        }

        /// <summary>
        /// Sticky-path grind: re-reads motor / input and pulses if the hull is still
        /// thrusting or sliding on this rock.
        /// </summary>
        static void TryStickyAsteroidGrind(
            ref SystemState state,
            Entity shipEntity,
            Entity asteroid,
            Entity gemPrefab,
            float spawnServerTime,
            EntityCommandBuffer ecb,
            double now,
            ref ShipRamContactElement contact)
        {
            if (!state.EntityManager.HasComponent<ShipState>(shipEntity) ||
                !state.EntityManager.HasComponent<ShipMotorConfig>(shipEntity) ||
                !state.EntityManager.HasComponent<ShipInput>(shipEntity) ||
                !state.EntityManager.HasComponent<LocalTransform>(shipEntity))
                return;

            var ship = state.EntityManager.GetComponentData<ShipState>(shipEntity);
            if (ship.IsDead || ship.AwaitingTeamSelection)
                return;
            if (state.EntityManager.HasComponent<ShipTurretControlState>(shipEntity) &&
                state.EntityManager.GetComponentData<ShipTurretControlState>(shipEntity).IsControlling)
                return;
            if (IsMoonDockImmune(ref state, shipEntity))
                return;
            if (state.EntityManager.HasComponent<MegaShipState>(shipEntity) &&
                state.EntityManager.GetComponentData<MegaShipState>(shipEntity).IsMega)
                return;

            var input = state.EntityManager.GetComponentData<ShipInput>(shipEntity);
            if (!ShipComponentRammingSuggestions.ShouldGrindFromMotion(
                    input.Thrust, ReadPlanarSpeed(ref state, shipEntity)))
                return;

            var motor = state.EntityManager.GetComponentData<ShipMotorConfig>(shipEntity);
            ResolveMobilityRamInputs(in ship, in motor, out float totalMass, out float taxedAccel, out float hullMassRef);
            float familyRam = motor.RammingPower > 0.001f
                ? motor.RammingPower
                : ShipFamilyDefaultFallbackStats.CreateBaseline().rammingPower;
            int ramBankIndex = 0;
            if (state.EntityManager.HasComponent<ShipLoadoutState>(shipEntity))
                ramBankIndex = BulletBankFireResolve.ResolveFireBankIndex(
                    state.EntityManager.GetComponentData<ShipLoadoutState>(shipEntity));
            familyRam *= BulletBankCombatLogic.GetRammingPowerMultiplier(ramBankIndex);
            float ramRating = ShipComponentRammingSuggestions.ComputeDamageRatingFromFamilyPower(familyRam);

            float3 shipPos = state.EntityManager.GetComponentData<LocalTransform>(shipEntity).Position;
            float3 normal = float3.zero;
            if (state.EntityManager.HasComponent<LocalTransform>(asteroid) &&
                ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
            {
                float3 rockPos = state.EntityManager.GetComponentData<LocalTransform>(asteroid).Position;
                float3 off = ToroidalMapEcs.ShortestOffsetXZ(rockPos, shipPos, mapW, mapH);
                off.y = 0f;
                if (math.lengthsq(off) > 1e-8f)
                    normal = math.normalize(off);
            }

            TryPulseAsteroidGrind(
                ref state, ref ship, shipEntity, asteroid, normal,
                ramRating, totalMass, hullMassRef, taxedAccel,
                shipPos, gemPrefab, spawnServerTime, ecb, now, ref contact);
            state.EntityManager.SetComponentData(shipEntity, ship);
        }

        /// <summary>One 4 Hz grind pulse if <see cref="ShipRamContactElement.NextGrindTime"/> has elapsed.</summary>
        static void TryPulseAsteroidGrind(
            ref SystemState state,
            ref ShipState ship,
            Entity shipEntity,
            Entity asteroid,
            float3 normalShipFromOther,
            float ramRating,
            float totalMass,
            float hullMassRef,
            float taxedAccel,
            float3 shipPos,
            Entity gemPrefab,
            float spawnServerTime,
            EntityCommandBuffer ecb,
            double now,
            ref ShipRamContactElement contact)
        {
            if (now < contact.NextGrindTime || IsDeadAsteroid(ref state, asteroid))
                return;

            float pulse = ShipComponentRammingSuggestions.GrindPulseIntervalSeconds;
            float asteroidPulse = ShipComponentRammingSuggestions.ComputeGrindDamagePerPulse(
                ramRating, totalMass, pulse, hullMassRef);
            float selfPulse = ShipComponentRammingSuggestions.ComputeGrindSelfDamagePerPulse(
                ramRating, totalMass, pulse, hullMassRef);
            float grindIntensity =
                ShipComponentRammingSuggestions.ComputeRamGrindGemExpulsionIntensity(
                    taxedAccel, selfPulse);

            int grindNet = 0;
            if (state.EntityManager.HasComponent<GhostOwner>(shipEntity))
                grindNet = state.EntityManager.GetComponentData<GhostOwner>(shipEntity).NetworkId;
            ApplyAsteroidDamage(ref state, asteroid, asteroidPulse, ship.Team, grindNet);
            if (IsDeadAsteroid(ref state, asteroid))
                AsteroidDeathPhysics.QueueStripColliders(ecb, state.EntityManager, asteroid);

            NotifyRamAsteroidHit(
                ref state, ref ecb, shipEntity, asteroid, normalShipFromOther,
                asteroidPulse, ship.Team);
            ApplyShipSelfDamage(
                ref state, ref ship, shipEntity, selfPulse, grindIntensity,
                gemPrefab, shipPos, spawnServerTime, ecb, now,
                damagerNetworkId: 0,
                impulseXZ: new float2(normalShipFromOther.x, normalShipFromOther.z),
                impulsePower: selfPulse,
                sourceEntity: asteroid);

            contact.NextGrindTime = now + pulse;
        }

        static bool IsMoonDockImmune(ref SystemState state, Entity shipEntity)
        {
            if (!state.EntityManager.HasComponent<ShipMoonDockState>(shipEntity))
                return false;
            var moonDock = state.EntityManager.GetComponentData<ShipMoonDockState>(shipEntity);
            return moonDock.MoonPlanetId != 0 &&
                   moonDock.LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold;
        }

        static int FindContact(DynamicBuffer<ShipRamContactElement> contacts, Entity target)
        {
            for (int i = 0; i < contacts.Length; i++)
            {
                if (contacts[i].Target == target)
                    return i;
            }

            return -1;
        }

        /// <summary>Ensures the peer ship also has a sticky contact entry for this pair.</summary>
        static void MarkColliding(ref SystemState state, Entity shipEntity, Entity target, double now)
        {
            if (!state.EntityManager.HasBuffer<ShipRamContactElement>(shipEntity))
                return;

            var contacts = state.EntityManager.GetBuffer<ShipRamContactElement>(shipEntity);
            int idx = FindContact(contacts, target);
            if (idx < 0)
            {
                contacts.Add(new ShipRamContactElement
                {
                    Target = target,
                    NextGrindTime = now,
                    WasColliding = 1,
                    MissedTicks = 0,
                });
                return;
            }

            var c = contacts[idx];
            c.WasColliding = 1;
            c.MissedTicks = 0;
            contacts[idx] = c;
        }

        static long PackKey(Entity ship, Entity other) =>
            ((long)ship.Index << 32) ^ (uint)other.Index;

        /// <summary>
        /// True when this entity is an asteroid that should no longer ram or grind the hull
        /// (already killed this tick, or a leftover 0-HP zombie waiting for DestroyEntity).
        /// </summary>
        static bool IsDeadAsteroid(ref SystemState state, Entity asteroid)
        {
            if (!state.EntityManager.Exists(asteroid))
                return true;
            if (!state.EntityManager.HasComponent<AsteroidState>(asteroid))
                return true;

            var a = state.EntityManager.GetComponentData<AsteroidState>(asteroid);
            return a.IsDestroyed || !(a.Health > 0.01f);
        }

        /// <summary>
        /// Subtracts <paramref name="damage"/> from asteroid Health and flags <c>IsDestroyed</c>
        /// at 0. No-ops on already-dead rocks. Callers then broadcast HitRpc from the new Health
        /// and must <see cref="AsteroidDeathPhysics.QueueStripColliders"/> on kill so the next
        /// physics step cannot ram a 0-HP hull.
        /// </summary>
        static void ApplyAsteroidDamage(
            ref SystemState state,
            Entity asteroid,
            float damage,
            TeamId interactTeam,
            int interactNetworkId)
        {
            if (damage <= 0.0001f || !state.EntityManager.Exists(asteroid))
                return;
            if (!state.EntityManager.HasComponent<AsteroidState>(asteroid))
                return;

            var a = state.EntityManager.GetComponentData<AsteroidState>(asteroid);
            // Already dead — caller still strips the hull if PhysX raised a leftover contact.
            if (a.IsDestroyed || !(a.Health > 0.01f))
                return;

            // --- Apply combat damage ---
            // [TITAN-ORBIT] Health is independent of RemainingGems (AsteroidSettings ratios).
            a.Health -= damage;
            a.LastInteractTeam = interactTeam;
            a.LastInteractNetworkId = interactNetworkId;
            if (a.Health <= 0f)
            {
                a.Health = 0f;
                a.IsDestroyed = true;
            }

            state.EntityManager.SetComponentData(asteroid, a);
        }

        /// <summary>
        /// Broadcasts a Sequence=0 <see cref="BulletHitRpc"/> so every client plays this ship's
        /// bullet explosion at the ram contact and applies asteroid HP (kill frames cull the hull).
        /// </summary>
        /// <param name="shipEntity">Ramming ship (bank index + cannon scale).</param>
        /// <param name="asteroid">Rock that just took damage.</param>
        /// <param name="normalShipFromOther">Contact normal pointing from the rock toward the ship.</param>
        /// <param name="asteroidDamage">Damage just applied (VFX intensity).</param>
        /// <param name="team">Ramming ship's team.</param>
        /// <param name="visualScaleOverride">
        /// When &gt; 0, use this HitRpc scale instead of cannon fire-power × kill boom.
        /// MEGA plow passes remaining-HP scale so hull mass cannot inflate the explosion.
        /// </param>
        static void NotifyRamAsteroidHit(
            ref SystemState state,
            ref EntityCommandBuffer ecb,
            Entity shipEntity,
            Entity asteroid,
            float3 normalShipFromOther,
            float asteroidDamage,
            TeamId team,
            float visualScaleOverride = -1f)
        {
            if (asteroidDamage <= 0.0001f)
                return;
            if (!state.EntityManager.Exists(asteroid) ||
                !state.EntityManager.HasComponent<AsteroidState>(asteroid) ||
                !state.EntityManager.HasComponent<LocalTransform>(asteroid))
                return;

            // --- Health after this pulse (0 = kill) ---
            var asteroidState = state.EntityManager.GetComponentData<AsteroidState>(asteroid);
            float healthAfter = asteroidState.IsDestroyed
                ? 0f
                : math.max(0f, asteroidState.Health);

            // --- Contact on the rock hull (VFX origin) ---
            // [TITAN-ORBIT] Clients apply ram HitRpc with body-radius fit, not the bullet
            // hit-sphere. Sending this surface point keeps grind flashes on the hull; HP apply
            // uses GetAsteroidBodyRadiusWorld so a packed neighbor is not culled instead.
            float3 hitPos = ComputeRamSurfaceHitPosition(
                ref state, asteroid, normalShipFromOther);

            // --- This ship's current bullet bank (B-key cycle) ---
            int bankIndex = 0;
            if (state.EntityManager.HasComponent<ShipLoadoutState>(shipEntity))
                bankIndex = BulletBankFireResolve.ResolveFireBankIndex(
                    state.EntityManager.GetComponentData<ShipLoadoutState>(shipEntity));

            float cannonScale = 1f;
            if (state.EntityManager.HasComponent<ShipWeaponConfig>(shipEntity))
            {
                float authored = state.EntityManager.GetComponentData<ShipWeaponConfig>(shipEntity).BulletScale;
                if (authored > 0.1f)
                    cannonScale = authored;
            }

            // --- Visual size from ram damage; finishing blows are a bigger boom ---
            // Override (MEGA plow) is remaining rock HP vs a mid-size rock — do not feed
            // that HP into ComputePerShotScale (ref damage 8) or the 1.75× kill boom.
            float scaleMul;
            if (visualScaleOverride > 0f)
            {
                scaleMul = visualScaleOverride;
            }
            else
            {
                scaleMul = BulletVisualScale.ComputePerShotScale(
                    cannonScale,
                    asteroidDamage,
                    0f);
                if (healthAfter <= 0.01f)
                    scaleMul *= ShipComponentRammingSuggestions.RamKillImpactVisualScale;
            }

            int ownerNet = 0;
            if (state.EntityManager.HasComponent<GhostOwner>(shipEntity))
                ownerNet = state.EntityManager.GetComponentData<GhostOwner>(shipEntity).NetworkId;

            BulletNetNotify.SendRamAsteroidHit(
                ref ecb,
                hitPos,
                asteroidDamage,
                (byte)team,
                bankIndex,
                scaleMul,
                healthAfter,
                AsteroidLayoutSlot.Read(state.EntityManager, asteroid),
                ownerNet);
        }

        /// <summary>
        /// World XZ point on the asteroid hull facing the ship. Uses the PhysX contact normal
        /// and <see cref="BodyCollisionMath.GetAsteroidBodyRadiusWorld"/>.
        /// </summary>
        static float3 ComputeRamSurfaceHitPosition(
            ref SystemState state,
            Entity asteroid,
            float3 normalShipFromOther)
        {
            var lt = state.EntityManager.GetComponentData<LocalTransform>(asteroid);
            float3 pos = lt.Position;
            pos.y = 0f;

            float3 n = normalShipFromOther;
            n.y = 0f;
            if (math.lengthsq(n) < 1e-8f)
                n = new float3(0f, 0f, 1f);
            else
                n = math.normalize(n);

            float radius = BodyCollisionMath.GetAsteroidBodyRadiusWorld(lt.Scale);
            return pos + n * radius;
        }

        /// <summary>
        /// Applies ramming / grind hull damage to one ship and optionally stamps kill attribution.
        /// Cargo spilled this pulse spawns immediately as one world gem sized to
        /// <see cref="ShipDamageLogic.Result.GemsToExpel"/> (impact and 4 Hz grind share this path).
        /// </summary>
        /// <param name="damagerNetworkId">
        /// Attacker GhostOwner.NetworkId for ship-vs-ship; 0 for asteroid self-damage (no kill credit).
        /// </param>
        static void ApplyShipSelfDamage(
            ref SystemState state,
            ref ShipState ship,
            Entity shipEntity,
            float damage,
            float expulsionIntensity,
            Entity gemPrefab,
            float3 shipPos,
            float spawnServerTime,
            EntityCommandBuffer ecb,
            double now,
            int damagerNetworkId,
            float2 impulseXZ = default,
            float impulsePower = -1f,
            Entity sourceEntity = default)
        {
            if (damage <= 0.0001f || ship.IsDead)
                return;

            float health = ship.Health;
            float gems = ship.CurrentGems;
            bool isDead = ship.IsDead;

            // --- Hull then cargo ---
            // [TITAN-ORBIT] Hull absorbs first. Asteroid self-chips spill leftover only so the
            // pulse that breaks hull does not also dump the hold at full ram damage (that
            // one-shot high-level ships once Health hit 0).
            bool asteroidSelf = damagerNetworkId == 0;
            var result = ShipDamageLogic.ApplyHullAndGemDamage(
                ref health,
                ref gems,
                ref isDead,
                CardEffectQuery.ScaleIncomingDamage(state.EntityManager, shipEntity, damage),
                ship.Team,
                TeamId.None,
                gemExpulsionPerHullDamage: ShipDamageLogic.ExcessDamageGemExpulsionPerHullDamage,
                isImmune: false,
                spillLeftoverDamageOnly: asteroidSelf);

            ship.Health = health;
            ship.CurrentGems = gems;
            ship.IsDead = isDead;

            if (result.AppliedHullDamage &&
                state.EntityManager.HasComponent<ShipVitalsState>(shipEntity))
            {
                var vitals = state.EntityManager.GetComponentData<ShipVitalsState>(shipEntity);
                vitals.LastHullDamageTime = now;
                state.EntityManager.SetComponentData(shipEntity, vitals);
            }

            // --- Kill attribution + death-impulse ---
            if (result.AppliedHullDamage || result.GemsToExpel > 0.0001f || result.BecameDead)
            {
                float power = impulsePower >= 0f ? impulsePower : damage;
                ShipMatchStatsLogic.SetLastDamager(
                    state.EntityManager,
                    shipEntity,
                    damagerNetworkId,
                    (float)now,
                    impulseXZ,
                    power,
                    sourceEntity);
            }

            if (result.GemsToExpel > 0.0001f)
            {
                // [TITAN-ORBIT] Stamp GhostOwner.NetworkId so this ship cannot reclaim spilled gems
                // until GemExplosionSettings.SelfPickupBlockSeconds elapses.
                int sourceNetworkId = 0;
                if (state.EntityManager.HasComponent<GhostOwner>(shipEntity))
                    sourceNetworkId = state.EntityManager.GetComponentData<GhostOwner>(shipEntity).NetworkId;

                // [TITAN-ORBIT] One gem per pulse/impact — value is this interval's expelled cargo.
                ShipGemExpulsion.SpawnFromDamage(
                    ecb,
                    gemPrefab,
                    shipPos,
                    result.GemsToExpel,
                    expulsionIntensity,
                    salt: (uint)(shipEntity.Index * 73856093) ^ (uint)(now * 1000.0),
                    spawnServerTime,
                    sourceNetworkId);
            }
        }
    }
}
