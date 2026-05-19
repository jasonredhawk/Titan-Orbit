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

            // Planet only gives people above 50% of max capacity (reserve floor); same rule as Starship orbit transfer.
            float effectiveAmount = amount * Time.deltaTime;
            if (GameManager.Instance != null && GameManager.Instance.DebugMode) effectiveAmount *= 100f;
            float surplusAboveHalf = Mathf.Max(0f, planet.CurrentPopulation - 0.5f * planet.MaxPopulation);
            float peopleToLoad = Mathf.Min(
                effectiveAmount,
                surplusAboveHalf,
                ship.PeopleCapacity - ship.CurrentPeople
            );

            if (peopleToLoad > 0)
            {
                planet.RemovePopulationFromServer(peopleToLoad);
                ship.AddPeopleFromServer(peopleToLoad);
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

            float effectiveAmount = amount * Time.deltaTime;
            if (GameManager.Instance != null && GameManager.Instance.DebugMode) effectiveAmount *= 100f;
            float peopleToDrop = Mathf.Min(effectiveAmount, ship.CurrentPeople);
            // Same-team planets below 50% pull crew from ships until half full; at/above half they do not take reinforcements (ships load surplus instead).
            if (planet.TeamOwnership == ship.ShipTeam && ship.ShipTeam != TeamManager.Team.None)
            {
                float halfCap = 0.5f * planet.MaxPopulation;
                float roomToHalf = Mathf.Max(0f, halfCap - planet.CurrentPopulation);
                peopleToDrop = Mathf.Min(peopleToDrop, roomToHalf);
            }

            if (peopleToDrop > 0)
            {
                ship.RemovePeopleFromServer(peopleToDrop);
                planet.AddPopulationFromServer(peopleToDrop, ship.ShipTeam);
            }
        }
    }
}
