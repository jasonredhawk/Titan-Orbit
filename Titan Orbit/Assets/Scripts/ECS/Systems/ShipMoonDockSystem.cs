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
    /// Server-authoritative moon landing and docking for friendly planets. Detects when a ship
    /// enters a moon dock zone, progresses a landing timer, pins the ship to the moon when fully
    /// landed, and toggles PhysicsMassOverride kinematic mode so physics doesn't fight the dock pose.
    /// Thrust undocks immediately. Paired with moon dock visuals in ShipMoonDockVisualApplier.
    /// Runs before GemDepositSystem so deposit logic sees a landed ship.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(GemDepositSystem))]
    public partial struct ShipMoonDockSystem : ISystem
    {
        const float LandingDurationSeconds = 1f;
        const float MaxLandingSpeed = 2.35f;
        const float MoonDockZoneMultiplier = 1.05f;
        const float ShipRadiusEstimate = 0.8f;

        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;
            double elapsed = SystemAPI.Time.ElapsedTime;
            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;
            float approachDelayRequired = GemEconomyConstants.MoonLandingApproachDelaySeconds;

            foreach (var (shipTransform, shipInput, shipKinematics, shipState, moonDock, massOverride, shipEntity) in SystemAPI
                         .Query<RefRW<LocalTransform>, RefRO<ShipInput>, RefRW<ShipKinematics>, RefRO<ShipState>, RefRW<ShipMoonDockState>, RefRW<PhysicsMassOverride>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                {
                    moonDock.ValueRW = default;
                    massOverride.ValueRW = new PhysicsMassOverride { IsKinematic = 0, SetVelocityToZero = 0 };
                    continue;
                }

                // [TITAN-ORBIT] Thrust undocks immediately — player intent overrides landing.
                if (shipInput.ValueRO.Thrust && moonDock.ValueRO.MoonPlanetId != 0)
                {
                    moonDock.ValueRW = default;
                    massOverride.ValueRW = new PhysicsMassOverride { IsKinematic = 0, SetVelocityToZero = 0 };
                    continue;
                }

                int landedPlanetId = moonDock.ValueRO.MoonPlanetId;
                float landingProgress = moonDock.ValueRO.LandingProgress;
                float approachDelay = moonDock.ValueRO.LandingApproachDelay;
                bool inMoonZone = false;

                // --- Scan friendly planets for moon dock zones ---
                if (!shipInput.ValueRO.Thrust && shipState.ValueRO.Team != TeamId.None)
                {
                    float speed = math.length(new float2(shipKinematics.ValueRO.Velocity.x, shipKinematics.ValueRO.Velocity.z));
                    TeamId shipTeam = shipState.ValueRO.Team;
                    bool disruptLanding = IsDisruptingLanding(shipInput.ValueRO, speed);

                    foreach (var (planetState, planetTransform) in SystemAPI
                                 .Query<RefRO<PlanetState>, RefRO<LocalTransform>>()
                                 .WithAll<PlanetTag>())
                    {
                        if (planetState.ValueRO.Ownership != shipTeam)
                            continue;

                        float planetSize = math.max(0.25f, planetTransform.ValueRO.Scale);
                        // [TITAN-ORBIT] Moon orbits the planet — position is time-dependent.
                        float3 moonPos = PlanetOrbitMath.GetMoonWorldPosition(
                            planetTransform.ValueRO.Position,
                            planetSize,
                            planetState.ValueRO.PlanetLevel,
                            planetState.ValueRO.PlanetId,
                            elapsed,
                            planetState.ValueRO.IsHomePlanet);

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
                        if (dist > zoneRadius)
                            continue;

                        inMoonZone = true;

                        // Switching moons resets landing progress.
                        if (landedPlanetId != 0 && landedPlanetId != planetState.ValueRO.PlanetId)
                        {
                            landingProgress = 0f;
                            approachDelay = 0f;
                        }

                        landedPlanetId = planetState.ValueRO.PlanetId;

                        if (disruptLanding)
                        {
                            approachDelay = 0f;
                        }
                        else
                        {
                            // [TITAN-ORBIT] Must coast briefly at low speed before landing completes.
                            approachDelay = math.min(approachDelayRequired, approachDelay + dt);
                            if (approachDelay >= approachDelayRequired && speed <= MaxLandingSpeed)
                                landingProgress = math.min(1f, landingProgress + dt / LandingDurationSeconds);
                        }

                        // Fully landed — snap to moon and zero velocity.
                        if (landingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold)
                        {
                            shipTransform.ValueRW.Position = moonPos;
                            shipKinematics.ValueRW = new ShipKinematics { Velocity = float3.zero };
                        }

                        break;
                    }
                }

                // Left all moon zones — reset dock state.
                if (!inMoonZone)
                {
                    landedPlanetId = 0;
                    landingProgress = 0f;
                    approachDelay = 0f;
                }

                // --- Physics override: kinematic while fully landed ---
                // [UNITY] PhysicsMassOverride prevents the solver from pushing the docked ship off the moon.
                bool fullyLanded = landingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold;
                massOverride.ValueRW = new PhysicsMassOverride
                {
                    IsKinematic = (byte)(fullyLanded ? 1 : 0),
                    SetVelocityToZero = (byte)(fullyLanded ? 1 : 0),
                };

                moonDock.ValueRW = new ShipMoonDockState
                {
                    MoonPlanetId = landedPlanetId,
                    LandingProgress = landingProgress,
                    LandingApproachDelay = approachDelay,
                };
            }
        }

        /// <summary>Firing or excessive speed resets the landing approach timer.</summary>
        static bool IsDisruptingLanding(in ShipInput input, float speed)
        {
            if (input.Fire.IsSet)
                return true;

            return speed > MaxLandingSpeed;
        }
    }
}
