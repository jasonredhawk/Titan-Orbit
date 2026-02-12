using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Input;
using TitanOrbit.Data;

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

        public float CurrentHealth => currentHealth.Value;
        public float MaxHealth => maxHealth;
        public float CurrentGems => currentGems.Value;
        public float GemCapacity => gemCapacity;
        public float CurrentPeople => currentPeople.Value;
        public float PeopleCapacity => peopleCapacity;
        public TeamManager.Team ShipTeam => shipTeam.Value;
        public int ShipLevel => shipLevel;
        public ShipFocusType FocusType => focusType;

        private void Awake()
        {
            if (rb == null) rb = GetComponent<Rigidbody>();
            if (inputHandler == null) inputHandler = GetComponent<PlayerInputHandler>();
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                currentHealth.Value = maxHealth;
                currentGems.Value = 0f;
                currentPeople.Value = 0f;
            }

            // Assign team
            if (IsOwner && TeamManager.Instance != null)
            {
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
            if (!IsOwner) return;

            HandleMovement();
            HandleRotation();
        }

        private void HandleInput()
        {
            if (inputHandler == null) return;

            // Movement input
            Vector2 moveInput = inputHandler.MoveInput;
            if (moveInput.magnitude > 0.1f)
            {
                moveDirection = new Vector3(moveInput.x, 0f, moveInput.y);
            }
            else
            {
                // Try to get world position for movement (mouse/touch)
                UnityEngine.Camera cam = UnityEngine.Camera.main;
                if (cam != null)
                {
                    Vector3 worldPos = inputHandler.GetMoveWorldPosition(cam);
                    moveDirection = (worldPos - transform.position).normalized;
                    moveDirection.y = 0f;
                }
            }

            // Shooting input
            if (inputHandler.ShootPressed && CanFire())
            {
                FireServerRpc();
            }
        }

        private void HandleMovement()
        {
            if (moveDirection.magnitude > 0.1f)
            {
                Vector3 movement = moveDirection * movementSpeed * Time.fixedDeltaTime;
                rb.MovePosition(rb.position + movement);
            }
        }

        private void HandleRotation()
        {
            if (moveDirection.magnitude > 0.1f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(moveDirection);
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation,
                    targetRotation,
                    rotationSpeed * Time.fixedDeltaTime
                );
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
        private void FireServerRpc()
        {
            if (!CanFire()) return;

            lastFireTime = Time.time;
            
            // Spawn bullet
            if (firePoint != null && Systems.CombatSystem.Instance != null)
            {
                Systems.CombatSystem.Instance.SpawnBulletServerRpc(
                    firePoint.position,
                    firePoint.rotation,
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

        [ServerRpc(RequireOwnership = false)]
        public void TakeDamageServerRpc(float damage, TeamManager.Team attackerTeam)
        {
            // No friendly fire
            if (attackerTeam == shipTeam.Value) return;

            currentHealth.Value = Mathf.Max(0f, currentHealth.Value - damage);

            if (currentHealth.Value <= 0f)
            {
                DieServerRpc();
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void DieServerRpc()
        {
            // Handle death
            RespawnServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void RespawnServerRpc()
        {
            currentHealth.Value = maxHealth;
            currentGems.Value = 0f;
            currentPeople.Value = 0f;
            // Reset position to home planet
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
