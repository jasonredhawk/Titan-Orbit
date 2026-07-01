using TitanOrbit.Core;
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
        const float MaxLandingSpeed = 2.5f;

        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;
            double elapsed = SystemAPI.Time.ElapsedTime;

            foreach (var (shipTransform, shipInput, shipKinematics, shipState, moonDock, shipEntity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<ShipInput>, RefRO<ShipKinematics>, RefRO<ShipState>, RefRW<ShipMoonDockState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                int landedPlanetId = 0;
                float landingProgress = 0f;

                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                {
                    moonDock.ValueRW = default;
                    continue;
                }

                if (!shipInput.ValueRO.Thrust)
                {
                    float speed = math.length(new float2(shipKinematics.ValueRO.Velocity.x, shipKinematics.ValueRO.Velocity.z));
                    if (speed <= MaxLandingSpeed && shipState.ValueRO.Team != TeamId.None)
                    {
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

                            float surfaceRange = PlanetGemMoonMath.GetMoonSurfaceLandingRangeWorld(
                                planetSize, planetState.ValueRO.IsHomePlanet);
                            float dist = math.distance(shipTransform.ValueRO.Position, moonPos);
                            if (dist > surfaceRange)
                                continue;

                            float previous = moonDock.ValueRO.MoonPlanetId == planetState.ValueRO.PlanetId
                                ? moonDock.ValueRO.LandingProgress
                                : 0f;
                            landedPlanetId = planetState.ValueRO.PlanetId;
                            landingProgress = math.min(1f, previous + dt / LandingDurationSeconds);
                            break;
                        }
                    }
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
