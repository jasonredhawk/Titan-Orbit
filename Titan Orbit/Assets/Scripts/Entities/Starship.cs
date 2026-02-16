using UnityEngine;
using UnityEngine.EventSystems;
using Unity.Netcode;
using TitanOrbit.Core;
using TitanOrbit.Input;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Systems;

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
        [Header("Orbit")]
        [SerializeField] private float orbitSpeed = 0.8f; // Linear speed while auto-orbiting a home planet
        [SerializeField] private float orbitRadiusPullStrength = 2f; // How strongly to pull toward ideal orbit radius

        [Header("Combat")]
        [SerializeField] private float fireRate = 1f;
        [SerializeField] private float firePower = 10f;
        [SerializeField] private float bulletSpeed = 20f;
        [SerializeField] private Transform firePoint;

        [Header("Health")]
        [SerializeField] private float maxHealth = 100f;
        [SerializeField] private float healthRegenRate = 1f;

        [Header("Capacity (ship level only - upgrades with ship level)")]
        [SerializeField] private float gemCapacity = 100f;
        [SerializeField] private float peopleCapacity = 10f;

        [Header("Energy (weapon system)")]
        [SerializeField] private float energyCapacity = 50f;
        [SerializeField] private float energyRegenRate = 5f;
        private const float ENERGY_PER_SHOT = 1f;

        [Header("References")]
        [SerializeField] private PlayerInputHandler inputHandler;
        [SerializeField] private Rigidbody rb;
        [Tooltip("Optional: child transform whose visuals are replaced when upgrading to a new ship prefab. If null, direct children of this transform are replaced.")]
        [SerializeField] private Transform visualRoot;

        private MaterialPropertyBlock hullColorBlock;

        private NetworkVariable<float> currentHealth = new NetworkVariable<float>(100f);
        private NetworkVariable<float> currentGems = new NetworkVariable<float>(0f);
        private NetworkVariable<float> currentPeople = new NetworkVariable<float>(0f);
        private NetworkVariable<float> currentEnergy = new NetworkVariable<float>(50f);
        private NetworkVariable<TeamManager.Team> shipTeam = new NetworkVariable<TeamManager.Team>(TeamManager.Team.None);
        private NetworkVariable<bool> wantToLoadPeople = new NetworkVariable<bool>(false);
        private NetworkVariable<bool> wantToUnloadPeople = new NetworkVariable<bool>(false);
        private NetworkVariable<bool> wantToDepositGems = new NetworkVariable<bool>(false);

        // Attribute upgrade levels (Level N ship = up to N upgrades per attribute)
        private NetworkVariable<int> attrMovementSpeed = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrEnergyCapacity = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrFirePower = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrBulletSpeed = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrMaxHealth = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrHealthRegen = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrRotationSpeed = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrEnergyRegen = new NetworkVariable<int>(0);

        private const float ATTR_MULTIPLIER_PER_LEVEL = 0.1f;

        private float EffectiveMovementSpeed => movementSpeed * (1f + attrMovementSpeed.Value * ATTR_MULTIPLIER_PER_LEVEL);
        private float EffectiveEnergyCapacity => energyCapacity * (1f + attrEnergyCapacity.Value * ATTR_MULTIPLIER_PER_LEVEL);
        private float EffectiveFirePower => firePower * (1f + attrFirePower.Value * ATTR_MULTIPLIER_PER_LEVEL);
        private float EffectiveBulletSpeed => bulletSpeed * (1f + attrBulletSpeed.Value * ATTR_MULTIPLIER_PER_LEVEL);
        private float EffectiveHealthRegen => healthRegenRate * (1f + attrHealthRegen.Value * ATTR_MULTIPLIER_PER_LEVEL);
        private float EffectiveRotationSpeed => rotationSpeed * (1f + attrRotationSpeed.Value * ATTR_MULTIPLIER_PER_LEVEL);
        private float EffectiveEnergyRegen => energyRegenRate * (1f + attrEnergyRegen.Value * ATTR_MULTIPLIER_PER_LEVEL);

        private float lastFireTime = 0f;
        private Vector3 moveDirection = Vector3.zero;
        private Vector3 currentVelocity = Vector3.zero;
        private Planet currentOrbitPlanet; // When non-null, we're in a planet's orbit zone (any planet)
        private bool wasMovePressedLastFrame;

        public float CurrentHealth => currentHealth.Value;
        public float MaxHealth => maxHealth * (1f + attrMaxHealth.Value * ATTR_MULTIPLIER_PER_LEVEL);
        public float CurrentGems => currentGems.Value;
        public bool IsDead => isDead.Value;
        public float GemCapacity => gemCapacity;
        public float CurrentPeople => currentPeople.Value;
        public float PeopleCapacity => peopleCapacity;
        public float CurrentEnergy => currentEnergy.Value;
        public float EnergyCapacity => EffectiveEnergyCapacity;
        public TeamManager.Team ShipTeam => shipTeam.Value;
        public int ShipLevel => shipLevel;
        public int BranchIndex => shipData != null ? shipData.branchIndex : 0;
        public ShipFocusType FocusType => focusType;
        public bool IsInOrbit => currentOrbitPlanet != null;
        public Planet CurrentOrbitPlanet => currentOrbitPlanet;
        public bool WantToLoadPeople => wantToLoadPeople.Value;
        public bool WantToUnloadPeople => wantToUnloadPeople.Value;
        public bool WantToDepositGems => wantToDepositGems.Value;

        private const float FIXED_Y_POSITION = 0f;

        private void Awake()
        {
            if (rb == null) rb = GetComponent<Rigidbody>();
            if (inputHandler == null) inputHandler = GetComponent<PlayerInputHandler>();
            if (energyCapacity <= 0f) energyCapacity = 50f;
            if (energyRegenRate <= 0f) energyRegenRate = 5f;

            ApplyHullIdentityColor();

            // Lock Y position - prevent elevation changes
            if (rb != null)
            {
                rb.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            }
        }

        private void ApplyHullIdentityColor()
        {
            if (shipData == null || shipData.shipColor == Color.white) return;
            var mr = GetComponent<Renderer>();
            if (mr == null) return;
            if (hullColorBlock == null) hullColorBlock = new MaterialPropertyBlock();
            mr.GetPropertyBlock(hullColorBlock);
            hullColorBlock.SetColor("_BaseColor", shipData.shipColor);
            mr.SetPropertyBlock(hullColorBlock);
        }

        public override void OnNetworkSpawn()
        {
            // Ensure Y position is locked to 0
            Vector3 pos = transform.position;
            pos.y = FIXED_Y_POSITION;
            transform.position = pos;
            
            if (IsServer)
            {
                currentHealth.Value = MaxHealth;
                currentGems.Value = 0f;
                currentPeople.Value = 0f;
                currentEnergy.Value = EffectiveEnergyCapacity;
                // Team is set when NetworkGameManager.OnClientConnected runs (after spawn); try now in case it's already set
                if (TeamManager.Instance != null)
                    shipTeam.Value = TeamManager.Instance.GetPlayerTeam(OwnerClientId);
                StartInOrbitAroundHomePlanet();
            }
        }

        /// <summary>Server only: called by NetworkGameManager when team is assigned (after client connect). Sets team and starts in orbit.</summary>
        public void AssignTeamAndStartInOrbit(TeamManager.Team team)
        {
            if (!IsServer) return;
            shipTeam.Value = team;
            StartInOrbitAroundHomePlanet();
        }

        /// <summary>Server: position ship in orbit around its team's home planet at spawn.</summary>
        private void StartInOrbitAroundHomePlanet()
        {
            if (shipTeam.Value == TeamManager.Team.None || rb == null) return;
            HomePlanet home = null;
            foreach (var hp in Object.FindObjectsOfType<HomePlanet>())
            {
                if (hp.AssignedTeam == shipTeam.Value) { home = hp; break; }
            }
            if (home == null) return;
            float orbitRadius = home.PlanetSize * 0.6f;
            Vector3 planetPos = home.transform.position;
            Vector3 orbitPos = planetPos + new Vector3(orbitRadius, 0f, 0f);
            orbitPos.y = FIXED_Y_POSITION;
            orbitPos = ToroidalMap.WrapPosition(orbitPos);
            rb.position = orbitPos;
            rb.linearVelocity = new Vector3(0f, 0f, -orbitSpeed); // Tangent for clockwise orbit
            currentVelocity = rb.linearVelocity;
        }

        private void Update()
        {
            if (!IsOwner) return;

            HandleInput();
            bool movePressed = inputHandler != null && inputHandler.MoveForwardPressed;
            // When user starts moving, hide orbit menu immediately (even if still in zone)
            if (currentOrbitPlanet != null && movePressed)
            {
                var orbitUI = TitanOrbit.UI.HomePlanetOrbitUI.GetOrCreate();
                orbitUI.Hide();
            }
            // When user stops moving while still in orbit zone, show menu again
            if (currentOrbitPlanet != null && !movePressed && wasMovePressedLastFrame)
            {
                var orbitUI = TitanOrbit.UI.HomePlanetOrbitUI.GetOrCreate();
                orbitUI.Show(this, currentOrbitPlanet);
            }
            wasMovePressedLastFrame = movePressed;
            HandleHealthRegen();
            HandleEnergyRegen();
            // If we're in orbit zone but trigger didn't fire (e.g. spawned there), detect it
            if (currentOrbitPlanet == null)
                TryDetectOrbitZone();
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
            
            if (IsServer)
            {
                HandleDeath();
                TickOrbitPopulationTransfer();
                TickOrbitGemDeposit();
            }
            if (!IsOwner) return;
            bool useOrbit = currentOrbitPlanet != null && inputHandler != null && !inputHandler.MoveForwardPressed;
            if (useOrbit)
                HandleOrbitMovement();
            else
            {
                HandleMovement();
                HandleRotation();
            }
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
            // Don't fire when clicking on UI (e.g. orbit menu buttons)
            if (inputHandler.ShootPressed && CanFire() && firePoint != null && !IsPointerOverUI())
            {
                Vector3 dir = transform.forward;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
                else dir.Normalize();
                FireServerRpc(firePoint.position, dir);
            }
        }

        private static bool IsPointerOverUI()
        {
            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        }

        private void HandleMovement()
        {
            if (moveDirection.magnitude > 0.1f)
            {
                currentVelocity += moveDirection * acceleration * Time.fixedDeltaTime;
                if (currentVelocity.magnitude > EffectiveMovementSpeed)
                    currentVelocity = currentVelocity.normalized * EffectiveMovementSpeed;
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

        private void HandleOrbitMovement()
        {
            if (currentOrbitPlanet == null || rb == null) return;
            Vector3 planetPos = currentOrbitPlanet.transform.position;
            Vector3 toShip = rb.position - planetPos;
            toShip.y = 0f;
            float dist = toShip.magnitude;
            if (dist < 0.01f) return;
            // Orbit zone: radius 0.5 to 0.8 local. Ideal orbit = 0.65; world = PlanetSize * 0.65f.
            float idealOrbitRadius = currentOrbitPlanet.PlanetSize * 0.65f;
            Vector3 radial = toShip / dist;
            // Clockwise tangent (viewed from above): (radial.z, 0, -radial.x)
            Vector3 tangent = new Vector3(radial.z, 0f, -radial.x);
            Vector3 orbitVelocity = tangent * orbitSpeed;
            if (Mathf.Abs(dist - idealOrbitRadius) > 0.3f)
            {
                Vector3 pull = (dist > idealOrbitRadius ? -radial : radial) * orbitRadiusPullStrength;
                orbitVelocity += pull;
            }
            currentVelocity = orbitVelocity;
            rb.linearVelocity = orbitVelocity;
            Vector3 newPosition = rb.position + orbitVelocity * Time.fixedDeltaTime;
            newPosition.y = FIXED_Y_POSITION;
            newPosition = ToroidalMap.WrapPosition(newPosition);
            rb.MovePosition(newPosition);
            // Lock rotation to orbit direction so mouse never pulls the ship out of orbit
            if (orbitVelocity.sqrMagnitude > 0.001f)
            {
                Quaternion orbitFacing = Quaternion.LookRotation(orbitVelocity.normalized);
                rb.MoveRotation(orbitFacing);
            }
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
                        EffectiveRotationSpeed * Time.fixedDeltaTime
                    );
                    rb.MoveRotation(newRotation);
                }
            }
        }

        private void HandleHealthRegen()
        {
            if (IsServer && currentHealth.Value < MaxHealth)
            {
                float regen = EffectiveHealthRegen * Time.deltaTime;
                if (GameManager.Instance != null && GameManager.Instance.DebugMode) regen *= 100f;
                currentHealth.Value = Mathf.Min(currentHealth.Value + regen, MaxHealth);
            }
        }

        private void HandleEnergyRegen()
        {
            if (IsServer && currentEnergy.Value < EffectiveEnergyCapacity)
            {
                float regen = EffectiveEnergyRegen * Time.deltaTime;
                if (GameManager.Instance != null && GameManager.Instance.DebugMode) regen *= 100f;
                currentEnergy.Value = Mathf.Min(currentEnergy.Value + regen, EffectiveEnergyCapacity);
            }
        }

        private bool CanFire()
        {
            return Time.time - lastFireTime >= 1f / fireRate
                && currentEnergy.Value >= ENERGY_PER_SHOT;
        }

        [ServerRpc]
        private void FireServerRpc(Vector3 firePosition, Vector3 fireDirection)
        {
            if (!CanFire()) return;

            lastFireTime = Time.time;
            currentEnergy.Value = Mathf.Max(0f, currentEnergy.Value - ENERGY_PER_SHOT);
            
            if (Systems.CombatSystem.Instance != null)
            {
                Systems.CombatSystem.Instance.SpawnBulletServerRpc(
                    firePosition,
                    fireDirection,
                    EffectiveBulletSpeed,
                    EffectiveFirePower,
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

        /// <summary>Server: continuous load/unload at shipLevel people per second while in orbit.</summary>
        private void TickOrbitPopulationTransfer()
        {
            if (currentOrbitPlanet == null) return;

            float rate = shipLevel * Time.fixedDeltaTime; // e.g. level 1 = 1 per second
            if (GameManager.Instance != null && GameManager.Instance.DebugMode) rate *= 100f;
            if (rate <= 0f) return;

            if (wantToLoadPeople.Value)
            {
                bool friendly = (currentOrbitPlanet is HomePlanet home && home.AssignedTeam == shipTeam.Value)
                    || currentOrbitPlanet.TeamOwnership == shipTeam.Value;
                if (!friendly) return;
                float space = PeopleCapacity - currentPeople.Value;
                float available = currentOrbitPlanet.CurrentPopulation;
                float amount = Mathf.Min(rate, space, available);
                if (amount > 0f)
                {
                    currentOrbitPlanet.RemovePopulationServerRpc(amount);
                    AddPeopleServerRpc(amount);
                }
                // Reset toggle when ship is full or planet has no one left
                if (currentPeople.Value >= PeopleCapacity - 0.001f || available <= 0f)
                    wantToLoadPeople.Value = false;
            }
            else if (wantToUnloadPeople.Value)
            {
                float amount = Mathf.Min(rate, currentPeople.Value);
                if (amount > 0f)
                {
                    RemovePeopleServerRpc(amount);
                    currentOrbitPlanet.AddPopulationServerRpc(amount, shipTeam.Value); // friendly: adds pop; enemy/neutral: decreases (capture)
                }
                // Reset toggle when ship has no people left
                if (currentPeople.Value <= 0.001f)
                    wantToUnloadPeople.Value = false;
            }
        }

        /// <summary>Server: continuous gem deposit at shipLevel gems per 0.5s while in orbit at home planet (same team).</summary>
        private void TickOrbitGemDeposit()
        {
            if (currentOrbitPlanet == null || !wantToDepositGems.Value) return;
            if (!(currentOrbitPlanet is HomePlanet home) || home.AssignedTeam != shipTeam.Value) return;
            if (currentGems.Value <= 0f) { wantToDepositGems.Value = false; return; }

            // shipLevel gems per 0.5 sec = shipLevel * 2 per second
            float rate = shipLevel * 2f * Time.fixedDeltaTime;
            if (GameManager.Instance != null && GameManager.Instance.DebugMode) rate *= 100f;
            if (rate <= 0f) return;
            float amount = Mathf.Min(rate, currentGems.Value);
            if (amount > 0f)
            {
                RemoveGemsServerRpc(amount);
                home.DepositGemsServerRpc(amount, shipTeam.Value);
            }
            if (currentGems.Value <= 0.001f)
                wantToDepositGems.Value = false;
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
            currentHealth.Value = MaxHealth;
            currentGems.Value = 0f;
            currentPeople.Value = 0f;
            currentEnergy.Value = EffectiveEnergyCapacity;
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
            currentGems.Value = Mathf.Min(currentGems.Value + amount, GemCapacity);
        }

        [ServerRpc(RequireOwnership = false)]
        public void RemoveGemsServerRpc(float amount)
        {
            currentGems.Value = Mathf.Max(0f, currentGems.Value - amount);
        }

        [ServerRpc(RequireOwnership = false)]
        public void AddPeopleServerRpc(float amount)
        {
            currentPeople.Value = Mathf.Min(currentPeople.Value + amount, PeopleCapacity);
        }

        [ServerRpc(RequireOwnership = false)]
        public void RemovePeopleServerRpc(float amount)
        {
            currentPeople.Value = Mathf.Max(0f, currentPeople.Value - amount);
        }

        [ServerRpc(RequireOwnership = true)]
        public void SetWantToLoadPeopleServerRpc(bool value)
        {
            wantToLoadPeople.Value = value;
            if (value) wantToUnloadPeople.Value = false;
        }

        [ServerRpc(RequireOwnership = true)]
        public void SetWantToUnloadPeopleServerRpc(bool value)
        {
            wantToUnloadPeople.Value = value;
            if (value) wantToLoadPeople.Value = false;
        }

        [ServerRpc(RequireOwnership = true)]
        public void SetWantToDepositGemsServerRpc(bool value)
        {
            wantToDepositGems.Value = value;
        }

        /// <summary>Owner-only: detect if we're inside a planet's orbit zone (e.g. after spawning there).</summary>
        private void TryDetectOrbitZone()
        {
            if (rb == null || currentOrbitPlanet != null) return;
            foreach (var planet in Object.FindObjectsOfType<Planet>())
            {
                if (planet == null) continue;
                Vector3 toShip = rb.position - planet.transform.position;
                toShip.y = 0f;
                float dist = toShip.magnitude;
                float inner = planet.PlanetSize * 0.5f;
                float outer = planet.PlanetSize * 0.8f;
                if (dist >= inner && dist <= outer)
                {
                    currentOrbitPlanet = planet;
                    var orbitUI = TitanOrbit.UI.HomePlanetOrbitUI.GetOrCreate();
                    orbitUI.Show(this, planet);
                    break;
                }
            }
        }

        /// <summary>Called by PlanetOrbitZone when ship enters the orbit/loading zone.</summary>
        public void EnterOrbitZone(Planet planet)
        {
            if (planet == null) return;
            currentOrbitPlanet = planet;
            if (IsOwner)
            {
                var orbitUI = TitanOrbit.UI.HomePlanetOrbitUI.GetOrCreate();
                orbitUI.Show(this, planet);
            }
        }

        /// <summary>Called by PlanetOrbitZone when ship leaves the orbit zone.</summary>
        /// <remarks>Load/unload toggles are not cleared here so they don't reset when the ship briefly exits the zone (e.g. orbit wobble). They only reset in TickOrbitPopulationTransfer when transfer is complete (ship full or empty).</remarks>
        public void ExitOrbitZone(Planet planet)
        {
            if (currentOrbitPlanet == planet)
            {
                currentOrbitPlanet = null;
                if (IsOwner)
                {
                    var orbitUI = TitanOrbit.UI.HomePlanetOrbitUI.GetOrCreate();
                    orbitUI.Hide();
                }
            }
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
                energyCapacity = data.baseEnergyCapacity;
                energyRegenRate = data.baseEnergyRegenRate;

                if (data.shipPrefab != null)
                    ApplyShipVisual(data.shipPrefab);
                ApplyHullIdentityColor();
            }
        }

        /// <summary>Replaces this ship's visual with the chosen ship prefab: copies root hull mesh and reparents children (keeps FirePoint for shooting).</summary>
        private void ApplyShipVisual(GameObject shipPrefab)
        {
            if (shipPrefab == null) return;
            Transform root = visualRoot != null ? visualRoot : transform;

            GameObject instance = Instantiate(shipPrefab);
            Transform prefabRoot = instance.transform;

            // Copy hull from prefab root to our root (prefab has MeshFilter + MeshRenderer on root)
            MeshFilter prefabMf = prefabRoot.GetComponent<MeshFilter>();
            MeshRenderer prefabMr = prefabRoot.GetComponent<MeshRenderer>();
            if (prefabMf != null && prefabMr != null)
            {
                MeshFilter ourMf = root.GetComponent<MeshFilter>();
                if (ourMf == null) ourMf = root.gameObject.AddComponent<MeshFilter>();
                if (ourMf != null && prefabMf.sharedMesh != null)
                    ourMf.sharedMesh = prefabMf.sharedMesh;

                MeshRenderer ourMr = root.GetComponent<MeshRenderer>();
                if (ourMr == null) ourMr = root.gameObject.AddComponent<MeshRenderer>();
                if (ourMr != null)
                {
                    ourMr.sharedMaterials = prefabMr.sharedMaterials;
                    ourMr.enabled = prefabMr.enabled;
                }
            }

            // Remove our current visual children, then adopt prefab root's children
            for (int i = root.childCount - 1; i >= 0; i--)
                Object.Destroy(root.GetChild(i).gameObject);

            Transform newFirePoint = null;
            while (prefabRoot.childCount > 0)
            {
                Transform child = prefabRoot.GetChild(0);
                if (child.name == "FirePoint")
                    newFirePoint = child;
                Vector3 localPos = child.localPosition;
                Quaternion localRot = child.localRotation;
                Vector3 localScl = child.localScale;
                child.SetParent(root, false);
                child.localPosition = localPos;
                child.localRotation = localRot;
                child.localScale = localScl;
            }
            Destroy(instance);

            // Rebind FirePoint from the prefab child we just moved (don't use Find - old children may still be present until Destroy runs)
            if (newFirePoint != null)
                firePoint = newFirePoint;
            else
            {
                newFirePoint = root.Find("FirePoint");
                if (newFirePoint != null) firePoint = newFirePoint;
                else
                {
                    // Fallback: ensure we always have a valid fire point so shooting works
                    GameObject fp = new GameObject("FirePoint");
                    fp.transform.SetParent(root, false);
                    fp.transform.localPosition = new Vector3(0f, 0f, 0.55f);
                    fp.transform.localRotation = Quaternion.identity;
                    fp.transform.localScale = Vector3.one;
                    firePoint = fp.transform;
                }
            }
        }

        /// <summary>Returns the current upgrade level for the given attribute (0 to ShipLevel).</summary>
        public int GetAttributeLevel(AttributeUpgradeSystem.ShipAttributeType attributeType)
        {
            switch (attributeType)
            {
                case AttributeUpgradeSystem.ShipAttributeType.MovementSpeed: return attrMovementSpeed.Value;
                case AttributeUpgradeSystem.ShipAttributeType.EnergyCapacity: return attrEnergyCapacity.Value;
                case AttributeUpgradeSystem.ShipAttributeType.FirePower: return attrFirePower.Value;
                case AttributeUpgradeSystem.ShipAttributeType.BulletSpeed: return attrBulletSpeed.Value;
                case AttributeUpgradeSystem.ShipAttributeType.MaxHealth: return attrMaxHealth.Value;
                case AttributeUpgradeSystem.ShipAttributeType.HealthRegen: return attrHealthRegen.Value;
                case AttributeUpgradeSystem.ShipAttributeType.RotationSpeed: return attrRotationSpeed.Value;
                case AttributeUpgradeSystem.ShipAttributeType.EnergyRegen: return attrEnergyRegen.Value;
                default: return 0;
            }
        }

        /// <summary>Server only: increments the attribute level. Caller must validate cost and max level.</summary>
        public void IncrementAttributeLevel(AttributeUpgradeSystem.ShipAttributeType attributeType)
        {
            if (!IsServer) return;
            switch (attributeType)
            {
                case AttributeUpgradeSystem.ShipAttributeType.MovementSpeed: attrMovementSpeed.Value++; break;
                case AttributeUpgradeSystem.ShipAttributeType.EnergyCapacity: attrEnergyCapacity.Value++; break;
                case AttributeUpgradeSystem.ShipAttributeType.FirePower: attrFirePower.Value++; break;
                case AttributeUpgradeSystem.ShipAttributeType.BulletSpeed: attrBulletSpeed.Value++; break;
                case AttributeUpgradeSystem.ShipAttributeType.MaxHealth: attrMaxHealth.Value++; break;
                case AttributeUpgradeSystem.ShipAttributeType.HealthRegen: attrHealthRegen.Value++; break;
                case AttributeUpgradeSystem.ShipAttributeType.RotationSpeed: attrRotationSpeed.Value++; break;
                case AttributeUpgradeSystem.ShipAttributeType.EnergyRegen: attrEnergyRegen.Value++; break;
            }
        }
    }
}
