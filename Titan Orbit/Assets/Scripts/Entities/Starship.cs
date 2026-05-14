using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.Serialization;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine.InputSystem;
using TitanOrbit.Core;
using TitanOrbit.Input;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Systems;
using TitanOrbit.AI;
using TitanOrbit.Audio;
using SciFiArsenal;

namespace TitanOrbit.Entities
{
    [System.Serializable]
    public class ThrusterVfxColorPrefab
    {
        public string colorName = "Blue";
        public GameObject prefab;
    }

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
        /// <summary>Authored root BoxCollider (Starship) before runtime attribute-based scaling; Rigidbody uses this collider for shape.</summary>
        private Vector3 rootColliderBaselineSize = Vector3.one;
        private Vector3 rootColliderBaselineCenter;
        private bool rootColliderBaselineCaptured;

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
        [Tooltip("Optional: fallback VFX prefab for thruster components when no color match is found in Thruster Jet Flame Bank.")]
        [SerializeField] private GameObject thrusterVfxPrefab;
        [Tooltip("When enabled, thruster VFX are shown while accelerating (forward thrust) instead of while turning.")]
        [SerializeField] private bool useThrusterVfxForAcceleration = true;
        [Tooltip("Color-based JetFlame prefabs (name contains color, e.g. Blue/Red/Green). One prefab is chosen per thruster by matching the thruster object name color.")]
        [SerializeField] private List<ThrusterVfxColorPrefab> thrusterJetFlameBank = new List<ThrusterVfxColorPrefab>();
        [Tooltip("Local position offset for spawned thruster flames (use negative Z to push flame further behind the thruster).")]
        [SerializeField] private Vector3 thrusterVfxLocalOffset = new Vector3(0f, 0f, -0.2f);
        [Tooltip("Local rotation offset for spawned thruster flames. Default rotates flame to point backward.")]
        [SerializeField] private Vector3 thrusterVfxLocalEuler = new Vector3(0f, 180f, 0f);
        [Tooltip("Scale multiplier when thruster flame is idle/off. Keep > 0 to avoid harsh popping.")]
        [SerializeField, Range(0f, 1f)] private float thrusterVfxIdleScale = 0.1f;
        [Tooltip("How quickly thruster flame transitions between idle and full scale/emission. Lower is slower and more visible.")]
        [SerializeField, Min(0.01f)] private float thrusterVfxTransitionSpeed = 3f;
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
        [Tooltip("How much extra bullet size grows once Fire Power / Bullet Speed / cards push the upgrade factor above 1. At 0 upgrades the projectile scale multiplier is 1 (cannon bulletScale × prefab only); this only amplifies the upgrade delta.")]
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
        private float _cachedCardGemDepositSpeedMultiplier = 1f;
        private float _cachedCardPeopleTransferSpeedMultiplier = 1f;

        /// <summary>Mass from chassis components (Engine, Thruster, Wing, Cockpit, Part, etc.). Used when chassis applied.</summary>
        private float componentMass = 0f;
        /// <summary>Thrust force from engine components. Applied via AddForce; acceleration = thrust/mass.</summary>
        private float componentEngineThrust = 0f;
        /// <summary>Max speed from chassis: best single engine (or best thruster if no engines). Not summed across engines.</summary>
        private float componentEngineMaxSpeed = 0f;

        private WeaponConfig weaponConfig;
        /// <summary>Bullets from Weapon: light projectiles, low energy. Only weapons fire; cockpits do not.</summary>
        private WeaponConfig bulletConfig;
        private float[] bulletLastFireTime;
        /// <summary>Per-energy-cost round-robin cursor so equal-cost weapons alternate fairly.</summary>
        private readonly System.Collections.Generic.Dictionary<int, int> bulletRoundRobinStartByEnergy = new System.Collections.Generic.Dictionary<int, int>();

        [Header("Collision")]
        [Tooltip("Max bounce (coefficient of restitution along the impact normal) when ramming power is low. Higher = more energy reflected back to the ship. Ramming power reduces this toward the minimum below.")]
        [SerializeField, Range(0f, 1f), FormerlySerializedAs("asteroidCollisionEnergyRetention")]
        private float asteroidCollisionNormalSpeedRetention = 0.93f;
        [Tooltip("Restitution at very high ramming power (0 = stick/slide on the normal, no rebound). Clamped vs max above.")]
        [SerializeField, Range(0f, 1f)] private float asteroidRammingMinRestitution = 0f;
        [Tooltip("Ramming power at or below this keeps max bounce. Only excess above this pulls restitution toward the minimum (so baseline ship stats stay bouncy).")]
        [SerializeField, Min(0f)] private float asteroidRammingRestitutionThreshold = 6f;
        [Tooltip("When excess ramming (above threshold) equals this value, restitution is halfway between max and min. Higher = need more investment before bounce dies off.")]
        [SerializeField, Min(0.01f), FormerlySerializedAs("asteroidRammingRestitutionReferencePower")]
        private float asteroidRammingRestitutionReferenceExcess = 14f;
        [Tooltip("Continuous push into an asteroid: asteroid damage per second per Newton of thrust along the inward normal (no extra impact spike).")]
        [SerializeField, Min(0f)] private float asteroidGrindPushToAsteroidDpsScale = 0.012f;
        [Tooltip("Ignore grind below this push (N) to avoid jitter when nearly parallel to the surface.")]
        [SerializeField, Min(0f)] private float asteroidGrindMinPushNewtons = 8f;
        [Tooltip("Cap grind DPS to the asteroid so a stuck ship cannot melt a rock instantly.")]
        [SerializeField, Min(0f)] private float asteroidGrindMaxAsteroidDps = 120f;
        [Tooltip("Min seconds between grind impact VFX, sounds, and floating damage text per asteroid contact.")]
        [SerializeField, Min(0.02f)] private float asteroidGrindFeedbackInterval = 0.1f;
        [Tooltip("Maps grind push (× ram multiplier) to collision-style impact force for VFX/sound intensity.")]
        [SerializeField, Min(0.01f)] private float asteroidGrindFeedbackForceFromPushScale = 2.75f;
        [Tooltip("Minimum impact force (N) required before showing a floating impact number on asteroid collisions.")]
        [SerializeField, Min(0f)] private float asteroidImpactForcePopupMin = 80f;
        [Tooltip("Ship collision damage = impact force * this value. Lower this to heavily scale down collision damage.")]
        [SerializeField, Min(0f)] private float asteroidImpactForceToShipDamageScale = 0.0025f;
        [Tooltip("Asteroid collision damage = impact force * this value. Tune separately from ship damage.")]
        [SerializeField, Min(0f)] private float asteroidImpactForceToAsteroidDamageScale = 0.0015f;
        [Tooltip("Use global collision VFX tuning from VisualEffectsManager instead of local values below.")]
        [SerializeField] private bool useGlobalCollisionVfxTuning = true;
        [Tooltip("Minimum impact force (N) on asteroid hits before spawning weapon-style collision impact VFX.")]
        [SerializeField, Min(0f)] private float collisionWeaponVfxMinImpactForceN = 25f;
        [Tooltip("Impact force (N) that maps asteroid collision VFX to max severity when using local tuning.")]
        [SerializeField, Min(0f)] private float collisionWeaponVfxMaxImpactForceN = 1200f;
        [Tooltip("Minimum relative speed (m/s) for ship–ship collision impact VFX (toroidal overlap uses the same threshold).")]
        [SerializeField, Min(0f)] private float collisionWeaponVfxMinRelativeSpeed = 2f;
        [Tooltip("Relative speed (m/s) that maps ship collision VFX to max severity when using local tuning.")]
        [SerializeField, Min(0f)] private float collisionWeaponVfxMaxRelativeSpeed = 35f;
        [Tooltip("Applied to the root BoxCollider size/center when component meshes scale with attribute upgrades. Slight margin reduces mesh edges passing through colliders.")]
        [SerializeField, Min(1f)] private float rootColliderAttributeScalePadding = 1.03f;
        [Tooltip("Severity 0 maps to this collision VFX scale multiplier when using local tuning.")]
        [SerializeField, Min(0.01f)] private float collisionWeaponVfxMinScaleMultiplier = 0.35f;
        [Tooltip("Severity 1 maps to this collision VFX scale multiplier when using local tuning.")]
        [SerializeField, Min(0.01f)] private float collisionWeaponVfxMaxScaleMultiplier = 1.85f;

        private bool _hasPendingAsteroidBounce;
        private Vector3 _pendingAsteroidBounceVelocity;
        /// <summary>XZ velocity at end of last FixedUpdate (pre-collision reference when relativeVelocity is ambiguous).</summary>
        private Vector3 _lastFixedPlayPlaneVelocity;
        /// <summary>Cooldown for ship–ship scrape sounds from toroidal overlap (pair key → last Time.time).</summary>
        private readonly Dictionary<ulong, float> _toroidalShipPairLastSoundTime = new Dictionary<ulong, float>();
        /// <summary>Per-asteroid instance: next Time.time allowed for grind VFX/sound/floating damage.</summary>
        private readonly Dictionary<int, float> _asteroidGrindFeedbackNextTimeByInstance = new Dictionary<int, float>();
        /// <summary>Server: Time.time when hull last took damage; regen waits until healthRegenDelayAfterDamage after this.</summary>
        private float lastHullDamageServerTime = -999f;

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

        private static int GetEnergyCostGroupKey(float energyCostPerShot)
        {
            // Quantize float energy costs to stable integer groups.
            return Mathf.RoundToInt(Mathf.Max(0f, energyCostPerShot) * 1000f);
        }

        [Header("Health")]
        [SerializeField] private float maxHealth = 100f;
        [SerializeField] private float healthRegenRate = 6f;
        [Tooltip("Seconds after hull damage before health regen applies again.")]
        [SerializeField] private float healthRegenDelayAfterDamage = 0.35f;

        [Header("Capacity (ship level only - upgrades with ship level)")]
        [SerializeField] private float gemCapacity = 100f;
        [SerializeField] private float peopleCapacity = 10f;

        [Header("Mass (affects momentum and ramming)")]
        [Tooltip("Base mass when no chassis. Chassis components override with component weights. Mass is not scaled by ship level or cards.")]
        [SerializeField] private float baseMass = 1f;
        [Tooltip("Added mass per gem carried. Ship feels heavier when full; more momentum when braking.")]
        [SerializeField] private float massPerGem = 0.008f;
        [Tooltip("Multiplies chassis component mass (or baseMass when no chassis). Does not scale gem load.")]
        [SerializeField] private float hullMassScale = 0.7f;
        [Tooltip("Base collision ramming power before level/component modifiers.")]
        [SerializeField] private float baseRammingPower = 1f;

        [Header("Energy (weapon system)")]
        [SerializeField] private float energyCapacity = 50f;
        [SerializeField] private float energyRegenRate = 5f;
        private const float ENERGY_PER_SHOT = 1f;
        private float rammingPower = 1f;

        [Header("References")]
        [SerializeField] private PlayerInputHandler inputHandler;
        [SerializeField] private Rigidbody rb;
        [Tooltip("Runtime: BankPivot under this ship (created in Awake). Do not assign the Starship root here — if this is missing or wrong, we try to find a child named BankPivot.")]
        [SerializeField] private Transform visualRoot;
        /// <summary>Banking pivot (Starship → BankPivot → Prefab). ToroidalRenderer repositions this for non-local ships.</summary>
        public Transform BankPivotTransform => visualRoot;
        [Tooltip("Multiplies the loaded ship prefab scale (chassis size in the world). Lower values make the whole ship look smaller; gem-moon dock scales apply on top of this.")]
        [SerializeField] private float shipVisualScaleMultiplier = 0.175f;

        [Header("Banking (fallback when shipData has no values)")]
        [SerializeField] private float defaultMaxBankAngle = 111f;
        [SerializeField] private float defaultBankSmoothing = 2f;

        private MaterialPropertyBlock hullColorBlock;
        private int lastVisualApplyFrame = -1;
        private GameObject lastVisualApplyPrefab;
        private ShipFamilyDefinition currentVisualFamilyDefinition;
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
        /// <summary>Per level after 1, mobility loses this fraction of the <em>base</em> stat: base − (base × this) × (level − 1).</summary>
        private const float ShipLevelMobilityPenaltyFractionPerLevel = 0.11f;

        /// <summary>Ship-level mobility: moveSpeed − (moveSpeed × 0.11) × (level−1); same pattern for rotation and per-part move.</summary>
        private static float ApplyShipLevelMobilityScale(float baseStat, float perLvlAfterOne)
        {
            if (perLvlAfterOne <= 0f || baseStat <= 0f) return baseStat;
            return baseStat - (baseStat * ShipLevelMobilityPenaltyFractionPerLevel) * perLvlAfterOne;
        }

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
        /// <summary>Max speed from best engine (single highest move speed among engines). Scaled by attr/cards.</summary>
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

        /// <summary>Projectile-only scale factor from Fire Power + Bullet Speed attributes and weapon cards (not used for weapon mesh or muzzle — those use <see cref="ApplyComponentAttributeScaling"/>).</summary>
        private float WeaponProjectileUpgradeScaleMultiplier
        {
            get
            {
                float ex = EffectiveAttributeScaleExaggeration;
                float cardWeapon = (GetCardDamageMultiplier() - 1f) * 10f + (GetCardBulletSpeedMultiplier() - 1f) * 10f;
                return 1f + ((attrFirePower.Value + attrBulletSpeed.Value) * 0.5f + cardWeapon * 0.5f) * ex;
            }
        }

        /// <summary>Same factor that makes fire rate faster (1 + attrFirePower * 0.1). Stacked with projectile upgrade scale so fire-power upgrades visibly grow bullets.</summary>
        private float FirePowerScaleFactor => 1f + attrFirePower.Value * ATTR_MULTIPLIER_PER_LEVEL;

        /// <summary>
        /// Bullet projectile scale multiplier from upgrades only: 1 with no fire-power / bullet-speed / card combat boosts.
        /// Authored cannon bulletScale and prefab VFX then define the baseline size (tuning bullet speed in ShipFamilyDefinition does not add extra multiplier here).
        /// Exaggeration applies only to the delta above 1 so the default is not permanently inflated.
        /// </summary>
        private float BulletScaleMultiplier
        {
            get
            {
                float upgradeProduct = Mathf.Max(0.01f, FirePowerScaleFactor * WeaponProjectileUpgradeScaleMultiplier);
                float exaggeration = Mathf.Max(0.5f, bulletScaleExaggeration);
                return 1f + (upgradeProduct - 1f) * exaggeration;
            }
        }

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

        /// <summary>Chassis or fallback base mass after hullMassScale (excludes gem load). Used with EffectiveMaxSpeed for ramming baseline.</summary>
        private float ScaledHullMassReference => (componentMass > 0f ? componentMass : baseMass) * hullMassScale;

        /// <summary>Mass from components + gems. Not scaled by ship level or cards.</summary>
        private float EffectiveMass
        {
            get
            {
                return Mathf.Max(0.5f, ScaledHullMassReference + currentGems.Value * massPerGem);
            }
        }

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
        private float depositAccumulator; // Accumulates toward next deposit chunk (shipLevel gems per chunk, 2 chunks/sec)
        private float lastDepositSpawnTime = -999f;
        private float peopleLoadAccumulator;
        private float peopleUnloadAccumulator;
        [SerializeField, Min(0f)] private float peopleTransferStationaryHoldSeconds = 1f;
        private float peopleTransferStationaryTimer;
        private float peopleInTransit; // People in projectiles heading to this ship (load only)

        // Galactic zoom tracking (server-side)
        private bool hadGemsWhileInOrbitThisOrbit;
        private bool triggeredGalacticZoomThisOrbit;
        private bool depositedAnyGemsThisOrbit;

        // Banking (visual lean into turn) - only used when visualRoot is set.
        private float currentBankAngle;
        private Vector3 previousForward;
        private bool bankingInitialized;
        
        // Visual pitch: prefer local X on the Prefab mesh container; roll (bank) on BankPivot. See ApplyVisualBanking.
        [Header("Visual pitch & banking")]
        [Tooltip("If off, BankPivot / Prefab get no tilt (debug or style). On by default.")]
        [SerializeField] private bool enableVisualBankingPitch = true;
        [Tooltip("Set to -1 if the nose pitches the wrong way for your mesh import.")]
        [SerializeField] private float visualPitchSign = 1f;
        [Header("Visual pitch tuning (local X)")]
        [Tooltip("Max nose-down pitch while accelerating (strongest from rest, eases off toward max speed).")]
        [SerializeField] private float maxAccelerationPitchAngle = 28f;
        [Tooltip("Max nose-up pitch when Space Brakes slow the ship (no thrust).")]
        [SerializeField] private float maxBrakePitchAngle = 24f;
        [Tooltip("Max nose-up pitch from asteroid hits (scaled by impact force).")]
        [SerializeField] private float maxCollisionPitchAngle = 36f;
        [Tooltip("How fast asteroid/brake nose-up impulse decays back toward neutral (degrees per second).")]
        [SerializeField] private float asteroidVisualPitchDecay = 150f;
        [Tooltip("Pitch smoothing speed. Higher = snappier response to new pitch (approximate lerp speed). Values below 6 are treated as 6 so motion stays visible.")]
        [SerializeField] private float collisionPitchSpeed = 10f;
        private float currentCollisionPitchAngle;
        private float targetCollisionPitchAngle;
        private float asteroidVisualPitchImpulse;
        /// <summary>Forward speed along ship facing (XZ), from previous FixedUpdate — used to derive forward acceleration for visual pitch.</summary>
        private float _visualPitchPrevFwdSpeed;
        private bool _visualPitchFwdSpeedInitialized;
        /// <summary>Forward acceleration along facing (m/s²), updated every FixedUpdate. Used for pitch when network/ownership would hide raw input.</summary>
        private float _cachedForwardAccelAlongFwd;

