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
        [SerializeField] private float movementSpeed = 8f;
        [SerializeField] private float rotationSpeed = 180f;
        [SerializeField] private float acceleration = 32f;
        [Tooltip("When space brakes are on, speed is reduced by this amount per second (higher = more friction, faster stop).")]
        [SerializeField] private float brakeDeceleration = 7f;
        [Tooltip("When over max speed (e.g. from recoil), speed is reduced back toward max by this amount per second.")]
        [SerializeField] private float recoilDecayPerSecond = 6f;
        [Header("Orbit")]
        [SerializeField] private float orbitSpeed = 0.8f; // Baseline linear speed while orbiting; modified by planet size and radius
        [SerializeField] private float orbitRadiusPullStrength = 2.5f; // Push in/out when outside zone band; stronger = quicker stabilization
        [Tooltip("How quickly the ship's existing velocity is steered toward the ideal orbit velocity. Higher = snappier capture, lower = more drift-through.")]
        [SerializeField] private float orbitCaptureResponsiveness = 2.2f;

        [Header("Combat")]
        [SerializeField] private Transform firePoint;
        [Tooltip("Recoil impulse per shot scales with bullet scale and damage. Bigger bullets push the ship back more; stationary ships can reverse.")]
        [SerializeField] private float recoilStrength = 6.5f;

        [Header("Ramming")]
        [Tooltip("Base damage applied to ship and asteroid on impact (in addition to speed-based damage).")]
        [SerializeField] private float baseRammingDamage = 5f;
        [Tooltip("Extra damage per unit of impact speed (ship + asteroid relative velocity).")]
        [SerializeField] private float rammingDamagePerSpeedUnit = 3f;
        [Tooltip("Damage scale from momentum (mass * speed). Higher = ramming speed and weight matter more.")]
        [SerializeField] private float rammingMomentumDamageScale = 0.4f;
        [Tooltip("Mining ships (ice-breaker hull) deal this multiplier to asteroids; take less self-damage via HullRammingSelfDamageMultiplier.")]
        [SerializeField] private float minerRammingToAsteroidMultiplier = 1.4f;
        [Tooltip("Mining ships take this fraction of ramming self-damage (stronger hull). Fighter = 1, Miner = 0.35.")]
        [SerializeField] private float minerRammingSelfDamageMultiplier = 0.35f;
        [Tooltip("Damage per second to asteroid (and self) while sustaining contact (ramming).")]
        [SerializeField] private float ramDamagePerSecond = 18f;
        [Tooltip("Interval between sustained ram damage ticks (seconds).")]
        [SerializeField] private float ramTickInterval = 0.25f;
        private float lastRamDamageTime = -999f;
        [Tooltip("When overlapping an asteroid (e.g. after respawn), ship is pushed outward at this speed for a smooth escape.")]
        [SerializeField] private float overlapEscapeSpeed = 4f;
        private WeaponConfig weaponConfig;
        private float[] cannonLastFireTime;

        private static WeaponConfig defaultWeaponConfig;

        private static WeaponConfig GetDefaultWeaponConfig()
        {
            if (defaultWeaponConfig != null) return defaultWeaponConfig;
            defaultWeaponConfig = ScriptableObject.CreateInstance<WeaponConfig>();
            defaultWeaponConfig.displayName = "Default";
            defaultWeaponConfig.cannons = new System.Collections.Generic.List<CannonConfig>
            {
                new CannonConfig { fireRate = 2.5f, energyCostPerShot = 2f, damagePerBullet = 8f, bulletScale = 0.6f, bulletSpeed = 20f }
            };
            return defaultWeaponConfig;
        }

        /// <summary>Always returns a valid config (from ship data or default). Use this instead of weaponConfig so client can fire without sync.</summary>
        private WeaponConfig EffectiveWeaponConfig =>
            (weaponConfig != null && weaponConfig.cannons != null && weaponConfig.cannons.Count > 0)
                ? weaponConfig
                : GetDefaultWeaponConfig();

        private void EnsureCannonLastFireTime()
        {
            var wc = EffectiveWeaponConfig;
            int n = wc.cannons.Count;
            if (cannonLastFireTime == null || cannonLastFireTime.Length != n)
            {
                cannonLastFireTime = new float[n];
                for (int i = 0; i < n; i++) cannonLastFireTime[i] = -999f;
            }
        }

        [Header("Health")]
        [SerializeField] private float maxHealth = 100f;
        [SerializeField] private float healthRegenRate = 1f;

        [Header("Capacity (ship level only - upgrades with ship level)")]
        [SerializeField] private float gemCapacity = 100f;
        [SerializeField] private float peopleCapacity = 10f;

        [Header("Mass (affects momentum and ramming)")]
        [Tooltip("Base rigidbody mass when empty (overridden by ShipData.baseMass when set). Heavier ship = slower to accelerate/brake.")]
        [SerializeField] private float baseMass = 1f;
        [Tooltip("Added mass per gem carried. Ship feels heavier when full; more momentum when braking.")]
        [SerializeField] private float massPerGem = 0.005f;

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

        // Store inventory (rockets and mines)
        private NetworkVariable<int> smallRocketsCount = new NetworkVariable<int>(0);
        private NetworkVariable<int> largeRocketsCount = new NetworkVariable<int>(0);
        private NetworkVariable<int> smallMinesCount = new NetworkVariable<int>(0);
        private NetworkVariable<int> largeMinesCount = new NetworkVariable<int>(0);

        private const float ATTR_MULTIPLIER_PER_LEVEL = 0.1f;

        private float EffectiveMovementSpeed => movementSpeed * (1f + attrMovementSpeed.Value * ATTR_MULTIPLIER_PER_LEVEL);
        private float EffectiveEnergyCapacity => energyCapacity * (1f + attrEnergyCapacity.Value * ATTR_MULTIPLIER_PER_LEVEL);
        private float DamageMultiplier => 1f + attrFirePower.Value * ATTR_MULTIPLIER_PER_LEVEL;
        private float SpeedMultiplier => 1f + attrBulletSpeed.Value * ATTR_MULTIPLIER_PER_LEVEL;
        private float EffectiveHealthRegen => healthRegenRate * (1f + attrHealthRegen.Value * ATTR_MULTIPLIER_PER_LEVEL);
        private float EffectiveRotationSpeed => rotationSpeed * (1f + attrRotationSpeed.Value * ATTR_MULTIPLIER_PER_LEVEL);
        private float EffectiveEnergyRegen => energyRegenRate * (1f + attrEnergyRegen.Value * ATTR_MULTIPLIER_PER_LEVEL);

        /// <summary>Mass increases with gems carried; affects acceleration, braking, and ramming damage. Base from ShipData when set.</summary>
        private float EffectiveMass => Mathf.Max(0.5f, (shipData != null && shipData.baseMass > 0f ? shipData.baseMass : baseMass) + currentGems.Value * massPerGem);

        /// <summary>Mining ships (ice-breaker hull) deal more ramming damage to asteroids.</summary>
        private float HullRammingToAsteroidMultiplier => focusType == ShipFocusType.Miner ? minerRammingToAsteroidMultiplier : 1f;
        /// <summary>Mining ships take less ramming self-damage (stronger hull).</summary>
        private float HullRammingSelfDamageMultiplier => focusType == ShipFocusType.Miner ? minerRammingSelfDamageMultiplier : 1f;

        private float lastRocketTime = -999f;
        private float lastMineTime = -999f;
        private const float ROCKET_COOLDOWN = 0.6f;
        private const float MINE_COOLDOWN = 1f;
        private Vector3 moveDirection = Vector3.zero;
        private Vector3 currentVelocity = Vector3.zero;
        private Planet currentOrbitPlanet; // When non-null, we're in a planet's orbit zone (any planet)
        private bool wasMovePressedLastFrame;
        /// <summary>When &lt; 0 we're stable (or not in zone). When >= 0, time when we first dropped out of stable orbit (for menu hide delay).</summary>
        private float lastTimeStableOrbitLost = -1f;
        /// <summary>True only after we've been in stable orbit at least once this zone entry; prevents menu showing on zone entry before stable.</summary>
        private bool hasReachedStableOrbitThisZoneEntry = false;
        private const float STABLE_ORBIT_HIDE_DELAY = 0.6f; // Keep menu visible this long after briefly dipping out of stable orbit

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
        public int SmallRocketsCount => smallRocketsCount.Value;
        public int LargeRocketsCount => largeRocketsCount.Value;
        public int SmallMinesCount => smallMinesCount.Value;
        public int LargeMinesCount => largeMinesCount.Value;

        private const float FIXED_Y_POSITION = 0f;

        private void Awake()
        {
            if (rb == null) rb = GetComponent<Rigidbody>();
            if (inputHandler == null) inputHandler = GetComponent<PlayerInputHandler>();
            if (energyCapacity <= 0f) energyCapacity = 50f;
            if (energyRegenRate <= 0f) energyRegenRate = 5f;

            ApplyHullIdentityColor();

            // Lock Y position - prevent elevation changes; no drag so ship can float frictionless when brakes off
            if (rb != null)
            {
                rb.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous; // Prevent tunnelling through planets/asteroids
                rb.linearDamping = 0f; // Frictionless: velocity only changes from our code (thrust/brakes/recoil)
            }

            // High-friction material so ship doesn't slip off asteroids when ramming
            Collider shipCol = GetComponent<Collider>();
            if (shipCol != null && shipCol.sharedMaterial == null)
            {
                shipCol.sharedMaterial = GetOrCreateShipRammingMaterial();
            }
        }

        private static PhysicsMaterial shipRammingMaterial;
        private static PhysicsMaterial GetOrCreateShipRammingMaterial()
        {
            if (shipRammingMaterial != null) return shipRammingMaterial;
            shipRammingMaterial = new PhysicsMaterial("ShipRamming")
            {
                dynamicFriction = 0.95f,
                staticFriction = 0.95f,
                frictionCombine = PhysicsMaterialCombine.Maximum,
                bounceCombine = PhysicsMaterialCombine.Minimum,
                bounciness = 0f
            };
            return shipRammingMaterial;
        }

        private void OnDestroy()
        {
            // Cancel any pending respawn invokes
            CancelInvoke(nameof(RespawnServerRpc));
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
            // If we have shipData but no weapon config (e.g. scene ship or old prefab), apply it so we get a valid weaponConfig (or default)
            if (shipData != null && weaponConfig == null)
                SetShipData(shipData);

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

        /// <summary>Server only: set team without repositioning (for AI ships that are already placed).</summary>
        public void AssignTeamOnly(TeamManager.Team team)
        {
            if (!IsServer) return;
            shipTeam.Value = team;
        }

        /// <summary>Server: position ship in orbit around its team's home planet at spawn.</summary>
        private void StartInOrbitAroundHomePlanet()
        {
            if (shipTeam.Value == TeamManager.Team.None || rb == null) return;
            // AI ships are placed by AIStarshipManager; don't overwrite their position
            if (GetComponent<TitanOrbit.AI.AIShipMarker>() != null) return;
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
            rb.position = orbitPos;

            float innerWorld = home.PlanetSize * 0.5f;
            float outerWorld = home.PlanetSize * 0.85f;
            float targetSpeed = GetOrbitTargetSpeed(home, orbitRadius, innerWorld, outerWorld);

            rb.linearVelocity = new Vector3(0f, 0f, -targetSpeed); // Tangent for clockwise orbit
            currentVelocity = rb.linearVelocity;
        }

        private void Update()
        {
            if (!IsOwner) return;
            // AI ships have their own controller; skip player input and orbit UI logic
            if (GetComponent<TitanOrbit.AI.AIStarshipController>() != null) return;

            HandleInput();
            bool movePressed = inputHandler != null && inputHandler.MoveForwardPressed;
            if (IsLocalPlayerShip())
            {
                var orbitUI = TitanOrbit.UI.HomePlanetOrbitUI.GetOrCreate();
                bool stable = currentOrbitPlanet != null && !movePressed && IsInStableOrbit();
                if (stable)
                {
                    lastTimeStableOrbitLost = -1f;
                    hasReachedStableOrbitThisZoneEntry = true;
                }
                else if (currentOrbitPlanet != null && !movePressed)
                {
                    if (lastTimeStableOrbitLost < 0f)
                        lastTimeStableOrbitLost = Time.time;
                }
                else
                {
                    lastTimeStableOrbitLost = -1f;
                    if (currentOrbitPlanet == null)
                        hasReachedStableOrbitThisZoneEntry = false;
                }

                float notStableDuration = lastTimeStableOrbitLost >= 0f ? Time.time - lastTimeStableOrbitLost : 0f;
                bool allowHideDelay = hasReachedStableOrbitThisZoneEntry && currentOrbitPlanet != null && !movePressed && notStableDuration < STABLE_ORBIT_HIDE_DELAY;
                bool keepMenuVisible = stable || allowHideDelay;

                if (movePressed || currentOrbitPlanet == null || !keepMenuVisible)
                    orbitUI.Hide();
                else if (currentOrbitPlanet != null && !movePressed && keepMenuVisible)
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

            // Gem load increases mass: ship feels heavier and has more momentum (slower to accelerate/brake)
            rb.mass = EffectiveMass;

            // Always lock Y position (prevents drift from physics/collisions)
            Vector3 pos = rb.position;
            if (Mathf.Abs(pos.y - FIXED_Y_POSITION) > 0.01f)
            {
                pos.y = FIXED_Y_POSITION;
                rb.position = pos;
            }
            
            // Never wrap ship position: ship stays in world space (e.g. 100, 310). All other
            // entities are repositioned around the player via ToroidalRenderer (display copy closest to camera).
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
            
            // Dead ships cannot move or rotate
            if (isDead.Value)
            {
                // Stop all movement when dead
                if (rb != null)
                {
                    Vector3 vel = rb.linearVelocity;
                    vel.y = 0f;
                    vel = Vector3.MoveTowards(vel, Vector3.zero, brakeDeceleration * Time.fixedDeltaTime);
                    rb.linearVelocity = vel;
                }
                return;
            }
            
            // AI-controlled ships have their own movement; don't apply player/orbit movement
            if (GetComponent<TitanOrbit.AI.AIStarshipController>() != null) return;
            if (!IsOwner) return;
            bool useOrbit = currentOrbitPlanet != null && inputHandler != null && !inputHandler.MoveForwardPressed;
            if (useOrbit)
            {
                HandleOrbitMovement();
                HandleRotation(); // Ship can face any direction (e.g. toward mouse) while orbiting
            }
            else
            {
                HandleMovement();
                HandleRotation();
            }
        }

        private void HandleInput()
        {
            if (inputHandler == null) return;
            
            // Dead ships cannot process input
            if (isDead.Value)
            {
                moveDirection = Vector3.zero;
                return;
            }

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
            // Don't fire when clicking on UI (e.g. orbit menu buttons) or when dead
            if (inputHandler.ShootPressed && CanAnyCannonFire() && firePoint != null && !IsPointerOverUI())
            {
                Vector3 dir = transform.forward;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
                else dir.Normalize();
                FireServerRpc(transform.position, dir);
            }

            // Rocket: Q key (or FireRocket if bound). Prefer large if available.
            if (!IsPointerOverUI() && !isDead.Value && Time.time - lastRocketTime >= ROCKET_COOLDOWN)
            {
                bool wantRocket = (inputHandler as TitanOrbit.Input.PlayerInputHandler)?.RocketPressed == true
                    || (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.qKey.isPressed);
                if (wantRocket && (SmallRocketsCount > 0 || LargeRocketsCount > 0))
                {
                    bool preferLarge = LargeRocketsCount > 0;
                    FireRocketServerRpc(preferLarge);
                    lastRocketTime = Time.time;
                }
            }

            // Mine: E key. Place in front of ship.
            if (!IsPointerOverUI() && !isDead.Value && Time.time - lastMineTime >= MINE_COOLDOWN)
            {
                bool wantMine = (inputHandler as TitanOrbit.Input.PlayerInputHandler)?.MinePressed == true
                    || (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.eKey.isPressed);
                if (wantMine && (SmallMinesCount > 0 || LargeMinesCount > 0))
                {
                    bool preferLarge = LargeMinesCount > 0;
                    Vector3 placePos = transform.position + transform.forward * 3f;
                    placePos.y = 0f;
                    PlaceMineServerRpc(placePos, preferLarge);
                    lastMineTime = Time.time;
                }
            }
        }

        private static bool IsPointerOverUI()
        {
            return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        }

        private void HandleMovement()
        {
            // Sync from rigidbody so recoil (AddForce) is included in our velocity
            currentVelocity = rb.linearVelocity;
            currentVelocity.y = 0f;

            float mass = Mathf.Max(0.5f, rb.mass);
            // Heavier ship (more gems) = slower acceleration and braking (more momentum)
            float effectiveAccel = acceleration / mass;
            float effectiveBrake = brakeDeceleration / mass;
            float effectiveRecoilDecay = recoilDecayPerSecond / mass;

            float maxSpeed = EffectiveMovementSpeed;
            bool brakesOn = (inputHandler as TitanOrbit.Input.PlayerInputHandler)?.SpaceBrakesEnabled ?? true;

            if (moveDirection.magnitude > 0.1f)
            {
                currentVelocity += moveDirection * effectiveAccel * Time.fixedDeltaTime;
                if (currentVelocity.magnitude > maxSpeed)
                    currentVelocity = currentVelocity.normalized * maxSpeed;
            }
            else
            {
                if (brakesOn)
                    currentVelocity = Vector3.MoveTowards(currentVelocity, Vector3.zero, effectiveBrake * Time.fixedDeltaTime);
                // else: frictionless float (keep currentVelocity unchanged)
            }

            // Ensure velocity has no Y component
            currentVelocity.y = 0f;

            // Cap at max speed; if over (e.g. from recoil), decay back toward max over time (heavier = slower decay)
            float mag = currentVelocity.magnitude;
            if (mag > maxSpeed && maxSpeed > 0.001f)
            {
                float targetMag = Mathf.MoveTowards(mag, maxSpeed, effectiveRecoilDecay * Time.fixedDeltaTime);
                currentVelocity = currentVelocity.normalized * targetMag;
            }

            rb.linearVelocity = currentVelocity;
            // Do not use MovePosition - let physics move the body so collisions with planets/asteroids block properly
        }

        private void HandleOrbitMovement()
        {
            if (currentOrbitPlanet == null || rb == null) return;

            Vector3 planetPos = currentOrbitPlanet.transform.position;
            Vector3 toShip = rb.position - planetPos;
            toShip.y = 0f;
            float dist = toShip.magnitude;
            if (dist < 0.01f) return;

            // Orbit zone: inner 0.5 to outer 0.85 (world = planet size * local). Ship keeps whatever radius it entered.
            float innerWorld = currentOrbitPlanet.PlanetSize * 0.5f;
            float outerWorld = currentOrbitPlanet.PlanetSize * 0.85f;
            Vector3 radial = toShip / dist;

            // Clockwise tangent (viewed from above): (radial.z, 0, -radial.x).
            float targetSpeed = GetOrbitTargetSpeed(currentOrbitPlanet, dist, innerWorld, outerWorld);
            Vector3 tangent = new Vector3(radial.z, 0f, -radial.x);
            Vector3 desiredOrbitVelocity = tangent * targetSpeed;

            // Only nudge back when outside the band so ships stay in zone but keep their lane.
            Vector3 radialCorrection = Vector3.zero;
            if (dist < innerWorld)
                radialCorrection += radial * orbitRadiusPullStrength;
            else if (dist > outerWorld)
                radialCorrection -= radial * orbitRadiusPullStrength;

            desiredOrbitVelocity += radialCorrection;

            // Blend from current velocity toward desired orbit velocity. Heavier ship = more momentum = slower to align.
            Vector3 currentVel = rb.linearVelocity;
            currentVel.y = 0f;

            float mass = Mathf.Max(0.5f, rb.mass);
            float gravityFactor = GetOrbitGravityFactor(currentOrbitPlanet, dist, innerWorld, outerWorld);
            float alignRate = (orbitCaptureResponsiveness * gravityFactor) / mass;
            float t = Mathf.Clamp01(alignRate * Time.fixedDeltaTime);

            Vector3 blendedVelocity = Vector3.Lerp(currentVel, desiredOrbitVelocity, t);
            blendedVelocity.y = 0f;

            currentVelocity = blendedVelocity;
            rb.linearVelocity = blendedVelocity;
            // Do not use MovePosition - let physics move the body so collisions block properly
            // Rotation is handled by HandleRotation (mouse); ship can face any direction while orbiting.
        }

        /// <summary>
        /// Computes the ideal orbit linear speed for a given planet and radius.
        /// Closer orbits and larger planets yield faster orbital speeds.
        /// </summary>
        private float GetOrbitTargetSpeed(Planet planet, float radius, float innerWorld, float outerWorld)
        {
            if (planet == null)
                return orbitSpeed;

            float clampedRadius = Mathf.Clamp(radius, innerWorld, outerWorld);
            // 0 at outer edge of orbit band, 1 near the planet surface.
            float radiusFactor = Mathf.InverseLerp(outerWorld, innerWorld, clampedRadius);

            // Normalize planet size using the same rough range regular planets use (4–12), but works for home planets too.
            const float minSize = 4f;
            const float maxSize = 12f;
            float sizeNorm = Mathf.Clamp01((planet.PlanetSize - minSize) / (maxSize - minSize));

            // Bigger planets and tighter orbits move noticeably faster.
            float sizeMultiplier = Mathf.Lerp(0.8f, 1.4f, sizeNorm);     // Small → big planet
            float radiusMultiplier = Mathf.Lerp(0.7f, 1.6f, radiusFactor); // Outer → inner orbit

            return orbitSpeed * sizeMultiplier * radiusMultiplier;
        }

        /// <summary>
        /// Gravity-style factor used for how strongly we steer toward the orbit velocity.
        /// Larger planets and closer orbits pull velocity into alignment more quickly.
        /// </summary>
        private float GetOrbitGravityFactor(Planet planet, float radius, float innerWorld, float outerWorld)
        {
            if (planet == null)
                return 1f;

            float clampedRadius = Mathf.Clamp(radius, innerWorld, outerWorld);
            float radiusFactor = Mathf.InverseLerp(outerWorld, innerWorld, clampedRadius); // 0 outer, 1 inner

            const float minSize = 4f;
            const float maxSize = 12f;
            float sizeNorm = Mathf.Clamp01((planet.PlanetSize - minSize) / (maxSize - minSize));

            // Base 1x, up to roughly ~2.7x for large planets and inner orbits.
            float gravityFactor = 1f + 0.7f * sizeNorm + 1.0f * radiusFactor;
            return gravityFactor;
        }

        /// <summary>True when in orbit zone and velocity is aligned with orbital path and speed is close to target (i.e. "true orbit" for UI).</summary>
        private bool IsInStableOrbit()
        {
            if (currentOrbitPlanet == null || rb == null) return false;

            Vector3 planetPos = currentOrbitPlanet.transform.position;
            Vector3 toShip = rb.position - planetPos;
            toShip.y = 0f;
            float dist = toShip.magnitude;
            float innerWorld = currentOrbitPlanet.PlanetSize * 0.5f;
            float outerWorld = currentOrbitPlanet.PlanetSize * 0.85f;
            if (dist < innerWorld || dist > outerWorld) return false;

            Vector3 radial = toShip / dist;
            Vector3 tangent = new Vector3(radial.z, 0f, -radial.x);
            float targetSpeed = GetOrbitTargetSpeed(currentOrbitPlanet, dist, innerWorld, outerWorld);
            if (targetSpeed < 0.001f) return false;

            Vector3 vel = rb.linearVelocity;
            vel.y = 0f;
            float speed = vel.magnitude;
            if (speed < 0.001f) return false;

            float alignment = Vector3.Dot(vel.normalized, tangent);
            float speedRatio = speed / targetSpeed;
            // Strict thresholds: truly in orbit (~23° alignment, speed within ~30% of target). Buffer for not flickering is in Update (hide delay).
            return alignment >= 0.92f && speedRatio >= 0.7f && speedRatio <= 1.35f;
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
            // Health can regen even when at zero - regen is allowed
            // Only prevent regen when dead
            if (IsServer && !isDead.Value && currentHealth.Value < MaxHealth)
            {
                float regen = EffectiveHealthRegen * Time.deltaTime;
                if (GameManager.Instance != null && GameManager.Instance.DebugMode) regen *= 100f;
                float newHealth = currentHealth.Value + regen;
                // Ensure health never exceeds MaxHealth
                currentHealth.Value = Mathf.Min(newHealth, MaxHealth);
            }
            // Safety check: clamp health to zero minimum (shouldn't go negative)
            if (IsServer && currentHealth.Value < 0f)
            {
                currentHealth.Value = 0f;
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

        private bool CanAnyCannonFire()
        {
            if (isDead.Value) return false;
            var wc = EffectiveWeaponConfig;
            EnsureCannonLastFireTime();
            for (int i = 0; i < wc.cannons.Count; i++)
            {
                var c = wc.cannons[i];
                if (currentEnergy.Value >= c.energyCostPerShot &&
                    (i >= cannonLastFireTime.Length || Time.time - cannonLastFireTime[i] >= 1f / c.fireRate))
                    return true;
            }
            return false;
        }

        [ServerRpc]
        private void FireServerRpc(Vector3 shipPosition, Vector3 shipForward)
        {
            if (CombatSystem.Instance == null) return;
            var wc = EffectiveWeaponConfig;
            EnsureCannonLastFireTime();
            Vector3 forward = shipForward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            else forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 fireOrigin = firePoint != null ? firePoint.position : shipPosition + forward * 2f;
            Vector3 shipVel = rb != null ? rb.linearVelocity : Vector3.zero;
            shipVel.y = 0f;

            for (int i = 0; i < wc.cannons.Count; i++)
            {
                var c = wc.cannons[i];
                if (currentEnergy.Value < c.energyCostPerShot) continue;
                if (i < cannonLastFireTime.Length && Time.time - cannonLastFireTime[i] < 1f / c.fireRate) continue;

                currentEnergy.Value = Mathf.Max(0f, currentEnergy.Value - c.energyCostPerShot);
                if (i < cannonLastFireTime.Length) cannonLastFireTime[i] = Time.time;

                float baseDirAngle = c.directionAngle * Mathf.Deg2Rad;
                Vector3 baseDir = (forward * Mathf.Cos(baseDirAngle) + right * Mathf.Sin(baseDirAngle)).normalized;
                int numShots = 1;
                float angleMin = c.spreadAngleMin, angleMax = c.spreadAngleMax;
                if (c.spreadType == CannonSpreadType.FixedSpread && c.spreadProjectileCount > 1)
                {
                    numShots = Mathf.Max(1, c.spreadProjectileCount);
                }
                for (int s = 0; s < numShots; s++)
                {
                    Vector3 dir = baseDir;
                    if (c.spreadType == CannonSpreadType.RandomSpread)
                    {
                        float spread = Random.Range(c.spreadAngleMin, c.spreadAngleMax) * Mathf.Deg2Rad;
                        dir = (baseDir * Mathf.Cos(spread) + right * Mathf.Sin(spread)).normalized;
                    }
                    else if (c.spreadType == CannonSpreadType.FixedSpread && numShots > 1)
                    {
                        float t = numShots == 1 ? 0.5f : (float)s / (numShots - 1);
                        float spread = Mathf.Lerp(angleMin, angleMax, t) * Mathf.Deg2Rad;
                        dir = (baseDir * Mathf.Cos(spread) + right * Mathf.Sin(spread)).normalized;
                    }
                    Vector3 offset = forward * c.localOffsetZ + right * c.localOffsetX;
                    float damage = c.damagePerBullet * DamageMultiplier;
                    float speed = c.bulletSpeed * SpeedMultiplier;
                    float scale = c.bulletScale * (0.65f + damage / 50f);
                    byte shapeIndex = 0; // TODO: from weapon config or player preference
                    CombatSystem.Instance.SpawnBulletServerRpc(fireOrigin + offset, dir, speed, damage, shipTeam.Value, scale, shapeIndex, shipVel);
                    // Recoil: scaled down so it nudges the ship without throwing it; scales with bullet size and damage
                    if (rb != null)
                    {
                        // Same impulse regardless of mass: empty ship feels more recoil, heavy ship absorbs it better
                        float recoilImpulse = recoilStrength * scale * (0.25f + damage / 150f);
                        rb.AddForce(-dir * recoilImpulse, ForceMode.Impulse);
                    }
                }
            }
            FireClientRpc();
        }

        [ClientRpc]
        private void FireClientRpc()
        {
            // Visual/audio feedback for firing
        }

        /// <summary>Server-only: AI ships call this to fire at a target.</summary>
        public void FireAtTarget(Vector3 direction)
        {
            if (!IsServer) return;
            if (isDead.Value) return;
            if (!CanAnyCannonFire()) return;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f) direction = transform.forward;
            else direction.Normalize();
            FireServerRpc(transform.position, direction);
        }

        [ServerRpc]
        private void FireRocketServerRpc(bool preferLarge)
        {
            // Dead ships cannot fire rockets
            if (isDead.Value) return;
            bool useLarge = preferLarge && ConsumeLargeRocket();
            if (!useLarge && !ConsumeSmallRocket()) return;
            if (firePoint == null) return;
            Vector3 dir = transform.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
            else dir.Normalize();
            if (CombatSystem.Instance != null)
                CombatSystem.Instance.SpawnRocketServerRpc(firePoint.position, dir, useLarge, shipTeam.Value);
        }

        [ServerRpc]
        private void PlaceMineServerRpc(Vector3 position, bool preferLarge)
        {
            // Dead ships cannot place mines
            if (isDead.Value) return;
            bool useLarge = preferLarge && ConsumeLargeMine();
            if (!useLarge && !ConsumeSmallMine()) return;
            Vector3 pos = TitanOrbit.Generation.ToroidalMap.WrapPosition(position);
            pos.y = 0f;
            if (CombatSystem.Instance != null)
                CombatSystem.Instance.SpawnMineServerRpc(pos, useLarge, shipTeam.Value);
        }

        private NetworkVariable<bool> isDead = new NetworkVariable<bool>(false);

        [ServerRpc(RequireOwnership = false)]
        public void TakeDamageServerRpc(float damage, TeamManager.Team attackerTeam)
        {
            // Block friendly fire only when both have valid teams and they match
            if (attackerTeam != TeamManager.Team.None && attackerTeam == shipTeam.Value) return;
            if (isDead.Value) return;

            if (currentHealth.Value > 0f)
            {
                // Phase 1: Reduce health until it reaches zero
                currentHealth.Value = Mathf.Max(0f, currentHealth.Value - damage);
            }
            else
            {
                // Phase 2: Health is zero - bullets drain gems and expel them
                float gemsToExpel = Mathf.Min(damage, currentGems.Value);
                if (gemsToExpel > 0f && GemSpawner.Instance != null)
                {
                    currentGems.Value = Mathf.Max(0f, currentGems.Value - gemsToExpel);
                    ulong myId = GetComponent<NetworkObject>()?.NetworkObjectId ?? 0;
                    GemSpawner.Instance.SpawnGemsFromShipServerRpc(transform.position, gemsToExpel, myId);
                }
                else if (gemsToExpel > 0f)
                {
                    currentGems.Value = Mathf.Max(0f, currentGems.Value - gemsToExpel);
                }
            }

            // Check for death - use small epsilon to handle floating point precision
            const float DEATH_THRESHOLD = 0.001f;
            if (currentHealth.Value <= DEATH_THRESHOLD && currentGems.Value <= DEATH_THRESHOLD)
            {
                DieServerRpc();
            }
        }

        private void HandleDeath()
        {
            if (isDead.Value) return;
            // Death is triggered in TakeDamageServerRpc when health and gems both reach 0
            // No passive gem drain - gems only reduce when bullets hit (and get expelled)
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

        /// <summary>Server: continuous gem deposit at shipLevel gems per 0.5s while in orbit at planet (same team).</summary>
        private void TickOrbitGemDeposit()
        {
            if (currentOrbitPlanet == null || !wantToDepositGems.Value) return;
            
            // Check if planet is owned by same team (or is home planet with assigned team)
            bool canDeposit = false;
            if (currentOrbitPlanet is HomePlanet home)
            {
                canDeposit = home.AssignedTeam == shipTeam.Value;
            }
            else
            {
                canDeposit = currentOrbitPlanet.TeamOwnership == shipTeam.Value;
            }
            
            if (!canDeposit) return;
            if (currentGems.Value <= 0f) { wantToDepositGems.Value = false; return; }

            // shipLevel gems per 0.5 sec = shipLevel * 2 per second
            float rate = shipLevel * 2f * Time.fixedDeltaTime;
            if (GameManager.Instance != null && GameManager.Instance.DebugMode) rate *= 100f;
            if (rate <= 0f) return;
            float amount = Mathf.Min(rate, currentGems.Value);
            if (amount > 0f)
            {
                RemoveGemsServerRpc(amount);
                ulong clientId = OwnerClientId;
                if (currentOrbitPlanet is HomePlanet homePlanet)
                {
                    homePlanet.DepositGemsServerRpc(amount, shipTeam.Value, clientId);
                }
                else
                {
                    currentOrbitPlanet.DepositGemsServerRpc(amount, shipTeam.Value, clientId);
                }
            }
            if (currentGems.Value <= 0.001f)
                wantToDepositGems.Value = false;
        }

        [Header("Respawn Settings")]
        [SerializeField] private float respawnDelay = 5f;

        [ServerRpc(RequireOwnership = false)]
        private void DieServerRpc()
        {
            if (isDead.Value) return;
            isDead.Value = true;
            
            // Stop all movement immediately when dead
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                currentVelocity = Vector3.zero;
                moveDirection = Vector3.zero;
            }
            
            // Hide ship visually and spawn explosion
            HideShipVisuals();
            SpawnDeathExplosion();
            
            // Delay respawn by 5 seconds
            Invoke(nameof(RespawnServerRpc), respawnDelay);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RespawnServerRpc()
        {
            // Reset stats
            currentHealth.Value = MaxHealth;
            currentGems.Value = 0f;
            currentPeople.Value = 0f;
            currentEnergy.Value = EffectiveEnergyCapacity;
            isDead.Value = false;
            
            // Show ship visuals again
            ShowShipVisuals();
            
            // Respawn at home planet (for both player and AI ships)
            RespawnAtHomePlanet();
        }

        /// <summary>Server: respawn ship at home planet (called on death/respawn).</summary>
        private void RespawnAtHomePlanet()
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
            rb.position = orbitPos;

            float innerWorld = home.PlanetSize * 0.5f;
            float outerWorld = home.PlanetSize * 0.85f;
            float targetSpeed = GetOrbitTargetSpeed(home, orbitRadius, innerWorld, outerWorld);

            rb.linearVelocity = new Vector3(0f, 0f, -targetSpeed); // Tangent for clockwise orbit
            currentVelocity = rb.linearVelocity;
        }

        /// <summary>Hide all renderers to make ship invisible when dead.</summary>
        private void HideShipVisuals()
        {
            HideShipVisualsClientRpc();
        }

        [ClientRpc]
        private void HideShipVisualsClientRpc()
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer != null)
                    renderer.enabled = false;
            }
            
            // Also disable colliders so dead ships don't interfere
            Collider[] colliders = GetComponentsInChildren<Collider>(true);
            foreach (var collider in colliders)
            {
                if (collider != null)
                    collider.enabled = false;
            }
        }

        /// <summary>Show all renderers to make ship visible again on respawn.</summary>
        private void ShowShipVisuals()
        {
            ShowShipVisualsClientRpc();
        }

        [ClientRpc]
        private void ShowShipVisualsClientRpc()
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer != null)
                    renderer.enabled = true;
            }
            
            // Re-enable colliders
            Collider[] colliders = GetComponentsInChildren<Collider>(true);
            foreach (var collider in colliders)
            {
                if (collider != null)
                    collider.enabled = true;
            }
        }

        /// <summary>Spawn explosion effect at ship position when it dies.</summary>
        private void SpawnDeathExplosion()
        {
            if (VisualEffectsManager.Instance != null)
            {
                Vector3 explosionPos = transform.position;
                explosionPos.y = 0f;
                VisualEffectsManager.Instance.SpawnExplosionServerRpc(explosionPos);
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!IsServer || rb == null) return;
            if (collision.contactCount == 0) return;

            ContactPoint contact = collision.GetContact(0);
            Asteroid asteroid = collision.gameObject.GetComponent<Asteroid>();

            if (asteroid != null && !asteroid.IsDestroyed && contact.separation < 0f)
            {
                // Overlapping (e.g. asteroid respawned on ship): gentle escape push
                Vector3 normal = contact.normal;
                normal.y = 0f;
                if (normal.sqrMagnitude > 0.01f)
                {
                    normal.Normalize();
                    Vector3 vel = rb.linearVelocity;
                    vel.y = 0f;
                    vel += normal * (overlapEscapeSpeed * Time.fixedDeltaTime);
                    rb.linearVelocity = new Vector3(vel.x, 0f, vel.z);
                    currentVelocity = rb.linearVelocity;
                }
                return;
            }

            if (asteroid != null && !asteroid.IsDestroyed)
            {
                var no = asteroid.GetComponent<NetworkObject>();
                if (no != null && no.IsSpawned)
                {
                    float impactSpeed = collision.relativeVelocity.magnitude;
                    impactSpeed = Mathf.Max(0f, impactSpeed - 0.5f);
                    float mass = Mathf.Max(0.5f, rb.mass);
                    float momentum = mass * impactSpeed;
                    float damage = baseRammingDamage + rammingDamagePerSpeedUnit * impactSpeed + rammingMomentumDamageScale * momentum;
                    damage = Mathf.Max(2f, damage);

                    float toAsteroid = damage * HullRammingToAsteroidMultiplier;
                    float toSelf = damage * HullRammingSelfDamageMultiplier;
                    asteroid.TakeDamageServerRpc(toAsteroid);
                    TakeDamageServerRpc(toSelf, TeamManager.Team.None);
                }
            }
        }

        /// <summary>
        /// When pushing into an asteroid, cancel tangential velocity so the ship sustains pressure and doesn't slip off.
        /// When overlapping (e.g. asteroid respawned on top of ship), gently push the ship outward for a smooth escape.
        /// Contact normal points from asteroid toward ship.
        /// </summary>
        private void OnCollisionStay(Collision collision)
        {
            if (!IsServer || rb == null || isDead.Value) return;
            Asteroid asteroid = collision.gameObject.GetComponent<Asteroid>();
            if (asteroid == null || asteroid.IsDestroyed || collision.contactCount == 0) return;

            ContactPoint contact = collision.GetContact(0);
            Vector3 normal = contact.normal;
            normal.y = 0f;
            if (normal.sqrMagnitude < 0.01f) return;
            normal.Normalize();

            // Overlapping (e.g. asteroid respawned on ship): gently and progressively push ship outward
            // separation < 0 means penetration
            if (contact.separation < 0f)
            {
                Vector3 outward = normal; // normal points from asteroid to ship = outward
                Vector3 vel = rb.linearVelocity;
                vel.y = 0f;
                vel += outward * (overlapEscapeSpeed * Time.fixedDeltaTime);
                rb.linearVelocity = new Vector3(vel.x, 0f, vel.z);
                currentVelocity = rb.linearVelocity;
                return; // don't do ramming stick/damage while we're escaping overlap
            }

            // Into asteroid = opposite of normal (normal points from asteroid to us)
            Vector3 intoAsteroid = -normal;
            Vector3 vel2 = rb.linearVelocity;
            vel2.y = 0f;
            float pushAmount = Vector3.Dot(vel2, intoAsteroid);
            if (pushAmount <= 0f) return; // Not pushing in

            // Keep only the "pushing in" component so we don't slide sideways
            Vector3 ramVelocity = intoAsteroid * pushAmount;
            rb.linearVelocity = new Vector3(ramVelocity.x, 0f, ramVelocity.z);
            currentVelocity = rb.linearVelocity;

            // Sustained ramming damage (tick at interval). Scaled by mass (pressure) and hull type.
            if (Time.time - lastRamDamageTime >= ramTickInterval)
            {
                lastRamDamageTime = Time.time;
                float mass = Mathf.Max(0.5f, rb.mass);
                float baseTick = ramDamagePerSecond * ramTickInterval;
                float massScale = Mathf.Sqrt(mass); // so damage scales with mass but not too harshly
                float tickToAsteroid = baseTick * massScale * HullRammingToAsteroidMultiplier;
                float tickToSelf = baseTick * massScale * HullRammingSelfDamageMultiplier;
                asteroid.TakeDamageServerRpc(tickToAsteroid);
                TakeDamageServerRpc(tickToSelf, TeamManager.Team.None);
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
        public void AddSmallRocketsServerRpc(int count) { smallRocketsCount.Value += count; }
        [ServerRpc(RequireOwnership = false)]
        public void AddLargeRocketsServerRpc(int count) { largeRocketsCount.Value += count; }
        [ServerRpc(RequireOwnership = false)]
        public void AddSmallMinesServerRpc(int count) { smallMinesCount.Value += count; }
        [ServerRpc(RequireOwnership = false)]
        public void AddLargeMinesServerRpc(int count) { largeMinesCount.Value += count; }

        /// <summary>Server: consume one small rocket. Returns true if had one.</summary>
        public bool ConsumeSmallRocket()
        {
            if (smallRocketsCount.Value <= 0) return false;
            smallRocketsCount.Value--;
            return true;
        }
        public bool ConsumeLargeRocket()
        {
            if (largeRocketsCount.Value <= 0) return false;
            largeRocketsCount.Value--;
            return true;
        }
        public bool ConsumeSmallMine()
        {
            if (smallMinesCount.Value <= 0) return false;
            smallMinesCount.Value--;
            return true;
        }
        public bool ConsumeLargeMine()
        {
            if (largeMinesCount.Value <= 0) return false;
            largeMinesCount.Value--;
            return true;
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

        /// <summary>Server-only: set wantToLoadPeople (for AI ships; bypasses RPC ownership).</summary>
        public void SetWantToLoadPeopleFromServer(bool value)
        {
            if (!IsServer) return;
            wantToLoadPeople.Value = value;
            if (value) wantToUnloadPeople.Value = false;
        }

        /// <summary>Server-only: set wantToUnloadPeople (for AI ships; bypasses RPC ownership).</summary>
        public void SetWantToUnloadPeopleFromServer(bool value)
        {
            if (!IsServer) return;
            wantToUnloadPeople.Value = value;
            if (value) wantToLoadPeople.Value = false;
        }

        /// <summary>Server-only: set wantToDepositGems (for AI ships; bypasses RPC ownership).</summary>
        public void SetWantToDepositGemsFromServer(bool value)
        {
            if (!IsServer) return;
            wantToDepositGems.Value = value;
        }

        /// <summary>Owner-only: detect if we're inside a planet's orbit zone (e.g. after spawning there).</summary>
        private void TryDetectOrbitZone()
        {
            if (rb == null || currentOrbitPlanet != null) return;
            if (!IsLocalPlayerShip()) return;
            foreach (var planet in Object.FindObjectsOfType<Planet>())
            {
                if (planet == null) continue;
                Vector3 toShip = rb.position - planet.transform.position;
                toShip.y = 0f;
                float dist = toShip.magnitude;
                float inner = planet.PlanetSize * 0.5f;
                float outer = planet.PlanetSize * 0.85f;
                if (dist >= inner && dist <= outer)
                {
                    currentOrbitPlanet = planet;
                    // Menu shows in Update when IsInStableOrbit() is true
                    break;
                }
            }
        }

        /// <summary>True if this ship is the local player's ship (not AI or other players).</summary>
        private bool IsLocalPlayerShip()
        {
            if (NetworkManager.Singleton == null) return false;
            var localPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
            return localPlayer != null && localPlayer == GetComponent<NetworkObject>();
        }

        /// <summary>Called by PlanetOrbitZone when ship enters the orbit/loading zone. Menu is shown only once in stable orbit (see Update).</summary>
        public void EnterOrbitZone(Planet planet)
        {
            if (planet == null) return;
            currentOrbitPlanet = planet;
            // Menu shows in Update when IsInStableOrbit() is true, not on zone entry
        }

        /// <summary>Called by PlanetOrbitZone when ship leaves the orbit zone.</summary>
        /// <remarks>Load/unload toggles are not cleared here so they don't reset when the ship briefly exits the zone (e.g. orbit wobble). They only reset in TickOrbitPopulationTransfer when transfer is complete (ship full or empty).</remarks>
        public void ExitOrbitZone(Planet planet)
        {
            if (currentOrbitPlanet == planet)
            {
                currentOrbitPlanet = null;
                if (IsLocalPlayerShip())
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
                // When ship levels up, reset all ability upgrades so player can re-upgrade 0..newLevel per attribute
                if (IsServer && data.shipLevel > shipLevel)
                    ResetAttributeLevels();
                shipLevel = data.shipLevel;
                focusType = data.focusType;
                movementSpeed = data.baseMovementSpeed;
                weaponConfig = data.weaponConfig != null && data.weaponConfig.cannons != null && data.weaponConfig.cannons.Count > 0
                    ? data.weaponConfig
                    : GetDefaultWeaponConfig();
                cannonLastFireTime = new float[weaponConfig.cannons.Count];
                for (int i = 0; cannonLastFireTime != null && i < cannonLastFireTime.Length; i++) cannonLastFireTime[i] = -999f;

                maxHealth = data.baseMaxHealth;
                healthRegenRate = data.baseHealthRegenRate;
                rotationSpeed = data.baseRotationSpeed;
                gemCapacity = data.baseGemCapacity;
                peopleCapacity = data.basePeopleCapacity;
                energyCapacity = data.baseEnergyCapacity;
                energyRegenRate = data.baseEnergyRegenRate;

                if (data.shipPrefab != null)
                    ApplyShipVisual(data.shipPrefab);
                if (data.visualScale > 0f)
                {
                    Transform root = visualRoot != null ? visualRoot : transform;
                    root.localScale = Vector3.one * data.visualScale;
                }
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

        /// <summary>Server only: resets all attribute upgrade levels to 0. Called when ship levels up.</summary>
        private void ResetAttributeLevels()
        {
            if (!IsServer) return;
            attrMovementSpeed.Value = 0;
            attrEnergyCapacity.Value = 0;
            attrFirePower.Value = 0;
            attrBulletSpeed.Value = 0;
            attrMaxHealth.Value = 0;
            attrHealthRegen.Value = 0;
            attrRotationSpeed.Value = 0;
            attrEnergyRegen.Value = 0;
        }
    }
}
