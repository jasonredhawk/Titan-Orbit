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
        [SerializeField] private float baseScale = 0.5f; // Base visual scale

        private NetworkVariable<float> value = new NetworkVariable<float>(10f);
        private NetworkVariable<float> gemSize = new NetworkVariable<float>(1f); // Size multiplier (affects visual scale and value)
        private Rigidbody rb;
        private float effectivePickupRadius; // Scaled pickup radius based on gem size

        public float Value => value.Value;
        public float GemSize => gemSize.Value;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            effectivePickupRadius = pickupRadius;
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                value.Value = gemValue;
                if (rb != null) rb.linearDamping = slowdownDrag;
            }
            
            // Update visual scale based on gem size (client-side)
            gemSize.OnValueChanged += OnGemSizeChanged;
            UpdateVisualScale();
        }

        public override void OnNetworkDespawn()
        {
            gemSize.OnValueChanged -= OnGemSizeChanged;
            base.OnNetworkDespawn();
        }

        private void OnGemSizeChanged(float previousSize, float newSize)
        {
            UpdateVisualScale();
        }

        private void UpdateVisualScale()
        {
            // Scale the gem visually based on size
            float scale = baseScale * gemSize.Value;
            transform.localScale = Vector3.one * scale;
            
            // Scale pickup radius based on gem size (larger gems are easier to pick up)
            effectivePickupRadius = pickupRadius * gemSize.Value;
        }

        public void Initialize(float gemValue, float sizeMultiplier = 1f)
        {
            if (IsServer)
            {
                gemSize.Value = sizeMultiplier;
                // Value is already calculated correctly in GemSpawner (includes size multiplier)
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

            // Calculate effective pickup radius based on current gem size (ensures it's always correct)
            float currentPickupRadius = pickupRadius * gemSize.Value;
            Collider[] overlaps = Physics.OverlapSphere(transform.position, currentPickupRadius);
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
