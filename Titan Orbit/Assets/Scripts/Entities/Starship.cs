using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Input;
using TitanOrbit.Data;
using TitanOrbit.Generation;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Base starship controller for player-controlled ships
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Starship : NetworkBehaviour
    {
        [Header("Ship Settings")]
        [SerializeField] private ShipData shipData;
        [SerializeField] private int shipLevel = 1;
        [SerializeField] private ShipFocusType focusType = ShipFocusType.Fighter;

        [Header("Movement")]
        [SerializeField] private float movementSpeed = 10f;
        [SerializeField] private float rotationSpeed = 180f;
        [SerializeField] private float acceleration = 20f;
        [SerializeField] private float deceleration = 8f;

        [Header("Combat")]
        [SerializeField] private float fireRate = 1f;
        [SerializeField] private float firePower = 10f;
        [SerializeField] private float bulletSpeed = 20f;
        [SerializeField] private Transform firePoint;

        [Header("Health")]
        [SerializeField] private float maxHealth = 100f;
        [SerializeField] private float healthRegenRate = 1f;

        [Header("Capacity")]
        [SerializeField] private float gemCapacity = 100f;
        [SerializeField] private float peopleCapacity = 10f;

        [Header("References")]
        [SerializeField] private PlayerInputHandler inputHandler;
        [SerializeField] private Rigidbody rb;

        private NetworkVariable<float> currentHealth = new NetworkVariable<float>(100f);
        private NetworkVariable<float> currentGems = new NetworkVariable<float>(0f);
        private NetworkVariable<float> currentPeople = new NetworkVariable<float>(0f);
        private NetworkVariable<TeamManager.Team> shipTeam = new NetworkVariable<TeamManager.Team>(TeamManager.Team.None);

        private float lastFireTime = 0f;
        private Vector3 moveDirection = Vector3.zero;
        private Vector3 currentVelocity = Vector3.zero;

        public float CurrentHealth => currentHealth.Value;
        public float MaxHealth => maxHealth;
        public float CurrentGems => currentGems.Value;
        public bool IsDead => isDead.Value;
        public float GemCapacity => gemCapacity;
        public float CurrentPeople => currentPeople.Value;
        public float PeopleCapacity => peopleCapacity;
        public TeamManager.Team ShipTeam => shipTeam.Value;
        public int ShipLevel => shipLevel;
        public ShipFocusType FocusType => focusType;

        private const float FIXED_Y_POSITION = 0f;

        private void Awake()
        {
            if (rb == null) rb = GetComponent<Rigidbody>();
            if (inputHandler == null) inputHandler = GetComponent<PlayerInputHandler>();
            
            // Lock Y position - prevent elevation changes
            if (rb != null)
            {
                rb.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            }
        }

        public override void OnNetworkSpawn()
        {
            // Ensure Y position is locked to 0
            Vector3 pos = transform.position;
            pos.y = FIXED_Y_POSITION;
            transform.position = pos;
            
            if (IsServer)
            {
                currentHealth.Value = maxHealth;
                currentGems.Value = 0f;
                currentPeople.Value = 0f;
                if (TeamManager.Instance != null)
                    shipTeam.Value = TeamManager.Instance.GetPlayerTeam(OwnerClientId);
            }
        }

        private void Update()
        {
            if (!IsOwner) return;

            HandleInput();
            HandleHealthRegen();
        }

        private void FixedUpdate()
        {
            if (rb == null) return;
            
            // Always lock Y position (prevents drift from physics/collisions)
            Vector3 pos = rb.position;
            if (Mathf.Abs(pos.y - FIXED_Y_POSITION) > 0.01f)
            {
                pos.y = FIXED_Y_POSITION;
                rb.position = pos;
            }
            
            // Wrap position toroidally - use rb.position for consistency (avoids transform/rb desync)
            Vector3 wrappedPos = ToroidalMap.WrapPosition(pos);
            if (Vector3.SqrMagnitude(wrappedPos - pos) > 0.0001f)
            {
                rb.position = wrappedPos;
            }
            
            // Ensure rigidbody velocity has no Y component
            if (Mathf.Abs(rb.linearVelocity.y) > 0.01f)
            {
                Vector3 vel = rb.linearVelocity;
                vel.y = 0f;
                rb.linearVelocity = vel;
            }
            
            if (IsServer) HandleDeath();
            if (!IsOwner) return;
            HandleMovement();
            HandleRotation();
        }

        private void HandleInput()
        {
            if (inputHandler == null) return;

            // Movement: right-click only - move in direction ship is facing
            if (inputHandler.MoveForwardPressed)
            {
                moveDirection = transform.forward;
                moveDirection.y = 0f;
                if (moveDirection.sqrMagnitude > 0.01f)
                {
                    moveDirection.Normalize();
                }
            }
            else
            {
                moveDirection = Vector3.zero;
            }

            // Shooting input - pass fire position and direction from client (Vector3 avoids Quaternion sync issues)
            if (inputHandler.ShootPressed && CanFire() && firePoint != null)
            {
                Vector3 dir = transform.forward;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
                else dir.Normalize();
                FireServerRpc(firePoint.position, dir);
            }
        }

        private void HandleMovement()
        {
            if (moveDirection.magnitude > 0.1f)
            {
                currentVelocity += moveDirection * acceleration * Time.fixedDeltaTime;
                if (currentVelocity.magnitude > movementSpeed)
                    currentVelocity = currentVelocity.normalized * movementSpeed;
            }
            else
            {
                currentVelocity = Vector3.MoveTowards(currentVelocity, Vector3.zero, deceleration * Time.fixedDeltaTime);
            }

            // Ensure velocity has no Y component
            currentVelocity.y = 0f;

            // Calculate new position and lock Y to fixed position
            Vector3 newPosition = rb.position + currentVelocity * Time.fixedDeltaTime;
            newPosition.y = FIXED_Y_POSITION;
            
            // Wrap position toroidally (no borders - wraps around map)
            newPosition = ToroidalMap.WrapPosition(newPosition);
            
            rb.MovePosition(newPosition);
        }

        private void HandleRotation()
        {
            // Always rotate toward mouse cursor - works in place, no movement required
            UnityEngine.Camera cam = UnityEngine.Camera.main;
            if (cam != null && inputHandler != null)
            {
                Vector3 mouseWorldPos = inputHandler.GetMouseWorldPosition(cam);
                Vector3 directionToMouse = (mouseWorldPos - transform.position);
                directionToMouse.y = 0f;
                if (directionToMouse.sqrMagnitude > 0.001f)
                {
                    directionToMouse.Normalize();
                    Quaternion targetRotation = Quaternion.LookRotation(directionToMouse);
                    Quaternion newRotation = Quaternion.RotateTowards(
                        rb.rotation,
                        targetRotation,
                        rotationSpeed * Time.fixedDeltaTime
                    );
                    rb.MoveRotation(newRotation);
                }
            }
        }

        private void HandleHealthRegen()
        {
            if (IsServer && currentHealth.Value < maxHealth)
            {
                currentHealth.Value = Mathf.Min(
                    currentHealth.Value + healthRegenRate * Time.deltaTime,
                    maxHealth
                );
            }
        }

        private bool CanFire()
        {
            return Time.time - lastFireTime >= 1f / fireRate;
        }

        [ServerRpc]
        private void FireServerRpc(Vector3 firePosition, Vector3 fireDirection)
        {
            if (!CanFire()) return;

            lastFireTime = Time.time;
            
            if (Systems.CombatSystem.Instance != null)
            {
                Systems.CombatSystem.Instance.SpawnBulletServerRpc(
                    firePosition,
                    fireDirection,
                    bulletSpeed,
                    firePower,
                    shipTeam.Value
                );
            }
            
            FireClientRpc();
        }

        [ClientRpc]
        private void FireClientRpc()
        {
            // Visual/audio feedback for firing
        }

        private NetworkVariable<bool> isDead = new NetworkVariable<bool>(false);
        private float timeHealthZero;

        [ServerRpc(RequireOwnership = false)]
        public void TakeDamageServerRpc(float damage, TeamManager.Team attackerTeam)
        {
            if (attackerTeam == shipTeam.Value) return;
            if (isDead.Value) return;

            currentHealth.Value = Mathf.Max(0f, currentHealth.Value - damage);

            if (currentHealth.Value <= 0f)
            {
                timeHealthZero = Time.time;
            }
        }

        private void HandleDeath()
        {
            if (isDead.Value) return;

            if (currentHealth.Value <= 0f)
            {
                // Drain gems over time when health is zero
                float drainRate = 20f;
                float toDrain = drainRate * Time.deltaTime;
                currentGems.Value = Mathf.Max(0f, currentGems.Value - toDrain);
            }

            if (currentHealth.Value <= 0f && currentGems.Value <= 0f)
            {
                DieServerRpc();
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void DieServerRpc()
        {
            if (isDead.Value) return;
            isDead.Value = true;
            RespawnServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void RespawnServerRpc()
        {
            currentHealth.Value = maxHealth;
            currentGems.Value = 0f;
            currentPeople.Value = 0f;
            isDead.Value = false;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!IsServer) return;

            Asteroid asteroid = collision.gameObject.GetComponent<Asteroid>();
            if (asteroid != null)
            {
                float collisionDamage = 10f;
                asteroid.TakeDamageServerRpc(collisionDamage);
                TakeDamageServerRpc(collisionDamage, TeamManager.Team.None);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void AddGemsServerRpc(float amount)
        {
            currentGems.Value = Mathf.Min(currentGems.Value + amount, gemCapacity);
        }

        [ServerRpc(RequireOwnership = false)]
        public void RemoveGemsServerRpc(float amount)
        {
            currentGems.Value = Mathf.Max(0f, currentGems.Value - amount);
        }

        [ServerRpc(RequireOwnership = false)]
        public void AddPeopleServerRpc(float amount)
        {
            currentPeople.Value = Mathf.Min(currentPeople.Value + amount, peopleCapacity);
        }

        [ServerRpc(RequireOwnership = false)]
        public void RemovePeopleServerRpc(float amount)
        {
            currentPeople.Value = Mathf.Max(0f, currentPeople.Value - amount);
        }

        public void SetShipData(ShipData data)
        {
            shipData = data;
            if (data != null)
            {
                shipLevel = data.shipLevel;
                focusType = data.focusType;
                movementSpeed = data.baseMovementSpeed;
                fireRate = data.baseFireRate;
                firePower = data.baseFirePower;
                bulletSpeed = data.baseBulletSpeed;
                maxHealth = data.baseMaxHealth;
                healthRegenRate = data.baseHealthRegenRate;
                rotationSpeed = data.baseRotationSpeed;
                gemCapacity = data.baseGemCapacity;
                peopleCapacity = data.basePeopleCapacity;
            }
        }
    }
}
