using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;
using TitanOrbit.Core;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Handles population transport mechanics between planets
    /// </summary>
    public class TransportSystem : NetworkBehaviour
    {
        public static TransportSystem Instance { get; private set; }

        [Header("Transport Settings")]
        [SerializeField] private float orbitRadius = 5f;
        [SerializeField] private float loadRate = 5f; // People per second
        [SerializeField] private float dropOffRate = 5f; // People per second

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

        public bool IsInOrbit(Starship ship, Planet planet)
        {
            if (ship == null || planet == null) return false;

            float distance = Vector3.Distance(ship.transform.position, planet.transform.position);
            return distance <= orbitRadius;
        }

        [ServerRpc(RequireOwnership = false)]
        public void LoadPopulationServerRpc(ulong planetNetworkId, ulong shipNetworkId, float amount)
        {
            NetworkObject planetNetObj = GetNetworkObject(planetNetworkId);
            NetworkObject shipNetObj = GetNetworkObject(shipNetworkId);

            if (planetNetObj == null || shipNetObj == null) return;

            Planet planet = planetNetObj.GetComponent<Planet>();
            Starship ship = shipNetObj.GetComponent<Starship>();

            if (planet == null || ship == null) return;
            if (!IsInOrbit(ship, planet)) return;

            // Check if planet belongs to ship's team or is neutral
            if (planet.TeamOwnership != TeamManager.Team.None && 
                planet.TeamOwnership != ship.ShipTeam) return;

            // Calculate how much can be loaded
            float peopleToLoad = Mathf.Min(
                amount * Time.deltaTime,
                planet.CurrentPopulation,
                ship.PeopleCapacity - ship.CurrentPeople
            );

            if (peopleToLoad > 0)
            {
                planet.RemovePopulationServerRpc(peopleToLoad);
                ship.AddPeopleServerRpc(peopleToLoad);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void DropOffPopulationServerRpc(ulong planetNetworkId, ulong shipNetworkId, float amount)
        {
            NetworkObject planetNetObj = GetNetworkObject(planetNetworkId);
            NetworkObject shipNetObj = GetNetworkObject(shipNetworkId);

            if (planetNetObj == null || shipNetObj == null) return;

            Planet planet = planetNetObj.GetComponent<Planet>();
            Starship ship = shipNetObj.GetComponent<Starship>();

            if (planet == null || ship == null) return;
            if (!IsInOrbit(ship, planet)) return;

            // Calculate how much can be dropped off
            float peopleToDrop = Mathf.Min(
                amount * Time.deltaTime,
                ship.CurrentPeople
            );

            if (peopleToDrop > 0)
            {
                ship.RemovePeopleServerRpc(peopleToDrop);
                planet.AddPopulationServerRpc(peopleToDrop, ship.ShipTeam);
            }
        }
    }
}
