using UnityEngine;
using Unity.Netcode;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Gem pickup - spawned when asteroid is destroyed, explodes outward then stops. Collected by flying over.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Gem : NetworkBehaviour
    {
        [SerializeField] private float gemValue = 10f;
        [SerializeField] private float pickupRadius = 2f;
        [SerializeField] private float stopSpeedThreshold = 0.3f;
        [SerializeField] private float slowdownDrag = 4f;

        private NetworkVariable<float> value = new NetworkVariable<float>(10f);
        private Rigidbody rb;

        public float Value => value.Value;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                value.Value = gemValue;
                if (rb != null) rb.linearDamping = slowdownDrag;
            }
        }

        public void Initialize(float gemValue)
        {
            if (IsServer)
            {
                value.Value = gemValue;
            }
        }

        private void FixedUpdate()
        {
            if (!IsServer) return;
            if (value.Value <= 0) return;

            // Once slowed down, stop completely (makes pickup easier)
            if (rb != null && rb.linearVelocity.magnitude < stopSpeedThreshold)
            {
                rb.linearVelocity = Vector3.zero;
                rb.linearDamping = 0f;
            }

            Collider[] overlaps = Physics.OverlapSphere(transform.position, pickupRadius);
            foreach (Collider col in overlaps)
            {
                Starship ship = col.GetComponent<Starship>();
                if (ship != null && !ship.IsDead && ship.CurrentGems < ship.GemCapacity)
                {
                    float toAdd = Mathf.Min(value.Value, ship.GemCapacity - ship.CurrentGems);
                    ship.AddGemsServerRpc(toAdd);
                    value.Value -= toAdd;
                    if (value.Value <= 0)
                    {
                        var no = GetComponent<NetworkObject>();
                        if (no != null) no.Despawn();
                    }
                    return;
                }
            }
        }
    }
}
