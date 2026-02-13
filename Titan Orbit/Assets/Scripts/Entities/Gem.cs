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
        [SerializeField] private float lifetimeSeconds = 20f; // Time before gem expires and disappears

        private NetworkVariable<float> value = new NetworkVariable<float>(10f);
        private NetworkVariable<float> gemSize = new NetworkVariable<float>(1f); // Size multiplier (affects visual scale and value)
        private NetworkVariable<float> spawnTime = new NetworkVariable<float>(0f); // Server time when gem was spawned
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
                spawnTime.Value = (float)NetworkManager.Singleton.ServerTime.Time;
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
            // Calculate lifetime remaining (0 to 1, where 1 = full lifetime, 0 = expired)
            float lifetimeRemaining = 1f;
            if (NetworkManager.Singleton != null)
            {
                float elapsedTime = (float)NetworkManager.Singleton.ServerTime.Time - spawnTime.Value;
                lifetimeRemaining = Mathf.Clamp01(1f - (elapsedTime / lifetimeSeconds));
            }
            
            // Scale the gem visually based on size and lifetime
            // Gem shrinks as it approaches expiration
            float scale = baseScale * gemSize.Value * lifetimeRemaining;
            transform.localScale = Vector3.one * scale;
            
            // Scale pickup radius based on gem size and lifetime
            effectivePickupRadius = pickupRadius * gemSize.Value * lifetimeRemaining;
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
            // Update visual scale on all clients (for shrinking effect)
            UpdateVisualScale();
            
            if (!IsServer) return;
            if (value.Value <= 0) return;

            // Check if gem has expired
            float elapsedTime = (float)NetworkManager.Singleton.ServerTime.Time - spawnTime.Value;
            if (elapsedTime >= lifetimeSeconds)
            {
                // Gem expired - despawn it
                var no = GetComponent<NetworkObject>();
                if (no != null) no.Despawn();
                return;
            }

            // Once slowed down, stop completely (makes pickup easier)
            if (rb != null && rb.linearVelocity.magnitude < stopSpeedThreshold)
            {
                rb.linearVelocity = Vector3.zero;
                rb.linearDamping = 0f;
            }

            // Calculate effective pickup radius based on current gem size and lifetime (ensures it's always correct)
            float lifetimeRemaining = Mathf.Clamp01(1f - (elapsedTime / lifetimeSeconds));
            float currentPickupRadius = pickupRadius * gemSize.Value * lifetimeRemaining;
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
