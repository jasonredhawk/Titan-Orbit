using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Projectile that beams people between planet and ship. Load: planet->ship. Unload: ship->planet.
    /// Absorbs on contact with target.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class PeopleTransportProjectile : NetworkBehaviour
    {
        private NetworkVariable<float> amount = new NetworkVariable<float>(1f);
        private NetworkVariable<ulong> targetId = new NetworkVariable<ulong>(0);
        private NetworkVariable<bool> isLoad = new NetworkVariable<bool>(true); // true = planet->ship, false = ship->planet
        private NetworkVariable<int> team = new NetworkVariable<int>((int)TeamManager.Team.None);
        private NetworkVariable<ulong> spawningShipId = new NetworkVariable<ulong>(0);
        private Rigidbody rb;
        private const float magnetSpeed = 8f;
        private const float PeopleAmountScaleMin = 1f;
        private const float PeopleAmountScaleMax = 12f;
        private const float VisualScaleMinMultiplier = 0.9f;
        private const float VisualScaleMaxMultiplier = 2.1f;
        private Vector3 baseVisualScale = Vector3.one;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            baseVisualScale = transform.localScale.sqrMagnitude > 0.0001f ? transform.localScale : Vector3.one;
        }

        public override void OnNetworkSpawn()
        {
            amount.OnValueChanged += OnAmountChanged;
            ApplyVisualScaleFromAmount(amount.Value);
        }

        private void FixedUpdate()
        {
            if (!IsServer || rb == null || amount.Value <= 0f) return;

            // When loading (planet->ship), magnetically pull toward the target ship so we track it while it orbits
            if (isLoad.Value && targetId.Value != 0)
            {
                var nm = NetworkManager.Singleton;
                if (nm != null && nm.SpawnManager.SpawnedObjects.TryGetValue(targetId.Value, out NetworkObject targetObj))
                {
                    Starship ship = targetObj.GetComponent<Starship>();
                    if (ship != null && !ship.IsDead)
                    {
                        Vector3 myPos = rb.position;
                        Vector3 shipPos = ship.transform.position;
                        Vector3 toShip = ToroidalMap.ToroidalDirection(myPos, shipPos);
                        toShip.y = 0f;
                        if (toShip.sqrMagnitude < 0.0001f) toShip = Vector3.forward;
                        else toShip.Normalize();

                        Vector3 targetVel = toShip * magnetSpeed;
                        rb.linearVelocity = Vector3.MoveTowards(rb.linearVelocity, targetVel, magnetSpeed * Time.fixedDeltaTime * 4f);
                        rb.linearDamping = 0f;
                    }
                }
            }
        }

        public void Initialize(float peopleAmount, ulong targetNetworkObjectId, bool loadingFromPlanet, TeamManager.Team sourceTeam, ulong shipNetworkObjectId = 0)
        {
            if (IsServer)
            {
                amount.Value = peopleAmount;
                targetId.Value = targetNetworkObjectId;
                isLoad.Value = loadingFromPlanet;
                team.Value = (int)sourceTeam;
                spawningShipId.Value = shipNetworkObjectId;
                if (rb != null) rb.linearDamping = 0f;
                ApplyVisualScaleFromAmount(peopleAmount);
            }
        }

        public override void OnNetworkDespawn()
        {
            amount.OnValueChanged -= OnAmountChanged;
            if (IsServer && isLoad.Value && amount.Value > 0f && targetId.Value != 0)
            {
                var nm = NetworkManager.Singleton;
                if (nm != null && nm.SpawnManager.SpawnedObjects.TryGetValue(targetId.Value, out var targetObj))
                {
                    var ship = targetObj.GetComponent<Starship>();
                    if (ship != null)
                        ship.ReleasePeopleInTransit(amount.Value);
                }
            }
            base.OnNetworkDespawn();
        }

        private void OnAmountChanged(float previousValue, float newValue)
        {
            ApplyVisualScaleFromAmount(newValue);
        }

        private void ApplyVisualScaleFromAmount(float peopleAmount)
        {
            float clampedAmount = Mathf.Clamp(Mathf.Max(0.001f, peopleAmount), PeopleAmountScaleMin, PeopleAmountScaleMax);
            float normalized = Mathf.InverseLerp(PeopleAmountScaleMin, PeopleAmountScaleMax, clampedAmount);
            float scaleMultiplier = Mathf.Lerp(VisualScaleMinMultiplier, VisualScaleMaxMultiplier, normalized);
            transform.localScale = baseVisualScale * scaleMultiplier;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!IsServer) return;
            if (amount.Value <= 0f) return;

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.SpawnManager.SpawnedObjects.TryGetValue(targetId.Value, out NetworkObject targetObj))
                return;

            if (isLoad.Value)
            {
                Starship ship = targetObj.GetComponent<Starship>();
                Starship hitShip = other.GetComponent<Starship>() ?? other.GetComponentInParent<Starship>();
                if (ship != null && hitShip == ship)
                {
                    float space = ship.PeopleCapacity - ship.CurrentPeople;
                    float toAdd = Mathf.Min(amount.Value, space);
                    if (toAdd > 0f)
                    {
                        ship.AddPeopleServerRpc(toAdd);
                        Vector3 feedbackPos = ship.transform.position;
                        feedbackPos.y = 0f;
                        ship.OnPeopleLoadArrivedFromProjectile(toAdd, (TeamManager.Team)team.Value, feedbackPos);
                        ship.ReleasePeopleInTransit(toAdd);
                        if (ScoreSystem.Instance != null)
                            ScoreSystem.Instance.AwardFriendlyLoad(ship, toAdd);
                    }
                    else
                        ship.ReleasePeopleInTransit(amount.Value);
                    amount.Value = 0f;
                    var no = GetComponent<NetworkObject>();
                    if (no != null) no.Despawn();
                }
            }
            else
            {
                Planet planet = targetObj.GetComponent<Planet>();
                if (planet != null && other.GetComponent<Planet>() == planet)
                {
                    planet.AddPopulationServerRpc(amount.Value, (TeamManager.Team)team.Value);
                    if (ScoreSystem.Instance != null && spawningShipId.Value != 0 && nm.SpawnManager.SpawnedObjects.TryGetValue(spawningShipId.Value, out var shipObj))
                    {
                        var unloader = shipObj.GetComponent<Starship>();
                        if (unloader != null)
                            ScoreSystem.Instance.AwardHostileUnload(unloader, amount.Value);
                    }
                    amount.Value = 0f;
                    var no = GetComponent<NetworkObject>();
                    if (no != null) no.Despawn();
                }
            }
        }

    }
}
