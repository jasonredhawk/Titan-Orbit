using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-authoritative moon dock zone detection and landing progress for gem deposit UI.
    /// [TITAN-ORBIT] While fully landed, dock state latches until the player thrusts — the moon
    /// keeps orbiting the planet ring, so a pure world-space stillness check would clear landing
    /// after a few seconds (felt like the orbit ring "booting" the ship). Hull co-orbit attach
    /// lives in shared <see cref="ShipPhysicsDriveLogic"/> so client prediction matches.
    /// Fully-landed ships also ignore other friendly moon dock zones whose spheres briefly overlap
    /// when planet orbit rings cross — stealing the dock used to reset LandingProgress and replay
    /// the client grow/shrink land cinematic. Ships stowed in a planetary defense turret never
    /// accumulate dock state (pad parks under home-moon paths). MEGA hulls start landing when any
    /// part of the collider box is inside the moon orbit shell (pivot-only tests missed long ships).
    /// Thrust while fully landed starts a forced takeoff — <see cref="ShipPhysicsDriveLogic"/>
    /// drives the hull out of the moon orbit zone away from the planet; this system does not
    /// rewrite dock state during that window. Runs before <see cref="GemDepositSystem"/> so
    /// deposit sees the latest dock flags.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(GemDepositSystem))]
    public partial struct ShipMoonDockSystem : ISystem
    {
        /// <summary>Seconds of in-zone progress after approach delay to reach LandingProgress = 1.</summary>
        const float LandingDurationSeconds = 1f;

        /// <summary>Max horizontal speed (world units/s) allowed while accumulating landing progress.</summary>
        const float MaxLandingSpeed = 2.35f;

        /// <summary>Slight expand of the authored dock shell so approach feels fair at the rim.</summary>
        const float MoonDockZoneMultiplier = 1.05f;

        /// <summary>Fallback hull radius when estimating dock zone size (matches legacy dock feel).</summary>
        const float ShipRadiusEstimate = 0.8f;

        /// <summary>
        /// Each server sim tick: update who is in a friendly moon dock zone, advance landing
        /// timers, latch fully-landed ships until thrust, and mirror moon orbital velocity so
        /// post-physics velocity writes cannot freeze the hull in world space.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;

            // --- Shared orbit clock for moon dock zone center ---
            // [TITAN-ORBIT] Dock sphere must sit on the same analytic pose as colliders / visuals.
            int hz = 0;
            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                hz = tickRate.SimulationTickRate;
            double elapsed = SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                ? PlanetGemMoonOrbitClock.GetElapsedSeconds(networkTime, hz, includeTickFraction: false)
                : SystemAPI.Time.ElapsedTime;

            // Missing map period → skip moon dock this tick (never invent 1000).
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return;
            float approachDelayRequired = GemEconomyConstants.MoonLandingApproachDelaySeconds;

            foreach (var (shipTransform, shipInput, shipKinematics, shipState, moonDock, physicsVelocity, shipEntity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<ShipInput>, RefRW<ShipKinematics>, RefRO<ShipState>, RefRW<ShipMoonDockState>, RefRW<PhysicsVelocity>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                // --- Dead / team-select: clear dock ---
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                {
                    moonDock.ValueRW = default;
                    continue;
                }

                // --- Planetary defense turret possession: never moon-dock ---
                // [TITAN-ORBIT] Stow parks the hull on the pad. Home moons sweeping that zone used
                // to accumulate LandingProgress and open Orbit Menu while the player was piloting.
                if (PlanetaryDefenseTurretControlLogic.IsControllingTurret(state.EntityManager, shipEntity))
                {
                    moonDock.ValueRW = default;
                    continue;
                }

                // --- Forced takeoff owns dock state until the hull leaves the orbit zone ---
                // [TITAN-ORBIT] DriveLogic starts/advances takeoff on predicted + server ticks.
                // Do not rewrite MoonPlanetId here or we wipe TakeoffPlanetId and recapture
                // the ship in the moon/planet sandwich.
                if (moonDock.ValueRO.IsTakingOff)
                    continue;

                // --- Thrust always undocks (explicit player takeoff) ---
                if (shipInput.ValueRO.Thrust && moonDock.ValueRO.MoonPlanetId != 0)
                {
                    if (moonDock.ValueRO.IsFullyLanded)
                    {
                        moonDock.ValueRW = new ShipMoonDockState
                        {
                            TakeoffPlanetId = moonDock.ValueRO.MoonPlanetId,
                            TakeoffProgress = 0f,
                        };
                    }
                    else
                    {
                        moonDock.ValueRW = default;
                    }

                    continue;
                }

                int landedPlanetId = moonDock.ValueRO.MoonPlanetId;
                float landingProgress = moonDock.ValueRO.LandingProgress;
                float approachDelay = moonDock.ValueRO.LandingApproachDelay;

                // [TITAN-ORBIT] Once fully landed, keep dock state even if the zone check flickers —
                // the moon moves every tick; attach in ShipPhysicsDriveLogic re-centers the hull.
                bool fullyLandedLatch =
                    landedPlanetId != 0 &&
                    landingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold &&
                    !shipInput.ValueRO.Thrust;

                bool inMoonZone = false;
                float3 dockedMoonOrbitalVelocity = float3.zero;
                bool haveDockedMoonVelocity = false;

                if (!shipInput.ValueRO.Thrust && shipState.ValueRO.Team != TeamId.None)
                {
                    float speed = math.length(new float2(shipKinematics.ValueRO.Velocity.x, shipKinematics.ValueRO.Velocity.z));
                    bool disruptLanding = IsDisruptingLanding(shipInput.ValueRO, speed);

                    // Tight MEGA collider box (half-extents). Regular ships leave this false
                    // and keep the pivot + 0.8 pad. mapW/mapH from MapStateSingleton.
                    bool megaHull = MegaShipCombatAim.TryGetOverlapBoxWorld(
                        state.EntityManager,
                        shipEntity,
                        shipTransform.ValueRO,
                        out float3 hullCenter,
                        out float2 hullHalfExtents,
                        out float hullYaw);
                    float3 zoneRef = megaHull ? hullCenter : shipTransform.ValueRO.Position;

                    foreach (var (planetState, planetTransform) in SystemAPI
                                 .Query<RefRO<PlanetState>, RefRO<LocalTransform>>()
                                 .WithAll<PlanetTag>())
                    {
                        // Friendly moons only — enemy / neutral moons never accept a dock.
                        if (planetState.ValueRO.Ownership != shipState.ValueRO.Team)
                            continue;

                        // --- Fully-landed planet lock ---
                        // [TITAN-ORBIT] Moons ride planet orbit rings. When two rings cross, another
                        // friendly moon's dock sphere can briefly contain this hull. Query order used
                        // to "steal" the dock: reset LandingProgress to 0, switch MoonPlanetId, then
                        // climb 0→1 again. Client scale is 24% docked vs full flight — that replay
                        // looked like the ship growing, shrinking, and re-landing on the moon while
                        // the orbit menu stayed open (UI soft-undock hysteresis). Thrust is the only
                        // intentional undock, so ignore every other moon until then.
                        if (fullyLandedLatch && planetState.ValueRO.PlanetId != landedPlanetId)
                            continue;

                        float planetSize = math.max(0.25f, planetTransform.ValueRO.Scale);
                        // [TITAN-ORBIT] Near-tile moon — same unwrap as motor attach / combat.
                        // MEGA: unwrap next to the collider center so a long hull across a seam
                        // still sees the moon copy nearest the part that can enter the zone.
                        float3 moonPos = PlanetOrbitMath.GetMoonWorldPositionNear(
                            zoneRef,
                            planetTransform.ValueRO.Position,
                            planetSize,
                            planetState.ValueRO.PlanetLevel,
                            planetState.ValueRO.PlanetId,
                            elapsed,
                            mapW,
                            mapH);

                        bool inThisMoon = IsInMoonOrbitDockZone(
                            megaHull,
                            shipTransform.ValueRO,
                            hullCenter,
                            hullHalfExtents,
                            hullYaw,
                            moonPos,
                            mapW,
                            mapH,
                            planetSize,
                            planetState.ValueRO.IsHomePlanet);

                        // Latch path: still resolve orbital velocity for the docked planet even if
                        // the hull briefly sits outside the zone (attach runs next drive tick).
                        bool isLatchedPlanet = fullyLandedLatch && planetState.ValueRO.PlanetId == landedPlanetId;
                        if (!inThisMoon && !isLatchedPlanet)
                            continue;

                        if (inThisMoon)
                            inMoonZone = true;

                        // Planet switch only during approach — never after the fully-landed latch.
                        // (The latch skip above already blocks other moons; this guards progress.)
                        if (!fullyLandedLatch &&
                            landedPlanetId != 0 &&
                            landedPlanetId != planetState.ValueRO.PlanetId)
                        {
                            landingProgress = 0f;
                            approachDelay = 0f;
                        }

                        landedPlanetId = planetState.ValueRO.PlanetId;
                        dockedMoonOrbitalVelocity = PlanetOrbitMath.GetMoonOrbitalVelocity(
                            planetSize,
                            planetState.ValueRO.PlanetLevel,
                            planetState.ValueRO.PlanetId,
                            elapsed);
                        haveDockedMoonVelocity = true;

                        // --- Approach delay + landing progress (only while inside the zone) ---
                        // [TITAN-ORBIT] Once fully landed, never treat co-orbit speed / shield bumps as
                        // "disrupt" — zeroing LandingApproachDelay made the client cinematic drop
                        // (ship pops to full size beside the moon) while MoonPlanetId stayed set.
                        bool alreadyLanded =
                            landingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold;
                        if (inThisMoon && !alreadyLanded)
                        {
                            if (disruptLanding)
                            {
                                approachDelay = 0f;
                            }
                            else
                            {
                                approachDelay = math.min(approachDelayRequired, approachDelay + dt);
                                if (approachDelay >= approachDelayRequired && speed <= MaxLandingSpeed)
                                    landingProgress = math.min(1f, landingProgress + dt / LandingDurationSeconds);
                            }
                        }

                        break;
                    }
                }

                // --- Leave zone: clear unless fully landed (thrust is the only undock) ---
                // If the latched planet vanished from the world, drop dock so we cannot soft-lock.
                if (!inMoonZone && (!fullyLandedLatch || !haveDockedMoonVelocity))
                {
                    landedPlanetId = 0;
                    landingProgress = 0f;
                    approachDelay = 0f;
                }

                // --- Fully landed: keep kinematics matched to the moon (do not world-freeze) ---
                if (landedPlanetId != 0 &&
                    landingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold &&
                    haveDockedMoonVelocity)
                {
                    // Keep approach delay latched so ghost replication cannot starve the cinematic.
                    approachDelay = approachDelayRequired;
                    physicsVelocity.ValueRW = new PhysicsVelocity
                    {
                        Linear = dockedMoonOrbitalVelocity,
                        Angular = float3.zero,
                    };
                    shipKinematics.ValueRW = new ShipKinematics { Velocity = dockedMoonOrbitalVelocity };
                }

                moonDock.ValueRW = new ShipMoonDockState
                {
                    MoonPlanetId = landedPlanetId,
                    LandingProgress = landingProgress,
                    LandingApproachDelay = approachDelay,
                    TakeoffPlanetId = 0,
                    TakeoffProgress = 0f,
                };
            }
        }

        /// <summary>
        /// True when any part of the ship is inside this moon's orbit / dock shell.
        /// MEGA hulls use the tight yaw-aligned collider box against the drawn orbit
        /// zone (mapW/mapH from <see cref="ToroidalMapEcs"/>). Regular ships keep the
        /// legacy pivot + 0.8 radius pad.
        /// </summary>
        /// <param name="megaHull">True when <see cref="MegaShipCombatAim.TryGetHitBoxWorld"/> succeeded.</param>
        /// <param name="shipXf">Ship transform (pivot used for regular ships).</param>
        /// <param name="hullCenter">MEGA collider center already unwrapped with the moon.</param>
        /// <param name="hullHalfExtents">MEGA XZ half-extents in world units.</param>
        /// <param name="hullYaw">MEGA yaw around Y (radians).</param>
        /// <param name="moonPos">Moon center already placed on the near tile.</param>
        /// <param name="mapW">Toroidal map width from <c>MapStateSingleton</c>.</param>
        /// <param name="mapH">Toroidal map height from <c>MapStateSingleton</c>.</param>
        /// <param name="planetSize">Planet uniform scale (moon radius input).</param>
        /// <param name="isHomePlanet">Homeworlds use a larger moon / dock shell.</param>
        static bool IsInMoonOrbitDockZone(
            bool megaHull,
            in LocalTransform shipXf,
            float3 hullCenter,
            float2 hullHalfExtents,
            float hullYaw,
            float3 moonPos,
            float mapW,
            float mapH,
            float planetSize,
            bool isHomePlanet)
        {
            if (megaHull)
            {
                // Drawn moon orbit shell — no 0.8 pad and no 1.05 rim expand. The tight
                // hull box must actually reach that disc (any part inside starts landing).
                float zoneRadius = PlanetGemMoonMath.GetMoonVisualShellOuterRadiusWorld(
                    planetSize, isHomePlanet);
                float3 moonNear = hullCenter + ToroidalMapEcs.ShortestOffsetXZ(hullCenter, moonPos, mapW, mapH);
                float hullDist = BulletCollision.DistanceToOrientedBoxXZ(
                    moonNear, hullCenter, hullHalfExtents, hullYaw);
                return hullDist <= zoneRadius;
            }

            float paddedZone = PlanetGemMoonMath.GetMoonDockZoneRadiusWorld(
                planetSize,
                isHomePlanet,
                ShipRadiusEstimate,
                MoonDockZoneMultiplier);
            float dist = ToroidalMapEcs.ToroidalDistance(shipXf.Position, moonPos, mapW, mapH);
            return dist <= paddedZone;
        }

        /// <summary>
        /// True when moving too fast to count as a calm landing approach.
        /// Fire is ignored — moons sit in the planet orbit ring where weapons are locked, so
        /// holding shoot must not cancel a calm dock.
        /// </summary>
        /// <param name="input">Ship input (thrust / fire); fire is intentionally unused here.</param>
        /// <param name="speed">Current planar speed in world units/s.</param>
        /// <returns>True when landing progress should not accumulate this tick.</returns>
        static bool IsDisruptingLanding(in ShipInput input, float speed)
        {
            // [TITAN-ORBIT] Fire used to count as disruption, but BulletSimulationSystem rejects
            // shots while InOrbitRing — keeping the Fire check made moon dock feel broken when
            // the player held shoot. Speed alone decides calm approach.
            _ = input;
            return speed > MaxLandingSpeed;
        }
    }
}
