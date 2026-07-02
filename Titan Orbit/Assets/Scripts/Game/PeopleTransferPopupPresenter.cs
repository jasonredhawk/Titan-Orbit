using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Client-side +/- popups when replicated planet population or ship crew changes.</summary>
    public class PeopleTransferPopupPresenter : MonoBehaviour
    {
        readonly Dictionary<int, int> _planetPopulation = new Dictionary<int, int>();
        readonly Dictionary<int, int> _shipPeople = new Dictionary<int, int>();
        bool _primed;

        void Update()
        {
            if (!EcsGameBridge.IsNetworkInGame())
            {
                _primed = false;
                return;
            }

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            if (!_primed)
            {
                PrimeSnapshots(em);
                _primed = true;
                return;
            }

            PollPlanets(em);
            PollShips(em);
        }

        void PrimeSnapshots(EntityManager em)
        {
            _planetPopulation.Clear();
            _shipPeople.Clear();

            using var planetQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var planetStates = planetQuery.ToComponentDataArray<PlanetState>(Allocator.Temp);
            for (int i = 0; i < planetStates.Length; i++)
                _planetPopulation[planetStates[i].PlanetId] = planetStates[i].Population;

            using var shipQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<GhostOwner>());
            using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var owners = shipQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            for (int i = 0; i < shipStates.Length; i++)
            {
                if (owners[i].NetworkId == 0)
                    continue;
                _shipPeople[owners[i].NetworkId] = shipStates[i].CurrentPeople;
            }
        }

        void PollPlanets(EntityManager em)
        {
            using var planetQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<PlanetState>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var planetStates = planetQuery.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var transforms = planetQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            for (int i = 0; i < planetStates.Length; i++)
            {
                var state = planetStates[i];
                int id = state.PlanetId;
                if (!_planetPopulation.TryGetValue(id, out int lastPop))
                    lastPop = state.Population;

                int delta = state.Population - lastPop;
                _planetPopulation[id] = state.Population;
                if (delta == 0)
                    continue;

                var team = delta > 0 ? state.Ownership : TeamId.None;
                if (team == TeamId.None && EcsGameBridge.TryGetLocalShipState(out var localShip))
                    team = localShip.Team;

                Color color = team.ToColor();
                if (delta < 0)
                    color = new Color(0.95f, 0.35f, 0.3f, 1f);

                WorldFloatingCountSpawner.SpawnPeopleDelta(transforms[i].Position, delta, color);
            }
        }

        void PollShips(EntityManager em)
        {
            using var shipQuery = em.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<GhostOwner>(),
                ComponentType.ReadOnly<LocalTransform>());
            using var shipStates = shipQuery.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var owners = shipQuery.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var transforms = shipQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            for (int i = 0; i < shipStates.Length; i++)
            {
                int networkId = owners[i].NetworkId;
                if (networkId == 0)
                    continue;

                var state = shipStates[i];
                if (!_shipPeople.TryGetValue(networkId, out int lastPeople))
                    lastPeople = state.CurrentPeople;

                int delta = state.CurrentPeople - lastPeople;
                _shipPeople[networkId] = state.CurrentPeople;
                if (delta == 0)
                    continue;

                Color color = state.Team.ToColor();
                if (delta < 0)
                    color = new Color(0.95f, 0.35f, 0.3f, 1f);

                WorldFloatingCountSpawner.SpawnPeopleDelta(transforms[i].Position, delta, color);
            }
        }
    }
}
