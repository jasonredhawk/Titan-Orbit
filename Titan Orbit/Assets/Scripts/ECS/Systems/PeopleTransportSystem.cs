using TitanOrbit.Core;
using TitanOrbit.Simulation;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    public static class PeopleTransportConstants
    {
        public const float OrbitDwellBeforeTransferSeconds = 1f;
        public const float TransferSpeedMultiplier = 1f;
    }

    /// <summary>
    /// Orbit-ring people load/unload (legacy Starship orbit transfer, simplified — no projectiles).
    /// Friendly: reinforce below 50% pop, load surplus above 50%. Hostile/neutral: unload to capture.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ShipMovementSystem))]
    public partial struct PeopleTransportSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            float dt = SystemAPI.Time.DeltaTime;

            var planetById = new NativeHashMap<int, Entity>(32, Allocator.Temp);
            foreach (var (planet, entity) in SystemAPI.Query<RefRO<PlanetState>>().WithAll<PlanetTag>().WithEntityAccess())
                planetById[planet.ValueRO.PlanetId] = entity;

            foreach (var (shipState, shipInput, orbit, moonDock, transferState, _, entity) in SystemAPI
                         .Query<RefRW<ShipState>, RefRO<ShipInput>, RefRO<ShipOrbitState>, RefRO<ShipMoonDockState>,
                             RefRW<ShipPeopleTransferState>, RefRO<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                ref var transfer = ref transferState.ValueRW;
                if (!CanTransferPeople(in orbit.ValueRO, in shipInput.ValueRO, in moonDock.ValueRO))
                {
                    transfer.OrbitDwellSeconds = 0f;
                    continue;
                }

                if (orbit.ValueRO.OrbitPlanetId != transfer.LastOrbitPlanetId)
                {
                    transfer.LastOrbitPlanetId = orbit.ValueRO.OrbitPlanetId;
                    transfer.OrbitDwellSeconds = 0f;
                }

                transfer.OrbitDwellSeconds += dt;
                if (transfer.OrbitDwellSeconds < PeopleTransportConstants.OrbitDwellBeforeTransferSeconds)
                    continue;

                if (!planetById.TryGetValue(orbit.ValueRO.OrbitPlanetId, out var planetEntity))
                    continue;

                var planetState = state.EntityManager.GetComponentData<PlanetState>(planetEntity);
                var planetTransform = state.EntityManager.GetComponentData<LocalTransform>(planetEntity);
                float planetSize = math.max(0.5f, planetTransform.Scale);
                int maxPop = PlanetPopulationMath.GetMaxPopulation(planetSize, planetState.PlanetLevel);
                int halfCap = math.max(1, maxPop / 2);

                int chunkRate = math.max(1, math.min(shipState.ValueRO.ShipLevel, planetState.PlanetLevel));
                int amount = math.max(1, (int)math.ceil(chunkRate * dt * PeopleTransportConstants.TransferSpeedMultiplier));

                bool friendly = shipState.ValueRO.Team != TeamId.None && planetState.Ownership == shipState.ValueRO.Team;

                if (friendly)
                {
                    if (planetState.Population < halfCap)
                        TransferShipToPlanet(ref shipState.ValueRW, ref planetState, amount, halfCap, maxPop);
                    else
                        TransferPlanetSurplusToShip(ref shipState.ValueRW, ref planetState, amount, halfCap);
                }
                else
                {
                    TransferHostileUnload(ref shipState.ValueRW, ref planetState, amount, shipState.ValueRO.Team,
                        maxPop, halfCap);
                }

                state.EntityManager.SetComponentData(planetEntity, planetState);
            }

            planetById.Dispose();
        }

        static bool CanTransferPeople(in ShipOrbitState orbit, in ShipInput input, in ShipMoonDockState moonDock)
        {
            if (!orbit.InOrbitRing || orbit.OrbitPlanetId == 0)
                return false;
            if (input.Thrust)
                return false;
            if (moonDock.MoonPlanetId != 0 && moonDock.LandingProgress > 0.01f)
                return false;
            return true;
        }

        static void TransferShipToPlanet(ref ShipState ship, ref PlanetState planet, int amount, int halfCap, int maxPop)
        {
            if (ship.CurrentPeople <= 0)
                return;

            int room = halfCap - planet.Population;
            if (room <= 0)
                return;

            int moved = math.min(amount, math.min(ship.CurrentPeople, room));
            ship.CurrentPeople -= moved;
            planet.Population = math.min(planet.Population + moved, maxPop);
        }

        static void TransferPlanetSurplusToShip(ref ShipState ship, ref PlanetState planet, int amount, int halfCap)
        {
            int surplus = planet.Population - halfCap;
            if (surplus <= 0)
                return;

            int room = ship.PeopleCapacity - ship.CurrentPeople;
            if (room <= 0)
                return;

            int moved = math.min(amount, math.min(surplus, room));
            planet.Population -= moved;
            ship.CurrentPeople += moved;
        }

        static void TransferHostileUnload(ref ShipState ship, ref PlanetState planet, int amount, TeamId team,
            int maxPop, int halfCap)
        {
            if (ship.CurrentPeople <= 0)
                return;

            int moved = math.min(amount, ship.CurrentPeople);
            ship.CurrentPeople -= moved;
            planet.Population -= moved;

            if (planet.Population > 0)
                return;

            planet.Ownership = team;
            planet.Population = halfCap;
            LogPlanetCaptured(planet.PlanetId, team);
        }

        [BurstDiscard]
        static void LogPlanetCaptured(int planetId, TeamId team)
        {
            UnityEngine.Debug.Log($"[PeopleTransport] Planet {planetId} captured by {team}.");
        }
    }
}
