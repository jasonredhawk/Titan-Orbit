using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.Serialization;
using Unity.Netcode;
using UnityEngine.InputSystem;
using TitanOrbit.Core;
using TitanOrbit.Input;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Systems;
using TitanOrbit.Audio;
using SciFiArsenal;

namespace TitanOrbit.Entities
{
    /// <summary>Serializable card ID for syncing equipped loadout to clients. Uses FixedString64Bytes for NetworkList compatibility (non-nullable value type).</summary>
    public struct EquippedCardId : INetworkSerializable, System.IEquatable<EquippedCardId>
    {
        public FixedString64Bytes cardId;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref cardId);
        }

        public bool Equals(EquippedCardId other) => cardId.Equals(other.cardId);
    }

    /// <summary>
    /// Base starship controller for player-controlled ships
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [DefaultExecutionOrder(60000)] // Run last so banking is not overwritten by transform sync or other LateUpdates
    public class Starship : NetworkBehaviour
    {
        /// <summary>Global registry of all active starships to avoid repeated FindObjectsByType scans.</summary>
        public static readonly System.Collections.Generic.List<Starship> AllStarships = new System.Collections.Generic.List<Starship>();

        // Cached references to avoid repeated global searches from Update.
        private static TitanOrbit.UI.HomePlanetOrbitUI s_cachedOrbitUI;
        private static TitanOrbit.Camera.CameraController s_cachedCameraController;
        private bool _orbitUiVisible;
        [Header("Ship Settings")]
        [SerializeField] private ShipData shipData;
        /// <summary>Current ship data (model, weapon config, stats). Used so AI can match player ship.</summary>
        public ShipData CurrentShipData => shipData;
        [SerializeField] private int shipLevel = 1;
        [SerializeField] private ShipFocusType focusType = ShipFocusType.Fighter;

        [Header("Movement")]
        [Tooltip("Engine thrust (force) when no chassis applied. Chassis engines override.")]
        [SerializeField] private float engineThrust = 12f;
        /// <summary>When set from Ship Family preview stats, stores authored turn units (small numbers). Otherwise legacy/world °/s (e.g. ShipData or scale fallback). See <see cref="rotationSpeedFromShipFamilyDefinition"/>.</summary>
        [SerializeField] private float rotationSpeed = 180f;
        /// <summary>True: <see cref="rotationSpeed"/> is family definition units — multiply by <see cref="ShipTurnDefinitionToDegreesPerSecond"/> only in rotation/banking. False: already °/s (ShipData, chassis scale fallback).</summary>
        private bool rotationSpeedFromShipFamilyDefinition;
        /// <summary>Converts family-authored turn stats to °/s. Applied only in rotation and related visuals — not in power scores or persisted data.</summary>
        private const float ShipTurnDefinitionToDegreesPerSecond = 10f;
        [SerializeField] private float acceleration = 32f;
        [Tooltip("When space brakes are on, speed is reduced by this amount per second (higher = more friction, faster stop).")]
        [SerializeField] private float brakeDeceleration = 7f;
        [Tooltip("When over max speed (e.g. from recoil), speed is reduced back toward max by this amount per second.")]
        [SerializeField] private float recoilDecayPerSecond = 6f;
        [Header("Orbit")]
        [SerializeField] private float orbitSpeed = 0.8f; // Baseline linear speed while orbiting; modified by planet size and radius
        [SerializeField] private float orbitRadiusPullStrength = 2.5f; // Push in/out when outside zone band; stronger = quicker stabilization
        [Tooltip("How quickly the ship's existing velocity is steered toward the ideal orbit velocity. Higher = snappier capture, lower = more drift-through.")]
        [SerializeField] private float orbitCaptureResponsiveness = 3.5f;
        [Tooltip("After gem-moon dock or any off-plane height, Y eases toward the play plane (0) instead of snapping. XZ unchanged so you can drift out of the orbit band naturally. Higher = faster.")]
        [SerializeField] private float orbitExitYRecoverySpeed = 10f;
        [Tooltip("Radial snap toward moon at outer dock ring (blend 0). Kept low so motion ramps with trigger depth.")]
        [SerializeField] private float gemMoonSnapPositionSpeedOuter = 2.5f;
        [Tooltip("Radial snap toward moon when fully blended toward surface (blend 1).")]
        [SerializeField] private float gemMoonSnapPositionSpeedInner = 8f;
        [Tooltip("While gem-moon docked, max horizontal velocity change per second when matching moon orbit speed.")]
        [SerializeField] private float gemMoonSnapVelocityAlign = 18f;

        [Header("Gem Moon Landing")]
        [Tooltip("Scale at the outer dock ring (blend 0). Usually 1 = full size.")]
        [SerializeField, Range(0.05f, 1.5f)]
        private float gemMoonDockScaleAtOrbitEdge = 1f;
        [Tooltip("Scale when fully blended to the moon surface (blend 1). Set to 1 for no shrink. Overall ship size also uses Ship Visual Scale Multiplier on this component.")]
        [SerializeField, Range(0.05f, 1.5f), FormerlySerializedAs("gemMoonLandingVisualScale")]
        private float gemMoonDockScaleAtSurface = 0.24f;
        [Tooltip("If docked, ships only shrink/land when within moon trigger distance = dockSnapRadiusWorld × this multiplier.")]
        [SerializeField] private float gemMoonLandingRangeMultiplier = 1.0f;
        [Tooltip("Seconds for ease-in-out dock (in band) and undock (scale + orbit handoff). Shared timeline for scale, position, and rotation.")]
        [SerializeField] private float gemMoonTransitionDurationSeconds = 1f;
        [Tooltip("Speed to recover scale when docked outside the landing band or when not using timed undock.")]
        [SerializeField] private float gemMoonLandingScaleLerpSpeed = 0.35f;
        [Tooltip("How quickly the networked starship root eases into moon center while docked.")]
        [SerializeField] private float gemMoonCenterSnapSpeed = 6f;
        [Tooltip("Seconds to blend reparented prefab onto moon surface pose.")]
        [SerializeField] private float gemMoonVisualDockBlendSeconds = 1f;

        [Tooltip("When undocking, temporarily ignore the moon trigger so the ship can actually leave.")]
        [SerializeField] private float gemMoonDockIgnoreSeconds = 0.75f;
        [Tooltip("XZ speed away from the moon center at the start of grace (ramps down as orbit tangent takes over).")]
        [SerializeField] private float gemMoonUndockOutwardSpeed = 2.75f;
        [Tooltip("Orbit velocity capture strength at start of post-undock grace (ramps to 1). Lower = softer handoff.")]
        [SerializeField, Range(0.05f, 1f)]
        private float gemMoonUndockOrbitCaptureEase = 0.22f;
        private float gemMoonUndockOrbitGraceUntilTime = -1f;
        private Vector3 gemMoonUndockCachedMoonPos;

        private float gemMoonVisualScaleMultiplier = 1f;
        private bool wasGemMoonDocked;
        private ulong gemMoonLandingPlanetIdCache;
        private Vector3 gemMoonLandingOffset = Vector3.zero;
        private Quaternion gemMoonDockVisualStartRotation = Quaternion.identity;
        private float gemMoonDockApproachElapsed;
        private float gemMoonDockApproachStartScaleMultiplier = 1f;
        private Vector3 gemMoonDockApproachStartWorldPos = Vector3.zero;
        private float gemMoonUndockBlendElapsed;
        private float gemMoonUndockStartScale = 1f;
        private bool gemMoonUndockBlendActive;
        private Transform prefabTransformCache = null;
        /// <summary>Prefab container localScale while parented to BankPivot (updated when chassis visual is applied).</summary>
        private Vector3 gemMoonPrefabBaselineLocalScale = Vector3.one;
        private Transform gemMoonReparentTarget = null;
        private Transform gemMoonVisualParentBeforeAttach = null;
        private bool gemMoonVisualAttached = false;
        private bool gemMoonVisualDockBlendActive = false;
        private float gemMoonVisualDockBlendElapsed = 0f;
        private Vector3 gemMoonVisualDockStartLocalPos = Vector3.zero;
        private Vector3 gemMoonVisualDockTargetLocalPos = Vector3.zero;
        private Quaternion gemMoonVisualDockStartLocalRot = Quaternion.identity;
        private Quaternion gemMoonVisualDockTargetLocalRot = Quaternion.identity;
        private bool gemMoonVisualUndockBlendActive = false;
        private float gemMoonVisualUndockBlendElapsed = 0f;
        private Vector3 gemMoonVisualUndockStartLocalPos = Vector3.zero;
        private Quaternion gemMoonVisualUndockStartLocalRot = Quaternion.identity;
        private Vector3 gemMoonVisualUndockStartLocalScale = Vector3.one;
        private Collider rootCollider;
        private bool rootColliderEnabledBeforeDock = true;
        private bool rootColliderDockOverrideActive = false;

        [Header("Combat")]
        [SerializeField] private Transform firePoint;
        [Tooltip("Recoil impulse per shot scales with bullet scale and damage. Bigger bullets push the ship back more; stationary ships can reverse.")]
        [SerializeField] private float recoilStrength = 1.2f;

        /// <summary>Bullet fire points (Weapon components only; Cockpit cannons removed).</summary>
        private List<Transform> bulletFirePoints = new List<Transform>();
        /// <summary>CombatSystem bullet prefab bank index for this ship (from ShipFamilyDefinition). -1 = use CombatSystem default.</summary>
        private int bulletPrefabBankIndex = -1;
        /// <summary>Runtime bullet index (synced). B key cycles this. When >= 0 use instead of bulletPrefabBankIndex when firing.</summary>
        private NetworkVariable<int> runtimeBulletPrefabIndex = new NetworkVariable<int>(-1);
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

        [Header("Component Attribute Scaling")]
        [Tooltip("Per-ship fallback when GameManager.AttributeScaleExaggeration is 0. 0.2 = 20% per attribute unit. GameManager overrides when set.")]
        [SerializeField] private float attributeScaleExaggeration = 0.2f;
        [Tooltip("How much component mesh scale reflects stat upgrades. 0.5 = 10% stat increase → 5% bigger component; 1 = 1:1. Set higher so upgrades are clearly visible.")]
        [SerializeField] [Range(0.2f, 1.5f)] private float componentScaleVisibility = 0.6f;
        [Tooltip("Extra influence of gem capacity upgrades on wing size. 1.67 with visibility 0.6 means +100% gem capacity can produce about 2x wing scale.")]
        [SerializeField] [Range(1f, 3f)] private float wingGemScaleBoost = 1.67f;
        [Tooltip("Extra multiplier for bullet size so projectiles scale more visibly with Fire Power / Bullet Speed / cards. 1 = same as weapon component scale; 1.5 = bullets grow 50% more per upgrade.")]
        [SerializeField] [Range(0.5f, 3f)] private float bulletScaleExaggeration = 1.5f;

        private List<Transform> cockpitScaleTransforms = new List<Transform>();
        private List<Vector3> cockpitBaseScales = new List<Vector3>();
        private List<Vector3> cockpitBasePositions = new List<Vector3>();
        private List<Transform> wingScaleTransforms = new List<Transform>();
        private List<Vector3> wingBaseScales = new List<Vector3>();
        private List<Vector3> wingBasePositions = new List<Vector3>();
        private List<Transform> weaponScaleTransforms = new List<Transform>();
        private List<Vector3> weaponBaseScales = new List<Vector3>();
        private List<Vector3> weaponBasePositions = new List<Vector3>();
        private List<Transform> engineScaleTransforms = new List<Transform>();
        private List<Vector3> engineBaseScales = new List<Vector3>();
        private List<Vector3> engineBasePositions = new List<Vector3>();
        private List<Transform> thrusterScaleTransforms = new List<Transform>();
        private List<Vector3> thrusterBaseScales = new List<Vector3>();
        private List<Vector3> thrusterBasePositions = new List<Vector3>();
        private List<Transform> partScaleTransforms = new List<Transform>();
        private List<Vector3> partBaseScales = new List<Vector3>();
        private List<Vector3> partBasePositions = new List<Vector3>();
        private List<float> muzzleBaseSizes = new List<float>();
        private List<float> muzzleBaseSpeeds = new List<float>();

        [Header("Feedback")]
        [Tooltip("World-space floating text prefab (with SimpleFloatingText) used to show bullet/weapon changes above the ship.")]
        [SerializeField] private GameObject bulletNameTextPrefab;

        /// <summary>Cached card stat sums, refreshed once per frame to avoid iterating equippedCards 16+ times in LateUpdate.</summary>
        private int _cardStatsCacheFrame = -1;
        private float _cachedCardMovementSpeedAdd;
        private float _cachedCardRotationSpeedAdd;
        private float _cachedCardMaxHealthAdd;
        private float _cachedCardHealthRegenAdd;
        private float _cachedCardEnergyCapacityAdd;
        private float _cachedCardEnergyRegenAdd;
        private float _cachedCardGemCapacityAdd;
        private float _cachedCardPeopleCapacityAdd;
        private float _cachedCardDamageMultiplier = 1f;
        private float _cachedCardBulletSpeedMultiplier = 1f;

        /// <summary>Mass from chassis components (Engine, Thruster, Wing, Cockpit, Part, etc.). Used when chassis applied.</summary>
        private float componentMass = 0f;
        /// <summary>Thrust force from engine components. Applied via AddForce; acceleration = thrust/mass.</summary>
        private float componentEngineThrust = 0f;
        /// <summary>Max speed from engine components. More engines = higher top speed cap.</summary>
        private float componentEngineMaxSpeed = 0f;

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
        [Tooltip("Additional multiplier for how much this ship's FirePower boosts ramming damage.")]
        [SerializeField] private float rammingFirePowerDamageMultiplier = 1f;
        [Tooltip("Maximum damage applied on the first collision contact (before asteroid/self split).")]
        [SerializeField] private float maxRammingDamageOnFirstHit = 25f;
        [Tooltip("Mining ships (ice-breaker hull) deal this multiplier to asteroids; take less self-damage via HullRammingSelfDamageMultiplier.")]
        [SerializeField] private float minerRammingToAsteroidMultiplier = 1.4f;
        [Tooltip("Mining ships take this fraction of ramming self-damage (stronger hull). Fighter = 1, Miner = 0.35.")]
        [SerializeField] private float minerRammingSelfDamageMultiplier = 0.35f;
        [Tooltip("Approximate damage per second to asteroid (and self) while you are actively pushing into an asteroid. Tuned so sustained collisions overwhelm regen.")]
        [SerializeField] private float ramDamagePerSecond = 30f;
        [Tooltip("Interval between collision ramming ticks (seconds). Every tick applies a small pushback and mutual damage while in contact with an asteroid.")]
        [SerializeField] private float ramTickInterval = 0.25f;
        private float lastRamDamageTime = -999f;
        [Tooltip("When overlapping an asteroid (e.g. after respawn), ship is pushed outward at this speed for a smooth escape.")]
        [SerializeField] private float overlapEscapeSpeed = 4f;
        [Tooltip("Base pushback speed applied on each collision tick while in contact with an asteroid. Higher = stronger bounce. Scaled by asteroid size.")]
        [SerializeField] private float asteroidCollisionPushbackSpeed = 1.0f;

        [Tooltip("Pushback at low momentum. Higher = more bounce.")]
        [SerializeField] private float rammingPushbackMaxMultiplier = 1.15f;
        [Tooltip("Pushback at high momentum. Lower = more 'crash through' (less bounce).")]
        [SerializeField] private float rammingPushbackMinMultiplier = 0.25f;
        [Tooltip("How quickly momentum ramps pushback down. ~1 means full-speed ships feel meaningfully different.")]
        [SerializeField] private float rammingMomentumInfluenceForPushback = 1f;

        [Tooltip("Cap for sustained ramming DPS (pre asteroid/self split).")]
        [SerializeField] private float maxRammingDamagePerSecond = 90f;

        [Header("Collision Impact Feedback")]
        [Tooltip("Camera shake amount on asteroid and ship impacts for the owning player (0..1).")]
        [SerializeField] private float collisionShakeAmountMin = 0.05f;
        [Tooltip("Camera shake amount on asteroid and ship impacts for the owning player (0..1).")]
        [SerializeField] private float collisionShakeAmountMax = 0.35f;
        [Tooltip("Multiplier on damage-based shake intensity (sustained and impacts).")]
        [SerializeField] private float collisionShakeMomentumScale = 1f;
        [Tooltip("Self hull damage on one ram impact at or above this value maps to full impact shake.")]
        [SerializeField] private float collisionShakeFullAtSelfDamage = 25f;
        [Tooltip("Self hull damage per second while grinding at or above this maps to full sustained shake.")]
        [SerializeField] private float collisionShakeFullAtSelfDps = 45f;
        [Tooltip("Ship–ship bumps use no hull damage; blend in shake from closing speed up to this fraction of full shake.")]
        [SerializeField] private float shipRamContactShakeFromSpeed = 0.42f;

        /// <summary>Tracks asteroid collision pairs so we clear sustained shake only when the last asteroid contact ends.</summary>
        private readonly HashSet<int> _asteroidRamContactInstanceIds = new HashSet<int>();

        private ClientRpcParams OwnerOnlyClientRpcParams => new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
        };

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
        [SerializeField] private float healthRegenRate = 6f;

        [Header("Capacity (ship level only - upgrades with ship level)")]
        [SerializeField] private float gemCapacity = 100f;
        [SerializeField] private float peopleCapacity = 10f;

        [Header("Mass (affects momentum and ramming)")]
        [Tooltip("Base mass when no chassis. Chassis components override with component weights. Mass is not scaled by ship level or cards.")]
        [SerializeField] private float baseMass = 1f;
        [Tooltip("Added mass per gem carried. Ship feels heavier when full; more momentum when braking.")]
        [SerializeField] private float massPerGem = 0.01f;

        [Header("Energy (weapon system)")]
        [SerializeField] private float energyCapacity = 50f;
        [SerializeField] private float energyRegenRate = 5f;
        private const float ENERGY_PER_SHOT = 1f;

        [Header("References")]
        [SerializeField] private PlayerInputHandler inputHandler;
        [SerializeField] private Rigidbody rb;
        [Tooltip("Optional: child transform whose visuals are replaced when upgrading to a new ship prefab. If null, direct children of this transform are replaced. Also the transform we tilt for banking; if null at Start, a pivot is created so banking works.")]
        [SerializeField] private Transform visualRoot;
        [Tooltip("Multiplies the loaded ship prefab scale (chassis size in the world). Lower values make the whole ship look smaller; gem-moon dock scales apply on top of this.")]
        [SerializeField] private float shipVisualScaleMultiplier = 0.175f;

        [Header("Banking (fallback when shipData has no values)")]
        [SerializeField] private float defaultMaxBankAngle = 111f;
        [SerializeField] private float defaultBankSmoothing = 2f;

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
        /// <summary>Server-authored: ship is snapped to the planet gem moon (safe from damage; gem deposit + orbit station UI).</summary>
        private NetworkVariable<bool> gemMoonDocked = new NetworkVariable<bool>(false);
        /// <summary>Server: NetworkObjectId of the planet whose gem moon we are docked at (0 when not docked).</summary>
        private NetworkVariable<ulong> gemMoonPlanetNetworkObjectId = new NetworkVariable<ulong>(0ul);
        /// <summary>Server time until which the moon trigger ignores this ship (so undocking isn't immediately canceled).</summary>
        private NetworkVariable<float> gemMoonDockIgnoreUntilServerTime = new NetworkVariable<float>(0f);

        // Attribute upgrade levels (Level N ship = up to N upgrades per attribute)
        private NetworkVariable<int> attrMovementSpeed = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrEnergyCapacity = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrFirePower = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrFireRate = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrBulletSpeed = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrMaxHealth = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrHealthRegen = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrRotationSpeed = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrEnergyRegen = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrGemCapacity = new NetworkVariable<int>(0);
        private NetworkVariable<int> attrPeopleCapacity = new NetworkVariable<int>(0);

        // Store inventory (rockets and mines)
        private NetworkVariable<int> smallRocketsCount = new NetworkVariable<int>(0);
        private NetworkVariable<int> largeRocketsCount = new NetworkVariable<int>(0);
        private NetworkVariable<int> smallMinesCount = new NetworkVariable<int>(0);
        private NetworkVariable<int> largeMinesCount = new NetworkVariable<int>(0);

        /// <summary>Index into ShipUnlockTable.entries for the current chassis (-1 = default/unknown grid). Synced so clients can show correct grid sizes.</summary>
        private NetworkVariable<int> currentChassisIndex = new NetworkVariable<int>(-1);
        /// <summary>Chassis ID (e.g. CraizanStar_05) when using planet ship families. Used to resolve prefab from correct family.</summary>
        private NetworkVariable<FixedString64Bytes> currentChassisId = new NetworkVariable<FixedString64Bytes>(default);

        /// <summary>Ship level synced to clients so orbit UI shows correct slot count (level 2 = 2 slots, etc.).</summary>
        private NetworkVariable<int> networkShipLevel = new NetworkVariable<int>(1);
        /// <summary>Upgrade-tree branch index (0..level-1), synced so clients match ladder choices without relying on shared ShipData assets.</summary>
        private NetworkVariable<int> networkBranchIndex = new NetworkVariable<int>(0);

        [Header("Card Loadout (WIP)")]
        [Tooltip("Equipped upgrade cards for this ship. Server-authoritative; synced to clients via equippedCardIds for UI display.")]
        [SerializeField] private List<CardData> equippedCards = new List<CardData>();

        /// <summary>Synced list of equipped card IDs so clients can display loadout. Server keeps this in sync with equippedCards.</summary>
        private NetworkList<EquippedCardId> equippedCardIds;

        private const float ATTR_MULTIPLIER_PER_LEVEL = 0.1f;

        /// <summary>Engine thrust force. More engines = more force; heavier ship = less acceleration (F/m).</summary>
        private float EffectiveEngineThrust
        {
            get
            {
                float baseThrust = componentEngineThrust > 0f ? componentEngineThrust : engineThrust;
                float baseWithCards = baseThrust + GetCardMovementSpeedAdd();
                float attrScale = 1f + attrMovementSpeed.Value * ATTR_MULTIPLIER_PER_LEVEL;
                // Boost acceleration so ships feel snappier. 5x matches previous feel better after mass changes.
                const float ENGINE_THRUST_VISIBILITY = 10f;
                return baseWithCards * attrScale * FriendlyTerritoryMovementMultiplier * ENGINE_THRUST_VISIBILITY;
            }
        }
        /// <summary>Max speed from engines. More engines = higher cap. Scaled by attr/cards.</summary>
        private float EffectiveMaxSpeed
        {
            get
            {
                float baseSpeed = componentEngineMaxSpeed > 0f ? componentEngineMaxSpeed : engineThrust * 0.5f;
                float baseWithCards = baseSpeed + GetCardMovementSpeedAdd() * 0.5f;
                float attrScale = 1f + attrMovementSpeed.Value * ATTR_MULTIPLIER_PER_LEVEL;
                float speed = Mathf.Max(2f, baseWithCards * attrScale);
                return speed * FriendlyTerritoryMovementMultiplier;
            }
        }

        /// <summary>When in a friendly triangle, ships move 5% per home planet level faster. Otherwise 1.</summary>
        private float FriendlyTerritoryMovementMultiplier
        {
            get
            {
                if (PlanetConnectionSystem.Instance == null || shipTeam.Value == TeamManager.Team.None) return 1f;
                Vector3 pos = ToroidalMap.WrapPosition(transform.position);
                TeamManager.Team teamAtPos = PlanetConnectionSystem.Instance.GetTeamAtPosition(pos);
                if (teamAtPos != shipTeam.Value) return 1f;
                int homeLevel = PlanetConnectionSystem.GetHomePlanetLevelForTeam(shipTeam.Value);
                return 1f + 0.05f * homeLevel;
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
                float chassis = rotationSpeedFromShipFamilyDefinition
                    ? Mathf.Max(1f, rotationSpeed) * ShipTurnDefinitionToDegreesPerSecond
                    : rotationSpeed;
                float baseWithCards = chassis + GetCardRotationSpeedAdd();
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

        /// <summary>Weapon component scale from Fire Power + Bullet Speed attributes and cards. Used for weapon visuals and muzzle particles.</summary>
        private float WeaponComponentScaleMultiplier
        {
            get
            {
                float ex = EffectiveAttributeScaleExaggeration;
                float cardWeapon = (GetCardDamageMultiplier() - 1f) * 10f + (GetCardBulletSpeedMultiplier() - 1f) * 10f;
                return 1f + ((attrFirePower.Value + attrBulletSpeed.Value) * 0.5f + cardWeapon * 0.5f) * ex;
            }
        }

        /// <summary>Same factor that makes fire rate faster (1 + attrFirePower * 0.1). Used so bullet size always scales when fire power upgrades.</summary>
        private float FirePowerScaleFactor => 1f + attrFirePower.Value * ATTR_MULTIPLIER_PER_LEVEL;

        /// <summary>Bullet projectile scale: base size × damage term × fire-power factor (same as fire rate) × weapon/card scale × exaggeration. Ensures upgrading fire power visibly increases bullet size.</summary>
        private float BulletScaleMultiplier => FirePowerScaleFactor * WeaponComponentScaleMultiplier * Mathf.Max(0.5f, bulletScaleExaggeration);

#if UNITY_EDITOR
        // Editor-only helpers exposing effective ship ability stats for inspector visualizations
        public float EditorFirePowerMultiplier => DamageMultiplier;
        public float EditorBulletSpeedMultiplier => SpeedMultiplier;
        public float EditorHealthCap => MaxHealth;
        public float EditorHealthRegen => EffectiveHealthRegen;
        public float EditorEnergyCap => EffectiveEnergyCapacity;
        public float EditorEnergyRegen => EffectiveEnergyRegen;
        public float EditorMoveSpeed => EffectiveMaxSpeed;
        public float EditorTurnSpeed => EffectiveRotationSpeed;
        public float EditorMaxGems => GemCapacity;
        public float EditorMaxPeople => PeopleCapacity;
#endif

        /// <summary>Mass from components + gems. Not scaled by ship level or cards.</summary>
        private float EffectiveMass
        {
            get
            {
                float baseValue = componentMass > 0f ? componentMass : baseMass;
                return Mathf.Max(0.5f, baseValue + currentGems.Value * massPerGem);
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
        private Planet currentOrbitPlanet; // When non-null, we're in a planet's orbit zone
        private float lastOrbitDetectServerTime = -999f;
        private float lastOrbitDetectClientTime = -999f;
        private const float OrbitDetectInterval = 1.5f;
        private bool wasMovePressedLastFrame;
        private float depositAccumulator; // Gems accumulated for deposit (1 gem per spawn, interval = 1/(shipLevel*2) sec)
        private float lastDepositSpawnTime = -999f;
        private float peopleLoadAccumulator;
        private float peopleUnloadAccumulator;
        private float lastPeopleSpawnTime = -999f;
        private float peopleInTransit; // People in projectiles heading to this ship (load only)

        // Galactic zoom tracking (server-side)
        private bool hadGemsWhileInOrbitThisOrbit;
        private bool triggeredGalacticZoomThisOrbit;
        private bool depositedAnyGemsThisOrbit;

        // Banking (visual lean into turn) - only used when visualRoot is set.
        private float currentBankAngle;
        private Vector3 previousForward;
        private bool bankingInitialized;
        
        // Pitch from asteroid impacts (visual only, up/down tilt on visualRoot)
        [Header("Collision Pitch")]
        [Tooltip("Maximum pitch angle (degrees) the ship can visually tilt from asteroid impacts.")]
        [SerializeField] private float maxCollisionPitchAngle = 20f;
        [Tooltip("Pitch smoothing speed. Higher = snappier response to new pitch (approximate lerp speed).")]
        [SerializeField] private float collisionPitchSpeed = 1f;
        private float currentCollisionPitchAngle;
        private float targetCollisionPitchAngle;

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
        /// <summary>Max gem capacity. Base comes from ShipFamilyDefinition (via chassis components), plus card bonuses and attribute upgrades.</summary>
        public float GemCapacity
        {
            get
            {
                float baseWithCards = gemCapacity + GetCardGemCapacityAdd();
                float attrScale = 1f + attrGemCapacity.Value * ATTR_MULTIPLIER_PER_LEVEL;
                return Mathf.Max(0f, baseWithCards * attrScale);
            }
        }

        /// <summary>Base gem capacity without card bonuses. Comes from ShipFamilyDefinition (via chassis components).</summary>
        public float BaseGemCapacity => Mathf.Max(0f, gemCapacity);

        /// <summary>Stat value as if no attribute upgrades (attr=0). Used to scale components by percentage increase (current/base).</summary>
        private float BaseMaxHealthNoAttr => Mathf.Max(1f, maxHealth + GetCardMaxHealthAdd());
        private float BaseGemCapacityNoAttr => Mathf.Max(0.1f, gemCapacity + GetCardGemCapacityAdd());
        private float BasePeopleCapacityNoAttr => Mathf.Max(0.1f, peopleCapacity);
        private float BaseEnergyCapacityNoAttr => Mathf.Max(0.1f, energyCapacity + GetCardEnergyCapacityAdd());
        private float BaseEnergyRegenNoAttr => Mathf.Max(0.01f, energyRegenRate + GetCardEnergyRegenAdd());
        private float BaseRotationSpeedNoAttr
        {
            get
            {
                float chassis = rotationSpeedFromShipFamilyDefinition
                    ? Mathf.Max(1f, rotationSpeed) * ShipTurnDefinitionToDegreesPerSecond
                    : rotationSpeed;
                return Mathf.Max(1f, chassis + GetCardRotationSpeedAdd());
            }
        }
        private float BaseHealthRegenNoAttr => Mathf.Max(0.01f, healthRegenRate + GetCardHealthRegenAdd());
        private float BaseMaxSpeedNoAttr
        {
            get
            {
                float baseSpeed = componentEngineMaxSpeed > 0f ? componentEngineMaxSpeed : engineThrust * 0.5f;
                return Mathf.Max(0.1f, baseSpeed + GetCardMovementSpeedAdd() * 0.5f);
            }
        }
        private float BaseDamageMultiplierNoAttr => Mathf.Max(0.1f, GetCardDamageMultiplier());
        private float BaseSpeedMultiplierNoAttr => Mathf.Max(0.1f, GetCardBulletSpeedMultiplier());
        public float CurrentPeople => currentPeople.Value;
        /// <summary>Server-only: release people-in-transit when a load projectile delivers. Call from PeopleTransportProjectile.</summary>
        public void ReleasePeopleInTransit(float amount)
        {
            if (IsServer)
                peopleInTransit = Mathf.Max(0f, peopleInTransit - amount);
        }
        public float PeopleCapacity
        {
            get
            {
                float attrScale = 1f + attrPeopleCapacity.Value * ATTR_MULTIPLIER_PER_LEVEL;
                return peopleCapacity * attrScale;
            }
        }
        public float CurrentEnergy => currentEnergy.Value;
        public float EnergyCapacity => EffectiveEnergyCapacity;
        public IReadOnlyList<CardData> EquippedCards => GetEquippedCardsForDisplay();

        private readonly List<CardData> _clientEquippedCardsCache = new List<CardData>();

        private IReadOnlyList<CardData> GetEquippedCardsForDisplay()
        {
            if (IsServer)
                return equippedCards ?? (IReadOnlyList<CardData>)new List<CardData>();
            _clientEquippedCardsCache.Clear();
            if (equippedCardIds != null && Systems.CardShopSystem.Instance != null)
            {
                for (int i = 0; i < equippedCardIds.Count; i++)
                {
                    var card = Systems.CardShopSystem.Instance.GetCardById(equippedCardIds[i].cardId.ToString());
                    if (card != null)
                        _clientEquippedCardsCache.Add(card);
                }
            }
            return _clientEquippedCardsCache;
        }

        /// <summary>Number of card slots on this ship (1 per ship level). Each slot holds at most one card.</summary>
        public int SlotCount => (IsSpawned && networkShipLevel != null) ? Mathf.Max(1, networkShipLevel.Value) : Mathf.Max(1, shipLevel);

        /// <summary>True if there is at least one empty slot.</summary>
        public bool HasEmptySlot => equippedCards != null && equippedCards.Count < SlotCount;
        public TeamManager.Team ShipTeam => shipTeam.Value;
        public int ShipLevel => (IsSpawned && networkShipLevel != null) ? networkShipLevel.Value : shipLevel;
        public int BranchIndex => (IsSpawned && networkBranchIndex != null) ? networkBranchIndex.Value : (shipData != null ? shipData.branchIndex : 0);
        public ShipFocusType FocusType => focusType;
        public bool IsInOrbit => currentOrbitPlanet != null;
        public Planet CurrentOrbitPlanet => currentOrbitPlanet;
        public bool WantToLoadPeople => wantToLoadPeople.Value;
        public bool WantToUnloadPeople => wantToUnloadPeople.Value;
        public bool WantToDepositGems => wantToDepositGems.Value;
        /// <summary>True when docked at the planet's gem moon (synced from server).</summary>
        public bool GemMoonDocked => gemMoonDocked.Value;
        public float GemMoonDockIgnoreUntilServerTime => gemMoonDockIgnoreUntilServerTime.Value;
        public int SmallRocketsCount => smallRocketsCount.Value;
        public int LargeRocketsCount => largeRocketsCount.Value;
        public int SmallMinesCount => smallMinesCount.Value;
        public int LargeMinesCount => largeMinesCount.Value;
        /// <summary>Chassis index in ShipUnlockTable (-1 = default). Used by UI for grid dimensions.</summary>
        public int CurrentChassisIndex => currentChassisIndex.Value;
        /// <summary>Chassis ID (e.g. AstroEagle_01) for upgrade/shop logic.</summary>
        public string CurrentChassisId => currentChassisId.Value.ToString();

        /// <summary>Attribute upgrade levels for Ship Attribute Upgrade HUD.
        /// Index: 0=FirePower, 1=BulletSpeed, 2=MaxHealth, 3=HealthRegen, 4=EnergyCapacity, 5=EnergyRegen, 6=MovementSpeed, 7=RotationSpeed, 8=GemCapacity, 9=PeopleCapacity.</summary>
        public int GetAttributeLevel(int index)
        {
            return index switch
            {
                0 => attrFirePower.Value,
                1 => attrBulletSpeed.Value,
                2 => attrMaxHealth.Value,
                3 => attrHealthRegen.Value,
                4 => attrEnergyCapacity.Value,
                5 => attrEnergyRegen.Value,
                6 => attrMovementSpeed.Value,
                7 => attrRotationSpeed.Value,
                8 => attrGemCapacity.Value,
                9 => attrPeopleCapacity.Value,
                _ => 0
            };
        }

        /// <summary>Cost per attribute upgrade: ShipLevel * 5 gems.</summary>
        public int AttributeUpgradeCost => ShipLevel * 5;

        /// <summary>Max attribute upgrades per stat = ShipLevel.</summary>
        public int MaxAttributeUpgrades => ShipLevel;

        private const float FIXED_Y_POSITION = 0f;

        /// <summary>Ship level scale disabled. Was 1.2^(level-1); now always 1.</summary>
        public float LevelScaleFactor => 1f;

        /// <summary>Cached so we don't call GetComponent every frame in Update.</summary>
        private bool _isAIControlled;
        /// <summary>Base visual scale (from ShipData/chassis).</summary>
        private float visualBaseScale = 1f;
        /// <summary>Prefab root localScale from the loaded model (for re-applying with level scale in LateUpdate).</summary>
        private Vector3 lastPrefabScale = Vector3.one;
        /// <summary>Local scale cache so gem-moon docking can scale the whole ship safely.</summary>
        private Vector3 baseLocalScale = Vector3.one;

        private void Awake()
        {
            _isAIControlled = GetComponent<TitanOrbit.AI.AIStarshipController>() != null;
            // Run before OnNetworkSpawn/SetShipData so the BankPivot + Prefab structure exists.
            EnsureVisualRootForBanking();

            baseLocalScale = transform.localScale;

            if (rb == null) rb = GetComponent<Rigidbody>();
            rootCollider = GetComponent<Collider>();
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

            equippedCardIds = new NetworkList<EquippedCardId>();
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
            prefabTransformCache = prefabContainer.transform;
            gemMoonPrefabBaselineLocalScale = prefabTransformCache.localScale;

            visualRoot = pivot.transform;
        }

        private void RefreshGemMoonPrefabBaseline()
        {
            Transform t = prefabTransformCache != null ? prefabTransformCache : GetPrefabTransform();
            if (t == null || visualRoot == null || t.parent != visualRoot) return;
            gemMoonPrefabBaselineLocalScale = t.localScale;
        }

        /// <summary>Returns the Prefab transform (StarshipMain -> BankPivot -> Prefab) where the loaded ship is added.</summary>
        private Transform GetPrefabTransform()
        {
            if (visualRoot == null || visualRoot == transform) return transform;
            if (prefabTransformCache != null) return prefabTransformCache;
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
            prefabTransformCache = prefab;
            return prefab;
        }

        /// <summary>No longer creates a fallback; bullets fire only from Weapon component positions (bulletFirePoints).</summary>
        private void EnsureFirePoint()
        {
            // Intentionally do not create a FirePoint GameObject. Only Weapon components provide fire positions.
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
            // Remove from global registry if present
            AllStarships.Remove(this);
            equippedCardIds?.Dispose();
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
            if (!AllStarships.Contains(this))
                AllStarships.Add(this);
            // Server: sync initial ship level so clients show correct slot count
            if (IsServer && networkShipLevel != null)
                networkShipLevel.Value = Mathf.Max(1, shipLevel);
            if (IsServer && networkBranchIndex != null && shipData != null)
                networkBranchIndex.Value = shipData.branchIndex;

            // Server: sync existing equipped cards to NetworkList (e.g. from save or late-join)
            if (IsServer && equippedCardIds != null && equippedCards != null)
            {
                for (int i = equippedCardIds.Count; i < equippedCards.Count; i++)
                {
                    if (i < equippedCards.Count && equippedCards[i] != null)
                        equippedCardIds.Add(new EquippedCardId { cardId = new FixedString64Bytes(equippedCards[i].cardId) });
                }
            }

            // Server: apply starter ship (chassis 0) first so SetShipData won't overwrite with a different prefab
            if (IsServer && !_isAIControlled && currentChassisIndex.Value == -1 && CardShopSystem.Instance != null)
            {
                string starterChassisId = CardShopSystem.Instance.GetStarterChassisId();
                GameObject starterPrefab = !string.IsNullOrEmpty(starterChassisId) ? CardShopSystem.Instance.GetShipPrefabForChassisId(starterChassisId) : null;
                if (starterPrefab == null)
                    starterPrefab = CardShopSystem.Instance.GetShipPrefabForChassisIndex(0);
                if (starterPrefab != null)
                {
                    ApplyShipVisualFromPrefab(starterPrefab);
                    SetCurrentChassisIndex(0);
                    if (!string.IsNullOrEmpty(starterChassisId)) SetCurrentChassisId(starterChassisId);
                    _lastAppliedChassisIndex = 0;
                }
                else
                    Debug.LogWarning("Starship: No starter ship prefab. Assign ShipUnlockTable.homeShipFamilyDefinition (e.g. AstroEagleShipFamily) with upgrade tree prefabs, and ensure CardShopSystem references the same ShipUnlockTable.");
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
            foreach (var hp in HomePlanet.AllHomePlanets)
            {
                if (hp == null) continue;
                if (hp.AssignedTeam == shipTeam.Value) { home = hp; break; }
            }
            if (home == null) return;
            float orbitRadius = home.PlanetSize * 0.6f;
            Vector3 planetPos = home.transform.position;
            Vector3 orbitPos = planetPos + new Vector3(orbitRadius, 0f, 0f);
            orbitPos.y = FIXED_Y_POSITION;
            rb.position = orbitPos;

            float innerWorld = home.PlanetSize * 0.5f;
            float outerWorld = home.PlanetSize * home.GetOrbitZoneOuterRadiusLocal();
            float targetSpeed = GetOrbitTargetSpeed(home, orbitRadius, innerWorld, outerWorld);

            rb.linearVelocity = new Vector3(0f, 0f, -targetSpeed); // Tangent for clockwise orbit
            currentVelocity = rb.linearVelocity;
        }

        private void Update()
        {
            float updateStartTime = Time.realtimeSinceStartup;
            bool didTriggerZoomReturn = false;
            bool didShowOrbitUI = false;
            bool didHideOrbitUI = false;
            // Server: regen for ALL ships (including AI) - run before IsOwner check
            if (IsServer && !isDead.Value)
            {
                HandleHealthRegen();
                HandleEnergyRegen();
            }

            // Server: ensure first ship (no chassis yet) gets starter visual (AstroEagle_01 or first family's ship 1)
            if (IsServer && !_isAIControlled && currentChassisIndex.Value == -1 && _lastAppliedChassisIndex == -2 && CardShopSystem.Instance != null)
            {
                string starterChassisId = CardShopSystem.Instance.GetStarterChassisId();
                GameObject prefab = !string.IsNullOrEmpty(starterChassisId) ? CardShopSystem.Instance.GetShipPrefabForChassisId(starterChassisId) : null;
                if (prefab == null)
                    prefab = CardShopSystem.Instance.GetShipPrefabForChassisIndex(0);
                if (prefab != null)
                {
                    ApplyShipVisualFromPrefab(prefab);
                    SetCurrentChassisIndex(0);
                    if (!string.IsNullOrEmpty(starterChassisId)) SetCurrentChassisId(starterChassisId);
                    _lastAppliedChassisIndex = 0;
                }
            }
            // Owner: when chassis index is set (or synced), apply that ship visual so client sees the correct model
            if (IsOwner && currentChassisIndex.Value >= 0 && currentChassisIndex.Value != _lastAppliedChassisIndex && CardShopSystem.Instance != null)
            {
                string cid = currentChassisId.Value.ToString();
                GameObject prefab = !string.IsNullOrEmpty(cid) ? CardShopSystem.Instance.GetShipPrefabForChassisId(cid) : null;
                if (prefab == null)
                    prefab = CardShopSystem.Instance.GetShipPrefabForChassisIndex(currentChassisIndex.Value);
                if (prefab != null)
                {
                    ApplyShipVisualFromPrefab(prefab);
                    _lastAppliedChassisIndex = currentChassisIndex.Value;
                }
                else if (currentChassisIndex.Value != _lastAppliedChassisIndex)
                {
                    Debug.LogWarning($"Starship: No prefab for chassis '{cid}' (index {currentChassisIndex.Value}). Assign ShipUnlockTable.homeShipFamilyDefinition with an upgrade tree that has prefabs set, or assign CardShopSystem's Ship Unlock Table.");
                    _lastAppliedChassisIndex = currentChassisIndex.Value;
                }
            }

            if (!IsOwner) return;
            // AI ships have their own controller; skip player input and orbit UI logic
            if (_isAIControlled) return;

            HandleInput();
            bool movePressed = inputHandler != null && inputHandler.MoveForwardPressed;

            if (movePressed && !wasMovePressedLastFrame && gemMoonDocked.Value)
                RequestUndockGemMoonServerRpc();

            // When the local player begins moving (e.g. right click), trigger camera zoom-in if a galactic zoom is active.
            if (IsLocalPlayerShip() && movePressed && !wasMovePressedLastFrame)
            {
                if (s_cachedCameraController == null)
                    s_cachedCameraController = UnityEngine.Object.FindFirstObjectByType<TitanOrbit.Camera.CameraController>();
                if (s_cachedCameraController != null)
                {
                    s_cachedCameraController.TriggerGalacticZoomReturn();
                    didTriggerZoomReturn = true;
                }
            }

            bool isLocalWithTeam = IsLocalPlayerShip() && shipTeam.Value != TeamManager.Team.None;
            Planet orbitUiPlanet = currentOrbitPlanet;
            if (orbitUiPlanet == null && gemMoonDocked.Value)
                orbitUiPlanet = ResolveGemMoonDockPlanet();
            bool shouldShowOrbitUI = isLocalWithTeam && !movePressed && orbitUiPlanet != null && gemMoonDocked.Value;
            if (isLocalWithTeam)
            {
                if (s_cachedOrbitUI == null)
                    s_cachedOrbitUI = TitanOrbit.UI.HomePlanetOrbitUI.GetOrCreate();
                if (s_cachedOrbitUI != null)
                {
                    // Only toggle orbit UI when visibility state actually changes to avoid redundant Show/Hide work.
                    if (shouldShowOrbitUI && !_orbitUiVisible)
                    {
                        s_cachedOrbitUI.Show(this, orbitUiPlanet);
                        didShowOrbitUI = true;
                        _orbitUiVisible = true;
                    }
                    else if (!shouldShowOrbitUI && _orbitUiVisible)
                    {
                        s_cachedOrbitUI.Hide();
                        didHideOrbitUI = true;
                        _orbitUiVisible = false;
                    }
                }
            }

            wasMovePressedLastFrame = movePressed;
            // If we're in orbit zone but trigger didn't fire (e.g. spawned there), detect it occasionally (avoid per-frame FindObjectsOfType cost).
            if (currentOrbitPlanet == null && Time.time - lastOrbitDetectClientTime >= OrbitDetectInterval)
            {
                lastOrbitDetectClientTime = Time.time;
                TryDetectOrbitZone();
            }

            // #region agent log
            if (IsOwner && !_isAIControlled)
            {
                int frame = Time.frameCount;
                if ((frame % 180) == 0)
                {
                    float durMs = (Time.realtimeSinceStartup - updateStartTime) * 1000f;
                    TitanOrbit.Core.DebugSessionLog.Write(
                        "Starship.Update",
                        "starship update",
                        "{\"durationMs\":" + durMs +
                        ",\"didTriggerZoomReturn\":" + (didTriggerZoomReturn ? "true" : "false") +
                        ",\"didShowOrbitUI\":" + (didShowOrbitUI ? "true" : "false") +
                        ",\"didHideOrbitUI\":" + (didHideOrbitUI ? "true" : "false") +
                        ",\"hasOrbitPlanet\":" + (currentOrbitPlanet != null ? "true" : "false") +
                        "}",
                        "SU");
                }
            }
            // #endregion
        }

        private void LateUpdate()
        {
            float startTime = Time.realtimeSinceStartup;

            RefreshCardStatsCache();
            if (visualBaseScale > 0.001f && lastPrefabScale.sqrMagnitude > 0.001f)
            {
                Transform root = GetPrefabTransform();
                if (root != null)
                {
                    float v = visualBaseScale * Mathf.Max(0.001f, gemMoonVisualScaleMultiplier);
                    root.localScale = Vector3.Scale(lastPrefabScale, Vector3.one * v);
                }
            }
            ApplyComponentAttributeScaling();
            UpdateEngineAndThrusterVFX();
            if (visualRoot == null || visualRoot == transform || isDead.Value || rb == null) return;
            ApplyVisualBanking(Time.deltaTime);

            // #region agent log
            if (IsOwner && !_isAIControlled)
            {
                int frame = Time.frameCount;
                if ((frame % 180) == 0)
                {
                    float durMs = (Time.realtimeSinceStartup - startTime) * 1000f;
                    TitanOrbit.Core.DebugSessionLog.Write(
                        "Starship.LateUpdate",
                        "starship lateupdate",
                        "{\"durationMs\":" + durMs +
                        ",\"cockpitCount\":" + cockpitScaleTransforms.Count +
                        ",\"wingCount\":" + wingScaleTransforms.Count +
                        ",\"engineCount\":" + engineScaleTransforms.Count +
                        ",\"thrusterCount\":" + thrusterScaleTransforms.Count +
                        ",\"partCount\":" + partScaleTransforms.Count + "}",
                        "S");
                }
            }
            // #endregion
        }

        /// <summary>Effective exaggeration. Uses GameManager when set; else per-ship value (legacy 0.5 treated as 0.15).</summary>
        private float EffectiveAttributeScaleExaggeration
        {
            get
            {
                if (GameManager.Instance != null && GameManager.Instance.AttributeScaleExaggeration > 0f)
                    return GameManager.Instance.AttributeScaleExaggeration;
                if (attributeScaleExaggeration > 0f)
                    return Mathf.Approximately(attributeScaleExaggeration, 0.5f) ? 0.2f : attributeScaleExaggeration;
                return 0.2f;
            }
        }

        /// <summary>Refreshes cached card stat sums once per frame so we don't iterate equippedCards 16+ times in LateUpdate and property getters.</summary>
        private void RefreshCardStatsCache()
        {
            int frame = Time.frameCount;
            if (_cardStatsCacheFrame == frame) return;
            _cardStatsCacheFrame = frame;

            _cachedCardMovementSpeedAdd = 0f;
            _cachedCardRotationSpeedAdd = 0f;
            _cachedCardMaxHealthAdd = 0f;
            _cachedCardHealthRegenAdd = 0f;
            _cachedCardEnergyCapacityAdd = 0f;
            _cachedCardEnergyRegenAdd = 0f;
            _cachedCardGemCapacityAdd = 0f;
            _cachedCardPeopleCapacityAdd = 0f;
            _cachedCardDamageMultiplier = 1f;
            _cachedCardBulletSpeedMultiplier = 1f;

            if (equippedCards == null) return;
            foreach (var card in equippedCards)
            {
                if (card == null) continue;
                float scale = CardLevelScale(Mathf.Max(1, card.cardLevel)) * CardRarityScale(Mathf.Max(1, card.rarity));
                _cachedCardMovementSpeedAdd += card.movementSpeedAdd * scale;
                _cachedCardRotationSpeedAdd += card.rotationSpeedAdd * scale;
                _cachedCardMaxHealthAdd += card.maxHealthAdd * scale;
                _cachedCardHealthRegenAdd += card.healthRegenAdd * scale;
                _cachedCardEnergyCapacityAdd += card.energyCapacityAdd * scale;
                _cachedCardEnergyRegenAdd += card.energyRegenAdd * scale;
                _cachedCardGemCapacityAdd += card.gemCapacityAdd * scale;
                _cachedCardPeopleCapacityAdd += card.peopleCapacityAdd * scale;
                if (card.damageMultiplier > 0f)
                {
                    float bonus = (card.damageMultiplier - 1f) * scale + 1f;
                    _cachedCardDamageMultiplier *= bonus;
                }
                if (card.bulletSpeedMultiplier > 0f)
                {
                    float bonus = (card.bulletSpeedMultiplier - 1f) * scale + 1f;
                    _cachedCardBulletSpeedMultiplier *= bonus;
                }
            }
        }

        /// <summary>Scale ship components by the percentage increase in their associated stats (current value / base value at 0 upgrades). E.g. wings scale with max gems and turn speed.</summary>
        private void ApplyComponentAttributeScaling()
        {
            float vis = Mathf.Max(0.2f, componentScaleVisibility);

            // Stat ratios: current / base (base = value at 0 attribute upgrades). Ratio = 1 at no upgrades, >1 when upgraded.
            float ratioHealth = MaxHealth / BaseMaxHealthNoAttr;
            float ratioGem = GemCapacity / BaseGemCapacityNoAttr;
            float ratioPeople = PeopleCapacity / BasePeopleCapacityNoAttr;
            float ratioEnergyCap = EffectiveEnergyCapacity / BaseEnergyCapacityNoAttr;
            float ratioEnergyRegen = EffectiveEnergyRegen / BaseEnergyRegenNoAttr;
            float ratioTurn = EffectiveRotationSpeed / BaseRotationSpeedNoAttr;
            float ratioRegen = EffectiveHealthRegen / BaseHealthRegenNoAttr;
            float ratioMove = EffectiveMaxSpeed / BaseMaxSpeedNoAttr;
            float ratioDamage = DamageMultiplier / BaseDamageMultiplierNoAttr;
            float ratioBulletSpeed = SpeedMultiplier / BaseSpeedMultiplierNoAttr;

            float StatScale(float ratio, float visibility, float boost = 1f)
            {
                float clampedRatio = Mathf.Max(1f, ratio);
                return Mathf.Max(1f, 1f + (clampedRatio - 1f) * visibility * Mathf.Max(0.01f, boost));
            }

            // Blend average with strongest contributor so a large single upgrade still shows clearly.
            float avgCockpit = (ratioHealth + ratioPeople + ratioEnergyCap + ratioEnergyRegen) * 0.25f;
            float avgWeapon = (ratioDamage + ratioBulletSpeed) * 0.5f;
            float avgPart = (ratioHealth + ratioRegen + ratioGem + ratioPeople) * 0.25f;

            float cockpitScale = Mathf.Max(StatScale(avgCockpit, vis), StatScale(Mathf.Max(Mathf.Max(ratioHealth, ratioPeople), Mathf.Max(ratioEnergyCap, ratioEnergyRegen)), vis, 0.9f));
            float wingScaleFromGem = StatScale(ratioGem, vis, wingGemScaleBoost);
            float wingScaleFromTurn = StatScale(ratioTurn, vis, 0.9f);
            float wingScale = Mathf.Max(wingScaleFromGem, StatScale((ratioGem + ratioTurn) * 0.5f, vis));
            wingScale = Mathf.Max(wingScale, wingScaleFromTurn);
            float weaponScale = Mathf.Max(StatScale(avgWeapon, vis), StatScale(Mathf.Max(ratioDamage, ratioBulletSpeed), vis, 0.9f));
            float engineScale = StatScale(ratioMove, vis);
            float thrusterScale = StatScale(ratioTurn, vis);
            float partScale = Mathf.Max(StatScale(avgPart, vis), StatScale(Mathf.Max(ratioGem, ratioHealth), vis, 0.85f));

            wingScale = Mathf.Min(wingScale, 3.5f);
            cockpitScale = Mathf.Min(cockpitScale, 3f);
            weaponScale = Mathf.Min(weaponScale, 3f);
            engineScale = Mathf.Min(engineScale, 3f);
            thrusterScale = Mathf.Min(thrusterScale, 3f);
            partScale = Mathf.Min(partScale, 3f);

            for (int i = 0; i < cockpitScaleTransforms.Count; i++)
            {
                if (cockpitScaleTransforms[i] != null && i < cockpitBaseScales.Count)
                {
                    cockpitScaleTransforms[i].localScale = cockpitBaseScales[i] * cockpitScale;
                    if (i < cockpitBasePositions.Count)
                        cockpitScaleTransforms[i].localPosition = cockpitBasePositions[i] * cockpitScale;
                }
            }
            for (int i = 0; i < wingScaleTransforms.Count; i++)
            {
                if (wingScaleTransforms[i] != null && i < wingBaseScales.Count)
                {
                    wingScaleTransforms[i].localScale = wingBaseScales[i] * wingScale;
                    if (i < wingBasePositions.Count)
                        wingScaleTransforms[i].localPosition = wingBasePositions[i] * wingScale;
                }
            }
            for (int i = 0; i < weaponScaleTransforms.Count; i++)
            {
                if (weaponScaleTransforms[i] != null && i < weaponBaseScales.Count)
                {
                    weaponScaleTransforms[i].localScale = weaponBaseScales[i] * weaponScale;
                    if (i < weaponBasePositions.Count)
                        weaponScaleTransforms[i].localPosition = weaponBasePositions[i] * weaponScale;
                }
            }
            for (int i = 0; i < engineScaleTransforms.Count; i++)
            {
                if (engineScaleTransforms[i] != null && i < engineBaseScales.Count)
                {
                    engineScaleTransforms[i].localScale = engineBaseScales[i] * engineScale;
                    if (i < engineBasePositions.Count)
                        engineScaleTransforms[i].localPosition = engineBasePositions[i] * engineScale;
                }
            }
            for (int i = 0; i < thrusterScaleTransforms.Count; i++)
            {
                if (thrusterScaleTransforms[i] != null && i < thrusterBaseScales.Count)
                {
                    thrusterScaleTransforms[i].localScale = thrusterBaseScales[i] * thrusterScale;
                    if (i < thrusterBasePositions.Count)
                        thrusterScaleTransforms[i].localPosition = thrusterBasePositions[i] * thrusterScale;
                }
            }
            for (int i = 0; i < partScaleTransforms.Count; i++)
            {
                if (partScaleTransforms[i] != null && i < partBaseScales.Count)
                {
                    partScaleTransforms[i].localScale = partBaseScales[i] * partScale;
                    if (i < partBasePositions.Count)
                        partScaleTransforms[i].localPosition = partBasePositions[i] * partScale;
                }
            }

            // Muzzle particles: size follows weapon scale, speed follows bullet speed ratio
            float muzzleSpeedScale = Mathf.Max(0.5f, ratioBulletSpeed);
            for (int i = 0; i < bulletMuzzleParticleSystems.Count; i++)
            {
                var ps = bulletMuzzleParticleSystems[i];
                if (ps == null) continue;
                if (i < muzzleBaseSizes.Count && i < muzzleBaseSpeeds.Count)
                {
                    var main = ps.main;
                    main.startSize = muzzleBaseSizes[i] * weaponScale;
                    main.startSpeed = muzzleBaseSpeeds[i] * muzzleSpeedScale;
                }
            }
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

        /// <summary>Maps linear dock depth (0 outer ring → 1 surface) to an ease-in-out curve for approach/scale/orientation.</summary>
        private static float GemMoonDockEaseInOut(float t)
        {
            t = Mathf.Clamp01(t);
            return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;
        }

        /// <summary>
        /// Updates banking (roll) from turn rate and blends in collision pitch.
        /// Must run on a child of the root—never on the root itself (physics/NetworkTransform would overwrite).
        /// </summary>
        private void ApplyVisualBanking(float dt)
        {
            if (visualRoot == null || visualRoot == transform || rb == null) return;
            if (gemMoonDocked.Value) return;

            Vector3 fwd = rb.rotation * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) return;
            fwd.Normalize();

            if (!bankingInitialized)
            {
                previousForward = fwd;
                currentBankAngle = 0f;
                bankingInitialized = true;
                visualRoot.localRotation = Quaternion.identity;
                return;
            }

            dt = Mathf.Max(dt, 0.0001f);

            float maxBank = shipData != null ? shipData.maxBankAngle : defaultMaxBankAngle;
            float bankSmooth = shipData != null ? shipData.bankSmoothing : defaultBankSmoothing;
            // Roll (Z): bank whenever turning; amount based on turn rate, independent of forward speed.
            float signedAngle = Vector3.SignedAngle(previousForward, fwd, Vector3.up);
            float angularVelDegPerSec = Mathf.Abs(signedAngle) / dt;
            float turnRatio = Mathf.Clamp01(angularVelDegPerSec / EffectiveRotationSpeed);
            float targetBankAngle = Mathf.Sign(signedAngle) * turnRatio * maxBank;
            float bankT = 1f - Mathf.Exp(-bankSmooth * dt);
            currentBankAngle = Mathf.Lerp(currentBankAngle, targetBankAngle, bankT);

            // Smooth collision pitch toward target
            float pitchT = 1f - Mathf.Exp(-Mathf.Max(collisionPitchSpeed, 0.01f) * dt);
            currentCollisionPitchAngle = Mathf.Lerp(currentCollisionPitchAngle, targetCollisionPitchAngle, pitchT);

            // Clamp pitch to configured max
            currentCollisionPitchAngle = Mathf.Clamp(currentCollisionPitchAngle, -maxCollisionPitchAngle, maxCollisionPitchAngle);

            // Combine pitch (X) and bank (Z)
            visualRoot.localRotation = Quaternion.Euler(currentCollisionPitchAngle, 0f, -currentBankAngle);

            previousForward = fwd;
        }

        private void FixedUpdate()
        {
            float startTime = Time.realtimeSinceStartup;
            if (rb == null) return;

            // Gem load increases mass: ship feels heavier and has more momentum (slower to accelerate/brake)
            rb.mass = EffectiveMass;

            if (gemMoonDocked.Value)
                gemMoonUndockOrbitGraceUntilTime = -1f;

            // Lock Y to play plane when not gem-moon docked, but ease down from moon height instead of snapping.
            if (!gemMoonDocked.Value)
            {
                Vector3 pos = rb.position;
                if (Mathf.Abs(pos.y - FIXED_Y_POSITION) > 0.01f)
                {
                    pos.y = Mathf.MoveTowards(pos.y, FIXED_Y_POSITION, Mathf.Max(0.01f, orbitExitYRecoverySpeed) * Time.fixedDeltaTime);
                    rb.position = pos;
                }
            }
            
            // Never wrap ship position: ship stays in world space (e.g. 100, 310). All other
            // entities are repositioned around the player via ToroidalRenderer (display copy closest to camera).
            // Keep Y velocity constrained unless docked on moon tilted-axis track.
            if (!gemMoonDocked.Value && Mathf.Abs(rb.linearVelocity.y) > 0.01f)
            {
                Vector3 vel = rb.linearVelocity;
                vel.y = 0f;
                rb.linearVelocity = vel;
            }
            
            if (IsServer)
            {
                // Server must detect orbit zone when ship spawns inside (OnTriggerEnter doesn't fire for objects that start inside).
                // Avoid calling FindObjectsOfType<Planet>() every FixedUpdate by throttling checks.
                if (currentOrbitPlanet == null && Time.time - lastOrbitDetectServerTime >= OrbitDetectInterval)
                {
                    lastOrbitDetectServerTime = Time.time;
                    TryDetectOrbitZoneServer();
                }
                HandleDeath();
                TickOrbitPopulationTransfer();
                TickOrbitGemDeposit();
                TickNearbyGemAttraction();
            }

            Planet dockPlanet = null;
            PlanetGemMoon moon = null;
            bool withinGemMoonBoundary = false;
            float moonDockOuterRadius = 0f;
            float moonDockSurfaceRadius = 0f;

            if (!isDead.Value && gemMoonDocked.Value)
            {
                dockPlanet = ResolveGemMoonDockPlanet();
                moon = dockPlanet != null ? dockPlanet.GemMoon : null;
                if (moon != null)
                {
                    gemMoonUndockCachedMoonPos = moon.transform.position;
                    Vector3 moonPosForBoundary = moon.transform.position;
                    moonPosForBoundary.y = 0f;
                    Vector3 shipPosForBoundary = rb.position;
                    shipPosForBoundary.y = 0f;
                    float distToMoon = ToroidalMap.ToroidalDistance(shipPosForBoundary, moonPosForBoundary);
                    moonDockOuterRadius = moon.GetMoonDockSnapRadiusWorld() * gemMoonLandingRangeMultiplier;
                    float bodyRadiusWorld = moon.GetMoonBodyRadiusWorld();
                    float shipRadius = 0.05f;
                    Collider shipCol = rootCollider != null ? rootCollider : GetComponent<Collider>();
                    if (shipCol != null)
                    {
                        Bounds b = shipCol.bounds;
                        shipRadius = Mathf.Max(0.05f, Mathf.Max(b.extents.x, b.extents.z) * 0.6f);
                    }
                    moonDockSurfaceRadius = bodyRadiusWorld + shipRadius;

                    withinGemMoonBoundary = moonDockOuterRadius > 0.0001f
                        && distToMoon <= moonDockOuterRadius;

                }
            }

            if (!gemMoonDocked.Value && wasGemMoonDocked)
            {
                gemMoonUndockStartScale = gemMoonVisualScaleMultiplier;
                gemMoonUndockBlendElapsed = 0f;
                gemMoonUndockBlendActive = true;
                gemMoonDockApproachElapsed = 0f;
            }

            if (gemMoonDocked.Value)
            {
                gemMoonUndockBlendActive = false;
                gemMoonUndockBlendElapsed = 0f;
            }

            if (gemMoonDocked.Value && withinGemMoonBoundary)
                gemMoonDockApproachElapsed += Time.fixedDeltaTime;
            else
                gemMoonDockApproachElapsed = 0f;

            float dockDuration = Mathf.Max(0.05f, gemMoonTransitionDurationSeconds);
            float moonDockEaseInOut = 0f;
            float moonDockLinearT = 0f;
            if (gemMoonDocked.Value && withinGemMoonBoundary)
            {
                moonDockLinearT = Mathf.Clamp01(gemMoonDockApproachElapsed / dockDuration);
                moonDockEaseInOut = GemMoonDockEaseInOut(moonDockLinearT);
            }

            if (gemMoonDocked.Value && withinGemMoonBoundary)
            {
                // Start from *current* pose when the 1s transition begins (avoid snapping to orbit-edge values).
                if (!wasGemMoonDocked || moonDockLinearT <= 0.03f)
                    gemMoonDockApproachStartScaleMultiplier = gemMoonVisualScaleMultiplier;

                gemMoonVisualScaleMultiplier = Mathf.Lerp(
                    gemMoonDockApproachStartScaleMultiplier,
                    gemMoonDockScaleAtSurface,
                    moonDockEaseInOut
                );
            }
            else if (!gemMoonDocked.Value && gemMoonUndockBlendActive)
            {
                gemMoonUndockBlendElapsed += Time.fixedDeltaTime;
                float u = Mathf.Clamp01(gemMoonUndockBlendElapsed / dockDuration);
                float uEase = GemMoonDockEaseInOut(u);
                gemMoonVisualScaleMultiplier = Mathf.Lerp(gemMoonUndockStartScale, 1f, uEase);
                if (u >= 0.999f)
                    gemMoonUndockBlendActive = false;
            }
            else
            {
                gemMoonVisualScaleMultiplier = Mathf.MoveTowards(
                    gemMoonVisualScaleMultiplier,
                    1f,
                    Mathf.Max(0.001f, gemMoonLandingScaleLerpSpeed * Time.fixedDeltaTime)
                );
            }

            // Keep NetworkObject root at base scale; dock shrink is applied with chassis scale on Prefab in LateUpdate.
            transform.localScale = baseLocalScale;

            if (!isDead.Value && gemMoonDocked.Value && moon != null && IsOwner && withinGemMoonBoundary)
            {
                ulong currentPlanetId = gemMoonPlanetNetworkObjectId.Value;

                Vector3 moonPos = moon.transform.position;
                float contactRadius = Mathf.Max(0.0001f, moonDockSurfaceRadius);
                Vector3 moonSpinAxis = moon.SpinAxisWorld.normalized;

                // Cache surface contact offset relative to moon center from initial collision direction.
                if (!wasGemMoonDocked || gemMoonLandingPlanetIdCache != currentPlanetId)
                {
                    Vector3 initialDir = ToroidalMap.ToroidalDirection(moonPos, rb.position);
                    initialDir = Vector3.ProjectOnPlane(initialDir, moonSpinAxis);
                    if (initialDir.sqrMagnitude < 0.0001f)
                    {
                        Vector3 fallback = Vector3.Cross(moonSpinAxis, Vector3.forward);
                        if (fallback.sqrMagnitude < 0.0001f) fallback = Vector3.Cross(moonSpinAxis, Vector3.right);
                        initialDir = fallback;
                    }
                    initialDir.Normalize();
                    gemMoonLandingOffset = initialDir * contactRadius;
                    gemMoonLandingPlanetIdCache = currentPlanetId;
                    gemMoonDockApproachElapsed = 0f;
                    if (visualRoot != null && visualRoot != transform)
                        gemMoonDockVisualStartRotation = visualRoot.rotation;
                }

                // Rotate contact offset with moon axial spin so ship appears static on surface.
                // Ease the moon spin influence during the 1s dock transition to avoid a visible sideways "jump".
                float spinStepDeg = moon.SpinDegreesPerSecond * Time.fixedDeltaTime * moonDockEaseInOut;
                if (Mathf.Abs(spinStepDeg) > 0.0001f)
                    gemMoonLandingOffset = Quaternion.AngleAxis(spinStepDeg, moon.SpinAxisWorld) * gemMoonLandingOffset;
                Vector3 radial = gemMoonLandingOffset;
                if (radial.sqrMagnitude < 0.0001f) radial = Vector3.forward;
                radial = radial.normalized * contactRadius;
                gemMoonLandingOffset = radial;

                // Distance blend outer zone → surface along spin-aligned radial (not toward current ship XZ),
                // otherwise the ship stays at a fixed world azimuth and does not co-rotate with the moon.
                Vector3 orbitDir = radial.sqrMagnitude > 0.0001f ? radial.normalized : Vector3.forward;

                // Start the dock transition from the ship's live world pose so entering the moon zone
                // never teleports to a precomputed radial pose.
                if (!wasGemMoonDocked || moonDockLinearT <= 0.03f)
                    gemMoonDockApproachStartWorldPos = rb.position;

                Vector3 targetSurfacePos = moonPos + orbitDir * contactRadius;
                Vector3 targetPos = Vector3.Lerp(gemMoonDockApproachStartWorldPos, targetSurfacePos, moonDockEaseInOut);
                rb.MovePosition(targetPos);
                SetRootColliderDocked(true);

                // Re-orient visuals: same ease-in-out t as scale & position (refresh start pose at outer ring).
                if (visualRoot != null && visualRoot != transform)
                {
                    if (!wasGemMoonDocked || moonDockLinearT <= 0.03f)
                        gemMoonDockVisualStartRotation = visualRoot.rotation;
                    Vector3 surfaceNormal = radial.normalized;
                    Vector3 tangent = Vector3.Cross(moon.SpinAxisWorld, surfaceNormal);
                    if (tangent.sqrMagnitude < 0.0001f)
                        tangent = Vector3.ProjectOnPlane(transform.forward, surfaceNormal);
                    if (tangent.sqrMagnitude < 0.0001f)
                        tangent = Vector3.forward;
                    tangent.Normalize();
                    Quaternion targetRot = Quaternion.LookRotation(tangent, surfaceNormal);
                    visualRoot.rotation = Quaternion.Slerp(gemMoonDockVisualStartRotation, targetRot, moonDockEaseInOut);
                }

                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                currentVelocity = rb.linearVelocity;
            }
            else if (!gemMoonDocked.Value && wasGemMoonDocked)
            {
                gemMoonLandingOffset = Vector3.zero;
                SetRootColliderDocked(false);
                gemMoonUndockOrbitGraceUntilTime = Time.time + Mathf.Max(0.05f, gemMoonTransitionDurationSeconds);
            }

            wasGemMoonDocked = gemMoonDocked.Value;
            
            // Dead ships cannot move or rotate
            if (isDead.Value)
            {
                SetRootColliderDocked(false);
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

            // Player movement input undocks and restores original parent.
            if (gemMoonDocked.Value && inputHandler != null && inputHandler.MoveForwardPressed)
            {
                SetRootColliderDocked(false);
                RequestUndockGemMoonServerRpc();
            }

            // While docked and within landing range: snap + moon surface orientation run above. Do not steer rb toward mouse
            // (that fought moon alignment and reintroduced banking-style roll via the parent transform).
            if (gemMoonDocked.Value && withinGemMoonBoundary)
                return;

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

            // #region agent log
            if (IsOwner && !_isAIControlled)
            {
                int frame = Time.frameCount;
                if ((frame % 180) == 0)
                {
                    float durMs = (Time.realtimeSinceStartup - startTime) * 1000f;
                    TitanOrbit.Core.DebugSessionLog.Write(
                        "Starship.FixedUpdate",
                        "starship fixedupdate",
                        "{\"durationMs\":" + durMs +
                        ",\"isDead\":" + isDead.Value +
                        ",\"hasOrbitPlanet\":" + (currentOrbitPlanet != null) +
                        "}",
                        "SF");
                }
            }
            // #endregion
        }

        private void AttachToGemMoonParent(PlanetGemMoon moon, Vector3 targetWorldPos)
        {
            if (moon == null) return;
            Transform parentTarget = moon.LandingParentTransform;
            if (parentTarget == null) return;
            Transform reparentTarget = GetPrefabTransform();
            if (reparentTarget == null) return;
            gemMoonVisualUndockBlendActive = false;
            RefreshGemMoonPrefabBaseline();

            // Reparent only visual mesh container (Prefab), never the NetworkObject root.
            // worldPositionStays=false avoids Unity inflating localScale to preserve world size under the moon visual.
            if (!gemMoonVisualAttached)
            {
                gemMoonReparentTarget = reparentTarget;
                gemMoonVisualParentBeforeAttach = reparentTarget.parent;
                reparentTarget.SetParent(parentTarget, false);
                gemMoonReparentTarget.localPosition = parentTarget.InverseTransformPoint(targetWorldPos);
                gemMoonReparentTarget.localScale = Vector3.Scale(
                    gemMoonPrefabBaselineLocalScale,
                    Vector3.one * Mathf.Max(0.001f, gemMoonVisualScaleMultiplier));
                gemMoonVisualDockStartLocalPos = reparentTarget.localPosition;
                gemMoonVisualDockStartLocalRot = reparentTarget.localRotation;
                gemMoonVisualDockBlendElapsed = 0f;
                gemMoonVisualDockBlendActive = true;
                gemMoonVisualAttached = true;
            }
            else if (gemMoonReparentTarget != null && gemMoonReparentTarget.parent != parentTarget)
            {
                gemMoonReparentTarget.SetParent(parentTarget, false);
                gemMoonReparentTarget.localPosition = parentTarget.InverseTransformPoint(targetWorldPos);
                gemMoonReparentTarget.localScale = Vector3.Scale(
                    gemMoonPrefabBaselineLocalScale,
                    Vector3.one * Mathf.Max(0.001f, gemMoonVisualScaleMultiplier));
                gemMoonVisualDockStartLocalPos = gemMoonReparentTarget.localPosition;
                gemMoonVisualDockStartLocalRot = gemMoonReparentTarget.localRotation;
                gemMoonVisualDockBlendElapsed = 0f;
                gemMoonVisualDockBlendActive = true;
            }
        }

        private void DetachFromGemMoonParent()
        {
            if (!gemMoonVisualAttached || gemMoonReparentTarget == null) return;
            Transform restoreParent = gemMoonVisualParentBeforeAttach != null ? gemMoonVisualParentBeforeAttach : visualRoot;
            gemMoonReparentTarget.SetParent(restoreParent, true);

            // Animate back to canonical local pose under BankPivot.
            gemMoonVisualUndockStartLocalPos = gemMoonReparentTarget.localPosition;
            gemMoonVisualUndockStartLocalRot = gemMoonReparentTarget.localRotation;
            gemMoonVisualUndockStartLocalScale = gemMoonReparentTarget.localScale;
            gemMoonVisualUndockBlendElapsed = 0f;
            gemMoonVisualUndockBlendActive = true;

            gemMoonVisualAttached = false;
            gemMoonVisualParentBeforeAttach = null;
            gemMoonVisualDockBlendActive = false;
            gemMoonVisualDockBlendElapsed = 0f;
        }

        private void UpdateDockedVisualBlend(Vector3 moonPos, Vector3 targetPos, PlanetGemMoon moon)
        {
            if (gemMoonReparentTarget == null || moon == null) return;
            Transform parentTarget = gemMoonReparentTarget.parent;
            if (parentTarget == null) return;

            // Moon-local tangent frame so belly stays toward surface while parent spin rotates the ship with the moon.
            Vector3 surfaceNormal = targetPos - moonPos;
            surfaceNormal.y = 0f;
            if (surfaceNormal.sqrMagnitude < 0.0001f) surfaceNormal = Vector3.up;
            surfaceNormal.Normalize();

            Vector3 facing = rb != null ? rb.rotation * Vector3.forward : transform.forward;
            facing.y = 0f;
            facing = Vector3.ProjectOnPlane(facing, surfaceNormal);
            if (facing.sqrMagnitude < 0.0001f)
            {
                Vector3 tangent = Vector3.Cross(moon.SpinAxisWorld, surfaceNormal);
                facing = tangent.sqrMagnitude > 0.0001f ? tangent.normalized : Vector3.forward;
            }
            facing.Normalize();

            Vector3 localN = parentTarget.InverseTransformDirection(surfaceNormal).normalized;
            Vector3 localF = parentTarget.InverseTransformDirection(facing).normalized;
            localF = Vector3.ProjectOnPlane(localF, localN);
            if (localF.sqrMagnitude < 0.0001f)
                localF = parentTarget.InverseTransformDirection(transform.forward);
            localF = Vector3.ProjectOnPlane(localF, localN).normalized;
            if (localF.sqrMagnitude < 0.0001f) localF = Vector3.forward;

            gemMoonVisualDockTargetLocalPos = parentTarget.InverseTransformPoint(targetPos);
            gemMoonVisualDockTargetLocalRot = Quaternion.LookRotation(localF, localN);

            if (!gemMoonVisualDockBlendActive)
            {
                gemMoonReparentTarget.localPosition = gemMoonVisualDockTargetLocalPos;
                gemMoonReparentTarget.localRotation = gemMoonVisualDockTargetLocalRot;
                return;
            }

            gemMoonVisualDockBlendElapsed += Time.fixedDeltaTime;
            float duration = Mathf.Max(0.01f, gemMoonVisualDockBlendSeconds);
            float t = Mathf.Clamp01(gemMoonVisualDockBlendElapsed / duration);
            float smoothT = t * t * (3f - 2f * t);

            gemMoonReparentTarget.localPosition = Vector3.Lerp(gemMoonVisualDockStartLocalPos, gemMoonVisualDockTargetLocalPos, smoothT);
            gemMoonReparentTarget.localRotation = Quaternion.Slerp(gemMoonVisualDockStartLocalRot, gemMoonVisualDockTargetLocalRot, smoothT);

            if (t >= 0.999f)
                gemMoonVisualDockBlendActive = false;
        }

        private void ApplyGemMoonVisualScaleToReparentTarget()
        {
            if (gemMoonReparentTarget == null)
                return;
            if (gemMoonVisualUndockBlendActive) return;
            gemMoonReparentTarget.localScale = Vector3.Scale(
                gemMoonPrefabBaselineLocalScale,
                Vector3.one * Mathf.Max(0.001f, gemMoonVisualScaleMultiplier));
        }

        private void UpdateUndockedVisualBlend()
        {
            if (!gemMoonVisualUndockBlendActive || gemMoonReparentTarget == null) return;

            gemMoonVisualUndockBlendElapsed += Time.fixedDeltaTime;
            float duration = Mathf.Max(0.01f, gemMoonVisualDockBlendSeconds);
            float t = Mathf.Clamp01(gemMoonVisualUndockBlendElapsed / duration);
            float smoothT = t * t * (3f - 2f * t);

            gemMoonReparentTarget.localPosition = Vector3.Lerp(gemMoonVisualUndockStartLocalPos, Vector3.zero, smoothT);
            gemMoonReparentTarget.localRotation = Quaternion.Slerp(gemMoonVisualUndockStartLocalRot, Quaternion.identity, smoothT);
            gemMoonReparentTarget.localScale = Vector3.Lerp(gemMoonVisualUndockStartLocalScale, gemMoonPrefabBaselineLocalScale, smoothT);

            if (t >= 0.999f)
            {
                gemMoonReparentTarget.localPosition = Vector3.zero;
                gemMoonReparentTarget.localRotation = Quaternion.identity;
                gemMoonReparentTarget.localScale = gemMoonPrefabBaselineLocalScale;
                gemMoonVisualUndockBlendActive = false;
                gemMoonReparentTarget = null;
            }
        }

        private void SetRootColliderDocked(bool docked)
        {
            if (rootCollider == null) rootCollider = GetComponent<Collider>();
            if (rootCollider == null) return;

            if (docked)
            {
                if (!rootColliderDockOverrideActive)
                {
                    rootColliderEnabledBeforeDock = rootCollider.enabled;
                    rootColliderDockOverrideActive = true;
                }
                rootCollider.enabled = false;
            }
            else if (rootColliderDockOverrideActive)
            {
                rootCollider.enabled = rootColliderEnabledBeforeDock;
                rootColliderDockOverrideActive = false;
            }
        }

        /// <summary>Server: pull nearby free gems toward this ship so ships, not gems, drive attraction.</summary>
        private void TickNearbyGemAttraction()
        {
            if (!IsServer) return;
            if (isDead.Value) return;
            if (currentGems.Value >= GemCapacity) return;
            if (gemMoonDocked.Value) return;

            // Throttle attraction work across frames to reduce CPU cost.
            if (((Time.frameCount + GetInstanceID()) & 1) != 0)
                return;

            if (TitanOrbit.Entities.Gem.AllGems == null || TitanOrbit.Entities.Gem.AllGems.Count == 0)
                return;

            Vector3 shipPos = rb != null ? rb.position : transform.position;
            bool inOrbitZone = currentOrbitPlanet != null;
            float searchRadius = inOrbitZone ? 9f : 5f;
            float attractionSpeed = inOrbitZone ? 14f : 8f;

            foreach (var gem in TitanOrbit.Entities.Gem.AllGems)
            {
                if (gem == null || !gem.IsSpawned || gem.IsInPool || gem.IsDepositGem) continue;
                if (gem.Value <= 0f) continue;

                Rigidbody gemRb = gem.GetComponent<Rigidbody>();
                if (gemRb == null) continue;

                Vector3 gemPos = gemRb.position;
                float dist = TitanOrbit.Generation.ToroidalMap.ToroidalDistance(gemPos, shipPos);
                if (dist > searchRadius) continue;

                // Respect expelled cooldown: victim ship cannot collect their own expelled gems immediately.
                // This is enforced on collision as well; here we just avoid pulling them in.
                // (Gem handles the exact cooldown window during collection.)

                Vector3 toShip = TitanOrbit.Generation.ToroidalMap.ToroidalDirection(gemPos, shipPos);
                toShip.y = 0f;
                if (toShip.sqrMagnitude < 0.0001f) continue;
                toShip.Normalize();

                Vector3 targetVel = toShip * attractionSpeed;
                gemRb.linearVelocity = Vector3.MoveTowards(
                    gemRb.linearVelocity,
                    targetVel,
                    attractionSpeed * Time.fixedDeltaTime * 4f
                );
                gemRb.linearDamping = 0f;
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

            // Shooting: only from Weapon components (bulletFirePoints). No firePoint fallback.
            if (inputHandler.ShootPressed && CanFire() && bulletFirePoints != null && bulletFirePoints.Count > 0 && !IsPointerOverUI())
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

            // B key: cycle bullet prefab through CombatSystem's Bullet Prefab Bank (via PlayerInputHandler or new Input System)
            bool cycleBulletPressed = (inputHandler is TitanOrbit.Input.PlayerInputHandler pih && pih.CycleBulletPressed)
                || (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.bKey.wasPressedThisFrame);
            if (IsOwner && !IsPointerOverUI() && !isDead.Value &&
                Systems.CombatSystem.Instance != null && Systems.CombatSystem.Instance.BulletPrefabBankCount >= 1 &&
                cycleBulletPressed)
            {
                // Local preview of which bullet we are switching to so we can show floating text immediately.
                int count = Systems.CombatSystem.Instance.BulletPrefabBankCount;
                int current = runtimeBulletPrefabIndex.Value;
                int next = current < 0 ? 0 : (current + 1) % count;
                ShowBulletNameLocal(next);

                // Tell server to actually apply the change and sync runtimeBulletPrefabIndex.
                CycleBulletPrefabServerRpc();
            }
        }

        /// <summary>True only when the pointer is over a UI element (Canvas/Graphic). Ignores 3D colliders so clicking the ship or world doesn't block shooting.</summary>
        private static bool IsPointerOverUI()
        {
            if (EventSystem.current == null) return false;
            Vector2 pointerPosition = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
            var eventData = new PointerEventData(EventSystem.current) { position = pointerPosition };
            if (s_raycastResults == null) s_raycastResults = new List<RaycastResult>();
            s_raycastResults.Clear();
            EventSystem.current.RaycastAll(eventData, s_raycastResults);
            foreach (var r in s_raycastResults)
            {
                if (r.gameObject != null && r.module is GraphicRaycaster)
                    return true;
            }
            return false;
        }

        private static List<RaycastResult> s_raycastResults;

        private void HandleMovement()
        {
            // Sync from rigidbody so recoil (AddForce) is included in our velocity
            currentVelocity = rb.linearVelocity;
            currentVelocity.y = 0f;

            float mass = Mathf.Max(0.5f, rb.mass);
            float maxSpeed = EffectiveMaxSpeed;

            if (moveDirection.magnitude > 0.1f)
            {
                float speed = currentVelocity.magnitude;
                if (speed < maxSpeed)
                {
                    rb.AddForce(moveDirection * EffectiveEngineThrust, ForceMode.Force);
                }
                else
                {
                    // At max speed: apply steering force only (perpendicular to velocity) so we turn without overspeeding. Keeps physics intact.
                    Vector3 velNorm = currentVelocity.normalized;
                    Vector3 thrustVec = moveDirection * EffectiveEngineThrust;
                    float alongVel = Vector3.Dot(thrustVec, velNorm);
                    Vector3 steerForce = thrustVec - velNorm * alongVel; // Remove forward component; only steer
                    rb.AddForce(steerForce, ForceMode.Force);
                }
            }
            else
            {
                // Braking when not thrusting (respects SpaceBrakes toggle)
                bool brakesOn = (inputHandler as TitanOrbit.Input.PlayerInputHandler)?.SpaceBrakesEnabled ?? true;
                if (brakesOn && currentVelocity.sqrMagnitude > 0.001f)
                {
                    float brakeForce = brakeDeceleration * mass;
                    rb.AddForce(-currentVelocity.normalized * brakeForce, ForceMode.Force);
                }
            }

            // Ensure velocity has no Y component
            Vector3 vel = rb.linearVelocity;
            if (Mathf.Abs(vel.y) > 0.01f)
            {
                vel.y = 0f;
                rb.linearVelocity = vel;
            }

            // Recoil decay: if over max speed (e.g. from shooting), decay back toward max
            float mag = rb.linearVelocity.magnitude;
            if (mag > maxSpeed && maxSpeed > 0.001f)
            {
                float effectiveRecoilDecay = recoilDecayPerSecond / mass;
                float targetMag = Mathf.MoveTowards(mag, maxSpeed, effectiveRecoilDecay * Time.fixedDeltaTime);
                vel = rb.linearVelocity;
                vel.y = 0f;
                rb.linearVelocity = vel.normalized * targetMag;
            }

            currentVelocity = rb.linearVelocity;
        }

        private void HandleOrbitMovement()
        {
            if (currentOrbitPlanet == null || rb == null) return;

            Vector3 planetPos = currentOrbitPlanet.transform.position;
            Vector3 toShip = rb.position - planetPos;
            toShip.y = 0f;
            float dist = toShip.magnitude;
            if (dist < 0.01f) return;

            // Orbit zone: inner 0.5 to outer (local). Ship keeps whatever radius it entered.
            float innerWorld = currentOrbitPlanet.PlanetSize * 0.5f;
            float outerWorld = currentOrbitPlanet.PlanetSize * currentOrbitPlanet.GetOrbitZoneOuterRadiusLocal();
            Vector3 radial = toShip / dist;

            float targetSpeed = GetOrbitTargetSpeed(currentOrbitPlanet, dist, innerWorld, outerWorld);
            Vector3 tangent = new Vector3(radial.z, 0f, -radial.x);

            float graceRemaining = gemMoonUndockOrbitGraceUntilTime - Time.time;
            bool inUndockGrace = !gemMoonDocked.Value && graceRemaining > 0f;

            // While leaving the gem moon, the ship sits outside the planet orbit band; inward radial pull reads as a snap toward the ring.
            Vector3 radialCorrection = Vector3.zero;
            if (!inUndockGrace)
            {
                if (dist < innerWorld)
                    radialCorrection += radial * orbitRadiusPullStrength;
                else if (dist > outerWorld)
                    radialCorrection -= radial * orbitRadiusPullStrength;
            }

            Vector3 orbitTangentVelocity = tangent * targetSpeed + radialCorrection;

            // Do not stack full orbit speed + extra outward (felt like a huge launch). Blend from radial exit off the moon into orbit tangent.
            Vector3 desiredOrbitVelocity;
            float transitionDur = Mathf.Max(0.05f, gemMoonTransitionDurationSeconds);
            if (inUndockGrace && transitionDur > 0.001f)
            {
                float w = Mathf.Clamp01(graceRemaining / transitionDur); // 1 = start of grace, 0 = end
                Vector3 flat = rb.position - gemMoonUndockCachedMoonPos;
                flat.y = 0f;
                Vector3 outwardDir = flat.sqrMagnitude > 0.0001f ? flat.normalized : tangent;
                Vector3 outwardVel = outwardDir * (gemMoonUndockOutwardSpeed * w);
                float handoff = 1f - w;
                desiredOrbitVelocity = Vector3.Lerp(outwardVel, orbitTangentVelocity, Mathf.SmoothStep(0f, 1f, handoff));
            }
            else
                desiredOrbitVelocity = orbitTangentVelocity;

            Vector3 currentVel = rb.linearVelocity;
            currentVel.y = 0f;

            float mass = Mathf.Max(0.5f, rb.mass);
            float gravityFactor = GetOrbitGravityFactor(currentOrbitPlanet, dist, innerWorld, outerWorld);
            float massFactor = Mathf.Sqrt(mass);
            float alignRate = (orbitCaptureResponsiveness * gravityFactor) / massFactor;
            if (inUndockGrace && transitionDur > 0.001f)
            {
                float fade = Mathf.Clamp01(graceRemaining / transitionDur);
                float ease = Mathf.Lerp(gemMoonUndockOrbitCaptureEase, 1f, 1f - fade);
                alignRate *= ease;
            }
            float t = Mathf.Clamp01(alignRate * Time.fixedDeltaTime);

            Vector3 blendedVelocity = Vector3.Lerp(currentVel, desiredOrbitVelocity, t);
            blendedVelocity.y = 0f;

            currentVelocity = blendedVelocity;
            rb.linearVelocity = blendedVelocity;
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

            // Normalize planet size using the same rough range regular planets use (9–18), but works for home planets too.
            const float minSize = 9f;
            const float maxSize = 18f;
            float sizeNorm = Mathf.Clamp01((planet.PlanetSize - minSize) / (maxSize - minSize));

            // Bigger planets and tighter orbits move noticeably faster.
            float sizeMultiplier = Mathf.Lerp(0.8f, 1.4f, sizeNorm);     // Small → big planet
            float radiusMultiplier = Mathf.Lerp(0.7f, 1.6f, radiusFactor); // Outer → inner orbit

            return orbitSpeed * sizeMultiplier * radiusMultiplier * FriendlyTerritoryMovementMultiplier;
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

            const float minSize = 9f;
            const float maxSize = 18f;
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
            float outerWorld = currentOrbitPlanet.PlanetSize * currentOrbitPlanet.GetOrbitZoneOuterRadiusLocal();
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
            // EffectiveRotationSpeed is °/s (family definition units are converted there via ShipTurnDefinitionToDegreesPerSecond).
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
            if (currentOrbitPlanet != null) return false; // Cannot fire while in orbit zone
            if (gemMoonDocked.Value) return false;
            EnsureBulletLastFireTime();
            var bulletWc = bulletConfig ?? EffectiveWeaponConfig;
            if (bulletWc.cannons != null)
            {
                for (int i = 0; i < bulletWc.cannons.Count; i++)
                {
                    var c = bulletWc.cannons[i];
                    float effectiveFireRate = c.fireRate * (1f + attrFireRate.Value * ATTR_MULTIPLIER_PER_LEVEL);
                    if (currentEnergy.Value >= c.energyCostPerShot &&
                        (i >= bulletLastFireTime.Length || Time.time - bulletLastFireTime[i] >= 1f / effectiveFireRate))
                        return true;
                }
            }
            return false;
        }

        [ServerRpc]
        private void CycleBulletPrefabServerRpc()
        {
            if (CombatSystem.Instance == null) return;
            int count = CombatSystem.Instance.BulletPrefabBankCount;
            if (count < 1) return;
            int current = runtimeBulletPrefabIndex.Value;
            int next = current < 0 ? 0 : (current + 1) % count;
            runtimeBulletPrefabIndex.Value = next;
        }

        /// <summary>Owner-only: spawns floating text above this ship showing the current bullet name/category.</summary>
        private void ShowBulletNameLocal(int bankIndex)
        {
            if (!IsOwner) return;
            if (bulletNameTextPrefab == null) return;
            if (Systems.CombatSystem.Instance == null) return;

            string name = Systems.CombatSystem.Instance.GetBulletDisplayName(bankIndex);
            if (string.IsNullOrEmpty(name)) return;

            // Spawn a bit above the ship so it's not occluded by the hull in top-down view.
            Vector3 pos = transform.position + Vector3.up * 5f;
            GameObject go = Instantiate(bulletNameTextPrefab, pos, Quaternion.identity);
            var ft = go.GetComponent<TitanOrbit.Systems.SimpleFloatingText>();
            if (ft != null)
            {
                // White text, ~2 seconds duration
                ft.Initialize(name, Color.white, 2f);
            }
        }

        [ServerRpc]
        private void FireServerRpc(Vector3 shipPosition, Vector3 shipForward)
        {
            if (CombatSystem.Instance == null) return;
            if (currentOrbitPlanet != null) return; // Cannot fire while in orbit zone
            if (gemMoonDocked.Value) return;
            EnsureBulletLastFireTime();
            Vector3 shipVel = rb != null ? rb.linearVelocity : Vector3.zero;
            shipVel.y = 0f;

            var bulletIndicesFired = new System.Collections.Generic.List<byte>();
            var bulletPrefabIndicesFired = new System.Collections.Generic.List<int>();

            // Fire bullets (Weapon only): only from actual weapon components (bulletFirePoints). No fallback to ship center — avoids phantom bullet with no muzzle.
            var bulletWc = bulletConfig ?? EffectiveWeaponConfig;
            int maxCannons = (bulletFirePoints != null && bulletFirePoints.Count > 0) ? bulletFirePoints.Count : 0;
            if (bulletWc.cannons != null && maxCannons > 0)
            {
                for (int i = 0; i < bulletWc.cannons.Count && i < maxCannons; i++)
                {
                    var c = bulletWc.cannons[i];
                    // Only fire from actual weapon positions; skip if no fire point (no phantom from ship center).
                    if (bulletFirePoints == null || i >= bulletFirePoints.Count || bulletFirePoints[i] == null)
                        continue;
                    if (currentEnergy.Value < c.energyCostPerShot) continue;
                    float effectiveFireRate = c.fireRate * (1f + attrFireRate.Value * ATTR_MULTIPLIER_PER_LEVEL);
                    if (i >= bulletLastFireTime.Length || Time.time - bulletLastFireTime[i] < 1f / effectiveFireRate) continue;

                    currentEnergy.Value = Mathf.Max(0f, currentEnergy.Value - c.energyCostPerShot);
                    bulletLastFireTime[i] = Time.time;
                    bulletIndicesFired.Add((byte)i);

                    int bankCount = CombatSystem.Instance != null ? CombatSystem.Instance.BulletPrefabBankCount : 0;
                    // Prefer cycled runtime index (B key) when valid so toggling bullets always takes effect; else per-cannon, else family default.
                    int bulletIdx = (runtimeBulletPrefabIndex.Value >= 0 && bankCount > 0)
                        ? (runtimeBulletPrefabIndex.Value % bankCount)
                        : (c.bulletPrefabIndex >= 0 && bankCount > 0 && c.bulletPrefabIndex < bankCount)
                            ? c.bulletPrefabIndex
                            : (bulletPrefabBankIndex >= 0 && bulletPrefabBankIndex < bankCount ? bulletPrefabBankIndex : 0);
                    bulletPrefabIndicesFired.Add(bulletIdx);

                    Transform firePt = bulletFirePoints[i];
                    Vector3 fireOrigin = firePt.position;

                    // Horizontal aim basis from this weapon's facing (not ship forward) so side mounts / turrets shoot correctly.
                    Vector3 cannonFwd = firePt.forward;
                    cannonFwd.y = 0f;
                    if (cannonFwd.sqrMagnitude < 0.01f)
                    {
                        cannonFwd = shipForward;
                        cannonFwd.y = 0f;
                    }
                    if (cannonFwd.sqrMagnitude < 0.01f) cannonFwd = Vector3.forward;
                    cannonFwd.Normalize();
                    Vector3 cannonRight = Vector3.Cross(Vector3.up, cannonFwd);

                    float baseDirAngle = c.directionAngle * Mathf.Deg2Rad;
                    Vector3 baseDir = (cannonFwd * Mathf.Cos(baseDirAngle) + cannonRight * Mathf.Sin(baseDirAngle)).normalized;
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
                            dir = (baseDir * Mathf.Cos(spread) + cannonRight * Mathf.Sin(spread)).normalized;
                        }
                        else if (c.spreadType == CannonSpreadType.FixedSpread && numShots > 1)
                        {
                            float t = numShots == 1 ? 0.5f : (float)s / (numShots - 1);
                            float spread = Mathf.Lerp(angleMin, angleMax, t) * Mathf.Deg2Rad;
                            dir = (baseDir * Mathf.Cos(spread) + cannonRight * Mathf.Sin(spread)).normalized;
                        }
                        float damage = c.damagePerBullet;
                        float speed = c.bulletSpeed;
                        float scale = c.bulletScale * (0.65f + damage / 50f) * BulletScaleMultiplier;
                        CombatSystem.Instance.SpawnBulletServerRpc(fireOrigin, dir, speed, damage, shipTeam.Value, NetworkObjectId, scale, 0, shipVel, bulletIdx);
                        if (rb != null)
                        {
                            float recoilImpulse = recoilStrength * scale * (0.08f + damage / 400f);
                            rb.AddForce(-dir * recoilImpulse, ForceMode.Impulse);
                        }
                    }
                }
            }

            FireClientRpc(
                bulletIndicesFired.Count > 0 ? bulletIndicesFired.ToArray() : System.Array.Empty<byte>(),
                bulletPrefabIndicesFired.Count > 0 ? bulletPrefabIndicesFired.ToArray() : System.Array.Empty<int>());
        }

        [ClientRpc]
        private void FireClientRpc(byte[] bulletIndicesFired, int[] bulletPrefabIndices)
        {
            if (bulletIndicesFired != null)
            {
                for (int j = 0; j < bulletIndicesFired.Length; j++)
                {
                    int idx = bulletIndicesFired[j];
                    bool usedSciFiMuzzle = false;
                    if (bulletPrefabIndices != null && j < bulletPrefabIndices.Length && CombatSystem.Instance != null)
                    {
                        GameObject bulletPrefab = CombatSystem.Instance.GetBulletPrefabFromBank(bulletPrefabIndices[j], shipTeam.Value);
                        var sciFi = bulletPrefab != null ? bulletPrefab.GetComponent<SciFiProjectileScript>() : null;
                        if (sciFi != null && sciFi.muzzleParticle != null && bulletFirePoints != null && idx >= 0 && idx < bulletFirePoints.Count && bulletFirePoints[idx] != null)
                        {
                            Transform pt = bulletFirePoints[idx];
                            Vector3 pos = pt.position;
                            Vector3 fwd = pt.forward;
                            if (fwd.sqrMagnitude < 0.01f) fwd = -transform.forward;
                            GameObject muzzle = Instantiate(sciFi.muzzleParticle, pos, Quaternion.LookRotation(-fwd));
                            if (muzzle != null)
                            {
                                // Pitch any muzzle audio contained in the particle prefab.
                                SetAudioPitchInHierarchy(muzzle, GetWeaponSoundPitchForCannon(idx));
                                Destroy(muzzle, 1.5f);
                                usedSciFiMuzzle = true;
                            }
                        }
                    }
                    if (!usedSciFiMuzzle && bulletMuzzleParticleSystems != null && idx >= 0 && idx < bulletMuzzleParticleSystems.Count && bulletMuzzleParticleSystems[idx] != null)
                    {
                        // Also pitch any audio embedded in the muzzle particle system hierarchy (fallback path).
                        SetAudioPitchInHierarchy(bulletMuzzleParticleSystems[idx].gameObject, GetWeaponSoundPitchForCannon(idx));
                        bulletMuzzleParticleSystems[idx].Play();
                    }
                }
            }
            if (bulletIndicesFired != null && bulletIndicesFired.Length > 0 && AudioManager.Instance != null)
            {
                for (int j = 0; j < bulletIndicesFired.Length; j++)
                {
                    int idx = bulletIndicesFired[j];
                    float pitch = GetWeaponSoundPitchForCannon(idx);
                    AudioManager.Instance.PlayWeaponShootSound(pitch);
                }
            }
        }

        /// <summary>Pitch for weapon sound based on fire power (attrFirePower): stronger fire power = lower pitch.</summary>
        private float GetWeaponSoundPitchForCannon(int cannonIndex)
        {
            // Stronger fire power = lower pitch.
            // attrFirePower ranges [0..ShipLevel], so normalize to that directly.
            float maxUpgrades = Mathf.Max(1f, ShipLevel);
            float upgrades = Mathf.Clamp(attrFirePower.Value, 0, (int)maxUpgrades);
            float normalized = Mathf.Clamp01(upgrades / maxUpgrades);

            // Make differences more apparent across early upgrades too.
            float emphasized = Mathf.Pow(normalized, 0.75f);

            const float highPitch = 2.6f;
            const float lowPitch = 0.3f;
            return Mathf.Lerp(highPitch, lowPitch, emphasized);
        }

        private static void SetAudioPitchInHierarchy(GameObject root, float pitch)
        {
            if (root == null) return;
            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            if (sources == null || sources.Length == 0) return;
            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource src = sources[i];
                if (src == null) continue;
                src.pitch = pitch;
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
            if (currentOrbitPlanet != null || gemMoonDocked.Value) return;
            bool useLarge = preferLarge && ConsumeLargeRocket();
            if (!useLarge && !ConsumeSmallRocket()) return;
            Vector3 dir = transform.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
            else dir.Normalize();
            Vector3 spawnPos = firePoint != null ? firePoint.position : transform.position + dir * 2f;
            if (CombatSystem.Instance != null)
                CombatSystem.Instance.SpawnRocketServerRpc(spawnPos, dir, useLarge, shipTeam.Value, NetworkObjectId);
        }

        [ServerRpc]
        private void PlaceMineServerRpc(Vector3 position, bool preferLarge)
        {
            // Dead ships cannot place mines
            if (isDead.Value) return;
            if (currentOrbitPlanet != null || gemMoonDocked.Value) return;
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
            if (gemMoonDocked.Value) return;

            // Gem expulsion tuning: how quickly gems are lost once health hits 0.
            // Lower values = slower gem loss; higher values = faster loss.
            // Rough target: about 50% of damage value comes out as gems, with caps so a single hit doesn't dump everything.
            const float GemExpulsionPerDamage = 0.5f;              // gems expelled per 1 damage
            const float MaxLethalExpulsionFraction = 0.6f;         // at most 60% of current gems on the lethal hit
            const float MaxPostDeathExpulsionFraction = 0.4f;      // at most 40% of current gems per hit after death

            float healthBefore = currentHealth.Value;
            bool wasAlive = healthBefore > 0f;

            if (wasAlive)
            {
                // Phase 1: Reduce health until it reaches zero
                float newHealth = Mathf.Max(0f, healthBefore - damage);
                float deltaHealth = newHealth - healthBefore;
                currentHealth.Value = newHealth;

                // Feedback: show health delta as a floating popup at the ship position.
                // (Health changes only during the alive->alive phase, not after health is already 0.)
                const float minAbsHealthForPopup = 1f;
                if (Mathf.Abs(deltaHealth) >= minAbsHealthForPopup && VisualEffectsManager.Instance != null)
                    VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                        transform.position,
                        (int)FloatingCountChannel.HealthChange,
                        deltaHealth,
                        (int)attackerTeam
                    );

                // Any excess damage beyond what was needed to reach 0 is converted into gem expulsion (scaled and capped).
                float excessDamage = Mathf.Max(0f, damage - healthBefore);
                if (excessDamage > 0f && currentGems.Value > 0f)
                {
                    float desired = excessDamage * GemExpulsionPerDamage;
                    float maxForThisHit = currentGems.Value * MaxLethalExpulsionFraction;
                    float gemsToExpel = Mathf.Min(desired, maxForThisHit);
                    if (gemsToExpel > 0f)
                    {
                        currentGems.Value = Mathf.Max(0f, currentGems.Value - gemsToExpel);
                        if (GemSpawner.Instance != null)
                        {
                            ulong myId = GetComponent<NetworkObject>()?.NetworkObjectId ?? 0;
                            GemSpawner.Instance.SpawnGemsFromShipServerRpc(transform.position, gemsToExpel, myId);
                        }
                    }
                }
            }
            else
            {
                // Phase 2: Health is already zero - incoming damage drains gems and expels them, but at a throttled rate.
                float desired = damage * GemExpulsionPerDamage;
                float maxForThisHit = currentGems.Value * MaxPostDeathExpulsionFraction;
                float gemsToExpel = Mathf.Min(desired, maxForThisHit);
                if (gemsToExpel > 0f)
                {
                    currentGems.Value = Mathf.Max(0f, currentGems.Value - gemsToExpel);
                    if (GemSpawner.Instance != null)
                    {
                        ulong myId = GetComponent<NetworkObject>()?.NetworkObjectId ?? 0;
                        GemSpawner.Instance.SpawnGemsFromShipServerRpc(transform.position, gemsToExpel, myId);
                    }
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

        /// <summary>Server: auto load people from planets we own, auto unload onto neutral or enemy planets. People beam up/down as projectiles.</summary>
        private void TickOrbitPopulationTransfer()
        {
            if (currentOrbitPlanet == null)
            {
                peopleLoadAccumulator = 0f;
                peopleUnloadAccumulator = 0f;
                return;
            }
            float peopleSpaceAvailable = PeopleCapacity - currentPeople.Value - peopleInTransit;
            bool debugModeEnabled = GameManager.Instance != null && GameManager.Instance.DebugMode;

            float rate = shipLevel * Time.fixedDeltaTime; // e.g. level 1 = 1 per second
            if (debugModeEnabled) rate *= 100f;
            if (rate <= 0f) return;

            bool friendly = (currentOrbitPlanet is HomePlanet home && home.AssignedTeam == shipTeam.Value)
                || currentOrbitPlanet.TeamOwnership == shipTeam.Value;

            float now = (float)NetworkManager.Singleton.ServerTime.Time;
            float peopleInterval = shipLevel > 0 ? 1f / shipLevel : 1f; // 1 person per spawn, shipLevel per second
            bool shouldSpawnPeople = (now - lastPeopleSpawnTime) >= peopleInterval;

            if (friendly)
            {
                float available = currentOrbitPlanet.CurrentPopulation;
                if (debugModeEnabled)
                {
                    float instantLoadAmount = Mathf.Min(peopleSpaceAvailable, available);
                    if (instantLoadAmount > 0f)
                    {
                        currentOrbitPlanet.RemovePopulationServerRpc(instantLoadAmount);
                        AddPeopleServerRpc(instantLoadAmount);
                        PlayPeopleLoadSoundClientRpc(instantLoadAmount);
                    }
                    return;
                }

                float amount = Mathf.Min(rate, peopleSpaceAvailable, available);
                if (amount > 0f) peopleLoadAccumulator += amount;

                if (shouldSpawnPeople && peopleLoadAccumulator >= 1f && peopleSpaceAvailable >= 1f && available >= 1f && GemSpawner.Instance != null)
                {
                    currentOrbitPlanet.RemovePopulationServerRpc(1f);
                    peopleLoadAccumulator -= 1f;
                    peopleInTransit += 1f;
                    lastPeopleSpawnTime = now;

                    Vector3 planetPos = currentOrbitPlanet.transform.position;
                    Vector3 shipPos = rb != null ? rb.position : transform.position;
                    var shipNo = GetComponent<NetworkObject>();
                    if (shipNo != null)
                        GemSpawner.Instance.SpawnPeopleLoad(planetPos, shipPos, 1f, shipNo.NetworkObjectId, shipTeam.Value);

                }
            }
            else
            {
                if (debugModeEnabled)
                {
                    float instantUnloadPeople = currentPeople.Value;
                    if (instantUnloadPeople > 0f)
                    {
                        RemovePeopleServerRpc(instantUnloadPeople);
                        // Debug-only shortcut: each 1 unloaded person applies 100 population impact.
                        currentOrbitPlanet.AddPopulationServerRpc(instantUnloadPeople * 100f, shipTeam.Value);
                        PlayPeopleUnloadSoundClientRpc(instantUnloadPeople);
                    }
                    return;
                }

                float amount = Mathf.Min(rate, currentPeople.Value);
                if (amount > 0f) peopleUnloadAccumulator += amount;

                if (shouldSpawnPeople && peopleUnloadAccumulator >= 1f && GemSpawner.Instance != null)
                {
                    RemovePeopleServerRpc(1f);
                    peopleUnloadAccumulator -= 1f;
                    lastPeopleSpawnTime = now;

                    Vector3 shipPos = rb != null ? rb.position : transform.position;
                    Vector3 planetPos = currentOrbitPlanet.transform.position;
                    var planetNo = currentOrbitPlanet.GetComponent<NetworkObject>();
                    var shipNo = GetComponent<NetworkObject>();
                    if (planetNo != null && shipNo != null)
                        GemSpawner.Instance.SpawnPeopleUnload(shipPos, planetPos, 1f, planetNo.NetworkObjectId, shipTeam.Value, shipNo.NetworkObjectId);

                    if (VisualEffectsManager.Instance != null)
                        VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                            planetPos,
                            (int)FloatingCountChannel.PeopleUnload,
                            1f,
                            (int)shipTeam.Value);
                    PlayPeopleUnloadSoundClientRpc(1f);
                }
            }
        }

        /// <summary>Server: credits gems straight to the planet (same as old flying deposit gems). No gem projectiles.</summary>
        private void ApplyMoonGemDepositToPlanet(Planet depositPlanet, float amount)
        {
            if (!IsServer || depositPlanet == null || amount <= 0.0001f) return;
            var team = shipTeam.Value;
            ulong clientId = OwnerClientId;
            Vector3 depositPopupPos = depositPlanet.GetGemMoonWorldPosition();
            depositPopupPos.y = 0f;

            if (depositPlanet is HomePlanet home)
                home.DepositGemsFromServer(amount, team, clientId, depositPopupPos);
            else
            {
                depositPlanet.DepositGemsFromServer(amount, team, clientId, depositPopupPos);
                HomePlanet shipHome = GetHomePlanetForTeam(team);
                if (shipHome != null)
                    shipHome.AddContributedGemsFromServer(clientId, amount);
            }

            if (ScoreSystem.Instance != null)
                ScoreSystem.Instance.AwardDeposit(this, amount);
        }

        /// <summary>Server: while docked at gem moon, 1 gem/sec of value shipLevel×5; applied directly to planet level gems.</summary>
        private void TickOrbitGemDeposit()
        {
            if (!gemMoonDocked.Value)
            {
                depositAccumulator = 0f;
                return;
            }

            Planet depositPlanet = ResolveGemMoonDockPlanet();
            if (depositPlanet == null)
            {
                depositAccumulator = 0f;
                return;
            }
            
            bool canDeposit = false;
            if (depositPlanet is HomePlanet home)
                canDeposit = home.AssignedTeam == shipTeam.Value;
            else
                canDeposit = depositPlanet.TeamOwnership == shipTeam.Value;
            
            if (!canDeposit)
            {
                depositAccumulator = 0f;
                return;
            }
            if (currentGems.Value <= 0f) return;

            // Track that we had gems to deposit during this orbit session (server only).
            hadGemsWhileInOrbitThisOrbit = true;

            bool debugModeEnabled = GameManager.Instance != null && GameManager.Instance.DebugMode;
            if (debugModeEnabled)
            {
                float instantDepositAmount = currentGems.Value;
                RemoveGemsServerRpc(instantDepositAmount);
                depositAccumulator = 0f;
                ApplyMoonGemDepositToPlanet(depositPlanet, instantDepositAmount);
                depositedAnyGemsThisOrbit = true;
            }
            else
            {
            float gemValue = ShipLevel * 5f; // e.g. level 3 = 15 value per gem
            float rate = gemValue * Time.fixedDeltaTime; // 1 gem per second
            if (debugModeEnabled) rate *= 100f;
            if (rate <= 0f) return;
            float amount = Mathf.Min(rate, currentGems.Value);
            if (amount <= 0f) return;

            depositAccumulator += amount;
            float now = (float)NetworkManager.Singleton.ServerTime.Time;
            const float gemInterval = 1f; // always 1 gem per second
            bool shouldDepositChunk = depositAccumulator >= gemValue && currentGems.Value >= gemValue && (now - lastDepositSpawnTime) >= gemInterval;
            if (shouldDepositChunk)
            {
                RemoveGemsServerRpc(gemValue);
                depositAccumulator -= gemValue;
                lastDepositSpawnTime = now;

                ApplyMoonGemDepositToPlanet(depositPlanet, gemValue);
                depositedAnyGemsThisOrbit = true;
            }

            // Remainder: when gems are below one full "gem value", credit remaining directly so the ship empties completely.
            if (!shouldDepositChunk && currentGems.Value > 0f && currentGems.Value < gemValue)
            {
                float remainder = currentGems.Value;
                RemoveGemsServerRpc(remainder);
                depositAccumulator = 0f;
                ApplyMoonGemDepositToPlanet(depositPlanet, remainder);
                depositedAnyGemsThisOrbit = true;
            }
            }

            // When all carried gems have been fully deposited during this orbit session, trigger galactic zoom on the owning client.
            if (!triggeredGalacticZoomThisOrbit && depositedAnyGemsThisOrbit && currentGems.Value <= 0.0001f)
            {
                triggeredGalacticZoomThisOrbit = true;

                var sendParams = new ClientRpcParams
                {
                    Send = new ClientRpcSendParams
                    {
                        TargetClientIds = new ulong[] { OwnerClientId }
                    }
                };
                TriggerGalacticZoomClientRpc(sendParams);
            }
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
            peopleInTransit = 0f;
            gemMoonDocked.Value = false;
            gemMoonPlanetNetworkObjectId.Value = 0ul;

            if (!_isAIControlled)
                ClearRammingShakeDriveClientRpc(OwnerOnlyClientRpcParams);

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
                    foreach (var p in Planet.AllPlanets)
                    {
                        if (p == null) continue;
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
                foreach (var hp in HomePlanet.AllHomePlanets)
                {
                    if (hp == null) continue;
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
            float outerWorld = planet.PlanetSize * planet.GetOrbitZoneOuterRadiusLocal();
            float targetSpeed = GetOrbitTargetSpeed(planet, orbitRadius, innerWorld, outerWorld);

            rb.linearVelocity = new Vector3(0f, 0f, -targetSpeed);
            currentVelocity = rb.linearVelocity;
        }

        private static HomePlanet GetHomePlanetForTeam(TeamManager.Team team)
        {
            if (team == TeamManager.Team.None) return null;
            foreach (var hp in HomePlanet.AllHomePlanets)
            {
                if (hp != null && hp.AssignedTeam == team) return hp;
            }
            return null;
        }

        /// <summary>Server: respawn ship at home planet (legacy fallback; prefer RespawnAtOriginOrHomePlanet).</summary>
        private void RespawnAtHomePlanet()
        {
            if (shipTeam.Value == TeamManager.Team.None || rb == null) return;
            HomePlanet home = GetHomePlanetForTeam(shipTeam.Value);
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

        /// <summary>Instantaneous self hull DPS while pushing into an asteroid at this inward speed.</summary>
        private float ComputeSelfDamagePerSecondFromAsteroidRam(float pushAmount)
        {
            float mass = Mathf.Max(0.5f, rb.mass);
            float effectiveMax = Mathf.Max(0.1f, EffectiveMaxSpeed);
            float baselineMomentum = Mathf.Max(0.01f, baseMass * effectiveMax);
            float momentum01 = (mass * pushAmount) / baselineMomentum;
            float momentumInfluence = Mathf.Sqrt(Mathf.Clamp(momentum01, 0f, 4f));
            float momentumMultiplier = 1f + rammingMomentumDamageScale * momentumInfluence;
            float firePowerMultiplier = 1f + attrFirePower.Value * ATTR_MULTIPLIER_PER_LEVEL;
            firePowerMultiplier *= Mathf.Max(0.01f, rammingFirePowerDamageMultiplier);
            float intensity = Mathf.Clamp01(pushAmount / Mathf.Max(0.1f, EffectiveMaxSpeed));
            float perSecondAtThisPush = ramDamagePerSecond * intensity * momentumMultiplier * firePowerMultiplier;
            perSecondAtThisPush = Mathf.Min(perSecondAtThisPush, maxRammingDamagePerSecond);
            return perSecondAtThisPush * HullRammingSelfDamageMultiplier;
        }

        private float ShakeStrengthFromSelfDamage(float selfDamage, float fullDamageReference)
        {
            float t = Mathf.Clamp01(
                selfDamage * Mathf.Max(0.01f, collisionShakeMomentumScale) / Mathf.Max(0.01f, fullDamageReference));
            return Mathf.Lerp(collisionShakeAmountMin, collisionShakeAmountMax, t);
        }

        private float ShakeStrengthFromSelfDps(float selfDps)
        {
            float t = Mathf.Clamp01(
                selfDps * Mathf.Max(0.01f, collisionShakeMomentumScale) / Mathf.Max(0.01f, collisionShakeFullAtSelfDps));
            return Mathf.Lerp(collisionShakeAmountMin, collisionShakeAmountMax, t);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!IsServer || rb == null) return;
            if (collision.contactCount == 0) return;

            ContactPoint contact = collision.GetContact(0);
            Asteroid asteroid = collision.gameObject.GetComponent<Asteroid>();

            if (asteroid != null && !asteroid.IsDestroyed)
            {
                _asteroidRamContactInstanceIds.Add(asteroid.gameObject.GetInstanceID());
                // Spawn collision spark VFX at contact point
                if (VisualEffectsManager.Instance != null)
                {
                    Vector3 hitPos = contact.point;
                    if (hitPos == Vector3.zero)
                        hitPos = transform.position;
                    Vector3 normal = contact.normal;
                    if (normal.sqrMagnitude < 0.0001f)
                        normal = (transform.position - asteroid.transform.position).normalized;
                    VisualEffectsManager.Instance.SpawnAsteroidCollisionEffectServerRpc(hitPos, normal);
                }

                if (contact.separation < 0f)
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

                var no = asteroid.GetComponent<NetworkObject>();
                if (no != null && no.IsSpawned)
                {
                    float impactSpeed = collision.relativeVelocity.magnitude;
                    impactSpeed = Mathf.Max(0f, impactSpeed - 0.5f);
                    float mass = Mathf.Max(0.5f, rb.mass);

                    float effectiveMax = Mathf.Max(0.1f, EffectiveMaxSpeed);
                    float speed01 = Mathf.Clamp01(impactSpeed / effectiveMax);

                    // Momentum/mass scaling: heavier and faster ships should feel meaningfully stronger.
                    float baselineMomentum = Mathf.Max(0.01f, baseMass * effectiveMax);
                    float momentum01 = (mass * impactSpeed) / baselineMomentum;
                    float momentumInfluence = Mathf.Sqrt(Mathf.Clamp(momentum01, 0f, 4f)); // soften extremes
                    float momentumMultiplier = 1f + rammingMomentumDamageScale * momentumInfluence;

                    // FirePower amplifies ramming damage (attribute-driven, not card-driven).
                    float firePowerMultiplier = 1f + attrFirePower.Value * ATTR_MULTIPLIER_PER_LEVEL;
                    firePowerMultiplier *= Mathf.Max(0.01f, rammingFirePowerDamageMultiplier);

                    // One-shot impact damage on first contact: speed normalized to EffectiveMaxSpeed for stable tuning.
                    float damageFromSpeed = baseRammingDamage + rammingDamagePerSpeedUnit * effectiveMax * speed01;
                    float damage = damageFromSpeed * momentumMultiplier * firePowerMultiplier;
                    damage = Mathf.Clamp(damage, 1f, maxRammingDamageOnFirstHit);

                    // Feedback intensity: stronger momentum also means deeper SFX + more shake.
                    float ramIntensity01 = Mathf.Clamp01((0.6f * speed01 + 0.4f * Mathf.Clamp01(momentum01)) * firePowerMultiplier);
                    float impactPitch = Mathf.Lerp(2.1f, 0.45f, ramIntensity01); // stronger = deeper

                    float toAsteroid = damage * HullRammingToAsteroidMultiplier;
                    float toSelf = damage * HullRammingSelfDamageMultiplier;
                    float shakeAmount = ShakeStrengthFromSelfDamage(toSelf, collisionShakeFullAtSelfDamage);
                    if (VisualEffectsManager.Instance != null && toAsteroid > 0.01f)
                    {
                        VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                            transform.position,
                            (int)FloatingCountChannel.DamageAsteroid,
                            toAsteroid,
                            (int)shipTeam.Value
                        );
                    }
                    asteroid.TakeDamageServerRpc(toAsteroid, NetworkObjectId);
                    TakeDamageServerRpc(toSelf, TeamManager.Team.None);
                    PlayCollisionImpactFeedbackClientRpc(impactPitch, shakeAmount);
                }
            }

            Starship otherShip = collision.collider.GetComponentInParent<Starship>();
            if (otherShip != null && otherShip != this && !otherShip.IsDead && !IsDead)
            {
                var otherNo = otherShip.GetComponent<NetworkObject>();
                if (otherNo != null && otherNo.IsSpawned)
                {
                    float impactSpeed = collision.relativeVelocity.magnitude;
                    impactSpeed = Mathf.Max(0f, impactSpeed - 0.5f);
                    if (impactSpeed < 0.25f) return;

                    float mass = Mathf.Max(0.5f, rb.mass);
                    float effectiveMax = Mathf.Max(0.1f, EffectiveMaxSpeed);
                    float speed01 = Mathf.Clamp01(impactSpeed / effectiveMax);

                    float baselineMomentum = Mathf.Max(0.01f, baseMass * effectiveMax);
                    float momentum01 = (mass * impactSpeed) / baselineMomentum;

                    float firePowerMultiplier = 1f + attrFirePower.Value * ATTR_MULTIPLIER_PER_LEVEL;
                    firePowerMultiplier *= Mathf.Max(0.01f, rammingFirePowerDamageMultiplier);

                    float ramIntensity01 = Mathf.Clamp01((0.6f * speed01 + 0.4f * Mathf.Clamp01(momentum01)) * firePowerMultiplier);
                    float impactPitch = Mathf.Lerp(2.1f, 0.45f, ramIntensity01);

                    float speedT = Mathf.Clamp01(impactSpeed / effectiveMax) * shipRamContactShakeFromSpeed;
                    float shakeAmount = Mathf.Lerp(collisionShakeAmountMin, collisionShakeAmountMax, speedT);

                    PlayCollisionImpactFeedbackClientRpc(impactPitch, shakeAmount);
                }
            }
        }

        private void OnCollisionExit(Collision collision)
        {
            if (!IsServer || _isAIControlled) return;
            Asteroid asteroid = collision.gameObject.GetComponent<Asteroid>();
            if (asteroid == null)
                asteroid = collision.gameObject.GetComponentInParent<Asteroid>();
            if (asteroid == null) return;

            int id = asteroid.gameObject.GetInstanceID();
            if (_asteroidRamContactInstanceIds.Remove(id) && _asteroidRamContactInstanceIds.Count == 0)
                ClearRammingShakeDriveClientRpc(OwnerOnlyClientRpcParams);
        }

        [ClientRpc]
        private void PlayCollisionImpactFeedbackClientRpc(float pitch, float shakeAmount)
        {
            if (AudioManager.Instance != null)
                AudioManager.Instance.PlayImpactSound(pitch);

            // Only the owning player's ship should shake the camera (prevents everyone shaking every ram).
            if (!IsOwner) return;

            if (s_cachedCameraController == null)
                s_cachedCameraController = UnityEngine.Object.FindFirstObjectByType<TitanOrbit.Camera.CameraController>();
            if (s_cachedCameraController == null || !s_cachedCameraController.IsCollisionCameraShakeEnabled)
                return;

            s_cachedCameraController.ApplyCollisionShake(Mathf.Clamp01(shakeAmount));
        }

        [ClientRpc]
        private void ApplyRammingShakeDriveClientRpc(float shakeStrength, ClientRpcParams clientRpcParams = default)
        {
            if (!IsOwner) return;
            if (s_cachedCameraController == null)
                s_cachedCameraController = UnityEngine.Object.FindFirstObjectByType<TitanOrbit.Camera.CameraController>();
            if (s_cachedCameraController == null || !s_cachedCameraController.IsCollisionCameraShakeEnabled)
                return;
            s_cachedCameraController.SetRammingShakeDrive(Mathf.Clamp01(shakeStrength));
        }

        [ClientRpc]
        private void ClearRammingShakeDriveClientRpc(ClientRpcParams clientRpcParams = default)
        {
            if (!IsOwner) return;
            if (s_cachedCameraController == null)
                s_cachedCameraController = UnityEngine.Object.FindFirstObjectByType<TitanOrbit.Camera.CameraController>();
            if (s_cachedCameraController == null) return;
            s_cachedCameraController.SetRammingShakeDrive(0f);
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
                if (!_isAIControlled)
                    ApplyRammingShakeDriveClientRpc(0f, OwnerOnlyClientRpcParams);
                return; // don't do ramming stick/damage while we're escaping overlap
            }

            // Into asteroid = opposite of normal (normal points from asteroid to us)
            Vector3 intoAsteroid = -normal;
            Vector3 vel2 = rb.linearVelocity;
            vel2.y = 0f;
            float pushAmount = Vector3.Dot(vel2, intoAsteroid);
            if (pushAmount <= 0f)
            {
                if (!_isAIControlled)
                    ApplyRammingShakeDriveClientRpc(0f, OwnerOnlyClientRpcParams);
                return; // Not pushing in
            }

            // Keep only the "pushing in" component so we don't slide sideways
            Vector3 ramVelocity = intoAsteroid * pushAmount;
            rb.linearVelocity = new Vector3(ramVelocity.x, 0f, ramVelocity.z);
            currentVelocity = rb.linearVelocity;

            // Update visual collision pitch: tilt slightly away from asteroid based on hit strength and size.
            float hitStrength = Mathf.Clamp01(pushAmount / Mathf.Max(0.1f, EffectiveMaxSpeed));
            float pitchSizeScale = Mathf.Clamp(asteroid.AsteroidSize / 35f, 0.3f, 1.5f);
            float desiredPitch = maxCollisionPitchAngle * hitStrength * pitchSizeScale;
            // Normal points from asteroid to ship; use it to determine left/right sign for pitch (nose up/down)
            Vector3 shipRight = transform.right;
            shipRight.y = 0f;
            float sideSign = Mathf.Sign(Vector3.Dot(shipRight, normal));
            targetCollisionPitchAngle = desiredPitch * sideSign;

            float selfDps = ComputeSelfDamagePerSecondFromAsteroidRam(pushAmount);
            float shakeStrength = ShakeStrengthFromSelfDps(selfDps);
            if (!_isAIControlled)
                ApplyRammingShakeDriveClientRpc(shakeStrength, OwnerOnlyClientRpcParams);

            // Every ramTickInterval while pushing in, apply a noticeable pushback "bounce" and mutual damage.
            float now = Time.time;
            if (now - lastRamDamageTime >= ramTickInterval)
            {
                lastRamDamageTime = now;

                // Pushback impulse: away from asteroid, scaled by size.
                float sizeScale = Mathf.Clamp(asteroid.AsteroidSize / 35f, 0.5f, 1.5f);
                Vector3 pushDir = normal; // away from asteroid
                float mass = Mathf.Max(0.5f, rb.mass);
                float effectiveMax = Mathf.Max(0.1f, EffectiveMaxSpeed);
                float baselineMomentum = Mathf.Max(0.01f, baseMass * effectiveMax);
                float momentum01 = (mass * pushAmount) / baselineMomentum;

                // High momentum should reduce pushback so the ship can "crash through" instead of always bouncing away.
                float momentumInfluence01 = Mathf.Clamp01(momentum01 / Mathf.Max(0.01f, rammingMomentumInfluenceForPushback));
                float pushbackMultiplier = Mathf.Lerp(rammingPushbackMaxMultiplier, rammingPushbackMinMultiplier, momentumInfluence01);

                Vector3 extraVel = pushDir * (asteroidCollisionPushbackSpeed * sizeScale * pushbackMultiplier);
                Vector3 newVel = rb.linearVelocity + extraVel;
                rb.linearVelocity = new Vector3(newVel.x, 0f, newVel.z);
                currentVelocity = rb.linearVelocity;

                // Damage per tick: small chip based on how hard we're pushing.
                float intensity = Mathf.Clamp01(pushAmount / Mathf.Max(0.1f, EffectiveMaxSpeed)); // 0..1
                float momentumInfluence = Mathf.Sqrt(Mathf.Clamp(momentum01, 0f, 4f));
                float momentumMultiplier = 1f + rammingMomentumDamageScale * momentumInfluence;

                float firePowerMultiplier = 1f + attrFirePower.Value * ATTR_MULTIPLIER_PER_LEVEL;
                firePowerMultiplier *= Mathf.Max(0.01f, rammingFirePowerDamageMultiplier);

                float perSecondAtThisPush = ramDamagePerSecond * intensity * momentumMultiplier * firePowerMultiplier;
                perSecondAtThisPush = Mathf.Min(perSecondAtThisPush, maxRammingDamagePerSecond);
                // Ensure each tick does a noticeable chunk of damage so regen cannot keep up under sustained collision.
                float perTick = Mathf.Max(3f, perSecondAtThisPush * ramTickInterval);
                float toAsteroid = perTick * HullRammingToAsteroidMultiplier;
                float toSelf = perTick * HullRammingSelfDamageMultiplier;
                if (VisualEffectsManager.Instance != null && toAsteroid > 0.01f)
                {
                    VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                        transform.position,
                        (int)FloatingCountChannel.DamageAsteroid,
                        toAsteroid,
                        (int)shipTeam.Value
                    );
                }
                if (toAsteroid > 0.0001f) asteroid.TakeDamageServerRpc(toAsteroid, NetworkObjectId);
                if (toSelf > 0.0001f) TakeDamageServerRpc(toSelf, TeamManager.Team.None);
            }

        }

        [ServerRpc(RequireOwnership = false)]
        public void AddGemsServerRpc(float amount, bool playCollectSound = false)
        {
            currentGems.Value = Mathf.Min(currentGems.Value + amount, GemCapacity);
            if (playCollectSound)
                PlayGemCollectSoundClientRpc(amount);
        }

        [ClientRpc]
        private void PlayGemCollectSoundClientRpc(float amount)
        {
            if (AudioManager.Instance != null)
                AudioManager.Instance.PlayGemCollectSound(amount);
        }

        [ClientRpc]
        private void PlayPeopleLoadSoundClientRpc(float amount)
        {
            if (AudioManager.Instance != null)
                AudioManager.Instance.PlayPeopleLoadSound(amount);
        }

        [ClientRpc]
        private void PlayPeopleUnloadSoundClientRpc(float amount)
        {
            if (AudioManager.Instance != null)
                AudioManager.Instance.PlayPeopleUnloadSound(amount);
        }

        [ServerRpc(RequireOwnership = false)]
        public void RemoveGemsServerRpc(float amount)
        {
            currentGems.Value = Mathf.Max(0f, currentGems.Value - amount);
        }

        /// <summary>Client: start the galactic zoom-out camera animation on the owning player.</summary>
        [ClientRpc]
        private void TriggerGalacticZoomClientRpc(ClientRpcParams rpcParams = default)
        {
            if (!IsOwner) return;

            var camController = UnityEngine.Object.FindFirstObjectByType<TitanOrbit.Camera.CameraController>();
            if (camController != null)
            {
                camController.StartGalacticZoomOut();
            }
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

        /// <summary>
        /// Server-only: apply successful people load arrival feedback at ship contact.
        /// Called by PeopleTransportProjectile when a load projectile reaches this ship.
        /// </summary>
        public void OnPeopleLoadArrivedFromProjectile(float amount, TeamManager.Team sourceTeam, Vector3 worldPosition)
        {
            if (!IsServer || amount <= 0f) return;

            if (VisualEffectsManager.Instance != null)
            {
                VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                    worldPosition,
                    (int)FloatingCountChannel.PeopleLoad,
                    amount,
                    (int)sourceTeam
                );
            }

            PlayPeopleLoadSoundClientRpc(amount);
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

        /// <summary>Server-only: set by <see cref="PlanetGemMoon"/> when a ship enters or leaves the dock trigger.</summary>
        public void ServerSetGemMoonDocked(bool value, Planet planetContext = null)
        {
            if (!IsServer) return;
            bool wasDocked = gemMoonDocked.Value;
            gemMoonDocked.Value = value;
            if (value && planetContext != null)
            {
                var no = planetContext.GetComponent<NetworkObject>();
                gemMoonPlanetNetworkObjectId.Value = no != null ? no.NetworkObjectId : 0ul;
            }
            else
                gemMoonPlanetNetworkObjectId.Value = 0ul;

            // Any time the ship newly enters moon orbit, trigger galactic zoom-out for that ship's owner.
            if (value && !wasDocked)
            {
                var sendParams = new ClientRpcParams
                {
                    Send = new ClientRpcSendParams
                    {
                        TargetClientIds = new ulong[] { OwnerClientId }
                    }
                };
                TriggerGalacticZoomClientRpc(sendParams);
            }
        }

        private Planet ResolveGemMoonDockPlanet()
        {
            ulong id = gemMoonPlanetNetworkObjectId.Value;
            if (id == 0ul) return null;
            var spawnManager = NetworkManager.Singleton != null ? NetworkManager.Singleton.SpawnManager : null;
            if (spawnManager != null && spawnManager.SpawnedObjects.TryGetValue(id, out NetworkObject netObj) && netObj != null)
            {
                var p = netObj.GetComponent<Planet>();
                if (p != null) return p;
            }
            for (int i = 0; i < Planet.AllPlanets.Count; i++)
            {
                var pl = Planet.AllPlanets[i];
                if (pl == null) continue;
                var n = pl.GetComponent<NetworkObject>();
                if (n != null && n.NetworkObjectId == id) return pl;
            }
            return null;
        }

        [ServerRpc(RequireOwnership = true)]
        private void RequestUndockGemMoonServerRpc()
        {
            gemMoonDocked.Value = false;
            gemMoonPlanetNetworkObjectId.Value = 0ul;
            if (NetworkManager.Singleton != null)
            {
                gemMoonDockIgnoreUntilServerTime.Value = (float)NetworkManager.Singleton.ServerTime.Time + gemMoonDockIgnoreSeconds;
            }
        }

        /// <summary>Purchase an attribute upgrade. Index 0-9: FirePower, BulletSpeed, MaxHealth, HealthRegen, EnergyCapacity, EnergyRegen, MovementSpeed, RotationSpeed, GemCapacity, PeopleCapacity. Cost = ShipLevel * 5 gems per upgrade.</summary>
        [ServerRpc(RequireOwnership = true)]
        public void UpgradeAttributeServerRpc(int attributeIndex)
        {
            if (attributeIndex < 0 || attributeIndex > 9) return;
            int currentLevel = GetAttributeLevel(attributeIndex);
            if (currentLevel >= MaxAttributeUpgrades) return;
            int cost = AttributeUpgradeCost;
            if (currentGems.Value < cost - 0.01f) return;

            RemoveGemsServerRpc(cost);
            switch (attributeIndex)
            {
                case 0: attrFirePower.Value++; break;
                case 1: attrBulletSpeed.Value++; break;
                case 2: attrMaxHealth.Value++; break;
                case 3: attrHealthRegen.Value++; break;
                case 4: attrEnergyCapacity.Value++; break;
                case 5: attrEnergyRegen.Value++; break;
                case 6: attrMovementSpeed.Value++; break;
                case 7: attrRotationSpeed.Value++; break;
                case 8: attrGemCapacity.Value++; break;
                case 9: attrPeopleCapacity.Value++; break;
            }

            // Weapon stat upgrades are baked into chassis-derived per-cannon values.
            // Rebuild immediately so fire power / bullet speed upgrades take effect on the next shot.
            if (attributeIndex == 0 || attributeIndex == 1)
                RefreshChassisStatsAfterWeaponUpgrade();
        }

        /// <summary>Server-only: refresh current chassis stats after weapon upgrades.</summary>
        private void RefreshChassisStatsAfterWeaponUpgrade()
        {
            if (!IsServer) return;
            if (CardShopSystem.Instance == null) return;

            string cid = currentChassisId.Value.ToString();
            GameObject prefab = !string.IsNullOrEmpty(cid)
                ? CardShopSystem.Instance.GetShipPrefabForChassisId(cid)
                : null;
            if (prefab == null && currentChassisIndex.Value >= 0)
                prefab = CardShopSystem.Instance.GetShipPrefabForChassisIndex(currentChassisIndex.Value);
            if (prefab == null) return;

            ApplyShipVisualFromPrefab(prefab);
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

        /// <summary>Server-only: detect if ship is inside a planet's orbit zone (e.g. after spawning there). OnTriggerEnter doesn't fire for objects that start inside.</summary>
        private void TryDetectOrbitZoneServer()
        {
            if (!IsServer || rb == null || currentOrbitPlanet != null) return;
            foreach (var planet in Planet.AllPlanets)
            {
                if (planet == null) continue;
                Vector3 toShip = rb.position - planet.transform.position;
                toShip.y = 0f;
                float dist = toShip.magnitude;
                float inner = planet.PlanetSize * 0.5f;
                float outer = planet.PlanetSize * planet.GetOrbitZoneOuterRadiusLocal();
                if (dist >= inner && dist <= outer)
                {
                    currentOrbitPlanet = planet;
                    break;
                }
            }
        }

        /// <summary>Owner-only: detect if we're inside a planet's orbit zone (e.g. after spawning there).</summary>
        private void TryDetectOrbitZone()
        {
            if (rb == null || currentOrbitPlanet != null) return;
            if (!IsLocalPlayerShip()) return;
            foreach (var planet in Planet.AllPlanets)
            {
                if (planet == null) continue;
                Vector3 toShip = rb.position - planet.transform.position;
                toShip.y = 0f;
                float dist = toShip.magnitude;
                float inner = planet.PlanetSize * 0.5f;
                float outer = planet.PlanetSize * planet.GetOrbitZoneOuterRadiusLocal();
                if (dist >= inner && dist <= outer)
                {
                    currentOrbitPlanet = planet;
                    break;
                }
            }
        }

        /// <summary>True if this ship is the local player's ship (not AI or other players).</summary>
        private bool IsLocalPlayerShip()
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.SpawnManager == null) return false;
            var localPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
            var netObj = GetComponent<NetworkObject>();
            return localPlayer != null && netObj != null && localPlayer == netObj;
        }

        /// <summary>Called by PlanetOrbitZone when ship enters the orbit/loading zone. Menu is shown only once in stable orbit (see Update).</summary>
        public void EnterOrbitZone(Planet planet)
        {
            if (planet == null) return;
            currentOrbitPlanet = planet;
            hadGemsWhileInOrbitThisOrbit = false;
            depositedAnyGemsThisOrbit = false;
            triggeredGalacticZoomThisOrbit = false;
            // Menu shows in Update when IsInStableOrbit() is true, not on zone entry
        }

        /// <summary>Called by PlanetOrbitZone when ship leaves the orbit zone.</summary>
        /// <remarks>Load/unload toggles are not cleared here so they don't reset when the ship briefly exits the zone (e.g. orbit wobble). They only reset in TickOrbitPopulationTransfer when transfer is complete (ship full or empty).</remarks>
        public void ExitOrbitZone(Planet planet)
        {
            if (currentOrbitPlanet == planet)
            {
                if (IsOwner && gemMoonDocked.Value)
                {
                    DetachFromGemMoonParent();
                    SetRootColliderDocked(false);
                    RequestUndockGemMoonServerRpc();
                }
                currentOrbitPlanet = null;
                hadGemsWhileInOrbitThisOrbit = false;
                depositedAnyGemsThisOrbit = false;
                triggeredGalacticZoomThisOrbit = false;
                if (IsLocalPlayerShip() && shipTeam.Value != TeamManager.Team.None)
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
                if (IsServer && networkBranchIndex != null)
                    networkBranchIndex.Value = data.branchIndex;
                // When ship levels up, reset attribute upgrades only (keep cards)
                if (IsServer && data.shipLevel > shipLevel)
                {
                    ResetAttributeLevels();
                }
                shipLevel = data.shipLevel;
                if (IsServer && networkShipLevel != null)
                    networkShipLevel.Value = Mathf.Max(1, shipLevel);
                focusType = data.focusType;
                weaponConfig = data.weaponConfig != null && data.weaponConfig.cannons != null && data.weaponConfig.cannons.Count > 0
                    ? data.weaponConfig
                    : GetDefaultWeaponConfig();
                EnsureBulletLastFireTime();
                for (int i = 0; bulletLastFireTime != null && i < bulletLastFireTime.Length; i++) bulletLastFireTime[i] = -999f;

                // Stats come solely from chassis components (ApplyChassisComponentStats). Only use ShipData as fallback when no prefab.
                if (data.shipPrefab == null)
                {
                    componentEngineThrust = 0f;
                    componentEngineMaxSpeed = 0f;
                    componentMass = 0f;
                    engineThrust = data.baseMovementSpeed;
                    maxHealth = data.baseMaxHealth;
                    healthRegenRate = data.baseHealthRegenRate;
                    rotationSpeed = data.baseRotationSpeed;
                    rotationSpeedFromShipFamilyDefinition = false;
                    gemCapacity = data.baseGemCapacity;
                    peopleCapacity = data.basePeopleCapacity;
                    energyCapacity = data.baseEnergyCapacity;
                    energyRegenRate = data.baseEnergyRegenRate;
                }

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
            if (root == null)
            {
                Debug.LogWarning("Starship: GetPrefabTransform() returned null. Ensure EnsureVisualRootForBanking runs in Awake.");
                return;
            }

            if (lastVisualApplyFrame == Time.frameCount && lastVisualApplyPrefab == shipPrefab)
            {
                return;
            }
            lastVisualApplyFrame = Time.frameCount;
            lastVisualApplyPrefab = shipPrefab;

            GameObject instance = Instantiate(shipPrefab);
            Transform prefabRoot = instance.transform;
            Vector3 prefabScale = prefabRoot.localScale;

            // Read ShipFamilyStatsPreview from prefab instance before reparenting (instance is destroyed later).
            // All starship prefabs should have this component with Ship Family assigned so Starship gets proper summed stats.
            ShipComponentAbilityStats? previewStats = null;
            ShipFamilyDefinition previewFamilyDef = null;
            System.Collections.Generic.List<string> matchedComponentIds = null;
            System.Collections.Generic.List<ShipComponentAbilityStats> perComponentStatsList = null;
            var preview = instance.GetComponentInChildren<ShipFamilyStatsPreview>(true);
            if (preview != null && preview.ShipFamily != null)
            {
                preview.RecalculateFromChildren();
                previewStats = preview.TotalStats;
                previewFamilyDef = preview.ShipFamily;
                matchedComponentIds = new System.Collections.Generic.List<string>(preview.MatchedComponentIds);
                perComponentStatsList = new System.Collections.Generic.List<ShipComponentAbilityStats>(preview.PerComponentStats);
            }
            else if (preview == null || preview.ShipFamily == null)
            {
                WarnOnceMissingShipFamilyStatsPreview(shipPrefab, preview != null);
            }

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

            // Scale parent container once (prefab root scale + game scale)
            float baseScale = (data != null && data.visualScale > 0f ? data.visualScale : 1f) * Mathf.Max(0.005f, shipVisualScaleMultiplier);
            visualBaseScale = baseScale;
            lastPrefabScale = prefabScale;
            root.localScale = Vector3.Scale(prefabScale, Vector3.one * baseScale);

            // Rebind FirePoint only if the prefab provided one; never create a fallback. Bullets fire only from Weapon components.
            if (newFirePoint != null)
                firePoint = newFirePoint;
            else
                firePoint = null;

            // Imported example prefabs may include many colliders/rigidbodies/scripts intended for editor setup.
            // Keep only visual components under the ship visual root to avoid heavy runtime overhead.
            StripNonVisualComponents(root, firePoint);

            RefreshGemMoonPrefabBaseline();

            // Parse chassis component names (e.g. AstroEagle_Weapon, CraizanStar_Engine_2). Derive family from prefab name.
            string familyPrefix = DeriveFamilyPrefixFromPrefab(shipPrefab);
            ApplyChassisComponentStats(root, data, familyPrefix, previewStats, previewFamilyDef, matchedComponentIds, perComponentStatsList);
        }

        /// <summary>Derives family prefix from prefab name (e.g. CraizanStar3 -> CraizanStar). USC modular prefabs use FamilyName + number.</summary>
        private static string DeriveFamilyPrefixFromPrefab(GameObject prefab)
        {
            if (prefab == null) return "AstroEagle";
            string name = prefab.name;
            if (string.IsNullOrEmpty(name)) return "AstroEagle";
            int cloneIdx = name.IndexOf("(Clone)");
            if (cloneIdx > 0) name = name.Substring(0, cloneIdx).TrimEnd();
            int i = name.Length - 1;
            while (i >= 0 && char.IsDigit(name[i])) i--;
            if (i < name.Length - 1)
                name = name.Substring(0, i + 1);
            return string.IsNullOrEmpty(name) ? "AstroEagle" : name;
        }

        private static readonly HashSet<int> _warnedMissingPreviewPrefabIds = new HashSet<int>();

        /// <summary>Warn once per prefab that ShipFamilyStatsPreview is missing or has no Ship Family assigned. Starship uses it for proper summed ability stats.</summary>
        private static void WarnOnceMissingShipFamilyStatsPreview(GameObject prefab, bool hasComponentNoFamily)
        {
            if (prefab == null) return;
            int id = prefab.GetInstanceID();
            if (_warnedMissingPreviewPrefabIds.Contains(id)) return;
            _warnedMissingPreviewPrefabIds.Add(id);
            if (hasComponentNoFamily)
                Debug.LogWarning($"Starship prefab '{prefab.name}' has ShipFamilyStatsPreview but no Ship Family assigned. Assign the ShipFamilyDefinition (e.g. AstroEagle) so the ship uses proper summed ability stats.");
            else
                Debug.LogWarning($"Starship prefab '{prefab.name}' has no ShipFamilyStatsPreview. Add ShipFamilyStatsPreview to the prefab root and assign Ship Family so the ship uses proper summed ability stats. Use Titan Orbit > Add Ship Family Stats Preview To Upgrade Tree Prefabs on the ShipFamilyDefinition.");
        }

        private const string CHASSIS_FAMILY_PREFIX = "AstroEagle";
        private static readonly float MUZZLE_BASE_SIZE = 0.18f;
        private static readonly float MUZZLE_SIZE_PER_ENERGY = 0.04f;

        private void ApplyChassisComponentStats(Transform root, ShipData data, string familyPrefix = null,
            ShipComponentAbilityStats? previewStats = null, ShipFamilyDefinition previewFamilyDef = null,
            System.Collections.Generic.IReadOnlyList<string> matchedComponentIds = null,
            System.Collections.Generic.IReadOnlyList<ShipComponentAbilityStats> perComponentStats = null)
        {
            string prefix = !string.IsNullOrEmpty(familyPrefix) ? familyPrefix : CHASSIS_FAMILY_PREFIX;
            var stats = ChassisComponentStats.FromTransform(root, prefix);

            int level = ShipLevel;
            bool usePreviewStats = previewStats.HasValue && previewFamilyDef != null;
            float weaponScaleTotal = 0f;
            for (int w = 0; w < stats.weaponScales.Count; w++) weaponScaleTotal += stats.weaponScales[w];

            if (usePreviewStats)
            {
                ShipComponentAbilityStats s = previewStats.Value;
                float perLvl = Mathf.Max(0, level - 1);

                maxHealth = Mathf.Max(1f, s.healthCap + s.healthCapPerLevel * perLvl);
                healthRegenRate = Mathf.Max(0f, s.healthRegen + s.healthRegenPerLevel * perLvl);
                energyCapacity = Mathf.Max(1f, s.energyCap + s.energyCapPerLevel * perLvl);
                energyRegenRate = Mathf.Max(0f, s.energyRegen + s.energyRegenPerLevel * perLvl);
                rotationSpeed = Mathf.Max(1f, s.turnSpeed + s.turnSpeedPerLevel * perLvl);
                rotationSpeedFromShipFamilyDefinition = true;
                gemCapacity = Mathf.Max(0f, s.maxGems + s.maxGemsPerLevel * perLvl);
                peopleCapacity = Mathf.Max(0f, s.maxPeople + s.maxPeoplePerLevel * perLvl);

                float moveVal = s.moveSpeed + s.moveSpeedPerLevel * perLvl;
                componentEngineThrust = Mathf.Max(0f, moveVal);
                // Max speed from largest engine only (more engines = more acceleration, not higher top speed)
                float maxEngineMoveSpeed = 0f;
                if (matchedComponentIds != null && perComponentStats != null)
                {
                    for (int k = 0; k < matchedComponentIds.Count && k < perComponentStats.Count; k++)
                    {
                        if (ShipComponentAbilityStats.IsEngineComponent(matchedComponentIds[k]))
                        {
                            ShipComponentAbilityStats comp = perComponentStats[k];
                            float engineSpeed = comp.moveSpeed + comp.moveSpeedPerLevel * perLvl;
                            if (engineSpeed > maxEngineMoveSpeed) maxEngineMoveSpeed = engineSpeed;
                        }
                    }
                }
                componentEngineMaxSpeed = Mathf.Max(0.1f, maxEngineMoveSpeed > 0f ? maxEngineMoveSpeed : moveVal);

                componentMass =
                    stats.engineScaleTotal +
                    stats.thrusterScaleTotal +
                    stats.wingScaleTotal +
                    stats.cockpitScaleTotal +
                    stats.partScaleTotal +
                    stats.tailScaleTotal +
                    stats.finScaleTotal +
                    weaponScaleTotal;
                componentMass = Mathf.Max(0.5f, componentMass);
            }
            else
            {
                // Fallback when ShipFamilyDefinition stats are not available: derive rough values from component scales only.
                float thrustFromEngines = stats.engineScaleTotal;
                float thrustFromThrusters = stats.thrusterScaleTotal;
                componentEngineThrust = Mathf.Max(0f, thrustFromEngines + thrustFromThrusters);
                componentEngineMaxSpeed = Mathf.Max(0.1f, stats.engineScaleMax);

                // Safety: never let fallback component-based values make the ship slower than the legacy base values.
                // If parsing or naming changes reduce engineScale totals, we still keep at least the original thrust and max speed.
                if (componentEngineThrust < engineThrust)
                    componentEngineThrust = engineThrust;
                float legacyBaseMaxSpeed = Mathf.Max(2f, engineThrust * 0.5f);
                if (componentEngineMaxSpeed < legacyBaseMaxSpeed)
                    componentEngineMaxSpeed = legacyBaseMaxSpeed;

                componentMass =
                    stats.engineScaleTotal +
                    stats.thrusterScaleTotal +
                    stats.wingScaleTotal +
                    stats.cockpitScaleTotal +
                    stats.partScaleTotal +
                    stats.tailScaleTotal +
                    stats.finScaleTotal +
                    weaponScaleTotal;
                componentMass = Mathf.Max(0.5f, componentMass);

                float turnVal = stats.thrusterScaleTotal + stats.tailScaleTotal + stats.wingScaleTotal + stats.finScaleTotal;
                float healthVal = stats.cockpitScaleTotal + stats.partScaleTotal;
                float healthRegenVal = stats.wingScaleTotal + stats.partScaleTotal;
                float gemVal = stats.wingScaleTotal + stats.partScaleTotal;
                float peopleVal = stats.cockpitScaleTotal + stats.partScaleTotal;
                float energyCapVal = stats.cockpitCannonScaleTotal;
                float energyRegenVal = stats.cockpitCannonScaleTotal;

                rotationSpeed = Mathf.Max(1f, turnVal);
                rotationSpeedFromShipFamilyDefinition = false;
                maxHealth = Mathf.Max(1f, healthVal);
                healthRegenRate = Mathf.Max(0f, healthRegenVal);
                gemCapacity = Mathf.Max(0f, gemVal);
                peopleCapacity = Mathf.Max(0f, peopleVal);
                energyCapacity = Mathf.Max(1f, energyCapVal);
                energyRegenRate = Mathf.Max(0f, energyRegenVal);
            }

            // Clear component scale caches for attribute-based scaling
            cockpitScaleTransforms.Clear();
            cockpitBaseScales.Clear();
            cockpitBasePositions.Clear();
            wingScaleTransforms.Clear();
            wingBaseScales.Clear();
            wingBasePositions.Clear();
            weaponScaleTransforms.Clear();
            weaponBaseScales.Clear();
            weaponBasePositions.Clear();
            engineScaleTransforms.Clear();
            engineBaseScales.Clear();
            engineBasePositions.Clear();
            thrusterScaleTransforms.Clear();
            thrusterBaseScales.Clear();
            thrusterBasePositions.Clear();
            partScaleTransforms.Clear();
            partBaseScales.Clear();
            partBasePositions.Clear();
            muzzleBaseSizes.Clear();
            muzzleBaseSpeeds.Clear();

            // Clear previous bullet state (from previous prefab). Cannons removed; only Weapon bullets.
            bulletFirePoints.Clear();
            bulletPrefabBankIndex = -1;
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

            // Destroy previous runtime-created WeaponConfig to avoid ScriptableObject leak when transforming ship
            if (bulletConfig != null)
            {
                Object.Destroy(bulletConfig);
            }
            bulletConfig = null;

            // Bullets (Weapon only): one cannon per component with "Weapon" in the name; fire from each weapon position.
            int weaponCount = stats.weaponTransforms != null ? stats.weaponTransforms.Count : 0;
            if (weaponScaleTotal <= 0f && weaponCount > 0) weaponScaleTotal = weaponCount;
            if (weaponCount > 0)
            {
                var baseBullet = (data != null && data.weaponConfig != null && data.weaponConfig.cannons != null && data.weaponConfig.cannons.Count > 0)
                    ? data.weaponConfig.cannons[0]
                    : GetDefaultWeaponConfig().cannons[0];
                var bc = ScriptableObject.CreateInstance<WeaponConfig>();
                bc.displayName = "ChassisBullets";
                bc.cannons = new System.Collections.Generic.List<CannonConfig>();

                // Per-level scaling for weapon abilities comes from the ship's attribute upgrade levels.
                int firePowerUpgrades = attrFirePower.Value;
                int bulletSpeedUpgrades = attrBulletSpeed.Value;
                int fireRateUpgrades = attrFireRate.Value;

                float perLvlFirePower = Mathf.Max(0f, firePowerUpgrades);
                float perLvlBulletSpeed = Mathf.Max(0f, bulletSpeedUpgrades);
                float perLvlFireRate = Mathf.Max(0f, fireRateUpgrades);

                // Use same familyId as ShipFamilyStatsPreview so componentId matches matchedComponentIds (e.g. "Weapon_1" not full name).
                string weaponLookupFamilyId = (previewFamilyDef != null && !string.IsNullOrEmpty(previewFamilyDef.familyId))
                    ? previewFamilyDef.familyId.Trim()
                    : prefix;

                for (int i = 0; i < weaponCount; i++)
                {
                    var c = baseBullet.Clone();
                    Transform wt = stats.weaponTransforms != null && i < stats.weaponTransforms.Count ? stats.weaponTransforms[i] : null;
                    string componentId = "";
                    string resolvedComponentId = componentId;
                    if (wt != null && !string.IsNullOrEmpty(wt.name))
                    {
                        if (!string.IsNullOrEmpty(weaponLookupFamilyId) && wt.name.StartsWith(weaponLookupFamilyId + "_", System.StringComparison.OrdinalIgnoreCase))
                            componentId = wt.name.Substring(weaponLookupFamilyId.Length + 1);
                        else
                            componentId = wt.name;
                    }
                    resolvedComponentId = componentId;

                    bool usedPerComponent = false;
                    // 1) Prefer per-component stats from ShipFamilyStatsPreview (matched component list) - case-insensitive match.
                    if (matchedComponentIds != null && perComponentStats != null && !string.IsNullOrEmpty(componentId))
                    {
                        for (int k = 0; k < matchedComponentIds.Count; k++)
                        {
                            if (string.Equals(matchedComponentIds[k], componentId, System.StringComparison.OrdinalIgnoreCase) && k < perComponentStats.Count)
                            {
                                ShipComponentAbilityStats comp = perComponentStats[k];
                                float wp = comp.firePower + comp.firePowerPerLevel * perLvlFirePower;
                                float bs = comp.bulletSpeed + comp.bulletSpeedPerLevel * perLvlBulletSpeed;
                                float fr = Mathf.Max(0.01f, comp.fireRate + comp.fireRatePerLevel * perLvlFireRate);
                                c.damagePerBullet = wp;
                                c.bulletSpeed = bs;
                                c.fireRate = fr;
                                c.energyCostPerShot = c.damagePerBullet;
                                usedPerComponent = true;
                                break;
                            }
                        }
                    }
                    // 2) If no match in preview list, get this weapon's stats from ShipFamilyDefinition and scale by transform (still per-component, not summed).
                    if (!usedPerComponent && previewFamilyDef != null && wt != null && !string.IsNullOrEmpty(componentId) && previewFamilyDef.TryGetStatsForComponent(componentId, out var defStats))
                    {
                        ShipComponentAbilityStats scaled = ShipComponentAbilityStats.ScaleStatsByTransform(defStats, wt, componentId);
                        float wp = scaled.firePower + scaled.firePowerPerLevel * perLvlFirePower;
                        float bs = scaled.bulletSpeed + scaled.bulletSpeedPerLevel * perLvlBulletSpeed;
                        float fr = Mathf.Max(0.01f, scaled.fireRate + scaled.fireRatePerLevel * perLvlFireRate);
                        c.damagePerBullet = wp;
                        c.bulletSpeed = bs;
                        c.fireRate = fr;
                        c.energyCostPerShot = c.damagePerBullet;
                        usedPerComponent = true;
                    }
                    // 3) If direct lookup fails (name mismatch), map cannon index to the Nth weapon entry in ShipFamilyDefinition.
                    // This avoids using unrelated summed totals and keeps ShipFamilyDefinition as source of truth.
                    if (!usedPerComponent && previewFamilyDef != null && wt != null && previewFamilyDef.components != null)
                    {
                        int weaponEntryCounter = -1;
                        for (int e = 0; e < previewFamilyDef.components.Count; e++)
                        {
                            var entry = previewFamilyDef.components[e];
                            if (entry == null || string.IsNullOrEmpty(entry.componentId)) continue;
                            if (!ShipComponentAbilityStats.IsWeaponComponent(entry.componentId)) continue;
                            weaponEntryCounter++;
                            if (weaponEntryCounter != i) continue;

                            ShipComponentAbilityStats scaled = ShipComponentAbilityStats.ScaleStatsByTransform(entry.stats, wt, entry.componentId);
                            float wp = scaled.firePower + scaled.firePowerPerLevel * perLvlFirePower;
                            float bs = scaled.bulletSpeed + scaled.bulletSpeedPerLevel * perLvlBulletSpeed;
                            float fr = Mathf.Max(0.01f, scaled.fireRate + scaled.fireRatePerLevel * perLvlFireRate);
                            c.damagePerBullet = wp;
                            c.bulletSpeed = bs;
                            c.fireRate = fr;
                            c.energyCostPerShot = c.damagePerBullet;
                            resolvedComponentId = entry.componentId;
                            usedPerComponent = true;
                            break;
                        }
                    }
                    // Per-weapon bullet prefab index from ShipFamilyComponentEntry (index into CombatSystem's Bullet Prefab Bank).
                    if (previewFamilyDef != null && !string.IsNullOrEmpty(resolvedComponentId) && previewFamilyDef.TryGetComponentEntry(resolvedComponentId, out var compEntry) && compEntry != null && compEntry.bulletPrefabIndex >= 0)
                        c.bulletPrefabIndex = compEntry.bulletPrefabIndex;
                    bc.cannons.Add(c);
                    Transform pt = stats.weaponTransforms[i];
                    if (pt == null) pt = transform;
                    bulletFirePoints.Add(pt);

                    float ws = (stats.weaponScales != null && i < stats.weaponScales.Count) ? stats.weaponScales[i] : 1f;
                    float muzzleScale = (MUZZLE_BASE_SIZE + c.energyCostPerShot * MUZZLE_SIZE_PER_ENERGY) * Mathf.Max(0.5f, ws);
                    ParticleSystem muzzle = CreateMuzzleParticleSystem(pt, muzzleScale);
                    if (muzzle != null)
                    {
                        bulletMuzzleParticleSystems.Add(muzzle);
                        muzzleBaseSizes.Add(muzzleScale);
                        muzzleBaseSpeeds.Add(2.5f);
                    }
                    if (wt != null)
                    {
                        weaponScaleTransforms.Add(wt);
                        weaponBaseScales.Add(wt.localScale);
                        weaponBasePositions.Add(wt.localPosition);
                    }
                }
                // Bullet prefab index from family definition (index into CombatSystem's Bullet Prefab Bank)
                if (Systems.CombatSystem.Instance != null)
                {
                    int count = Systems.CombatSystem.Instance.BulletPrefabBankCount;
                    int idx = (previewFamilyDef != null && count > 0) ? previewFamilyDef.bulletPrefabIndex : 0;
                    bulletPrefabBankIndex = (idx >= 0 && count > 0 && idx < count) ? idx : (count > 0 ? 0 : -1);
                    if (IsServer)
                        runtimeBulletPrefabIndex.Value = bulletPrefabBankIndex;
                }
                else
                {
                    bulletPrefabBankIndex = -1;
                    if (IsServer)
                        runtimeBulletPrefabIndex.Value = -1;
                }
                bulletConfig = bc;
            }

            EnsureBulletLastFireTime();

            // Populate component scale caches for attribute-based scaling
            if (stats.cockpitTransforms != null)
            {
                foreach (Transform t in stats.cockpitTransforms)
                {
                    if (t != null) { cockpitScaleTransforms.Add(t); cockpitBaseScales.Add(t.localScale); cockpitBasePositions.Add(t.localPosition); }
                }
            }
            if (stats.wingTransforms != null)
            {
                foreach (Transform t in stats.wingTransforms)
                {
                    if (t != null) { wingScaleTransforms.Add(t); wingBaseScales.Add(t.localScale); wingBasePositions.Add(t.localPosition); }
                }
            }
            if (stats.engineTransforms != null)
            {
                foreach (Transform t in stats.engineTransforms)
                {
                    if (t != null) { engineScaleTransforms.Add(t); engineBaseScales.Add(t.localScale); engineBasePositions.Add(t.localPosition); }
                }
            }
            if (stats.thrusterTransforms != null)
            {
                foreach (Transform t in stats.thrusterTransforms)
                {
                    if (t != null) { thrusterScaleTransforms.Add(t); thrusterBaseScales.Add(t.localScale); thrusterBasePositions.Add(t.localPosition); }
                }
            }
            if (stats.partTransforms != null)
            {
                foreach (Transform t in stats.partTransforms)
                {
                    if (t != null) { partScaleTransforms.Add(t); partBaseScales.Add(t.localScale); partBasePositions.Add(t.localPosition); }
                }
            }

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

        /// <summary>Server only: resets all attribute upgrades and removes all equipped cards. Available for full reset if needed.</summary>
        public void ResetCardsAndAttributesFromServer()
        {
            if (!IsServer) return;
            ResetAttributeLevels();
            ClearAllCardsFromServer();
        }

        /// <summary>Server only: resets attribute upgrades only. Keeps equipped cards/slots. Call when buying a new chassis.</summary>
        public void ResetAttributesOnlyFromServer()
        {
            if (!IsServer) return;
            ResetAttributeLevels();
        }

        /// <summary>Server only: removes all equipped cards. Called when ship levels up.</summary>
        private void ClearAllCardsFromServer()
        {
            if (!IsServer) return;
            if (equippedCards != null) equippedCards.Clear();
            if (equippedCardIds != null) equippedCardIds.Clear();
            _cardStatsCacheFrame = -1;
            var composer = GetComponent<ShipVisualComposer>();
            if (composer != null) composer.RebuildVisuals();
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
            attrGemCapacity.Value = 0;
            attrPeopleCapacity.Value = 0;
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
            if (_cardStatsCacheFrame != Time.frameCount) RefreshCardStatsCache();
            return _cachedCardMovementSpeedAdd;
        }

        private float GetCardRotationSpeedAdd()
        {
            if (_cardStatsCacheFrame != Time.frameCount) RefreshCardStatsCache();
            return _cachedCardRotationSpeedAdd;
        }

        private float GetCardMaxHealthAdd()
        {
            if (_cardStatsCacheFrame != Time.frameCount) RefreshCardStatsCache();
            return _cachedCardMaxHealthAdd;
        }

        private float GetCardHealthRegenAdd()
        {
            if (_cardStatsCacheFrame != Time.frameCount) RefreshCardStatsCache();
            return _cachedCardHealthRegenAdd;
        }

        private float GetCardEnergyCapacityAdd()
        {
            if (_cardStatsCacheFrame != Time.frameCount) RefreshCardStatsCache();
            return _cachedCardEnergyCapacityAdd;
        }

        private float GetCardEnergyRegenAdd()
        {
            if (_cardStatsCacheFrame != Time.frameCount) RefreshCardStatsCache();
            return _cachedCardEnergyRegenAdd;
        }

        private float GetCardGemCapacityAdd()
        {
            if (_cardStatsCacheFrame != Time.frameCount) RefreshCardStatsCache();
            return _cachedCardGemCapacityAdd;
        }

        private float GetCardPeopleCapacityAdd()
        {
            if (_cardStatsCacheFrame != Time.frameCount) RefreshCardStatsCache();
            return _cachedCardPeopleCapacityAdd;
        }

        private float GetCardDamageMultiplier()
        {
            if (equippedCards == null || equippedCards.Count == 0) return 1f;
            if (_cardStatsCacheFrame != Time.frameCount) RefreshCardStatsCache();
            return _cachedCardDamageMultiplier;
        }

        private float GetCardBulletSpeedMultiplier()
        {
            if (equippedCards == null || equippedCards.Count == 0) return 1f;
            if (_cardStatsCacheFrame != Time.frameCount) RefreshCardStatsCache();
            return _cachedCardBulletSpeedMultiplier;
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
            if (equippedCardIds == null) return;
            int maxSlots = SlotCount;
            if (equippedCards.Count >= maxSlots) return;
            if (!equippedCards.Contains(card))
            {
                equippedCards.Add(card);
                equippedCardIds.Add(new EquippedCardId { cardId = new FixedString64Bytes(card.cardId) });
                _cardStatsCacheFrame = -1;
            }
        }

        /// <summary>
        /// Server-only: remove a card from the given slot index. Players can always remove a card to make space for a new one.
        /// </summary>
        public void RemoveCardFromServer(int slotIndex)
        {
            if (!IsServer) return;
            if (equippedCards == null) return;
            if (slotIndex < 0 || slotIndex >= equippedCards.Count) return;
            equippedCards.RemoveAt(slotIndex);
            _cardStatsCacheFrame = -1;
            if (equippedCardIds != null && slotIndex < equippedCardIds.Count)
                equippedCardIds.RemoveAt(slotIndex);
        }

        /// <summary>Client calls this to request removal of a card at the given slot. Only the ship owner can remove cards.</summary>
        [ServerRpc(RequireOwnership = true)]
        public void RemoveCardServerRpc(int slotIndex)
        {
            RemoveCardFromServer(slotIndex);
        }

        /// <summary>Server-only: set the current chassis index (from ShipUnlockTable) so clients can show the correct card grid layout.</summary>
        public void SetCurrentChassisIndex(int index)
        {
            if (!IsServer) return;
            currentChassisIndex.Value = index;
        }

        /// <summary>Server-only: set chassis ID when purchasing from planet-specific family (e.g. CraizanStar_05). Enables correct prefab resolution.</summary>
        public void SetCurrentChassisId(string chassisId)
        {
            if (!IsServer) return;
            currentChassisId.Value = string.IsNullOrEmpty(chassisId) ? default : new FixedString64Bytes(chassisId);
        }

        /// <summary>Server-only: set ship level from chassis tier when upgrading without baseShipData (e.g. AstroEagle variants). Syncs to clients so orbit UI shows correct slot count.</summary>
        public void SetShipLevelFromTier(int tierLevel)
        {
            if (!IsServer) return;
            int level = Mathf.Max(1, tierLevel);
            shipLevel = level;
            if (networkShipLevel != null)
                networkShipLevel.Value = level;
        }
    }
}
