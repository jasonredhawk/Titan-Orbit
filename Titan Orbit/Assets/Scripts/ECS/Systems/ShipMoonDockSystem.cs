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
    /// Ships stay fully dynamic — no kinematic overrides or position snapping.
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
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                {
                    moonDock.ValueRW = default;
                    continue;
                }

                if (shipInput.ValueRO.Thrust && moonDock.ValueRO.MoonPlanetId != 0)
                {
                    moonDock.ValueRW = default;
                    continue;
                }

                int landedPlanetId = moonDock.ValueRO.MoonPlanetId;
                float landingProgress = moonDock.ValueRO.LandingProgress;
                float approachDelay = moonDock.ValueRO.LandingApproachDelay;
                bool inMoonZone = false;

                if (!shipInput.ValueRO.Thrust && shipState.ValueRO.Team != TeamId.None)
                {
                    float speed = math.length(new float2(shipKinematics.ValueRO.Velocity.x, shipKinematics.ValueRO.Velocity.z));
                    bool disruptLanding = IsDisruptingLanding(shipInput.ValueRO, speed);

                    foreach (var (planetState, planetTransform) in SystemAPI
                                 .Query<RefRO<PlanetState>, RefRO<LocalTransform>>()
                                 .WithAll<PlanetTag>())
                    {
                        if (planetState.ValueRO.Ownership != shipState.ValueRO.Team)
                            continue;

                        float planetSize = math.max(0.25f, planetTransform.ValueRO.Scale);
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
                            approachDelay = math.min(approachDelayRequired, approachDelay + dt);
                            if (approachDelay >= approachDelayRequired && speed <= MaxLandingSpeed)
                                landingProgress = math.min(1f, landingProgress + dt / LandingDurationSeconds);
                        }

                        if (landingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold)
                        {
                            physicsVelocity.ValueRW = PhysicsVelocity.Zero;
                            shipKinematics.ValueRW = new ShipKinematics { Velocity = float3.zero };
                        }

                        break;
                    }
                }

                if (!inMoonZone)
                {
                    landedPlanetId = 0;
                    landingProgress = 0f;
                    approachDelay = 0f;
                }

                moonDock.ValueRW = new ShipMoonDockState
                {
                    MoonPlanetId = landedPlanetId,
                    LandingProgress = landingProgress,
                    LandingApproachDelay = approachDelay,
                };
            }
        }

        static bool IsDisruptingLanding(in ShipInput input, float speed)
        {
            if (input.Fire.IsSet)
                return true;

            return speed > MaxLandingSpeed;
        }
    }
}
