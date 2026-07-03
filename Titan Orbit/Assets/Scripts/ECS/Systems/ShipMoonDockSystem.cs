using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
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

            foreach (var (shipTransform, shipInput, shipKinematics, shipState, moonDock, shipEntity) in SystemAPI
                         .Query<RefRW<LocalTransform>, RefRO<ShipInput>, RefRW<ShipKinematics>, RefRO<ShipState>, RefRW<ShipMoonDockState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                {
                    moonDock.ValueRW = default;
                    continue;
                }

                // Thrust undocks immediately.
                if (shipInput.ValueRO.Thrust && moonDock.ValueRO.MoonPlanetId != 0)
                {
                    moonDock.ValueRW = default;
                    continue;
                }

                int landedPlanetId = moonDock.ValueRO.MoonPlanetId;
                float landingProgress = moonDock.ValueRO.LandingProgress;
                bool inMoonZone = false;

                if (!shipInput.ValueRO.Thrust && shipState.ValueRO.Team != TeamId.None)
                {
                    float speed = math.length(new float2(shipKinematics.ValueRO.Velocity.x, shipKinematics.ValueRO.Velocity.z));
                    TeamId shipTeam = shipState.ValueRO.Team;

                    foreach (var (planetState, planetTransform) in SystemAPI
                                 .Query<RefRO<PlanetState>, RefRO<LocalTransform>>()
                                 .WithAll<PlanetTag>())
                    {
                        if (planetState.ValueRO.Ownership != shipTeam)
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
                        }

                        landedPlanetId = planetState.ValueRO.PlanetId;

                        if (speed <= MaxLandingSpeed)
                        {
                            landingProgress = math.min(1f, landingProgress + dt / LandingDurationSeconds);
                        }

                        if (landingProgress >= GemEconomyConstants.MoonLandingCompleteThreshold)
                        {
                            shipTransform.ValueRW.Position = moonPos;
                            shipKinematics.ValueRW = new ShipKinematics { Velocity = float3.zero };
                        }

                        break;
                    }
                }

                if (!inMoonZone)
                {
                    landedPlanetId = 0;
                    landingProgress = 0f;
                }

                moonDock.ValueRW = new ShipMoonDockState
                {
                    MoonPlanetId = landedPlanetId,
                    LandingProgress = landingProgress,
                };
            }
        }
    }
}
