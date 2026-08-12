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
    /// PhysX collision-event pairs (same-tile) plus cross-seam asteroid
    /// penetrations queued by <see cref="ShipToroidalWorldCollisionSystem"/>.
    /// No proximity skin — flying past an asteroid does not chip hull.
    /// <para>
    /// [TITAN-ORBIT] Damage uses mobility <c>totalMass</c> and after-tax motion:
    /// Impact = rating × totalMass × closingSpeed; Grind = rating × totalMass × taxedAccel × dt.
    /// Same helpers as the HUD (<see cref="ShipComponentRammingSuggestions"/>).
    /// Bounce / PhysX still use <see cref="ShipMassLogic.ComputeRammingMass"/> elsewhere.
    /// </para>
    /// <para>
    /// Targets: asteroids (impact + grind) and enemy ships (impact reciprocal damage).
    /// Hull/gem rules use <see cref="ShipDamageLogic"/>. Clients never predict this.
    /// Dead / 0-HP asteroids are ignored even if PhysX still emits a contact (phantom grind).
    /// Each asteroid impact and grind pulse broadcasts <see cref="BulletHitRpc"/> (Sequence 0)
    /// so every client plays the ship's bullet explosion and culls the rock the same way bullets do.
    /// Grind pulses at 4 Hz (<see cref="AsteroidSettings.GrindPulseIntervalSeconds"/>):
    /// each pulse applies that interval's ship damage, then spawns one gem worth that pulse's
    /// expelled cargo (four pulses → four gems per second, no banking).
    /// </para>
    /// </summary>
    [UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(ShipToroidalWorldCollisionSystem))]
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
                ResolveMobilityRamInputs(in ship, in motor, out float totalMass, out float taxedAccel);

                // [TITAN-ORBIT] Rating from ShipFamilyDefinition component rammingPower (summed +
                // level-scaled in ShipStatApplyLogic → motor.RammingPower). Not a flat constant.
                float familyRam = motor.RammingPower > 0.001f
                    ? motor.RammingPower
                    : ShipFamilyDefaultFallbackStats.CreateBaseline().rammingPower;
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

                // --- Impact on contact enter: rating × totalMass × closingSpeed ---
                if (isNewContact && closing >= ImpactMinClosingSpeed)
                {
                    if (!otherIsShip)
                    {
                        float asteroidDamage = ShipComponentRammingSuggestions.ComputeImpactDamage(
                            ramRating, totalMass, closing);
                        float selfDamage = ShipComponentRammingSuggestions.ComputeImpactSelfDamage(
                            ramRating, totalMass, closing);

                        // Gem VFX intensity only — not part of the damage product.
                        float impactForceN = (totalMass * closing) / math.max(1e-4f, fixedDt);
                        float intensity = ShipComponentRammingSuggestions.ComputeRamImpactGemExpulsionIntensity(
                            impactForceN, selfDamage);

                        ApplyAsteroidDamage(ref state, other, asteroidDamage, ship.Team);
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
                            damagerNetworkId: 0);
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

                // --- Asteroid grind: rating × totalMass × taxedAccel × pulseInterval (4 Hz) ---
                // [TITAN-ORBIT] Interval is authored on AsteroidSettings (default 0.25s).
                // Damage per pulse already × interval, so four pulses/s = four gems/s, each gem
                // sized to that pulse's expelled cargo (no bank-until-N).
                // Skip if impact already killed the rock this tick (would double the kill boom).
                if (!otherIsShip && input.Thrust && !IsDeadAsteroid(ref state, other))
                {
                    float3 forward = math.mul(
                        state.EntityManager.GetComponentData<LocalTransform>(shipEntity).Rotation,
                        new float3(0f, 0f, 1f));
                    forward.y = 0f;
                    // [TITAN-ORBIT] Gate push uses taxedAccel — same after-tax Accel as drive / grind damage.
                    float3 driveForce = float3.zero;
                    if (math.lengthsq(forward) > 1e-6f)
                        driveForce = math.normalize(forward) * math.max(0f, taxedAccel);

                    float pushN = ShipComponentRammingSuggestions.ComputeNormalPushNewtons(
                        new Vector3(normalShipFromOther.x, 0f, normalShipFromOther.z),
                        new Vector3(driveForce.x, 0f, driveForce.z));

                    if (pushN >= ShipComponentRammingSuggestions.GrindMinPushNewtons &&
                        now >= contact.NextGrindTime)
                    {
                        float pulse = ShipComponentRammingSuggestions.GrindPulseIntervalSeconds;
                        float asteroidPulse = ShipComponentRammingSuggestions.ComputeGrindDamagePerPulse(
                            ramRating, totalMass, taxedAccel, pulse);

                        float selfPulse = ShipComponentRammingSuggestions.ComputeGrindSelfDamagePerPulse(
                            ramRating, totalMass, taxedAccel, pulse);
                        float grindIntensity =
                            ShipComponentRammingSuggestions.ComputeRamGrindGemExpulsionIntensity(
                                taxedAccel, selfPulse);

                        ApplyAsteroidDamage(ref state, other, asteroidPulse, ship.Team);
                        if (IsDeadAsteroid(ref state, other))
                        {
                            // [PHYSICS] Grind kill — same hull strip as impact so the rock cannot
                            // keep generating contacts after HitRpc hid the mesh on clients.
                            AsteroidDeathPhysics.QueueStripColliders(ecb, state.EntityManager, other);
                        }
                        NotifyRamAsteroidHit(
                            ref state, ref ecb, shipEntity, other, normalShipFromOther,
                            asteroidPulse, ship.Team);
                        // [TITAN-ORBIT] Grind self-damage — environment, not a player kill.
                        // One pulse = this interval's hull/cargo damage; SpawnFromDamage emits
                        // a single gem sized to GemsToExpel for this pulse (4 Hz → 4 gems/s).
                        ApplyShipSelfDamage(
                            ref state, ref ship, shipEntity, selfPulse, grindIntensity,
                            gemPrefab, shipPos, spawnServerTime, ecb, now,
                            damagerNetworkId: 0);
                        state.EntityManager.SetComponentData(shipEntity, ship);

                        contact.NextGrindTime = now + pulse;
                    }
                }

                contact.WasColliding = 1;
                contact.MissedTicks = 0;
                contacts[contactIndex] = contact;
            }

            // --- Sticky miss / prune contacts with no collision this tick ---
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

            ResolveMobilityRamInputs(in offShip, in offMotor, out float totalMass, out _);
            float ramRating = ShipComponentRammingSuggestions.ComputeDamageRatingFromFamilyPower(
                offMotor.RammingPower);

            float damage = ShipComponentRammingSuggestions.ComputeImpactDamage(
                ramRating, totalMass, closing);

            float impactForceN = (totalMass * closing) / math.max(1e-4f, fixedDt);
            float intensity = ShipComponentRammingSuggestions.ComputeRamImpactGemExpulsionIntensity(
                impactForceN, damage);

            float3 vicPos = state.EntityManager.GetComponentData<LocalTransform>(victim).Position;
            // [TITAN-ORBIT] Credit the offender as last damager for kill stats.
            int offenderNetworkId = 0;
            if (state.EntityManager.HasComponent<GhostOwner>(offender))
                offenderNetworkId = state.EntityManager.GetComponentData<GhostOwner>(offender).NetworkId;

            ApplyShipSelfDamage(
                ref state, ref vicShip, victim, damage, intensity,
                gemPrefab, vicPos, spawnServerTime, ecb, now,
                damagerNetworkId: offenderNetworkId);
            state.EntityManager.SetComponentData(victim, vicShip);
        }

        /// <summary>
        /// Mobility totalMass + after-tax Accel — same subtractive tax as
        /// <see cref="ShipPhysicsDriveLogic"/> / the speedometer.
        /// </summary>
        /// <param name="ship">Current vitals (gems / people).</param>
        /// <param name="motor">Untaxed chassis baselines + HullMassReference (ComponentSize).</param>
        /// <param name="totalMass">Gems×mG + people×mP + size×mCS.</param>
        /// <param name="taxedAccel">After-tax acceleration used for grind damage and the push gate.</param>
        static void ResolveMobilityRamInputs(
            in ShipState ship,
            in ShipMotorConfig motor,
            out float totalMass,
            out float taxedAccel)
        {
            float baseMass = motor.Mass > 0f ? motor.Mass : ShipMassLogic.DefaultBaseMass;
            float componentSize = motor.HullMassReference > 0f
                ? motor.HullMassReference
                : math.max(ShipMassLogic.MinMass, baseMass * ShipMassLogic.HullMassScale);

            // [TITAN-ORBIT] ApplyMassTaxFromCargo reads ShipCargoMobilitySettingsCache — same asset as drive.
            ShipMobilityResolution.TaxedMotorStats taxed = ShipMobilityResolution.ApplyMassTaxFromCargo(
                motor.MaxSpeed,
                motor.EngineThrust,
                motor.RotationSpeed,
                ship.CurrentGems,
                ship.CurrentPeople,
                componentSize);
            totalMass = taxed.TotalMass;
            taxedAccel = taxed.EngineThrust;
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
        static void ApplyAsteroidDamage(ref SystemState state, Entity asteroid, float damage, TeamId interactTeam)
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
        static void NotifyRamAsteroidHit(
            ref SystemState state,
            ref EntityCommandBuffer ecb,
            Entity shipEntity,
            Entity asteroid,
            float3 normalShipFromOther,
            float asteroidDamage,
            TeamId team)
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
                bankIndex = math.max(0, state.EntityManager.GetComponentData<ShipLoadoutState>(shipEntity).RuntimeBulletIndex);

            float cannonScale = 1f;
            if (state.EntityManager.HasComponent<ShipWeaponConfig>(shipEntity))
            {
                float authored = state.EntityManager.GetComponentData<ShipWeaponConfig>(shipEntity).BulletScale;
                if (authored > 0.1f)
                    cannonScale = authored;
            }

            // --- Visual size from ram damage; finishing blows are a bigger boom ---
            float scaleMul = BulletVisualScale.ComputePerShotScale(
                cannonScale,
                asteroidDamage,
                0f);
            if (healthAfter <= 0.01f)
                scaleMul *= ShipComponentRammingSuggestions.RamKillImpactVisualScale;

            BulletNetNotify.SendRamAsteroidHit(
                ref ecb,
                hitPos,
                asteroidDamage,
                (byte)team,
                bankIndex,
                scaleMul,
                healthAfter);
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
            int damagerNetworkId)
        {
            if (damage <= 0.0001f || ship.IsDead)
                return;

            float health = ship.Health;
            float gems = ship.CurrentGems;
            bool isDead = ship.IsDead;

            // --- Hull then cargo ---
            // [TITAN-ORBIT] Hull absorbs first. Cargo spills 1:1 with leftover / post-zero damage
            // so a 4 Hz grind pulse that chips 1.5 hull+cargo becomes one gem of value 1.5 once hull is 0.
            var result = ShipDamageLogic.ApplyHullAndGemDamage(
                ref health,
                ref gems,
                ref isDead,
                damage,
                ship.Team,
                TeamId.None,
                gemExpulsionPerHullDamage: 1f,
                isImmune: false);

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

            // --- Kill attribution ---
            // [TITAN-ORBIT] Only stamp when another ship dealt the damage (network id > 0).
            if ((result.AppliedHullDamage || result.GemsToExpel > 0.0001f || result.BecameDead) &&
                damagerNetworkId > 0)
            {
                ShipMatchStatsLogic.SetLastDamager(
                    state.EntityManager,
                    shipEntity,
                    damagerNetworkId,
                    (float)now);
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
