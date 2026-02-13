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
        [SerializeField] private float baseScale = 0.5f; // Base visual scale (multiplied by asteroid size so gem ≈ half asteroid)
        [SerializeField] private float visualScaleMultiplier = 2f; // Scale all gems up so smallest (value 1) is visible
        [SerializeField] private float lifetimeSeconds = 20f; // Time before gem expires and disappears
        [SerializeField] private float shrinkDuration = 3f; // Shrink from full to zero over this many seconds at end of life
        [SerializeField] private float magnetSpeed = 8f; // Speed when moving toward ship
        [SerializeField] private float collectRadius = 0.6f; // Collect when gem is this close to ship

        private NetworkVariable<float> value = new NetworkVariable<float>(10f);
        private NetworkVariable<float> gemSize = new NetworkVariable<float>(1f); // Size multiplier (affects visual scale and value)
        private NetworkVariable<float> asteroidPhysicalSize = new NetworkVariable<float>(0.5f); // Asteroid scale for "half asteroid" gem size
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
            // Shrink only in the last shrinkDuration seconds (e.g. 3 sec)
            float lifetimeRemaining = 1f;
            if (NetworkManager.Singleton != null)
            {
                float elapsedTime = (float)NetworkManager.Singleton.ServerTime.Time - spawnTime.Value;
                if (elapsedTime >= lifetimeSeconds - shrinkDuration)
                    lifetimeRemaining = Mathf.Clamp01((lifetimeSeconds - elapsedTime) / shrinkDuration);
            }
            
            // Gem scale ≈ half the asteroid size, then by gem size multiplier, lifetime, and global scale-up
            float scale = baseScale * asteroidPhysicalSize.Value * gemSize.Value * lifetimeRemaining * visualScaleMultiplier;
            transform.localScale = Vector3.one * scale;
            
            // Scale pickup radius based on gem size and lifetime
            effectivePickupRadius = pickupRadius * gemSize.Value * lifetimeRemaining;
        }

        public void Initialize(float gemValue, float sizeMultiplier = 1f, float asteroidScale = 0.5f)
        {
            if (IsServer)
            {
                gemSize.Value = sizeMultiplier;
                asteroidPhysicalSize.Value = asteroidScale;
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

            // Pickup radius and lifetime shrink (same as visual: full until last shrinkDuration sec)
            float lifetimeRemaining = 1f;
            if (elapsedTime >= lifetimeSeconds - shrinkDuration)
                lifetimeRemaining = Mathf.Clamp01((lifetimeSeconds - elapsedTime) / shrinkDuration);
            float currentPickupRadius = pickupRadius * gemSize.Value * lifetimeRemaining;

            // Find nearest valid ship in range for magnetic pull
            Collider[] overlaps = Physics.OverlapSphere(transform.position, currentPickupRadius);
            Starship nearestShip = null;
            float nearestDistSq = currentPickupRadius * currentPickupRadius;
            foreach (Collider col in overlaps)
            {
                Starship ship = col.GetComponent<Starship>();
                if (ship == null || ship.IsDead || ship.CurrentGems >= ship.GemCapacity) continue;
                float distSq = (ship.transform.position - transform.position).sqrMagnitude;
                if (distSq < nearestDistSq)
                {
                    nearestDistSq = distSq;
                    nearestShip = ship;
                }
            }

            if (nearestShip != null)
            {
                Vector3 toShip = nearestShip.transform.position - transform.position;
                toShip.y = 0f;
                float dist = toShip.magnitude;

                // Collect when very close
                if (dist <= collectRadius)
                {
                    float toAdd = Mathf.Min(value.Value, nearestShip.GemCapacity - nearestShip.CurrentGems);
                    nearestShip.AddGemsServerRpc(toAdd);
                    value.Value -= toAdd;
                    if (value.Value <= 0)
                    {
                        var no = GetComponent<NetworkObject>();
                        if (no != null) no.Despawn();
                    }
                    return;
                }

                // Magnetic pull toward ship (XZ only)
                if (rb != null && dist > 0.01f)
                {
                    Vector3 dir = toShip / dist;
                    Vector3 targetVel = dir * magnetSpeed;
                    rb.linearVelocity = Vector3.MoveTowards(rb.linearVelocity, targetVel, magnetSpeed * Time.fixedDeltaTime * 4f);
                    rb.linearDamping = 0f;
                }
                return;
            }

            // No ship in range: apply drag so gem slows and stops
            if (rb != null)
            {
                rb.linearDamping = slowdownDrag;
                if (rb.linearVelocity.magnitude < stopSpeedThreshold)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.linearDamping = 0f;
                }
            }
        }
    }
}
