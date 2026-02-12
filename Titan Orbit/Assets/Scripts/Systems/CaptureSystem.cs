using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;
using TitanOrbit.Core;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Handles planet capture mechanics and win conditions
    /// </summary>
    public class CaptureSystem : NetworkBehaviour
    {
        public static CaptureSystem Instance { get; private set; }

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void AttemptCaptureServerRpc(ulong planetNetworkId, ulong shipNetworkId)
        {
            NetworkObject planetNetObj = GetNetworkObject(planetNetworkId);
            NetworkObject shipNetObj = GetNetworkObject(shipNetworkId);

            if (planetNetObj == null || shipNetObj == null) return;

            Planet planet = planetNetObj.GetComponent<Planet>();
            Starship ship = shipNetObj.GetComponent<Starship>();

            if (planet == null || ship == null) return;
            if (!planet.CanBeCapturedBy(ship.ShipTeam)) return;

            // Check if ship has enough people to capture
            float populationNeeded = planet.GetPopulationNeededToCapture(ship.ShipTeam);
            
            if (ship.CurrentPeople >= populationNeeded)
            {
                // Drop off enough people to capture
                float peopleToDrop = populationNeeded;
                ship.RemovePeopleServerRpc(peopleToDrop);
                planet.AddPopulationServerRpc(-peopleToDrop, ship.ShipTeam); // Negative to reduce enemy population

                // Check if capture threshold is met
                if (planet.CurrentPopulation <= 0)
                {
                    // Planet is captured
                    planet.CapturePlanetServerRpc(ship.ShipTeam);

                    // Check if it's a home planet
                    HomePlanet homePlanet = planet.GetComponent<HomePlanet>();
                    if (homePlanet != null)
                    {
                        homePlanet.OnHomePlanetCapturedServerRpc(ship.ShipTeam);
                    }

                    // Check win conditions
                    CheckWinConditionsServerRpc();
                }
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void CheckWinConditionsServerRpc()
        {
            // Find all planets
            Planet[] allPlanets = FindObjectsOfType<Planet>();
            HomePlanet[] allHomePlanets = FindObjectsOfType<HomePlanet>();

            // Check if any team has captured all planets
            foreach (TeamManager.Team team in System.Enum.GetValues(typeof(TeamManager.Team)))
            {
                if (team == TeamManager.Team.None) continue;

                bool ownsAllPlanets = true;
                bool ownsAllHomePlanets = true;

                foreach (Planet planet in allPlanets)
                {
                    if (planet.TeamOwnership != team)
                    {
                        ownsAllPlanets = false;
                        break;
                    }
                }

                foreach (HomePlanet homePlanet in allHomePlanets)
                {
                    if (homePlanet.AssignedTeam != team)
                    {
                        ownsAllHomePlanets = false;
                        break;
                    }
                }

                if (ownsAllPlanets && ownsAllHomePlanets)
                {
                    // Team wins!
                    TeamWinsClientRpc(team);
                    break;
                }
            }
        }

        [ClientRpc]
        private void TeamWinsClientRpc(TeamManager.Team winningTeam)
        {
            Debug.Log($"Team {winningTeam} wins!");
            // Handle win condition UI
        }
    }
}
