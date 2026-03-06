using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Unity.Netcode;
using UnityEngine.InputSystem;
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
    [DefaultExecutionOrder(60000)] // Run last so banking is not overwritten by transform sync or other LateUpdates
    public class Starship : NetworkBehaviour
    {
        [Header("Ship Settings")]
        [SerializeField] private ShipData shipData;
        /// <summary>Current ship data (model, weapon config, stats). Used so AI can match player ship.</summary>
        public ShipData CurrentShipData => shipData;
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
        [SerializeField] private float recoilStrength = 1.2f;

        /// <summary>Bullet fire points (Weapon components only; Cockpit cannons removed).</summary>
        private List<Transform> bulletFirePoints = new List<Transform>();
        /// <summary>Muzzle particle systems at each bullet (Weapon) position.</summary>
        private List<ParticleSystem> bulletMuzzleParticleSystems = new List<ParticleSystem>();

        [Header("Chassis VFX (Engine/Thruster)")]
        [Tooltip("Optional: VFX prefab for engine components (movement). e.g. AllIn1VfxToolkit Blue Fire or Real Fire.")]
        [SerializeField] private GameObject engineVfxPrefab;
        [Tooltip("Optional: VFX prefab for thruster components (rotation). e.g. AllIn1VfxToolkit Fire Trail.")]
        [SerializeField] private GameObject thrusterVfxPrefab;
        private List<GameObject> engineVfxInstances = new List<GameObject>();
        private List<GameObject> thrusterVfxInstances = new List<GameObject>();
        private List<ParticleSystem> engineParticleSystems = new List<ParticleSystem>();
        private List<ParticleSystem> thrusterParticleSystems = new List<ParticleSystem>();

        private WeaponConfig weaponConfig;
        /// <summary>Bullets from Weapon: light projectiles, low energy. Only weapons fire; cockpits do not.</summary>
        private WeaponConfig bulletConfig;
        private float[] bulletLastFireTime;

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

        /// <summary>Always returns a valid config for legacy (bullets only). When chassis is applied, bulletConfig is set from Weapon components.</summary>
        private WeaponConfig EffectiveWeaponConfig =>
            (weaponConfig != null && weaponConfig.cannons != null && weaponConfig.cannons.Count > 0)
                ? weaponConfig
                : GetDefaultWeaponConfig();

        private void EnsureBulletLastFireTime()
        {
            var bulletWc = bulletConfig ?? EffectiveWeaponConfig;
            int bn = bulletWc.cannons != null ? bulletWc.cannons.Count : 0;
            if (bulletLastFireTime == null || bulletLastFireTime.Length != bn)
            {
                bulletLastFireTime = new float[bn];
                for (int i = 0; i < bn; i++) bulletLastFireTime[i] = -999f;
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
        [Tooltip("Optional: child transform whose visuals are replaced when upgrading to a new ship prefab. If null, direct children of this transform are replaced. Also the transform we tilt for banking; if null at Start, a pivot is created so banking works.")]
        [SerializeField] private Transform visualRoot;
        [SerializeField] private float shipVisualScaleMultiplier = 0.175f;

        [Header("Banking (fallback when shipData has no values)")]
        [SerializeField] private float defaultMaxBankAngle = 111f;
        [SerializeField] private float defaultMaxPitchAngle = 77f;
        [SerializeField] private float defaultBankSmoothing = 2f;
        [SerializeField] private float defaultPitchSmoothing = 2f;

        private MaterialPropertyBlock hullColorBlock;
        private int lastVisualApplyFrame = -1;
        private GameObject lastVisualApplyPrefab;
        /// <summary>Last chassis index we applied (so we re-apply when buying a new ship). -2 = never applied; server uses this to apply default AstroEagle_01 once.</summary>
        private int _lastAppliedChassisIndex = -2;

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

        /// <summary>Index into ShipUnlockTable.entries for the current chassis (-1 = default/unknown grid). Synced so clients can show correct grid sizes.</summary>
        private NetworkVariable<int> currentChassisIndex = new NetworkVariable<int>(-1);

        [Header("Card Loadout (WIP)")]
        [Tooltip("Equipped upgrade cards for this ship. Currently server-authoritative only; stats will be derived from ShipData + these cards in a later step.")]
        [SerializeField] private List<CardData> equippedCards = new List<CardData>();

        private const float ATTR_MULTIPLIER_PER_LEVEL = 0.1f;

        // Effective stats: base ShipData + attribute upgrades + card contributions.
        private float EffectiveMovementSpeed
        {
            get
            {
                float baseWithCards = movementSpeed + GetCardMovementSpeedAdd();
                float attrScale = 1f + attrMovementSpeed.Value * ATTR_MULTIPLIER_PER_LEVEL;
                return baseWithCards * attrScale;
            }
        }

        private float EffectiveEnergyCapacity
        {
            get
            {
                float baseWithCards = energyCapacity + GetCardEnergyCapacityAdd();
                float attrScale = 1f + attrEnergyCapacity.Value * ATTR_MULTIPLIER_PER_LEVEL;
                return baseWithCards * attrScale;
            }
        }

        private float DamageMultiplier
        {
            get
            {
                float attrScale = 1f + attrFirePower.Value * ATTR_MULTIPLIER_PER_LEVEL;
                float cardScale = GetCardDamageMultiplier();
                return attrScale * cardScale;
            }
        }

        private float SpeedMultiplier
        {
            get
            {
                float attrScale = 1f + attrBulletSpeed.Value * ATTR_MULTIPLIER_PER_LEVEL;
                float cardScale = GetCardBulletSpeedMultiplier();
                return attrScale * cardScale;
            }
        }

        private float EffectiveHealthRegen
        {
            get
            {
                float baseWithCards = healthRegenRate + GetCardHealthRegenAdd();
                float attrScale = 1f + attrHealthRegen.Value * ATTR_MULTIPLIER_PER_LEVEL;
                return baseWithCards * attrScale;
            }
        }

        private float EffectiveRotationSpeed
        {
            get
            {
                float baseWithCards = rotationSpeed + GetCardRotationSpeedAdd();
                float attrScale = 1f + attrRotationSpeed.Value * ATTR_MULTIPLIER_PER_LEVEL;
                return baseWithCards * attrScale;
            }
        }

        private float EffectiveEnergyRegen
        {
            get
            {
                float baseWithCards = energyRegenRate + GetCardEnergyRegenAdd();
                float attrScale = 1f + attrEnergyRegen.Value * ATTR_MULTIPLIER_PER_LEVEL;
                return baseWithCards * attrScale;
            }
        }

        /// <summary>Mass increases with gems carried and equipped cards; affects acceleration, braking, and ramming damage. Base from ShipData when set.</summary>
        private float EffectiveMass
        {
            get
            {
                float baseValue = shipData != null && shipData.baseMass > 0f ? shipData.baseMass : baseMass;
                float cardMass = GetCardMassContribution();
                return Mathf.Max(0.5f, baseValue + cardMass + currentGems.Value * massPerGem);
            }
        }

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

        // Banking (visual lean into turn) - only used when visualRoot is set
        private float currentBankAngle;
        private float currentPitchAngle;
        private Vector3 previousForward;
        private float previousForwardSpeed;
        private bool bankingInitialized;

        public float CurrentHealth => currentHealth.Value;
        public float MaxHealth
        {
            get
            {
                float baseWithCards = maxHealth + GetCardMaxHealthAdd();
                float attrScale = 1f + attrMaxHealth.Value * ATTR_MULTIPLIER_PER_LEVEL;
                return baseWithCards * attrScale;
            }
        }
        public float CurrentGems => currentGems.Value;
        public bool IsDead => isDead.Value;
        /// <summary>Max gem capacity. Base = 20 * Level^2; level is max(shipLevel, chassis tier) so purchasing a higher-tier ship increases capacity. Plus card bonuses.</summary>
        public float GemCapacity => 20f * EffectiveLevelForCapacity * EffectiveLevelForCapacity + GetCardGemCapacityAdd();

        /// <summary>Level used for capacity and scale: max(ship level from upgrades, chassis tier from purchased ship). So buying a Level 2 chassis gives 20*2^2 = 80 capacity.</summary>
        public int EffectiveLevelForCapacity
        {
            get
            {
                int chassisTier = 1;
                if (CardShopSystem.Instance != null && currentChassisIndex.Value >= 0)
                {
                    var chassis = CardShopSystem.Instance.GetChassisByIndex(currentChassisIndex.Value);
                    if (chassis != null && chassis.minHomePlanetLevel > 0)
                        chassisTier = chassis.minHomePlanetLevel;
                }
                return Mathf.Max(shipLevel, chassisTier);
            }
        }
        public float CurrentPeople => currentPeople.Value;
        public float PeopleCapacity => peopleCapacity;
        public float CurrentEnergy => currentEnergy.Value;
        public float EnergyCapacity => EffectiveEnergyCapacity;
        public IReadOnlyList<CardData> EquippedCards => equippedCards;

        /// <summary>Number of card slots on this ship (1 per ship level). Each slot holds at most one card.</summary>
        public int SlotCount => Mathf.Max(1, shipLevel);

        /// <summary>True if there is at least one empty slot.</summary>
        public bool HasEmptySlot => equippedCards != null && equippedCards.Count < SlotCount;
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
        /// <summary>Chassis index in ShipUnlockTable (-1 = default). Used by UI for grid dimensions.</summary>
        public int CurrentChassisIndex => currentChassisIndex.Value;

        private const float FIXED_Y_POSITION = 0f;

        /// <summary>Scale increase per ship level (e.g. 1.2 = 20% bigger per level). Level 1 = 1.0, level 2 = 1.2, level 3 = 1.44. Used for ship visual, camera zoom, weapon scale/damage.</summary>
        private const float SCALE_PER_LEVEL = 1.2f;
        /// <summary>Scale factor for current ship level: 1.2^(level-1). Level 1 = 1, level 2 = 1.2, level 3 = 1.44, etc.</summary>
        public float LevelScaleFactor => Mathf.Pow(SCALE_PER_LEVEL, shipLevel - 1);

        /// <summary>Cached so we don't call GetComponent every frame in Update.</summary>
        private bool _isAIControlled;
        /// <summary>Base visual scale (from ShipData/chassis) without level. Applied scale = visualBaseScale * LevelScaleFactor.</summary>
        private float visualBaseScale = 1f;
        /// <summary>Prefab root localScale from the loaded model (for re-applying with level scale in LateUpdate).</summary>
        private Vector3 lastPrefabScale = Vector3.one;

        private void Awake()
        {
            _isAIControlled = GetComponent<TitanOrbit.AI.AIStarshipController>() != null;
            // Run before OnNetworkSpawn/SetShipData so the BankPivot + Prefab structure exists.
            EnsureVisualRootForBanking();

            if (rb == null) rb = GetComponent<Rigidbody>();
            if (inputHandler == null) inputHandler = GetComponent<PlayerInputHandler>();
            if (energyCapacity <= 0f) energyCapacity = 50f;
            if (energyRegenRate <= 0f) energyRegenRate = 5f;

            ApplyHullIdentityColor();

            // Lock Y position - prevent elevation changes; no drag so ship can float frictionless when brakes off
            if (rb != null)
            {
                //rb.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous; // Prevent tunnelling through planets/asteroids
                rb.linearDamping = 0f; // Frictionless: velocity only changes from our code (thrust/brakes/recoil)
            }

            // High-friction material so ship doesn't slip off asteroids when ramming
            Collider shipCol = GetComponent<Collider>();
            if (shipCol != null && shipCol.sharedMaterial == null)
            {
                shipCol.sharedMaterial = GetOrCreateShipRammingMaterial();
            }

            // Toroidal display: ship is shown at the toroidal copy closest to the local camera (so AI ships appear correctly when player has flown far).
            if (GetComponent<ToroidalRenderer>() == null)
                gameObject.AddComponent<ToroidalRenderer>();
        }

        private const string PREFAB_CONTAINER_NAME = "Prefab";

        /// <summary>
        /// Structure: Starship (empty) -> BankPivot -> Prefab -> [ship components].
        /// The root is kept empty (no mesh). BankPivot is rotated for banking.
        /// Prefab holds the loaded ship—Level 1 and upgrades are loaded the same way via ApplyShipVisual.
        /// </summary>
        private void EnsureVisualRootForBanking()
        {
            // Remove all existing visual children and mesh from root—start empty
            for (int i = transform.childCount - 1; i >= 0; i--)
                Object.Destroy(transform.GetChild(i).gameObject);

            MeshFilter mf = GetComponent<MeshFilter>();
            MeshRenderer mr = GetComponent<MeshRenderer>();
            if (mf != null) Object.Destroy(mf);
            if (mr != null) Object.Destroy(mr);

            // Create BankPivot under Starship
            GameObject pivot = new GameObject("BankPivot");
            pivot.transform.SetParent(transform, false);
            pivot.transform.localPosition = Vector3.zero;
            pivot.transform.localRotation = Quaternion.identity;
            pivot.transform.localScale = Vector3.one;

            // Create Prefab container under BankPivot (holds Level 1 ship and upgraded ships)
            GameObject prefabContainer = new GameObject(PREFAB_CONTAINER_NAME);
            prefabContainer.transform.SetParent(pivot.transform, false);
            prefabContainer.transform.localPosition = Vector3.zero;
            prefabContainer.transform.localRotation = Quaternion.identity;
            prefabContainer.transform.localScale = Vector3.one;

            visualRoot = pivot.transform;
        }

        /// <summary>Returns the Prefab transform (StarshipMain -> BankPivot -> Prefab) where the loaded ship is added.</summary>
        private Transform GetPrefabTransform()
        {
            if (visualRoot == null || visualRoot == transform) return transform;
            Transform prefab = visualRoot.Find(PREFAB_CONTAINER_NAME);
            if (prefab == null)
            {
                var go = new GameObject(PREFAB_CONTAINER_NAME);
                prefab = go.transform;
                prefab.SetParent(visualRoot, false);
                prefab.localPosition = Vector3.zero;
                prefab.localRotation = Quaternion.identity;
                prefab.localScale = Vector3.one;
            }
            return prefab;
        }

        /// <summary>Ensures firePoint is set so the owner can shoot. Creates a fallback under the prefab root if null (e.g. prefab has no FirePoint or ApplyShipVisual wasn't run).</summary>
        private void EnsureFirePoint()
        {
            if (firePoint != null || !IsOwner) return;
            Transform root = GetPrefabTransform();
            if (root == null) return;
            GameObject fp = new GameObject("FirePoint");
            fp.transform.SetParent(root, false);
            fp.transform.localPosition = new Vector3(0f, 0f, 0.55f);
            fp.transform.localRotation = Quaternion.identity;
            fp.transform.localScale = Vector3.one;
            firePoint = fp.transform;
        }

        /// <summary>
        /// Exposes the prefab container so external systems (e.g. ShipVisualComposer) can attach card-driven parts.
        /// </summary>
        public Transform GetCardVisualRoot()
        {
            return GetPrefabTransform();
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
            Renderer mr = visualRoot != null ? visualRoot.GetComponentInChildren<Renderer>() : null;
            if (mr == null) mr = GetComponent<Renderer>();
            if (mr == null) return;
            if (hullColorBlock == null) hullColorBlock = new MaterialPropertyBlock();
            mr.GetPropertyBlock(hullColorBlock);
            hullColorBlock.SetColor("_BaseColor", shipData.shipColor);
            mr.SetPropertyBlock(hullColorBlock);
        }

        public override void OnNetworkSpawn()
        {
            // Server: apply starter ship (chassis 0) first so SetShipData won't overwrite with a different prefab
            if (IsServer && !_isAIControlled && currentChassisIndex.Value == -1 && CardShopSystem.Instance != null)
            {
                GameObject starterPrefab = CardShopSystem.Instance.GetShipPrefabForChassisIndex(0);
                if (starterPrefab != null)
                {
                    ApplyShipVisualFromPrefab(starterPrefab);
                    SetCurrentChassisIndex(0);
                    _lastAppliedChassisIndex = 0;
                }
            }

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
                if (TeamManager.Instance != null)
                    shipTeam.Value = TeamManager.Instance.GetPlayerTeam(OwnerClientId);
                if (shipTeam.Value == TeamManager.Team.None)
                {
                    // Not yet chosen a team: hold ship at lobby position (off-world) until they click Join
                    if (rb != null)
                    {
                        Vector3 lobbyPos = new Vector3(0f, -10000f, 0f); // below play area
                        rb.position = lobbyPos;
                        rb.linearVelocity = Vector3.zero;
                    }
                }
                else
                    StartInOrbitAroundHomePlanet();
            }

            // Initialize banking state so first LateUpdate doesn't spike
            if (rb != null)
            {
                Vector3 fwd = rb.rotation * Vector3.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude > 0.01f)
                {
                    fwd.Normalize();
                    previousForward = fwd;
                    bankingInitialized = true;
                }
            }

            // Ship loadout grid is shown by OrbitStationUI when in orbit; no separate ShipCardGridUI needed.
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
            // Server: ensure first ship (no chassis yet) gets AstroEagle_01 visual so the first ship created is the one we want
            if (IsServer && !_isAIControlled && currentChassisIndex.Value == -1 && _lastAppliedChassisIndex == -2 && CardShopSystem.Instance != null)
            {
                GameObject prefab = CardShopSystem.Instance.GetShipPrefabForChassisIndex(0);
                if (prefab != null)
                {
                    ApplyShipVisualFromPrefab(prefab);
                    SetCurrentChassisIndex(0);
                    _lastAppliedChassisIndex = 0;
                }
            }
            // Owner: when chassis index is set (or synced), apply that ship visual so client sees the correct model
            if (IsOwner && currentChassisIndex.Value >= 0 && currentChassisIndex.Value != _lastAppliedChassisIndex && CardShopSystem.Instance != null)
            {
                GameObject prefab = CardShopSystem.Instance.GetShipPrefabForChassisIndex(currentChassisIndex.Value);
                if (prefab != null)
                {
                    ApplyShipVisualFromPrefab(prefab);
                    _lastAppliedChassisIndex = currentChassisIndex.Value;
                }
            }

            if (!IsOwner) return;
            // AI ships have their own controller; skip player input and orbit UI logic
            if (_isAIControlled) return;

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

        private void LateUpdate()
        {
            // Apply 20% per level scale to prefab container so when ship levels up the visual updates
            if (visualBaseScale > 0.001f && lastPrefabScale.sqrMagnitude > 0.001f)
            {
                Transform root = GetPrefabTransform();
                if (root != null)
                    root.localScale = Vector3.Scale(lastPrefabScale, Vector3.one * (visualBaseScale * LevelScaleFactor));
            }
            UpdateEngineAndThrusterVFX();
            if (visualRoot == null || visualRoot == transform || isDead.Value || rb == null) return;
            ApplyVisualBanking(Time.deltaTime);
        }

        private static readonly float ENGINE_VFX_SPEED_THRESHOLD = 0.5f;
        private static readonly float THRUSTER_VFX_ANGULAR_THRESHOLD_RAD = 0.15f;
        private static readonly float ENGINE_VFX_EMISSION_RATE = 18f;
        private static readonly float THRUSTER_VFX_EMISSION_RATE = 15f;
        private bool lastEngineVfxMoving = false;
        private bool lastThrusterVfxTurning = false;

        private void UpdateEngineAndThrusterVFX()
        {
            if (rb == null) return;
            if (!IsOwner) return;
            if (engineVfxInstances.Count == 0 && thrusterVfxInstances.Count == 0) return;
            Vector3 vel = rb.linearVelocity;
            vel.y = 0f;
            float speed = vel.magnitude;
            float angularRad = rb.angularVelocity.magnitude;
            bool moving = speed >= ENGINE_VFX_SPEED_THRESHOLD;
            bool turning = angularRad >= THRUSTER_VFX_ANGULAR_THRESHOLD_RAD;
            if (moving == lastEngineVfxMoving && turning == lastThrusterVfxTurning)
                return;
            lastEngineVfxMoving = moving;
            lastThrusterVfxTurning = turning;

            for (int i = 0; i < engineVfxInstances.Count; i++)
            {
                GameObject go = engineVfxInstances[i];
                if (go != null) go.SetActive(moving);
            }
            for (int i = 0; i < thrusterVfxInstances.Count; i++)
            {
                GameObject go = thrusterVfxInstances[i];
                if (go != null) go.SetActive(turning);
            }
            for (int i = 0; i < engineParticleSystems.Count; i++)
            {
                ParticleSystem ps = engineParticleSystems[i];
                if (ps == null) continue;
                var emission = ps.emission;
                emission.enabled = true;
                emission.rateOverTime = moving ? ENGINE_VFX_EMISSION_RATE : 0f;
                if (moving && !ps.isPlaying) ps.Play();
            }
            for (int i = 0; i < thrusterParticleSystems.Count; i++)
            {
                ParticleSystem ps = thrusterParticleSystems[i];
                if (ps == null) continue;
                var emission = ps.emission;
                emission.enabled = true;
                emission.rateOverTime = turning ? THRUSTER_VFX_EMISSION_RATE : 0f;
                if (turning && !ps.isPlaying) ps.Play();
            }
        }

        /// <summary>
        /// Updates banking (roll) and pitch from turn rate and acceleration, then sets visualRoot.localRotation.
        /// Must run on a child of the root—never on the root itself (physics/NetworkTransform would overwrite).
        /// </summary>
        private void ApplyVisualBanking(float dt)
        {
            if (visualRoot == null || visualRoot == transform || rb == null) return;

            Vector3 fwd = rb.rotation * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) return;
            fwd.Normalize();

            if (!bankingInitialized)
            {
                previousForward = fwd;
                previousForwardSpeed = Vector3.Dot(rb.linearVelocity, fwd);
                currentBankAngle = 0f;
                currentPitchAngle = 0f;
                bankingInitialized = true;
                visualRoot.localRotation = Quaternion.identity;
                return;
            }

            dt = Mathf.Max(dt, 0.0001f);

            float maxBank = shipData != null ? shipData.maxBankAngle : defaultMaxBankAngle;
            float bankSmooth = shipData != null ? shipData.bankSmoothing : defaultBankSmoothing;
            // Roll (Z): faster turn -> more roll, up to maxBankAngle. Positive signedAngle = turning right -> bank right (positive Z).
            float signedAngle = Vector3.SignedAngle(previousForward, fwd, Vector3.up);
            float angularVelDegPerSec = Mathf.Abs(signedAngle) / dt;
            float turnRatio = Mathf.Clamp01(angularVelDegPerSec / EffectiveRotationSpeed);
            float targetBankAngle = Mathf.Sign(signedAngle) * turnRatio * maxBank;
            float bankT = 1f - Mathf.Exp(-bankSmooth * dt);
            currentBankAngle = Mathf.Lerp(currentBankAngle, targetBankAngle, bankT);

            // Pitch (X): accelerate -> nose up, brake -> nose down
            float forwardSpeed = Vector3.Dot(rb.linearVelocity, fwd);
            float forwardAccel = (forwardSpeed - previousForwardSpeed) / dt;
            float accelNorm = 0f;
            if (acceleration > 0.01f)
                accelNorm = Mathf.Clamp(forwardAccel / acceleration, -1f, 1f);
            float maxPitch = shipData != null ? shipData.maxPitchAngle : defaultMaxPitchAngle;
            float pitchSmooth = shipData != null ? shipData.pitchSmoothing : defaultPitchSmoothing;
            float targetPitchAngle = accelNorm * maxPitch;
            float pitchT = 1f - Mathf.Exp(-pitchSmooth * dt);
            currentPitchAngle = Mathf.Lerp(currentPitchAngle, targetPitchAngle, pitchT);

            visualRoot.localRotation = Quaternion.Euler(-currentPitchAngle, 0f, -currentBankAngle);

            previousForward = fwd;
            previousForwardSpeed = forwardSpeed;
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

            // Ensure we have a fire point (e.g. if ApplyShipVisual wasn't run or prefab has no FirePoint child)
            EnsureFirePoint();

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
            if (inputHandler.ShootPressed && CanFire() && firePoint != null && !IsPointerOverUI())
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

        /// <summary>True only when the pointer is over a UI element (Canvas/Graphic). Ignores 3D colliders so clicking the ship or world doesn't block shooting.</summary>
        private static bool IsPointerOverUI()
        {
            if (EventSystem.current == null) return false;
            Vector2 pointerPosition = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
            var eventData = new PointerEventData(EventSystem.current) { position = pointerPosition };
            var results = new List<RaycastResult>();
            EventSystem.current.RaycastAll(eventData, results);
            foreach (var r in results)
            {
                if (r.gameObject != null && r.module is GraphicRaycaster)
                    return true;
            }
            return false;
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

        private bool CanFire()
        {
            if (isDead.Value) return false;
            EnsureBulletLastFireTime();
            var bulletWc = bulletConfig ?? EffectiveWeaponConfig;
            if (bulletWc.cannons != null)
            {
                for (int i = 0; i < bulletWc.cannons.Count; i++)
                {
                    var c = bulletWc.cannons[i];
                    if (currentEnergy.Value >= c.energyCostPerShot &&
                        (i >= bulletLastFireTime.Length || Time.time - bulletLastFireTime[i] >= 1f / c.fireRate))
                        return true;
                }
            }
            return false;
        }

        [ServerRpc]
        private void FireServerRpc(Vector3 shipPosition, Vector3 shipForward)
        {
            if (CombatSystem.Instance == null) return;
            EnsureBulletLastFireTime();
            Vector3 forward = shipForward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            else forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 defaultFireOrigin = firePoint != null ? firePoint.position : shipPosition + forward * 2f;
            Vector3 shipVel = rb != null ? rb.linearVelocity : Vector3.zero;
            shipVel.y = 0f;

            var bulletIndicesFired = new System.Collections.Generic.List<byte>();

            // Fire bullets (Weapon only): small projectiles, low energy per shot
            var bulletWc = bulletConfig ?? EffectiveWeaponConfig;
            if (bulletWc.cannons != null)
            {
                for (int i = 0; i < bulletWc.cannons.Count; i++)
                {
                    var c = bulletWc.cannons[i];
                    if (currentEnergy.Value < c.energyCostPerShot) continue;
                    if (i >= bulletLastFireTime.Length || Time.time - bulletLastFireTime[i] < 1f / c.fireRate) continue;

                    currentEnergy.Value = Mathf.Max(0f, currentEnergy.Value - c.energyCostPerShot);
                    bulletLastFireTime[i] = Time.time;
                    bulletIndicesFired.Add((byte)i);

                    Vector3 fireOrigin = defaultFireOrigin;
                    if (bulletFirePoints != null && i < bulletFirePoints.Count && bulletFirePoints[i] != null)
                        fireOrigin = bulletFirePoints[i].position;
                    else
                    {
                        Vector3 offset = forward * c.localOffsetZ + right * c.localOffsetX;
                        fireOrigin = defaultFireOrigin + offset;
                    }

                    float baseDirAngle = c.directionAngle * Mathf.Deg2Rad;
                    Vector3 baseDir = (forward * Mathf.Cos(baseDirAngle) + right * Mathf.Sin(baseDirAngle)).normalized;
                    int numShots = 1;
                    float angleMin = c.spreadAngleMin, angleMax = c.spreadAngleMax;
                    if (c.spreadType == CannonSpreadType.FixedSpread && c.spreadProjectileCount > 1)
                        numShots = Mathf.Max(1, c.spreadProjectileCount);
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
                        float damage = c.damagePerBullet * DamageMultiplier * LevelScaleFactor;
                        float speed = c.bulletSpeed * SpeedMultiplier;
                        float scale = c.bulletScale * (0.65f + damage / 50f) * LevelScaleFactor;
                        CombatSystem.Instance.SpawnBulletServerRpc(fireOrigin, dir, speed, damage, shipTeam.Value, NetworkObjectId, scale, 0, shipVel);
                        if (rb != null)
                        {
                            float recoilImpulse = recoilStrength * scale * (0.08f + damage / 400f);
                            rb.AddForce(-dir * recoilImpulse, ForceMode.Impulse);
                        }
                    }
                }
            }

            FireClientRpc(bulletIndicesFired.Count > 0 ? bulletIndicesFired.ToArray() : System.Array.Empty<byte>());
        }

        [ClientRpc]
        private void FireClientRpc(byte[] bulletIndicesFired)
        {
            if (bulletIndicesFired != null && bulletMuzzleParticleSystems != null)
            {
                for (int j = 0; j < bulletIndicesFired.Length; j++)
                {
                    int idx = bulletIndicesFired[j];
                    if (idx >= 0 && idx < bulletMuzzleParticleSystems.Count && bulletMuzzleParticleSystems[idx] != null)
                        bulletMuzzleParticleSystems[idx].Play();
                }
            }
        }

        /// <summary>Server-only: AI ships call this to fire at a target.</summary>
        public void FireAtTarget(Vector3 direction)
        {
            if (!IsServer) return;
            if (isDead.Value) return;
            if (!CanFire()) return;
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
                CombatSystem.Instance.SpawnRocketServerRpc(firePoint.position, dir, useLarge, shipTeam.Value, NetworkObjectId);
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
                CombatSystem.Instance.SpawnMineServerRpc(pos, useLarge, shipTeam.Value, NetworkObjectId);
        }

        private NetworkVariable<bool> isDead = new NetworkVariable<bool>(false);

        [ServerRpc(RequireOwnership = false)]
        public void TakeDamageServerRpc(float damage, TeamManager.Team attackerTeam, ulong attackerShipNetworkId = 0)
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
                DieServerRpc(attackerShipNetworkId);
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
                    if (ScoreSystem.Instance != null)
                        ScoreSystem.Instance.AwardFriendlyLoad(this, amount);
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
                    bool friendly = (currentOrbitPlanet is HomePlanet home && home.AssignedTeam == shipTeam.Value)
                        || currentOrbitPlanet.TeamOwnership == shipTeam.Value;
                    RemovePeopleServerRpc(amount);
                    currentOrbitPlanet.AddPopulationServerRpc(amount, shipTeam.Value); // friendly: adds pop; enemy/neutral: decreases (capture)
                    if (!friendly && ScoreSystem.Instance != null)
                        ScoreSystem.Instance.AwardHostileUnload(this, amount);
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
                if (ScoreSystem.Instance != null)
                    ScoreSystem.Instance.AwardDeposit(this, amount);
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
        private void DieServerRpc(ulong killerShipNetworkId = 0)
        {
            if (isDead.Value) return;
            if (killerShipNetworkId != 0 && ScoreSystem.Instance != null)
            {
                var spawnManager = NetworkManager.Singleton != null ? NetworkManager.Singleton.SpawnManager : null;
                if (spawnManager != null && spawnManager.SpawnedObjects.TryGetValue(killerShipNetworkId, out NetworkObject killerObj))
                {
                    Starship killerShip = killerObj != null ? killerObj.GetComponent<Starship>() : null;
                    if (killerShip != null && killerShip != this)
                        ScoreSystem.Instance.AwardEnemyKill(killerShip);
                }
            }
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
            
            // Respawn at origin planet (if chassis has one and team owns it), otherwise at home planet.
            RespawnAtOriginOrHomePlanet();
        }

        /// <summary>Server: respawn at the ship's chassis origin planet if team owns it, otherwise at home planet.</summary>
        private void RespawnAtOriginOrHomePlanet()
        {
            if (shipTeam.Value == TeamManager.Team.None || rb == null) return;

            Planet respawnPlanet = null;
            int chassisIndex = currentChassisIndex.Value;
            if (chassisIndex >= 0 && CardShopSystem.Instance != null)
            {
                var chassis = CardShopSystem.Instance.GetChassisByIndex(chassisIndex);
                if (chassis != null && chassis.originPlanetId > 0)
                {
                    foreach (var p in Object.FindObjectsByType<Planet>(FindObjectsSortMode.None))
                    {
                        if (p.PlanetId == chassis.originPlanetId && p.TeamOwnership == shipTeam.Value)
                        {
                            respawnPlanet = p;
                            break;
                        }
                    }
                }
            }

            if (respawnPlanet == null)
            {
                foreach (var hp in Object.FindObjectsByType<HomePlanet>(FindObjectsSortMode.None))
                {
                    if (hp.AssignedTeam == shipTeam.Value) { respawnPlanet = hp; break; }
                }
            }

            if (respawnPlanet == null) return;

            PlaceShipInOrbitAround(respawnPlanet);
        }

        /// <summary>Server: place ship in orbit around the given planet (used for respawn).</summary>
        private void PlaceShipInOrbitAround(Planet planet)
        {
            if (planet == null || rb == null) return;
            float orbitRadius = planet.PlanetSize * 0.6f;
            Vector3 planetPos = planet.transform.position;
            Vector3 orbitPos = planetPos + new Vector3(orbitRadius, 0f, 0f);
            orbitPos.y = FIXED_Y_POSITION;
            rb.position = orbitPos;

            float innerWorld = planet.PlanetSize * 0.5f;
            float outerWorld = planet.PlanetSize * 0.85f;
            float targetSpeed = GetOrbitTargetSpeed(planet, orbitRadius, innerWorld, outerWorld);

            rb.linearVelocity = new Vector3(0f, 0f, -targetSpeed);
            currentVelocity = rb.linearVelocity;
        }

        /// <summary>Server: respawn ship at home planet (legacy fallback; prefer RespawnAtOriginOrHomePlanet).</summary>
        private void RespawnAtHomePlanet()
        {
            if (shipTeam.Value == TeamManager.Team.None || rb == null) return;
            HomePlanet home = null;
            foreach (var hp in Object.FindObjectsOfType<HomePlanet>())
            {
                if (hp.AssignedTeam == shipTeam.Value) { home = hp; break; }
            }
            if (home != null)
                PlaceShipInOrbitAround(home);
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
                EnsureBulletLastFireTime();
                for (int i = 0; bulletLastFireTime != null && i < bulletLastFireTime.Length; i++) bulletLastFireTime[i] = -999f;

                maxHealth = data.baseMaxHealth;
                healthRegenRate = data.baseHealthRegenRate;
                rotationSpeed = data.baseRotationSpeed;
                gemCapacity = data.baseGemCapacity;
                peopleCapacity = data.basePeopleCapacity;
                energyCapacity = data.baseEnergyCapacity;
                energyRegenRate = data.baseEnergyRegenRate;

                if (data.shipPrefab != null)
                {
                    // When chassis already applied (e.g. starter ship at index 0), don't overwrite with ShipData's prefab
                    if (currentChassisIndex.Value < 0)
                    {
                        ApplyShipVisual(data.shipPrefab, data);
                        var composer = GetComponent<ShipVisualComposer>();
                        if (composer != null)
                            composer.RebuildVisuals();
                    }
                }
                else
                    Debug.LogWarning($"Starship: ShipData '{data.shipName}' has no shipPrefab. Assign a ship prefab (e.g. Level 1) so the ship visual loads.");
                ApplyHullIdentityColor();
            }
        }

        /// <summary>Replaces this ship's visual with the given prefab while keeping current ShipData stats. Used when purchasing a new chassis that only defines a model (e.g. AstroEagle variants).</summary>
        public void ApplyShipVisualFromPrefab(GameObject shipPrefab)
        {
            if (shipPrefab == null) return;
            ApplyShipVisual(shipPrefab, shipData);
            var composer = GetComponent<ShipVisualComposer>();
            if (composer != null) composer.RebuildVisuals();
            ApplyHullIdentityColor();
        }

        /// <summary>Replaces this ship's visual with the chosen ship prefab: copies root hull mesh and reparents children (keeps FirePoint for shooting). Uses Prefab container (StarshipMain -> BankPivot -> Prefab) so upgrades swap cleanly.</summary>
        private void ApplyShipVisual(GameObject shipPrefab, ShipData data)
        {
            if (shipPrefab == null) return;
            Transform root = GetPrefabTransform();

            if (lastVisualApplyFrame == Time.frameCount && lastVisualApplyPrefab == shipPrefab)
            {
                return;
            }
            lastVisualApplyFrame = Time.frameCount;
            lastVisualApplyPrefab = shipPrefab;

            GameObject instance = Instantiate(shipPrefab);
            Transform prefabRoot = instance.transform;
            Vector3 prefabScale = prefabRoot.localScale;

            // Remove our current visual children, then adopt prefab root's children
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform oldChild = root.GetChild(i);
                if (oldChild == null) continue;

                // Disable immediately so repeated applies in the same frame don't stack rendering/physics cost.
                var oldRenderers = oldChild.GetComponentsInChildren<Renderer>(true);
                foreach (var r in oldRenderers) if (r != null) r.enabled = false;
                var oldColliders = oldChild.GetComponentsInChildren<Collider>(true);
                foreach (var c in oldColliders) if (c != null) c.enabled = false;

                Object.Destroy(oldChild.gameObject);
            }

            // Copy hull from prefab root to a Hull child (scale 1; parent container scale handles sizing)
            MeshFilter prefabMf = prefabRoot.GetComponent<MeshFilter>();
            MeshRenderer prefabMr = prefabRoot.GetComponent<MeshRenderer>();
            if (prefabMf != null && prefabMr != null && prefabMf.sharedMesh != null)
            {
                var hullGo = new GameObject("Hull");
                Transform hullParent = hullGo.transform;
                hullParent.SetParent(root, false);
                hullParent.localPosition = Vector3.zero;
                hullParent.localRotation = Quaternion.identity;
                hullParent.localScale = Vector3.one;

                var ourMf = hullParent.gameObject.AddComponent<MeshFilter>();
                ourMf.sharedMesh = prefabMf.sharedMesh;
                var ourMr = hullParent.gameObject.AddComponent<MeshRenderer>();
                ourMr.sharedMaterials = prefabMr.sharedMaterials;
                ourMr.enabled = prefabMr.enabled;
            }

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

            // Scale parent container once (includes prefab root scale + game scale + 20% per level)
            float baseScale = (data != null && data.visualScale > 0f ? data.visualScale : 1f) * Mathf.Max(0.005f, shipVisualScaleMultiplier);
            visualBaseScale = baseScale;
            lastPrefabScale = prefabScale;
            float levelScale = LevelScaleFactor;
            float gameScale = baseScale * levelScale;
            root.localScale = Vector3.Scale(prefabScale, Vector3.one * gameScale);

            // Rebind FirePoint from the prefab child we just moved (don't use Find - old children may still be present until Destroy runs)
            if (newFirePoint != null)
                firePoint = newFirePoint;
            else
            {
                // Always create a fresh fallback when upgraded prefab doesn't provide FirePoint.
                // Avoid binding to an old child queued for Destroy (can become null next frame).
                GameObject fp = new GameObject("FirePoint");
                fp.transform.SetParent(root, false);
                fp.transform.localPosition = new Vector3(0f, 0f, 0.55f);
                fp.transform.localRotation = Quaternion.identity;
                fp.transform.localScale = Vector3.one;
                firePoint = fp.transform;
            }

            // Imported example prefabs may include many colliders/rigidbodies/scripts intended for editor setup.
            // Keep only visual components under the ship visual root to avoid heavy runtime overhead.
            StripNonVisualComponents(root, firePoint);

            // Parse chassis component names (e.g. AstroEagle_Weapon, AstroEagle_Engine_2) and apply stats + weapon/muzzle setup.
            ApplyChassisComponentStats(root, data);
        }

        private const string CHASSIS_FAMILY_PREFIX = "AstroEagle";
        private static readonly float PER_ENGINE_MOVEMENT = 1.2f;
        private static readonly float PER_TURN_COMPONENT_ROTATION = 12f;
        private static readonly float PER_WING_GEM_CAPACITY = 18f;
        private static readonly float PER_COCKPIT_PEOPLE = 2f;
        private static readonly float PER_PART_PEOPLE = 1f;
        /// <summary>Cannon energy: extra capacity and regen per Cockpit component.</summary>
        private static readonly float PER_COCKPIT_ENERGY_CAPACITY = 15f;
        private static readonly float PER_COCKPIT_ENERGY_REGEN = 1.5f;
        /// <summary>Bullet energy: damage and energy cost scale per Weapon (proportionate).</summary>
        private static readonly float PER_WEAPON_DAMAGE_SCALE = 0.15f;
        private static readonly float PER_WEAPON_ENERGY_COST_SCALE = 0.25f;
        private static readonly float PER_WEAPON_BULLET_SPEED_SCALE = 0.08f;
        private static readonly float MUZZLE_BASE_SIZE = 0.18f;
        private static readonly float MUZZLE_SIZE_PER_ENERGY = 0.04f;

        private void ApplyChassisComponentStats(Transform root, ShipData data)
        {
            var stats = ChassisComponentStats.FromTransform(root, CHASSIS_FAMILY_PREFIX);

            // Apply stat modifiers from components (on top of base from ShipData). Scale = bonus multiplier per component.
            if (data != null)
            {
                movementSpeed = data.baseMovementSpeed + stats.engineScaleTotal * PER_ENGINE_MOVEMENT;
                float turnScaleTotal = stats.thrusterScaleTotal + stats.tailScaleTotal + stats.finScaleTotal;
                rotationSpeed = data.baseRotationSpeed + turnScaleTotal * PER_TURN_COMPONENT_ROTATION;
                gemCapacity = data.baseGemCapacity + stats.wingScaleTotal * PER_WING_GEM_CAPACITY;
                peopleCapacity = data.basePeopleCapacity + stats.cockpitScaleTotal * PER_COCKPIT_PEOPLE + stats.partScaleTotal * PER_PART_PEOPLE;
            }

            // Clear previous bullet state (from previous prefab). Cannons removed; only Weapon bullets.
            bulletFirePoints.Clear();
            foreach (var ps in bulletMuzzleParticleSystems)
            {
                if (ps != null && ps.gameObject != null)
                    Object.Destroy(ps.gameObject);
            }
            bulletMuzzleParticleSystems.Clear();
            foreach (var go in engineVfxInstances)
            {
                if (go != null) Object.Destroy(go);
            }
            engineVfxInstances.Clear();
            engineParticleSystems.Clear();
            foreach (var go in thrusterVfxInstances)
            {
                if (go != null) Object.Destroy(go);
            }
            thrusterVfxInstances.Clear();
            thrusterParticleSystems.Clear();
            lastEngineVfxMoving = false;
            lastThrusterVfxTurning = false;

            bulletConfig = null;

            // Energy (capacity/regen) from Cockpit scale — cockpits no longer fire, only provide stats
            if (data != null && stats.cockpitCannonCount > 0)
            {
                energyCapacity = data.baseEnergyCapacity + stats.cockpitCannonScaleTotal * PER_COCKPIT_ENERGY_CAPACITY;
                energyRegenRate = data.baseEnergyRegenRate + stats.cockpitCannonScaleTotal * PER_COCKPIT_ENERGY_REGEN;
            }

            // Bullets (Weapon only): small projectiles, low energy; fire from weapon positions
            int weaponCount = stats.weaponTransforms != null ? stats.weaponTransforms.Count : 0;
            float weaponScaleTotal = 0f;
            for (int w = 0; w < stats.weaponScales.Count; w++) weaponScaleTotal += stats.weaponScales[w];
            if (weaponScaleTotal <= 0f && weaponCount > 0) weaponScaleTotal = weaponCount;
            float bulletEnergyScale = 1f + weaponScaleTotal * PER_WEAPON_ENERGY_COST_SCALE;
            float bulletDamageScale = 1f + weaponScaleTotal * PER_WEAPON_DAMAGE_SCALE;
            float bulletSpeedScale = 1f + weaponScaleTotal * PER_WEAPON_BULLET_SPEED_SCALE;

            if (weaponCount > 0 && data != null)
            {
                var baseBullet = (data.weaponConfig != null && data.weaponConfig.cannons != null && data.weaponConfig.cannons.Count > 0)
                    ? data.weaponConfig.cannons[0]
                    : GetDefaultWeaponConfig().cannons[0];
                var bc = ScriptableObject.CreateInstance<WeaponConfig>();
                bc.displayName = "ChassisBullets";
                bc.cannons = new System.Collections.Generic.List<CannonConfig>();
                for (int i = 0; i < weaponCount; i++)
                {
                    var c = baseBullet.Clone();
                    c.energyCostPerShot *= bulletEnergyScale;
                    c.damagePerBullet *= bulletDamageScale;
                    c.bulletSpeed *= bulletSpeedScale;
                    bc.cannons.Add(c);
                    Transform pt = stats.weaponTransforms[i];
                    if (pt == null) pt = firePoint;
                    bulletFirePoints.Add(pt);
                    float ws = (stats.weaponScales != null && i < stats.weaponScales.Count) ? stats.weaponScales[i] : 1f;
                    float muzzleScale = (MUZZLE_BASE_SIZE + c.energyCostPerShot * MUZZLE_SIZE_PER_ENERGY) * Mathf.Max(0.5f, ws);
                    ParticleSystem muzzle = CreateMuzzleParticleSystem(pt, muzzleScale);
                    if (muzzle != null) bulletMuzzleParticleSystems.Add(muzzle);
                }
                bulletConfig = bc;
            }

            EnsureBulletLastFireTime();

            // Engine VFX (movement) and Thruster VFX (rotation)
            if (engineVfxPrefab != null && stats.engineTransforms != null)
            {
                foreach (Transform t in stats.engineTransforms)
                {
                    if (t == null) continue;
                    GameObject go = Instantiate(engineVfxPrefab, t);
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localRotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one;
                    engineVfxInstances.Add(go);
                    var psList = go.GetComponentsInChildren<ParticleSystem>(true);
                    foreach (var ps in psList)
                    {
                        if (ps != null) engineParticleSystems.Add(ps);
                    }
                }
            }
            if (thrusterVfxPrefab != null && stats.thrusterTransforms != null)
            {
                foreach (Transform t in stats.thrusterTransforms)
                {
                    if (t == null) continue;
                    GameObject go = Instantiate(thrusterVfxPrefab, t);
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localRotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one;
                    thrusterVfxInstances.Add(go);
                    var psList = go.GetComponentsInChildren<ParticleSystem>(true);
                    foreach (var ps in psList)
                    {
                        if (ps != null) thrusterParticleSystems.Add(ps);
                    }
                }
            }
        }

        private static ParticleSystem CreateMuzzleParticleSystem(Transform parent, float visualScale = 0.18f)
        {
            if (parent == null) return null;
            GameObject go = new GameObject("MuzzleFlash");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.duration = 0.1f;
            main.loop = false;
            main.startLifetime = 0.08f;
            main.startSpeed = 2.5f;
            main.startSize = Mathf.Max(0.12f, visualScale);
            main.startColor = new Color(1f, 0.85f, 0.6f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            int burstCount = Mathf.Clamp(Mathf.RoundToInt(4 * visualScale / 0.18f), 3, 12);
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, burstCount) });
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = Mathf.Max(0.02f, 0.02f * visualScale / 0.18f);

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                Material urpMat = GetMuzzleFlashURPMaterial();
                if (urpMat != null)
                    renderer.sharedMaterial = urpMat;
            }
            return ps;
        }

        private static Material muzzleFlashURPMaterial;

        private static Material GetMuzzleFlashURPMaterial()
        {
            if (muzzleFlashURPMaterial != null) return muzzleFlashURPMaterial;
            Material fromResources = Resources.Load<Material>("Materials/MuzzleFlash");
            if (fromResources != null)
            {
                muzzleFlashURPMaterial = fromResources;
                return muzzleFlashURPMaterial;
            }
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                ?? Shader.Find("Universal Render Pipeline/Particles/Lit")
                ?? Shader.Find("Particles/Standard Unlit")
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null) return null;
            muzzleFlashURPMaterial = new Material(shader);
            muzzleFlashURPMaterial.name = "MuzzleFlash_URP";
            muzzleFlashURPMaterial.SetColor("_BaseColor", Color.white);
            if (muzzleFlashURPMaterial.HasProperty("_Color"))
                muzzleFlashURPMaterial.SetColor("_Color", Color.white);
            muzzleFlashURPMaterial.renderQueue = 3000;
            return muzzleFlashURPMaterial;
        }

        /// <summary>Removes expensive non-visual components from adopted visual hierarchy.</summary>
        internal static void StripNonVisualComponents(Transform visualRootTransform, Transform keepFirePoint)
        {
            if (visualRootTransform == null) return;

            Collider[] childColliders = visualRootTransform.GetComponentsInChildren<Collider>(true);
            foreach (var col in childColliders)
            {
                if (col == null) continue;
                // Keep the main ship collider on the ship root.
                if (col.transform == visualRootTransform) continue;
                if (keepFirePoint != null && (col.transform == keepFirePoint || col.transform.IsChildOf(keepFirePoint))) continue;
                Object.Destroy(col);
            }

            Rigidbody[] childRigidbodies = visualRootTransform.GetComponentsInChildren<Rigidbody>(true);
            foreach (var rb in childRigidbodies)
            {
                if (rb == null) continue;
                if (rb.transform == visualRootTransform) continue;
                Object.Destroy(rb);
            }

            // Remove any extra behaviours attached inside imported visual prefabs.
            MonoBehaviour[] childBehaviours = visualRootTransform.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var behaviour in childBehaviours)
            {
                if (behaviour == null) continue;
                if (behaviour.transform == visualRootTransform) continue;
                Object.Destroy(behaviour);
            }

            // Example prefabs often have many tiny parts; shadow casting on all of them is very expensive.
            Renderer[] childRenderers = visualRootTransform.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in childRenderers)
            {
                if (renderer == null) continue;
                if (renderer.transform == visualRootTransform) continue;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
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

        #region Card stat helpers

        private static float CardLevelScale(int level)
        {
            return level <= 1 ? 1f : 1f + (level - 1) * 0.35f; // L1=1, L2=1.35, L3=1.7, L4=2.05
        }

        private static float CardRarityScale(int rarity)
        {
            if (rarity <= 1) return 1f;
            if (rarity == 2) return 1.25f;
            if (rarity == 3) return 1.5f;
            return 2f; // Epic
        }

        private float GetCardMovementSpeedAdd()
        {
            if (equippedCards == null) return 0f;
            float sum = 0f;
            foreach (var card in equippedCards)
            {
                if (card == null) continue;
                float scale = CardLevelScale(Mathf.Max(1, card.cardLevel)) * CardRarityScale(Mathf.Max(1, card.rarity));
                sum += card.movementSpeedAdd * scale;
            }
            return sum;
        }

        private float GetCardRotationSpeedAdd()
        {
            if (equippedCards == null) return 0f;
            float sum = 0f;
            foreach (var card in equippedCards)
            {
                if (card == null) continue;
                float scale = CardLevelScale(Mathf.Max(1, card.cardLevel)) * CardRarityScale(Mathf.Max(1, card.rarity));
                sum += card.rotationSpeedAdd * scale;
            }
            return sum;
        }

        private float GetCardMaxHealthAdd()
        {
            if (equippedCards == null) return 0f;
            float sum = 0f;
            foreach (var card in equippedCards)
            {
                if (card == null) continue;
                float scale = CardLevelScale(Mathf.Max(1, card.cardLevel)) * CardRarityScale(Mathf.Max(1, card.rarity));
                sum += card.maxHealthAdd * scale;
            }
            return sum;
        }

        private float GetCardHealthRegenAdd()
        {
            if (equippedCards == null) return 0f;
            float sum = 0f;
            foreach (var card in equippedCards)
            {
                if (card == null) continue;
                float scale = CardLevelScale(Mathf.Max(1, card.cardLevel)) * CardRarityScale(Mathf.Max(1, card.rarity));
                sum += card.healthRegenAdd * scale;
            }
            return sum;
        }

        private float GetCardEnergyCapacityAdd()
        {
            if (equippedCards == null) return 0f;
            float sum = 0f;
            foreach (var card in equippedCards)
            {
                if (card == null) continue;
                float scale = CardLevelScale(Mathf.Max(1, card.cardLevel)) * CardRarityScale(Mathf.Max(1, card.rarity));
                sum += card.energyCapacityAdd * scale;
            }
            return sum;
        }

        private float GetCardEnergyRegenAdd()
        {
            if (equippedCards == null) return 0f;
            float sum = 0f;
            foreach (var card in equippedCards)
            {
                if (card == null) continue;
                float scale = CardLevelScale(Mathf.Max(1, card.cardLevel)) * CardRarityScale(Mathf.Max(1, card.rarity));
                sum += card.energyRegenAdd * scale;
            }
            return sum;
        }

        private float GetCardGemCapacityAdd()
        {
            if (equippedCards == null) return 0f;
            float sum = 0f;
            foreach (var card in equippedCards)
            {
                if (card == null) continue;
                float scale = CardLevelScale(Mathf.Max(1, card.cardLevel)) * CardRarityScale(Mathf.Max(1, card.rarity));
                sum += card.gemCapacityAdd * scale;
            }
            return sum;
        }

        private float GetCardDamageMultiplier()
        {
            if (equippedCards == null || equippedCards.Count == 0) return 1f;
            float mul = 1f;
            foreach (var card in equippedCards)
            {
                if (card == null) continue;
                if (card.damageMultiplier > 0f)
                {
                    float scale = CardLevelScale(Mathf.Max(1, card.cardLevel)) * CardRarityScale(Mathf.Max(1, card.rarity));
                    float bonus = (card.damageMultiplier - 1f) * scale + 1f;
                    mul *= bonus;
                }
            }
            return mul;
        }

        private float GetCardBulletSpeedMultiplier()
        {
            if (equippedCards == null || equippedCards.Count == 0) return 1f;
            float mul = 1f;
            foreach (var card in equippedCards)
            {
                if (card == null) continue;
                if (card.bulletSpeedMultiplier > 0f)
                {
                    float scale = CardLevelScale(Mathf.Max(1, card.cardLevel)) * CardRarityScale(Mathf.Max(1, card.rarity));
                    float bonus = (card.bulletSpeedMultiplier - 1f) * scale + 1f;
                    mul *= bonus;
                }
            }
            return mul;
        }

        private float GetCardMassContribution()
        {
            if (equippedCards == null) return 0f;
            float sum = 0f;
            foreach (var card in equippedCards)
            {
                if (card == null) continue;
                float scale = CardLevelScale(Mathf.Max(1, card.cardLevel)) * CardRarityScale(Mathf.Max(1, card.rarity));
                sum += card.massContribution * scale;
            }
            return sum;
        }

        #endregion

        /// <summary>
        /// Server-only: add a card to this ship's loadout. Uses simple slots: 1 slot per ship level, 1 card per slot.
        /// Only adds if there is an empty slot (first available).
        /// </summary>
        public void AddCardFromServer(CardData card)
        {
            if (!IsServer) return;
            if (card == null) return;
            if (equippedCards == null) equippedCards = new List<CardData>();
            int maxSlots = SlotCount;
            if (equippedCards.Count >= maxSlots) return;
            if (!equippedCards.Contains(card))
                equippedCards.Add(card);
        }

        /// <summary>Server-only: set the current chassis index (from ShipUnlockTable) so clients can show the correct card grid layout.</summary>
        public void SetCurrentChassisIndex(int index)
        {
            if (!IsServer) return;
            currentChassisIndex.Value = index;
        }
    }
}
