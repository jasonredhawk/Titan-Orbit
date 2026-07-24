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
    /// Runs before <see cref="GemDepositSystem"/> so deposit sees the latest dock flags.
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

            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;
            float approachDelayRequired = GemEconomyConstants.MoonLandingApproachDelaySeconds;

            foreach (var (shipTransform, shipInput, shipKinematics, shipState, moonDock, physicsVelocity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<ShipInput>, RefRW<ShipKinematics>, RefRO<ShipState>, RefRW<ShipMoonDockState>, RefRW<PhysicsVelocity>>()
                         .WithAll<ShipTag>())
            {
                // --- Dead / team-select: clear dock ---
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                {
                    moonDock.ValueRW = default;
                    continue;
                }

                // --- Thrust always undocks (explicit player takeoff) ---
                if (shipInput.ValueRO.Thrust && moonDock.ValueRO.MoonPlanetId != 0)
                {
                    moonDock.ValueRW = default;
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

                    foreach (var (planetState, planetTransform) in SystemAPI
                                 .Query<RefRO<PlanetState>, RefRO<LocalTransform>>()
                                 .WithAll<PlanetTag>())
                    {
                        // Friendly moons only — enemy / neutral moons never accept a dock.
                        if (planetState.ValueRO.Ownership != shipState.ValueRO.Team)
                            continue;

                        float planetSize = math.max(0.25f, planetTransform.ValueRO.Scale);
                        // [TITAN-ORBIT] Near-tile moon — same unwrap as motor attach / combat.
                        float3 moonPos = PlanetOrbitMath.GetMoonWorldPositionNear(
                            shipTransform.ValueRO.Position,
                            planetTransform.ValueRO.Position,
                            planetSize,
                            planetState.ValueRO.PlanetLevel,
                            planetState.ValueRO.PlanetId,
                            elapsed,
                            mapW,
                            mapH);

                        float zoneRadius = PlanetGemMoonMath.GetMoonDockZoneRadiusWorld(
                            planetSize,
                            planetState.ValueRO.IsHomePlanet,
                            ShipRadiusEstimate,
                            MoonDockZoneMultiplier);
                        float dist = ToroidalMapEcs.ToroidalDistance(
                            shipTransform.ValueRO.Position,
                            moonPos,
                            mapW,
                            mapH);

                        // Latch path: still resolve orbital velocity for the docked planet even if
                        // the hull briefly sits outside the zone (attach runs next drive tick).
                        bool isLatchedPlanet = fullyLandedLatch && planetState.ValueRO.PlanetId == landedPlanetId;
                        if (dist > zoneRadius && !isLatchedPlanet)
                            continue;

                        if (dist <= zoneRadius)
                            inMoonZone = true;

                        if (landedPlanetId != 0 && landedPlanetId != planetState.ValueRO.PlanetId)
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
                        if (dist <= zoneRadius && !alreadyLanded)
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
                };
            }
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