        /// <summary>Below this horizontal speed (m/s), visual banking ignores microscopic yaw and forward-accel pitch is not derived from velocity (stops idle rocking from mouse jitter + in-place rotation).</summary>
        private const float IdleVisualLinearSpeedThreshold = 0.12f;
        /// <summary>When nearly stationary, per-frame yaw smaller than this (degrees) does not drive bank angle.</summary>
        private const float IdleBankSignedAngleDeadbandDeg = 0.55f;

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

        /// <summary>Horizontal speed in the play plane (XZ), units/sec. Matches movement clamp / HUD speedometer.</summary>
        public float CurrentHorizontalSpeed
        {
            get
            {
                if (rb == null) return 0f;
                Vector3 v = rb.linearVelocity;
                v.y = 0f;
                return v.magnitude;
            }
        }

        /// <summary>Effective maximum movement speed cap (same units as <see cref="CurrentHorizontalSpeed"/>).</summary>
        public float MaxMoveSpeed => EffectiveMaxSpeed;
        /// <summary>Current rigidbody mass used by movement, momentum, and collisions.</summary>
        public float CurrentMass => rb != null ? rb.mass : EffectiveMass;

        /// <summary>Approximate max rate of increase of horizontal speed (engine thrust / mass). Decreases when mass rises (e.g. gems). HUD baseline for accelerometer max.</summary>
        public float MaxHorizontalAcceleration => EffectiveEngineThrust / Mathf.Max(0.5f, CurrentMass);

        /// <summary>Braking deceleration magnitude when space brakes slow the ship (matches applied brake force / mass).</summary>
        public float MaxBrakingDeceleration => brakeDeceleration;

        /// <summary>
        /// HUD: asteroid ram outcome using the same impulse → force → damage path as asteroid <see cref="OnCollisionEnter"/>,
        /// assuming <paramref name="inboundNormalSpeed"/> is your speed component into the surface (head-on: use <see cref="CurrentHorizontalSpeed"/>).
        /// </summary>
        public void GetHudAsteroidRamDamageEstimate(float inboundNormalSpeed, out float asteroidDamage, out float selfDamage)
        {
            float e = GetEffectiveAsteroidRestitution();
            float deltaNormalSpeed = (1f + e) * Mathf.Max(0f, inboundNormalSpeed);
            float mass = Mathf.Max(0.01f, CurrentMass);
            float impactImpulse = mass * deltaNormalSpeed;
            float dt = Time.fixedDeltaTime > 1e-6f ? Time.fixedDeltaTime : 0.02f;
            float impactForceNewtons = impactImpulse / Mathf.Max(0.0001f, dt);
            float ramMul = GetRammingForceMultiplier();
            asteroidDamage = Mathf.Max(0f, impactForceNewtons * ramMul * asteroidImpactForceToAsteroidDamageScale);
            selfDamage = Mathf.Max(0f, impactForceNewtons * ramMul * asteroidImpactForceToShipDamageScale);
        }

        private float GetRammingForceMultiplier()
        {
            return 1f + Mathf.Max(0f, rammingPower) * 0.1f;
        }

        private float GetEffectiveAsteroidRestitution()
        {
            return AsteroidRammingBehavior.ComputeRestitution(
                asteroidCollisionNormalSpeedRetention,
                asteroidRammingMinRestitution,
                Mathf.Max(0f, rammingPower),
                asteroidRammingRestitutionThreshold,
                asteroidRammingRestitutionReferenceExcess);
        }

        /// <summary>Thrust/drive force in the play plane (N): player engine thrust or AI mass × drive acceleration.</summary>
        private Vector3 GetDrivePushForceXZ()
        {
            if (rb == null) return Vector3.zero;
            var ai = GetComponent<AIStarshipController>();
            if (ai != null)
            {
                Vector3 a = ai.GetDriveAccelerationXZ();
                float m = Mathf.Max(0.5f, rb.mass);
                return a * m;
            }
            if (moveDirection.magnitude > 0.1f)
                return moveDirection * EffectiveEngineThrust;
            return Vector3.zero;
        }

        /// <summary>
        /// HUD: stats for the highest-DPS cannon (same damage and fire-rate basis as firing). <paramref name="damagePerBullet"/> is one projectile;
        /// <paramref name="damagePerSecond"/> includes fixed multi-projectile spreads per trigger pull.
        /// </summary>
        public bool TryGetHudPrimaryBulletStats(out float damagePerBullet, out float shotsPerSecond, out float damagePerSecond)
        {
            damagePerBullet = 0f;
            shotsPerSecond = 0f;
            damagePerSecond = 0f;
            var wc = bulletConfig ?? EffectiveWeaponConfig;
            if (wc == null || wc.cannons == null || wc.cannons.Count == 0) return false;

            bool any = false;
            float bestDps = 0f;
            foreach (var c in wc.cannons)
            {
                if (c == null) continue;
                float rate = Mathf.Max(0f, c.fireRate * (1f + attrFireRate.Value * ATTR_MULTIPLIER_PER_LEVEL));
                int pellets = 1;
                if (c.spreadType == CannonSpreadType.FixedSpread && c.spreadProjectileCount > 1)
                    pellets = Mathf.Max(1, c.spreadProjectileCount);
                float d = Mathf.Max(0f, c.damagePerBullet);
                float dps = d * pellets * rate;
                if (!any || dps > bestDps)
                {
                    any = true;
                    bestDps = dps;
                    damagePerBullet = d;
                    shotsPerSecond = rate;
                    damagePerSecond = dps;
                }
            }
            return any;
        }

        /// <summary>Raw chassis/base stat with no attribute upgrades and no card bonuses. Used to scale components by percentage increase (current/base).</summary>
        private float BaseMaxHealthNoAttr => Mathf.Max(1f, maxHealth);
        private float BaseGemCapacityNoAttr => Mathf.Max(0.1f, gemCapacity);
        private float BasePeopleCapacityNoAttr => Mathf.Max(0.1f, peopleCapacity);
        private float BaseEnergyCapacityNoAttr => Mathf.Max(0.1f, energyCapacity);
        private float BaseEnergyRegenNoAttr => Mathf.Max(0.01f, energyRegenRate);
        private float BaseRotationSpeedNoAttr
        {
            get
            {
                float chassis = rotationSpeedFromShipFamilyDefinition
                    ? Mathf.Max(1f, rotationSpeed) * ShipTurnDefinitionToDegreesPerSecond
                    : rotationSpeed;
                return Mathf.Max(1f, chassis);
            }
        }
        private float BaseHealthRegenNoAttr => Mathf.Max(0.01f, healthRegenRate);
        private float BaseMaxSpeedNoAttr
        {
            get
            {
                float baseSpeed = componentEngineMaxSpeed > 0f ? componentEngineMaxSpeed : engineThrust * 0.5f;
                // Match EffectiveMaxSpeed's chassis floor (Mathf.Max(2f, …)) so thruster visual ratio stays ~1 when authored cap < 2.
                return Mathf.Max(2f, baseSpeed);
            }
        }
        private float BaseHorizontalAccelerationNoAttr
        {
            get
            {
                float baseThrust = componentEngineThrust > 0f ? componentEngineThrust : engineThrust;
                const float ENGINE_THRUST_VISIBILITY = 10f;
                float baseMassNoGems = Mathf.Max(0.5f, (componentMass > 0f ? componentMass : baseMass) * hullMassScale);
                return Mathf.Max(0.01f, (baseThrust * ENGINE_THRUST_VISIBILITY) / baseMassNoGems);
            }
        }
        private float BaseDamageMultiplierNoAttr => 1f;
        private float BaseSpeedMultiplierNoAttr => 1f;
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
                float baseWithCards = peopleCapacity + GetCardPeopleCapacityAdd();
                float attrScale = 1f + attrPeopleCapacity.Value * ATTR_MULTIPLIER_PER_LEVEL;
                return Mathf.Max(0f, baseWithCards * attrScale);
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
                    var card = Systems.CardShopSystem.Instance.GetCardByIdForShip(this, equippedCardIds[i].cardId.ToString());
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
        /// <summary>True when this ship is gem-moon docked and the dock target is <paramref name="planet"/>.</summary>
        public bool IsGemMoonDockedAtPlanet(Planet planet)
        {
            if (planet == null || !gemMoonDocked.Value) return false;
            var planetNo = planet.GetComponent<NetworkObject>();
            if (planetNo == null) return false;
            return gemMoonPlanetNetworkObjectId.Value == planetNo.NetworkObjectId;
        }
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

            if (_isAIControlled)
                EnemyShipWorldStatsPanel.CreateAsStarshipChild(this);

            baseLocalScale = transform.localScale;

            if (rb == null) rb = GetComponent<Rigidbody>();
            rootCollider = GetComponent<Collider>();
            TryCaptureRootBoxColliderBaseline();
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

        /// <summary>Ensures <see cref="visualRoot"/> points at the runtime BankPivot. Fixes inspector mis-assignments (e.g. to the Starship root), which would skip all banking/pitch.</summary>
        private void ResolveBankPivotFromHierarchy()
        {
            if (visualRoot != null && visualRoot != transform && visualRoot.parent == transform)
                return;
            Transform bp = transform.Find("BankPivot");
            if (bp != null)
                visualRoot = bp;
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

        private void OnDestroy()
        {
            // Remove from global registry if present
            AllStarships.Remove(this);
            equippedCardIds?.Dispose();
            // Cancel any pending respawn invokes
            CancelInvoke(nameof(DelayedRespawnAfterDeath));
        }

        private void ApplyHullIdentityColor()
        {
            // Prefer authored per-team materials from ShipFamilyDefinition over runtime color tinting.
            if (ApplyTeamMaterialsFromShipFamily())
                return;

            if (shipData == null || shipData.shipColor == Color.white) return;
            Renderer mr = visualRoot != null ? visualRoot.GetComponentInChildren<Renderer>() : null;
            if (mr == null) mr = GetComponent<Renderer>();
            if (mr == null) return;
            if (hullColorBlock == null) hullColorBlock = new MaterialPropertyBlock();
            mr.GetPropertyBlock(hullColorBlock);
            hullColorBlock.SetColor("_BaseColor", shipData.shipColor);
            mr.SetPropertyBlock(hullColorBlock);
        }

        private bool ApplyTeamMaterialsFromShipFamily()
        {
            if (currentVisualFamilyDefinition == null)
                return false;

            List<Material> teamMats = currentVisualFamilyDefinition.GetMaterialsForTeam(shipTeam.Value);
            if (teamMats == null || teamMats.Count == 0)
                return false;

            Transform root = GetPrefabTransform();
            if (root == null)
                return false;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0)
                return false;

            bool appliedAny = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                if (r is ParticleSystemRenderer) continue; // Never recolor VFX/jet flames via ship team materials.
                if (r.GetComponentInParent<EnemyShipWorldStatsPanel>() != null) continue;

                Material[] current = r.sharedMaterials;
                if (current == null || current.Length == 0)
                    continue;

                Material[] replaced = new Material[current.Length];
                for (int s = 0; s < current.Length; s++)
                {
                    Material chosen = teamMats[s % teamMats.Count];
                    replaced[s] = chosen != null ? chosen : current[s];
                }

                r.sharedMaterials = replaced;
                appliedAny = true;
            }

            return appliedAny;
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

            // Team is server-authored; hull materials are applied on server in AssignTeamAndStartInOrbit. Owning client must
            // refresh when shipTeam replicates (otherwise local player stays neutral while remotes see correct team color).
            shipTeam.OnValueChanged += OnShipTeamValueChanged;
            ApplyHullIdentityColor();

            // Hide the ship until the player picks a team — avoids showing a neutral ship in the team-select lobby.
            // OnShipTeamValueChanged re-enables the visuals when shipTeam is assigned. AI ships skip this (they always have a team).
            if (!_isAIControlled && shipTeam.Value == TeamManager.Team.None)
                SetShipBodyVisibleLocal(false);

