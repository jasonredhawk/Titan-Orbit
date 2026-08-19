using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Physics;
using Unity.Transforms;
using RuntimeTriangle = TitanOrbit.Simulation.PlanetConnectionGraphLogic.RuntimeTriangle;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Shared Starblast-style planar motor for server authority and client owner prediction.
    /// [NETCODE] Identical math on both worlds inside PredictedFixedStepSimulationSystemGroup —
    /// same inputs must produce the same velocity/yaw so reconciliation stays quiet.
    /// [PHYSICS] Drive writes <see cref="PhysicsVelocity"/> and yaw only; Unity Physics integrates
    /// position and resolves hull collisions afterward. The next tick <b>reads</b> post-collision
    /// velocity (bounce is not overwritten blindly).
    /// [TITAN-ORBIT] Also detects planet orbit rings (toroidal distance), blends passive orbit
    /// velocity when coasting, writes <see cref="ShipOrbitState"/> for people-transport dwell /
    /// HUD, applies enemy moon shield repel, latches friendly-triangle speed
    /// (<c>1 + 0.05 × homePlanetLevel</c> — not a ship MovementSpeed attribute) via
    /// <see cref="ShipTerritoryBoostLatch"/>, and OVERDRIVE via ghosted
    /// <see cref="ShipState.OverdriveLockout"/> (<see cref="ShipOverdriveTuning.StepLockout"/>):
    /// burst = Shift ∧ Thrust ∧ energy &gt; 0 ∧ ¬lockout; lockout sets at energy 0 and clears at
    /// ≥25% MaxEnergy (or Shift release). Normal RMB thrust is free. When burst ends, planar
    /// speed hard-caps to the new max so speedometer / bloom stay in sync.
    /// Drain rate = <see cref="ShipMotorConfig.ThrustEnergyDrainPerSecond"/>
    /// (ExtraSpeedEnergyDrain summed across engines).
    /// MEGA hulls never engage overdrive. Shift instead locks yaw (heading stays put
    /// while the mouse moves) so unoccupied auto-guns can fire at the cursor.
    /// Live subtractive mass tax (<see cref="ShipMobilityResolution"/>) converts untaxed motor
    /// baselines into MaxSpeed / accel / turn from current gems/people + ComponentSize.
    /// While <see cref="ShipAsteroidContactState"/> reports contact from the previous physics
    /// step, inward velocity into the rock is removed so continuous thrust cannot dig the hull in
    /// (position shove from AABB spheres was tried and rejected — compound hulls over-estimate).
    /// Paired with <see cref="ShipPhysicsDriveSystem"/> and
    /// <see cref="ShipClientPredictedPhysicsDriveSystem"/>.
    /// </summary>
    public static class ShipPhysicsDriveLogic
    {
        /// <summary>
        /// Raw PIT mult must exceed this to count as "inside" (avoids float noise around 1.0).
        /// </summary>
        const float TerritoryBoostInsideEpsilon = 1.001f;

        /// <summary>
        /// Applies player input before <see cref="Unity.Physics.Systems.PhysicsSystemGroup"/>.
        /// Starts from the previous physics step's linear velocity so asteroid bounces carry forward.
        /// </summary>
        /// <param name="input">Predicted ship input for this tick.</param>
        /// <param name="motor">Designer motor caps (thrust, max speed, turn rate).</param>
        /// <param name="moonDock">
        /// Moon landing progress — co-orbits moon surface when fully landed without thrust.
        /// Thrust while fully landed writes takeoff fields and <see cref="ShipMoonTakeoffLogic"/>
        /// owns the motor until the hull is outside the moon orbit zone.
        /// </param>
        /// <param name="shipState">Death / team-select / mass contributors (HP, gems).</param>
        /// <param name="physicsVelocity">Read/write linear velocity handed to Unity Physics.</param>
        /// <param name="physicsDamping">Cleared so package damping cannot fight motor curves.</param>
        /// <param name="transform">Position (read) and yaw (write); position integration is physics-owned.</param>
        /// <param name="orbitState">Replicated orbit ring context for HUD and people transports.</param>
        /// <param name="territoryLatch">Sticky friendly-triangle mult (client + server, not ghosted).</param>
        /// <param name="asteroidContact">
        /// Previous physics step's ship↔asteroid contact (from collision events). When in contact,
        /// inward velocity along the rock normal is removed so continuous thrust cannot dig in.
        /// </param>
        /// <param name="planets">Main-thread planet snapshots shared by all ships this tick.</param>
        /// <param name="dt">Fixed prediction step delta time.</param>
        /// <param name="mapW">Toroidal map width.</param>
        /// <param name="mapH">Toroidal map height.</param>
        /// <param name="elapsedSeconds">
        /// Shared moon orbit clock (<c>PlanetGemMoonOrbitClock</c> / ServerTick seconds) for shield repel
        /// phase and territory sticky expiry.
        /// </param>
        /// <param name="territoryTriangles">Baked planet-center triangles for friendly speed boost; may be empty.</param>
        /// <param name="homeLevelByTeam">Home planet level indexed by <c>TeamId</c> byte (length ≥ 6).</param>
        /// <param name="massPerGem">From <see cref="ShipCargoMobilitySettings"/> — cargo → totalMass.</param>
        /// <param name="massPerPerson">Cargo people → totalMass.</param>
        /// <param name="massPerComponentSize">ComponentSize (HullMassReference) → totalMass.</param>
        /// <param name="speedWeightPerMass">Subtract from MaxSpeed per unit totalMass.</param>
        /// <param name="accelWeightPerMass">Subtract from accel per unit totalMass.</param>
        /// <param name="turnWeightPerMass">Subtract from turn °/s per unit totalMass.</param>
        /// <param name="minSpeed">Floor after subtractive MaxSpeed tax.</param>
        /// <param name="minAccel">Floor after subtractive accel tax.</param>
        /// <param name="minTurn">Floor after subtractive turn tax.</param>
        /// <param name="skipMassTax">True for MEGA hulls — keep chassis speed / accel / turn.</param>
        /// <param name="isMegaShip">
        /// True while <see cref="MegaShipState.IsMega"/>. Disables overdrive and treats
        /// Shift as a heading lock instead of a speed burst.
        /// </param>
        public static void Step(
            in ShipInput input,
            in ShipMotorConfig motor,
            ref ShipMoonDockState moonDock,
            ref ShipState shipState,
            ref PhysicsVelocity physicsVelocity,
            ref PhysicsDamping physicsDamping,
            ref LocalTransform transform,
            ref ShipOrbitState orbitState,
            ref ShipTerritoryBoostLatch territoryLatch,
            in ShipAsteroidContactState asteroidContact,
            in NativeArray<PlanetMotorSnapshot> planets,
            float dt,
            float mapW,
            float mapH,
            double elapsedSeconds,
            in NativeArray<RuntimeTriangle> territoryTriangles,
            in NativeArray<int> homeLevelByTeam,
            float massPerGem,
            float massPerPerson,
            float massPerComponentSize,
            float speedWeightPerMass,
            float accelWeightPerMass,
            float turnWeightPerMass,
            float minSpeed,
            float minAccel,
            float minTurn,
            bool skipMassTax = false,
            bool isMegaShip = false)
        {
            // --- Guard: fixed-step dt only ---
            if (dt <= 0f)
                return;

            // --- Dead / team select: freeze the hull ---
            if (shipState.IsDead || shipState.AwaitingTeamSelection)
            {
                physicsVelocity = PhysicsVelocity.Zero;
                physicsDamping = default;
                orbitState = default;
                ClearTerritoryBoostLatch(ref territoryLatch);
                shipState.OverdriveLockout = false;
                return;
            }

            // --- Forced moon takeoff (thrust while fully landed, or already departing) ---
            // [TITAN-ORBIT] Exit along planet→moon (away from the planet) so the hull cannot
            // stall between the moon and the planet orbit ring. Bank visuals hold 0 until
            // TakeoffPlanetId clears (outside the drawn orbit zone).
            bool startMoonTakeoff =
                moonDock.MoonPlanetId != 0 &&
                moonDock.LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold &&
                input.Thrust;
            if (moonDock.IsTakingOff || startMoonTakeoff)
            {
                if (!moonDock.IsTakingOff)
                {
                    moonDock.TakeoffPlanetId = moonDock.MoonPlanetId;
                    moonDock.TakeoffProgress = 0f;
                    moonDock.MoonPlanetId = 0;
                    moonDock.LandingProgress = 0f;
                    moonDock.LandingApproachDelay = 0f;
                }

                float takeoffSpeed = math.max(8f, motor.MaxSpeed);
                if (ShipMoonTakeoffLogic.TryApply(
                        ref moonDock,
                        ref transform,
                        ref physicsVelocity,
                        in planets,
                        dt,
                        mapW,
                        mapH,
                        elapsedSeconds,
                        takeoffSpeed,
                        isMegaShip))
                {
                    physicsDamping = default;
                    orbitState = default;
                    ClearTerritoryBoostLatch(ref territoryLatch);
                    shipState.OverdriveLockout = false;
                    return;
                }

                // Takeoff just finished — continue into normal flight this tick.
            }

            // --- Landed moon dock — co-orbit the moon until thrust undocks ---
            // [TITAN-ORBIT] Moons ride the planet orbit ring every tick. Zeroing world velocity here
            // (old behavior) left the hull parked while the moon drifted away → dock zone cleared
            // → takeoff loop that felt like the ring "booting" the ship. Instead we pin the hull to
            // the moon surface and match moon orbital velocity so Physics integration keeps pace.
            // ShipMoonDockSystem owns LandingProgress; this motor runs on server + predicted client.
            if (moonDock.MoonPlanetId != 0 &&
                moonDock.LandingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold &&
                !input.Thrust)
            {
                ApplyMoonDockAttach(
                    moonDock.MoonPlanetId,
                    ref transform,
                    ref physicsVelocity,
                    in planets,
                    mapW,
                    mapH,
                    elapsedSeconds);
                physicsDamping = default;
                orbitState = default;
                ClearTerritoryBoostLatch(ref territoryLatch);
                shipState.OverdriveLockout = false;
                return;
            }

            // --- Orbit / recoil mass scalar (not used for flight accel — tax sets accel directly) ---
            float baseMass = motor.Mass > 0f ? motor.Mass : ShipMassLogic.DefaultBaseMass;
            float movementMass = ShipMassLogic.ComputeMovementMass(
                motor.HullMassReference,
                shipState.MaxHealth,
                motor.ChassisReferenceHealth,
                shipState.CurrentGems,
                baseMass,
                shipState.CurrentPeople,
                massPerGem,
                massPerPerson);

            // --- Live subtractive mass tax (chassis baselines on motor × cargo + ComponentSize) ---
            // [TITAN-ORBIT] MaxSpeed / EngineThrust / RotationSpeed are chassis pre-tax values from
            // ShipStatApplyLogic. Collecting cargo updates Speed/Accel/Turn every tick.
            // totalMass = gems×mG + people×mP + componentSize×mCS.
            float componentSize = motor.HullMassReference > 0f
                ? motor.HullMassReference
                : math.max(ShipMassLogic.MinMass, baseMass * ShipMassLogic.HullMassScale);
            ShipMobilityResolution.TaxedMotorStats taxed;
            if (skipMassTax)
            {
                taxed = new ShipMobilityResolution.TaxedMotorStats
                {
                    MaxSpeed = motor.MaxSpeed,
                    EngineThrust = motor.EngineThrust,
                    RotationSpeed = motor.RotationSpeed,
                    TotalMass = 0f,
                };
            }
            else
            {
                float totalMass = ShipMobilityResolution.ComputeTotalMassBurst(
                    shipState.CurrentGems,
                    shipState.CurrentPeople,
                    componentSize,
                    massPerGem,
                    massPerPerson,
                    massPerComponentSize);
                taxed = ShipMobilityResolution.ApplyMassTaxBurst(
                    motor.MaxSpeed,
                    motor.EngineThrust,
                    motor.RotationSpeed,
                    totalMass,
                    speedWeightPerMass,
                    accelWeightPerMass,
                    turnWeightPerMass,
                    minSpeed,
                    minAccel,
                    minTurn);
            }

            float rotationSpeed = taxed.RotationSpeed;

            // --- Yaw: dt-capped slerp toward aim (never snap to mouse in one frame) ---
            // [TITAN-ORBIT] MEGA + Shift: lock heading. Mouse still aims unoccupied
            // auto-guns (MegaShipAutoFireSystem); the hull keeps flying the last facing.
            if (!(isMegaShip && input.Overdrive))
            {
                AimWorldPoint(in transform.Position, in transform.Rotation, in input.AimPlanarDir, out float2 aimWorldXz);
                TryRotateTowardAim(ref transform, in aimWorldXz, rotationSpeed, dt);
            }

            // --- Orbit ring detection (toroidal) ---
            // [TITAN-ORBIT] PeopleTransportDispatchSystem dwells on InOrbitRing; without this write,
            // load/unload never starts. Thrust cancels the passive orbit motor only — Fire does
            // not (weapons are locked in the ring by BulletSimulationSystem). Ring flag stays
            // true while still inside the annulus (tractor / HUD / dwell can still see it).
            // While moon-docking (approach / land), skip the orbit motor so radial pull cannot yank
            // the hull out of the dock sphere mid-landing.
            bool inOrbitRing = TryFindOrbitPlanet(
                transform.Position, mapW, mapH, in planets,
                out PlanetState orbitPlanetState, out LocalTransform orbitPlanetTransform);
            bool moonDocking = moonDock.MoonPlanetId != 0 && !input.Thrust;
            bool useOrbit = inOrbitRing && !input.Thrust && !moonDocking;

            // --- Friendly territory speed (1 + 0.05 × homeLevel) — not ship MovementSpeed attributes ---
            // [TITAN-ORBIT] Instant PIT can flicker at edges; latch matches presentation sticky so
            // client prediction + server authority keep the same cruise boost. Must match on both
            // worlds or reconciliation fights the boost.
            float rawTerritoryMult = PlanetConnectionGraphLogic.FriendlyTerritoryMovementMultiplier(
                transform.Position,
                shipState.Team,
                territoryTriangles,
                homeLevelByTeam,
                mapW,
                mapH);
            float territoryMult = ApplyTerritoryBoostLatch(
                ref territoryLatch, rawTerritoryMult, elapsedSeconds);
            // [TITAN-ORBIT] EngineThrust on motor is untaxed accel; live tax already applied above.
            float thrust = taxed.EngineThrust * territoryMult;
            float maxSpeed = taxed.MaxSpeed * territoryMult;

            // --- OVERDRIVE lockout + burst (shared predicted + server) ---
            // [TITAN-ORBIT] Ghosted OverdriveLockout — see ShipOverdriveTuning.StepLockout.
            // MEGAs have no overdrive at all: Shift is heading-lock / mouse-aim, not a speed burst.
            bool overdriveActive = false;
            if (isMegaShip)
            {
                shipState.OverdriveLockout = false;
            }
            else
            {
                ShipOverdriveTuning.StepLockout(
                    input.Overdrive,
                    shipState.CurrentEnergy,
                    shipState.MaxEnergy,
                    ref shipState.OverdriveLockout);

                overdriveActive = ShipOverdriveTuning.IsBurstActive(
                    input.Overdrive,
                    input.Thrust,
                    useOrbit,
                    shipState.CurrentEnergy,
                    shipState.OverdriveLockout);

                if (overdriveActive)
                {
                    thrust *= ShipOverdriveTuning.ResolveThrustMultiplier(motor);
                    maxSpeed *= ShipOverdriveTuning.ResolveSpeedMultiplier(motor);

                    // Energy cost is OVERDRIVE-only (normal flight regenerates / stays full).
                    if (motor.ThrustEnergyDrainPerSecond > 0f)
                    {
                        float drainMult = ShipOverdriveTuning.ResolveEnergyDrainMultiplier(motor);
                        float spend = motor.ThrustEnergyDrainPerSecond * drainMult * dt;
                        shipState.CurrentEnergy = math.max(0f, shipState.CurrentEnergy - spend);
                        // Empty this tick → lockout so bloom/speed drop immediately.
                        if (shipState.CurrentEnergy <= 0f)
                        {
                            shipState.OverdriveLockout = true;
                            overdriveActive = false;
                            // Restore cruise caps — drain ended the burst mid-tick.
                            thrust = taxed.EngineThrust * territoryMult;
                            maxSpeed = taxed.MaxSpeed * territoryMult;
                        }
                    }
                }
            }

            // --- Start from post-collision velocity (Unity Physics may have bounced us last tick) ---
            float3 vel = physicsVelocity.Linear;
            vel.y = 0f;

            if (useOrbit)
            {
                // --- Passive orbit blend (replaces thrust/coast this tick) ---
                // [TITAN-ORBIT] Continuous lerp toward the shared ring speed — Starblast pillar 3.
                // Same GetOrbitRingSpeed as gem-moon kinematics. Do NOT multiply by territoryMult:
                // friendly boost is for thrust flight only; scaling orbit made ships lap the moon.
                PlanetOrbitMath.BuildOrbitMotorParams(
                    transform.Position,
                    orbitPlanetTransform.Position,
                    orbitPlanetTransform.Scale,
                    orbitPlanetState.PlanetLevel,
                    movementMass,
                    mapW,
                    mapH,
                    out float3 desiredVel,
                    out float alignRate);
                desiredVel.y = 0f;
                float t = math.saturate(alignRate * dt);
                vel = math.lerp(vel, desiredVel, t);
                vel.y = 0f;
            }
            else
            {
                ApplyThrustCoastAndBrakes(
                    ref vel,
                    in transform.Rotation,
                    thrust,
                    maxSpeed,
                    motor.BrakeDeceleration,
                    movementMass,
                    input.Thrust,
                    !input.DisableSpaceBrakes,
                    dt,
                    hardCapToMaxSpeed: !overdriveActive);

                ApplyRecoilDecay(ref vel, maxSpeed, movementMass, motor.RecoilDecayPerSecond, dt);
            }

            // --- Enemy / neutral moon shield (deterministic; moons have no physics colliders) ---
            // [TITAN-ORBIT] Must run on client prediction + server — never client-only VFX push.
            // Moons share the ship orbit ring. Hard 8–22 kicks during passive coast made neutral/
            // enemy rings feel stepped (friendly moons skip entirely). Soften only while useOrbit;
            // thrust / moon-dock approach still get the full combat boot (Fire no longer exits orbit).
            PlanetGemMoonCombatLogic.ApplyShieldRepelIfNeeded(
                transform.Position,
                ref vel,
                shipState.Team,
                in planets,
                mapW,
                mapH,
                elapsedSeconds,
                softenForPassiveOrbit: useOrbit);

            // --- Asteroid contact: reject inward motor velocity (no position shove) ---
            // [TITAN-ORBIT] Continuous thrust into a rock used to fight PhysX and slowly dig the
            // hull in. Contact normal is from last physics step's collision events. We only remove
            // the into-rock component so slide / bounce remnant / orbit still work. Do NOT push
            // LocalTransform with AABB sphere radii — compound hulls over-estimate and shove.
            RejectInwardAsteroidVelocity(ref vel, in asteroidContact);

            vel.y = 0f;
            physicsVelocity = new PhysicsVelocity
            {
                Linear = vel,
                Angular = float3.zero,
            };

            // [PHYSICS] Motor owns cruise feel — clear package damping so it cannot fight our curves.
            physicsDamping = default;

            // --- Replicate orbit context for HUD, tractor beam, people transports ---
            // Preserve IsTransferringPeople while still locked so client prediction does not
            // wipe the server/ghost flag; clear immediately on thrust or leave.
            bool transferring = useOrbit && orbitState.IsTransferringPeople;
            orbitState = new ShipOrbitState
            {
                OrbitPlanetId = inOrbitRing ? orbitPlanetState.PlanetId : 0,
                InOrbitRing = inOrbitRing,
                UsingOrbitMotor = useOrbit,
                IsTransferringPeople = transferring,
            };
        }

        /// <summary>
        /// Sticky-holds friendly territory mult for
        /// <see cref="PlanetConnectionGraphLogic.TerritoryBoostStickySeconds"/> after exit so
        /// edge / brief PIT misses do not chop EngineThrust and MaxSpeed every tick.
        /// Same hold window as presentation <c>LocalOwnerTerritoryMult</c>.
        /// </summary>
        /// <param name="latch">Per-ship latch written this tick (predicted, not ghosted).</param>
        /// <param name="rawMult">Instant point-in-triangle result (1 or 1+0.05×homeLevel).</param>
        /// <param name="elapsedSeconds">Shared moon orbit clock used for sticky expiry.</param>
        /// <returns>Mult to apply to thrust and max speed this tick (≥ 1).</returns>
        public static float ApplyTerritoryBoostLatch(
            ref ShipTerritoryBoostLatch latch,
            float rawMult,
            double elapsedSeconds)
        {
            rawMult = math.max(1f, rawMult);

            // --- Inside friendly fill: refresh latch ---
            if (rawMult > TerritoryBoostInsideEpsilon)
            {
                latch.LatchedMult = rawMult;
                latch.HoldUntilElapsed =
                    elapsedSeconds + PlanetConnectionGraphLogic.TerritoryBoostStickySeconds;
                return rawMult;
            }

            // --- Outside: keep latched boost briefly (edge / cache flicker) ---
            if (elapsedSeconds < latch.HoldUntilElapsed &&
                latch.LatchedMult > TerritoryBoostInsideEpsilon)
            {
                return latch.LatchedMult;
            }

            ClearTerritoryBoostLatch(ref latch);
            return 1f;
        }

        /// <summary>Resets sticky territory boost (death, team select, moon dock park).</summary>
        public static void ClearTerritoryBoostLatch(ref ShipTerritoryBoostLatch latch)
        {
            latch.LatchedMult = 1f;
            latch.HoldUntilElapsed = -1.0;
        }

        /// <summary>
        /// Removes velocity into an asteroid contact normal so the motor cannot dig the hull deeper
        /// while grinding. Leaves tangential slide and outward (separating) speed untouched.
        /// </summary>
        /// <param name="vel">Planar linear velocity — inward component written to zero when contacting.</param>
        /// <param name="asteroidContact">Previous physics step contact cache (may be empty).</param>
        public static void RejectInwardAsteroidVelocity(
            ref float3 vel,
            in ShipAsteroidContactState asteroidContact)
        {
            if (asteroidContact.InContact == 0)
                return;

            float3 n = asteroidContact.OutwardNormal;
            n.y = 0f;
            if (math.lengthsq(n) < 1e-8f)
                return;
            n = math.normalize(n);

            // Negative vn = moving into the rock (opposite the outward normal).
            float vn = math.dot(vel, n);
            if (vn >= 0f)
                return;

            vel -= n * vn;
            vel.y = 0f;
        }

        /// <summary>
        /// Continuous thrust and optional space-brake deceleration on the XZ plane.
        /// Skipped entirely on ticks where passive orbit motor owns velocity.
        /// When <paramref name="spaceBrakes"/> is false and the player is not thrusting,
        /// velocity is left alone (frictionless coast — Left Ctrl / AIR BRAKES toggle).
        /// Callers pass <c>!input.DisableSpaceBrakes</c> so a zeroed command still brakes.
        /// </summary>
        /// <param name="hardCapToMaxSpeed">
        /// When true (OVERDRIVE off), clamp planar speed to <paramref name="maxSpeed"/> so
        /// post-OD overspeed does not linger via the 1.08/1.35 soft band + recoil decay.
        /// </param>
        static void ApplyThrustCoastAndBrakes(
            ref float3 vel,
            in quaternion rotation,
            float acceleration,
            float maxSpeed,
            float brakeDeceleration,
            float mass,
            bool thrust,
            bool spaceBrakes,
            float dt,
            bool hardCapToMaxSpeed = false)
        {
            mass = math.max(ShipMassLogic.MinMass, mass);
            maxSpeed = math.max(0.1f, maxSpeed);

            if (thrust)
            {
                float3 fwd = math.mul(rotation, new float3(0f, 0f, 1f));
                fwd.y = 0f;
                if (math.lengthsq(fwd) > 0.01f)
                {
                    float3 moveDirection = math.normalize(fwd);
                    float speed = math.length(vel);
                    float3 accel;
                    if (speed < maxSpeed)
                    {
                        // [TITAN-ORBIT] EngineThrust is already acceleration after mass tax — no F/m.
                        accel = moveDirection * acceleration;
                    }
                    else
                    {
                        // At cruise cap: thrust steers sideways without adding forward speed.
                        float3 velNorm = math.normalize(vel);
                        float3 accelVec = moveDirection * acceleration;
                        float alongVel = math.dot(accelVec, velNorm);
                        accel = accelVec - velNorm * math.max(0f, alongVel);
                    }

                    vel += accel * dt;
                }
            }
            else if (spaceBrakes && math.lengthsq(vel) > 0.001f)
            {
                // --- Space brakes ON (default) ---
                // [TITAN-ORBIT] Hard authored deceleration toward stop when not thrusting.
                // Toggle: Left Ctrl (desktop) or AIR BRAKES button (mobile).
                float brakeAccel = math.max(0.5f, brakeDeceleration);
                float3 brake = -math.normalize(vel) * brakeAccel * dt;
                if (math.lengthsq(brake) >= math.lengthsq(vel))
                    vel = float3.zero;
                else
                    vel += brake;
            }
            // else: DisableSpaceBrakes → frictionless coast (keep velocity; no CoastFriction).
            // PlayerInputHandler documents this as "float endlessly" when SpaceBrakesEnabled is false.

            vel.y = 0f;

            float mag = math.length(vel);

            // --- OVERDRIVE exit: snap to cruise max (bloom / speedometer sync) ---
            // [TITAN-ORBIT] Without this, OD overspeed sits in the 1.08–1.35 band and bleeds
            // slowly via recoil decay — feels like OD "lingers" after energy empties.
            if (hardCapToMaxSpeed && mag > maxSpeed)
            {
                vel = math.normalize(vel) * maxSpeed;
                return;
            }

            // --- H74 hard cruise lock (thrusting, small overspeed band only) ---
            // [TITAN-ORBIT] At cruise, speed can hunt a small band above MaxSpeed even with steady
            // forward thrust. That variance makes presentation step size wobble every frame
            // (expected = speed×dt), which reads as chop on a ~60 FPS client.
            // Lock the hunt band to MaxSpeed while thrusting. Larger overspeed (impacts) still
            // uses ApplyRecoilDecay + the soft 1.35× ceiling below — not zeroed here.
            if (thrust && mag > maxSpeed && mag <= maxSpeed * 1.08f)
                vel = math.normalize(vel) * maxSpeed;

            // Soft hard ceiling — collision overspeed above this is clipped; mid-band bleeds via recoil.
            mag = math.length(vel);
            if (mag > maxSpeed * 1.35f)
                vel = vel * ((maxSpeed * 1.35f) / mag);
        }

        /// <summary>
        /// Finds the nearest planet whose orbit ring contains the ship (toroidal distance).
        /// When multiple rings overlap, the closer planet wins.
        /// </summary>
        static bool TryFindOrbitPlanet(
            in float3 shipPos,
            float mapW,
            float mapH,
            in NativeArray<PlanetMotorSnapshot> planets,
            out PlanetState planetState,
            out LocalTransform planetTransform)
        {
            planetState = default;
            planetTransform = default;

            float bestDist = float.MaxValue;
            bool found = false;

            for (int i = 0; i < planets.Length; i++)
            {
                var snapshot = planets[i];
                var state = snapshot.Planet;
                var planetXform = snapshot.Transform;
                float planetSize = math.max(0.5f, planetXform.Scale);
                PlanetOrbitMath.GetRingRadiiWorld(planetSize, state.PlanetLevel, out float inner, out float outer, out _);

                // [TITAN-ORBIT] Toroidal distance — Euclidean would fail across map seams.
                float dist = ToroidalMapEcs.ToroidalDistance(shipPos, planetXform.Position, mapW, mapH);
                if (!PlanetOrbitMath.IsInOrbitRing(dist, inner, outer))
                    continue;

                if (dist >= bestDist)
                    continue;

                bestDist = dist;
                planetState = state;
                planetTransform = planetXform;
                found = true;
            }

            return found;
        }

        /// <summary>
        /// Looks up a planet snapshot by <see cref="PlanetState.PlanetId"/> for moon-dock attach.
        /// </summary>
        /// <returns>True when a matching planet exists in this tick's snapshot list.</returns>
        static bool TryFindPlanetById(
            int planetId,
            in NativeArray<PlanetMotorSnapshot> planets,
            out PlanetMotorSnapshot snapshot)
        {
            snapshot = default;
            if (planetId == 0)
                return false;

            for (int i = 0; i < planets.Length; i++)
            {
                if (planets[i].Planet.PlanetId != planetId)
                    continue;
                snapshot = planets[i];
                return true;
            }

            return false;
        }

        /// <summary>
        /// Pins a fully landed ship to the gem-moon surface and matches moon orbital velocity.
        /// Looks up <paramref name="moonPlanetId"/> in the per-tick snapshot list.
        /// Called from <see cref="Step"/> on both server and predicted client before Physics integrates.
        /// </summary>
        public static void ApplyMoonDockAttach(
            int moonPlanetId,
            ref LocalTransform transform,
            ref PhysicsVelocity physicsVelocity,
            in NativeArray<PlanetMotorSnapshot> planets,
            float mapW,
            float mapH,
            double elapsedSeconds)
        {
            // --- Resolve docked moon ---
            if (!TryFindPlanetById(moonPlanetId, in planets, out PlanetMotorSnapshot snapshot))
            {
                // [STANDARD] Planet missing this tick (despawn / collect race) — freeze safely.
                physicsVelocity = PhysicsVelocity.Zero;
                return;
            }

            ApplyMoonDockAttach(
                moonPlanetId,
                ref transform,
                ref physicsVelocity,
                in snapshot,
                mapW,
                mapH,
                elapsedSeconds);
        }

        /// <summary>
        /// Pins a fully landed ship using a pre-resolved planet snapshot (upgrade re-attach path).
        /// Preserves the ship's angular side of the moon; contact radius updates for the new hull size.
        /// </summary>
        public static void ApplyMoonDockAttach(
            int moonPlanetId,
            ref LocalTransform transform,
            ref PhysicsVelocity physicsVelocity,
            in PlanetMotorSnapshot snapshot,
            float mapW,
            float mapH,
            double elapsedSeconds)
        {
            if (moonPlanetId == 0 || snapshot.Planet.PlanetId != moonPlanetId)
            {
                physicsVelocity = PhysicsVelocity.Zero;
                return;
            }

            var planet = snapshot.Planet;
            var planetXform = snapshot.Transform;
            float planetSize = math.max(0.25f, planetXform.Scale);

            // [TITAN-ORBIT] Near-tile moon pose so wrap seams do not place the attach point a map away.
            float3 moonPos = PlanetOrbitMath.GetMoonWorldPositionNear(
                transform.Position,
                planetXform.Position,
                planetSize,
                planet.PlanetLevel,
                planet.PlanetId,
                elapsedSeconds,
                mapW,
                mapH);

            // --- Surface contact direction (preserve approach side on the XZ plane) ---
            float3 offset = ToroidalMapEcs.ShortestOffsetXZ(moonPos, transform.Position, mapW, mapH);
            offset.y = 0f;
            float offsetLen = math.length(offset);
            if (offsetLen < 1e-4f)
                offset = new float3(1f, 0f, 0f);
            else
                offset /= offsetLen;

            // Contact = moon body + hull radius (matches client ShipMoonDockVisualApplier standoff).
            // [TITAN-ORBIT] Flight is planar (Y = 0). Do not preserve Position.y — the kinematic
            // moon hull can push the ship upward during Physics; locking that Y made undock leave
            // the hull flying above asteroids.
            float shipRadius = BodyCollisionMath.GetShipHullRadiusWorld(transform.Scale);
            float contactRadius = math.max(0.05f, snapshot.MoonBodyRadiusWorld + shipRadius);
            float3 attachPos = moonPos + offset * contactRadius;
            attachPos.y = 0f;
            transform.Position = attachPos;

            // --- Match moon velocity so Physics does not leave the moon behind this tick ---
            float3 moonVel = PlanetOrbitMath.GetMoonOrbitalVelocity(
                planetSize,
                planet.PlanetLevel,
                planet.PlanetId,
                elapsedSeconds);
            moonVel.y = 0f;
            physicsVelocity = new PhysicsVelocity
            {
                Linear = moonVel,
                Angular = float3.zero,
            };
        }

        /// <summary>
        /// Slerps ship rotation toward the aim point, clamped by rotation speed × dt.
        /// [TITAN-ORBIT] Starblast pillar 3 — continuous turn rate; never set Rotation = LookAt in one tick.
        /// </summary>
        static void TryRotateTowardAim(
            ref LocalTransform transform,
            in float2 aimWorldXz,
            float rotationSpeedDeg,
            float dt)
        {
            float3 shipPos = transform.Position;
            float3 aimPoint = new float3(aimWorldXz.x, shipPos.y, aimWorldXz.y);
            float3 directionToAim = aimPoint - shipPos;
            directionToAim.y = 0f;
            if (math.lengthsq(directionToAim) <= 0.001f)
                return;

            directionToAim = math.normalize(directionToAim);
            quaternion targetRotation = quaternion.LookRotationSafe(directionToAim, math.up());
            float maxRadians = math.radians(math.max(0f, rotationSpeedDeg) * dt);
            float angle = math.angle(transform.Rotation, targetRotation);
            transform.Rotation = angle <= maxRadians
                ? targetRotation
                : math.slerp(transform.Rotation, targetRotation, maxRadians / math.max(angle, 1e-6f));
        }

        /// <summary>
        /// Bleeds temporary overspeed from recoil / impact bounces back toward MaxSpeed.
        /// </summary>
        static void ApplyRecoilDecay(
            ref float3 vel,
            float maxSpeed,
            float mass,
            float recoilDecayPerSecond,
            float dt)
        {
            float mag = math.length(vel);
            if (mag <= maxSpeed || maxSpeed <= 0.001f)
                return;

            float decay = recoilDecayPerSecond > 0f ? recoilDecayPerSecond : 6f;
            float effectiveRecoilDecay = decay / math.max(ShipMassLogic.MinMass, mass);
            float targetMag = math.clamp(mag - effectiveRecoilDecay * dt, maxSpeed, mag);
            vel = math.normalize(vel) * targetMag;
        }

        /// <summary>
        /// Builds a world aim point from stick/mouse planar direction, or falls back to current facing.
        /// </summary>
        static void AimWorldPoint(
            in float3 shipPos,
            in quaternion rot,
            in float2 aimPlanarDir,
            out float2 aimWorldXz)
        {
            if (math.lengthsq(aimPlanarDir) > 0.01f)
            {
                float2 dir = math.normalize(aimPlanarDir);
                aimWorldXz = shipPos.xz + dir * 100f;
                return;
            }

            float3 forward = math.mul(rot, new float3(0f, 0f, 1f));
            forward.y = 0f;
            if (math.lengthsq(forward) < 0.0001f)
                forward = new float3(0f, 0f, 1f);
            else
                forward = math.normalize(forward);

            aimWorldXz = shipPos.xz + forward.xz * 100f;
        }
    }
}