            // Ship loadout grid is shown by OrbitStationUI when in orbit; no separate ShipCardGridUI needed.
        }

        private void OnShipTeamValueChanged(TeamManager.Team previous, TeamManager.Team current)
        {
            ApplyHullIdentityColor();
            if (!_isAIControlled)
                SetShipBodyVisibleLocal(current != TeamManager.Team.None);
        }

        public override void OnNetworkDespawn()
        {
            shipTeam.OnValueChanged -= OnShipTeamValueChanged;
            base.OnNetworkDespawn();
        }

        /// <summary>Server only: called by NetworkGameManager when team is assigned (after client connect). Sets team and starts in orbit.</summary>
        public void AssignTeamAndStartInOrbit(TeamManager.Team team)
        {
            if (!IsServer) return;
            shipTeam.Value = team;
            ApplyHullIdentityColor();
            StartInOrbitAroundHomePlanet();
        }

        /// <summary>Called from team selection UI. Server validates the sender matches this ship's owner (works even if <see cref="IsOwner"/> is briefly false before NGO sync).</summary>
        public void RequestJoinTeamFromClient(TeamManager.Team preferredTeam)
        {
            RequestJoinTeamServerRpc(preferredTeam);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestJoinTeamServerRpc(TeamManager.Team preferredTeam, ServerRpcParams rpcParams = default)
        {
            if (TeamManager.Instance == null) return;
            // Use RPC sender as the player id. Do not compare to OwnerClientId — on connect/Relay, ownership
            // can lag the sender id for a frame and the old check dropped the RPC with no client feedback.
            ulong sender = rpcParams.Receive.SenderClientId;
            TeamManager.Instance.ApplyTeamChoiceFromServer(sender, preferredTeam);
            // If server lookup missed the player object (wrong Singleton / late join), still apply on this RPC target ship.
            // Only when the request succeeded (assigned team matches pick) so failed team switches do not teleport.
            TeamManager.Team assigned = TeamManager.Instance.GetPlayerTeam(sender);
            if (assigned != TeamManager.Team.None && assigned == preferredTeam)
                AssignTeamAndStartInOrbit(assigned);
        }

        /// <summary>Server only: set team without repositioning (for AI ships that are already placed).</summary>
        public void AssignTeamOnly(TeamManager.Team team)
        {
            if (!IsServer) return;
            shipTeam.Value = team;
            ApplyHullIdentityColor();
        }

        /// <summary>Server: position ship in orbit around its team's home planet at spawn.</summary>
        private void StartInOrbitAroundHomePlanet()
        {
            if (shipTeam.Value == TeamManager.Team.None || rb == null) return;
            HomePlanet home = null;
            foreach (var hp in HomePlanet.AllHomePlanets)
            {
                if (hp == null) continue;
                if (hp.AssignedTeam == shipTeam.Value) { home = hp; break; }
            }
            if (home == null) return;
            if (!TryComputeOrbitSpawnPose(home, out Vector3 orbitPos, out Vector3 vel, out Quaternion rot)) return;
            ApplyServerOrbitSpawnPoseAndNotifyOwner(orbitPos, vel, rot);
        }

        /// <summary>Computes spawn pose in the home orbit band. Returns false for AI ships (placed elsewhere).</summary>
        private bool TryComputeOrbitSpawnPose(Planet planet, out Vector3 orbitPos, out Vector3 linearVelocity, out Quaternion rotation)
        {
            orbitPos = default;
            linearVelocity = default;
            rotation = default;
            if (planet == null || rb == null) return false;
            if (GetComponent<TitanOrbit.AI.AIShipMarker>() != null) return false;

            float orbitRadius = planet.PlanetSize * 0.6f;
            Vector3 planetPos = planet.transform.position;
            orbitPos = planetPos + new Vector3(orbitRadius, 0f, 0f);
            orbitPos.y = FIXED_Y_POSITION;

            float innerWorld = planet.PlanetSize * 0.5f;
            float outerWorld = planet.PlanetSize * planet.GetOrbitZoneOuterRadiusLocal();
            float targetSpeed = GetOrbitTargetSpeed(planet, orbitRadius, innerWorld, outerWorld);

            linearVelocity = new Vector3(0f, 0f, -targetSpeed);
            Vector3 horizForward = linearVelocity.sqrMagnitude > 0.0001f ? linearVelocity.normalized : Vector3.forward;
            rotation = Quaternion.LookRotation(horizForward, Vector3.up);
            return true;
        }

        /// <summary>
        /// Server: applies orbit pose to this instance. On a dedicated server, player ships simulate on the owner client only —
        /// writing <see cref="Rigidbody"/> here does not move the owning client, so we RPC the pose (and NetworkTransform teleport).
        /// </summary>
        private void ApplyServerOrbitSpawnPoseAndNotifyOwner(Vector3 orbitPos, Vector3 vel, Quaternion rot)
        {
            if (!IsServer || rb == null) return;

            rb.position = orbitPos;
            rb.linearVelocity = vel;
            rb.rotation = rot;
            rb.angularVelocity = Vector3.zero;
            currentVelocity = vel;

            if (_isAIControlled) return;

            var nm = NetworkManager.Singleton;
            bool dedicatedServer = nm != null && nm.IsServer && !nm.IsClient;
            if (dedicatedServer && IsSpawned)
                SnapOwnerShipPhysicsClientRpc(orbitPos, vel, rot, OwnerOnlyClientRpcParams);
            else if (nm != null && nm.IsHost && IsServer)
            {
                var nt = GetComponent<NetworkTransform>();
                if (nt != null)
                    nt.SetState(orbitPos, rot, transform.localScale, teleportDisabled: false);
            }
        }

        /// <summary>Owner client: authoritative physics pose after server placed us in orbit (dedicated server).</summary>
        [ClientRpc]
        private void SnapOwnerShipPhysicsClientRpc(Vector3 position, Vector3 linearVelocity, Quaternion rotation, ClientRpcParams clientRpcParams = default)
        {
            if (!IsOwner || rb == null) return;
            rb.position = position;
            rb.linearVelocity = linearVelocity;
            rb.rotation = rotation;
            rb.angularVelocity = Vector3.zero;
            currentVelocity = linearVelocity;
            var nt = GetComponent<NetworkTransform>();
            if (nt != null)
                nt.SetState(position, rotation, transform.localScale, teleportDisabled: false);
        }

        private void Update()
        {
            // Server: regen for ALL ships (including AI) - run before IsOwner check
            if (IsServer && !isDead.Value)
            {
                TryDieIfHullAndGemsDepleted(0);
            }
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
            // When chassis index/id is set or synced from the server, every peer must build the mesh (not just the owner).
            // Otherwise other players see an empty BankPivot: invisible ship while bullets/weapons still spawn from the server.
            if (currentChassisIndex.Value >= 0 && currentChassisIndex.Value != _lastAppliedChassisIndex && CardShopSystem.Instance != null)
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
                        _orbitUiVisible = true;
                    }
                    else if (!shouldShowOrbitUI && _orbitUiVisible)
                    {
                        s_cachedOrbitUI.Hide();
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
        }

        private void LateUpdate()
        {
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
            ResolveBankPivotFromHierarchy();
            if (!enableVisualBankingPitch) return;
            if (visualRoot == null || visualRoot == transform || isDead.Value || rb == null) return;
            ApplyVisualBanking(Time.deltaTime);
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
            _cachedCardGemDepositSpeedMultiplier = 1f;
            _cachedCardPeopleTransferSpeedMultiplier = 1f;

            if (equippedCards == null) return;
            foreach (var card in equippedCards)
            {
                if (card == null) continue;
                // Card stats use authored values only; level gates equipping, rarity affects shop drop weights — not combat math.
                _cachedCardMovementSpeedAdd += card.movementSpeedAdd;
                _cachedCardRotationSpeedAdd += card.rotationSpeedAdd;
                _cachedCardMaxHealthAdd += card.maxHealthAdd;
                _cachedCardHealthRegenAdd += card.healthRegenAdd;
                _cachedCardEnergyCapacityAdd += card.energyCapacityAdd;
                _cachedCardEnergyRegenAdd += card.energyRegenAdd;
                // Gem and people capacity are discrete in gameplay; round so fractional card data still applies cleanly.
                _cachedCardGemCapacityAdd += Mathf.Round(card.gemCapacityAdd);
                _cachedCardPeopleCapacityAdd += Mathf.Round(card.peopleCapacityAdd);
                if (card.damageMultiplier > 0f)
                    _cachedCardDamageMultiplier *= card.damageMultiplier;
                if (card.bulletSpeedMultiplier > 0f)
                    _cachedCardBulletSpeedMultiplier *= card.bulletSpeedMultiplier;
                if (card.gemDepositSpeedMultiplier > 0f)
                    _cachedCardGemDepositSpeedMultiplier *= card.gemDepositSpeedMultiplier;
                if (card.peopleTransferSpeedMultiplier > 0f)
                    _cachedCardPeopleTransferSpeedMultiplier *= card.peopleTransferSpeedMultiplier;
            }
        }

        /// <summary>Scale ship components by effective stat vs chassis baseline (no cards, no ability upgrades). E.g. +40 gems on 40 base → ratio 2 → larger wings; ability levels scale the same way.</summary>
        private void ApplyComponentAttributeScaling()
        {
            float vis = Mathf.Max(0.2f, componentScaleVisibility);

            // Stat ratios: current (chassis + cards, then × ability multiplier) / raw chassis. Ratio = 1 with no cards and no ability upgrades.
            float ratioHealth = MaxHealth / BaseMaxHealthNoAttr;
            float ratioGem = GemCapacity / BaseGemCapacityNoAttr;
            float ratioPeople = PeopleCapacity / BasePeopleCapacityNoAttr;
            float ratioEnergyCap = EffectiveEnergyCapacity / BaseEnergyCapacityNoAttr;
            float ratioEnergyRegen = EffectiveEnergyRegen / BaseEnergyRegenNoAttr;
            float ratioTurn = EffectiveRotationSpeed / BaseRotationSpeedNoAttr;
            float ratioRegen = EffectiveHealthRegen / BaseHealthRegenNoAttr;
            float ratioMove = EffectiveMaxSpeed / BaseMaxSpeedNoAttr;
            // Use EffectiveMass, not rb.mass: mass is applied in FixedUpdate; LateUpdate can run first and leave prefab mass, inflating accel ratio and engine scale for a frame.
            float massForVisualAccel = Mathf.Max(0.5f, EffectiveMass);
            float currentAccelForVisualScale = EffectiveEngineThrust / massForVisualAccel;
            float ratioAcceleration = currentAccelForVisualScale / BaseHorizontalAccelerationNoAttr;
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
            // Engines now represent acceleration; keep their visual scaling tied to acceleration changes, not top speed.
            float engineScale = Mathf.Max(StatScale(ratioAcceleration, vis), StatScale((ratioAcceleration + ratioHealth) * 0.5f, vis, 0.85f));
            // Thrusters are movement-speed related; blend move + turn so speed upgrades are visible on thrusters.
            float thrusterScale = Mathf.Max(StatScale(ratioMove, vis, 0.9f), StatScale(ratioTurn, vis, 0.8f));
            float partScale = Mathf.Max(StatScale(avgPart, vis), StatScale(Mathf.Max(ratioGem, ratioHealth), vis, 0.85f));

            wingScale = Mathf.Min(wingScale, 3.5f);
            cockpitScale = Mathf.Min(cockpitScale, 3f);
            weaponScale = Mathf.Min(weaponScale, 3f);
            engineScale = Mathf.Min(engineScale, 2f);
            thrusterScale = Mathf.Min(thrusterScale, 2.5f);
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

            // Root Rigidbody has no "size" — physics uses the Starship BoxCollider. Child meshes scale here; match collider so hull/wings do not tunnel.
            float maxAttrVisualScale = Mathf.Max(1f, wingScale, cockpitScale, weaponScale, engineScale, thrusterScale, partScale);
            ApplyRootColliderForAttributeScale(maxAttrVisualScale);
        }

        private void TryCaptureRootBoxColliderBaseline()
        {
            if (rootColliderBaselineCaptured) return;
            if (rootCollider == null) rootCollider = GetComponent<Collider>();
            if (rootCollider is BoxCollider box)
            {
                rootColliderBaselineSize = box.size;
                rootColliderBaselineCenter = box.center;
                rootColliderBaselineCaptured = true;
            }
        }

        /// <summary>Scales the authored root BoxCollider so it stays aligned with attribute-driven component mesh scaling.</summary>
        private void ApplyRootColliderForAttributeScale(float maxComponentScaleFactor)
        {
            TryCaptureRootBoxColliderBaseline();
            if (!rootColliderBaselineCaptured) return;
            if (rootCollider == null) rootCollider = GetComponent<Collider>();
            if (!(rootCollider is BoxCollider box)) return;

            float m = Mathf.Max(0.01f, maxComponentScaleFactor) * Mathf.Max(1f, rootColliderAttributeScalePadding);
            box.size = rootColliderBaselineSize * m;
            box.center = rootColliderBaselineCenter * m;
        }

        private static readonly float ENGINE_VFX_SPEED_THRESHOLD = 0.5f;
        private static readonly float THRUSTER_VFX_ANGULAR_THRESHOLD_RAD = 0.15f;
        private static readonly float ENGINE_VFX_EMISSION_RATE = 18f;
        private static readonly float THRUSTER_VFX_EMISSION_RATE = 15f;
        private static readonly string[] VfxColorNames = { "Blue", "Green", "Orange", "Purple", "Red", "Yellow" };
        private bool lastEngineVfxMoving = false;
        private bool lastThrusterVfxTurning = false;
        private float thrusterVfxBlend = 0f;

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
            bool accelerating = moving && IsActivelyAccelerating();
            bool showThrusters = useThrusterVfxForAcceleration ? accelerating : turning;
            float targetThrusterBlend = showThrusters ? 1f : 0f;
            float transitionSpeed = Mathf.Max(0.01f, thrusterVfxTransitionSpeed);
            thrusterVfxBlend = Mathf.MoveTowards(thrusterVfxBlend, targetThrusterBlend, transitionSpeed * Time.deltaTime);
            bool thrusterTransitionActive = Mathf.Abs(thrusterVfxBlend - targetThrusterBlend) > 0.0001f;
            if (moving == lastEngineVfxMoving && showThrusters == lastThrusterVfxTurning && !thrusterTransitionActive)
                return;
            lastEngineVfxMoving = moving;
            lastThrusterVfxTurning = showThrusters;

            for (int i = 0; i < engineVfxInstances.Count; i++)
            {
                GameObject go = engineVfxInstances[i];
                if (go != null) go.SetActive(moving);
            }
            for (int i = 0; i < thrusterVfxInstances.Count; i++)
            {
                GameObject go = thrusterVfxInstances[i];
                if (go != null)
                {
                    float scaleLerp = Mathf.Lerp(Mathf.Clamp01(thrusterVfxIdleScale), 1f, thrusterVfxBlend);
                    go.transform.localScale = Vector3.one * scaleLerp;
                    bool visible = scaleLerp > 0.0005f;
                    if (go.activeSelf != visible)
                        go.SetActive(visible);
                }
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
                emission.rateOverTime = THRUSTER_VFX_EMISSION_RATE * thrusterVfxBlend;
                if (thrusterVfxBlend > 0.001f && !ps.isPlaying) ps.Play();
            }
        }

        private bool IsActivelyAccelerating()
        {
            if (_isAIControlled)
            {
                if (rb == null) return false;
                Vector3 v = rb.linearVelocity;
                v.y = 0f;
                if (v.sqrMagnitude < 0.01f) return false;
                v.Normalize();
                Vector3 fwd = transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) return false;
                fwd.Normalize();
                return Vector3.Dot(v, fwd) > 0.1f;
            }

            if (inputHandler == null)
                return false;

            return inputHandler.MoveForwardPressed;
        }

        private GameObject ResolveThrusterVfxPrefabForTransform(Transform thrusterTransform)
        {
            if (thrusterJetFlameBank != null && thrusterJetFlameBank.Count > 0)
            {
                string color = ExtractColorNameFromText(thrusterTransform != null ? thrusterTransform.name : null);
                if (!string.IsNullOrEmpty(color))
                {
                    for (int i = 0; i < thrusterJetFlameBank.Count; i++)
                    {
                        var entry = thrusterJetFlameBank[i];
                        if (entry == null || entry.prefab == null || string.IsNullOrEmpty(entry.colorName)) continue;
                        if (string.Equals(entry.colorName, color, System.StringComparison.OrdinalIgnoreCase))
                            return entry.prefab;
                    }
                }

                // Fallback: use first configured JetFlame so a ship still gets thruster VFX even if names don't encode color.
                for (int i = 0; i < thrusterJetFlameBank.Count; i++)
                {
                    var entry = thrusterJetFlameBank[i];
                    if (entry != null && entry.prefab != null)
                        return entry.prefab;
                }
            }

            return thrusterVfxPrefab;
        }

        private static string ExtractColorNameFromText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            for (int i = 0; i < VfxColorNames.Length; i++)
            {
                string color = VfxColorNames[i];
                if (value.IndexOf(color, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return color;
            }

            return null;
        }

        /// <summary>Maps linear dock depth (0 outer ring → 1 surface) to an ease-in-out curve for approach/scale/orientation.</summary>
        private static float GemMoonDockEaseInOut(float t)
        {
            t = Mathf.Clamp01(t);
            return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;
        }

        /// <summary>
        /// Sets <see cref="targetCollisionPitchAngle"/> from thrust/brake/asteroid impulses (visual only).
        /// Uses <see cref="_cachedForwardAccelAlongFwd"/> from FixedUpdate plus local input when available.
        /// </summary>
        private void UpdateVisualPitchTarget()
        {
            Vector3 vel = rb.linearVelocity;
            vel.y = 0f;
            float speed = vel.magnitude;
            float maxSp = EffectiveMaxSpeed;

            if (asteroidVisualPitchImpulse != 0f)
                asteroidVisualPitchImpulse = Mathf.MoveTowards(asteroidVisualPitchImpulse, 0f, asteroidVisualPitchDecay * Time.deltaTime);

            float accelPitch = 0f;
            float brakePitch = 0f;

            // Physics-driven pitch (all ships): works for remotes, host, and cases where IsOwner/input timing is wrong.
            float fwdA = _cachedForwardAccelAlongFwd;
            // Lower refs = reach near-max tilt at moderate forward accel/decel (m/s²).
            const float fwdAccelRef = 14f;
            const float fwdDecelRef = 10f;
            if (maxSp > 0.01f)
            {
                if (fwdA > 0.25f)
                {
                    float t = Mathf.Clamp01((fwdA - 0.25f) / fwdAccelRef);
                    accelPitch = maxAccelerationPitchAngle * t * Mathf.Max(0f, 1f - speed / maxSp);
                }
                if (fwdA < -0.22f && speed > 0.2f)
                {
                    float t = Mathf.Clamp01((-fwdA - 0.22f) / fwdDecelRef);
                    brakePitch = -maxBrakePitchAngle * t * Mathf.Clamp01(speed / Mathf.Max(2f, maxSp * 0.45f));
                }
            }

            // Local human: input-driven pitch. When NGO is listening and spawned, require owner/local player; otherwise allow input (offline / not yet spawned).
            bool nmOk = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
            bool useInputPitch = !_isAIControlled && inputHandler != null;
            if (useInputPitch && nmOk && IsSpawned)
                useInputPitch = IsOwner || IsLocalPlayerShip();
            if (useInputPitch)
            {
                bool thrusting = inputHandler.MoveForwardPressed;
                bool brakesOn = (inputHandler as PlayerInputHandler)?.SpaceBrakesEnabled ?? true;

                if (thrusting && maxSp > 0.01f)
                {
                    float ramp = Mathf.Max(0f, 1f - speed / maxSp);
                    accelPitch = Mathf.Max(accelPitch, maxAccelerationPitchAngle * ramp);
                }
                if (!thrusting && brakesOn && speed > 0.15f)
                {
                    float denom = Mathf.Max(3f, maxSp * 0.5f);
                    float b = -maxBrakePitchAngle * Mathf.Clamp01(speed / denom);
                    brakePitch = Mathf.Min(brakePitch, b);
                }
            }

            float pitchClamp = Mathf.Max(maxCollisionPitchAngle, maxAccelerationPitchAngle, maxBrakePitchAngle);
            float combined = accelPitch + brakePitch + asteroidVisualPitchImpulse;
            targetCollisionPitchAngle = Mathf.Clamp(combined, -pitchClamp, pitchClamp);
        }

        /// <summary>Called from FixedUpdate finally so every ship (owner, proxy, AI) gets consistent forward acceleration for visuals.</summary>
        private void CacheVisualForwardAccelForPitch()
        {
            if (rb == null || isDead.Value)
            {
                _cachedForwardAccelAlongFwd = 0f;
                if (isDead.Value)
                    _visualPitchFwdSpeedInitialized = false;
                return;
            }

            Vector3 v = rb.linearVelocity;
            v.y = 0f;
            Vector3 ff = rb.rotation * Vector3.forward;
            ff.y = 0f;
            if (ff.sqrMagnitude < 1e-8f)
            {
                _cachedForwardAccelAlongFwd = 0f;
                return;
            }
            ff.Normalize();
            float fwdSp = Vector3.Dot(v, ff);
            float dt = Time.fixedDeltaTime;
            if (v.magnitude < IdleVisualLinearSpeedThreshold)
            {
                _cachedForwardAccelAlongFwd = 0f;
                _visualPitchPrevFwdSpeed = fwdSp;
                _visualPitchFwdSpeedInitialized = true;
                return;
            }
            if (_visualPitchFwdSpeedInitialized)
                _cachedForwardAccelAlongFwd = (fwdSp - _visualPitchPrevFwdSpeed) / Mathf.Max(1e-5f, dt);
            else
                _cachedForwardAccelAlongFwd = 0f;
            _visualPitchFwdSpeedInitialized = true;
            _visualPitchPrevFwdSpeed = fwdSp;
        }

        /// <summary>
        /// Updates banking (roll) from turn rate and blends in visual pitch (acceleration / brakes / asteroid).
        /// Must run on a child of the root—never on the root itself (physics/NetworkTransform would overwrite).
        /// </summary>
        private void ApplyVisualBanking(float dt)
        {
            if (!enableVisualBankingPitch || visualRoot == null || visualRoot == transform || rb == null) return;
            if (gemMoonDocked.Value)
            {
                if (asteroidVisualPitchImpulse != 0f)
                    asteroidVisualPitchImpulse = Mathf.MoveTowards(asteroidVisualPitchImpulse, 0f, asteroidVisualPitchDecay * dt);
                return;
            }

            Vector3 fwd = rb.rotation * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) return;
            fwd.Normalize();

            dt = Mathf.Max(dt, 0.0001f);
            UpdateVisualPitchTarget();

            Transform prefabNode = GetPrefabTransform();
            bool pitchOnPrefabChild = prefabNode != null && prefabNode.parent == visualRoot;

            if (!bankingInitialized)
            {
                previousForward = fwd;
                currentBankAngle = 0f;
                bankingInitialized = true;
                visualRoot.localRotation = Quaternion.identity;
                if (pitchOnPrefabChild)
                    prefabNode.localRotation = Quaternion.identity;
                return;
            }

            float maxBank = shipData != null ? shipData.maxBankAngle : defaultMaxBankAngle;
            float bankSmooth = shipData != null ? shipData.bankSmoothing : defaultBankSmoothing;
            // Roll (Z): bank whenever turning; amount based on turn rate, independent of forward speed.
            float signedAngle = Vector3.SignedAngle(previousForward, fwd, Vector3.up);
            Vector3 velFlat = rb.linearVelocity;
            velFlat.y = 0f;
            if (velFlat.sqrMagnitude < IdleVisualLinearSpeedThreshold * IdleVisualLinearSpeedThreshold
                && Mathf.Abs(signedAngle) < IdleBankSignedAngleDeadbandDeg)
                signedAngle = 0f;
            float angularVelDegPerSec = Mathf.Abs(signedAngle) / dt;
            float turnRatio = Mathf.Clamp01(angularVelDegPerSec / EffectiveRotationSpeed);
            float targetBankAngle = Mathf.Sign(signedAngle) * turnRatio * maxBank;
            float bankT = 1f - Mathf.Exp(-bankSmooth * dt);
            currentBankAngle = Mathf.Lerp(currentBankAngle, targetBankAngle, bankT);

            // Smooth visual pitch toward target (floor speed so low inspector values still read as motion)
            float pitchT = 1f - Mathf.Exp(-Mathf.Max(collisionPitchSpeed, 6f) * dt);
            currentCollisionPitchAngle = Mathf.Lerp(currentCollisionPitchAngle, targetCollisionPitchAngle, pitchT);

            float pitchClamp = Mathf.Max(maxCollisionPitchAngle, maxAccelerationPitchAngle, maxBrakePitchAngle);
            currentCollisionPitchAngle = Mathf.Clamp(currentCollisionPitchAngle, -pitchClamp, pitchClamp);

            float pitchDeg = currentCollisionPitchAngle * visualPitchSign;
            float rollDeg = -currentBankAngle;

            // Roll on BankPivot; pitch on Prefab child so the imported hull actually tilts (mesh lives under Prefab).
            if (pitchOnPrefabChild)
            {
                visualRoot.localRotation = Quaternion.Euler(0f, 0f, rollDeg);
                prefabNode.localRotation = Quaternion.Euler(pitchDeg, 0f, 0f);
            }
            else
            {
                visualRoot.localRotation = Quaternion.Euler(pitchDeg, 0f, rollDeg);
            }

            previousForward = fwd;
        }

        private void FixedUpdate()
        {
            if (rb == null) return;

            try
            {

            // Apply asteroid bounce before movement forces so thrust does not overwrite the rebound.
            if (_hasPendingAsteroidBounce)
            {
                bool bounceAuth = (_isAIControlled && IsServer) || (!_isAIControlled && IsOwner);
                if (bounceAuth)
                {
                    Vector3 bv = _pendingAsteroidBounceVelocity;
                    rb.linearVelocity = new Vector3(bv.x, 0f, bv.z);
                    currentVelocity = rb.linearVelocity;
                }
                _hasPendingAsteroidBounce = false;
            }

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

            if (!gemMoonDocked.Value)
                TickToroidalShipVsShipCollision();

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

            }
            finally
            {
                if (rb != null)
                {
                    Vector3 lv = rb.linearVelocity;
                    _lastFixedPlayPlaneVelocity = new Vector3(lv.x, 0f, lv.z);
                }
                CacheVisualForwardAccelForPitch();
            }
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
            if (IsGemCollectionSuppressed) return;
            if (currentGems.Value >= GemCapacity) return;
            if (gemMoonDocked.Value) return;

            // Throttle attraction work across frames to reduce CPU cost.
            if (((Time.frameCount + GetInstanceID()) & 1) != 0)
                return;

            if (TitanOrbit.Entities.Gem.AllGems == null || TitanOrbit.Entities.Gem.AllGems.Count == 0)
                return;

            Vector3 shipPos = rb != null ? rb.position : transform.position;
            bool inOrbitZone = currentOrbitPlanet != null;
            float searchRadius = inOrbitZone ? 4.5f : 2.5f;
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

            // Shooting: owner sends world-space muzzles + velocity; player ships never snap to server-side weapon rigs.
            bool uiBlocksShot = IsPointerOverUI();
            MobileInputHandler mobileHud = MobileInputHandler.Resolve();
            if (mobileHud != null && (mobileHud.ShootButtonPressed
                || (Application.isMobilePlatform && inputHandler.ShootPressed)))
                uiBlocksShot = false;
            if (inputHandler.ShootPressed && CanFire() && !uiBlocksShot)
            {
                Vector3 dir = transform.forward;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward;
                else dir.Normalize();
                Vector3 shipVelForFire = rb != null ? rb.linearVelocity : Vector3.zero;
                shipVelForFire.y = 0f;
                TryBuildOwnerReportedCannonBallisticsForFireRpc(out Vector3[] cannonOrigins, out Vector3[] cannonForwards);
                FireServerRpc(transform.position, dir, shipVelForFire, cannonOrigins, cannonForwards);
                // Owner: immediate cosmetic tracers (host + dedicated client) so shots are not gated on RPC latency.
                if (ShouldSpawnOwnerPredictedBulletTracers())
                    TrySpawnOwnerPredictedBulletVolley(transform.position, dir, shipVelForFire, cannonOrigins, cannonForwards);
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

        /// <summary>Resolves screen position for UI raycasts (mouse, else first active touch).</summary>
        private static bool TryGetPrimaryPointerScreenPosition(out Vector2 screenPos)
        {
            if (Mouse.current != null)
            {
                screenPos = Mouse.current.position.ReadValue();
                return true;
            }
            if (UnityEngine.Input.touchCount > 0)
            {
                for (int i = 0; i < UnityEngine.Input.touchCount; i++)
                {
                    UnityEngine.Touch t = UnityEngine.Input.GetTouch(i);
                    if (t.phase == UnityEngine.TouchPhase.Ended || t.phase == UnityEngine.TouchPhase.Canceled)
                        continue;
                    screenPos = t.position;
                    return true;
                }
            }
            screenPos = default;
            return false;
        }

        /// <summary>True only when the pointer is over a UI element (Canvas/Graphic). Ignores 3D colliders so clicking the ship or world doesn't block shooting.</summary>
        private static bool IsPointerOverUI()
        {
            if (EventSystem.current == null) return false;
            if (!TryGetPrimaryPointerScreenPosition(out Vector2 pointerPosition))
                return false;
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
                    // At max speed: drop only thrust that would add more speed along current velocity (so we don't overshoot max).
                    // If thrust opposes velocity (quick 180°), alongVel is negative — do not cancel that; full thrust slows/reverses.
                    Vector3 velNorm = currentVelocity.normalized;
                    Vector3 thrustVec = moveDirection * EffectiveEngineThrust;
                    float alongVel = Vector3.Dot(thrustVec, velNorm);
                    Vector3 steerForce = thrustVec - velNorm * Mathf.Max(0f, alongVel);
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

        /// <summary>
        /// Population transfer should run while genuinely orbiting (tangential motion), not when nearly stationary.
        /// The old near-zero velocity gate never fired during normal orbit, so people never loaded/unloaded.
        /// </summary>
        private bool IsOrbitStableForPeopleTransfer()
        {
            if (IsInStableOrbit())
                return true;
            // AI uses a fixed tangent speed that may not match GetOrbitTargetSpeed ratios; accept looser alignment in-band.
            if (!_isAIControlled || currentOrbitPlanet == null || rb == null)
                return false;
            Vector3 planetPos = currentOrbitPlanet.transform.position;
            Vector3 toShip = rb.position - planetPos;
            toShip.y = 0f;
            float dist = toShip.magnitude;
            if (dist < 0.01f)
                return false;
            float innerWorld = currentOrbitPlanet.PlanetSize * 0.5f;
            float outerWorld = currentOrbitPlanet.PlanetSize * currentOrbitPlanet.GetOrbitZoneOuterRadiusLocal();
            if (dist < innerWorld || dist > outerWorld)
                return false;
            Vector3 vel = rb.linearVelocity;
            vel.y = 0f;
            if (vel.sqrMagnitude < 0.0001f)
                return false;
            Vector3 radial = toShip / dist;
            Vector3 tangent = new Vector3(radial.z, 0f, -radial.x);
            float alignment = Vector3.Dot(vel.normalized, tangent);
            return alignment >= 0.82f;
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
            // Health can regen from low values when not dead.
            // If hull and gems are both depleted, Update() runs TryDieIfHullAndGemsDepleted() before this method.
            if (IsServer && !isDead.Value && currentHealth.Value < MaxHealth)
            {
                if (Time.time < lastHullDamageServerTime + healthRegenDelayAfterDamage)
                {
                    return;
                }
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
            // Orbit firing rule is enforced on the server via ServerWorldPositionInsideAnyOrbitZone (FireServerRpc).
            // Local currentOrbitPlanet is not replicated and often disagrees with the server on relay/dedicated clients,
            // which prevented FireServerRpc from ever being sent while host/editor looked fine.
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

        private bool ShouldSpawnOwnerPredictedBulletTracers()
        {
            if (!IsOwner) return false;
            var nm = NetworkManager.Singleton;
            return nm != null && nm.IsClient;
        }

        /// <summary>
        /// Builds per-cannon world origins and aim axes from this client's transforms (same sizing rules as <see cref="FireServerRpc"/>).
        /// Missing weapon slots use ship position + ship forward so the server never substitutes desynced server rigs for players.
        /// </summary>
        private bool TryBuildOwnerReportedCannonBallisticsForFireRpc(out Vector3[] origins, out Vector3[] forwards)
        {
            origins = null;
            forwards = null;
            var bulletWc = bulletConfig ?? EffectiveWeaponConfig;
            if (bulletWc == null || bulletWc.cannons == null || bulletWc.cannons.Count == 0)
                return false;

            Vector3 shipFwd = transform.forward;
            shipFwd.y = 0f;
            if (shipFwd.sqrMagnitude < 0.01f) shipFwd = Vector3.forward;
            else shipFwd.Normalize();

            int fpCount = bulletFirePoints != null ? bulletFirePoints.Count : 0;
            int cannonCount = bulletWc.cannons.Count;
            int maxCannons = Mathf.Max(fpCount, cannonCount);
            int n = Mathf.Min(cannonCount, maxCannons);
            if (n <= 0)
                return false;

            origins = new Vector3[n];
            forwards = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                if (bulletFirePoints != null && i < bulletFirePoints.Count && bulletFirePoints[i] != null)
                {
                    Transform pt = bulletFirePoints[i];
                    origins[i] = pt.position;
                    Vector3 wd = pt.forward;
                    wd.y = 0f;
                    if (wd.sqrMagnitude < 0.01f) wd = shipFwd;
                    else wd.Normalize();
                    forwards[i] = wd;
                }
                else
                {
                    origins[i] = transform.position;
                    forwards[i] = shipFwd;
                }
            }
            return true;
        }

        /// <summary>
        /// Owner-only cosmetic tracers using the same volley rules as <see cref="FireServerRpc"/> (instant feedback on host and client).
        /// </summary>
        private void TrySpawnOwnerPredictedBulletVolley(
            Vector3 shipPosition,
            Vector3 shipForward,
            Vector3 ownerReportedShipVelocity,
            Vector3[] ownerReportedCannonOrigins,
            Vector3[] ownerReportedCannonForwards)
        {
            CombatSystem combat = CombatSystem.Instance;
            if (combat == null)
                combat = UnityEngine.Object.FindFirstObjectByType<CombatSystem>(FindObjectsInactive.Include);
            if (combat == null) return;
            if (ServerWorldPositionInsideAnyOrbitZone(shipPosition)) return;
            if (gemMoonDocked.Value) return;
            EnsureBulletLastFireTime();
            Vector3 shipVel = ownerReportedShipVelocity.sqrMagnitude > 0.0001f
                ? ownerReportedShipVelocity
                : (rb != null ? rb.linearVelocity : Vector3.zero);
            shipVel.y = 0f;

            var bulletWc = bulletConfig ?? EffectiveWeaponConfig;
            int serverWeaponPoints = bulletFirePoints != null ? bulletFirePoints.Count : 0;
            int ownerWeaponPoints = ownerReportedCannonOrigins != null ? ownerReportedCannonOrigins.Length : 0;
            int maxCannons = Mathf.Max(serverWeaponPoints, ownerWeaponPoints);
            if (bulletWc.cannons == null || maxCannons <= 0) return;

            var cannonsByEnergy = new System.Collections.Generic.SortedDictionary<int, System.Collections.Generic.List<int>>();
            int cannonCount = Mathf.Min(bulletWc.cannons.Count, maxCannons);
            for (int i = 0; i < cannonCount; i++)
            {
                bool hasServerPoint = bulletFirePoints != null && i < bulletFirePoints.Count && bulletFirePoints[i] != null;
                bool hasOwnerPoint = ownerReportedCannonOrigins != null && i < ownerReportedCannonOrigins.Length;
                if (!hasServerPoint && !hasOwnerPoint) continue;
                int energyKey = GetEnergyCostGroupKey(bulletWc.cannons[i].energyCostPerShot);
                if (!cannonsByEnergy.TryGetValue(energyKey, out var group))
                {
                    group = new System.Collections.Generic.List<int>();
                    cannonsByEnergy.Add(energyKey, group);
                }
                group.Add(i);
            }

            foreach (var kv in cannonsByEnergy)
            {
                int energyKey = kv.Key;
                var group = kv.Value;
                if (group == null || group.Count == 0) continue;

                int start = 0;
                if (bulletRoundRobinStartByEnergy.TryGetValue(energyKey, out int savedStart) && group.Count > 0)
                    start = ((savedStart % group.Count) + group.Count) % group.Count;

                for (int step = 0; step < group.Count; step++)
                {
                    int i = group[(start + step) % group.Count];
                    var c = bulletWc.cannons[i];
                    bool skipEnergyForHostOwnerCosmetic = IsOwner && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
                    if (!skipEnergyForHostOwnerCosmetic && currentEnergy.Value < c.energyCostPerShot) continue;

                    float effectiveFireRate = c.fireRate * (1f + attrFireRate.Value * ATTR_MULTIPLIER_PER_LEVEL);
                    if (i >= bulletLastFireTime.Length || Time.time - bulletLastFireTime[i] < 1f / effectiveFireRate) continue;

                    int bankCount = combat.BulletPrefabBankCount;
                    int bulletIdx = (runtimeBulletPrefabIndex.Value >= 0 && bankCount > 0)
                        ? (runtimeBulletPrefabIndex.Value % bankCount)
                        : (c.bulletPrefabIndex >= 0 && bankCount > 0 && c.bulletPrefabIndex < bankCount)
                            ? c.bulletPrefabIndex
                            : (bulletPrefabBankIndex >= 0 && bulletPrefabBankIndex < bankCount ? bulletPrefabBankIndex : 0);

                    Transform firePt = (bulletFirePoints != null && i < bulletFirePoints.Count && bulletFirePoints[i] != null)
                        ? bulletFirePoints[i]
                        : transform;
                    bool hasOwnerReportedOrigin = ownerReportedCannonOrigins != null && i >= 0 && i < ownerReportedCannonOrigins.Length;
                    bool hasOwnerReportedForward = ownerReportedCannonForwards != null && i >= 0 && i < ownerReportedCannonForwards.Length;
                    Vector3 fireOrigin = hasOwnerReportedOrigin ? ownerReportedCannonOrigins[i] : firePt.position;

                    Vector3 cannonFwd = hasOwnerReportedForward ? ownerReportedCannonForwards[i] : firePt.forward;
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
                    bool spawnedAnyForThisCannon = false;
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
                        float scale = c.bulletScale * BulletScaleMultiplier;
                        BulletSpawnPayload payload = combat.BuildBulletTracerPayloadForClientPreview(
                            fireOrigin, dir, speed, damage, shipTeam.Value, NetworkObjectId, scale, 0, shipVel, bulletIdx);
                        ClientBulletTracer.SpawnOwnerPredicted(payload);
                        spawnedAnyForThisCannon = true;
                    }

                    if (!spawnedAnyForThisCannon) continue;

                    // Match server cooldown locally so we do not spawn a full volley every frame while ShootPressed
                    // stays true (FireClientRpc arrives later after RTT).
                    if (i < bulletLastFireTime.Length)
                        bulletLastFireTime[i] = Time.time;
                }
            }
        }

        [ServerRpc(RequireOwnership = true)]
        private void FireServerRpc(Vector3 shipPosition, Vector3 shipForward, Vector3 ownerReportedShipVelocity, Vector3[] ownerReportedCannonOrigins, Vector3[] ownerReportedCannonForwards)
        {
            CombatSystem combat = CombatSystem.Instance;
            if (combat == null)
                combat = UnityEngine.Object.FindFirstObjectByType<CombatSystem>(FindObjectsInactive.Include);
            if (combat == null) return;
            // Use owner-reported position for the orbit band: cached currentOrbitPlanet is not replicated and can block all shots on dedicated server.
            if (ServerWorldPositionInsideAnyOrbitZone(shipPosition)) return;
            if (gemMoonDocked.Value) return;
            EnsureBulletLastFireTime();

            bool useOnlyOwnerBallistics = NetworkObject != null && NetworkObject.IsPlayerObject;
            Vector3 shipVel;
            if (useOnlyOwnerBallistics)
            {
                shipVel = ownerReportedShipVelocity;
                shipVel.y = 0f;
            }
            else
            {
                shipVel = ownerReportedShipVelocity.sqrMagnitude > 0.0001f
                    ? ownerReportedShipVelocity
                    : (rb != null ? rb.linearVelocity : Vector3.zero);
                shipVel.y = 0f;
            }

            var bulletIndicesFired = new System.Collections.Generic.List<byte>();
            var bulletPrefabIndicesFired = new System.Collections.Generic.List<int>();

            // Player ships: spawn geometry matches the owning client's RPC (no server-side muzzle snap). AI uses server transforms when arrays are null.
            var bulletWc = bulletConfig ?? EffectiveWeaponConfig;
            int serverWeaponPoints = bulletFirePoints != null ? bulletFirePoints.Count : 0;
            int ownerWeaponPoints = ownerReportedCannonOrigins != null ? ownerReportedCannonOrigins.Length : 0;
            int maxCannons = Mathf.Max(serverWeaponPoints, ownerWeaponPoints);
            if (bulletWc.cannons != null && maxCannons > 0)
            {
                var cannonsByEnergy = new System.Collections.Generic.SortedDictionary<int, System.Collections.Generic.List<int>>();
                int cannonCount = Mathf.Min(bulletWc.cannons.Count, maxCannons);
                for (int i = 0; i < cannonCount; i++)
                {
                    bool hasServerPoint = bulletFirePoints != null && i < bulletFirePoints.Count && bulletFirePoints[i] != null;
                    bool hasOwnerPoint = ownerReportedCannonOrigins != null && i < ownerReportedCannonOrigins.Length;
                    if (!hasServerPoint && !hasOwnerPoint) continue;
                    int energyKey = GetEnergyCostGroupKey(bulletWc.cannons[i].energyCostPerShot);
                    if (!cannonsByEnergy.TryGetValue(energyKey, out var group))
                    {
                        group = new System.Collections.Generic.List<int>();
                        cannonsByEnergy.Add(energyKey, group);
                    }
                    group.Add(i);
                }

                foreach (var kv in cannonsByEnergy)
                {
                    int energyKey = kv.Key;
                    var group = kv.Value;
                    if (group == null || group.Count == 0) continue;

                    int start = 0;
                    if (bulletRoundRobinStartByEnergy.TryGetValue(energyKey, out int savedStart) && group.Count > 0)
                        start = ((savedStart % group.Count) + group.Count) % group.Count;

                    bool firedInGroup = false;
                    int nextStart = start;

                    for (int step = 0; step < group.Count; step++)
                    {
                        int i = group[(start + step) % group.Count];
                        var c = bulletWc.cannons[i];
                        if (currentEnergy.Value < c.energyCostPerShot) continue;

                        float effectiveFireRate = c.fireRate * (1f + attrFireRate.Value * ATTR_MULTIPLIER_PER_LEVEL);
                        if (i >= bulletLastFireTime.Length || Time.time - bulletLastFireTime[i] < 1f / effectiveFireRate) continue;

                        int bankCount = combat.BulletPrefabBankCount;
                        // Prefer cycled runtime index (B key) when valid so toggling bullets always takes effect; else per-cannon, else family default.
                        int bulletIdx = (runtimeBulletPrefabIndex.Value >= 0 && bankCount > 0)
                            ? (runtimeBulletPrefabIndex.Value % bankCount)
                            : (c.bulletPrefabIndex >= 0 && bankCount > 0 && c.bulletPrefabIndex < bankCount)
                                ? c.bulletPrefabIndex
                                : (bulletPrefabBankIndex >= 0 && bulletPrefabBankIndex < bankCount ? bulletPrefabBankIndex : 0);

                        Transform firePt = (bulletFirePoints != null && i < bulletFirePoints.Count && bulletFirePoints[i] != null)
                            ? bulletFirePoints[i]
                            : transform;
                        bool hasOwnerReportedOrigin = ownerReportedCannonOrigins != null && i >= 0 && i < ownerReportedCannonOrigins.Length;
                        bool hasOwnerReportedForward = ownerReportedCannonForwards != null && i >= 0 && i < ownerReportedCannonForwards.Length;

                        Vector3 fireOrigin;
                        if (hasOwnerReportedOrigin)
                            fireOrigin = ownerReportedCannonOrigins[i];
                        else if (useOnlyOwnerBallistics)
                            fireOrigin = shipPosition;
                        else
                            fireOrigin = firePt.position;

                        Vector3 cannonFwd;
                        if (hasOwnerReportedForward)
                            cannonFwd = ownerReportedCannonForwards[i];
                        else if (useOnlyOwnerBallistics)
                            cannonFwd = shipForward;
                        else
                            cannonFwd = firePt.forward;
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
                        bool spawnedAnyForThisCannon = false;
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
                            float scale = c.bulletScale * BulletScaleMultiplier;
                            if (combat.TrySpawnBulletOnServer(fireOrigin, dir, speed, damage, shipTeam.Value, NetworkObjectId, scale, 0, shipVel, bulletIdx))
                            {
                                spawnedAnyForThisCannon = true;
                                if (rb != null)
                                {
                                    float recoilImpulse = recoilStrength * scale * (0.08f + damage / 400f);
                                    rb.AddForce(-dir * recoilImpulse, ForceMode.Impulse);
                                }
                            }
                        }

                        if (!spawnedAnyForThisCannon) continue;

                        currentEnergy.Value = Mathf.Max(0f, currentEnergy.Value - c.energyCostPerShot);
                        bulletLastFireTime[i] = Time.time;
                        bulletIndicesFired.Add((byte)i);
                        bulletPrefabIndicesFired.Add(bulletIdx);

                        firedInGroup = true;
                        nextStart = ((start + step + 1) % group.Count + group.Count) % group.Count;
                    }

                    if (firedInGroup)
                    {
                        bulletRoundRobinStartByEnergy[energyKey] = nextStart;
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
                    // Same as Bullet visuals: avoid instantiating Sci-Fi AllIn1 muzzle prefabs on mobile (shader / stability).
                    if (!Application.isMobilePlatform && bulletPrefabIndices != null && j < bulletPrefabIndices.Length && CombatSystem.Instance != null)
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
                                // Same URP/mobile fix as Bullet VFX: muzzle prefabs use AllIn1 GrabPass otherwise invisible on device.
                                VfxUrpCompat.PrepareVfxInstance(muzzle);
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

                    if (Application.isMobilePlatform && bulletFirePoints != null && idx >= 0 && idx < bulletFirePoints.Count && bulletFirePoints[idx] != null)
                    {
                        Transform pt = bulletFirePoints[idx];
                        Vector3 fwd = pt.forward;
                        fwd.y = 0f;
                        if (fwd.sqrMagnitude < 0.01f)
                        {
                            fwd = transform.forward;
                            fwd.y = 0f;
                        }
                        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                        fwd.Normalize();
                        Color flashColor = TeamManager.Instance != null
                            ? TeamManager.GetTeamColor(shipTeam.Value)
                            : new Color(1f, 0.88f, 0.45f);
                        VfxUrpCompat.SpawnMobileMuzzleFlash(pt.position, fwd, flashColor);
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

            if (IsOwner && bulletIndicesFired != null && bulletIndicesFired.Length > 0)
            {
                EnsureBulletLastFireTime();
                float t = Time.time;
                for (int j = 0; j < bulletIndicesFired.Length; j++)
                {
                    int idx = bulletIndicesFired[j];
                    if (idx >= 0 && idx < bulletLastFireTime.Length)
                        bulletLastFireTime[idx] = t;
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
            Vector3 serverVel = rb != null ? rb.linearVelocity : Vector3.zero;
            serverVel.y = 0f;
            FireServerRpc(transform.position, direction, serverVel, null, null);
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
        // Server-only cooldown window that suppresses gem magnet + pickup while this ship is in death flow.
        private float gemCollectionSuppressedUntilServerTime = 0f;
        private const float GemCollectionSuppressionSeconds = 2f;

        public bool IsGemCollectionSuppressed
        {
            get
            {
                float now = NetworkManager.Singleton != null
                    ? (float)NetworkManager.Singleton.ServerTime.Time
                    : Time.time;
                return now < gemCollectionSuppressedUntilServerTime;
            }
        }

        private void SuppressGemCollectionForRespawnDelay()
        {
            float now = NetworkManager.Singleton != null
                ? (float)NetworkManager.Singleton.ServerTime.Time
                : Time.time;
            float until = now + GemCollectionSuppressionSeconds;
            if (until > gemCollectionSuppressedUntilServerTime)
                gemCollectionSuppressedUntilServerTime = until;
        }

        /// <summary>Server: lethal state is hull and carried gems both depleted. Damage applies this; gem removal (moon deposit, upgrades) must too, and we run before hull regen so 0/0 cannot heal away.</summary>
        private void TryDieIfHullAndGemsDepleted(ulong killerShipNetworkId = 0)
        {
            if (!IsServer || isDead.Value) return;
            const float deathThreshold = 0.001f;
            if (currentHealth.Value > deathThreshold || currentGems.Value > deathThreshold)
                return;
            SuppressGemCollectionForRespawnDelay();
            // Do not invoke DieServerRpc() from server logic: NGO ServerRpc send path is for client→server;
            // run death immediately on the authoritative host/dedicated process.
            ServerApplyDeath(killerShipNetworkId);
        }

        [ServerRpc(RequireOwnership = false)]
        public void TakeDamageServerRpc(float damage, TeamManager.Team attackerTeam, ulong attackerShipNetworkId = 0)
        {
            // Block friendly fire only when both have valid teams and they match
            if (attackerTeam != TeamManager.Team.None && attackerTeam == shipTeam.Value) return;
            if (isDead.Value) return;
            if (gemMoonDocked.Value) return;

            if (damage > 0.0001f)
                lastHullDamageServerTime = Time.time;

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

            const float deathThreshold = 0.001f;
            if (currentHealth.Value <= deathThreshold)
                SuppressGemCollectionForRespawnDelay();
            TryDieIfHullAndGemsDepleted(attackerShipNetworkId);
        }

        private void HandleDeath()
        {
            if (isDead.Value) return;
            // Death is triggered in TakeDamageServerRpc when health and gems both reach 0
            // No passive gem drain - gems only reduce when bullets hit (and get expelled)
        }

        /// <summary>Server: friendly planets below 50% max population pull crew from ships until half full; at/above 50%, only surplus above half loads onto ships. Non-friendly: unload onto neutral/enemy as invasion. People beam as projectiles. Unload is 1 person/s (not scaled by ship level); load still scales with level (~2 chunks/s).</summary>
        private void TickOrbitPopulationTransfer()
        {
            if (currentOrbitPlanet == null)
            {
                peopleLoadAccumulator = 0f;
                peopleUnloadAccumulator = 0f;
                peopleTransferStationaryTimer = 0f;
                return;
            }

            bool orbitStableForTransfer = IsOrbitStableForPeopleTransfer();
            if (orbitStableForTransfer)
                peopleTransferStationaryTimer += Time.fixedDeltaTime;
            else
                peopleTransferStationaryTimer = 0f;

            if (peopleTransferStationaryTimer < peopleTransferStationaryHoldSeconds)
                return;

            float peopleSpaceAvailable = PeopleCapacity - currentPeople.Value - peopleInTransit;
            bool debugModeEnabled = GameManager.Instance != null && GameManager.Instance.DebugMode;

            float peopleDropValue = Mathf.Max(1f, ShipLevel); // people moved per load projectile (ship level)
            float loadRate = peopleDropValue * 2f * Time.fixedDeltaTime * GetCardPeopleTransferSpeedMultiplier(); // ~2 load chunks/sec, card-scaled
            const float peopleUnloadPerSecondBase = 1f; // unload: always 1 person/s before unload-specific cards
            const float peopleUnloadChunk = 1f;
            float unloadAccumStep = peopleUnloadPerSecondBase * Time.fixedDeltaTime * GetPeopleUnloadSpeedMultiplier();
            if (debugModeEnabled)
            {
                loadRate *= 100f;
                unloadAccumStep *= 100f;
            }

            if (loadRate <= 0f && unloadAccumStep <= 0f) return;

            bool friendly = (currentOrbitPlanet is HomePlanet home && home.AssignedTeam == shipTeam.Value)
                || currentOrbitPlanet.TeamOwnership == shipTeam.Value;

            const int maxPeopleProjectilesPerFixedStep = 8;

            if (friendly)
            {
                Planet orbitPlanet = currentOrbitPlanet;
                float halfCap = 0.5f * orbitPlanet.MaxPopulation;
                float curPop = orbitPlanet.CurrentPopulation;
                // Below 50%: planet pulls people from ships until half capacity. At/above 50%: only surplus above half can load onto ships.
                bool planetWantsReinforce = curPop < halfCap - 0.0001f;

                if (planetWantsReinforce)
                {
                    peopleLoadAccumulator = 0f;
                    float roomToHalf = Mathf.Max(0f, halfCap - curPop);

                    if (debugModeEnabled)
                    {
                        float instantUnload = Mathf.Min(currentPeople.Value, roomToHalf);
                        if (instantUnload > 0f)
                        {
                            RemovePeopleServerRpc(instantUnload);
                            orbitPlanet.AddPopulationServerRpc(instantUnload, shipTeam.Value);
                            PlayPeopleUnloadSoundClientRpc(instantUnload);
                        }
                        return;
                    }

                    if (currentPeople.Value > 0.0001f && roomToHalf > 0.0001f)
                        peopleUnloadAccumulator += unloadAccumStep;

                    float roomBudget = roomToHalf;
                    if (peopleUnloadAccumulator >= peopleUnloadChunk
                        && currentPeople.Value >= peopleUnloadChunk
                        && roomBudget >= peopleUnloadChunk
                        && GemSpawner.Instance != null)
                    {
                        RemovePeopleServerRpc(peopleUnloadChunk);
                        peopleUnloadAccumulator -= peopleUnloadChunk;
                        roomBudget -= peopleUnloadChunk;

                        Vector3 shipPos = rb != null ? rb.position : transform.position;
                        Vector3 planetPos = orbitPlanet.transform.position;
                        var planetNo = orbitPlanet.GetComponent<NetworkObject>();
                        var shipNo = GetComponent<NetworkObject>();
                        if (planetNo != null && shipNo != null)
                            GemSpawner.Instance.SpawnPeopleUnload(shipPos, planetPos, peopleUnloadChunk, planetNo.NetworkObjectId, shipTeam.Value, shipNo.NetworkObjectId);

                        if (VisualEffectsManager.Instance != null)
                            VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                                planetPos,
                                (int)FloatingCountChannel.PeopleUnload,
                                peopleUnloadChunk,
                                (int)shipTeam.Value);
                        PlayPeopleUnloadSoundClientRpc(peopleUnloadChunk);
                    }
                    else
                    {
                        float maxReinforceRem = Mathf.Min(currentPeople.Value, roomBudget);
                        if (maxReinforceRem > 0.0001f
                            && maxReinforceRem < peopleUnloadChunk
                            && peopleUnloadAccumulator >= maxReinforceRem - 0.0001f
                            && GemSpawner.Instance != null)
                        {
                            RemovePeopleServerRpc(maxReinforceRem);
                            peopleUnloadAccumulator -= maxReinforceRem;

                            Vector3 shipPos = rb != null ? rb.position : transform.position;
                            Vector3 planetPos = orbitPlanet.transform.position;
                            var planetNo = orbitPlanet.GetComponent<NetworkObject>();
                            var shipNo = GetComponent<NetworkObject>();
                            if (planetNo != null && shipNo != null)
                                GemSpawner.Instance.SpawnPeopleUnload(shipPos, planetPos, maxReinforceRem, planetNo.NetworkObjectId, shipTeam.Value, shipNo.NetworkObjectId);

                            if (VisualEffectsManager.Instance != null)
                                VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                                    planetPos,
                                    (int)FloatingCountChannel.PeopleUnload,
                                    maxReinforceRem,
                                    (int)shipTeam.Value);
                            PlayPeopleUnloadSoundClientRpc(maxReinforceRem);
                        }
                    }
                    return;
                }

                peopleUnloadAccumulator = 0f;
                float available = Mathf.Max(0f, curPop - halfCap);
                if (debugModeEnabled)
                {
                    float instantLoadAmount = Mathf.Min(peopleSpaceAvailable, available);
                    if (instantLoadAmount > 0f)
                    {
                        orbitPlanet.RemovePopulationServerRpc(instantLoadAmount);
                        AddPeopleServerRpc(instantLoadAmount);
                        PlayPeopleLoadSoundClientRpc(instantLoadAmount);
                    }
                    return;
                }

                float amount = Mathf.Min(loadRate, peopleSpaceAvailable, available);
                if (amount > 0f) peopleLoadAccumulator += amount;

                int loadSpawnCount = 0;
                while (loadSpawnCount < maxPeopleProjectilesPerFixedStep
                    && peopleLoadAccumulator >= peopleDropValue
                    && GemSpawner.Instance != null)
                {
                    float spaceLeft = PeopleCapacity - currentPeople.Value - peopleInTransit;
                    float surplusNow = Mathf.Max(0f, orbitPlanet.CurrentPopulation - halfCap);
                    if (spaceLeft < peopleDropValue || surplusNow < peopleDropValue)
                        break;

                    orbitPlanet.RemovePopulationServerRpc(peopleDropValue);
                    peopleLoadAccumulator -= peopleDropValue;
                    peopleInTransit += peopleDropValue;
                    loadSpawnCount++;

                    Vector3 planetPos = orbitPlanet.transform.position;
                    Vector3 shipPos = rb != null ? rb.position : transform.position;
                    var planetNo = orbitPlanet.GetComponent<NetworkObject>();
                    var shipNo = GetComponent<NetworkObject>();
                    if (shipNo != null && planetNo != null)
                        GemSpawner.Instance.SpawnPeopleLoad(planetPos, shipPos, peopleDropValue, shipNo.NetworkObjectId, planetNo.NetworkObjectId, shipTeam.Value);
                }

                float spaceAfter = PeopleCapacity - currentPeople.Value - peopleInTransit;
                float surplusAfter = Mathf.Max(0f, orbitPlanet.CurrentPopulation - halfCap);
                float maxLoadRem = Mathf.Min(spaceAfter, surplusAfter);
                if (loadSpawnCount < maxPeopleProjectilesPerFixedStep
                    && maxLoadRem > 0.0001f
                    && maxLoadRem < peopleDropValue
                    && peopleLoadAccumulator >= maxLoadRem - 0.0001f
                    && GemSpawner.Instance != null)
                {
                    orbitPlanet.RemovePopulationServerRpc(maxLoadRem);
                    peopleLoadAccumulator -= maxLoadRem;
                    peopleInTransit += maxLoadRem;

                    Vector3 planetPos = orbitPlanet.transform.position;
                    Vector3 shipPos = rb != null ? rb.position : transform.position;
                    var planetNo = orbitPlanet.GetComponent<NetworkObject>();
                    var shipNo = GetComponent<NetworkObject>();
                    if (shipNo != null && planetNo != null)
                        GemSpawner.Instance.SpawnPeopleLoad(planetPos, shipPos, maxLoadRem, shipNo.NetworkObjectId, planetNo.NetworkObjectId, shipTeam.Value);
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

                if (currentPeople.Value > 0.0001f)
                    peopleUnloadAccumulator += unloadAccumStep;

                if (peopleUnloadAccumulator >= peopleUnloadChunk
                    && currentPeople.Value >= peopleUnloadChunk
                    && GemSpawner.Instance != null)
                {
                    RemovePeopleServerRpc(peopleUnloadChunk);
                    peopleUnloadAccumulator -= peopleUnloadChunk;

                    Vector3 shipPos = rb != null ? rb.position : transform.position;
                    Vector3 planetPos = currentOrbitPlanet.transform.position;
                    var planetNo = currentOrbitPlanet.GetComponent<NetworkObject>();
                    var shipNo = GetComponent<NetworkObject>();
                    if (planetNo != null && shipNo != null)
                        GemSpawner.Instance.SpawnPeopleUnload(shipPos, planetPos, peopleUnloadChunk, planetNo.NetworkObjectId, shipTeam.Value, shipNo.NetworkObjectId);

                    if (VisualEffectsManager.Instance != null)
                        VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                            planetPos,
                            (int)FloatingCountChannel.PeopleUnload,
                            peopleUnloadChunk,
                            (int)shipTeam.Value);
                    PlayPeopleUnloadSoundClientRpc(peopleUnloadChunk);
                }
                else if (currentPeople.Value > 0f
                    && currentPeople.Value < peopleUnloadChunk
                    && peopleUnloadAccumulator >= currentPeople.Value - 0.0001f
                    && GemSpawner.Instance != null)
                {
                    float remainder = currentPeople.Value;
                    RemovePeopleServerRpc(remainder);
                    peopleUnloadAccumulator = Mathf.Max(0f, peopleUnloadAccumulator - remainder);

                    Vector3 shipPos = rb != null ? rb.position : transform.position;
                    Vector3 planetPos = currentOrbitPlanet.transform.position;
                    var planetNo = currentOrbitPlanet.GetComponent<NetworkObject>();
                    var shipNo = GetComponent<NetworkObject>();
                    if (planetNo != null && shipNo != null)
                        GemSpawner.Instance.SpawnPeopleUnload(shipPos, planetPos, remainder, planetNo.NetworkObjectId, shipTeam.Value, shipNo.NetworkObjectId);

                    if (VisualEffectsManager.Instance != null)
                        VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                            planetPos,
                            (int)FloatingCountChannel.PeopleUnload,
                            remainder,
                            (int)shipTeam.Value);
                    PlayPeopleUnloadSoundClientRpc(remainder);
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

        /// <summary>Server: while docked at gem moon, deposits shipLevel gems per tick at 2 ticks/sec; applied directly to planet level gems.</summary>
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
                RemoveGemsFromDepositServer(instantDepositAmount);
                depositAccumulator = 0f;
                ApplyMoonGemDepositToPlanet(depositPlanet, instantDepositAmount);
                depositedAnyGemsThisOrbit = true;
            }
            else
            {
            float gemValue = Mathf.Max(1f, ShipLevel); // gems credited per deposit tick
            float rate = gemValue * 2f * Time.fixedDeltaTime * GetCardGemDepositSpeedMultiplier(); // 2 deposit ticks/sec, card-scaled
            if (debugModeEnabled) rate *= 100f;
            if (rate <= 0f) return;
            float amount = Mathf.Min(rate, currentGems.Value);
            if (amount <= 0f) return;

            depositAccumulator += amount;
            float now = (float)NetworkManager.Singleton.ServerTime.Time;
            const float gemInterval = 0.5f; // twice per second
            bool shouldDepositChunk = depositAccumulator >= gemValue && currentGems.Value >= gemValue && (now - lastDepositSpawnTime) >= gemInterval;
            if (shouldDepositChunk)
            {
                RemoveGemsFromDepositServer(gemValue);
                depositAccumulator -= gemValue;
                lastDepositSpawnTime = now;

                ApplyMoonGemDepositToPlanet(depositPlanet, gemValue);
                depositedAnyGemsThisOrbit = true;
            }

            // Remainder: when gems are below one full "gem value", credit remaining directly so the ship empties completely.
            if (!shouldDepositChunk && currentGems.Value > 0f && currentGems.Value < gemValue)
            {
                float remainder = currentGems.Value;
                RemoveGemsFromDepositServer(remainder);
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
        [Tooltip("Seconds after death before the ship respawns at its home/origin orbit.")]
        [SerializeField] private float respawnDelay = 5f;

        [Header("Death breakup (client-only)")]
        [Tooltip("Death breakup tuning now lives on CombatSystem > Ship Death Breakup.")]
        [SerializeField] private bool useCombatSystemDeathBreakupTuning = true;
        private static PhysicsMaterial s_deathDebrisNoFrictionMaterial;

        [ServerRpc(RequireOwnership = false)]
        private void DieServerRpc(ulong killerShipNetworkId = 0)
        {
            ServerApplyDeath(killerShipNetworkId);
        }

        /// <summary>Server-only: lethal breakup, stats, and delayed respawn. Shared by <see cref="DieServerRpc"/> and <see cref="TryDieIfHullAndGemsDepleted"/>.</summary>
        private void ServerApplyDeath(ulong killerShipNetworkId = 0)
        {
            if (!IsServer || isDead.Value) return;
            SuppressGemCollectionForRespawnDelay();
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

            // Detach mesh pieces with physics on clients, then hide the real ship; central explosion VFX
            ShipDeathBreakupClientRpc();
            SpawnDeathExplosion();

            // Do not Invoke a Netcode ServerRpc: on a dedicated server, Invoke may not execute the RPC body the same way
            // as a direct call from host logic. Use a plain MonoBehaviour callback for the delayed respawn.
            Invoke(nameof(DelayedRespawnAfterDeath), respawnDelay);
        }

        /// <summary>Server-only delayed respawn. Invoked by <see cref="ServerApplyDeath"/>; must not be a ServerRpc.</summary>
        private void DelayedRespawnAfterDeath()
        {
            if (!IsServer) return;
            // Reset stats
            currentHealth.Value = MaxHealth;
            currentGems.Value = 0f;
            currentPeople.Value = 0f;
            currentEnergy.Value = EffectiveEnergyCapacity;
            isDead.Value = false;
            gemCollectionSuppressedUntilServerTime = 0f;
            
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
            if (!TryComputeOrbitSpawnPose(planet, out Vector3 orbitPos, out Vector3 vel, out Quaternion rot)) return;
            ApplyServerOrbitSpawnPoseAndNotifyOwner(orbitPos, vel, rot);
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

        [ClientRpc]
        private void ShipDeathBreakupClientRpc()
        {
            if (AudioManager.Instance != null)
                AudioManager.Instance.PlayShipDeathSound();
            SpawnDeathDebrisClientLocal();
            DisableShipRenderersAndCollidersClientLocal();
        }

        /// <summary>Spawns non-networked rigidbody shards from the ship visual (Prefab + card parts) for a local breakup effect.</summary>
        private void SpawnDeathDebrisClientLocal()
        {
            Transform root = GetCardVisualRoot();
            if (root == null && visualRoot != null)
                root = visualRoot;
            if (root == null) return;

            Vector3 explosionCenter = transform.position;
            explosionCenter.y = FIXED_Y_POSITION;
            int count = 0;
            CombatSystem combat = useCombatSystemDeathBreakupTuning ? CombatSystem.Instance : null;
            int maxPieces = combat != null ? combat.DeathDebrisMaxPieces : 64;

            var meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < meshFilters.Length && count < maxPieces; i++)
            {
                MeshFilter mf = meshFilters[i];
                if (mf == null || mf.sharedMesh == null) continue;
                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled) continue;
                if (mr.bounds.extents.sqrMagnitude < 1e-8f) continue;
                if (TrySpawnOneDebrisPiece(mf.transform, mf.sharedMesh, mr, false, null, explosionCenter))
                    count++;
            }

            var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skins.Length && count < maxPieces; i++)
            {
                SkinnedMeshRenderer skin = skins[i];
                if (skin == null || !skin.enabled) continue;
                if (skin.bounds.extents.sqrMagnitude < 1e-8f) continue;
                Mesh baked = new Mesh();
                skin.BakeMesh(baked, true);
                if (TrySpawnOneDebrisPiece(skin.transform, baked, skin, true, baked, explosionCenter))
                    count++;
            }
        }

        private bool TrySpawnOneDebrisPiece(Transform source, Mesh mesh, Renderer sourceRenderer, bool destroyMeshWithPiece, Mesh meshToDestroy, Vector3 explosionCenter)
        {
            if (mesh == null) return false;

            var go = new GameObject("ShipDebris");
            go.transform.SetPositionAndRotation(source.position, source.rotation);
            go.transform.localScale = source.lossyScale;
            go.layer = 0; // Default layer to avoid inheriting non-colliding ship layers.

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;

            var mr = go.AddComponent<MeshRenderer>();
            if (sourceRenderer != null)
            {
                // Preserve runtime team tint/material state from the live ship piece.
                Material[] runtimeMaterials = sourceRenderer.materials;
                if (runtimeMaterials != null && runtimeMaterials.Length > 0)
                    mr.materials = runtimeMaterials;
                else if (sourceRenderer.sharedMaterials != null && sourceRenderer.sharedMaterials.Length > 0)
                    mr.sharedMaterials = sourceRenderer.sharedMaterials;

                var propertyBlock = new MaterialPropertyBlock();
                sourceRenderer.GetPropertyBlock(propertyBlock);
                mr.SetPropertyBlock(propertyBlock);
            }

            Bounds mb = mesh.bounds;
            var box = go.AddComponent<BoxCollider>();
            box.center = mb.center;
            box.size = new Vector3(
                Mathf.Max(0.12f, mb.size.x),
                Mathf.Max(0.06f, mb.size.y),
                Mathf.Max(0.12f, mb.size.z)
            );
            box.contactOffset = Mathf.Max(0.03f, box.contactOffset);
            box.material = GetDeathDebrisNoFrictionMaterial();

            var debRb = go.AddComponent<Rigidbody>();
            debRb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            debRb.interpolation = RigidbodyInterpolation.Interpolate;
            debRb.useGravity = false;
            // Keep debris on the same gameplay plane as ships/asteroids.
            debRb.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            // Keep breakup pieces moving and bouncing for the full respawn timeout.
            CombatSystem combat = useCombatSystemDeathBreakupTuning ? CombatSystem.Instance : null;
            float minImpulse = combat != null ? combat.DeathDebrisMinImpulse : 1f;
            float maxImpulse = combat != null ? combat.DeathDebrisMaxImpulse : 3f;
            float minPieceMul = combat != null ? combat.DeathDebrisPieceSpeedMulMin : 0.2f;
            float maxPieceMul = combat != null ? combat.DeathDebrisPieceSpeedMulMax : 1.1f;
            float minUpImpulse = combat != null ? combat.DeathDebrisUpImpulseMin : 0f;
            float maxUpImpulse = combat != null ? combat.DeathDebrisUpImpulseMax : 1.5f;
            float minAngularVel = combat != null ? combat.DeathDebrisAngularVelMin : 2.5f;
            float maxAngularVel = combat != null ? combat.DeathDebrisAngularVelMax : 12f;
            float debrisLinearDamping = combat != null ? combat.DeathDebrisLinearDamping : 0f;
            float debrisLifetime = combat != null ? combat.DeathDebrisLifetime : 5f;
            float asteroidBounceMultiplier = combat != null ? combat.DeathDebrisAsteroidBounceMultiplier : 0.9f;
            float asteroidBounceMinSpeed = combat != null ? combat.DeathDebrisAsteroidBounceMinSpeed : 0.15f;
            bool debrisBlocksEnemyBullets = combat == null || combat.DeathDebrisBlocksEnemyBullets;
            int debrisBulletHitsToBreak = combat != null ? combat.DeathDebrisBulletHitsToBreak : 3;
            float debrisBulletShieldDuration = combat != null ? combat.DeathDebrisBulletShieldDuration : debrisLifetime;
            debRb.linearDamping = debrisLinearDamping;
            debRb.angularDamping = 0.8f;
            debRb.mass = Mathf.Clamp(mb.size.magnitude * 0.35f, 0.04f, 3f);
            debRb.maxDepenetrationVelocity = 10f;

            Vector3 shardPos = go.transform.TransformPoint(mb.center);
            Vector3 dir = shardPos - explosionCenter;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f)
            {
                Vector2 c = Random.insideUnitCircle.normalized;
                dir = new Vector3(c.x, 0f, c.y);
            }
            else
                dir.Normalize();

            float horizontalSpeed = Random.Range(minImpulse, maxImpulse)
                * Random.Range(minPieceMul, maxPieceMul);
            Vector3 vel = dir * horizontalSpeed;
            vel.y = 0f;
            debRb.linearVelocity = vel;
            debRb.angularVelocity = Vector3.up * Random.Range(minAngularVel, maxAngularVel);
            if (IsServer && debrisBlocksEnemyBullets)
            {
                var shield = go.AddComponent<ShipDeathDebris>();
                shield.Initialize(shipTeam.Value, debrisBulletHitsToBreak, debrisBulletShieldDuration);
            }

            if (destroyMeshWithPiece && meshToDestroy != null)
            {
                var disposer = go.AddComponent<DestroyMeshWithGameObject>();
                disposer.Mesh = meshToDestroy;
            }

            Object.Destroy(go, debrisLifetime);
            return true;
        }

        private static PhysicsMaterial GetDeathDebrisNoFrictionMaterial()
        {
            if (s_deathDebrisNoFrictionMaterial != null)
                return s_deathDebrisNoFrictionMaterial;

            s_deathDebrisNoFrictionMaterial = new PhysicsMaterial("DeathDebris_NoFriction_Bouncy")
            {
                dynamicFriction = 0f,
                staticFriction = 0f,
                bounciness = 1f,
                frictionCombine = PhysicsMaterialCombine.Minimum,
                bounceCombine = PhysicsMaterialCombine.Maximum
            };

            return s_deathDebrisNoFrictionMaterial;
        }

        private void DisableShipRenderersAndCollidersClientLocal()
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer != null)
                    renderer.enabled = false;
            }

            Collider[] colliders = GetComponentsInChildren<Collider>(true);
            foreach (var collider in colliders)
            {
                if (collider != null)
                    collider.enabled = false;
            }
        }

        /// <summary>Local-only: toggle ship visuals + colliders. Used to hide the ship while the player has no team
        /// (so they don't see a neutral ship in the team-select lobby) and to reveal it once a team is picked.</summary>
        private void SetShipBodyVisibleLocal(bool visible)
        {
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer != null)
                    renderer.enabled = visible;
            }

            Collider[] colliders = GetComponentsInChildren<Collider>(true);
            foreach (var collider in colliders)
            {
                if (collider != null)
                    collider.enabled = visible;
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
            if (rb == null || collision.contactCount == 0) return;

            // Player ships are moved only on the owning client (see FixedUpdate). AI ships are simulated on the server.
            bool canApplyBounce = (_isAIControlled && IsServer) || (!_isAIControlled && IsOwner);
            if (!canApplyBounce) return;

            float relativeSpeed = collision.relativeVelocity.magnitude;
            float collisionSoundPitch = Mathf.Lerp(0.8f, 1.35f, Mathf.InverseLerp(2f, 35f, relativeSpeed));

            Starship otherShip = collision.gameObject.GetComponent<Starship>();
            if (otherShip == null)
                otherShip = collision.gameObject.GetComponentInParent<Starship>();
            if (otherShip != null && otherShip != this)
            {
                if (AudioManager.Instance != null)
                    AudioManager.Instance.PlayShipCollisionSound(collisionSoundPitch);
                float shipVfxMinSpeed = GetCollisionVfxShipMinRelativeSpeed();
                if (relativeSpeed >= shipVfxMinSpeed)
                {
                    ContactPoint cp = collision.GetContact(0);
                    Vector3 impactPos = cp.point;
                    Vector3 outward = impactPos - transform.position;
                    outward.y = 0f;
                    if (outward.sqrMagnitude < 1e-6f) outward = transform.forward;
                    outward.Normalize();
                    float sev = ComputeCollisionVfxSeverityFromRelativeSpeed(relativeSpeed);
                    TrySpawnWeaponCollisionImpactVfx(impactPos, outward, sev, collisionSoundPitch);
                }
                return;
            }

            Asteroid asteroid = collision.gameObject.GetComponent<Asteroid>();
            if (asteroid == null)
                asteroid = collision.gameObject.GetComponentInParent<Asteroid>();
            if (asteroid == null || asteroid.IsDestroyed) return;

            ContactPoint contact = collision.GetContact(0);

            // Outward unit normal in XZ: asteroid center → impact point (impact angle vs movement uses this plane).
            Vector3 asteroidCenter = asteroid.transform.position;
            Vector3 n = contact.point - asteroidCenter;
            n.y = 0f;
            if (n.sqrMagnitude < 0.0001f) return;
            n.Normalize();

            // Pre-impact velocity: relativeVelocity is the reliable signal; rb here is often already post-solver.
            Vector3 vInc = collision.relativeVelocity;
            vInc.y = 0f;
            float vn = Vector3.Dot(vInc, n);
            if (vn >= 0f)
            {
                vInc = -collision.relativeVelocity;
                vInc.y = 0f;
                vn = Vector3.Dot(vInc, n);
            }
            if (vn >= 0f)
            {
                vInc = _lastFixedPlayPlaneVelocity;
                vn = Vector3.Dot(vInc, n);
            }
            if (vn >= 0f)
            {
                if (relativeSpeed < 2.5f) return;
                vn = -Mathf.Max(1f, relativeSpeed * 0.22f);
                vInc = n * vn;
            }

            float e = GetEffectiveAsteroidRestitution();
            Vector3 vOut = vInc - (1f + e) * vn * n;

            // Normal impulse approximation from the scripted bounce response:
            // Jn = m * (1 + e) * |vn|, force ~= Jn / dt.
            float deltaNormalSpeed = (1f + e) * Mathf.Abs(vn);
            float impactImpulse = rb.mass * deltaNormalSpeed;
            float impactForceNewtons = impactImpulse / Mathf.Max(0.0001f, Time.fixedDeltaTime);

            float asteroidCollisionPitch = Mathf.Lerp(0.7f, 1.25f, Mathf.InverseLerp(25f, 1200f, impactForceNewtons));
            if (AudioManager.Instance != null)
                AudioManager.Instance.PlayAsteroidCollisionSound(asteroidCollisionPitch);

            if (impactForceNewtons >= GetCollisionVfxAsteroidMinImpactForce())
            {
                float sev = ComputeCollisionVfxSeverityFromImpactForce(impactForceNewtons);
                TrySpawnWeaponCollisionImpactVfx(contact.point, n, sev, asteroidCollisionPitch);
            }

            // Visual nose-up kick (local X on visual root); stronger on harder hits.
            {
                float t = Mathf.Clamp01(Mathf.InverseLerp(35f, 900f, impactForceNewtons));
                asteroidVisualPitchImpulse = Mathf.Lerp(-maxCollisionPitchAngle * 0.3f, -maxCollisionPitchAngle * 0.92f, t);
            }

            float ramMul = GetRammingForceMultiplier();
            float shipCollisionDamage = Mathf.Max(0f, impactForceNewtons * ramMul * asteroidImpactForceToShipDamageScale);
            float asteroidCollisionDamage = Mathf.Max(0f, impactForceNewtons * ramMul * asteroidImpactForceToAsteroidDamageScale);

            if (shipCollisionDamage > 0.0001f)
            {
                // Self-inflicted collision damage: Team.None bypasses friendly-fire checks.
                TakeDamageServerRpc(shipCollisionDamage, TeamManager.Team.None, 0);
            }

            if (asteroidCollisionDamage > 0.0001f)
            {
                ulong attackerShipId = NetworkObject != null ? NetworkObjectId : 0ul;
                asteroid.TakeDamageServerRpc(asteroidCollisionDamage, attackerShipId);

                if (VisualEffectsManager.Instance != null)
                {
                    Vector3 asteroidHitPos = contact.point;
                    asteroidHitPos.y = Mathf.Max(asteroidHitPos.y, 0f);
                    VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                        asteroidHitPos,
                        (int)FloatingCountChannel.DamageAsteroid,
                        asteroidCollisionDamage,
                        (int)shipTeam.Value
                    );
                    VisualEffectsManager.Instance.SpawnAsteroidStatsFloatingTextServerRpc(
                        asteroidHitPos,
                        asteroid.RemainingHealth,
                        asteroid.RemainingGems,
                        (int)shipTeam.Value
                    );
                }
            }

            if (impactForceNewtons >= asteroidImpactForcePopupMin && VisualEffectsManager.Instance != null)
            {
                Vector3 impactPos = contact.point;
                impactPos.y = Mathf.Max(impactPos.y, 0f);
                VisualEffectsManager.Instance.SpawnImpactForceFloatingTextServerRpc(impactPos, impactForceNewtons);
            }

            _pendingAsteroidBounceVelocity = vOut;
            _hasPendingAsteroidBounce = true;

            rb.linearVelocity = new Vector3(vOut.x, 0f, vOut.z);
            currentVelocity = rb.linearVelocity;
        }

        private void OnCollisionExit(Collision collision)
        {
            Asteroid asteroid = collision.gameObject.GetComponent<Asteroid>();
            if (asteroid == null)
                asteroid = collision.gameObject.GetComponentInParent<Asteroid>();
            if (asteroid != null)
                _asteroidGrindFeedbackNextTimeByInstance.Remove(asteroid.GetInstanceID());
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

        private void OnCollisionStay(Collision collision)
        {
            if (rb == null || collision.contactCount == 0 || isDead.Value) return;

            bool canRam = (_isAIControlled && IsServer) || (!_isAIControlled && IsOwner);
            if (!canRam) return;

            Asteroid asteroid = collision.gameObject.GetComponent<Asteroid>();
            if (asteroid == null)
                asteroid = collision.gameObject.GetComponentInParent<Asteroid>();
            if (asteroid == null || asteroid.IsDestroyed) return;

            if (asteroidGrindPushToAsteroidDpsScale <= 0f) return;

            ContactPoint contact = collision.GetContact(0);
            Vector3 asteroidCenter = asteroid.transform.position;
            Vector3 n = contact.point - asteroidCenter;
            n.y = 0f;
            if (n.sqrMagnitude < 0.0001f) return;
            n.Normalize();

            Vector3 driveF = GetDrivePushForceXZ();
            float pushN = AsteroidRammingBehavior.ComputeNormalPushNewtons(n, driveF);
            if (pushN < asteroidGrindMinPushNewtons) return;

            float ramMul = GetRammingForceMultiplier();
            float dps = pushN * ramMul * asteroidGrindPushToAsteroidDpsScale;
            if (asteroidGrindMaxAsteroidDps > 0f)
                dps = Mathf.Min(dps, asteroidGrindMaxAsteroidDps);
            float asteroidGrindDamage = dps * Time.fixedDeltaTime;
            if (asteroidGrindDamage <= 0.0001f) return;

            ulong attackerShipId = NetworkObject != null ? NetworkObjectId : 0ul;
            asteroid.TakeDamageServerRpc(asteroidGrindDamage, attackerShipId);

            TryPlayAsteroidGrindFeedback(asteroid, contact.point, n, pushN, ramMul, asteroidGrindDamage);
        }

        /// <summary>Throttled VFX, sound, and floating numbers while grinding an asteroid (same flavor as collision enter).</summary>
        private void TryPlayAsteroidGrindFeedback(Asteroid asteroid, Vector3 hitWorldPos, Vector3 asteroidOutwardNormalXZ, float pushNewtons, float ramMul, float damageThisPulse)
        {
            if (asteroid == null) return;

            int id = asteroid.GetInstanceID();
            float now = Time.time;
            float interval = Mathf.Max(0.02f, asteroidGrindFeedbackInterval);
            if (_asteroidGrindFeedbackNextTimeByInstance.TryGetValue(id, out float nextOk) && now < nextOk)
                return;
            _asteroidGrindFeedbackNextTimeByInstance[id] = now + interval;

            float equivForce = Mathf.Max(pushNewtons * ramMul * Mathf.Max(0.01f, asteroidGrindFeedbackForceFromPushScale), 30f);

            float pitch = Mathf.Lerp(0.7f, 1.25f, Mathf.InverseLerp(25f, 1200f, equivForce));
            if (AudioManager.Instance != null)
                AudioManager.Instance.PlayAsteroidCollisionSound(pitch);

            if (VisualEffectsManager.Instance == null) return;

            float sev = ComputeCollisionVfxSeverityFromImpactForce(equivForce);
            sev = Mathf.Max(sev, 0.12f);
            TrySpawnWeaponCollisionImpactVfx(hitWorldPos, asteroidOutwardNormalXZ, sev, pitch);

            Vector3 asteroidHitPos = hitWorldPos;
            asteroidHitPos.y = Mathf.Max(asteroidHitPos.y, 0f);
            VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                asteroidHitPos,
                (int)FloatingCountChannel.DamageAsteroid,
                damageThisPulse,
                (int)shipTeam.Value
            );
            VisualEffectsManager.Instance.SpawnAsteroidStatsFloatingTextServerRpc(
                asteroidHitPos,
                asteroid.RemainingHealth,
                asteroid.RemainingGems,
                (int)shipTeam.Value
            );
        }

        private static ulong ToroidalShipPairKey(int instanceIdA, int instanceIdB)
        {
            uint ua = (uint)instanceIdA;
            uint ub = (uint)instanceIdB;
            return ua <= ub ? ((ulong)ua << 32) | ub : ((ulong)ub << 32) | ua;
        }

        private float GetShipCollisionRadiusXZ()
        {
            Collider c = rootCollider != null ? rootCollider : GetComponent<Collider>();
            if (c == null) return 0.05f;
            Bounds b = c.bounds;
            return Mathf.Max(0.05f, Mathf.Max(b.extents.x, b.extents.z) * 0.6f);
        }

        private float GetCollisionVfxShipMinRelativeSpeed()
        {
            if (VisualEffectsManager.Instance != null)
                return VisualEffectsManager.Instance.CollisionVfxShipMinRelativeSpeed;
            return Mathf.Max(0f, collisionWeaponVfxMinRelativeSpeed);
        }

        private float GetCollisionVfxShipMaxRelativeSpeed()
        {
            if (VisualEffectsManager.Instance != null)
                return VisualEffectsManager.Instance.CollisionVfxShipMaxRelativeSpeed;
            float min = GetCollisionVfxShipMinRelativeSpeed();
            return Mathf.Max(min + 0.01f, collisionWeaponVfxMaxRelativeSpeed);
        }

        private float GetCollisionVfxAsteroidMinImpactForce()
        {
            if (VisualEffectsManager.Instance != null)
                return VisualEffectsManager.Instance.CollisionVfxAsteroidMinImpactForce;
            return Mathf.Max(0f, collisionWeaponVfxMinImpactForceN);
        }

        private float GetCollisionVfxAsteroidMaxImpactForce()
        {
            if (VisualEffectsManager.Instance != null)
                return VisualEffectsManager.Instance.CollisionVfxAsteroidMaxImpactForce;
            float min = GetCollisionVfxAsteroidMinImpactForce();
            return Mathf.Max(min + 0.01f, collisionWeaponVfxMaxImpactForceN);
        }

        private float GetCollisionVfxScaleMinMultiplier()
        {
            if (VisualEffectsManager.Instance != null)
                return VisualEffectsManager.Instance.CollisionVfxMinScaleMultiplier;
            return Mathf.Max(0.01f, collisionWeaponVfxMinScaleMultiplier);
        }

        private float GetCollisionVfxScaleMaxMultiplier()
        {
            if (VisualEffectsManager.Instance != null)
                return VisualEffectsManager.Instance.CollisionVfxMaxScaleMultiplier;
            float min = GetCollisionVfxScaleMinMultiplier();
            return Mathf.Max(min + 0.01f, collisionWeaponVfxMaxScaleMultiplier);
        }

        private float ComputeCollisionVfxSeverityFromRelativeSpeed(float relativeSpeed)
        {
            float min = GetCollisionVfxShipMinRelativeSpeed();
            float max = GetCollisionVfxShipMaxRelativeSpeed();
            return Mathf.InverseLerp(min, max, relativeSpeed);
        }

        private float ComputeCollisionVfxSeverityFromImpactForce(float impactForceNewtons)
        {
            float min = GetCollisionVfxAsteroidMinImpactForce();
            float max = GetCollisionVfxAsteroidMaxImpactForce();
            return Mathf.InverseLerp(min, max, impactForceNewtons);
        }

        /// <summary>Bank index for Sci-Fi impact prefab (same as bullets). Returns -1 if no bank.</summary>
        private int GetCollisionImpactBulletBankIndex()
        {
            if (CombatSystem.Instance == null) return -1;
            int bankCount = CombatSystem.Instance.BulletPrefabBankCount;
            if (bankCount <= 0) return -1;
            int runtime = runtimeBulletPrefabIndex.Value;
            if (runtime >= 0) return runtime % bankCount;
            return 0;
        }

        /// <summary>Weapon-style impact burst (SciFi impact particle), scaled by severity01 (0..1).</summary>
        private void TrySpawnWeaponCollisionImpactVfx(Vector3 impactWorldPos, Vector3 outwardXZNormal, float severity01, float audioPitch)
        {
            if (VisualEffectsManager.Instance == null) return;
            impactWorldPos.y = Mathf.Max(impactWorldPos.y, 0f);
            Vector3 n = outwardXZNormal;
            n.y = 0f;
            if (n.sqrMagnitude < 1e-6f) n = transform.forward;
            n.Normalize();
            float scaleMul = Mathf.Lerp(GetCollisionVfxScaleMinMultiplier(), GetCollisionVfxScaleMaxMultiplier(), Mathf.Clamp01(severity01));
            int bank = GetCollisionImpactBulletBankIndex();
            VisualEffectsManager.Instance.SpawnWeaponCollisionImpactServerRpc(
                impactWorldPos, n, scaleMul, audioPitch, bank, (int)shipTeam.Value);
        }

        /// <summary>
        /// Ships keep unwrapped world positions; Unity colliders only see raw separation, so hulls can overlap
        /// on the torus without <see cref="OnCollisionEnter"/>. Resolve overlap using shortest toroidal offset
        /// (each authoritative body corrects itself, matching owner physics + server AI).
        /// </summary>
        private void TickToroidalShipVsShipCollision()
        {
            bool auth = (_isAIControlled && IsServer) || (!_isAIControlled && IsOwner);
            if (!auth || rb == null) return;

            Vector3 myPos = rb.position;
            myPos.y = 0f;
            float myR = GetShipCollisionRadiusXZ();

            for (int i = 0; i < AllStarships.Count; i++)
            {
                Starship other = AllStarships[i];
                if (other == null || other == this) continue;
                if (other.IsDead || other.GemMoonDocked) continue;

                Rigidbody otherRb = other.GetComponent<Rigidbody>();
                if (otherRb == null) continue;

                Vector3 otherPos = otherRb.position;
                otherPos.y = 0f;

                float dist = ToroidalMap.ToroidalDistance(myPos, otherPos);
                float otherR = other.GetShipCollisionRadiusXZ();
                float combined = myR + otherR;
                if (dist >= combined - 0.0001f) continue;

                Vector3 toOther = ToroidalMap.ShortestWorldOffsetXZ(myPos, otherPos);
                if (toOther.sqrMagnitude < 1e-10f)
                {
                    toOther = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
                    if (toOther.sqrMagnitude < 1e-10f)
                        toOther = transform.forward;
                    toOther.y = 0f;
                }
                Vector3 n = toOther.normalized;

                float penetration = combined - Mathf.Max(dist, 0.0001f);
                float half = penetration * 0.5f;
                Vector3 newPos = rb.position + (-n) * half;
                rb.MovePosition(newPos);

                Vector3 vMe = rb.linearVelocity;
                vMe.y = 0f;
                Vector3 vO = otherRb.linearVelocity;
                vO.y = 0f;
                float relSpeed = (vMe - vO).magnitude;
                bool playSound = relSpeed >= 2f && AudioManager.Instance != null;
                bool playVfx = relSpeed >= GetCollisionVfxShipMinRelativeSpeed() && VisualEffectsManager.Instance != null;
                if (playSound || playVfx)
                {
                    ulong pairKey = ToroidalShipPairKey(GetInstanceID(), other.GetInstanceID());
                    float now = Time.time;
                    if (!_toroidalShipPairLastSoundTime.TryGetValue(pairKey, out float last) || now - last >= 0.22f)
                    {
                        _toroidalShipPairLastSoundTime[pairKey] = now;
                        float pitch = Mathf.Lerp(0.8f, 1.35f, Mathf.InverseLerp(2f, 35f, relSpeed));
                        if (playSound)
                            AudioManager.Instance.PlayShipCollisionSound(pitch);
                        if (playVfx)
                        {
                            Vector3 impactPos = myPos + (-n) * myR;
                            impactPos.y = 0f;
                            Vector3 outward = -n;
                            outward.y = 0f;
                            if (outward.sqrMagnitude < 1e-6f) outward = transform.forward;
                            outward.Normalize();
                            float sev = ComputeCollisionVfxSeverityFromRelativeSpeed(relSpeed);
                            TrySpawnWeaponCollisionImpactVfx(impactPos, outward, sev, pitch);
                        }
                    }
                }
            }
        }

        /// <summary>Server-only gem credit from pickups (same as <see cref="AddGemsServerRpc"/>; avoids invoking a ServerRpc from another NetworkBehaviour on the server).</summary>
        public void AddGemsFromPickupServer(float amount, bool playCollectSound = false)
        {
            if (!IsServer) return;
            ApplyAddGemsOnServer(amount, playCollectSound);
        }

        private void ApplyAddGemsOnServer(float amount, bool playCollectSound)
        {
            currentGems.Value = Mathf.Min(currentGems.Value + amount, GemCapacity);
            if (playCollectSound)
                PlayGemCollectSoundClientRpc(amount);
        }

        [ServerRpc(RequireOwnership = false)]
        public void AddGemsServerRpc(float amount, bool playCollectSound = false)
        {
            ApplyAddGemsOnServer(amount, playCollectSound);
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

        /// <summary>Server-only gem removal used by moon deposit path (avoids nested ServerRpc from server authority).</summary>
        public void RemoveGemsFromDepositServer(float amount)
        {
            if (!IsServer) return;
            ApplyRemoveGemsOnServer(amount);
        }

        private void ApplyRemoveGemsOnServer(float amount)
        {
            currentGems.Value = Mathf.Max(0f, currentGems.Value - amount);
            TryDieIfHullAndGemsDepleted(0);
        }

        [ServerRpc(RequireOwnership = false)]
        public void RemoveGemsServerRpc(float amount)
        {
            ApplyRemoveGemsOnServer(amount);
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

        /// <summary>
        /// Server: for AI ships, ensure networked chassis index/id are set and the matching prefab is applied so all clients see the correct hull.
        /// Call after <see cref="SetShipData"/> when spawning AI (player starter logic in <see cref="OnNetworkSpawn"/> does not run for AI).
        /// </summary>
        public void EnsureSyncedChassisForAiVisual()
        {
            if (!IsServer || CardShopSystem.Instance == null) return;

            if (currentChassisIndex.Value < 0)
            {
                string starterChassisId = CardShopSystem.Instance.GetStarterChassisId();
                GameObject starterPrefab = !string.IsNullOrEmpty(starterChassisId)
                    ? CardShopSystem.Instance.GetShipPrefabForChassisId(starterChassisId)
                    : null;
                if (starterPrefab == null)
                    starterPrefab = CardShopSystem.Instance.GetShipPrefabForChassisIndex(0);
                if (starterPrefab != null)
                {
                    ApplyShipVisualFromPrefab(starterPrefab);
                    SetCurrentChassisIndex(0);
                    if (!string.IsNullOrEmpty(starterChassisId))
                        SetCurrentChassisId(starterChassisId);
                    _lastAppliedChassisIndex = 0;
                }
            }
            else
            {
                string cid = currentChassisId.Value.ToString();
                GameObject prefab = !string.IsNullOrEmpty(cid)
                    ? CardShopSystem.Instance.GetShipPrefabForChassisId(cid)
                    : null;
                if (prefab == null && currentChassisIndex.Value >= 0)
                    prefab = CardShopSystem.Instance.GetShipPrefabForChassisIndex(currentChassisIndex.Value);
                if (prefab != null)
                {
                    ApplyShipVisualFromPrefab(prefab);
                    _lastAppliedChassisIndex = currentChassisIndex.Value;
                }
            }
        }

        /// <summary>
        /// Re-cache AI control after <see cref="TitanOrbit.AI.AIStarshipController"/> is added at runtime, and ensure the enemy world stats panel exists.
        /// </summary>
        public void RefreshAIControlledFlag()
        {
            _isAIControlled = GetComponent<TitanOrbit.AI.AIStarshipController>() != null;
            if (!_isAIControlled) return;
            if (transform.Find(EnemyShipWorldStatsPanel.ChildObjectName) != null) return;
            EnemyShipWorldStatsPanel.CreateAsStarshipChild(this);
        }

        /// <summary>Server-only: detect if ship is inside a planet's orbit zone (e.g. after spawning there). OnTriggerEnter doesn't fire for objects that start inside.</summary>
        /// <summary>Server: true if the given XZ world position lies in any planet's orbit band (same ring math as <see cref="TryDetectOrbitZoneServer"/>).</summary>
        /// <remarks>
        /// Used by <see cref="FireServerRpc"/> instead of the cached <see cref="currentOrbitPlanet"/> field, which is not replicated
        /// and can disagree between dedicated server and owning client (blocking all shots while the client can still press fire).
        /// </remarks>
        private static bool ServerWorldPositionInsideAnyOrbitZone(Vector3 shipWorldPos)
        {
            shipWorldPos.y = 0f;
            foreach (var planet in Planet.AllPlanets)
            {
                if (planet == null) continue;
                Vector3 toShip = shipWorldPos - planet.transform.position;
                toShip.y = 0f;
                float dist = toShip.magnitude;
                float inner = planet.PlanetSize * 0.5f;
                float outer = planet.PlanetSize * planet.GetOrbitZoneOuterRadiusLocal();
                if (dist >= inner && dist <= outer)
                    return true;
            }
            return false;
        }

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
        public bool IsLocalPlayerShip()
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
            // Re-hide on clients if visual was just applied via chassis-sync before the team is set.
            // OnShipTeamValueChanged handles the show transition once the player picks a team.
            if (!_isAIControlled && shipTeam.Value == TeamManager.Team.None)
                SetShipBodyVisibleLocal(false);
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
            currentVisualFamilyDefinition = previewFamilyDef;

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
            ApplyHullIdentityColor();
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
                rotationSpeed = Mathf.Max(1f, ApplyShipLevelMobilityScale(s.turnSpeed, perLvl));
                rotationSpeedFromShipFamilyDefinition = true;
                gemCapacity = Mathf.Max(0f, s.maxGems + s.maxGemsPerLevel * perLvl);
                peopleCapacity = Mathf.Max(0f, s.maxPeople + s.maxPeoplePerLevel * perLvl);
                rammingPower = Mathf.Max(0f, baseRammingPower + s.rammingPower + s.rammingPowerPerLevel * perLvl);

                // Movement: proportional penalty per level (see ApplyShipLevelMobilityScale).
                float moveVal = Mathf.Max(0.1f, ApplyShipLevelMobilityScale(s.moveSpeed, perLvl));
                // Top speed is non-cumulative (max movement component). Acceleration is cumulative.
                float sumAccelerationCap = 0f;
                float maxEngineMoveSpeed = 0f;
                float maxThrusterMoveSpeed = 0f;
                if (matchedComponentIds != null && perComponentStats != null)
                {
                    for (int k = 0; k < matchedComponentIds.Count && k < perComponentStats.Count; k++)
                    {
                        string cid = matchedComponentIds[k];
                        ShipComponentAbilityStats comp = perComponentStats[k];
                        float partSpeed = Mathf.Max(0.1f, ApplyShipLevelMobilityScale(comp.moveSpeed, perLvl));
                        float partAcceleration = Mathf.Max(0f, comp.accelerationCap + comp.accelerationCapPerLevel * perLvl);
                        if (ShipComponentAbilityStats.IsEngineComponent(cid))
                        {
                            sumAccelerationCap += partAcceleration;
                            if (partSpeed > maxEngineMoveSpeed) maxEngineMoveSpeed = partSpeed;
                        }
                        else if (ShipComponentAbilityStats.IsThrusterComponent(cid))
                        {
                            sumAccelerationCap += partAcceleration;
                            if (partSpeed > maxThrusterMoveSpeed) maxThrusterMoveSpeed = partSpeed;
                        }
                    }
                }
                float accelFallback = Mathf.Max(0f, s.accelerationCap + s.accelerationCapPerLevel * perLvl);
                componentEngineThrust = Mathf.Max(0f, sumAccelerationCap > 0f ? sumAccelerationCap : (accelFallback > 0f ? accelFallback : moveVal));
                float capFromParts = maxEngineMoveSpeed > 0f ? maxEngineMoveSpeed : maxThrusterMoveSpeed;
                componentEngineMaxSpeed = Mathf.Max(0.1f, capFromParts > 0f ? capFromParts : engineThrust * 0.5f);

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
                rammingPower = Mathf.Max(0f, baseRammingPower + stats.cockpitScaleTotal);

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
            thrusterVfxBlend = 0f;

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
                    if (!usedPerComponent && previewFamilyDef != null && wt != null && !string.IsNullOrEmpty(componentId) && previewFamilyDef.TryResolveStatsForComponent(componentId, out var defStats))
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
            if ((thrusterVfxPrefab != null || (thrusterJetFlameBank != null && thrusterJetFlameBank.Count > 0)) && stats.thrusterTransforms != null)
            {
                foreach (Transform t in stats.thrusterTransforms)
                {
                    if (t == null) continue;
                    GameObject thrusterPrefab = ResolveThrusterVfxPrefabForTransform(t);
                    if (thrusterPrefab == null) continue;
                    GameObject go = Instantiate(thrusterPrefab, t);
                    go.transform.localPosition = thrusterVfxLocalOffset;
                    go.transform.localRotation = Quaternion.Euler(thrusterVfxLocalEuler);
                    go.transform.localScale = Vector3.one * Mathf.Clamp01(thrusterVfxIdleScale);
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

        /// <summary>Server only: refills combat vitals to their current effective caps (used after ship/chassis upgrades).</summary>
        public void RefillCombatVitalsToMaxFromServer()
        {
            if (!IsServer) return;
            currentHealth.Value = MaxHealth;
            currentEnergy.Value = EffectiveEnergyCapacity;
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

        private float GetCardGemDepositSpeedMultiplier()
        {
            if (equippedCards == null || equippedCards.Count == 0) return 1f;
            if (_cardStatsCacheFrame != Time.frameCount) RefreshCardStatsCache();
            return _cachedCardGemDepositSpeedMultiplier;
        }

        private float GetCardPeopleTransferSpeedMultiplier()
        {
            if (equippedCards == null || equippedCards.Count == 0) return 1f;
            if (_cardStatsCacheFrame != Time.frameCount) RefreshCardStatsCache();
            return _cachedCardPeopleTransferSpeedMultiplier;
        }

        /// <summary>Orbit people unload rate scale (base 1 person/s). Wire card multipliers here when cards define unload speed.</summary>
        private float GetPeopleUnloadSpeedMultiplier() => 1f;

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

        /// <summary>Releases runtime baked meshes used for death debris so they do not leak.</summary>
        private sealed class DestroyMeshWithGameObject : MonoBehaviour
        {
            public Mesh Mesh;

            private void OnDestroy()
            {
                if (Mesh != null)
                    Destroy(Mesh);
            }
        }

    }

    /// <summary>Server-side marker for ship death debris that can absorb enemy bullets.</summary>
    public sealed class ShipDeathDebris : MonoBehaviour
    {
        private TeamManager.Team ownerTeam = TeamManager.Team.None;
        private int remainingHits;
        private float activeUntilTime;

        public void Initialize(TeamManager.Team team, int bulletHitsToBreak, float shieldDurationSeconds)
        {
            ownerTeam = team;
            remainingHits = Mathf.Max(1, bulletHitsToBreak);
            activeUntilTime = Time.time + Mathf.Max(0.05f, shieldDurationSeconds);
        }

        public bool TryAbsorbBullet(TeamManager.Team bulletTeam)
        {
            if (Time.time > activeUntilTime)
                return false;
            if (ownerTeam != TeamManager.Team.None && bulletTeam == ownerTeam)
                return false;

            remainingHits--;
            if (remainingHits <= 0)
                Destroy(gameObject);
            return true;
        }

        /// <summary>Read-only: whether an enemy bullet would be absorbed (for client-only tracer VFX; does not change state).</summary>
        public bool WouldAbsorbEnemyBulletCosmetic(TeamManager.Team bulletTeam)
        {
            if (Time.time > activeUntilTime)
                return false;
            if (ownerTeam != TeamManager.Team.None && bulletTeam == ownerTeam)
                return false;
            return remainingHits > 0;
        }
    }
}
