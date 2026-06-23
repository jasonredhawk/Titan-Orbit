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
using TitanOrbit.Services;
using TitanOrbit.Networking;
using TitanOrbit.Audio;
using TitanOrbit.Simulation;
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

    /// <summary>Serializable store equipment entry for syncing equipped support items to clients.</summary>
    public struct EquippedEquipmentEntry : INetworkSerializable, System.IEquatable<EquippedEquipmentEntry>
    {
        public int itemType;
        public int remainingCharges;
        public Unity.Collections.FixedString64Bytes componentId;
        public float localPosX;
        public float localPosY;
        public float localPosZ;
        public float localRotX;
        public float localRotY;
        public float localRotZ;

        public StoreItemType ItemType => (StoreItemType)itemType;
        public bool IsShipComponent => ItemType == StoreItemType.ShipComponent;
        public string ComponentId => componentId.ToString();
        public Vector3 LocalPosition => new Vector3(localPosX, localPosY, localPosZ);
        public Vector3 LocalEulerAngles => new Vector3(localRotX, localRotY, localRotZ);
        public Quaternion LocalRotation => Quaternion.Euler(localRotX, localRotY, localRotZ);

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref itemType);
            serializer.SerializeValue(ref remainingCharges);
            serializer.SerializeValue(ref componentId);
            serializer.SerializeValue(ref localPosX);
            serializer.SerializeValue(ref localPosY);
            serializer.SerializeValue(ref localPosZ);
            serializer.SerializeValue(ref localRotX);
            serializer.SerializeValue(ref localRotY);
            serializer.SerializeValue(ref localRotZ);
        }

        public bool Equals(EquippedEquipmentEntry other) =>
            itemType == other.itemType &&
            remainingCharges == other.remainingCharges &&
            componentId.Equals(other.componentId) &&
            localPosX == other.localPosX &&
            localPosY == other.localPosY &&
            localPosZ == other.localPosZ &&
            localRotX == other.localRotX &&
            localRotY == other.localRotY &&
            localRotZ == other.localRotZ;
    }

    /// <summary>
    /// Base starship controller for player-controlled ships
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [DefaultExecutionOrder(60000)] // Run last so banking is not overwritten by transform sync or other LateUpdates
    public partial class Starship : NetworkBehaviour
    {
        /// <summary>Global registry of all active starships to avoid repeated FindObjectsByType scans.</summary>
        public static readonly System.Collections.Generic.List<Starship> AllStarships = new System.Collections.Generic.List<Starship>();

        // Cached references to avoid repeated global searches from Update.
        private static TitanOrbit.UI.HomePlanetOrbitUI s_cachedOrbitUI;
        private static TitanOrbit.Camera.CameraController s_cachedCameraController;
        private bool _orbitUiVisible;
        private float _gemMoonLandingCompleteTime = -1f;
        private const float GemMoonDockMenuDelayAfterLandingSeconds = 0.5f;
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
        [Tooltip("Scale while inside a friendly gem-moon orbit shell (defensive stance). Landing eases from this toward surface scale.")]
        [SerializeField, Range(0.05f, 1.5f)]
        private float gemMoonDockScaleAtOrbitEdge = 1f / 3f;
        [Tooltip("Seconds to ease ship visual scale when entering or leaving the friendly gem-moon orbit shell.")]
        [SerializeField] private float gemMoonOrbitZoneScaleTransitionSeconds = 0.35f;
        [Tooltip("Scale when fully blended to the moon surface (blend 1). Set to 1 for no shrink. Overall ship size also uses Ship Visual Scale Multiplier on this component.")]
        [SerializeField, Range(0.05f, 1.5f), FormerlySerializedAs("gemMoonLandingVisualScale")]
        private float gemMoonDockScaleAtSurface = 0.55f;
        [Tooltip("Extra clearance beyond moon visual radius + scaled hull so the ship sits on top of the moon mesh (fraction of moon radius).")]
        [SerializeField, Range(0f, 0.25f)]
        private float gemMoonSurfaceStandoffOverMoonRadius = 0.08f;
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
        private uint gemMoonUndockGraceEndSimTick;
        private Vector3 gemMoonUndockCachedMoonPos;
        /// <summary>Server: time spent nearly stationary inside a friendly gem-moon zone before auto-dock is allowed.</summary>
        private float _serverGemMoonLandingDwellSeconds;
        private const float GemMoonLandingDwellSecondsRequired = 0.45f;

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
        /// <summary>Trigger-only sphere for gem-moon dock detection; sized to hull visual without enlarging physics collider.</summary>
        private SphereCollider moonDockProbeCollider;
        /// <summary>Cached XZ scoop radius for fly-through gem pickup (derived from visible hull).</summary>
        private float cachedGemFlythroughPickupRadius = -1f;

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
        [Tooltip("How much component mesh scale reflects attribute upgrade grid levels. 0.5 = 10% stat increase → 5% bigger component; 1 = 1:1. Does not include ship level (see Ship Level Scale Per Level).")]
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
        private readonly List<WingTractorBeamSlot> wingTractorBeams = new List<WingTractorBeamSlot>();
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
        private bool _subscribedEquippedEquipmentVisuals;
        private int _equipmentVisualRebuildSuppressDepth;

        private readonly List<Vector3> _authoredWingPositions = new List<Vector3>();
        private readonly List<Quaternion> _authoredWingRotations = new List<Quaternion>();
        private readonly List<Vector3> _authoredWeaponPositions = new List<Vector3>();
        private readonly List<Quaternion> _authoredWeaponRotations = new List<Quaternion>();
        private readonly List<Vector3> _authoredCockpitPositions = new List<Vector3>();
        private readonly List<Quaternion> _authoredCockpitRotations = new List<Quaternion>();
        private readonly List<Vector3> _authoredPartPositions = new List<Vector3>();
        private readonly List<Quaternion> _authoredPartRotations = new List<Quaternion>();
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
        /// <summary>Level-1 summed healthCap for the current hull (no per-level bonus). Ramming mass scales with MaxHealth / this.</summary>
        private float _chassisReferenceHealth = 25f;
        /// <summary>Thrust force from engine components. Applied via AddForce; acceleration = thrust/mass.</summary>
        private float componentEngineThrust = 0f;
        /// <summary>Max speed from chassis: best single engine (or best thruster if no engines). Not summed across engines.</summary>
        private float componentEngineMaxSpeed = 0f;

        /// <summary>Bullets from Weapon components only (built from ShipFamilyDefinition when chassis is applied).</summary>
        private WeaponConfig bulletConfig;
        private float[] bulletLastFireTime;
        /// <summary>Per-weapon authored energy cap/regen (from weapon components). Summed into one shared firing pool.</summary>
        private float[] cannonEnergyCapacityBase;
        private float[] cannonEnergyRegenBase;

        [Header("Collision")]
        [Tooltip("Max bounce (coefficient of restitution along the impact normal) for light/low-mass ships. Ramming power and hull mass reduce this toward the minimum below; suppressed bounce becomes impact damage.")]
        [SerializeField, Range(0f, 1f), FormerlySerializedAs("asteroidCollisionEnergyRetention")]
        private float asteroidCollisionNormalSpeedRetention = 0.93f;
        [Tooltip("Restitution at very high ramming power (0 = stick/slide on the normal, no rebound). Clamped vs max above.")]
        [SerializeField, Range(0f, 1f)] private float asteroidRammingMinRestitution = 0f;
        [Tooltip("Ramming power at or below this keeps max bounce. Only excess above this pulls restitution toward the minimum (so baseline ship stats stay bouncy).")]
        [SerializeField, Min(0f)] private float asteroidRammingRestitutionThreshold = 6f;
        [Tooltip("When excess ramming (above threshold) equals this value, restitution is halfway between max and min. Higher = need more investment before bounce dies off.")]
        [SerializeField, Min(0.01f), FormerlySerializedAs("asteroidRammingRestitutionReferencePower")]
        private float asteroidRammingRestitutionReferenceExcess = 14f;
        [Tooltip("Hull mass ratio (vs empty level-1 reference) above 1.0 that pulls restitution halfway to min. Lower = heavy ships stop bouncing sooner.")]
        [SerializeField, Min(0.01f)] private float asteroidRammingRestitutionReferenceMassRatioExcess = 0.4f;
        [Tooltip("Coefficient of restitution for ship-vs-ship collisions (friendly or enemy). Heavier ships move less via mass-weighted impulse.")]
        [SerializeField, Range(0f, 1f)] private float shipShipRestitution = 0.42f;
        [Tooltip("Continuous push into an asteroid: asteroid DPS per Newton of thrust along the inward normal. Scaled with ship collision damage (same proportion).")]
        [SerializeField, Min(0f)] private float asteroidGrindPushToAsteroidDpsScale = 0.003f;
        [Tooltip("Ignore grind below this push (N) to avoid jitter when nearly parallel to the surface.")]
        [SerializeField, Min(0f)] private float asteroidGrindMinPushNewtons = 8f;
        [Tooltip("Cap grind DPS to the asteroid so a stuck ship cannot melt a rock instantly.")]
        [SerializeField, Min(0f)] private float asteroidGrindMaxAsteroidDps = 120f;
        [Tooltip("Min seconds between grind damage pulses (asteroid/self chip damage, gem expulsion, VFX, and floating text) per asteroid contact. 0.25 = 4 pulses/sec.")]
        [SerializeField, Min(0.02f)] private float asteroidGrindFeedbackInterval = 0.25f;
        [Tooltip("Maps grind push (× ram multiplier) to collision-style impact force for VFX/sound intensity.")]
        [SerializeField, Min(0.01f)] private float asteroidGrindFeedbackForceFromPushScale = 2.75f;
        [Tooltip("Minimum impact force (N) required before showing a floating impact number on asteroid collisions.")]
        [SerializeField, Min(0f)] private float asteroidImpactForcePopupMin = 80f;
        [Tooltip("Legacy ratio anchor for self vs asteroid ram chip damage (see ShipComponentRammingSuggestions.SelfToAsteroidDamageRatio).")]
        [SerializeField, Min(0f)] private float asteroidImpactForceToShipDamageScale = 0.000625f;
        [Tooltip("Legacy ratio anchor for self vs asteroid ram chip damage (see ShipComponentRammingSuggestions.SelfToAsteroidDamageRatio).")]
        [SerializeField, Min(0f)] private float asteroidImpactForceToAsteroidDamageScale = 0.000375f;
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
        /// <summary>Asteroid ram/grind impact bursts: 0.4 = 60% smaller than severity-based scale.</summary>
        private const float RamGrindImpactVfxScaleFactor = 0.4f;

        private bool _hasPendingAsteroidBounce;
        private Vector3 _pendingAsteroidBounceVelocity;
        private bool _hasPendingGemMoonShieldRepel;
        private Vector3 _pendingGemMoonShieldRepelVelocity;
        /// <summary>XZ velocity at end of last FixedUpdate (pre-collision reference when relativeVelocity is ambiguous).</summary>
        private Vector3 _lastFixedPlayPlaneVelocity;
        /// <summary>Cooldown for ship–ship scrape sounds from toroidal overlap (pair key → last Time.time).</summary>
        private readonly Dictionary<ulong, float> _toroidalShipPairLastSoundTime = new Dictionary<ulong, float>();
        /// <summary>Cooldown for ship–ship impulse so resting overlap does not re-fire every FixedUpdate.</summary>
        private readonly Dictionary<ulong, float> _toroidalShipPairLastImpulseTime = new Dictionary<ulong, float>();
        private bool _hasPendingShipShipBounce;
        private Vector3 _pendingShipShipBounceVelocity;
        private Vector3 _collisionVelEstPrevPos;
        private bool _collisionVelEstHasPrev;
        private Vector3 _collisionPlanarVelocityEstimate;
        /// <summary>Per-asteroid instance: next Time.time allowed for grind VFX/sound/floating damage.</summary>
        private readonly Dictionary<int, float> _asteroidGrindFeedbackNextTimeByInstance = new Dictionary<int, float>();
        /// <summary>Asteroids the ship is currently colliding with (ram contact).</summary>
        private readonly HashSet<Asteroid> _asteroidRamContactInstances = new HashSet<Asteroid>();
        /// <summary>Asteroids that reported collision this physics step (for stale contact cleanup).</summary>
        private readonly HashSet<Asteroid> _asteroidRamContactsThisPhysicsStep = new HashSet<Asteroid>();
        /// <summary>Avoid double-firing destroy shake when predictive kill and despawn both fire.</summary>
        private readonly HashSet<Asteroid> _asteroidDestroyShakeTriggered = new HashSet<Asteroid>();
        /// <summary>Server: Time.time when hull last took damage; regen waits until healthRegenDelayAfterDamage after this.</summary>
        private float lastHullDamageServerTime = -999f;

        private ClientRpcParams OwnerOnlyClientRpcParams => new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
        };

        private bool IsValidWeaponFirePointIndex(int index)
        {
            return bulletFirePoints != null
                && index >= 0
                && index < bulletFirePoints.Count
                && bulletFirePoints[index] != null
                && bulletFirePoints[index] != transform;
        }

        private void EnsureBulletLastFireTime()
        {
            int bn = bulletConfig != null && bulletConfig.cannons != null ? bulletConfig.cannons.Count : 0;
            if (bulletLastFireTime == null || bulletLastFireTime.Length != bn)
            {
                bulletLastFireTime = new float[bn];
                for (int i = 0; i < bn; i++) bulletLastFireTime[i] = -999f;
            }
        }

        private bool HasWeaponComponentEnergy =>
            cannonEnergyCapacityBase != null
            && cannonEnergyCapacityBase.Length > 0
            && bulletConfig != null
            && bulletConfig.cannons != null
            && bulletConfig.cannons.Count > 0;

        /// <summary>Multiple weapons share one energy pool: volley when full, sequential round-robin when low.</summary>
        private bool UsesSharedWeaponEnergyPool =>
            HasWeaponComponentEnergy
            && bulletConfig.cannons.Count > 1;

        private readonly List<int> _cannonsToFireScratch = new List<int>(8);
        private readonly List<int> _sharedPoolReadyCannonsScratch = new List<int>(8);
        private readonly List<float> _sharedPoolReadyCostsScratch = new List<float>(8);
        /// <summary>Owner: local weapon pool while hold-firing (avoids fighting replicated energy each shot).</summary>
        private bool ownerFiringSessionActive;
        private float ownerFiringEnergy;
        private int ownerFiringPoolCursor = -1;
        /// <summary>Clients: last synced energy for smooth HUD regen between network updates.</summary>
        private float _energyRegenBaseline;
        private double _energyRegenBaselineServerTime;
        /// <summary>Clients: last synced health for smooth HUD regen between network updates.</summary>
        private float _healthRegenBaseline;
        private double _healthRegenBaselineServerTime;
        private double _healthRegenDelayUntilServerTime;
        /// <summary>Ignore small client/server stat drift so regen bars do not dip on every network tick.</summary>
        private const float StatDisplaySyncDeadband = 1.5f;
        /// <summary>Treat larger drops as real spend/damage; regen ticks may wobble slightly below prediction.</summary>
        private const float StatDisplaySpendSnapThreshold = 0.75f;

        private void EnsureCannonEnergyState(int cannonCount)
        {
            if (cannonCount <= 0)
            {
                cannonEnergyCapacityBase = null;
                cannonEnergyRegenBase = null;
                return;
            }

            if (cannonEnergyCapacityBase == null || cannonEnergyCapacityBase.Length != cannonCount)
            {
                cannonEnergyCapacityBase = new float[cannonCount];
                cannonEnergyRegenBase = new float[cannonCount];
            }
        }

        private static void ExtractWeaponEnergyFromStats(
            ShipComponentAbilityStats stats,
            float perLvlWeapon,
            out float cap,
            out float regen)
        {
            cap = Mathf.Max(0.1f, stats.energyCap + stats.energyCapPerLevel * perLvlWeapon);
            regen = Mathf.Max(0f, stats.energyRegen + stats.energyRegenPerLevel * perLvlWeapon);
        }

        private float GetSummedWeaponEnergyCapacityBase()
        {
            if (!HasWeaponComponentEnergy)
                return 0f;
            float sum = 0f;
            for (int i = 0; i < cannonEnergyCapacityBase.Length; i++)
                sum += Mathf.Max(0.1f, cannonEnergyCapacityBase[i]);
            return sum;
        }

        private float GetSummedWeaponEnergyRegenBase()
        {
            if (!HasWeaponComponentEnergy)
                return 0f;
            float sum = 0f;
            for (int i = 0; i < cannonEnergyRegenBase.Length; i++)
                sum += Mathf.Max(0f, cannonEnergyRegenBase[i]);
            return sum;
        }

        private float GetFiringPoolEnergy()
        {
            if (IsOwner && ownerFiringSessionActive)
                return ownerFiringEnergy;
            return currentEnergy.Value;
        }

        private bool CannonHasEnergyForShot(int cannonIndex, float cost) =>
            GetFiringPoolEnergy() >= cost;

        private bool TryConsumeFiringPoolEnergy(int cannonIndex, float cost)
        {
            if (IsOwner && ownerFiringSessionActive)
            {
                if (ownerFiringEnergy < cost)
                    return false;
                ownerFiringEnergy = Mathf.Max(0f, ownerFiringEnergy - cost);
                if (UsesSharedWeaponEnergyPool)
                    ownerFiringPoolCursor = cannonIndex;
                return true;
            }

            if (!IsServer)
                return false;
            if (currentEnergy.Value < cost)
                return false;
            currentEnergy.Value = Mathf.Max(0f, currentEnergy.Value - cost);
            if (UsesSharedWeaponEnergyPool)
                lastSharedPoolWeaponFiredIndex.Value = cannonIndex;
            return true;
        }

        private int GetSharedPoolRoundRobinCursor()
        {
            if (!UsesSharedWeaponEnergyPool)
                return -1;
            if (IsOwner && ownerFiringSessionActive)
                return ownerFiringPoolCursor;
            return lastSharedPoolWeaponFiredIndex.Value;
        }

        private void ResetSharedPoolWeaponFiredIndex()
        {
            if (IsServer)
                lastSharedPoolWeaponFiredIndex.Value = -1;
            ownerFiringPoolCursor = -1;
        }

        private void BeginOwnerWeaponFiringSession()
        {
            if (!IsOwner)
                return;
            ownerFiringSessionActive = true;
            ownerFiringEnergy = currentEnergy.Value;
            ownerFiringPoolCursor = lastSharedPoolWeaponFiredIndex.Value;
        }

        private void EndOwnerWeaponFiringSession()
        {
            if (!IsOwner)
                return;
            ownerFiringSessionActive = false;
            float authoritative = currentEnergy.Value;
            float deadband = GetStatDisplaySyncDeadband(EnergyCapacity);
            _energyRegenBaseline = Mathf.Abs(ownerFiringEnergy - authoritative) <= deadband
                ? ownerFiringEnergy
                : authoritative;
            _energyRegenBaselineServerTime = GetServerTimeNowSeconds();
        }

        private void TickOwnerFiringEnergyRegen()
        {
            if (!IsOwner || !ownerFiringSessionActive)
                return;

            float cap = EffectiveEnergyCapacity;
            if (ownerFiringEnergy >= cap)
                return;

            float regen = EffectiveEnergyRegen * Time.fixedDeltaTime;
            if (GameManager.Instance != null && GameManager.Instance.DebugMode)
                regen *= 100f;
            ownerFiringEnergy = Mathf.Min(ownerFiringEnergy + regen, cap);
        }

        /// <summary>Strict next weapon index in shared-pool round-robin (0 → 1 → … → N-1 → 0).</summary>
        private int ResolveSharedPoolTurnCannonIndex()
        {
            if (bulletConfig?.cannons == null || bulletConfig.cannons.Count == 0)
                return -1;

            int count = bulletConfig.cannons.Count;
            int cursor = GetSharedPoolRoundRobinCursor();
            int turn = cursor < 0 ? 0 : (cursor + 1) % count;
            if (turn < 0 || turn >= count)
                return -1;
            return turn;
        }

        private bool TryCanCannonFireRateReady(int cannonIndex, out float energyCostPerShot)
        {
            energyCostPerShot = 0f;
            if (bulletConfig?.cannons == null || cannonIndex < 0 || cannonIndex >= bulletConfig.cannons.Count)
                return false;
            if (!IsValidWeaponFirePointIndex(cannonIndex))
                return false;

            var c = bulletConfig.cannons[cannonIndex];
            int bankIdx = ResolveBulletBankIndexForCannon(c, CombatSystem.Instance);
            ResolveEffectiveCannonStats(c, bankIdx, out _, out _, out float effectiveFireRate, out energyCostPerShot);
            EnsureBulletLastFireTime();
            if (cannonIndex < bulletLastFireTime.Length
                && Time.fixedTime - bulletLastFireTime[cannonIndex] < 1f / Mathf.Max(0.01f, effectiveFireRate))
                return false;
            return true;
        }

        private bool TryCanCannonFireNow(int cannonIndex, out float energyCostPerShot, bool requireEnergy = true)
        {
            if (!TryCanCannonFireRateReady(cannonIndex, out energyCostPerShot))
                return false;
            if (requireEnergy && !CannonHasEnergyForShot(cannonIndex, energyCostPerShot))
                return false;
            return true;
        }

        /// <summary>Sum of per-shot costs for every weapon in the shared pool (volley threshold).</summary>
        private float GetSharedPoolVolleyEnergyThreshold()
        {
            if (bulletConfig?.cannons == null || bulletConfig.cannons.Count == 0)
                return 0f;

            float sum = 0f;
            int count = bulletConfig.cannons.Count;
            for (int i = 0; i < count; i++)
            {
                if (!IsValidWeaponFirePointIndex(i))
                    continue;
                var c = bulletConfig.cannons[i];
                int bankIdx = ResolveBulletBankIndexForCannon(c, CombatSystem.Instance);
                ResolveEffectiveCannonStats(c, bankIdx, out _, out _, out _, out float energyCostPerShot);
                sum += energyCostPerShot;
            }
            return sum;
        }

        /// <summary>
        /// Builds cannons to fire this cycle. Full pooled energy fires every rate-ready weapon at once;
        /// below that threshold only the next weapon in round-robin may fire.
        /// </summary>
        private bool TryCollectCannonsToFire(List<int> cannonsToFire, bool requireEnergy = true)
        {
            cannonsToFire.Clear();
            if (bulletConfig?.cannons == null || bulletConfig.cannons.Count == 0)
                return false;

            int count = bulletConfig.cannons.Count;

            if (!UsesSharedWeaponEnergyPool)
            {
                for (int i = 0; i < count; i++)
                {
                    if (!TryCanCannonFireNow(i, out _, requireEnergy))
                        continue;
                    cannonsToFire.Add(i);
                    return true;
                }
                return false;
            }

            _sharedPoolReadyCannonsScratch.Clear();
            _sharedPoolReadyCostsScratch.Clear();
            for (int i = 0; i < count; i++)
            {
                if (!TryCanCannonFireRateReady(i, out float cost))
                    continue;
                _sharedPoolReadyCannonsScratch.Add(i);
                _sharedPoolReadyCostsScratch.Add(cost);
            }

            if (_sharedPoolReadyCannonsScratch.Count == 0)
                return false;

            float volleyThreshold = GetSharedPoolVolleyEnergyThreshold();
            if (!requireEnergy || GetFiringPoolEnergy() >= volleyThreshold)
            {
                cannonsToFire.AddRange(_sharedPoolReadyCannonsScratch);
                return true;
            }

            int turn = ResolveSharedPoolTurnCannonIndex();
            if (turn < 0 || !IsValidWeaponFirePointIndex(turn))
                return false;
            if (!TryCanCannonFireNow(turn, out _, requireEnergy))
                return false;

            cannonsToFire.Add(turn);
            return true;
        }

        private bool CanAnyCannonFire(bool requireEnergy = true) =>
            TryCollectCannonsToFire(_cannonsToFireScratch, requireEnergy);

        private void RefillCannonEnergyFromServer()
        {
            if (!IsServer)
                return;
            currentEnergy.Value = ComputeEnergyCapacityLocal();
            ResetSharedPoolWeaponFiredIndex();
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
        [Tooltip("Base mass when no chassis. Chassis components override with component weights. Movement mass scales with hull bulk using Movement Hull Bulk Exponent; ramming uses full HP bulk.")]
        [SerializeField] private float baseMass = 1f;
        [Tooltip("Added mass per gem carried. Ship feels heavier when full; more momentum when braking.")]
        [SerializeField] private float massPerGem = 0.008f;
        [Tooltip("Extra multiplier on gem cargo mass for ramming/grind damage only (full hold hits harder). Movement uses base massPerGem.")]
        [SerializeField, Min(1f)] private float rammingGemMassScale = 2.5f;
        [Tooltip("Multiplies chassis component mass (or baseMass when no chassis). Does not scale gem load.")]
        [SerializeField] private float hullMassScale = 0.7f;
        [Tooltip("Exponent on HP-driven hull bulk for movement only (ramming/collisions use full bulk). 1 = same as ramming; ~0.4 keeps higher-level ships roughly half as sluggish.")]
        [SerializeField, Range(0f, 1f)] private float movementHullBulkExponent = 0.4f;
        [Tooltip("Base collision ramming power before level/component modifiers.")]
        [SerializeField] private float baseRammingPower = 1f;

        [Header("Energy (weapon system)")]
        [SerializeField] private float energyCapacity = 50f;
        [SerializeField] private float energyRegenRate = 5f;
        private const float ENERGY_PER_SHOT = 1f;
        private float rammingPower = 1f;
        /// <summary>Summed rammingPower from ShipFamilyDefinition (level 1, no per-level bonus).</summary>
        private float _summedRammingPowerBase;
        /// <summary>Summed rammingPowerPerLevel from ShipFamilyDefinition — scales offense each ship level.</summary>
        private float _summedRammingPowerPerLevel;

        /// <summary>Synced server time (seconds) until electric-shock stun ends. Blocks move, turn, and fire.</summary>
        private NetworkVariable<float> electricShockEndServerTime = new NetworkVariable<float>(0f);
        private struct ActiveBulletBurn
        {
            public float Dps;
            public float RemainingDuration;
            public float TickTimer;
            public float TickInterval;
            public TeamManager.Team SourceTeam;
        }
        private readonly List<ActiveBulletBurn> activeBulletBurns = new List<ActiveBulletBurn>(4);
        private GameObject clientBurnVfxInstance;

        [Header("References")]
        [SerializeField] private PlayerInputHandler inputHandler;
        [SerializeField] private Rigidbody rb;
        [Tooltip("Runtime: BankPivot under this ship (created in Awake). Do not assign the Starship root here — if this is missing or wrong, we try to find a child named BankPivot.")]
        [SerializeField] private Transform visualRoot;
        /// <summary>Banking pivot (Starship → BankPivot → Prefab). ToroidalRenderer repositions this for non-local ships.</summary>
        public Transform BankPivotTransform => visualRoot;
        [Tooltip("Multiplies the loaded ship prefab scale (chassis size in the world). Lower values make the whole ship look smaller; gem-moon dock scales apply on top of this.")]
        [SerializeField] private float shipVisualScaleMultiplier = 0.175f;
        [Tooltip("Uniform scale multiplier per ship level above 1 (level 6 → 1.15^5 ≈ 2.01×). Sole source of level-based hull size; attribute upgrades add per-component scale on top.")]
        [SerializeField] private float shipLevelScalePerLevel = 1.15f;

        [Header("Banking (fallback when shipData has no values)")]
        [SerializeField] private float defaultMaxBankAngle = ShipPropulsionAggregation.VisualBankReferenceMaxAngleDegrees;
        [SerializeField] private float defaultBankSmoothing = 8f;

        private MaterialPropertyBlock hullColorBlock;
        private int lastVisualApplyFrame = -1;
        private GameObject lastVisualApplyPrefab;
        private ShipFamilyDefinition currentVisualFamilyDefinition;
        /// <summary>Last chassis index we applied (so we re-apply when buying a new ship). -2 = never applied; server uses this to apply default AstroEagle_01 once.</summary>
        private int _lastAppliedChassisIndex = -2;
        /// <summary>Ship level used when <see cref="ApplyShipVisualFromPrefab"/> last ran; re-apply when level syncs after chassis (rescue restore).</summary>
        private int _lastAppliedShipLevel = -1;
        /// <summary>Invalidate gem pickup radius cache when ship level changes.</summary>
        private int _lastRadiusCacheShipLevel = -1;
        /// <summary>Server: true after default spawn or map-instance restore has run for this human player ship.</summary>
        private bool _playerSpawnSetupComplete;

        private NetworkVariable<float> currentHealth = new NetworkVariable<float>(100f);
        private NetworkVariable<float> currentGems = new NetworkVariable<float>(0f);
        private NetworkVariable<float> currentPeople = new NetworkVariable<float>(0f);
        private NetworkVariable<float> currentEnergy = new NetworkVariable<float>(50f);
        /// <summary>Last weapon index that consumed shared-pool energy; next shot round-robins from here.</summary>
        private NetworkVariable<int> lastSharedPoolWeaponFiredIndex = new NetworkVariable<int>(-1);
        /// <summary>Server-authoritative gem/people/health/energy caps for HUD (clients may not resolve all card bonuses locally).</summary>
        private NetworkVariable<float> networkGemCapacity = new NetworkVariable<float>(100f);
        private NetworkVariable<float> networkPeopleCapacity = new NetworkVariable<float>(10f);
        private NetworkVariable<float> networkMaxHealth = new NetworkVariable<float>(100f);
        private NetworkVariable<float> networkEnergyCapacity = new NetworkVariable<float>(50f);
        private NetworkVariable<TeamManager.Team> shipTeam = new NetworkVariable<TeamManager.Team>(TeamManager.Team.None);
        /// <summary>Human player has no team yet (brief replication window; player ships are spawned only after team join).</summary>
        private bool IsAwaitingTeamSelection => shipTeam.Value == TeamManager.Team.None;

        private void SyncInputHandlerForTeamSelectionState()
        {
            if (inputHandler == null || !IsOwner) return;
            inputHandler.enabled = !IsAwaitingTeamSelection;
        }

        private NetworkVariable<bool> wantToLoadPeople = new NetworkVariable<bool>(false);
        private NetworkVariable<bool> wantToUnloadPeople = new NetworkVariable<bool>(false);
        private NetworkVariable<bool> wantToDepositGems = new NetworkVariable<bool>(false);
        private NetworkVariable<bool> wantToExpelGems = new NetworkVariable<bool>(false);
        /// <summary>Owner-synced: combat fire held (UI/orbit gated on client before sending).</summary>
        private NetworkVariable<bool> wantToFire = new NetworkVariable<bool>(false);
        /// <summary>Server: right-click / move-forward held (from input buffer).</summary>
        private NetworkVariable<bool> moveForwardPressedNet = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
        /// <summary>Server: shoot held (from input buffer).</summary>
        private NetworkVariable<bool> shootPressedNet = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);
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

        [Header("Equipment Loadout")]
        [Tooltip("Store items (drones, rockets, mines) equipped in ship equipment slots. Server-authoritative.")]
        [SerializeField] private List<EquippedEquipmentEntry> equippedEquipment = new List<EquippedEquipmentEntry>();

        /// <summary>Synced equipment entries for client UI.</summary>
        private NetworkList<EquippedEquipmentEntry> equippedEquipmentEntries;

        /// <summary>Summed effective stats from equipped ship-family components (equipment slots).</summary>
        private ShipComponentAbilityStats _equippedComponentStatSum;

        private const float ATTR_MULTIPLIER_PER_LEVEL = 0.1f;
        /// <summary>Per level after 1, mobility loses this fraction of the <em>base</em> stat: base − (base × this) × (level − 1).</summary>
        private const float ShipLevelMobilityPenaltyFractionPerLevel = ShipPropulsionAggregation.ShipLevelMobilityPenaltyFractionPerLevel;

        /// <summary>Ship-level mobility: moveSpeed − (moveSpeed × 0.11) × (level−1); same pattern for rotation and per-part move.</summary>
        private static float ApplyShipLevelMobilityScale(float baseStat, float perLvlAfterOne)
        {
            return ShipPropulsionAggregation.ApplyShipLevelMobilityScale(baseStat, Mathf.RoundToInt(perLvlAfterOne));
        }

        /// <summary>Propulsion force (sum of engine + thruster acceleration caps). More propulsion parts = more force; heavier ship = less acceleration (F/m).</summary>
        private float EffectiveEngineThrust
        {
            get
            {
                float baseThrust = componentEngineThrust > 0f ? componentEngineThrust : engineThrust;
                baseThrust += _equippedComponentStatSum.accelerationCap;
                float baseWithCards = baseThrust + GetCardMovementSpeedAdd();
                float attrScale = 1f + attrMovementSpeed.Value * ATTR_MULTIPLIER_PER_LEVEL;
                // Boost acceleration so ships feel snappier. 5x matches previous feel better after mass changes.
                const float ENGINE_THRUST_VISIBILITY = 10f;
                return baseWithCards * attrScale * FriendlyTerritoryMovementMultiplier * ENGINE_THRUST_VISIBILITY;
            }
        }
        /// <summary>Max speed from shared engine/thruster pool: best base move speed plus half the summed moveSpeedPerLevel from other propulsion parts.</summary>
        private float EffectiveMaxSpeed
        {
            get
            {
                float baseSpeed = componentEngineMaxSpeed > 0f ? componentEngineMaxSpeed : engineThrust * 0.5f;
                baseSpeed += _equippedComponentStatSum.moveSpeed;
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
                Vector3 pos = UsesInputSyncedMotor
                    ? ToroidalMap.WrapPosition(GetSimPosition())
                    : ToroidalMap.WrapPosition(transform.position);
                TeamManager.Team teamAtPos = PlanetConnectionSystem.Instance.GetTeamAtPosition(pos);
                if (teamAtPos != shipTeam.Value) return 1f;
                int homeLevel = PlanetConnectionSystem.GetHomePlanetLevelForTeam(shipTeam.Value);
                return 1f + 0.05f * homeLevel;
            }
        }

        private float EffectiveEnergyCapacity => ComputeEnergyCapacityLocal();

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
                float baseWithCards = healthRegenRate + _equippedComponentStatSum.healthRegen + GetCardHealthRegenAdd();
                float attrScale = 1f + attrHealthRegen.Value * ATTR_MULTIPLIER_PER_LEVEL;
                return baseWithCards * attrScale;
            }
        }

        private float EffectiveRotationSpeed
        {
            get
            {
                if (IsBulletElectricShockDisabled)
                    return 0f;
                float chassis = rotationSpeedFromShipFamilyDefinition
                    ? Mathf.Max(1f, rotationSpeed) * ShipTurnDefinitionToDegreesPerSecond
                    : rotationSpeed;
                float baseWithCards = chassis + _equippedComponentStatSum.turnSpeed + GetCardRotationSpeedAdd();
                float attrScale = 1f + attrRotationSpeed.Value * ATTR_MULTIPLIER_PER_LEVEL;
                return baseWithCards * attrScale;
            }
        }

        /// <summary>Electric-shock stun: no movement thrust, rotation, or weapon fire until server time catches up.</summary>
        public bool IsBulletElectricShockDisabled
        {
            get
            {
                var nm = NetworkManager.Singleton;
                if (nm == null) return false;
                return (float)nm.ServerTime.Time < electricShockEndServerTime.Value;
            }
        }

        [System.Obsolete("Use IsBulletElectricShockDisabled")]
        public bool IsBulletRotationLocked => IsBulletElectricShockDisabled;

        private float EffectiveEnergyRegen
        {
            get
            {
                float baseWithCards = HasWeaponComponentEnergy
                    ? GetSummedWeaponEnergyRegenBase() + GetCardEnergyRegenAdd()
                    : energyRegenRate + _equippedComponentStatSum.energyRegen + GetCardEnergyRegenAdd();
                float attrScale = 1f + attrEnergyRegen.Value * ATTR_MULTIPLIER_PER_LEVEL;
                float regen = baseWithCards * attrScale;
                return HasWeaponComponentEnergy ? Mathf.Max(0.01f, regen) : regen;
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

        /// <summary>Chassis or fallback base mass after hullMassScale (excludes gem load and hull-bulk scaling).</summary>
        private float ScaledHullMassReference => (componentMass > 0f ? componentMass : baseMass) * hullMassScale;

        /// <summary>
        /// Hull bulk for ramming: level-up HP (healthCapPerLevel), bigger chassis prefabs, and attribute/card max-health upgrades
        /// all increase MaxHealth but componentMass only tracks prefab part scales — this aligns impact mass with hull size.
        /// </summary>
        private float GetRammingBulkScale() => MaxHealth / Mathf.Max(1f, _chassisReferenceHealth);

        /// <summary>
        /// Softer HP bulk for movement/braking. Engines scale via acceleration caps; full ramming bulk made high-level ships feel glued.
        /// </summary>
        private float GetMovementBulkScale()
        {
            float bulk = GetRammingBulkScale();
            if (bulk <= 1f || movementHullBulkExponent >= 0.999f)
                return bulk;
            if (movementHullBulkExponent <= 0.001f)
                return 1f;
            return Mathf.Pow(bulk, movementHullBulkExponent);
        }

        /// <summary>Movement mass: softened hull bulk + gem cargo (base massPerGem).</summary>
        private float EffectiveMass
        {
            get
            {
                float bulkScale = GetMovementBulkScale();
                return Mathf.Max(0.5f, ScaledHullMassReference * bulkScale + currentGems.Value * massPerGem);
            }
        }

        /// <summary>
        /// Hull mass at this ship level (components × HP bulk, no gems). Ram damage massFactor uses this as baseline
        /// so level-3 bulk (~40) does not multiply self/impact damage vs level-1 (~9).
        /// </summary>
        private float GetRammingHullMassBaseline()
        {
            float bulkScale = GetRammingBulkScale();
            return Mathf.Max(0.5f, ScaledHullMassReference * bulkScale);
        }

        /// <summary>Ramming mass: hull baseline + gem cargo (boosted). Movement uses the same hull baseline.</summary>
        private float GetRammingMassForDamage()
        {
            float hullMass = GetRammingHullMassBaseline();
            float gemMass = currentGems.Value * massPerGem * Mathf.Max(1f, rammingGemMassScale);
            return Mathf.Max(0.5f, hullMass + gemMass);
        }

        /// <summary>Mass used for asteroid impact impulse (includes gem cargo).</summary>
        private float GetRammingImpactMass() => Mathf.Max(0.01f, GetRammingMassForDamage());

        /// <summary>HUD: mass factor vs this ship's hull baseline (≈1 empty, &gt;1 with gems).</summary>
        public float GetHudRamMassDamageFactor()
        {
            float baseline = GetRammingHullMassBaseline();
            return ShipComponentRammingSuggestions.ComputeMassDamageFactor(GetRammingMassForDamage(), baseline);
        }

        /// <summary>HUD: softer self-damage mass factor.</summary>
        public float GetHudRamSelfMassDamageFactor()
        {
            return ShipComponentRammingSuggestions.ComputeSelfMassDamageFactor(
                GetRammingMassForDamage(),
                GetRammingHullMassBaseline());
        }

        /// <summary>Bullet-comparable ram rating from ShipFamilyDefinition only (prefab baseRammingPower affects bounce, not damage).</summary>
        private float GetRammingDamageRating()
        {
            float perLvl = Mathf.Max(0, ShipLevel - 1);
            float familyPower = _summedRammingPowerBase + _summedRammingPowerPerLevel * perLvl;
            float rating = ShipComponentRammingSuggestions.ComputeDamageRatingFromFamilyPower(familyPower);
            return BulletBankProfileUtility.ScaleRammingRating(rating, GetActiveBulletBankIndexForShip());
        }

        /// <summary>Active bullet bank index (B-key cycle, family default, or cannon override).</summary>
        private int ResolveBulletBankIndexForCannon(CannonConfig c, CombatSystem combat)
        {
            if (combat == null) return -1;
            int bankCount = combat.BulletPrefabBankCount;
            if (bankCount <= 0) return -1;
            if (runtimeBulletPrefabIndex.Value >= 0)
                return runtimeBulletPrefabIndex.Value % bankCount;
            if (c != null && c.bulletPrefabIndex >= 0)
                return c.bulletPrefabIndex % bankCount;
            if (bulletPrefabBankIndex >= 0)
                return bulletPrefabBankIndex % bankCount;
            return 0;
        }

        private int GetActiveBulletBankIndexForShip()
        {
            if (CombatSystem.Instance == null) return -1;
            int bankCount = CombatSystem.Instance.BulletPrefabBankCount;
            if (bankCount <= 0) return -1;
            int runtime = runtimeBulletPrefabIndex.Value;
            if (runtime >= 0) return runtime % bankCount;
            if (bulletPrefabBankIndex >= 0) return bulletPrefabBankIndex % bankCount;
            return 0;
        }

        private static void ApplyBulletBankShotStats(int bulletBankIndex, ref float damage, ref float speed, ref float fireRate)
        {
            if (bulletBankIndex < 0) return;
            damage = BulletBankProfileUtility.ScaleFirePower(damage, bulletBankIndex);
            speed = BulletBankProfileUtility.ScaleBulletSpeed(speed, bulletBankIndex);
            fireRate = BulletBankProfileUtility.ScaleFireRate(fireRate, bulletBankIndex);
        }

        /// <summary>
        /// Runtime weapon stats: ship-level *PerLevel terms are baked into <see cref="bulletConfig"/> (except bullet speed, which stays at authored base);
        /// attribute upgrades and cards apply here (same pattern as <see cref="ComputeMaxHealthLocal"/>).
        /// </summary>
        private void ResolveEffectiveCannonStats(CannonConfig c, int bulletBankIndex, out float damage, out float speed, out float fireRate, out float energyCostPerShot)
        {
            fireRate = c.fireRate * (1f + attrFireRate.Value * ATTR_MULTIPLIER_PER_LEVEL);
            damage = c.damagePerBullet * DamageMultiplier;
            speed = c.bulletSpeed * SpeedMultiplier;
            ApplyBulletBankShotStats(bulletBankIndex, ref damage, ref speed, ref fireRate);
            energyCostPerShot = c.energyCostPerShot * DamageMultiplier;
        }

        /// <summary>
        /// Damage for drone bullets: primary cannon base fire power at <see cref="ShipLevel"/> (no attribute/card multipliers),
        /// with optional bullet-bank profile scaling.
        /// </summary>
        public float GetDroneBulletDamage(int bulletBankIndex = -1)
        {
            CombatSystem combat = CombatSystem.Instance;
            if (bulletConfig == null || bulletConfig.cannons == null || bulletConfig.cannons.Count == 0)
            {
                int perLvl = Mathf.Max(0, ShipLevel - 1);
                float damage = ShipComponentWeaponSuggestions.GetSuggestedFirePower(1)
                    + ShipComponentWeaponSuggestions.GetSuggestedFirePowerPerLevel(1) * perLvl;
                if (bulletBankIndex >= 0)
                    damage = BulletBankProfileUtility.ScaleFirePower(damage, bulletBankIndex);
                return Mathf.Max(0.5f, damage);
            }

            var c = bulletConfig.cannons[0];
            float baseFirePower = Mathf.Max(0.5f, c.damagePerBullet);
            int bankIdx = bulletBankIndex >= 0
                ? bulletBankIndex
                : (combat != null ? ResolveBulletBankIndexForCannon(c, combat) : -1);
            if (bankIdx >= 0)
                baseFirePower = BulletBankProfileUtility.ScaleFirePower(baseFirePower, bankIdx);
            return baseFirePower;
        }

        /// <summary>Server: play bank muzzle VFX at a drone fire origin on all clients.</summary>
        public void ServerPlayDroneMuzzleVfx(Vector3 position, Vector3 direction, int bankIndex, float damage)
        {
            if (!IsServer) return;
            float pitch = BulletHitResolver.GetImpactSoundPitch(Mathf.Max(0.01f, damage));
            PlayDroneMuzzleClientRpc(position, direction, bankIndex, (byte)ShipTeam, pitch);
        }

        [ClientRpc]
        private void PlayDroneMuzzleClientRpc(Vector3 position, Vector3 direction, int bankIndex, byte teamByte, float pitch)
        {
            if (CombatSystem.Instance == null) return;
            CombatSystem.Instance.PlayWeaponMuzzleVfxAt(
                position,
                direction,
                bankIndex,
                (TeamManager.Team)teamByte,
                pitch,
                DroneSwarmController.DroneBulletVisualScale);
        }

        /// <summary>Server: electric shock — movement, rotation, and firing disabled for the duration.</summary>
        public void ApplyBulletElectricShockOnServer(float durationSeconds)
        {
            if (!IsServer || durationSeconds <= 0f) return;
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            float end = (float)nm.ServerTime.Time + durationSeconds;
            electricShockEndServerTime.Value = Mathf.Max(electricShockEndServerTime.Value, end);
        }

        [System.Obsolete("Use ApplyBulletElectricShockOnServer")]
        public void ApplyBulletRotationLockOnServer(float durationSeconds) =>
            ApplyBulletElectricShockOnServer(durationSeconds);

        private void TickElectricShockBraking()
        {
            if (!IsBulletElectricShockDisabled || rb == null) return;
            moveDirection = Vector3.zero;
            Vector3 vel = rb.linearVelocity;
            vel.y = 0f;
            float mass = Mathf.Max(0.5f, rb.mass);
            float brake = brakeDeceleration * mass * 2.5f;
            if (vel.sqrMagnitude > 0.0001f)
                rb.AddForce(-vel.normalized * brake, ForceMode.Force);
        }

        /// <summary>Transform burn VFX parents to so fire moves with the visible hull (BankPivot when present).</summary>
        public Transform GetBurnVfxAttachTransform()
        {
            if (visualRoot != null && visualRoot != transform)
                return visualRoot;
            return transform;
        }

        public void ApplyBulletBurnOnServer(
            float dps,
            float durationSeconds,
            float tickInterval,
            TeamManager.Team sourceTeam,
            int bankIndex,
            Vector3 impactWorldPos)
        {
            if (!IsServer || isDead.Value || dps <= 0f || durationSeconds <= 0f) return;
            bool wasBurning = activeBulletBurns.Count > 0;
            activeBulletBurns.Add(new ActiveBulletBurn
            {
                Dps = dps,
                RemainingDuration = durationSeconds,
                TickTimer = 0f,
                TickInterval = Mathf.Max(0.05f, tickInterval),
                SourceTeam = sourceTeam,
            });

            Transform attach = GetBurnVfxAttachTransform();
            Vector3 localOffset = attach.InverseTransformPoint(impactWorldPos);
            localOffset.y = 0f;
            float vfxDuration = GetLongestActiveBulletBurnDuration();
            PlayBurnLingerVfxClientRpc(bankIndex, vfxDuration, localOffset, wasBurning);
        }

        private float GetLongestActiveBulletBurnDuration()
        {
            float longest = 0f;
            for (int i = 0; i < activeBulletBurns.Count; i++)
                longest = Mathf.Max(longest, activeBulletBurns[i].RemainingDuration);
            return longest;
        }

        private void ClearBulletBurnEffectsOnServer()
        {
            if (!IsServer) return;
            activeBulletBurns.Clear();
            StopBurnLingerVfxClientRpc();
        }

        private void ClearClientBurnVfx()
        {
            if (clientBurnVfxInstance != null)
            {
                Destroy(clientBurnVfxInstance);
                clientBurnVfxInstance = null;
            }
        }

        [ClientRpc]
        private void PlayBurnLingerVfxClientRpc(
            int bankIndex,
            float durationSeconds,
            Vector3 localAttachOffset,
            bool extendExistingBurn)
        {
            if (isDead.Value)
            {
                ClearClientBurnVfx();
                return;
            }

            if (extendExistingBurn && clientBurnVfxInstance != null)
            {
                var anchor = clientBurnVfxInstance.GetComponent<ShipBurnVfxAnchor>();
                if (anchor != null)
                {
                    anchor.SetDurationFromNow(durationSeconds);
                    return;
                }
            }

            ClearClientBurnVfx();

            if (Application.isMobilePlatform || CombatSystem.Instance == null || bankIndex < 0)
                return;

            GameObject prefab = CombatSystem.Instance.GetImpactPrefabFromBank(bankIndex, shipTeam.Value);
            if (prefab == null) return;

            Transform attach = GetBurnVfxAttachTransform();
            localAttachOffset.y = 0f;
            clientBurnVfxInstance = BulletVisualFactory.SpawnLoopingImpactAt(
                attach.TransformPoint(localAttachOffset),
                prefab,
                1f,
                BulletVisualFactory.DefaultImpactScale,
                durationSeconds,
                attach,
                localAttachOffset);
        }

        [ClientRpc]
        private void StopBurnLingerVfxClientRpc()
        {
            ClearClientBurnVfx();
        }

        private void HandleIsDeadChangedForBurnVfx(bool previous, bool dead)
        {
            if (!dead) return;
            if (IsServer)
                activeBulletBurns.Clear();
            ClearClientBurnVfx();
        }

        public float ApplyBulletHealOnServer(float healAmount, TeamManager.Team sourceTeam)
        {
            if (!IsServer || isDead.Value || healAmount <= 0f) return 0f;
            float before = currentHealth.Value;
            currentHealth.Value = Mathf.Min(MaxHealth, before + healAmount);
            float applied = currentHealth.Value - before;
            if (applied > 0.001f && VisualEffectsManager.Instance != null)
            {
                VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                    transform.position,
                    (int)FloatingCountChannel.Healing,
                    applied,
                    (int)sourceTeam);
            }
            return applied;
        }

        public void ApplyBulletKnockbackOnServer(Vector3 impactWorldPos, float force, bool pull)
        {
            if (!IsServer || rb == null || isDead.Value || force <= 0f) return;
            Vector3 dir = rb.position - impactWorldPos;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f)
                dir = transform.forward;
            dir.Normalize();
            if (!pull)
                dir = -dir;
            rb.AddForce(dir * force, ForceMode.Impulse);
        }

        private void TickBulletStatusEffectsServer(float dt)
        {
            if (!IsServer || isDead.Value || activeBulletBurns.Count == 0) return;
            for (int i = activeBulletBurns.Count - 1; i >= 0; i--)
            {
                ActiveBulletBurn b = activeBulletBurns[i];
                b.RemainingDuration -= dt;
                if (b.RemainingDuration <= 0f)
                {
                    activeBulletBurns.RemoveAt(i);
                    continue;
                }

                b.TickTimer -= dt;
                if (b.TickTimer <= 0f)
                {
                    float tickDamage = b.Dps * b.TickInterval;
                    if (!isDead.Value && tickDamage > 0f)
                        ApplyDamageOnServer(tickDamage, b.SourceTeam, 0, 0.5f, 0f);
                    b.TickTimer = b.TickInterval;
                }
                activeBulletBurns[i] = b;
            }
        }

        private void ComputeRamImpactDamage(float inboundNormalSpeed, out float asteroidDamage, out float selfDamage)
        {
            float mass = GetRammingMassForDamage();
            float baseline = GetRammingHullMassBaseline();
            float rating = GetRammingDamageRating();
            float offenseMassFactor = ShipComponentRammingSuggestions.ComputeMassDamageFactor(mass, baseline);
            float selfMassFactor = ShipComponentRammingSuggestions.ComputeSelfMassDamageFactor(mass, baseline);

            // Bounce uses actual restitution; damage always budgets the max collision energy (suppressed bounce → chip damage).
            float damageRestitution = GetMaxAsteroidRestitution();
            float deltaNormalSpeed = (1f + damageRestitution) * Mathf.Max(0f, inboundNormalSpeed);
            float speedFactor = deltaNormalSpeed / Mathf.Max(0.1f, ShipComponentRammingSuggestions.ReferenceImpactSpeed);

            asteroidDamage = Mathf.Max(0f, rating * offenseMassFactor * speedFactor);
            selfDamage = Mathf.Max(0f, rating * selfMassFactor * speedFactor * ShipComponentRammingSuggestions.SelfToAsteroidDamageRatio);

            float selfCap = MaxHealth * ShipComponentRammingSuggestions.MaxSelfImpactDamageFractionOfMaxHealth;
            if (selfCap > 0f)
                selfDamage = Mathf.Min(selfDamage, selfCap);
        }

        private float ComputeRamGrindAsteroidDamage(float pushNewtons, float pulseInterval)
        {
            return ShipComponentRammingSuggestions.ComputeGrindDamagePerPulse(
                GetRammingDamageRating(),
                GetRammingMassForDamage(),
                GetRammingHullMassBaseline(),
                pushNewtons,
                pulseInterval);
        }

        private float ComputeRamGrindSelfDamage(float pushNewtons, float pulseInterval)
        {
            float mass = GetRammingMassForDamage();
            float baseline = GetRammingHullMassBaseline();
            float rating = GetRammingDamageRating();
            float pushFactor = pushNewtons / Mathf.Max(1f, ShipComponentRammingSuggestions.ReferenceGrindPushNewtons);
            float selfMassFactor = ShipComponentRammingSuggestions.ComputeSelfMassDamageFactor(mass, baseline);
            float damage = rating * selfMassFactor * pushFactor * pulseInterval * ShipComponentRammingSuggestions.SelfToAsteroidDamageRatio;
            float selfCap = MaxHealth * ShipComponentRammingSuggestions.MaxSelfImpactDamageFractionOfMaxHealth;
            if (selfCap > 0f)
                damage = Mathf.Min(damage, selfCap);
            return damage;
        }

        /// <summary>HUD: effective ram mass (hull + boosted gem cargo).</summary>
        public float GetHudRamEffectiveMass() => GetRammingMassForDamage();

        /// <summary>HUD: ram damage rating before × mass (same scale as bullet damage per hit).</summary>
        public float GetHudRamDamageRating() => GetRammingDamageRating();

        private float lastRocketTime = -999f;
        private float lastMineTime = -999f;
        private const float ROCKET_COOLDOWN = 0.6f;
        private const float MINE_COOLDOWN = 1f;
        private Vector3 moveDirection = Vector3.zero;
        private Vector3 currentVelocity = Vector3.zero;
        private Planet currentOrbitPlanet; // When non-null, we're in a planet's orbit zone
        /// <summary>World-space radius locked once stable orbit is reached; held until orbit exit.</summary>
        private float lockedOrbitRadiusWorld = -1f;
        private Planet lockedOrbitRadiusPlanet;

        private bool HasLockedOrbitRadius(Planet planet)
        {
            return planet != null && lockedOrbitRadiusPlanet == planet && lockedOrbitRadiusWorld > 0f;
        }

        /// <summary>Strict visual orbit ring (matches <see cref="Planet.IsWorldPositionInOrbitRing"/>).</summary>
        private bool IsShipInPlanetOrbitRing(Planet planet)
        {
            if (planet == null) return false;
            Vector3 pos = UsesInputSyncedMotor ? GetSimPosition() : (rb != null ? rb.position : transform.position);
            pos.y = 0f;
            if (planet.IsWorldPositionInOrbitRing(pos)) return true;
            Vector3 tPos = transform.position;
            tPos.y = 0f;
            return planet.IsWorldPositionInOrbitRing(tPos);
        }
        private bool wasMovePressedLastFrame;
        private bool wasShootPressedLastFrame;
        /// <summary>While gem-moon docked: deposit chunks per second (each chunk = <see cref="ShipLevel"/> gems).</summary>
        private const float GemMoonDepositChunksPerSecond = 3f;
        private float depositAccumulator;
        private float lastVoluntaryGemExpulsionServerTime = -999f;
        private int voluntaryGemExpulsionShotIndex;
        private bool localWantToExpelGemsSent;
        private bool localWantToFireSent;
        private float lastDepositSpawnTime = -999f;
        private float peopleUnloadAccumulator;
        /// <summary>Server: orbit planet id used for the current transfer session; accumulators reset when this changes.</summary>
        private ulong peopleTransferOrbitPlanetId;
        [Tooltip("Seconds the ship must stay in stable orbit without thrusting before people load/unload begins.")]
        [SerializeField, Min(0f)] private float peopleTransferStationaryHoldSeconds = 1f;
        private float peopleTransferStationaryTimer;
        private float peopleInTransit; // People in projectiles heading to this ship (load only)

        // Galactic zoom tracking (server-side)
        private bool hadGemsWhileInOrbitThisOrbit;
        private bool triggeredGalacticZoomThisOrbit;
        private bool triggeredGalacticZoomThisMoonDock;
        private bool depositedAnyGemsThisOrbit;

        // Banking (visual lean into turn) - only used when visualRoot is set.
        private float currentBankAngle;
        private bool bankingInitialized;
        /// <summary>Smoothed yaw rate (°/s) sampled in FixedUpdate so banking does not jitter between render frames.</summary>
        private float _cachedBankAngularVelDegPerSec;
        private float _prevBankYawDeg;
        private bool _bankYawInitialized;
        
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
        /// <summary>When nearly stationary, turn rates below this (°/s) do not drive bank angle.</summary>
        private const float IdleBankAngularVelDeadbandDegPerSec = 18f;

        public float CurrentHealth => currentHealth.Value;

        /// <summary>Health for HUD (clients extrapolate regen between network syncs).</summary>
        public float GetHealthForDisplay()
        {
            if (!IsSpawned)
                return currentHealth.Value;
            if (IsServer)
                return currentHealth.Value;
            return ComputeDisplayedHealth(GetServerTimeNowSeconds());
        }
        public float MaxHealth
        {
            get
            {
                if (IsSpawned && !IsServer)
                    return networkMaxHealth.Value;
                return ComputeMaxHealthLocal();
            }
        }
        public float CurrentGems => currentGems.Value;
        public bool IsDead => isDead.Value;
        /// <summary>Max gem capacity. Base comes from ShipFamilyDefinition (via chassis components), plus card bonuses and attribute upgrades.</summary>
        public float GemCapacity
        {
            get
            {
                if (IsSpawned && !IsServer)
                    return networkGemCapacity.Value;
                return ComputeGemCapacityLocal();
            }
        }

        /// <summary>Base gem capacity without card bonuses. Comes from ShipFamilyDefinition (via chassis components).</summary>
        public float BaseGemCapacity => Mathf.Max(0f, gemCapacity);

        /// <summary>One gem tractor beam per wing; reach and pull strength come from each wing's Max Gems Capacity stats.</summary>
        public IReadOnlyList<WingTractorBeamSlot> WingTractorBeams => wingTractorBeams;

        /// <summary>Horizontal speed in the play plane (XZ), units/sec. Matches movement clamp / HUD speedometer.</summary>
        public float CurrentHorizontalSpeed
        {
            get
            {
                Vector3 v = GetSimVelocity();
                v.y = 0f;
                return v.magnitude;
            }
        }

        /// <summary>Effective maximum movement speed cap (same units as <see cref="CurrentHorizontalSpeed"/>).</summary>
        public float MaxMoveSpeed => EffectiveMaxSpeed;
        /// <summary>Effective yaw turn rate in degrees per second (chassis, cards, attributes).</summary>
        public float MaxTurnSpeed => EffectiveRotationSpeed;
        /// <summary>Current rigidbody mass used by movement, momentum, and collisions.</summary>
        public float CurrentMass => rb != null ? rb.mass : EffectiveMass;

        /// <summary>Approximate max rate of increase of horizontal speed (engine thrust / mass). Decreases when mass rises (e.g. gems). HUD baseline for accelerometer max.</summary>
        public float MaxHorizontalAcceleration => EffectiveEngineThrust / Mathf.Max(0.5f, CurrentMass);

        /// <summary>Braking deceleration magnitude when space brakes slow the ship (matches applied brake force / mass).</summary>
        public float MaxBrakingDeceleration => brakeDeceleration;

        /// <summary>
        /// HUD: asteroid ram outcome — ram rating × mass × speed factor (head-on: use <see cref="CurrentHorizontalSpeed"/>).
        /// </summary>
        public void GetHudAsteroidRamDamageEstimate(float inboundNormalSpeed, out float asteroidDamage, out float selfDamage)
        {
            ComputeRamImpactDamage(inboundNormalSpeed, out asteroidDamage, out selfDamage);
        }

        private void RefreshTotalRammingPower()
        {
            rammingPower = GetTotalRammingPower();
        }

        private float GetTotalRammingPower()
        {
            float perLvl = Mathf.Max(0, ShipLevel - 1);
            return Mathf.Max(0f, baseRammingPower + _summedRammingPowerBase + _summedRammingPowerPerLevel * perLvl + _equippedComponentStatSum.rammingPower);
        }

        private float GetMaxAsteroidRestitution() => Mathf.Clamp01(asteroidCollisionNormalSpeedRetention);

        private float GetEffectiveAsteroidRestitution()
        {
            float maxE = GetMaxAsteroidRestitution();
            float minE = Mathf.Min(maxE, Mathf.Clamp01(asteroidRammingMinRestitution));

            float fromPower = AsteroidRammingBehavior.ComputeRestitution(
                maxE,
                minE,
                GetTotalRammingPower(),
                asteroidRammingRestitutionThreshold,
                asteroidRammingRestitutionReferenceExcess);

            float levelOneHullMass = Mathf.Max(0.5f, ScaledHullMassReference);
            float massRatio = GetRammingMassForDamage() / levelOneHullMass;
            float fromMass = AsteroidRammingBehavior.ComputeRestitution(
                maxE,
                minE,
                massRatio,
                1f,
                asteroidRammingRestitutionReferenceMassRatioExcess);

            return Mathf.Min(fromPower, fromMass);
        }

        /// <summary>Thrust/drive force in the play plane (N): player engine thrust or AI mass × drive acceleration.</summary>
        private Vector3 GetDrivePushForceXZ()
        {
            if (rb == null) return Vector3.zero;
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
            if (bulletConfig == null || bulletConfig.cannons == null || bulletConfig.cannons.Count == 0) return false;

            bool any = false;
            float bestDps = 0f;
            for (int i = 0; i < bulletConfig.cannons.Count; i++)
            {
                if (!IsValidWeaponFirePointIndex(i)) continue;
                var c = bulletConfig.cannons[i];
                if (c == null) continue;
                int hudBank = ResolveBulletBankIndexForCannon(c, CombatSystem.Instance);
                ResolveEffectiveCannonStats(c, hudBank, out float d, out float hudSpeed, out float rate, out _);
                d = Mathf.Max(0f, d);
                rate = Mathf.Max(0f, rate);
                int pellets = 1;
                if (c.spreadType == CannonSpreadType.FixedSpread && c.spreadProjectileCount > 1)
                    pellets = Mathf.Max(1, c.spreadProjectileCount);
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
        private float BaseEnergyCapacityNoAttr
        {
            get
            {
                if (HasWeaponComponentEnergy)
                {
                    float sum = 0f;
                    for (int i = 0; i < cannonEnergyCapacityBase.Length; i++)
                        sum += Mathf.Max(0.1f, cannonEnergyCapacityBase[i]);
                    return Mathf.Max(0.1f, sum);
                }

                return Mathf.Max(0.1f, energyCapacity);
            }
        }

        private float BaseEnergyRegenNoAttr
        {
            get
            {
                if (HasWeaponComponentEnergy)
                {
                    float sum = 0f;
                    for (int i = 0; i < cannonEnergyRegenBase.Length; i++)
                        sum += Mathf.Max(0f, cannonEnergyRegenBase[i]);
                    return Mathf.Max(0.01f, sum);
                }

                return Mathf.Max(0.01f, energyRegenRate);
            }
        }
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

        /// <summary>Server: people moved per planet→ship load projectile — capped by both ship and planet level.</summary>
        public float GetPeopleTransferChunkSize(Planet planet)
        {
            float shipChunk = Mathf.Max(1f, ShipLevel);
            if (planet == null)
                return shipChunk;
            return Mathf.Max(1f, Mathf.Min(shipChunk, planet.PlanetLevel));
        }

        /// <summary>Server: people per ship→neutral/enemy unload projectile — capped by ship level only.</summary>
        public float GetPeopleUnloadChunkSize()
        {
            return Mathf.Max(1f, ShipLevel);
        }

        /// <summary>Server: remaining crew capacity accounting for in-flight load projectiles.</summary>
        public float GetPeopleLoadSpaceRemaining()
        {
            if (!IsServer)
                return 0f;
            return Mathf.Max(0f, PeopleCapacity - currentPeople.Value - peopleInTransit);
        }

        /// <summary>Server: stable orbit, friendly planet at/above 50% reserve, and this ship wants surplus crew.</summary>
        public bool CanReceivePlanetSurplusPeopleLoadFrom(Planet planet)
        {
            if (!IsServer || planet == null || isDead.Value)
                return false;
            if (currentOrbitPlanet != planet)
                return false;
            if (!CanAccumulatePeopleTransferDwell())
                return false;
            if (peopleTransferStationaryTimer < peopleTransferStationaryHoldSeconds)
                return false;

            bool friendly = (planet is HomePlanet home && home.AssignedTeam == shipTeam.Value)
                || planet.TeamOwnership == shipTeam.Value;
            if (!friendly)
                return false;

            float halfCap = 0.5f * planet.MaxPopulation;
            if (planet.CurrentPopulation < halfCap - 0.0001f)
                return false;

            if (!ShouldLoadPeopleFromOrbitPlanet())
                return false;

            return GetPeopleLoadSpaceRemaining() > 0.0001f;
        }

        /// <summary>Server: spawn one planet→ship people transport if surplus and ship capacity allow.</summary>
        public bool TryDispatchPlanetSurplusPeopleLoad(Planet orbitPlanet, float amount, out float amountSent)
        {
            amountSent = 0f;
            if (!IsServer || orbitPlanet == null || amount <= 0f || GemSpawner.Instance == null)
                return false;

            float halfCap = 0.5f * orbitPlanet.MaxPopulation;
            float surplusNow = Mathf.Max(0f, orbitPlanet.CurrentPopulation - halfCap);
            float spaceLeft = GetPeopleLoadSpaceRemaining();
            float sendAmount = Mathf.Min(amount, surplusNow, spaceLeft);
            if (sendAmount <= 0.0001f)
                return false;

            orbitPlanet.RemovePopulationFromServer(sendAmount);
            peopleInTransit += sendAmount;
            amountSent = sendAmount;

            Vector3 shipPos = rb != null ? rb.position : transform.position;
            shipPos.y = 0f;
            Vector3 planetSpawn = PeopleTransportProjectile.GetSurfaceSpawnPointToward(orbitPlanet, shipPos);
            var planetNo = orbitPlanet.GetComponent<NetworkObject>();
            var shipNo = GetComponent<NetworkObject>();
            if (shipNo != null && planetNo != null)
                GemSpawner.Instance.SpawnPeopleLoad(planetSpawn, shipPos, sendAmount, shipNo.NetworkObjectId, planetNo.NetworkObjectId, shipTeam.Value);
            return true;
        }
        public float PeopleCapacity
        {
            get
            {
                if (IsSpawned && !IsServer)
                    return networkPeopleCapacity.Value;
                return ComputePeopleCapacityLocal();
            }
        }
        public float CurrentEnergy =>
            IsOwner && ownerFiringSessionActive ? ownerFiringEnergy : currentEnergy.Value;

        /// <summary>Energy for HUD (owner firing uses local pool; otherwise clients extrapolate regen).</summary>
        public float GetEnergyForDisplay()
        {
            if (!IsSpawned)
                return currentEnergy.Value;
            if (IsOwner && ownerFiringSessionActive)
                return ownerFiringEnergy;
            if (IsServer)
                return currentEnergy.Value;
            return ComputeDisplayedEnergy(GetServerTimeNowSeconds());
        }

        public float EnergyCapacity
        {
            get
            {
                if (IsSpawned && !IsServer)
                    return networkEnergyCapacity.Value;
                return ComputeEnergyCapacityLocal();
            }
        }
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

        /// <summary>Number of equipment slots (1 per ship level). Each slot holds at most one store item.</summary>
        public int EquipmentSlotCount => SlotCount;

        /// <summary>True if there is at least one empty equipment slot.</summary>
        public bool HasEmptyEquipmentSlot => equippedEquipment != null && equippedEquipment.Count < EquipmentSlotCount;

        public int EquippedEquipmentCount => equippedEquipment != null ? equippedEquipment.Count : 0;

        public IReadOnlyList<EquippedEquipmentEntry> EquippedEquipment => GetEquippedEquipmentForDisplay();

        /// <summary>Synced equipment for <see cref="DroneSwarmController"/> list change events.</summary>
        public NetworkList<EquippedEquipmentEntry> EquippedEquipmentNetworkList => equippedEquipmentEntries;

        public DroneSwarmController DroneSwarm => droneSwarm;

        private DroneSwarmController droneSwarm;

        private readonly List<EquippedEquipmentEntry> _clientEquippedEquipmentCache = new List<EquippedEquipmentEntry>();

        private IReadOnlyList<EquippedEquipmentEntry> GetEquippedEquipmentForDisplay()
        {
            if (IsServer)
                return equippedEquipment ?? (IReadOnlyList<EquippedEquipmentEntry>)new List<EquippedEquipmentEntry>();
            _clientEquippedEquipmentCache.Clear();
            if (equippedEquipmentEntries != null)
            {
                for (int i = 0; i < equippedEquipmentEntries.Count; i++)
                    _clientEquippedEquipmentCache.Add(equippedEquipmentEntries[i]);
            }
            return _clientEquippedEquipmentCache;
        }

        public TeamManager.Team ShipTeam => shipTeam.Value;
        public int ShipLevel => (IsSpawned && networkShipLevel != null) ? networkShipLevel.Value : shipLevel;
        public int BranchIndex => (IsSpawned && networkBranchIndex != null) ? networkBranchIndex.Value : (shipData != null ? shipData.branchIndex : 0);
        public ShipFocusType FocusType => focusType;
        public bool IsInOrbit => currentOrbitPlanet != null;
        public Planet CurrentOrbitPlanet => currentOrbitPlanet;

        /// <summary>
        /// True when the ship sits in its cached orbit planet's people-transfer ring (relaxed margin for replicated pose jitter).
        /// Used by people transports to skip hull collisions while loading/unloading in orbit.
        /// </summary>
        public bool IsInPlanetOrbitRingForGameplay(float margin = 0.12f)
        {
            Planet planet = currentOrbitPlanet;
            if (planet == null) return false;

            Vector3 rbPos = rb != null ? rb.position : transform.position;
            rbPos.y = 0f;
            Vector3 tPos = transform.position;
            tPos.y = 0f;
            return planet.IsWorldPositionInOrbitRingRelaxed(rbPos, margin)
                || planet.IsWorldPositionInOrbitRingRelaxed(tPos, margin);
        }

        public bool WantToLoadPeople => wantToLoadPeople.Value;
        public bool WantToUnloadPeople => wantToUnloadPeople.Value;
        public bool WantToDepositGems => wantToDepositGems.Value;
        public bool WantToExpelGems => wantToExpelGems.Value;
        /// <summary>Server: owner-synced thrust (right mouse). AI ships always read false.</summary>
        public bool IsMoveForwardPressedForGemMoonLanding => moveForwardPressedNet.Value;
        /// <summary>Server: owner-synced fire held.</summary>
        public bool IsShootPressedForGemMoonLanding => shootPressedNet.Value;
        /// <summary>True when docked at the planet's gem moon (synced from server).</summary>
        public bool GemMoonDocked => gemMoonDocked.Value;

        /// <summary>True while the gem-moon orbit station menu is shown after landing.</summary>
        public bool IsOrbitStationMenuVisible => _orbitUiVisible;

        /// <summary>
        /// True while the gem-moon orbit station menu is open and the pointer is over its UI.
        /// Clicks there should browse the menu, not exit theatrical camera or count as combat input.
        /// </summary>
        public bool IsInteractingWithOrbitStationMenu =>
            _orbitUiVisible && IsPointerOverUI();

        /// <summary>Planar speed in world XZ (m/s) for camera / UI.</summary>
        public float GetPlanarSpeedWorld()
        {
            Vector3 v = GetSimVelocity();
            v.y = 0f;
            return v.magnitude;
        }

        /// <summary>Below this yaw delta (°), mouse-aim rotation is treated as settled for idle camera.</summary>
        private const float MouseAimAlignmentToleranceDeg = 1.5f;

        /// <summary>True while the ship is still turning to face the mouse pointer.</summary>
        public bool IsRotatingTowardMousePointer()
        {
            if (IsBulletElectricShockDisabled || rb == null)
                return false;

            EnsureCachedCameraControllerForShake();
            if (s_cachedCameraController != null && s_cachedCameraController.IsTheatricalShipRotationLocked)
                return false;

            if (!TryGetMouseAimRotation(out Quaternion targetRotation))
                return false;

            return Quaternion.Angle(rb.rotation, targetRotation) > MouseAimAlignmentToleranceDeg;
        }

        private bool TryGetMouseAimRotation(out Quaternion targetRotation)
        {
            targetRotation = Quaternion.identity;
            UnityEngine.Camera cam = UnityEngine.Camera.main;
            if (cam == null || inputHandler == null)
                return false;

            Vector3 mouseWorldPos = inputHandler.GetMouseWorldPosition(cam);
            Vector3 directionToMouse = mouseWorldPos - transform.position;
            directionToMouse.y = 0f;
            if (directionToMouse.sqrMagnitude <= 0.001f)
                return false;

            directionToMouse.Normalize();
            targetRotation = Quaternion.LookRotation(directionToMouse);
            return true;
        }

        /// <summary>First friendly gem moon whose dock shell contains this ship (toroidal).</summary>
        public bool TryGetFriendlyGemMoonInZone(out PlanetGemMoon moon, float radiusMultiplier = 1.05f)
        {
            moon = null;
            if (shipTeam.Value == TeamManager.Team.None) return false;
            for (int i = 0; i < PlanetGemMoon.ActiveMoonCount; i++)
            {
                PlanetGemMoon candidate = PlanetGemMoon.GetActiveMoonAt(i);
                if (candidate == null || !candidate.IsTeamFriendlyToThisMoon(shipTeam.Value)) continue;
                if (!candidate.IsShipInMoonDockZoneToroidal(this, radiusMultiplier)) continue;
                moon = candidate;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Camera follow point. In the friendly moon shell on the owning client, uses display-space moon center + toroidal
        /// offset so follow matches rendered moon/planet motion (reduces orbit jitter).
        /// </summary>
        public Vector3 GetCameraFollowWorldPosition()
        {
            Vector3 shipPos = UsesInputSyncedMotor && !gemMoonDocked.Value
                ? GetDisplayMotorWorldPosition()
                : (rb != null ? rb.position : transform.position);
            shipPos.y = FIXED_Y_POSITION;
            if (ShouldUseGemMoonDisplayDockSpace() && TryGetFriendlyGemMoonInZone(out PlanetGemMoon moon))
            {
                Vector3 moonPos = moon.GetDisplayWorldPosition();
                shipPos = moonPos + ToroidalMap.ShortestWorldOffsetXZ(moonPos, shipPos);
            }
            return shipPos;
        }

        private bool ShouldUseGemMoonDisplayDockSpace() =>
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient;

        /// <summary>
        /// Distance from moon center to ship root while docked: visual moon radius + scaled hull extent + surface standoff.
        /// </summary>
        private float ComputeGemMoonDockContactRadiusWorld(PlanetGemMoon moon)
        {
            if (moon == null) return 0.0001f;
            float moonRadius = Mathf.Max(moon.GetMoonVisualRadiusWorld(), moon.GetMoonBodyRadiusWorld());
            float hullScale = Mathf.Max(0.05f, gemMoonVisualScaleMultiplier);
            float shipHull = GetShipMoonDockRadiusXZ() * hullScale;
            float standoff = moonRadius * gemMoonSurfaceStandoffOverMoonRadius;
            return moonRadius + shipHull + standoff;
        }

        /// <summary>
        /// Snaps the ship onto the gem-moon surface and advances the landing offset with moon spin.
        /// Server uses canonical gameplay space; clients use display space (after the moon's orbit LateUpdate).
        /// </summary>
        /// <param name="advanceSpin">When false, repositions from the current offset without rotating it (host client visual pass).</param>
        private bool ApplyGemMoonDockSurfaceSnap(bool useDisplaySpace, bool advanceSpin = true)
        {
            if (isDead.Value || !gemMoonDocked.Value || rb == null) return false;

            Planet dockPlanet = ResolveGemMoonDockPlanet();
            PlanetGemMoon moon = dockPlanet != null ? dockPlanet.GemMoon : null;
            if (moon == null) return false;

            Vector3 moonPos = moon.GetDockTrackingWorldPosition(useDisplaySpace);
            moonPos.y = FIXED_Y_POSITION;

            float moonDockOuterRadius = moon.GetMoonDockSnapRadiusWorld() * gemMoonLandingRangeMultiplier;
            float shipRadius = GetShipMoonDockRadiusXZ();

            Vector3 shipPosTransform = transform.position;
            shipPosTransform.y = FIXED_Y_POSITION;
            Vector3 shipPosRigidbody = rb.position;
            shipPosRigidbody.y = FIXED_Y_POSITION;
            float distToMoon = Mathf.Min(
                ToroidalMap.ToroidalDistance(shipPosTransform, moonPos),
                ToroidalMap.ToroidalDistance(shipPosRigidbody, moonPos));

            bool withinGemMoonBoundary = moonDockOuterRadius > 0.0001f
                && distToMoon <= moonDockOuterRadius + shipRadius;
            bool inMoonDockZone = withinGemMoonBoundary
                || moon.IsShipInMoonDockZoneToroidal(this, radiusMultiplier: 1.05f);
            if (!inMoonDockZone) return false;

            float dockDuration = Mathf.Max(0.05f, gemMoonTransitionDurationSeconds);
            float moonDockLinearT = Mathf.Clamp01(gemMoonDockApproachElapsed / dockDuration);
            float moonDockEaseInOut = GemMoonDockEaseInOut(moonDockLinearT);

            ulong currentPlanetId = gemMoonPlanetNetworkObjectId.Value;
            float contactRadius = Mathf.Max(0.0001f, ComputeGemMoonDockContactRadiusWorld(moon));
            Vector3 moonSpinAxis = moon.SpinAxisWorld.normalized;

            bool landingPlanetChanged = gemMoonLandingPlanetIdCache != currentPlanetId;
            if (landingPlanetChanged || gemMoonLandingOffset.sqrMagnitude < 0.0001f)
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
                if (landingPlanetChanged)
                    gemMoonDockApproachElapsed = 0f;
                if (visualRoot != null && visualRoot != transform)
                    gemMoonDockVisualStartRotation = visualRoot.rotation;
            }

            float spinStepDeg = moon.SpinDegreesPerSecond
                * (useDisplaySpace ? Time.deltaTime : Time.fixedDeltaTime)
                * moonDockEaseInOut;

            if (advanceSpin && Mathf.Abs(spinStepDeg) > 0.0001f)
                gemMoonLandingOffset = Quaternion.AngleAxis(spinStepDeg, moon.SpinAxisWorld) * gemMoonLandingOffset;
            Vector3 radial = gemMoonLandingOffset;
            if (radial.sqrMagnitude < 0.0001f) radial = Vector3.forward;
            radial = radial.normalized * contactRadius;
            gemMoonLandingOffset = radial;

            Vector3 orbitDir = radial.sqrMagnitude > 0.0001f ? radial.normalized : Vector3.forward;

            if (!wasGemMoonDocked || moonDockLinearT <= 0.03f || landingPlanetChanged)
                gemMoonDockApproachStartWorldPos = rb.position;

            Vector3 targetSurfacePos = moonPos + orbitDir * contactRadius;
            Vector3 targetPos = Vector3.Lerp(gemMoonDockApproachStartWorldPos, targetSurfacePos, moonDockEaseInOut);
            rb.MovePosition(targetPos);
            SetRootColliderDocked(true);

            if (visualRoot != null && visualRoot != transform)
            {
                if (!wasGemMoonDocked || moonDockLinearT <= 0.03f || landingPlanetChanged)
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
            return true;
        }

        /// <summary>True when this ship is gem-moon docked and the dock target is <paramref name="planet"/>.</summary>
        public bool IsGemMoonDockedAtPlanet(Planet planet)
        {
            if (planet == null || !gemMoonDocked.Value) return false;
            var planetNo = planet.GetComponent<NetworkObject>();
            if (planetNo == null) return false;
            return gemMoonPlanetNetworkObjectId.Value == planetNo.NetworkObjectId;
        }

        /// <summary>
        /// True when the ease-in-out dock transition has finished (ship on moon surface).
        /// Gem deposit must wait for this — <see cref="gemMoonDocked"/> latches earlier when landing gates pass.
        /// </summary>
        public bool IsGemMoonSurfaceLandingComplete()
        {
            if (!gemMoonDocked.Value) return false;
            float dockDuration = Mathf.Max(0.05f, gemMoonTransitionDurationSeconds);
            return gemMoonDockApproachElapsed >= dockDuration;
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

        /// <summary>Uniform visual scale from ship level (1.15^(level-1) by default). Multiplies prefab root in LateUpdate.</summary>
        public float LevelScaleFactor => Mathf.Pow(Mathf.Max(1f, shipLevelScalePerLevel), Mathf.Max(0, ShipLevel - 1));

        /// <summary>
        /// Prefab-root localScale for menu thumbnails: chassis base × ship level, excluding moon-dock shrink.
        /// </summary>
        public Vector3 GetMenuPreviewHullLocalScale()
        {
            Vector3 prefabScale = lastPrefabScale.sqrMagnitude > 0.001f ? lastPrefabScale : Vector3.one;
            float levelVisual = Mathf.Max(0.001f, visualBaseScale) * LevelScaleFactor;
            return Vector3.Scale(prefabScale, Vector3.one * levelVisual);
        }

        /// <summary>Refreshes attribute-upgrade component scales on the live hull before menu preview capture.</summary>
        public void EnsureMenuPreviewVisualSourceUpToDate()
        {
            ApplyComponentAttributeScaling();
        }

        /// <summary>Copies per-component positions/rotations/scales (attribute upgrades, equipped parts) onto a preview clone.</summary>
        public void SyncMenuPreviewComponentScales(Transform previewHullRoot)
        {
            Transform liveRoot = GetCardVisualRoot();
            if (liveRoot == null || previewHullRoot == null)
                return;

            SyncMenuPreviewTransformChildren(liveRoot, previewHullRoot);
        }

        private static void SyncMenuPreviewTransformChildren(Transform source, Transform dest)
        {
            int count = source.childCount;
            for (int i = 0; i < count; i++)
            {
                Transform srcChild = source.GetChild(i);
                Transform destChild = i < dest.childCount ? dest.GetChild(i) : dest.Find(srcChild.name);
                if (destChild == null)
                    continue;

                destChild.localPosition = srcChild.localPosition;
                destChild.localRotation = srcChild.localRotation;
                destChild.localScale = srcChild.localScale;
                SyncMenuPreviewTransformChildren(srcChild, destChild);
            }
        }

        /// <summary>Base visual scale (from ShipData/chassis).</summary>
        private float visualBaseScale = 1f;
        /// <summary>Prefab root localScale from the loaded model (for re-applying with level scale in LateUpdate).</summary>
        private Vector3 lastPrefabScale = Vector3.one;
        /// <summary>Local scale cache so gem-moon docking can scale the whole ship safely.</summary>
        private Vector3 baseLocalScale = Vector3.one;

        private void Awake()
        {
            // Run before OnNetworkSpawn/SetShipData so the BankPivot + Prefab structure exists.
            EnsureVisualRootForBanking();

            droneSwarm = GetComponent<DroneSwarmController>();
            if (droneSwarm == null)
                droneSwarm = gameObject.AddComponent<DroneSwarmController>();

            baseLocalScale = transform.localScale;

            if (rb == null) rb = GetComponent<Rigidbody>();
            rootCollider = GetComponent<Collider>();
            TryCaptureRootBoxColliderBaseline();
            EnsureMoonDockProbeCollider();
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
            equippedEquipmentEntries = new NetworkList<EquippedEquipmentEntry>();
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
            isDead.OnValueChanged -= HandleIsDeadChangedForBurnVfx;
            UnsubscribeEquippedEquipmentVisuals();
            ClearClientBurnVfx();
            // Remove from global registry if present
            AllStarships.Remove(this);
            equippedCardIds?.Dispose();
            equippedEquipmentEntries?.Dispose();
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
            isDead.OnValueChanged += HandleIsDeadChangedForBurnVfx;
            currentEnergy.OnValueChanged += OnCurrentEnergyDisplaySync;
            currentHealth.OnValueChanged += OnCurrentHealthDisplaySync;
            InitializeClientRegenDisplayBaselines();
            if (!AllStarships.Contains(this))
                AllStarships.Add(this);
            // Server: sync initial ship level so clients show correct slot count
            if (IsServer && networkShipLevel != null)
                networkShipLevel.Value = Mathf.Max(1, shipLevel);
            if (IsServer)
                lastSharedPoolWeaponFiredIndex.Value = -1;
            if (IsServer && networkBranchIndex != null && shipData != null)
                networkBranchIndex.Value = shipData.branchIndex;

            // Server: sync existing equipped cards to NetworkList (e.g. from save or late-join)
            if (IsServer && equippedCardIds != null && equippedCards != null)
            {
                for (int i = equippedCardIds.Count; i < equippedCards.Count; i++)
                {
                    if (i < equippedCards.Count && equippedCards[i] != null)
                        equippedCardIds.Add(new EquippedCardId { cardId = new FixedString64Bytes(equippedCards[i].GetStableCardId()) });
                }
            }

            if (IsServer && equippedEquipmentEntries != null && equippedEquipment != null)
            {
                for (int i = equippedEquipmentEntries.Count; i < equippedEquipment.Count; i++)
                    equippedEquipmentEntries.Add(equippedEquipment[i]);
            }

            // Scene / old prefab: apply ShipData so chassis visuals and weapon components initialize.
            if (shipData != null && bulletConfig == null && currentChassisIndex.Value < 0)
                SetShipData(shipData);

            // Ensure Y position is locked to 0
            Vector3 pos = transform.position;
            pos.y = FIXED_Y_POSITION;
            transform.position = pos;

            if (IsServer && IsOwner)
                TryRestoreOrApplyDefaultSpawnSetup(UnityGameServicesBootstrap.PlayerId, NetworkGameManager.PendingRestoreChoice);

            if (IsClient && IsOwner)
                RegisterMapInstanceProgressServerRpc(
                    UnityGameServicesBootstrap.PlayerId ?? string.Empty,
                    (int)NetworkGameManager.PendingRestoreChoice);

            // Initialize banking state so first LateUpdate doesn't spike
            if (rb != null)
            {
                _prevBankYawDeg = GetPlanarYawDegrees(rb.rotation);
                _bankYawInitialized = true;
                _cachedBankAngularVelDegPerSec = 0f;
                bankingInitialized = true;
            }

            // Team is server-authored; hull materials are applied on server in AssignTeamAndStartInOrbit. Owning client must
            // refresh when shipTeam replicates (otherwise local player stays neutral while remotes see correct team color).
            shipTeam.OnValueChanged += OnShipTeamValueChanged;
            ApplyHullIdentityColor();

            SyncInputHandlerForTeamSelectionState();

            InitializeMotorSimOnSpawn();

            droneSwarm?.OnStarshipNetworkSpawn();

            SubscribeEquippedEquipmentVisuals();
            RebuildEquippedComponentVisuals();

            // Ship loadout grid is shown by OrbitStationUI when in orbit; no separate ShipCardGridUI needed.
        }

        private void OnShipTeamValueChanged(TeamManager.Team previous, TeamManager.Team current)
        {
            ApplyHullIdentityColor();
            SyncInputHandlerForTeamSelectionState();
        }

        public override void OnNetworkDespawn()
        {
            droneSwarm?.OnStarshipNetworkDespawn();
            UnsubscribeMotorNetworkCallbacks();
            currentEnergy.OnValueChanged -= OnCurrentEnergyDisplaySync;
            currentHealth.OnValueChanged -= OnCurrentHealthDisplaySync;
            shipTeam.OnValueChanged -= OnShipTeamValueChanged;
            if (IsServer
                && (TeamManager.Instance == null || !TeamManager.Instance.IsTeamEliminated(shipTeam.Value)))
            {
                MapInstanceShipProgressStore.SaveSnapshot(
                    MapInstanceShipProgressStore.ResolveAuthPlayerId(OwnerClientId),
                    CaptureMapInstanceProgress());
            }
            base.OnNetworkDespawn();
        }

        [ServerRpc(RequireOwnership = true)]
        private void RegisterMapInstanceProgressServerRpc(string authPlayerId, int restoreChoiceInt, ServerRpcParams rpcParams = default)
        {
            if (!IsServer) return;
            if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
            MapInstanceShipProgressStore.RegisterClientAuthId(
                OwnerClientId,
                MapInstanceShipProgressStore.NormalizeAuthPlayerId(authPlayerId, OwnerClientId));
            TryRestoreOrApplyDefaultSpawnSetup(authPlayerId, (NetworkGameManager.ShipRestoreChoice)restoreChoiceInt);
        }

        private void TryRestoreOrApplyDefaultSpawnSetup(string authPlayerId, NetworkGameManager.ShipRestoreChoice restoreChoice)
        {
            if (!IsServer || _playerSpawnSetupComplete) return;

            string key = MapInstanceShipProgressStore.NormalizeAuthPlayerId(authPlayerId, OwnerClientId);
            MapInstanceShipProgressStore.RegisterClientAuthId(OwnerClientId, key);

            if (restoreChoice == NetworkGameManager.ShipRestoreChoice.StartAnew)
            {
                MapInstanceShipProgressStore.RemoveSnapshot(key);
                ApplyDefaultPlayerSpawnSetup();
                NetworkGameManager.PendingRestoreChoice = NetworkGameManager.ShipRestoreChoice.Unset;
                return;
            }

            if (MapInstanceShipProgressStore.TryGetSnapshot(key, out PlayerShipProgressSnapshot snapshot))
            {
                if (TeamManager.Instance != null && TeamManager.Instance.IsTeamEliminated(snapshot.Team))
                {
                    MapInstanceShipProgressStore.RemoveSnapshot(key);
                    ApplyDefaultPlayerSpawnSetup();
                    NetworkGameManager.PendingRestoreChoice = NetworkGameManager.ShipRestoreChoice.Unset;
                    return;
                }

                bool useRescueVitals = restoreChoice == NetworkGameManager.ShipRestoreChoice.Rescue;
                ApplyMapInstanceProgress(snapshot, useRescueVitals);
            }
            else
                ApplyDefaultPlayerSpawnSetup();

            NetworkGameManager.PendingRestoreChoice = NetworkGameManager.ShipRestoreChoice.Unset;
        }

        /// <summary>Server: first-time spawn for this map instance (starter chassis, fresh vitals).</summary>
        private void ApplyDefaultPlayerSpawnSetup()
        {
            if (!IsServer || _playerSpawnSetupComplete) return;
            _playerSpawnSetupComplete = true;

            if (currentChassisIndex.Value == -1 && CardShopSystem.Instance != null)
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
                    if (!string.IsNullOrEmpty(starterChassisId)) SetCurrentChassisId(starterChassisId);
                    _lastAppliedChassisIndex = 0;
                    _lastAppliedShipLevel = ShipLevel;
                }
                else
                    Debug.LogWarning("Starship: No starter ship prefab. Assign ShipUnlockTable.homeShipFamilyDefinition (e.g. AstroEagleShipFamily) with upgrade tree prefabs, and ensure CardShopSystem references the same ShipUnlockTable.");
            }

            currentHealth.Value = MaxHealth;
            currentGems.Value = 0f;
            currentPeople.Value = 0f;
            RefillCannonEnergyFromServer();
            RefreshSyncedCapacitiesOnServer();
            if (TeamManager.Instance != null)
                shipTeam.Value = TeamManager.Instance.GetPlayerTeam(OwnerClientId);
            PlaceShipForCurrentTeamOrLobby();
        }

        private void PlaceShipForCurrentTeamOrLobby()
        {
            if (!IsServer || rb == null) return;
            if (shipTeam.Value == TeamManager.Team.None)
            {
                Vector3 lobbyPos = new Vector3(0f, -10000f, 0f);
                rb.position = lobbyPos;
                rb.linearVelocity = Vector3.zero;
                return;
            }
            StartInOrbitAroundHomePlanet();
        }

        /// <summary>Server: snapshot loadout for <see cref="MapInstanceShipProgressStore"/>.</summary>
        public PlayerShipProgressSnapshot CaptureMapInstanceProgress()
        {
            var cardIds = new List<string>();
            if (equippedCards != null)
            {
                for (int i = 0; i < equippedCards.Count; i++)
                {
                    if (equippedCards[i] != null)
                    {
                        string stableId = equippedCards[i].GetStableCardId();
                        if (!string.IsNullOrEmpty(stableId))
                            cardIds.Add(stableId);
                    }
                }
            }

            return new PlayerShipProgressSnapshot(
                ShipLevel,
                BranchIndex,
                currentChassisIndex.Value,
                currentChassisId.Value.ToString(),
                shipTeam.Value,
                attrFirePower.Value,
                attrBulletSpeed.Value,
                attrMaxHealth.Value,
                attrHealthRegen.Value,
                attrEnergyCapacity.Value,
                attrEnergyRegen.Value,
                attrMovementSpeed.Value,
                attrRotationSpeed.Value,
                attrGemCapacity.Value,
                attrPeopleCapacity.Value,
                cardIds.ToArray(),
                CaptureEquipmentItemTypes(),
                CaptureEquipmentCharges(),
                CaptureEquipmentComponentIds(),
                CaptureEquipmentPlacement(),
                smallRocketsCount.Value,
                largeRocketsCount.Value,
                smallMinesCount.Value,
                largeMinesCount.Value,
                currentHealth.Value,
                currentGems.Value,
                currentPeople.Value,
                currentEnergy.Value);
        }

        /// <summary>Server: restore loadout saved for this map instance.</summary>
        private void ApplyMapInstanceProgress(in PlayerShipProgressSnapshot snapshot, bool useRescueVitals = false)
        {
            if (!IsServer) return;
            _playerSpawnSetupComplete = true;

            shipLevel = Mathf.Max(1, snapshot.ShipLevel);
            if (networkShipLevel != null)
                networkShipLevel.Value = shipLevel;
            if (networkBranchIndex != null)
                networkBranchIndex.Value = snapshot.BranchIndex;

            SetCurrentChassisIndex(snapshot.ChassisIndex);
            if (!string.IsNullOrEmpty(snapshot.ChassisId))
                SetCurrentChassisId(snapshot.ChassisId);

            SyncShipDataToLevelAndBranch(snapshot.ShipLevel, snapshot.BranchIndex);

            attrFirePower.Value = snapshot.AttrFirePower;
            attrBulletSpeed.Value = snapshot.AttrBulletSpeed;
            attrMaxHealth.Value = snapshot.AttrMaxHealth;
            attrHealthRegen.Value = snapshot.AttrHealthRegen;
            attrEnergyCapacity.Value = snapshot.AttrEnergyCapacity;
            attrEnergyRegen.Value = snapshot.AttrEnergyRegen;
            attrMovementSpeed.Value = snapshot.AttrMovementSpeed;
            attrRotationSpeed.Value = snapshot.AttrRotationSpeed;
            attrGemCapacity.Value = snapshot.AttrGemCapacity;
            attrPeopleCapacity.Value = snapshot.AttrPeopleCapacity;

            ClearAllCardsFromServer();
            ClearAllEquipmentFromServer();
            if (CardShopSystem.Instance != null && snapshot.CardIds != null)
            {
                for (int i = 0; i < snapshot.CardIds.Length; i++)
                {
                    CardData card = CardShopSystem.Instance.GetCardByIdForShip(this, snapshot.CardIds[i]);
                    if (card != null)
                        AddCardFromServer(card);
                }
            }

            RestoreEquipmentFromSnapshot(snapshot);
            HomePlanetStoreSystem.Instance?.RespawnEquipmentDronesForShip(this);

            smallRocketsCount.Value = snapshot.SmallRockets;
            largeRocketsCount.Value = snapshot.LargeRockets;
            smallMinesCount.Value = snapshot.SmallMines;
            largeMinesCount.Value = snapshot.LargeMines;

            // Apply hull after level, attributes, cards, and equipment are restored so per-level stats and component scaling match.
            TryApplyChassisVisualFromNetworkState();

            // Vitals must be set after chassis so MaxHealth / energy caps reflect the restored ship level and loadout.
            RefreshSyncedCapacitiesOnServer();
            if (useRescueVitals)
            {
                currentHealth.Value = MaxHealth;
                currentGems.Value = 0f;
                currentPeople.Value = 0f;
                RefillCannonEnergyFromServer();
            }
            else
            {
                currentHealth.Value = Mathf.Clamp(snapshot.CurrentHealth, 0f, MaxHealth);
                currentGems.Value = Mathf.Clamp(snapshot.CurrentGems, 0f, GemCapacity);
                currentPeople.Value = Mathf.Clamp(snapshot.CurrentPeople, 0f, PeopleCapacity);
                currentEnergy.Value = Mathf.Clamp(snapshot.CurrentEnergy, 0f, EffectiveEnergyCapacity);
            }

            if (snapshot.Team != TeamManager.Team.None && TeamManager.Instance != null)
            {
                if (TeamManager.Instance.GetPlayerTeam(OwnerClientId) == TeamManager.Team.None)
                    TeamManager.Instance.AddPlayerToTeam(OwnerClientId, snapshot.Team);
                shipTeam.Value = snapshot.Team;
            }
            else if (TeamManager.Instance != null)
            {
                shipTeam.Value = TeamManager.Instance.GetPlayerTeam(OwnerClientId);
            }

            ApplyHullIdentityColor();
            PlaceShipForCurrentTeamOrLobby();
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
            PlaceShipInOrbitAround(home);
        }

        /// <summary>Computes spawn pose in the home orbit band. Returns false for AI ships (placed elsewhere).</summary>
        private bool TryComputeOrbitSpawnPose(Planet planet, out Vector3 orbitPos, out Vector3 linearVelocity, out Quaternion rotation)
        {
            orbitPos = default;
            linearVelocity = default;
            rotation = default;
            if (planet == null || rb == null) return false;

            float orbitRadius = planet.PlanetSize * planet.GetOrbitRingCenterRadiusLocal();
            Vector3 planetPos = planet.transform.position;
            orbitPos = planetPos + new Vector3(orbitRadius, 0f, 0f);
            orbitPos.y = FIXED_Y_POSITION;

            float innerWorld = planet.PlanetSize * planet.GetOrbitRingInnerRadiusLocal();
            float outerWorld = planet.PlanetSize * planet.GetOrbitRingOuterRadiusLocal();
            float targetSpeed = GetOrbitTargetSpeed(planet, orbitRadius, innerWorld, outerWorld);

            linearVelocity = new Vector3(0f, 0f, -targetSpeed);
            Vector3 horizForward = linearVelocity.sqrMagnitude > 0.0001f ? linearVelocity.normalized : Vector3.forward;
            rotation = Quaternion.LookRotation(horizForward, Vector3.up);
            return true;
        }

        private void ApplyServerOrbitSpawnPoseAndNotifyOwner(Vector3 orbitPos, Vector3 vel, Quaternion rot)
        {
            if (!IsServer || rb == null) return;

            rb.position = orbitPos;
            rb.linearVelocity = vel;
            rb.rotation = rot;
            rb.angularVelocity = Vector3.zero;
            currentVelocity = vel;

            SyncMotorRigidbodyToTransform();

            var nt = GetComponent<NetworkTransform>();
            if (nt != null)
                nt.SetState(orbitPos, rot, transform.localScale, teleportDisabled: false);

            float mass = rb != null ? rb.mass : EffectiveMass;
            SnapMotorSimAfterSpawn(orbitPos, rot, vel, mass);
            BroadcastSnapMotorSimClientRpc(orbitPos, vel, rot, mass);
        }

        /// <summary>Dedicated owner: snap sim state to server orbit/respawn pose.</summary>
        [ClientRpc]
        private void SnapOwnerMotorPoseClientRpc(Vector3 orbitPos, Vector3 vel, Quaternion rot, ClientRpcParams rpcParams = default)
        {
            if (!IsOwner || IsServer) return;
            SnapClientMotorPose(orbitPos, rot, vel);
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
            if (IsServer && !_playerSpawnSetupComplete && currentChassisIndex.Value == -1 && _lastAppliedChassisIndex == -2 && CardShopSystem.Instance != null)
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
                    _lastAppliedShipLevel = ShipLevel;
                }
            }
            // When chassis index/id or ship level is synced from the server, every peer must build the mesh (not just the owner).
            // Level can replicate after chassis during rescue restore; re-apply when either changes so per-level hull scaling is correct.
            if (currentChassisIndex.Value >= 0 && CardShopSystem.Instance != null
                && (currentChassisIndex.Value != _lastAppliedChassisIndex || ShipLevel != _lastAppliedShipLevel))
            {
                TryApplyChassisVisualFromNetworkState();
            }

            if (!IsOwner) return;

            if (IsAwaitingTeamSelection)
            {
                return;
            }

            HandleInput();

            bool movePressed = inputHandler != null && inputHandler.MoveForwardPressed;
            if (movePressed && !wasMovePressedLastFrame && gemMoonDocked.Value)
                RequestUndockGemMoonServerRpc();

            bool shootPressed = inputHandler != null && inputHandler.ShootPressed;

            // When the local player begins moving or firing, restore gameplay camera (galactic zoom / theatrical orbit).
            if (IsLocalPlayerShip()
                && !IsInteractingWithOrbitStationMenu
                && ((movePressed && !wasMovePressedLastFrame) || (shootPressed && !wasShootPressedLastFrame)))
            {
                if (s_cachedCameraController == null)
                    s_cachedCameraController = UnityEngine.Object.FindFirstObjectByType<TitanOrbit.Camera.CameraController>();
                if (s_cachedCameraController != null)
                {
                    s_cachedCameraController.TriggerGalacticZoomReturn();
                    s_cachedCameraController.TriggerTheatricalReturn();
                }
            }

            bool isLocalWithTeam = IsLocalPlayerShip() && shipTeam.Value != TeamManager.Team.None;
            Planet orbitUiPlanet = currentOrbitPlanet;
            if (orbitUiPlanet == null && gemMoonDocked.Value)
                orbitUiPlanet = ResolveGemMoonDockPlanet();

            bool dockedOrbitEligible = isLocalWithTeam && !movePressed && orbitUiPlanet != null && gemMoonDocked.Value;
            if (dockedOrbitEligible && IsGemMoonSurfaceLandingComplete())
            {
                if (_gemMoonLandingCompleteTime < 0f)
                    _gemMoonLandingCompleteTime = Time.time;
            }
            else if (!gemMoonDocked.Value)
            {
                _gemMoonLandingCompleteTime = -1f;
            }

            bool shouldShowOrbitUI = dockedOrbitEligible
                && IsGemMoonSurfaceLandingComplete()
                && _gemMoonLandingCompleteTime >= 0f
                && Time.time >= _gemMoonLandingCompleteTime + GemMoonDockMenuDelayAfterLandingSeconds;

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
            wasShootPressedLastFrame = shootPressed;
        }

        private void LateUpdate()
        {
            RefreshCardStatsCache();

            ClientApplyMotorPoseSmoothing();

            // After PlanetGemMoon orbit LateUpdate (32100): every client re-snaps docked ships to the rendered moon
            // so they ride planetary orbit + moon spin. Server motor snapshots stay in gameplay space; display snap
            // must not run on the dedicated server process (headless has no display tile).
            if (ShouldUseGemMoonDisplayDockSpace() && gemMoonDocked.Value)
            {
                // Host: server FixedUpdate already advanced spin; only reproject to display tile here.
                bool advanceSpin = !IsServer;
                if (ApplyGemMoonDockSurfaceSnap(useDisplaySpace: true, advanceSpin: advanceSpin))
                    SyncMotorRigidbodyToTransform();
            }

            if (IsServer && IsSpawned && (Time.frameCount & 31) == 0)
            {
                float gemCap = ComputeGemCapacityLocal();
                float peopleCap = ComputePeopleCapacityLocal();
                float healthCap = ComputeMaxHealthLocal();
                float energyCap = ComputeEnergyCapacityLocal();
                if (currentGems.Value > gemCap + 0.001f || currentPeople.Value > peopleCap + 0.001f
                    || currentHealth.Value > healthCap + 0.001f || currentEnergy.Value > energyCap + 0.001f
                    || !Mathf.Approximately(networkGemCapacity.Value, gemCap)
                    || !Mathf.Approximately(networkPeopleCapacity.Value, peopleCap)
                    || !Mathf.Approximately(networkMaxHealth.Value, healthCap)
                    || !Mathf.Approximately(networkEnergyCapacity.Value, energyCap))
                {
                    ClampCarriedResourcesToCapacity();
                }
            }
            if (visualBaseScale > 0.001f && lastPrefabScale.sqrMagnitude > 0.001f)
            {
                Transform root = GetPrefabTransform();
                if (root != null)
                {
                    float v = visualBaseScale * Mathf.Max(0.001f, gemMoonVisualScaleMultiplier) * LevelScaleFactor;
                    root.localScale = Vector3.Scale(lastPrefabScale, Vector3.one * v);
                }
            }
            if (_lastRadiusCacheShipLevel != ShipLevel)
            {
                cachedGemFlythroughPickupRadius = -1f;
                _lastRadiusCacheShipLevel = ShipLevel;
            }
            ApplyComponentAttributeScaling();
            UpdateMoonDockProbeCollider();
            UpdateEngineAndThrusterVFX();
            ResolveBankPivotFromHierarchy();
            if (!enableVisualBankingPitch) return;
            if (visualRoot == null || visualRoot == transform || isDead.Value || rb == null) return;
            if (gemMoonDocked.Value) return;
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

            IReadOnlyList<CardData> cards = GetEquippedCardsForDisplay();
            if (cards == null) return;
            foreach (var card in cards)
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

        /// <summary>
        /// Visual scale multiplier from orbit-station attribute upgrades only (not ship level, cards, or equipment stats).
        /// Ship level uses <see cref="LevelScaleFactor"/> on the prefab root; this stacks on top per component type.
        /// </summary>
        private float AttributeUpgradeRatio(int attributeLevel) =>
            1f + attributeLevel * ATTR_MULTIPLIER_PER_LEVEL;

        /// <summary>Scale ship components by attribute upgrade grid only. Level size is handled by <see cref="LevelScaleFactor"/> on the prefab root.</summary>
        private void ApplyComponentAttributeScaling()
        {
            float vis = Mathf.Max(0.2f, componentScaleVisibility);

            float StatScale(float ratio, float visibility, float boost = 1f)
            {
                float clampedRatio = Mathf.Max(1f, ratio);
                return Mathf.Max(1f, 1f + (clampedRatio - 1f) * visibility * Mathf.Max(0.01f, boost));
            }

            float rHealth = AttributeUpgradeRatio(attrMaxHealth.Value);
            float rHealthRegen = AttributeUpgradeRatio(attrHealthRegen.Value);
            float rEnergyCap = AttributeUpgradeRatio(attrEnergyCapacity.Value);
            float rEnergyRegen = AttributeUpgradeRatio(attrEnergyRegen.Value);
            float rPeople = AttributeUpgradeRatio(attrPeopleCapacity.Value);
            float rGem = AttributeUpgradeRatio(attrGemCapacity.Value);
            float rMove = AttributeUpgradeRatio(attrMovementSpeed.Value);
            float rTurn = AttributeUpgradeRatio(attrRotationSpeed.Value);
            float rDamage = AttributeUpgradeRatio(attrFirePower.Value);
            float rBulletSpeed = AttributeUpgradeRatio(attrBulletSpeed.Value);

            float avgBody = (rHealth + rPeople + rEnergyCap + rEnergyRegen) * 0.25f;
            float avgWeapon = (rDamage + rBulletSpeed) * 0.5f;
            float avgPart = (rHealth + rHealthRegen + rGem + rPeople) * 0.25f;

            float cockpitScale = Mathf.Max(
                StatScale(avgBody, vis),
                StatScale(Mathf.Max(Mathf.Max(rHealth, rPeople), Mathf.Max(rEnergyCap, rEnergyRegen)), vis, 0.9f));

            float wingScaleFromGem = StatScale(rGem, vis, wingGemScaleBoost);
            float wingScaleFromTurn = StatScale(rTurn, vis, 0.9f);
            float wingScale = Mathf.Max(wingScaleFromGem, StatScale((rGem + rTurn) * 0.5f, vis));
            wingScale = Mathf.Max(wingScale, wingScaleFromTurn);

            float weaponScale = Mathf.Max(
                StatScale(avgWeapon, vis),
                StatScale(Mathf.Max(rDamage, rBulletSpeed), vis, 0.9f));
            // Weapon meshes often hold cannon energy; reflect health/energy upgrades when there is no Cockpit child.
            if (HasWeaponComponentEnergy || cockpitScaleTransforms.Count == 0)
                weaponScale = Mathf.Max(weaponScale, StatScale(avgBody, vis, 0.85f));

            float engineScale = Mathf.Max(StatScale(rMove, vis), StatScale((rMove + rHealth) * 0.5f, vis, 0.85f));
            float thrusterScale = Mathf.Max(StatScale(rMove, vis, 0.9f), StatScale(rTurn, vis, 0.8f));
            float partScale = Mathf.Max(StatScale(avgPart, vis), StatScale(Mathf.Max(rGem, rHealth), vis, 0.85f));

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

            // Muzzle particles: size follows weapon scale, speed follows bullet speed upgrades
            float muzzleSpeedScale = Mathf.Max(0.5f, rBulletSpeed);
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

            // Root collider: level base × largest attribute-upgrade component scale (prefab root already carries level scale).
            float maxUpgradeVisualScale = Mathf.Max(1f, wingScale, cockpitScale, weaponScale, engineScale, thrusterScale, partScale);
            ApplyRootColliderForAttributeScale(LevelScaleFactor * maxUpgradeVisualScale);
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

        private void EnsureMoonDockProbeCollider()
        {
            if (moonDockProbeCollider != null) return;
            var go = new GameObject("MoonDockProbe");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            moonDockProbeCollider = go.AddComponent<SphereCollider>();
            moonDockProbeCollider.isTrigger = true;
        }

        private void UpdateMoonDockProbeCollider()
        {
            EnsureMoonDockProbeCollider();
            if (moonDockProbeCollider == null) return;
            moonDockProbeCollider.enabled = !gemMoonDocked.Value;
            float worldRadius = GetShipMoonDockRadiusXZ();
            float parentScale = Mathf.Max(0.01f, Mathf.Max(transform.lossyScale.x, transform.lossyScale.z));
            moonDockProbeCollider.radius = worldRadius / parentScale;
        }

        /// <summary>Scales the authored root BoxCollider: level base × attribute-upgrade component scale.</summary>
        private void ApplyRootColliderForAttributeScale(float combinedScaleFactor)
        {
            TryCaptureRootBoxColliderBaseline();
            if (!rootColliderBaselineCaptured) return;
            if (rootCollider == null) rootCollider = GetComponent<Collider>();
            if (!(rootCollider is BoxCollider box)) return;

            float m = Mathf.Max(0.01f, combinedScaleFactor) * Mathf.Max(1f, rootColliderAttributeScalePadding);
            box.size = rootColliderBaselineSize * m;
            box.center = rootColliderBaselineCenter * m;
            cachedGemFlythroughPickupRadius = -1f;
        }

        private static readonly float ENGINE_VFX_SPEED_THRESHOLD = 0.5f;
        private static readonly float THRUSTER_VFX_ANGULAR_THRESHOLD_RAD = 0.15f;
        private static readonly float THRUSTER_VFX_ANGULAR_THRESHOLD_DEG = THRUSTER_VFX_ANGULAR_THRESHOLD_RAD * Mathf.Rad2Deg;
        private static readonly float ENGINE_VFX_EMISSION_RATE = 18f;
        private static readonly float THRUSTER_VFX_EMISSION_RATE = 15f;
        private static readonly string[] VfxColorNames = { "Blue", "Green", "Orange", "Purple", "Red", "Yellow" };
        private bool lastEngineVfxMoving = false;
        private bool lastThrusterVfxTurning = false;
        private float thrusterVfxBlend = 0f;

        private void UpdateEngineAndThrusterVFX()
        {
            if (rb == null) return;
            // Local owner + all client observers (human ships sim locally).
            if (IsServer) return;
            if (engineVfxInstances.Count == 0 && thrusterVfxInstances.Count == 0) return;

            Vector3 vel = GetSimVelocity();
            vel.y = 0f;
            float speed = vel.magnitude;
            float angularRad = rb.angularVelocity.magnitude;
            bool turning = angularRad >= THRUSTER_VFX_ANGULAR_THRESHOLD_RAD
                || Mathf.Abs(_cachedBankAngularVelDegPerSec) >= THRUSTER_VFX_ANGULAR_THRESHOLD_DEG;
            bool accelerating = (speed >= ENGINE_VFX_SPEED_THRESHOLD) && IsActivelyAccelerating();
            bool moving = speed >= ENGINE_VFX_SPEED_THRESHOLD;
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
            if (IsOwner && inputHandler != null)
                return inputHandler.MoveForwardPressed;
            return moveForwardPressedNet.Value;
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
            bool useInputPitch = inputHandler != null;
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

        /// <summary>
        /// Caches the planar yaw rate used to drive visual banking.
        private void CacheVisualAngularVelForBanking(float dt)
        {
            if (rb == null || isDead.Value)
            {
                _cachedBankAngularVelDegPerSec = 0f;
                if (isDead.Value)
                    _bankYawInitialized = false;
                return;
            }

            float yawDeg = GetPlanarYawDegrees(rb.rotation);
            if (!_bankYawInitialized)
            {
                _prevBankYawDeg = yawDeg;
                _bankYawInitialized = true;
                _cachedBankAngularVelDegPerSec = 0f;
                return;
            }

            dt = Mathf.Max(1e-5f, dt);
            float instantAngularVel = Mathf.DeltaAngle(_prevBankYawDeg, yawDeg) / dt;
            _prevBankYawDeg = yawDeg;

            float bankSmooth = shipData != null ? shipData.bankSmoothing : defaultBankSmoothing;
            float velT = 1f - Mathf.Exp(-bankSmooth * dt);
            _cachedBankAngularVelDegPerSec = Mathf.Lerp(_cachedBankAngularVelDegPerSec, instantAngularVel, velT);
        }

        private static float GetPlanarYawDegrees(Quaternion rotation)
        {
            Vector3 fwd = rotation * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-8f)
                return 0f;
            return Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
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

            Vector3 v = UsesInputSyncedMotor ? GetSimVelocity() : rb.linearVelocity;
            v.y = 0f;
            Vector3 ff = UsesInputSyncedMotor ? GetSimRotation() * Vector3.forward : rb.rotation * Vector3.forward;
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

            dt = Mathf.Max(dt, 0.0001f);
            UpdateVisualPitchTarget();

            Transform prefabNode = GetPrefabTransform();
            bool pitchOnPrefabChild = prefabNode != null && prefabNode.parent == visualRoot;

            if (!bankingInitialized)
            {
                currentBankAngle = 0f;
                bankingInitialized = true;
                visualRoot.localRotation = Quaternion.identity;
                if (pitchOnPrefabChild)
                    prefabNode.localRotation = Quaternion.identity;
                return;
            }

            float referenceMaxBank = shipData != null ? shipData.maxBankAngle : defaultMaxBankAngle;
            float bankSmooth = shipData != null ? shipData.bankSmoothing : defaultBankSmoothing;
            // Roll (Z): 0 turn rate → 0 bank; fastest ship's max turn rate → full bank.
            float signedAngularVelDegPerSec = _cachedBankAngularVelDegPerSec;
            Vector3 velFlat = rb.linearVelocity;
            velFlat.y = 0f;
            if (velFlat.sqrMagnitude < IdleVisualLinearSpeedThreshold * IdleVisualLinearSpeedThreshold
                && Mathf.Abs(signedAngularVelDegPerSec) < IdleBankAngularVelDeadbandDegPerSec)
                signedAngularVelDegPerSec = 0f;
            float globalMaxTurnDegPerSec = ShipPropulsionAggregation.GetGlobalMaxTurnSpeedDegreesPerSecond(
                ShipTurnDefinitionToDegreesPerSecond);
            float targetBankAngle = ShipPropulsionAggregation.ComputeVisualBankTargetAngle(
                signedAngularVelDegPerSec,
                referenceMaxBank,
                globalMaxTurnDegPerSec);
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
        }

        private void FixedUpdate()
        {
            if (rb == null) return;

            try
            {
            if (CanApplyLocalRamCameraShake())
                FinalizeAsteroidRamContactsFromLastPhysicsStep();

            if (IsServer)
                TickBulletStatusEffectsServer(Time.fixedDeltaTime);

            TickShipCollisionVelocityEstimate(Time.fixedDeltaTime);

            // Toroidal orbit band (physics triggers fail across map wraps; server + all sim clients need geometry).
            if (IsServer || (IsClient && IsOwner && !IsServer))
                RefreshOrbitPlanetFromPosition();

            if (_hasPendingGemMoonShieldRepel)
            {
                if (IsServer)
                {
                    Vector3 rv = _pendingGemMoonShieldRepelVelocity;
                    rb.linearVelocity = new Vector3(rv.x, 0f, rv.z);
                    rb.angularVelocity = Vector3.zero;
                    currentVelocity = rb.linearVelocity;
                }
                _hasPendingGemMoonShieldRepel = false;
            }

            SyncSimMassFromShip();

            if (gemMoonDocked.Value)
                gemMoonUndockOrbitGraceUntilTime = -1f;
            if (gemMoonDocked.Value)
                gemMoonUndockGraceEndSimTick = 0;

            // Y locked to play plane by motor sim; avoid writing rb.position before the sim step.
            if (!gemMoonDocked.Value && !UsesInputSyncedMotor)
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
            // Velocity Y clamp handled by motor sim for input-synced ships.
            if (!gemMoonDocked.Value && !UsesInputSyncedMotor && Mathf.Abs(rb.linearVelocity.y) > 0.01f)
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
                TickVoluntaryGemExpulsion();
                TickNearbyLootableDroneAttraction();
            }

            if (IsOwner && ownerFiringSessionActive && !IsServer)
                TickOwnerFiringEnergyRegen();

            Planet dockPlanet = null;
            PlanetGemMoon moon = null;
            bool withinGemMoonBoundary = false;
            float moonDockOuterRadius = 0f;

            if (!isDead.Value && gemMoonDocked.Value)
            {
                dockPlanet = ResolveGemMoonDockPlanet();
                moon = dockPlanet != null ? dockPlanet.GemMoon : null;
                if (moon != null)
                {
                    gemMoonUndockCachedMoonPos = moon.GetDockTrackingWorldPosition(ShouldUseGemMoonDisplayDockSpace());
                    Vector3 moonPosForBoundary = gemMoonUndockCachedMoonPos;
                    moonPosForBoundary.y = 0f;
                    Vector3 shipPosTransform = transform.position;
                    shipPosTransform.y = 0f;
                    Vector3 shipPosRigidbody = rb.position;
                    shipPosRigidbody.y = 0f;
                    float distToMoon = Mathf.Min(
                        ToroidalMap.ToroidalDistance(shipPosTransform, moonPosForBoundary),
                        ToroidalMap.ToroidalDistance(shipPosRigidbody, moonPosForBoundary));
                    moonDockOuterRadius = moon.GetMoonDockSnapRadiusWorld() * gemMoonLandingRangeMultiplier;
                    float shipRadius = GetShipMoonDockRadiusXZ();

                    withinGemMoonBoundary = moonDockOuterRadius > 0.0001f
                        && distToMoon <= moonDockOuterRadius + shipRadius;

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

            if (gemMoonDocked.Value)
            {
                // Keep approach progress while docked even if boundary flickers (large hull / toroidal edge).
                bool inMoonZone = withinGemMoonBoundary;
                if (!inMoonZone && moon != null)
                    inMoonZone = moon.IsShipInMoonDockZoneToroidal(this, radiusMultiplier: 1.05f);
                if (inMoonZone)
                    gemMoonDockApproachElapsed += Time.fixedDeltaTime;
            }
            else
                gemMoonDockApproachElapsed = 0f;

            float dockDuration = Mathf.Max(0.05f, gemMoonTransitionDurationSeconds);
            float moonDockEaseInOut = 0f;
            float moonDockLinearT = 0f;
            bool inMoonDockZoneForVisuals = withinGemMoonBoundary;
            if (gemMoonDocked.Value && moon != null
                && !inMoonDockZoneForVisuals)
            {
                inMoonDockZoneForVisuals = moon.IsShipInMoonDockZoneToroidal(this, radiusMultiplier: 1.05f);
            }
            if (gemMoonDocked.Value && inMoonDockZoneForVisuals)
            {
                moonDockLinearT = Mathf.Clamp01(gemMoonDockApproachElapsed / dockDuration);
                moonDockEaseInOut = GemMoonDockEaseInOut(moonDockLinearT);
            }

            if (IsServer)
                ServerTryTriggerGalacticZoomOnMoonSurfaceLanding();

            if (gemMoonDocked.Value && inMoonDockZoneForVisuals)
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
                float undockTargetScale = IsInsideFriendlyGemMoonOrbitZone()
                    ? gemMoonDockScaleAtOrbitEdge
                    : 1f;
                gemMoonVisualScaleMultiplier = Mathf.Lerp(gemMoonUndockStartScale, undockTargetScale, uEase);
                if (u >= 0.999f)
                {
                    gemMoonVisualScaleMultiplier = undockTargetScale;
                    gemMoonUndockBlendActive = false;
                }
            }
            else
            {
                float orbitZoneTargetScale = IsInsideFriendlyGemMoonOrbitZone()
                    ? gemMoonDockScaleAtOrbitEdge
                    : 1f;
                float orbitZoneScaleStep = Mathf.Abs(1f - gemMoonDockScaleAtOrbitEdge)
                    / Mathf.Max(0.05f, gemMoonOrbitZoneScaleTransitionSeconds);
                gemMoonVisualScaleMultiplier = Mathf.MoveTowards(
                    gemMoonVisualScaleMultiplier,
                    orbitZoneTargetScale,
                    Mathf.Max(0.001f, orbitZoneScaleStep * Time.fixedDeltaTime)
                );
            }

            // Keep NetworkObject root at base scale; dock shrink is applied with chassis scale on Prefab in LateUpdate.
            transform.localScale = baseLocalScale;

            // Server: authoritative gameplay-space dock pose for motor replication.
            if (IsServer)
                ApplyGemMoonDockSurfaceSnap(useDisplaySpace: false, advanceSpin: true);
            else if (!isDead.Value && gemMoonDocked.Value && moon != null && withinGemMoonBoundary)
            {
                SetRootColliderDocked(true);
            }
            else if (!gemMoonDocked.Value && wasGemMoonDocked)
            {
                gemMoonLandingOffset = Vector3.zero;
                SetRootColliderDocked(false);
                gemMoonUndockOrbitGraceUntilTime = Time.time + Mathf.Max(0.05f, gemMoonTransitionDurationSeconds);
                if (IsServer)
                    ServerAssignGemMoonUndockGraceSimTick();
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

            if (IsOwner && !isDead.Value && !gemMoonDocked.Value)
                TickNetworkInputSender();

            if (IsClient && IsOwner && !IsServer && !isDead.Value && !gemMoonDocked.Value)
                ClientPredictMotorFixedStep();

            if (!isDead.Value && !gemMoonDocked.Value && IsServer)
            {
                var clock = ServerSimClock.Instance;
                if (clock != null)
                    ServerTickAllShipMotors(clock.SimulationTick);
            }

            if (IsServer)
                TickNearbyGemAttraction();
            }
            finally
            {
                if (rb != null)
                {
                    Vector3 lv = rb.linearVelocity;
                    _lastFixedPlayPlaneVelocity = new Vector3(lv.x, 0f, lv.z);
                }
                CacheVisualForwardAccelForPitch();
                CacheVisualAngularVelForBanking(Time.fixedDeltaTime);
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
                if (moonDockProbeCollider != null)
                    moonDockProbeCollider.enabled = false;
            }
            else if (rootColliderDockOverrideActive)
            {
                rootCollider.enabled = rootColliderEnabledBeforeDock;
                rootColliderDockOverrideActive = false;
                UpdateMoonDockProbeCollider();
            }
        }

        /// <summary>Server: pull nearby lootable drones toward this ship when an equipment slot is free.</summary>
        private void TickNearbyLootableDroneAttraction()
        {
            if (!IsServer) return;
            if (isDead.Value || !HasEmptyEquipmentSlot || gemMoonDocked.Value) return;
            if (LootableDrone.AllLootableDrones == null || LootableDrone.AllLootableDrones.Count == 0) return;

            if (((Time.frameCount + GetInstanceID()) & 1) != 0)
                return;

            foreach (var drone in LootableDrone.AllLootableDrones)
            {
                if (!LootableDroneTractorUtility.ShouldApplyPullPhysics(this, drone))
                    continue;

                Rigidbody droneRb = drone.GetComponent<Rigidbody>();
                if (droneRb == null) continue;
                if (!LootableDroneTractorUtility.TryGetPullTowardDirection(this, drone, out Vector3 pullDir))
                    continue;

                float pullSpeed = LootableDroneTractorUtility.GetPullSpeed(this, drone);
                droneRb.linearVelocity = pullDir * pullSpeed;
                droneRb.linearDamping = 0f;
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

            if (TitanOrbit.Entities.Gem.AllGems == null || TitanOrbit.Entities.Gem.AllGems.Count == 0)
                return;

            TickNearbyGemAttractionImpl();
        }

        private void TickNearbyGemAttractionImpl()
        {
            GemTractorBeamSettings.BeginPhysicsPullUpdate();

            foreach (var gem in TitanOrbit.Entities.Gem.AllGems)
            {
                if (!GemTractorBeamSettings.ShouldApplyGemPullPhysics(this, gem))
                    continue;
                if (!gem.CanShipCollect(this))
                    continue;

                Rigidbody gemRb = gem.GetComponent<Rigidbody>();
                if (gemRb == null) continue;

                float pullSpeed = GemTractorBeamSettings.GetGameplayPullSpeed(this, gem);
                if (!GemTractorBeamSettings.TryGetPullTowardDirection(this, gem, out Vector3 pullDir))
                    continue;

                // Constant linear speed toward the closest assigned wing until collected.
                gemRb.linearVelocity = pullDir * pullSpeed;
                gemRb.linearDamping = 0f;
            }
        }

        private void HandleInput()
        {
            if (inputHandler == null) return;
            if (IsAwaitingTeamSelection) return;

            // Ensure we have a fire point (e.g. if ApplyShipVisual wasn't run or prefab has no FirePoint child)
            EnsureFirePoint();

            // Dead ships cannot process input
            if (isDead.Value)
            {
                moveDirection = Vector3.zero;
                return;
            }

            // Movement: right-click only - move in direction ship is facing
            if (IsBulletElectricShockDisabled)
                moveDirection = Vector3.zero;
            else if (inputHandler.MoveForwardPressed)
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

            // Fire intent is synced via TickNetworkInputSender / SubmitShipInputServerRpc.

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

            // V key: hold to expel carried gems forward — one gem per shot (3 × ship level value), 2 shots/sec.
            bool wantExpelGems = (inputHandler as TitanOrbit.Input.PlayerInputHandler)?.ExpelGemsHeld == true
                || (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.vKey.isPressed);
            bool canExpelGems = !IsPointerOverUI() && !isDead.Value && currentGems.Value > 0.001f;
            bool wantExpelGemsActive = wantExpelGems && canExpelGems;
            if (wantExpelGemsActive != localWantToExpelGemsSent)
            {
                localWantToExpelGemsSent = wantExpelGemsActive;
                SetWantToExpelGemsServerRpc(wantExpelGemsActive);
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
                if (r.gameObject == null || r.module is not GraphicRaycaster)
                    continue;
                // Gem moon world-space stat labels must not cancel hold-fire when the cursor is over an enemy moon.
                if (r.gameObject.GetComponentInParent<TitanOrbit.UI.GemMoonStatsDisplay>() != null)
                {
                    if (TitanOrbit.UI.GemMoonStatsDisplay.PointerHitBlocksCombatInput(r.gameObject))
                        return true;
                    continue;
                }
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

            if (IsBulletElectricShockDisabled)
            {
                TickElectricShockBraking();
                return;
            }

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
            if (IsBulletElectricShockDisabled)
            {
                TickElectricShockBraking();
                return;
            }

            Vector3 planetPos = currentOrbitPlanet.GetOrbitGameplayCenterWorld();
            Vector3 shipPos = rb.position;
            shipPos.y = 0f;
            float dist = ToroidalMap.ToroidalDistance(shipPos, planetPos);
            if (dist < 0.01f) return;

            Vector3 toShip = ToroidalMap.ShortestWorldOffsetXZ(planetPos, shipPos);

            // Orbit ring: steer toward band center; lock center radius after stable orbit.
            float innerWorld = currentOrbitPlanet.PlanetSize * currentOrbitPlanet.GetOrbitRingInnerRadiusLocal();
            float outerWorld = currentOrbitPlanet.PlanetSize * currentOrbitPlanet.GetOrbitRingOuterRadiusLocal();
            bool inOrbitRing = dist >= innerWorld && dist <= outerWorld;

            float graceRemaining = gemMoonUndockOrbitGraceUntilTime - Time.time;
            bool inUndockGrace = !gemMoonDocked.Value && graceRemaining > 0f;

            if (!inOrbitRing && !inUndockGrace)
            {
                if (HasLockedOrbitRadius(currentOrbitPlanet))
                    ClearLockedOrbitRadius();
                return;
            }

            float centerWorld = currentOrbitPlanet.PlanetSize * currentOrbitPlanet.GetOrbitRingCenterRadiusLocal();
            float guidanceRadius = centerWorld;
            Vector3 radial = toShip / dist;

            float targetSpeed = GetOrbitTargetSpeed(currentOrbitPlanet, guidanceRadius, innerWorld, outerWorld);
            Vector3 tangent = new Vector3(radial.z, 0f, -radial.x);

            Vector3 radialCorrection = Vector3.zero;
            if (!inUndockGrace && inOrbitRing)
            {
                float radiusError = dist - guidanceRadius;
                if (Mathf.Abs(radiusError) > 0.02f)
                    radialCorrection -= radial * radiusError * orbitRadiusPullStrength;
            }

            float graceRemainingForBlend = graceRemaining;
            bool inUndockGraceForBlend = inUndockGrace;

            Vector3 orbitTangentVelocity = tangent * targetSpeed + radialCorrection;

            // Do not stack full orbit speed + extra outward (felt like a huge launch). Blend from radial exit off the moon into orbit tangent.
            Vector3 desiredOrbitVelocity;
            float transitionDur = Mathf.Max(0.05f, gemMoonTransitionDurationSeconds);
            if (inUndockGraceForBlend && transitionDur > 0.001f)
            {
                float w = Mathf.Clamp01(graceRemainingForBlend / transitionDur); // 1 = start of grace, 0 = end
                Vector3 flat = ToroidalMap.ShortestWorldOffsetXZ(gemMoonUndockCachedMoonPos, rb.position);
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
            if (inUndockGraceForBlend && transitionDur > 0.001f)
            {
                float fade = Mathf.Clamp01(graceRemainingForBlend / transitionDur);
                float ease = Mathf.Lerp(gemMoonUndockOrbitCaptureEase, 1f, 1f - fade);
                alignRate *= ease;
            }
            float t = Mathf.Clamp01(alignRate * Time.fixedDeltaTime);

            Vector3 blendedVelocity = Vector3.Lerp(currentVel, desiredOrbitVelocity, t);
            blendedVelocity.y = 0f;

            currentVelocity = blendedVelocity;
            rb.linearVelocity = blendedVelocity;

            TryLockOrbitRadiusWhenStable();
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
            float centerWorld = planet.PlanetSize * planet.GetOrbitRingCenterRadiusLocal();
            float halfBand = Mathf.Max(0.001f, (outerWorld - innerWorld) * 0.5f);
            float radiusFactor = 1f - Mathf.Abs(clampedRadius - centerWorld) / halfBand;
            radiusFactor = Mathf.Clamp01(radiusFactor);

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
            float centerWorld = planet.PlanetSize * planet.GetOrbitRingCenterRadiusLocal();
            float halfBand = Mathf.Max(0.001f, (outerWorld - innerWorld) * 0.5f);
            float radiusFactor = 1f - Mathf.Abs(clampedRadius - centerWorld) / halfBand;
            radiusFactor = Mathf.Clamp01(radiusFactor);

            const float minSize = 9f;
            const float maxSize = 18f;
            float sizeNorm = Mathf.Clamp01((planet.PlanetSize - minSize) / (maxSize - minSize));

            // Base 1x, up to roughly ~2.7x for large planets and inner orbits.
            float gravityFactor = 1f + 0.7f * sizeNorm + 1.0f * radiusFactor;
            return gravityFactor;
        }

        /// <summary>True when in orbit zone and velocity is aligned with orbital path and speed is close to target (i.e. "true orbit" for UI).</summary>
        private bool IsInStableOrbit() => EvaluateStableOrbit();

        /// <summary>Stable-orbit check used before radius lock.</summary>
        private bool IsInStableOrbitForRadiusCapture() => EvaluateStableOrbit();

        private bool EvaluateStableOrbit()
        {
            if (currentOrbitPlanet == null || rb == null) return false;

            Vector3 planetPos = currentOrbitPlanet.GetOrbitGameplayCenterWorld();
            Vector3 shipWorld = GetShipWorldPositionForOrbitChecks();
            float dist = ToroidalMap.ToroidalDistance(shipWorld, planetPos);
            float innerWorld = currentOrbitPlanet.PlanetSize * currentOrbitPlanet.GetOrbitRingInnerRadiusLocal();
            float outerWorld = currentOrbitPlanet.PlanetSize * currentOrbitPlanet.GetOrbitRingOuterRadiusLocal();
            if (dist < innerWorld || dist > outerWorld) return false;

            float speedRadius = currentOrbitPlanet.PlanetSize * currentOrbitPlanet.GetOrbitRingCenterRadiusLocal();

            Vector3 toShip = ToroidalMap.ShortestWorldOffsetXZ(planetPos, shipWorld);
            Vector3 radial = toShip / dist;
            Vector3 tangent = new Vector3(radial.z, 0f, -radial.x);
            float targetSpeed = GetOrbitTargetSpeed(currentOrbitPlanet, speedRadius, innerWorld, outerWorld);
            if (targetSpeed < 0.001f) return false;

            Vector3 vel = GetPlanarVelocityForOrbitStableCheck();
            float speed = vel.magnitude;
            if (speed < 0.001f) return false;

            float alignment = Vector3.Dot(vel.normalized, tangent);
            float speedRatio = speed / targetSpeed;
            return alignment >= 0.92f && speedRatio >= 0.7f && speedRatio <= 1.35f;
        }

        /// <summary>Locks orbit radius at resting stable orbit (not on first ring entry).</summary>
        private void TryLockOrbitRadiusWhenStable()
        {
            if (currentOrbitPlanet == null || rb == null) return;
            if (HasLockedOrbitRadius(currentOrbitPlanet)) return;
            if (IsInStableOrbitForRadiusCapture())
                CaptureOrbitRadius(currentOrbitPlanet);
        }

        /// <summary>AI orbit helper: center of the planet orbit ring band.</summary>
        public float GetOrbitGuidanceRadiusForAI(Planet planet, float currentDist, float innerWorld, float outerWorld)
        {
            if (planet == null)
                return Mathf.Clamp(currentDist, innerWorld, outerWorld);
            return planet.PlanetSize * planet.GetOrbitRingCenterRadiusLocal();
        }

        public Vector3 GetPlanarVelocityForServerGameplayChecks()
        {
            if (rb == null) return Vector3.zero;
            Vector3 v = rb.linearVelocity;
            v.y = 0f;
            return v;
        }

        /// <summary>
        /// Planar velocity for orbit-stability checks. Server uses pose deltas for player ships (owner simulates physics).
        /// </summary>
        private Vector3 GetPlanarVelocityForOrbitStableCheck() => GetPlanarVelocityForServerGameplayChecks();

        /// <summary>True when the ship lies in the planet's orbit ring (same math as <see cref="RefreshOrbitPlanetFromPosition"/>).</summary>
        private static bool IsWorldPositionInPlanetOrbitShell(Planet planet, Vector3 shipWorldPos)
        {
            return planet != null && planet.IsWorldPositionInOrbitRing(shipWorldPos);
        }

        private static bool IsShipInCachedPlanetOrbitShell(Planet planet, Vector3 transformWorld, Vector3 rigidbodyWorld)
        {
            if (planet == null) return false;
            return IsWorldPositionInPlanetOrbitShell(planet, transformWorld)
                || IsWorldPositionInPlanetOrbitShell(planet, rigidbodyWorld);
        }

        /// <summary>Expanded orbit ring for server-side checks when owner-replicated pose jitters slightly outside the strict band.</summary>
        private static bool IsWorldPositionInPlanetOrbitShellRelaxed(Planet planet, Vector3 shipWorldPos, float margin = 0.1f)
        {
            return planet != null && planet.IsWorldPositionInOrbitRingRelaxed(shipWorldPos, margin);
        }

        private static bool IsShipInCachedPlanetOrbitShellRelaxed(Planet planet, Vector3 transformWorld, Vector3 rigidbodyWorld)
        {
            if (planet == null) return false;
            return IsWorldPositionInPlanetOrbitShellRelaxed(planet, transformWorld)
                || IsWorldPositionInPlanetOrbitShellRelaxed(planet, rigidbodyWorld);
        }

        private Vector3 GetShipWorldPositionForOrbitChecks()
        {
            Vector3 p = rb != null ? rb.position : transform.position;
            p.y = 0f;
            return p;
        }

        /// <summary>True when the ship is not actively thrusting (player RMB). Orbit transfer requires coasting in captured orbit.</summary>
        private bool IsShipIdleForPeopleTransfer()
        {
            return !IsMoveForwardPressedForGemMoonLanding;
        }

        /// <summary>
        /// Each fixed tick while in orbit without thrusting. Server sets <see cref="currentOrbitPlanet"/> from the orbit shell each tick.
        /// The 1s dwell timer is the stable-orbit requirement.
        /// </summary>
        private bool CanAccumulatePeopleTransferDwell()
        {
            if (currentOrbitPlanet == null || rb == null)
                return false;
            if (!IsShipInCachedPlanetOrbitShellRelaxed(currentOrbitPlanet, transform.position, rb.position))
                return false;
            return IsShipIdleForPeopleTransfer();
        }

        /// <summary>Players load surplus people automatically while coasting in the planet orbit ring.</summary>
        private bool ShouldLoadPeopleFromOrbitPlanet()
        {
            if (!CanLoadPeopleFromPlanetOrbitRing()) return false;
            return true;
        }

        /// <summary>People load onto the ship only in the planet orbit ring — not while in the gem moon dock/orbit shell.</summary>
        public bool IsInGemMoonOrbitBlockingPeopleLoadToShip()
        {
            if (gemMoonDocked.Value) return true;
            if (currentOrbitPlanet == null) return false;
            PlanetGemMoon moon = currentOrbitPlanet.GemMoon;
            if (moon == null) return false;
            return moon.IsShipInMoonDockZoneToroidal(this, 1f);
        }

        private bool CanLoadPeopleFromPlanetOrbitRing() => !IsInGemMoonOrbitBlockingPeopleLoadToShip();

        /// <summary>Players unload people automatically in hostile/neutral orbit while coasting.</summary>
        private bool ShouldUnloadPeopleToNeutralOrEnemyPlanet() => true;

        private void ClearPeopleTransferIntentIfComplete(Planet orbitPlanet, bool friendly, bool planetWantsReinforce)
        {
            if (!IsServer) return;
            if (wantToLoadPeople.Value && CurrentPeople >= PeopleCapacity - 0.0001f)
                wantToLoadPeople.Value = false;
            if (wantToUnloadPeople.Value && CurrentPeople <= 0.0001f)
                wantToUnloadPeople.Value = false;
            if (friendly && !planetWantsReinforce && orbitPlanet != null)
            {
                float surplus = Mathf.Max(0f, orbitPlanet.CurrentPopulation - 0.5f * orbitPlanet.MaxPopulation);
                if (wantToLoadPeople.Value && surplus < 0.0001f && peopleInTransit < 0.0001f)
                    wantToLoadPeople.Value = false;
            }
        }

        private void HandleRotation()
        {
            if (IsBulletElectricShockDisabled)
                return;

            EnsureCachedCameraControllerForShake();
            if (s_cachedCameraController != null && s_cachedCameraController.IsTheatricalShipRotationLocked)
            {
                if (rb != null)
                    rb.angularVelocity = Vector3.zero;
                return;
            }

            // EffectiveRotationSpeed is °/s (family definition units are converted there via ShipTurnDefinitionToDegreesPerSecond).
            // Always rotate toward mouse cursor - works in place, no movement required
            if (TryGetMouseAimRotation(out Quaternion targetRotation))
            {
                Quaternion newRotation = Quaternion.RotateTowards(
                    rb.rotation,
                    targetRotation,
                    EffectiveRotationSpeed * Time.fixedDeltaTime
                );
                rb.MoveRotation(newRotation);
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
            if (!IsServer)
                return;

            bool debugBoost = GameManager.Instance != null && GameManager.Instance.DebugMode;

            if (currentEnergy.Value < EffectiveEnergyCapacity)
            {
                float regen = EffectiveEnergyRegen * Time.deltaTime;
                if (debugBoost)
                    regen *= 100f;
                currentEnergy.Value = Mathf.Min(currentEnergy.Value + regen, EffectiveEnergyCapacity);
            }
        }

        private double GetServerTimeNowSeconds()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null)
                return nm.ServerTime.Time;
            return Time.timeAsDouble;
        }

        private void InitializeClientRegenDisplayBaselines()
        {
            if (!IsClient || IsServer)
                return;

            double now = GetServerTimeNowSeconds();
            _energyRegenBaseline = currentEnergy.Value;
            _energyRegenBaselineServerTime = now;
            _healthRegenBaseline = currentHealth.Value;
            _healthRegenBaselineServerTime = now;
            _healthRegenDelayUntilServerTime = 0;
        }

        private static float GetStatDisplaySyncDeadband(float capacity)
        {
            return Mathf.Max(StatDisplaySyncDeadband, capacity * 0.02f);
        }

        private void OnCurrentEnergyDisplaySync(float previous, float current)
        {
            if (IsServer)
                return;

            double now = GetServerTimeNowSeconds();
            if (current < previous - StatDisplaySpendSnapThreshold)
            {
                _energyRegenBaseline = current;
                _energyRegenBaselineServerTime = now;
                return;
            }

            float predicted = ComputeDisplayedEnergy(now);
            if (Mathf.Abs(predicted - current) <= GetStatDisplaySyncDeadband(EnergyCapacity))
                return;

            _energyRegenBaseline = current;
            _energyRegenBaselineServerTime = now;
        }

        private void OnCurrentHealthDisplaySync(float previous, float current)
        {
            if (IsServer)
                return;

            double now = GetServerTimeNowSeconds();
            if (current < previous - StatDisplaySpendSnapThreshold)
            {
                _healthRegenBaseline = current;
                _healthRegenBaselineServerTime = now;
                _healthRegenDelayUntilServerTime = now + healthRegenDelayAfterDamage;
                return;
            }

            float predicted = ComputeDisplayedHealth(now);
            if (Mathf.Abs(predicted - current) <= GetStatDisplaySyncDeadband(MaxHealth))
                return;

            _healthRegenBaseline = current;
            _healthRegenBaselineServerTime = now;
        }

        private float ComputeDisplayedEnergy(double now)
        {
            float cap = EnergyCapacity;
            if (_energyRegenBaseline >= cap - 0.001f)
                return cap;

            float regenPerSec = EffectiveEnergyRegen;
            if (GameManager.Instance != null && GameManager.Instance.DebugMode)
                regenPerSec *= 100f;

            float elapsed = (float)(now - _energyRegenBaselineServerTime);
            float predicted = Mathf.Clamp(_energyRegenBaseline + regenPerSec * elapsed, 0f, cap);
            return ApplyStatDisplayDeadband(predicted, currentEnergy.Value, cap);
        }

        private float ComputeDisplayedHealth(double now)
        {
            float cap = MaxHealth;
            if (_healthRegenBaseline >= cap - 0.001f)
                return cap;

            if (now < _healthRegenDelayUntilServerTime)
                return Mathf.Clamp(_healthRegenBaseline, 0f, cap);

            float regenPerSec = EffectiveHealthRegen;
            if (GameManager.Instance != null && GameManager.Instance.DebugMode)
                regenPerSec *= 100f;

            double regenStart = System.Math.Max(_healthRegenBaselineServerTime, _healthRegenDelayUntilServerTime);
            float elapsed = (float)(now - regenStart);
            float predicted = Mathf.Clamp(_healthRegenBaseline + regenPerSec * elapsed, 0f, cap);
            return ApplyStatDisplayDeadband(predicted, currentHealth.Value, cap);
        }

        /// <summary>Keep extrapolated display smooth when server is only slightly behind (or ahead).</summary>
        private static float ApplyStatDisplayDeadband(float predicted, float authoritative, float capacity)
        {
            float deadband = GetStatDisplaySyncDeadband(capacity);
            if (Mathf.Abs(predicted - authoritative) <= deadband)
                return predicted;
            return Mathf.Clamp(authoritative, 0f, capacity);
        }

        private bool CanFire()
        {
            if (isDead.Value) return false;
            if (IsBulletElectricShockDisabled) return false;
            if (IsAwaitingTeamSelection) return false;
            if (gemMoonDocked.Value) return false;
            return CanAnyCannonFire();
        }

        private bool IsWeaponFiringBlockedByWorldRules(Vector3 shipPosition)
        {
            if (gemMoonDocked.Value) return true;
            return ServerBlocksOrbitZoneWeaponFire(shipPosition);
        }

        [ServerRpc]
        private void SetWantToFireServerRpc(bool value)
        {
            wantToFire.Value = value;
        }

        /// <summary>
        /// Hold-fire tick: volleys all rate-ready weapons when the pool can afford a full burst; otherwise
        /// fires one weapon per step in round-robin order. Server spawns authoritative bullets; dedicated
        /// owner client mirrors energy/cursor locally and spawns predicted tracers.
        /// </summary>
        private void TickPlayerHoldWeaponFiring(bool authoritative)
        {
            if (authoritative)
            {
                if (!IsServer || !wantToFire.Value) return;
            }
            else if (!ownerFiringSessionActive)
            {
                return;
            }

            if (isDead.Value || IsBulletElectricShockDisabled || IsAwaitingTeamSelection)
            {
                if (authoritative && wantToFire.Value)
                    wantToFire.Value = false;
                return;
            }
            if (bulletConfig == null || bulletConfig.cannons == null || bulletConfig.cannons.Count == 0) return;

            Vector3 shipPos = GetSimPosition();
            if (IsWeaponFiringBlockedByWorldRules(shipPos))
            {
                if (authoritative && wantToFire.Value)
                    wantToFire.Value = false;
                return;
            }

            Vector3 shipFwd = GetSimRotation() * Vector3.forward;
            shipFwd.y = 0f;
            if (shipFwd.sqrMagnitude < 0.01f) shipFwd = Vector3.forward;
            else shipFwd.Normalize();

            Vector3 shipVel = GetSimVelocity();
            shipVel.y = 0f;

            Vector3[] aimOrigins = null;
            Vector3[] aimForwards = null;
            TryFireNextWeaponShot(authoritative, shipPos, shipFwd, shipVel, aimOrigins, aimForwards);
        }

        /// <summary>Fires the next volley or sequential weapon(s) (pool + per-cannon rate + energy). Returns true if any shot was taken.</summary>
        private bool TryFireNextWeaponShot(
            bool authoritative,
            Vector3 shipPosition,
            Vector3 shipForward,
            Vector3 shipVelocity,
            Vector3[] ownerReportedCannonOrigins,
            Vector3[] ownerReportedCannonForwards)
        {
            if (!TryCollectCannonsToFire(_cannonsToFireScratch, requireEnergy: true))
                return false;

            CombatSystem combat = CombatSystem.Instance;
            if (combat == null)
                combat = UnityEngine.Object.FindFirstObjectByType<CombatSystem>(FindObjectsInactive.Include);
            if (combat == null) return false;

            EnsureBulletLastFireTime();
            bool useOnlyOwnerBallistics = false;
            var bulletIndicesFired = new System.Collections.Generic.List<byte>();
            var bulletPrefabIndicesFired = new System.Collections.Generic.List<int>();

            for (int cIdx = 0; cIdx < _cannonsToFireScratch.Count; cIdx++)
            {
                int i = _cannonsToFireScratch[cIdx];
                var c = bulletConfig.cannons[i];
                if (!IsValidWeaponFirePointIndex(i))
                    continue;

                int bulletIdx = ResolveBulletBankIndexForCannon(c, combat);
                ResolveEffectiveCannonStats(c, bulletIdx, out float damage, out float speed, out _, out float energyCostPerShot);

                bool useOwnerPose = useOnlyOwnerBallistics && HasOwnerReportedCannonPose(ownerReportedCannonOrigins, ownerReportedCannonForwards, i);
                Vector3 fireOrigin;
                Vector3 cannonFwd;
                if (useOwnerPose)
                {
                    fireOrigin = ownerReportedCannonOrigins[i];
                    cannonFwd = ownerReportedCannonForwards[i];
                }
                else if (!TryResolveCannonFirePose(i, shipForward, out fireOrigin, out cannonFwd))
                    continue;

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

                    float scale = c.bulletScale * BulletScaleMultiplier;
                    if (authoritative)
                    {
                        if (!combat.TrySpawnBulletOnServer(fireOrigin, dir, speed, damage, shipTeam.Value, NetworkObjectId, scale, 0, shipVelocity, bulletIdx))
                            continue;
                        spawnedAnyForThisCannon = true;
                        if (IsOwner && !IsServer && ShouldSpawnOwnerPredictedBulletTracers())
                        {
                            BulletSpawnPayload payload = combat.BuildBulletTracerPayloadForClientPreview(
                                fireOrigin, dir, speed, damage, shipTeam.Value, NetworkObjectId, scale, 0, shipVelocity, bulletIdx);
                            ClientBulletTracer.SpawnOwnerPredicted(payload);
                        }
                        if (rb != null)
                        {
                            float recoilImpulse = recoilStrength * scale * (0.08f + damage / 400f);
                            ShipMotorSimulator.ApplyVelocityImpulse(ref _motorState, -dir * (recoilImpulse / Mathf.Max(0.5f, _motorState.Mass)));
                        }
                    }
                    else
                    {
                        BulletSpawnPayload payload = combat.BuildBulletTracerPayloadForClientPreview(
                            fireOrigin, dir, speed, damage, shipTeam.Value, NetworkObjectId, scale, 0, shipVelocity, bulletIdx);
                        ClientBulletTracer.SpawnOwnerPredicted(payload);
                        ClientBulletTracer.MarkOwnerPredictedFireForCannon(i);
                        spawnedAnyForThisCannon = true;
                    }
                }

                if (!spawnedAnyForThisCannon)
                    continue;

                if (!TryConsumeFiringPoolEnergy(i, energyCostPerShot))
                    return false;

                if (i < bulletLastFireTime.Length)
                    bulletLastFireTime[i] = Time.fixedTime;

                if (!authoritative && IsOwner && !IsServer && AudioManager.Instance != null)
                    AudioManager.Instance.PlayWeaponShootSound(GetWeaponSoundPitchForCannon(i));

                bulletIndicesFired.Add((byte)i);
                bulletPrefabIndicesFired.Add(bulletIdx);
            }

            if (authoritative && bulletIndicesFired.Count > 0)
            {
                FireClientRpc(
                    bulletIndicesFired.ToArray(),
                    bulletPrefabIndicesFired.ToArray());
            }

            return bulletIndicesFired.Count > 0;
        }

        [ServerRpc]
        private void CycleBulletPrefabServerRpc()
        {
            if (IsAwaitingTeamSelection) return;
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
        /// Builds per-cannon world origins and aim axes from this client's weapon component transforms (same sizing rules as <see cref="FireServerRpc"/>).
        /// Always allocates arrays when cannons exist so <see cref="FireServerRpc"/> can fall back per-cannon to server muzzles when a slot is invalid.
        /// </summary>
        private bool TryBuildOwnerReportedCannonBallisticsForFireRpc(out Vector3[] origins, out Vector3[] forwards)
        {
            origins = null;
            forwards = null;
            if (bulletConfig == null || bulletConfig.cannons == null || bulletConfig.cannons.Count == 0)
                return false;

            Vector3 shipFwd = transform.forward;
            shipFwd.y = 0f;
            if (shipFwd.sqrMagnitude < 0.01f) shipFwd = Vector3.forward;
            else shipFwd.Normalize();

            int cannonCount = bulletConfig.cannons.Count;
            origins = new Vector3[cannonCount];
            forwards = new Vector3[cannonCount];
            bool anyValid = false;
            for (int i = 0; i < cannonCount; i++)
            {
                if (!TryResolveCannonFirePose(i, shipFwd, out Vector3 origin, out Vector3 forward))
                    continue;
                origins[i] = origin;
                forwards[i] = forward;
                anyValid = true;
            }
            return anyValid;
        }

        /// <summary>Resolves muzzle origin/forward for cannon <paramref name="index"/> from weapon transforms.</summary>
        private bool TryResolveCannonFirePose(
            int index,
            Vector3 shipForward,
            out Vector3 fireOrigin,
            out Vector3 cannonForward)
        {
            fireOrigin = Vector3.zero;
            cannonForward = shipForward;
            if (!IsValidWeaponFirePointIndex(index))
                return false;

            SyncTransformForGameplayQuery();

            Transform firePt = bulletFirePoints[index];
            fireOrigin = firePt.position;

            Quaternion hullRot = GetSimRotation();
            Vector3 localBarrel = Quaternion.Inverse(hullRot) * firePt.forward;
            localBarrel.y = 0f;
            if (localBarrel.sqrMagnitude < 0.0001f)
            {
                cannonForward = shipForward;
            }
            else
            {
                cannonForward = hullRot * localBarrel.normalized;
                cannonForward.y = 0f;
                if (cannonForward.sqrMagnitude < 0.01f)
                    cannonForward = shipForward;
                else
                    cannonForward.Normalize();
            }

            cannonForward.y = 0f;
            if (cannonForward.sqrMagnitude < 0.01f)
            {
                cannonForward = shipForward;
                cannonForward.y = 0f;
            }
            if (cannonForward.sqrMagnitude < 0.01f)
                cannonForward = Vector3.forward;
            else
                cannonForward.Normalize();
            return true;
        }

        private static bool HasOwnerReportedCannonPose(Vector3[] origins, Vector3[] forwards, int index)
        {
            if (origins == null || forwards == null) return false;
            if (index < 0 || index >= origins.Length || index >= forwards.Length) return false;
            return forwards[index].sqrMagnitude > 0.0001f;
        }

        private bool TryResolveMuzzleVfxPose(int cannonIndex, out Vector3 position, out Vector3 forward)
        {
            Vector3 shipFwd = transform.forward;
            shipFwd.y = 0f;
            if (shipFwd.sqrMagnitude < 0.01f) shipFwd = Vector3.forward;
            else shipFwd.Normalize();
            if (!TryResolveCannonFirePose(cannonIndex, shipFwd, out position, out forward))
                return false;
            if (forward.sqrMagnitude < 0.01f)
            {
                forward = shipFwd;
                forward.y = 0f;
            }
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            else forward.Normalize();
            return true;
        }

        [ClientRpc]
        private void FireClientRpc(byte[] bulletIndicesFired, int[] bulletPrefabIndices)
        {
            bool dedicatedOwner = IsOwner && !IsServer;
            if (bulletIndicesFired != null)
            {
                for (int j = 0; j < bulletIndicesFired.Length; j++)
                {
                    int idx = bulletIndicesFired[j];
                    if (dedicatedOwner && ClientBulletTracer.WasOwnerPredictedRecentlyForCannon(idx))
                        continue;

                    bool usedSciFiMuzzle = false;
                    // Same as Bullet visuals: avoid instantiating Sci-Fi AllIn1 muzzle prefabs on mobile (shader / stability).
                    if (!Application.isMobilePlatform && bulletPrefabIndices != null && j < bulletPrefabIndices.Length && CombatSystem.Instance != null)
                    {
                        GameObject bulletPrefab = CombatSystem.Instance.GetBulletPrefabFromBank(bulletPrefabIndices[j], shipTeam.Value);
                        var sciFi = bulletPrefab != null ? bulletPrefab.GetComponent<SciFiProjectileScript>() : null;
                        if (sciFi != null && sciFi.muzzleParticle != null && bulletFirePoints != null && idx >= 0 && idx < bulletFirePoints.Count && bulletFirePoints[idx] != null)
                        {
                            Vector3 pos;
                            Vector3 fwd;
                            if (!TryResolveMuzzleVfxPose(idx, out pos, out fwd))
                            {
                                Transform pt = bulletFirePoints[idx];
                                pos = pt.position;
                                fwd = pt.forward;
                            }
                            if (fwd.sqrMagnitude < 0.01f) fwd = -transform.forward;
                            GameObject muzzle = Instantiate(sciFi.muzzleParticle, pos, Quaternion.LookRotation(-fwd));
                            if (muzzle != null)
                            {
                                float cannonScale = 1f;
                                if (bulletConfig != null && idx >= 0 && idx < bulletConfig.cannons.Count)
                                    cannonScale = bulletConfig.cannons[idx].bulletScale * BulletScaleMultiplier;
                                VfxUrpCompat.ApplyImpactVisualScale(muzzle, BulletVisualFactory.GetBulletVisualScale(cannonScale));
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
                        Vector3 muzzlePos;
                        Vector3 fwd;
                        if (!TryResolveMuzzleVfxPose(idx, out muzzlePos, out fwd))
                        {
                            Transform pt = bulletFirePoints[idx];
                            muzzlePos = pt.position;
                            fwd = pt.forward;
                            fwd.y = 0f;
                            if (fwd.sqrMagnitude < 0.01f)
                            {
                                fwd = transform.forward;
                                fwd.y = 0f;
                            }
                            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                            else fwd.Normalize();
                        }
                        Color flashColor = TeamManager.Instance != null
                            ? TeamManager.GetTeamColor(shipTeam.Value)
                            : new Color(1f, 0.88f, 0.45f);
                        float cannonScale = 1f;
                        if (bulletConfig != null && idx >= 0 && idx < bulletConfig.cannons.Count)
                            cannonScale = bulletConfig.cannons[idx].bulletScale * BulletScaleMultiplier;
                        VfxUrpCompat.SpawnMobileMuzzleFlash(
                            muzzlePos,
                            fwd,
                            flashColor,
                            BulletVisualFactory.GetBulletVisualScale(cannonScale));
                    }
                }
            }
            if (bulletIndicesFired != null && bulletIndicesFired.Length > 0 && AudioManager.Instance != null)
            {
                for (int j = 0; j < bulletIndicesFired.Length; j++)
                {
                    int idx = bulletIndicesFired[j];
                    if (dedicatedOwner && ClientBulletTracer.WasOwnerPredictedRecentlyForCannon(idx))
                        continue;
                    float pitch = GetWeaponSoundPitchForCannon(idx);
                    AudioManager.Instance.PlayWeaponShootSound(pitch);
                }
            }

            if (!dedicatedOwner && IsOwner && bulletIndicesFired != null && bulletIndicesFired.Length > 0)
            {
                EnsureBulletLastFireTime();
                float t = Time.fixedTime;
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
            if (IsBulletElectricShockDisabled) return;
            if (gemMoonDocked.Value) return;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f) direction = transform.forward;
            else direction.Normalize();
            Vector3 serverVel = rb != null ? rb.linearVelocity : Vector3.zero;
            serverVel.y = 0f;
            Vector3 shipPos = rb != null ? rb.position : transform.position;
            if (IsWeaponFiringBlockedByWorldRules(shipPos)) return;
            TryFireNextWeaponShot(true, shipPos, direction, serverVel, null, null);
        }

        [ServerRpc]
        private void FireRocketServerRpc(bool preferLarge)
        {
            if (IsAwaitingTeamSelection) return;
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
            if (IsAwaitingTeamSelection) return;
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

        private const int VoluntaryGemExpulsionPerShipLevel = 3;
        private const float VoluntaryGemExpulsionIntervalSeconds = 0.5f; // 2 shots/sec

        [ServerRpc]
        private void SetWantToExpelGemsServerRpc(bool value)
        {
            wantToExpelGems.Value = value;
            if (!value)
                voluntaryGemExpulsionShotIndex = 0;
        }

        /// <summary>Server: while V is held, fire one gem every 0.5s worth 3 × ship level.</summary>
        private void TickVoluntaryGemExpulsion()
        {
            if (!wantToExpelGems.Value || isDead.Value || currentGems.Value <= 0.001f)
            {
                if (wantToExpelGems.Value && (isDead.Value || currentGems.Value <= 0.001f))
                    wantToExpelGems.Value = false;
                return;
            }

            if (GemSpawner.Instance == null) return;

            float shotValue = VoluntaryGemExpulsionPerShipLevel * Mathf.Max(1, ShipLevel);
            float now = (float)NetworkManager.Singleton.ServerTime.Time;
            if (now - lastVoluntaryGemExpulsionServerTime < VoluntaryGemExpulsionIntervalSeconds)
                return;

            float gemsToExpel = currentGems.Value >= shotValue ? shotValue : currentGems.Value;
            if (gemsToExpel <= 0.001f)
            {
                wantToExpelGems.Value = false;
                return;
            }

            lastVoluntaryGemExpulsionServerTime = now;
            currentGems.Value = Mathf.Max(0f, currentGems.Value - gemsToExpel);

            Vector3 direction = transform.forward;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f) direction = Vector3.forward;
            else direction.Normalize();

            Vector3 shipVelocity = rb != null ? rb.linearVelocity : Vector3.zero;
            shipVelocity.y = 0f;
            Vector3 expelPos = GetGameplayShipCenterWorld();

            GemSpawner.Instance.SpawnVoluntaryGemFromShipOnServer(
                expelPos,
                direction,
                gemsToExpel,
                ShipLevel,
                NetworkObjectId,
                shipVelocity,
                voluntaryGemExpulsionShotIndex);
            voluntaryGemExpulsionShotIndex++;

            if (currentGems.Value <= 0.001f)
                wantToExpelGems.Value = false;
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
        public void TakeDamageServerRpc(
            float damage,
            TeamManager.Team attackerTeam,
            ulong attackerShipNetworkId = 0,
            float gemExpulsionIntensity = 0.5f,
            float gemExpulsionPerHullDamage = 0f)
        {
            ApplyDamageOnServer(damage, attackerTeam, attackerShipNetworkId, gemExpulsionIntensity, gemExpulsionPerHullDamage);
        }

        /// <summary>Server-only bullet damage (avoids ServerRpc when invoked from authoritative bullet sim).</summary>
        public void ApplyDamageFromBulletServer(
            float damage,
            TeamManager.Team attackerTeam,
            ulong attackerShipNetworkId = 0,
            float gemExpulsionIntensity = 0.5f,
            float gemExpulsionPerHullDamage = 0f)
        {
            if (!IsServer) return;
            ApplyDamageOnServer(damage, attackerTeam, attackerShipNetworkId, gemExpulsionIntensity, gemExpulsionPerHullDamage);
        }

        /// <summary>Server: apply hull damage and gem expulsion. Bullets use legacy 50% rules after hull is 0.
        /// Ram/grind pass <paramref name="gemExpulsionPerHullDamage"/> for 1:1 gem value on excess/post-zero damage only.</summary>
        private void ApplyDamageOnServer(
            float damage,
            TeamManager.Team attackerTeam,
            ulong attackerShipNetworkId,
            float gemExpulsionIntensity,
            float gemExpulsionPerHullDamage)
        {
            if (!IsServer) return;
            // Block friendly fire only when both have valid teams and they match
            if (attackerTeam != TeamManager.Team.None && attackerTeam == shipTeam.Value) return;
            if (isDead.Value) return;
            // Only immune once fully landed on the moon surface (not while approaching the dock zone).
            if (gemMoonDocked.Value && IsGemMoonSurfaceLandingComplete()) return;

            // Legacy bullet tuning: ~50% of damage as gems after hull is 0, with per-hit caps.
            const float LegacyGemExpulsionPerDamage = 0.5f;
            const float MaxLethalExpulsionFraction = 0.6f;
            const float MaxPostDeathExpulsionFraction = 0.4f;

            bool ramGrindGemExpulsion = gemExpulsionPerHullDamage > 0f;

            float healthBefore = currentHealth.Value;
            bool wasAlive = healthBefore > 0.001f;

            if (wasAlive && damage > 0.0001f)
            {
                float newHealth = Mathf.Max(0f, healthBefore - damage);
                float deltaHealth = newHealth - healthBefore;
                currentHealth.Value = newHealth;
                lastHullDamageServerTime = Time.time;

                const float minAbsHealthForPopup = 1f;
                if (Mathf.Abs(deltaHealth) >= minAbsHealthForPopup && VisualEffectsManager.Instance != null)
                    VisualEffectsManager.Instance.SpawnFloatingCountServerRpc(
                        GetGameplayShipCenterWorld(),
                        (int)FloatingCountChannel.HealthChange,
                        deltaHealth,
                        (int)attackerTeam
                    );
            }

            float gemsToExpel = 0f;
            if (currentGems.Value > 0.0001f)
            {
                if (ramGrindGemExpulsion)
                {
                    // Ram/grind: no gems until hull is 0; then 1:1 gem value with damage (excess on the breaking hit).
                    if (wasAlive)
                    {
                        float excessDamage = Mathf.Max(0f, damage - healthBefore);
                        if (excessDamage > 0f)
                            gemsToExpel = excessDamage * gemExpulsionPerHullDamage;
                    }
                    else
                        gemsToExpel = damage * gemExpulsionPerHullDamage;
                }
                else if (wasAlive)
                {
                    float excessDamage = Mathf.Max(0f, damage - healthBefore);
                    if (excessDamage > 0f)
                    {
                        float desired = excessDamage * LegacyGemExpulsionPerDamage;
                        float maxForThisHit = currentGems.Value * MaxLethalExpulsionFraction;
                        gemsToExpel = Mathf.Min(desired, maxForThisHit);
                    }
                }
                else
                {
                    float desired = damage * LegacyGemExpulsionPerDamage;
                    float maxForThisHit = currentGems.Value * MaxPostDeathExpulsionFraction;
                    gemsToExpel = Mathf.Min(desired, maxForThisHit);
                }

                gemsToExpel = Mathf.Min(gemsToExpel, currentGems.Value);
            }

            if (gemsToExpel > 0.0001f)
                ExpelGemsFromShipOnServer(gemsToExpel, gemExpulsionIntensity);

            TryDieIfHullAndGemsDepleted(attackerShipNetworkId);
        }

        /// <summary>Server: deduct carried gems and spawn one physical gem sized to this hit's expelled value.</summary>
        private void ExpelGemsFromShipOnServer(float gemsToExpel, float gemExpulsionIntensity)
        {
            if (!IsServer || gemsToExpel <= 0.0001f || currentGems.Value <= 0f) return;

            gemsToExpel = Mathf.Min(gemsToExpel, currentGems.Value);
            currentGems.Value = Mathf.Max(0f, currentGems.Value - gemsToExpel);
            SpawnDamageGemExpulsionOnServer(gemsToExpel, gemExpulsionIntensity);
        }

        private void SpawnDamageGemExpulsionOnServer(float gemValue, float gemExpulsionIntensity)
        {
            if (!IsServer || gemValue <= 0.0001f || GemSpawner.Instance == null) return;

            ulong myId = GetComponent<NetworkObject>()?.NetworkObjectId ?? 0;
            GemSpawner.Instance.SpawnGemsFromShipOnServer(
                GetGameplayShipCenterWorld(),
                gemValue,
                myId,
                gemExpulsionIntensity,
                ShipLevel);
        }

        private void HandleDeath()
        {
            if (isDead.Value) return;
            // Death is triggered in TakeDamageServerRpc when health and gems both reach 0
            // No passive gem drain - gems only reduce when bullets hit (and get expelled)
        }

        /// <summary>Server: friendly planets below 50% max population pull crew from ships until half full. At/above 50%, surplus loads are dispatched by <see cref="Planet"/> (growth-gated, round-robin). Non-friendly: unload onto neutral/enemy as invasion. People beam as projectiles. Chunk size is min(ship level, planet level); transfer rate scales with chunk size (card-scaled). Transfer only after <see cref="CanAccumulatePeopleTransferDwell"/> for <see cref="peopleTransferStationaryHoldSeconds"/>.</summary>
        /// <summary>Pick the orbit planet whose shell contains this ship (closest toroidal match). Used on server and local owner — physics triggers miss wrapped tiles.</summary>
        private void RefreshOrbitPlanetFromPosition()
        {
            if (!UsesInputSyncedMotor && rb == null)
                return;
            if (!IsServer && !IsLocalPlayerShip())
                return;

            Vector3 p0 = UsesInputSyncedMotor ? GetSimPosition() : rb.position;
            Vector3 p1 = UsesInputSyncedMotor ? GetSimPosition() : transform.position;
            Planet best = null;
            float bestDist = float.MaxValue;

            foreach (var planet in Planet.AllPlanets)
            {
                if (planet == null) continue;
                if (!IsShipInCachedPlanetOrbitShell(planet, p1, p0))
                    continue;

                float d = ToroidalMap.ToroidalDistance(p0, planet.GetOrbitGameplayCenterWorld());
                if (d < bestDist)
                {
                    bestDist = d;
                    best = planet;
                }
            }

            Planet previous = currentOrbitPlanet;
            currentOrbitPlanet = best;
            if (best == null)
                ClearLockedOrbitRadius();
            else if (best != previous)
                ClearLockedOrbitRadius();
        }

        private void CaptureOrbitRadius(Planet planet)
        {
            if (planet == null)
            {
                ClearLockedOrbitRadius();
                return;
            }

            lockedOrbitRadiusWorld = planet.PlanetSize * planet.GetOrbitRingCenterRadiusLocal();
            lockedOrbitRadiusPlanet = planet;
        }

        private void ClearLockedOrbitRadius()
        {
            lockedOrbitRadiusWorld = -1f;
            lockedOrbitRadiusPlanet = null;
        }

        /// <summary>AI orbit helper: call each fixed tick while orbiting.</summary>
        public void TryLockOrbitRadiusWhenStableFromAI() => TryLockOrbitRadiusWhenStable();

        public bool HasLockedOrbitRadiusForAI(Planet planet) => HasLockedOrbitRadius(planet);

        private void TickOrbitPopulationTransfer()
        {
            ulong orbitPlanetId = 0;
            if (currentOrbitPlanet != null)
            {
                var orbitNetObj = currentOrbitPlanet.GetComponent<NetworkObject>();
                if (orbitNetObj != null)
                    orbitPlanetId = orbitNetObj.NetworkObjectId;
            }

            if (orbitPlanetId != peopleTransferOrbitPlanetId)
            {
                peopleUnloadAccumulator = 0f;
                peopleTransferOrbitPlanetId = orbitPlanetId;
            }

            if (currentOrbitPlanet == null)
            {
                peopleUnloadAccumulator = 0f;
                peopleTransferStationaryTimer = 0f;
                return;
            }

            if (!CanAccumulatePeopleTransferDwell())
            {
                peopleTransferStationaryTimer = 0f;
                peopleUnloadAccumulator = 0f;
                return;
            }

            peopleTransferStationaryTimer += Time.fixedDeltaTime;
            if (peopleTransferStationaryTimer < peopleTransferStationaryHoldSeconds)
                return;

            float peopleSpaceAvailable = PeopleCapacity - currentPeople.Value - peopleInTransit;
            bool debugModeEnabled = GameManager.Instance != null && GameManager.Instance.DebugMode;

            float peopleDropValue = GetPeopleTransferChunkSize(currentOrbitPlanet);
            float peopleTransferStep = peopleDropValue * Time.fixedDeltaTime * GetCardPeopleTransferSpeedMultiplier();
            float loadRate = peopleTransferStep;
            float unloadAccumStep = peopleTransferStep;
            if (debugModeEnabled)
            {
                loadRate *= 100f;
                unloadAccumStep *= 100f;
            }

            if (loadRate <= 0f && unloadAccumStep <= 0f) return;

            bool friendly = (currentOrbitPlanet is HomePlanet home && home.AssignedTeam == shipTeam.Value)
                || currentOrbitPlanet.TeamOwnership == shipTeam.Value;

            if (friendly)
            {
                Planet orbitPlanet = currentOrbitPlanet;
                float halfCap = 0.5f * orbitPlanet.MaxPopulation;
                float curPop = orbitPlanet.CurrentPopulation;
                // Below 50%: planet pulls people from ships until half capacity. At/above 50%: only surplus above half can load onto ships.
                bool planetWantsReinforce = curPop < halfCap - 0.0001f;
                ClearPeopleTransferIntentIfComplete(orbitPlanet, true, planetWantsReinforce);

                if (planetWantsReinforce)
                {
                    float roomToHalf = Mathf.Max(0f, halfCap - curPop);

                    if (debugModeEnabled)
                    {
                        float instantUnload = Mathf.Min(currentPeople.Value, roomToHalf);
                        if (instantUnload > 0f)
                        {
                            RemovePeopleFromServer(instantUnload);
                            orbitPlanet.AddPopulationFromServer(instantUnload, shipTeam.Value);
                            Vector3 shipPos = rb != null ? rb.position : transform.position;
                            SpawnPeopleTransferFloatingCount(FloatingCountChannel.PeopleUnload, instantUnload, shipPos);
                            PlayPeopleUnloadSoundClientRpc(instantUnload);
                        }
                        return;
                    }

                    if (currentPeople.Value > 0.0001f && roomToHalf > 0.0001f)
                        peopleUnloadAccumulator += unloadAccumStep;

                    float roomBudget = roomToHalf;
                    if (peopleUnloadAccumulator >= peopleDropValue
                        && currentPeople.Value >= peopleDropValue
                        && roomBudget >= peopleDropValue
                        && GemSpawner.Instance != null)
                    {
                        RemovePeopleFromServer(peopleDropValue);
                        peopleUnloadAccumulator -= peopleDropValue;
                        roomBudget -= peopleDropValue;

                        Vector3 shipPos = rb != null ? rb.position : transform.position;
                        Vector3 planetPos = orbitPlanet.transform.position;
                        var planetNo = orbitPlanet.GetComponent<NetworkObject>();
                        var shipNo = GetComponent<NetworkObject>();
                        if (planetNo != null && shipNo != null)
                            GemSpawner.Instance.SpawnPeopleUnload(shipPos, planetPos, peopleDropValue, planetNo.NetworkObjectId, shipTeam.Value, shipNo.NetworkObjectId);
                    }
                    else
                    {
                        float maxReinforceRem = Mathf.Min(currentPeople.Value, roomBudget);
                        if (maxReinforceRem > 0.0001f
                            && maxReinforceRem < peopleDropValue
                            && peopleUnloadAccumulator >= maxReinforceRem - 0.0001f
                            && GemSpawner.Instance != null)
                        {
                            RemovePeopleFromServer(maxReinforceRem);
                            peopleUnloadAccumulator -= maxReinforceRem;

                            Vector3 shipPos = rb != null ? rb.position : transform.position;
                            Vector3 planetPos = orbitPlanet.transform.position;
                            var planetNo = orbitPlanet.GetComponent<NetworkObject>();
                            var shipNo = GetComponent<NetworkObject>();
                            if (planetNo != null && shipNo != null)
                                GemSpawner.Instance.SpawnPeopleUnload(shipPos, planetPos, maxReinforceRem, planetNo.NetworkObjectId, shipTeam.Value, shipNo.NetworkObjectId);
                        }
                    }
                    return;
                }

                peopleUnloadAccumulator = 0f;
                if (!ShouldLoadPeopleFromOrbitPlanet())
                    return;

                if (debugModeEnabled)
                {
                    float available = Mathf.Max(0f, curPop - halfCap);
                    float instantLoadAmount = Mathf.Min(peopleSpaceAvailable, available);
                    if (instantLoadAmount > 0f)
                    {
                        orbitPlanet.RemovePopulationFromServer(instantLoadAmount);
                        AddPeopleFromServer(instantLoadAmount);
                        Vector3 shipPos = rb != null ? rb.position : transform.position;
                        SpawnPeopleTransferFloatingCount(FloatingCountChannel.PeopleLoad, instantLoadAmount, shipPos);
                        PlayPeopleLoadSoundClientRpc(instantLoadAmount);
                    }
                    return;
                }

                // Surplus load (at/above 50% reserve) is dispatched by Planet.TickSurplusPeopleLoadToOrbitingShips:
                // at most one transport per second, round-robin; growth-gated when deployable surplus is tight.
            }
            else
            {
                ClearPeopleTransferIntentIfComplete(currentOrbitPlanet, false, false);

                if (!ShouldUnloadPeopleToNeutralOrEnemyPlanet())
                {
                    peopleUnloadAccumulator = 0f;
                    return;
                }

                float hostileUnloadChunk = GetPeopleUnloadChunkSize();
                float hostileUnloadAccumStep = hostileUnloadChunk * Time.fixedDeltaTime * GetCardPeopleTransferSpeedMultiplier();
                if (debugModeEnabled)
                    hostileUnloadAccumStep *= 100f;

                if (debugModeEnabled)
                {
                    float instantUnloadPeople = currentPeople.Value;
                    if (instantUnloadPeople > 0f)
                    {
                        RemovePeopleFromServer(instantUnloadPeople);
                        // Debug-only shortcut: each 1 unloaded person applies 100 population impact.
                        currentOrbitPlanet.AddPopulationFromServer(instantUnloadPeople * 100f, shipTeam.Value);
                        Vector3 shipPos = rb != null ? rb.position : transform.position;
                        SpawnPeopleTransferFloatingCount(FloatingCountChannel.PeopleUnload, instantUnloadPeople, shipPos);
                        PlayPeopleUnloadSoundClientRpc(instantUnloadPeople);
                    }
                    return;
                }

                if (currentPeople.Value > 0.0001f)
                    peopleUnloadAccumulator += hostileUnloadAccumStep;

                if (peopleUnloadAccumulator >= hostileUnloadChunk
                    && currentPeople.Value >= hostileUnloadChunk)
                {
                    ApplyHostileOrbitPeopleUnload(hostileUnloadChunk);
                    peopleUnloadAccumulator -= hostileUnloadChunk;
                }
                else if (currentPeople.Value > 0f
                    && currentPeople.Value < hostileUnloadChunk
                    && peopleUnloadAccumulator >= currentPeople.Value - 0.0001f)
                {
                    float remainder = currentPeople.Value;
                    ApplyHostileOrbitPeopleUnload(remainder);
                    peopleUnloadAccumulator = Mathf.Max(0f, peopleUnloadAccumulator - remainder);
                }
            }
        }

        /// <summary>Server: invasion unload — remove crew from ship and spawn projectile; planet population applies on surface delivery.</summary>
        private void ApplyHostileOrbitPeopleUnload(float chunk)
        {
            if (!IsServer || currentOrbitPlanet == null || chunk <= 0f || currentPeople.Value < chunk - 0.0001f)
                return;

            Planet targetPlanet = currentOrbitPlanet;
            RemovePeopleFromServer(chunk);

            Vector3 shipPos = rb != null ? rb.position : transform.position;
            Vector3 planetPos = targetPlanet.transform.position;
            var planetNo = targetPlanet.GetComponent<NetworkObject>();
            var shipNo = GetComponent<NetworkObject>();
            if (GemSpawner.Instance != null && planetNo != null && shipNo != null)
                GemSpawner.Instance.SpawnPeopleUnload(shipPos, planetPos, chunk, planetNo.NetworkObjectId, shipTeam.Value, shipNo.NetworkObjectId);
        }

        /// <summary>Server: credits gems straight to the planet (same as old flying deposit gems). No gem projectiles.</summary>
        private void ApplyMoonGemDepositToPlanet(Planet depositPlanet, float amount)
        {
            if (!IsServer || depositPlanet == null || amount <= 0.0001f) return;
            var team = shipTeam.Value;
            ulong clientId = GetContributedGemsClientId();
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

        /// <summary>Server: while docked at gem moon, deposits shipLevel gems per tick at 3 ticks/sec; applied directly to planet level gems.</summary>
        private void TickOrbitGemDeposit()
        {
            if (!gemMoonDocked.Value)
            {
                depositAccumulator = 0f;
                return;
            }

            if (!IsGemMoonSurfaceLandingComplete())
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
            float depositSpeedMul = Mathf.Max(0.01f, GetCardGemDepositSpeedMultiplier());
            float gemValue = Mathf.Max(1f, ShipLevel); // gems credited per deposit tick
            float rate = gemValue * GemMoonDepositChunksPerSecond * Time.fixedDeltaTime * depositSpeedMul;
            if (debugModeEnabled) rate *= 100f;
            if (rate <= 0f) return;
            float amount = Mathf.Min(rate, currentGems.Value);
            if (amount <= 0f) return;

            depositAccumulator += amount;
            float now = (float)NetworkManager.Singleton.ServerTime.Time;
            float gemInterval = 1f / (GemMoonDepositChunksPerSecond * depositSpeedMul);
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
            droneSwarm?.ServerDetachDronesAsLootOnDeath();
            ClearBulletBurnEffectsOnServer();
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
            RefillCannonEnergyFromServer();
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
            if (planet == null || rb == null) return;
            if (!TryComputeOrbitSpawnPose(planet, out Vector3 orbitPos, out Vector3 vel, out Quaternion rot)) return;
            currentOrbitPlanet = planet;
            CaptureOrbitRadius(planet);
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
            // Keep debris on the same gameplay plane as ships/asteroids, but allow tumbling on all axes.
            debRb.constraints = RigidbodyConstraints.FreezePositionY;
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
            debRb.angularVelocity = new Vector3(
                RandomSignedAngularSpeed(minAngularVel, maxAngularVel),
                RandomSignedAngularSpeed(minAngularVel, maxAngularVel),
                RandomSignedAngularSpeed(minAngularVel, maxAngularVel));
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

        private static float RandomSignedAngularSpeed(float minSpeed, float maxSpeed)
        {
            float speed = Random.Range(minSpeed, maxSpeed);
            return Random.value < 0.5f ? -speed : speed;
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

        /// <summary>Owner/AI collision path: apply on server directly when hosting; otherwise one ServerRpc (avoids per-frame RPC floods while grinding).</summary>
        private void ApplyShipRamDamage(float damage, float gemExpulsionIntensity, float gemExpulsionPerHullDamage)
        {
            if (damage <= 0.0001f) return;
            if (IsServer)
                ApplyDamageOnServer(damage, TeamManager.Team.None, 0, gemExpulsionIntensity, gemExpulsionPerHullDamage);
            else
                TakeDamageServerRpc(damage, TeamManager.Team.None, 0, gemExpulsionIntensity, gemExpulsionPerHullDamage);
        }

        /// <summary>Harder asteroid impacts → higher intensity → gems eject faster/farther.</summary>
        private float ComputeRamImpactGemExpulsionIntensity(float impactForceNewtons, float damage)
        {
            float forceT = Mathf.InverseLerp(35f, 900f, impactForceNewtons);
            float damageT = Mathf.InverseLerp(1f, 25f, damage);
            return Mathf.Clamp01(Mathf.Max(forceT, damageT) * 0.85f + 0.15f);
        }

        /// <summary>Grinding chip damage → lower intensity than impacts → more, smaller gem chunks.</summary>
        private float ComputeRamGrindGemExpulsionIntensity(float pushNewtons, float damage)
        {
            float pushT = Mathf.InverseLerp(asteroidGrindMinPushNewtons, asteroidGrindMinPushNewtons * 10f, pushNewtons);
            float damageT = Mathf.InverseLerp(0.5f, 10f, damage);
            return Mathf.Clamp01(Mathf.Max(pushT, damageT) * 0.22f + 0.04f);
        }

        private void ApplyAsteroidRamDamage(Asteroid asteroid, float damage, ulong attackerShipId)
        {
            if (asteroid == null || damage <= 0.0001f) return;
            if (IsServer)
                asteroid.ApplyDamageFromBulletServer(damage, attackerShipId);
            else
                asteroid.TakeDamageServerRpc(damage, attackerShipId);
        }

        /// <summary>Shared throttle for grind damage + feedback (same interval as <see cref="asteroidGrindFeedbackInterval"/>).</summary>
        private bool TryConsumeAsteroidGrindPulse(Asteroid asteroid)
        {
            if (asteroid == null) return false;
            int id = asteroid.GetInstanceID();
            float now = Time.time;
            float interval = Mathf.Max(0.02f, asteroidGrindFeedbackInterval);
            if (_asteroidGrindFeedbackNextTimeByInstance.TryGetValue(id, out float nextOk) && now < nextOk)
                return false;
            _asteroidGrindFeedbackNextTimeByInstance[id] = now + interval;
            return true;
        }

        private bool CanApplyLocalRamCameraShake() => IsOwner;

        private void EnsureCachedCameraControllerForShake()
        {
            if (s_cachedCameraController == null)
                s_cachedCameraController = UnityEngine.Object.FindFirstObjectByType<TitanOrbit.Camera.CameraController>();
        }

        private void MarkAsteroidRamContact(Asteroid asteroid)
        {
            if (asteroid == null) return;
            _asteroidRamContactInstances.Add(asteroid);
            _asteroidRamContactsThisPhysicsStep.Add(asteroid);
        }

        /// <summary>Drop contacts that stopped reporting collision (despawn, bounce-off, etc.).</summary>
        private void FinalizeAsteroidRamContactsFromLastPhysicsStep()
        {
            var staleContacts = new List<Asteroid>();
            foreach (Asteroid asteroid in _asteroidRamContactInstances)
            {
                if (asteroid == null || asteroid.IsDestroyed || !_asteroidRamContactsThisPhysicsStep.Contains(asteroid))
                    staleContacts.Add(asteroid);
            }

            foreach (Asteroid asteroid in staleContacts)
                ClearAsteroidRamCameraShakeState(asteroid);

            _asteroidRamContactsThisPhysicsStep.Clear();
        }

        /// <summary>0.5s camera shake when breaking an asteroid while in ram/grind contact.</summary>
        private void TryApplyAsteroidRamDestroyCameraShake(Asteroid asteroid)
        {
            if (!CanApplyLocalRamCameraShake() || asteroid == null) return;
            if (!_asteroidRamContactInstances.Contains(asteroid)) return;
            if (_asteroidDestroyShakeTriggered.Contains(asteroid)) return;
            _asteroidDestroyShakeTriggered.Add(asteroid);

            EnsureCachedCameraControllerForShake();
            if (s_cachedCameraController == null || !s_cachedCameraController.IsCollisionCameraShakeEnabled) return;

            float gemSize = Mathf.Max(asteroid.MaxGems, asteroid.RemainingGems);
            float shake = s_cachedCameraController.EvaluateRamDestroyShake(gemSize);
            float duration = s_cachedCameraController.RamDestroyShakeDurationSeconds;
            s_cachedCameraController.ApplyTimedCollisionShake(shake, duration);
        }

        private void ClearAsteroidRamCameraShakeState(Asteroid asteroid)
        {
            if (asteroid == null) return;
            _asteroidRamContactInstances.Remove(asteroid);
            _asteroidRamContactsThisPhysicsStep.Remove(asteroid);
            _asteroidDestroyShakeTriggered.Remove(asteroid);
        }

        private void OnCollisionEnter(Collision collision)
        {
            // Deterministic motor sim handles asteroid and ship collisions on all peers.
        }
        private void OnCollisionExit(Collision collision)
        {
            Asteroid asteroid = collision.gameObject.GetComponent<Asteroid>();
            if (asteroid == null)
                asteroid = collision.gameObject.GetComponentInParent<Asteroid>();
            if (asteroid != null)
            {
                _asteroidGrindFeedbackNextTimeByInstance.Remove(asteroid.GetInstanceID());
                ClearAsteroidRamCameraShakeState(asteroid);
            }
        }

        [ClientRpc]
        private void ClearRammingShakeDriveClientRpc(ClientRpcParams clientRpcParams = default)
        {
            if (!IsOwner) return;
            _asteroidRamContactInstances.Clear();
            _asteroidRamContactsThisPhysicsStep.Clear();
            _asteroidDestroyShakeTriggered.Clear();
        }

        private void OnCollisionStay(Collision collision)
        {
            if (rb == null || collision.contactCount == 0 || isDead.Value) return;

            if (!IsOwner) return;

            Asteroid asteroid = collision.gameObject.GetComponent<Asteroid>();
            if (asteroid == null)
                asteroid = collision.gameObject.GetComponentInParent<Asteroid>();
            if (asteroid == null) return;

            MarkAsteroidRamContact(asteroid);

            if (asteroid.IsDestroyed)
            {
                TryApplyAsteroidRamDestroyCameraShake(asteroid);
                ClearAsteroidRamCameraShakeState(asteroid);
                _asteroidGrindFeedbackNextTimeByInstance.Remove(asteroid.GetInstanceID());
                return;
            }

            ContactPoint contact = collision.GetContact(0);
            Vector3 asteroidCenter = asteroid.transform.position;
            Vector3 n = contact.point - asteroidCenter;
            n.y = 0f;
            if (n.sqrMagnitude < 0.0001f) return;
            n.Normalize();

            Vector3 driveF = GetDrivePushForceXZ();
            float pushN = AsteroidRammingBehavior.ComputeNormalPushNewtons(n, driveF);

            if (asteroidGrindPushToAsteroidDpsScale <= 0f) return;

            if (pushN < asteroidGrindMinPushNewtons) return;
            if (!TryConsumeAsteroidGrindPulse(asteroid)) return;

            float pulseInterval = Mathf.Max(0.02f, asteroidGrindFeedbackInterval);
            float asteroidGrindDamage = ComputeRamGrindAsteroidDamage(pushN, pulseInterval);
            if (asteroidGrindMaxAsteroidDps > 0f)
            {
                float capped = asteroidGrindMaxAsteroidDps * pulseInterval;
                asteroidGrindDamage = Mathf.Min(asteroidGrindDamage, capped);
            }
            if (asteroidGrindDamage <= 0.0001f) return;

            float healthBeforeGrind = asteroid.RemainingHealth;
            ulong attackerShipId = NetworkObject != null ? NetworkObjectId : 0ul;
            ApplyAsteroidRamDamage(asteroid, asteroidGrindDamage, attackerShipId);

            if (healthBeforeGrind <= asteroidGrindDamage + 0.001f)
            {
                TryApplyAsteroidRamDestroyCameraShake(asteroid);
                ClearAsteroidRamCameraShakeState(asteroid);
                _asteroidGrindFeedbackNextTimeByInstance.Remove(asteroid.GetInstanceID());
                return;
            }

            float grindFeedbackRam = GetRammingDamageRating() * GetRammingMassForDamage();
            TryPlayAsteroidGrindFeedback(asteroid, contact.point, n, pushN, grindFeedbackRam, asteroidGrindDamage);

            // Continuous self-damage while grinding; gems only after hull is 0 (1:1 with chip damage).
            float shipGrindDamage = ComputeRamGrindSelfDamage(pushN, pulseInterval);
            if (asteroidGrindMaxAsteroidDps > 0f)
                shipGrindDamage = Mathf.Min(shipGrindDamage, asteroidGrindDamage * ShipComponentRammingSuggestions.SelfToAsteroidDamageRatio);
            if (shipGrindDamage > 0.0001f)
            {
                float expulsionIntensity = ComputeRamGrindGemExpulsionIntensity(pushN, shipGrindDamage);
                ApplyShipRamDamage(
                    shipGrindDamage,
                    expulsionIntensity,
                    gemExpulsionPerHullDamage: 1f);
            }
        }

        private void SpawnAsteroidCollisionFeedback(
            Vector3 hitWorldPos,
            Asteroid asteroid,
            float? damage,
            float? impactForceNewtons)
        {
            if (VisualEffectsManager.Instance == null) return;
            Vector3 pos = hitWorldPos;
            pos.y = Mathf.Max(pos.y, 0f);

            var feedback = new AsteroidFloatingFeedback
            {
                Team = shipTeam.Value,
                Damage = damage,
                RemainingHealth = asteroid != null ? asteroid.RemainingHealth : null,
                RemainingGems = asteroid != null ? asteroid.RemainingGems : null,
                ImpactForceNewtons = impactForceNewtons,
            };

            VisualEffectsManager.Instance.SpawnAsteroidFeedback(pos, feedback);
        }

        private void SpawnAsteroidRamHitFloatingText(Vector3 hitWorldPos, float damage, Asteroid asteroid)
        {
            SpawnAsteroidCollisionFeedback(hitWorldPos, asteroid, damage, null);
        }

        private void SpawnAsteroidImpactForceFloatingText(Vector3 hitWorldPos, float impactForceNewtons)
        {
            SpawnAsteroidCollisionFeedback(hitWorldPos, null, null, impactForceNewtons);
        }

        /// <summary>Throttled VFX, sound, and floating numbers while grinding an asteroid (same flavor as collision enter).</summary>
        private void TryPlayAsteroidGrindFeedback(Asteroid asteroid, Vector3 hitWorldPos, Vector3 asteroidOutwardNormalXZ, float pushNewtons, float ramMul, float damageThisPulse)
        {
            if (asteroid == null) return;

            float equivForce = Mathf.Max(pushNewtons * ramMul * Mathf.Max(0.01f, asteroidGrindFeedbackForceFromPushScale), 30f);

            float pitch = Mathf.Lerp(0.7f, 1.25f, Mathf.InverseLerp(25f, 1200f, equivForce));
            if (AudioManager.Instance != null)
                AudioManager.Instance.PlayAsteroidCollisionSound(pitch);

            if (VisualEffectsManager.Instance != null)
            {
                float sev = ComputeCollisionVfxSeverityFromImpactForce(equivForce);
                sev = Mathf.Max(sev, 0.12f);
                TrySpawnWeaponCollisionImpactVfx(hitWorldPos, asteroidOutwardNormalXZ, sev, pitch, RamGrindImpactVfxScaleFactor);

                SpawnAsteroidRamHitFloatingText(hitWorldPos, damageThisPulse, asteroid);
            }
        }

        private static ulong ToroidalShipPairKey(int instanceIdA, int instanceIdB)
        {
            uint ua = (uint)instanceIdA;
            uint ub = (uint)instanceIdB;
            return ua <= ub ? ((ulong)ua << 32) | ub : ((ulong)ub << 32) | ua;
        }

        /// <summary>XZ hull radius for moon dock zones, ship-vs-ship separation, etc.</summary>
        public float GetShipCollisionRadiusXZ()
        {
            Collider c = rootCollider != null ? rootCollider : GetComponent<Collider>();
            if (c == null) return 0.05f;
            Bounds b = c.bounds;
            return Mathf.Max(0.05f, Mathf.Max(b.extents.x, b.extents.z) * 0.6f);
        }

        /// <summary>
        /// XZ scoop radius for fly-through gem pickup. Uses visible hull/wing extent so gems can be grabbed in flight
        /// without relying on the small root physics BoxCollider alone.
        /// </summary>
        public float GetShipGemFlythroughPickupRadiusXZ()
        {
            if (cachedGemFlythroughPickupRadius >= 0f)
                return cachedGemFlythroughPickupRadius;

            float colliderRadius = GetShipCollisionRadiusXZ();
            Transform visual = GetCardVisualRoot();
            if (visual == null)
            {
                cachedGemFlythroughPickupRadius = colliderRadius;
                return cachedGemFlythroughPickupRadius;
            }

            Bounds bounds = default;
            bool hasBounds = false;
            var renderers = visual.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                    continue;

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!hasBounds)
            {
                cachedGemFlythroughPickupRadius = colliderRadius;
                return cachedGemFlythroughPickupRadius;
            }

            Vector3 shipPos = rb != null ? rb.position : transform.position;
            shipPos.y = 0f;
            Vector3 boundsCenter = bounds.center;
            boundsCenter.y = 0f;
            Vector3 centerOffset = boundsCenter - shipPos;
            float boundsRadius = Mathf.Sqrt(bounds.extents.x * bounds.extents.x + bounds.extents.z * bounds.extents.z);
            cachedGemFlythroughPickupRadius = Mathf.Max(colliderRadius, boundsRadius + centerOffset.magnitude * 0.35f);
            return cachedGemFlythroughPickupRadius;
        }

        /// <summary>
        /// XZ radius from ship center to hull edge for moon orbit/dock zones. The physics collider stays small
        /// to avoid planet collisions, so this scales with ship level the same way the visible prefab root grows.
        /// </summary>
        public float GetShipMoonDockRadiusXZ()
        {
            float colliderR = GetShipCollisionRadiusXZ();
            float levelVisual = Mathf.Max(1f, LevelScaleFactor);
            // Approximate wing extent beyond the physics collider; capped so probe/trigger radii stay sane.
            float hullEdge = colliderR * Mathf.Min(levelVisual * 1.25f, 3f);
            return Mathf.Max(colliderR, hullEdge);
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
        private void TrySpawnWeaponCollisionImpactVfx(Vector3 impactWorldPos, Vector3 outwardXZNormal, float severity01, float audioPitch, float vfxScaleFactor = 1f)
        {
            if (VisualEffectsManager.Instance == null) return;
            impactWorldPos.y = Mathf.Max(impactWorldPos.y, 0f);
            Vector3 n = outwardXZNormal;
            n.y = 0f;
            if (n.sqrMagnitude < 1e-6f) n = transform.forward;
            n.Normalize();
            float scaleMul = Mathf.Lerp(GetCollisionVfxScaleMinMultiplier(), GetCollisionVfxScaleMaxMultiplier(), Mathf.Clamp01(severity01))
                * Mathf.Max(0.05f, vfxScaleFactor);
            int bank = GetCollisionImpactBulletBankIndex();
            VisualEffectsManager.Instance.SpawnWeaponCollisionImpactServerRpc(
                impactWorldPos, n, scaleMul, audioPitch, bank, (int)shipTeam.Value,
                NetworkObject != null ? NetworkObjectId : 0ul);
        }

        private float GetShipShipRestitution() => Mathf.Clamp01(shipShipRestitution);

        /// <summary>Planar velocity for ship–ship impulse (owner/AI sim uses rb; remotes use toroidal pose delta).</summary>
        public Vector3 GetPlanarVelocityForShipCollision()
        {
            if (rb == null) return Vector3.zero;
            if (IsServer || IsOwner)
            {
                Vector3 v = rb.linearVelocity;
                v.y = 0f;
                return v;
            }
            return _collisionPlanarVelocityEstimate;
        }

        private void TickShipCollisionVelocityEstimate(float deltaTime)
        {
            if (rb == null) return;
            Vector3 pos = rb.position;
            pos.y = 0f;
            if (_collisionVelEstHasPrev)
            {
                Vector3 delta = ToroidalMap.ShortestWorldOffsetXZ(_collisionVelEstPrevPos, pos);
                _collisionPlanarVelocityEstimate = delta / Mathf.Max(0.0001f, deltaTime);
            }
            _collisionVelEstPrevPos = pos;
            _collisionVelEstHasPrev = true;
        }

        /// <summary>
        /// Mass-weighted elastic impulse along <paramref name="separationNormalFromOtherToMe"/> (XZ).
        /// Each authoritative ship queues its own velocity delta (owner sim + server AI).
        /// </summary>
        private bool TryApplyShipShipCollisionResponse(
            Starship other,
            Vector3 separationNormalFromOtherToMe,
            float minClosingSpeed = 0.35f)
        {
            if (other == null || rb == null) return false;
            if (!IsServer) return false;

            Rigidbody otherRb = other.GetComponent<Rigidbody>();
            if (otherRb == null) return false;

            Vector3 n = separationNormalFromOtherToMe;
            n.y = 0f;
            if (n.sqrMagnitude < 1e-8f) return false;
            n.Normalize();

            Vector3 vMe = GetPlanarVelocityForShipCollision();
            Vector3 vOther = other.GetPlanarVelocityForShipCollision();
            float vRelN = Vector3.Dot(vMe - vOther, n);
            // Negative = closing along separation normal (approaching each other).
            if (vRelN >= -minClosingSpeed) return false;

            ulong pairKey = ToroidalShipPairKey(GetInstanceID(), other.GetInstanceID());
            float now = Time.time;
            if (_toroidalShipPairLastImpulseTime.TryGetValue(pairKey, out float lastImpulse) && now - lastImpulse < 0.1f)
                return false;
            _toroidalShipPairLastImpulseTime[pairKey] = now;

            float mMe = Mathf.Max(0.5f, rb.mass);
            float mOther = Mathf.Max(0.5f, otherRb.mass);
            float e = GetShipShipRestitution();
            float invMassSum = 1f / mMe + 1f / mOther;
            float j = -(1f + e) * vRelN / invMassSum;
            Vector3 newVel = vMe + n * (j / mMe);
            newVel.y = 0f;

            _pendingShipShipBounceVelocity = newVel;
            _hasPendingShipShipBounce = true;
            rb.linearVelocity = newVel;
            currentVelocity = newVel;
            return true;
        }

        /// <summary>
        /// Ships keep unwrapped world positions; Unity colliders only see raw separation, so hulls can overlap
        /// on the torus without <see cref="OnCollisionEnter"/>. Resolve overlap using shortest toroidal offset
        /// (each authoritative body corrects itself, matching owner physics + server AI).
        /// </summary>
        private void TickToroidalShipVsShipCollision()
        {
            bool auth = IsServer;
            if (!auth || rb == null) return;

            Vector3 myPos = rb.position;
            myPos.y = 0f;
            float myR = GetShipCollisionRadiusXZ();
            float mMe = Mathf.Max(0.5f, rb.mass);

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
                Vector3 separationNormal = -n;

                float penetration = combined - Mathf.Max(dist, 0.0001f);
                float mOther = Mathf.Max(0.5f, otherRb.mass);
                float totalMass = mMe + mOther;
                float mySepShare = totalMass > 0.001f ? mOther / totalMass : 0.5f;
                Vector3 newPos = rb.position + separationNormal * (penetration * mySepShare);
                rb.MovePosition(newPos);

                TryApplyShipShipCollisionResponse(other, separationNormal);

                Vector3 vMe = GetPlanarVelocityForShipCollision();
                Vector3 vO = other.GetPlanarVelocityForShipCollision();
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

        private void SpawnPeopleTransferFloatingCount(FloatingCountChannel channel, float amount, Vector3 worldPosition)
        {
            if (!IsServer || amount <= 0.0001f || VisualEffectsManager.Instance == null)
                return;

            Vector3 pos = worldPosition;
            pos.y = 0f;
            float signedAmount = channel == FloatingCountChannel.PeopleUnload ? -amount : amount;
            VisualEffectsManager.Instance.SpawnFloatingCountFromServerAuthority(pos, channel, signedAmount, shipTeam.Value);
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
            if (TryConsumeFromEquipment(StoreItemType.SmallRockets)) return true;
            if (smallRocketsCount.Value <= 0) return false;
            smallRocketsCount.Value--;
            return true;
        }
        public bool ConsumeLargeRocket()
        {
            if (TryConsumeFromEquipment(StoreItemType.LargeRockets)) return true;
            if (largeRocketsCount.Value <= 0) return false;
            largeRocketsCount.Value--;
            return true;
        }
        public bool ConsumeSmallMine()
        {
            if (TryConsumeFromEquipment(StoreItemType.SmallMines)) return true;
            if (smallMinesCount.Value <= 0) return false;
            smallMinesCount.Value--;
            return true;
        }
        public bool ConsumeLargeMine()
        {
            if (TryConsumeFromEquipment(StoreItemType.LargeMines)) return true;
            if (largeMinesCount.Value <= 0) return false;
            largeMinesCount.Value--;
            return true;
        }

        [ServerRpc(RequireOwnership = false)]
        public void AddPeopleServerRpc(float amount)
        {
            AddPeopleFromServer(amount);
        }

        /// <summary>Server-only: add people without routing through ServerRpc (used by people transport projectiles).</summary>
        public void AddPeopleFromServer(float amount)
        {
            if (!IsServer || amount <= 0f) return;
            currentPeople.Value = Mathf.Min(currentPeople.Value + amount, PeopleCapacity);
        }

        /// <summary>Server-only: remove people without routing through ServerRpc.</summary>
        public void RemovePeopleFromServer(float amount)
        {
            if (!IsServer || amount <= 0f) return;
            currentPeople.Value = Mathf.Max(0f, currentPeople.Value - amount);
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
                Vector3 pos = worldPosition;
                pos.y = 0f;
                VisualEffectsManager.Instance.SpawnFloatingCountFromServerAuthority(
                    pos,
                    FloatingCountChannel.PeopleLoad,
                    amount,
                    sourceTeam);
            }

            PlayPeopleLoadSoundClientRpc(amount);
        }

        /// <summary>
        /// Server-only: apply successful people unload arrival feedback at planet contact.
        /// Called by PeopleTransportProjectile when an unload projectile reaches the planet surface.
        /// </summary>
        public void OnPeopleUnloadArrivedFromProjectile(float amount, TeamManager.Team sourceTeam, Vector3 worldPosition, Planet targetPlanet)
        {
            if (!IsServer || amount <= 0f) return;

            if (VisualEffectsManager.Instance != null)
            {
                Vector3 pos = worldPosition;
                pos.y = 0f;
                VisualEffectsManager.Instance.SpawnFloatingCountFromServerAuthority(
                    pos,
                    FloatingCountChannel.PeopleUnload,
                    -amount,
                    sourceTeam);
            }

            PlayPeopleUnloadSoundClientRpc(amount);

            if (targetPlanet != null
                && targetPlanet.TeamOwnership != sourceTeam
                && !(targetPlanet is HomePlanet home && home.AssignedTeam == sourceTeam)
                && ScoreSystem.Instance != null)
            {
                ScoreSystem.Instance.AwardHostileUnload(this, amount);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void RemovePeopleServerRpc(float amount)
        {
            RemovePeopleFromServer(amount);
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

        /// <summary>Owner client: immediate repel from an active gem-moon shield (player ships are not driven by server physics).</summary>
        public void ApplyGemMoonShieldRepelLocal(Vector3 outwardVelocityWorld)
        {
            if (rb == null) return;
            outwardVelocityWorld.y = 0f;
            _pendingGemMoonShieldRepelVelocity = outwardVelocityWorld;
            _hasPendingGemMoonShieldRepel = true;
            rb.linearVelocity = outwardVelocityWorld;
            rb.angularVelocity = Vector3.zero;
            currentVelocity = rb.linearVelocity;
        }

        [ClientRpc]
        private void ApplyGemMoonShieldRepelClientRpc(Vector3 outwardVelocityWorld, ClientRpcParams clientRpcParams = default)
        {
            if (!IsOwner || rb == null) return;
            ApplyGemMoonShieldRepelLocal(outwardVelocityWorld);
        }

        /// <summary>Server-only: repel a ship from this moon's shield (AI on server rigidbody; players via owner ClientRpc).</summary>
        public void ServerNotifyGemMoonShieldRepel(Vector3 outwardVelocityWorld)
        {
            if (!IsServer) return;
            outwardVelocityWorld.y = 0f;
            ServerSetGemMoonDocked(false, null);

            ApplyGemMoonShieldRepelClientRpc(outwardVelocityWorld, OwnerOnlyClientRpcParams);
        }

        /// <summary>Server-only: set by <see cref="PlanetGemMoon"/> when a ship enters or leaves the dock trigger.</summary>
        /// <summary>Server: accumulate low-speed time in a gem-moon zone so fly-through does not auto-dock.</summary>
        public void ServerTickGemMoonLandingDwell(bool counting, float deltaTime)
        {
            if (!IsServer) return;
            if (counting)
                _serverGemMoonLandingDwellSeconds = Mathf.Min(
                    _serverGemMoonLandingDwellSeconds + deltaTime,
                    GemMoonLandingDwellSecondsRequired + 0.5f);
            else
                _serverGemMoonLandingDwellSeconds = 0f;
        }

        public bool ServerGemMoonLandingDwellMet =>
            _serverGemMoonLandingDwellSeconds >= GemMoonLandingDwellSecondsRequired;

        /// <summary>True when inside a friendly gem moon's dock/orbit shell (skip forced planet orbit capture while passing through).</summary>
        public bool IsInsideFriendlyGemMoonOrbitZone(float radiusMultiplier = 1.05f)
        {
            if (shipTeam.Value == TeamManager.Team.None) return false;
            for (int i = 0; i < PlanetGemMoon.ActiveMoonCount; i++)
            {
                PlanetGemMoon moon = PlanetGemMoon.GetActiveMoonAt(i);
                if (moon == null || !moon.IsTeamFriendlyToThisMoon(shipTeam.Value)) continue;
                if (moon.IsShipInMoonDockZoneToroidal(this, radiusMultiplier))
                    return true;
            }
            return false;
        }

        /// <summary>True inside any gem-moon orbit shell — equipped drones are stowed until the ship exits.</summary>
        public bool IsInGemMoonOrbitStowingDrones(float radiusMultiplier = 1f)
        {
            for (int i = 0; i < PlanetGemMoon.ActiveMoonCount; i++)
            {
                PlanetGemMoon moon = PlanetGemMoon.GetActiveMoonAt(i);
                if (moon != null && moon.IsShipInMoonDockZoneToroidal(this, radiusMultiplier))
                    return true;
            }
            return false;
        }

        public void ServerSetGemMoonDocked(bool value, Planet planetContext = null)
        {
            if (!IsServer) return;
            bool wasDocked = gemMoonDocked.Value;
            gemMoonDocked.Value = value;
            // Only clear landing dwell when actually undocking. OnTriggerStay calls Set(false) every
            // frame while dwell accumulates; resetting here prevented the landing sequence from ever starting.
            if (!value && wasDocked)
            {
                _serverGemMoonLandingDwellSeconds = 0f;
                triggeredGalacticZoomThisMoonDock = false;
            }
            if (value && planetContext != null)
            {
                var no = planetContext.GetComponent<NetworkObject>();
                gemMoonPlanetNetworkObjectId.Value = no != null ? no.NetworkObjectId : 0ul;
            }
            else
                gemMoonPlanetNetworkObjectId.Value = 0ul;
        }

        /// <summary>Server: galactic zoom when the dock ease-in-out finishes (ship on moon surface), not when dock latch fires at orbit-zone edge.</summary>
        private void ServerTryTriggerGalacticZoomOnMoonSurfaceLanding()
        {
            if (!gemMoonDocked.Value)
            {
                triggeredGalacticZoomThisMoonDock = false;
                return;
            }

            if (triggeredGalacticZoomThisMoonDock || !IsGemMoonSurfaceLandingComplete())
                return;

            triggeredGalacticZoomThisMoonDock = true;
            var sendParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new ulong[] { OwnerClientId }
                }
            };
            TriggerGalacticZoomClientRpc(sendParams);
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

            // Already on server inside this ServerRpc — nested ServerRpc would not deduct gems.
            ApplyRemoveGemsOnServer(cost);
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

            ClampCarriedResourcesToCapacity();
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

        /// <summary>
        /// Client id used for contributed-gem store credit. AI ships use their <see cref="NetworkObject.NetworkObjectId"/>
        /// so each bot has its own pool (server-owned ships would otherwise all share client 0).
        /// </summary>
        public ulong GetContributedGemsClientId() => OwnerClientId;

        /// <summary>Server-only: set wantToDepositGems (bypasses RPC ownership).</summary>
        public void SetWantToDepositGemsFromServer(bool value)
        {
            if (!IsServer) return;
            wantToDepositGems.Value = value;
        }

        /// <summary>Server-only: detect if ship is inside a planet's orbit zone (e.g. after spawning there). OnTriggerEnter doesn't fire for objects that start inside.</summary>
        /// <summary>Server: true if the given XZ world position lies in any planet's orbit band (same ring math as <see cref="RefreshOrbitPlanetFromPosition"/>).</summary>
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
                if (planet.IsWorldPositionInOrbitRing(shipWorldPos))
                    return true;
            }
            return false;
        }

        /// <summary>Planet orbit rings block weapons fire unless the ship is in a friendly gem-moon defensive shell.</summary>
        private bool ServerBlocksOrbitZoneWeaponFire(Vector3 shipWorldPos)
        {
            if (!ServerWorldPositionInsideAnyOrbitZone(shipWorldPos))
                return false;
            return !IsInsideFriendlyGemMoonOrbitZone();
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
            if (currentOrbitPlanet != planet)
                ClearLockedOrbitRadius();
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
                currentOrbitPlanet = null;
                ClearLockedOrbitRadius();
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
                    _chassisReferenceHealth = Mathf.Max(1f, data.baseMaxHealth);
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
            _lastAppliedChassisIndex = currentChassisIndex.Value;
            _lastAppliedShipLevel = ShipLevel;
            var composer = GetComponent<ShipVisualComposer>();
            if (composer != null) composer.RebuildVisuals();
            ApplyHullIdentityColor();
        }

        /// <summary>Keep runtime ShipData level/branch aligned with restored or synced ship state (visualScale and stats helpers).</summary>
        private void SyncShipDataToLevelAndBranch(int level, int branchIndex)
        {
            int clampedLevel = Mathf.Max(1, level);
            if (shipData != null)
                shipData = Instantiate(shipData);
            else
                shipData = ScriptableObject.CreateInstance<ShipData>();
            shipData.shipLevel = clampedLevel;
            shipData.branchIndex = branchIndex;
        }

        /// <summary>Resolve the networked chassis id/index to a prefab and apply it; updates last-applied chassis/level tracking.</summary>
        private void TryApplyChassisVisualFromNetworkState()
        {
            if (CardShopSystem.Instance == null || currentChassisIndex.Value < 0)
                return;

            string cid = currentChassisId.Value.ToString();
            GameObject prefab = !string.IsNullOrEmpty(cid)
                ? CardShopSystem.Instance.GetShipPrefabForChassisId(cid)
                : null;
            if (prefab == null)
                prefab = CardShopSystem.Instance.GetShipPrefabForChassisIndex(currentChassisIndex.Value);

            if (prefab != null)
            {
                ApplyShipVisualFromPrefab(prefab);
                _lastAppliedChassisIndex = currentChassisIndex.Value;
                _lastAppliedShipLevel = ShipLevel;
                return;
            }

            if (currentChassisIndex.Value != _lastAppliedChassisIndex || ShipLevel != _lastAppliedShipLevel)
            {
                Debug.LogWarning($"Starship: No prefab for chassis '{cid}' (index {currentChassisIndex.Value}). Assign ShipUnlockTable.homeShipFamilyDefinition with an upgrade tree that has prefabs set, or assign CardShopSystem's Ship Unlock Table.");
                _lastAppliedChassisIndex = currentChassisIndex.Value;
                _lastAppliedShipLevel = ShipLevel;
            }
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

            // Skip duplicate applies in one frame only when chassis/level/prefab are unchanged (avoids stacking work).
            // Do not skip when chassis or level changed — a prior apply may have scanned ghost weapons from deferred Destroy.
            if (lastVisualApplyFrame == Time.frameCount
                && lastVisualApplyPrefab == shipPrefab
                && _lastAppliedChassisIndex == currentChassisIndex.Value
                && _lastAppliedShipLevel == ShipLevel)
            {
                return;
            }
            lastVisualApplyFrame = Time.frameCount;
            lastVisualApplyPrefab = shipPrefab;
            cachedGemFlythroughPickupRadius = -1f;

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

                // Detach before Destroy so ChassisComponentStats.FromTransform does not count weapons from the old hull this frame.
                oldChild.SetParent(null, false);
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
            UpdateMoonDockProbeCollider();
            RebuildEquippedComponentVisuals();
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

            if (usePreviewStats)
            {
                ShipComponentAbilityStats s = previewStats.Value;
                float perLvl = Mathf.Max(0, level - 1);

                _chassisReferenceHealth = Mathf.Max(1f, s.healthCap);
                maxHealth = Mathf.Max(1f, s.healthCap + s.healthCapPerLevel * perLvl);
                healthRegenRate = Mathf.Max(0f, s.healthRegen + s.healthRegenPerLevel * perLvl);
                // Weapon energy is per-cannon (built below). Cockpit-only ships may still use summed energy.
                int weaponTransformCount = stats.weaponTransforms != null ? stats.weaponTransforms.Count : 0;
                if (weaponTransformCount > 0)
                {
                    energyCapacity = 0f;
                    energyRegenRate = 0f;
                }
                else
                {
                    energyCapacity = Mathf.Max(1f, s.energyCap + s.energyCapPerLevel * perLvl);
                    energyRegenRate = Mathf.Max(0f, s.energyRegen + s.energyRegenPerLevel * perLvl);
                }
                rotationSpeed = Mathf.Max(1f, ApplyShipLevelMobilityScale(s.turnSpeed, perLvl));
                rotationSpeedFromShipFamilyDefinition = true;
                gemCapacity = Mathf.Max(0f, s.maxGems + s.maxGemsPerLevel * perLvl);
                peopleCapacity = Mathf.Max(0f, s.maxPeople + s.maxPeoplePerLevel * perLvl);
                _summedRammingPowerBase = Mathf.Max(0f, s.rammingPower);
                _summedRammingPowerPerLevel = Mathf.Max(0f, s.rammingPowerPerLevel);
                RefreshTotalRammingPower();

                // Movement: engines and thrusters share one pool — best base once + half the sum of other parts' moveSpeedPerLevel; acceleration sums.
                float moveVal = Mathf.Max(0.1f, ApplyShipLevelMobilityScale(s.moveSpeed, perLvl));
                ShipPropulsionAggregation.Result propulsion = ShipPropulsionAggregation.ComputeThrusterPropulsion(
                    matchedComponentIds,
                    perComponentStats,
                    level);
                float accelFallback = Mathf.Max(0f, s.accelerationCap + s.accelerationCapPerLevel * perLvl);
                componentEngineThrust = Mathf.Max(0f, propulsion.sumAcceleration > 0f
                    ? propulsion.sumAcceleration
                    : (accelFallback > 0f ? accelFallback : moveVal));
                componentEngineMaxSpeed = Mathf.Max(0.1f, propulsion.topMoveSpeed > 0f
                    ? propulsion.topMoveSpeed
                    : (moveVal > 0f ? moveVal : engineThrust * 0.5f));

                componentMass = stats.ComputeComponentMass();
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

                componentMass = stats.ComputeComponentMass();

                float turnVal = stats.thrusterScaleTotal + stats.tailScaleTotal + stats.wingScaleTotal + stats.finScaleTotal;
                float healthVal = stats.cockpitScaleTotal + stats.partScaleTotal;
                float healthRegenVal = stats.wingScaleTotal + stats.partScaleTotal;
                float gemVal = stats.wingScaleTotal + stats.partScaleTotal;
                float peopleVal = stats.cockpitScaleTotal + stats.partScaleTotal;
                float energyCapVal = stats.cockpitCannonScaleTotal;
                float energyRegenVal = stats.cockpitCannonScaleTotal;
                _summedRammingPowerBase = Mathf.Max(0f, stats.cockpitScaleTotal);
                _summedRammingPowerPerLevel = 0f;
                RefreshTotalRammingPower();

                rotationSpeed = Mathf.Max(1f, turnVal);
                rotationSpeedFromShipFamilyDefinition = false;
                _chassisReferenceHealth = Mathf.Max(1f, healthVal);
                maxHealth = Mathf.Max(1f, healthVal);
                healthRegenRate = Mathf.Max(0f, healthRegenVal);
                gemCapacity = Mathf.Max(0f, gemVal);
                peopleCapacity = Mathf.Max(0f, peopleVal);
                energyCapacity = Mathf.Max(1f, energyCapVal);
                energyRegenRate = Mathf.Max(0f, energyRegenVal);
            }

            RecalculateEquippedComponentStatSum();

            // Clear component scale caches for attribute-based scaling
            cockpitScaleTransforms.Clear();
            cockpitBaseScales.Clear();
            cockpitBasePositions.Clear();
            wingScaleTransforms.Clear();
            wingBaseScales.Clear();
            wingBasePositions.Clear();
            wingTractorBeams.Clear();
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
            if (weaponCount > 0)
            {
                var baseBullet = new CannonConfig();
                var bc = ScriptableObject.CreateInstance<WeaponConfig>();
                bc.displayName = "ChassisBullets";
                bc.cannons = new System.Collections.Generic.List<CannonConfig>();
                var buildCannonEnergyCaps = new System.Collections.Generic.List<float>();
                var buildCannonEnergyRegens = new System.Collections.Generic.List<float>();

                // Per-level weapon scaling uses ship level (same pattern as health, ramming, etc.).
                float perLvlWeapon = Mathf.Max(0, level - 1);

                // Use same familyId as ShipFamilyStatsPreview so componentId matches matchedComponentIds (e.g. "Weapon_1" not full name).
                string weaponLookupFamilyId = (previewFamilyDef != null && !string.IsNullOrEmpty(previewFamilyDef.familyId))
                    ? previewFamilyDef.familyId.Trim()
                    : prefix;

                for (int i = 0; i < weaponCount; i++)
                {
                    Transform wt = stats.weaponTransforms != null && i < stats.weaponTransforms.Count ? stats.weaponTransforms[i] : null;
                    if (wt == null) continue;

                    var c = baseBullet.Clone();
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
                    float weaponEnergyCap = 50f;
                    float weaponEnergyRegen = 5f;
                    // 1) Prefer per-component stats from ShipFamilyStatsPreview (matched component list) - case-insensitive match.
                    if (matchedComponentIds != null && perComponentStats != null && !string.IsNullOrEmpty(componentId))
                    {
                        for (int k = 0; k < matchedComponentIds.Count; k++)
                        {
                            if (string.Equals(matchedComponentIds[k], componentId, System.StringComparison.OrdinalIgnoreCase) && k < perComponentStats.Count)
                            {
                                ShipComponentAbilityStats comp = perComponentStats[k];
                                float wp = comp.firePower + comp.firePowerPerLevel * perLvlWeapon;
                                float bs = comp.bulletSpeed;
                                float fr = Mathf.Max(0.01f, comp.fireRate + comp.fireRatePerLevel * perLvlWeapon);
                                c.damagePerBullet = wp;
                                c.bulletSpeed = bs;
                                c.fireRate = fr;
                                c.energyCostPerShot = c.damagePerBullet;
                                ExtractWeaponEnergyFromStats(comp, perLvlWeapon, out weaponEnergyCap, out weaponEnergyRegen);
                                buildCannonEnergyCaps.Add(weaponEnergyCap);
                                buildCannonEnergyRegens.Add(weaponEnergyRegen);
                                usedPerComponent = true;
                                break;
                            }
                        }
                    }
                    // 2) If no match in preview list, get this weapon's stats from ShipFamilyDefinition and scale by transform (still per-component, not summed).
                    if (!usedPerComponent && previewFamilyDef != null && wt != null && !string.IsNullOrEmpty(componentId) && previewFamilyDef.TryGetStatsForComponent(componentId, out var defStats))
                    {
                        ShipComponentAbilityStats scaled = ShipComponentAbilityStats.ScaleStatsByTransform(defStats, wt, componentId);
                        float wp = scaled.firePower + scaled.firePowerPerLevel * perLvlWeapon;
                        float bs = scaled.bulletSpeed;
                        float fr = Mathf.Max(0.01f, scaled.fireRate + scaled.fireRatePerLevel * perLvlWeapon);
                        c.damagePerBullet = wp;
                        c.bulletSpeed = bs;
                        c.fireRate = fr;
                        c.energyCostPerShot = c.damagePerBullet;
                        ExtractWeaponEnergyFromStats(scaled, perLvlWeapon, out weaponEnergyCap, out weaponEnergyRegen);
                        buildCannonEnergyCaps.Add(weaponEnergyCap);
                        buildCannonEnergyRegens.Add(weaponEnergyRegen);
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
                            float wp = scaled.firePower + scaled.firePowerPerLevel * perLvlWeapon;
                            float bs = scaled.bulletSpeed;
                            float fr = Mathf.Max(0.01f, scaled.fireRate + scaled.fireRatePerLevel * perLvlWeapon);
                            c.damagePerBullet = wp;
                            c.bulletSpeed = bs;
                            c.fireRate = fr;
                            c.energyCostPerShot = c.damagePerBullet;
                            ExtractWeaponEnergyFromStats(scaled, perLvlWeapon, out weaponEnergyCap, out weaponEnergyRegen);
                            buildCannonEnergyCaps.Add(weaponEnergyCap);
                            buildCannonEnergyRegens.Add(weaponEnergyRegen);
                            resolvedComponentId = entry.componentId;
                            usedPerComponent = true;
                            break;
                        }
                    }
                    if (!usedPerComponent)
                    {
                        buildCannonEnergyCaps.Add(Mathf.Max(0.1f, energyCapacity > 0f ? energyCapacity : 50f));
                        buildCannonEnergyRegens.Add(Mathf.Max(0f, energyRegenRate > 0f ? energyRegenRate : 5f));
                    }
                    // Per-weapon bullet prefab index from ShipFamilyComponentEntry (index into CombatSystem's Bullet Prefab Bank).
                    if (previewFamilyDef != null && !string.IsNullOrEmpty(resolvedComponentId) && previewFamilyDef.TryGetComponentEntry(resolvedComponentId, out var compEntry) && compEntry != null && compEntry.bulletPrefabIndex >= 0)
                        c.bulletPrefabIndex = compEntry.bulletPrefabIndex;
                    bc.cannons.Add(c);
                    bulletFirePoints.Add(wt);

                    float ws = (stats.weaponScales != null && i < stats.weaponScales.Count) ? stats.weaponScales[i] : 1f;
                    float muzzleScale = (MUZZLE_BASE_SIZE + c.energyCostPerShot * MUZZLE_SIZE_PER_ENERGY) * Mathf.Max(0.5f, ws);
                    ParticleSystem muzzle = CreateMuzzleParticleSystem(wt, muzzleScale);
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
                if (bc.cannons.Count == 0)
                {
                    Object.Destroy(bc);
                    EnsureCannonEnergyState(0);
                }
                else
                {
                    EnsureCannonEnergyState(bc.cannons.Count);
                    for (int ci = 0; ci < bc.cannons.Count; ci++)
                    {
                        cannonEnergyCapacityBase[ci] = ci < buildCannonEnergyCaps.Count
                            ? buildCannonEnergyCaps[ci]
                            : Mathf.Max(0.1f, energyCapacity > 0f ? energyCapacity : 50f);
                        cannonEnergyRegenBase[ci] = ci < buildCannonEnergyRegens.Count
                            ? buildCannonEnergyRegens[ci]
                            : Mathf.Max(0f, energyRegenRate > 0f ? energyRegenRate : 5f);
                    }

                    if (IsServer)
                        RefillCannonEnergyFromServer();

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
            }
            else
            {
                EnsureCannonEnergyState(0);
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

            BuildWingTractorBeams(stats.wingTransforms, prefix, previewFamilyDef, matchedComponentIds, perComponentStats, level);
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

            // Hull mesh (root body) scales with health/energy/people attribute upgrades like Cockpit parts.
            Transform hull = root.Find("Hull");
            if (hull != null)
            {
                cockpitScaleTransforms.Add(hull);
                cockpitBaseScales.Add(hull.localScale);
                cockpitBasePositions.Add(hull.localPosition);
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

            if (IsServer && IsSpawned)
                ClampCarriedResourcesToCapacity();

            SnapshotAuthoredChassisPlacements();
        }

        private void SnapshotAuthoredChassisPlacements()
        {
            CopyAuthoredPlacementList(wingScaleTransforms, wingBasePositions, _authoredWingPositions, _authoredWingRotations);
            CopyAuthoredPlacementList(weaponScaleTransforms, weaponBasePositions, _authoredWeaponPositions, _authoredWeaponRotations);
            CopyAuthoredPlacementList(cockpitScaleTransforms, cockpitBasePositions, _authoredCockpitPositions, _authoredCockpitRotations, skipHull: true);
            CopyAuthoredPlacementList(partScaleTransforms, partBasePositions, _authoredPartPositions, _authoredPartRotations);
        }

        private static void CopyAuthoredPlacementList(
            List<Transform> transforms,
            List<Vector3> basePositions,
            List<Vector3> authoredPositions,
            List<Quaternion> authoredRotations,
            bool skipHull = false)
        {
            authoredPositions.Clear();
            authoredRotations.Clear();
            if (transforms == null)
                return;

            for (int i = 0; i < transforms.Count; i++)
            {
                Transform t = transforms[i];
                if (t == null)
                    continue;
                if (skipHull && t.name == "Hull")
                    continue;

                authoredPositions.Add(i < basePositions?.Count ? basePositions[i] : t.localPosition);
                authoredRotations.Add(t.localRotation);
            }
        }

        /// <summary>
        /// One tractor beam per wing transform. Pull reach and strength use each wing's Max Gems Capacity from ShipFamilyDefinition.
        /// </summary>
        private void BuildWingTractorBeams(
            List<Transform> wingTransforms,
            string familyPrefix,
            ShipFamilyDefinition previewFamilyDef,
            System.Collections.Generic.IReadOnlyList<string> matchedComponentIds,
            System.Collections.Generic.IReadOnlyList<ShipComponentAbilityStats> perComponentStats,
            int shipLevel)
        {
            wingTractorBeams.Clear();
            if (wingTransforms == null || wingTransforms.Count == 0)
                return;

            string lookupFamilyId = (previewFamilyDef != null && !string.IsNullOrEmpty(previewFamilyDef.familyId))
                ? previewFamilyDef.familyId.Trim()
                : familyPrefix;

            for (int i = 0; i < wingTransforms.Count; i++)
            {
                Transform wt = wingTransforms[i];
                if (wt == null)
                    continue;

                string componentId = "";
                if (!string.IsNullOrEmpty(wt.name))
                {
                    if (!string.IsNullOrEmpty(lookupFamilyId) &&
                        wt.name.StartsWith(lookupFamilyId + "_", System.StringComparison.OrdinalIgnoreCase))
                        componentId = wt.name.Substring(lookupFamilyId.Length + 1);
                    else
                        componentId = wt.name;
                }

                ShipComponentAbilityStats wingStats = default;
                wingStats.maxGems = 8f;
                bool resolved = false;

                if (matchedComponentIds != null && perComponentStats != null && !string.IsNullOrEmpty(componentId))
                {
                    for (int k = 0; k < matchedComponentIds.Count; k++)
                    {
                        if (string.Equals(matchedComponentIds[k], componentId, System.StringComparison.OrdinalIgnoreCase) &&
                            k < perComponentStats.Count)
                        {
                            wingStats = perComponentStats[k];
                            resolved = true;
                            break;
                        }
                    }
                }

                if (!resolved && previewFamilyDef != null && !string.IsNullOrEmpty(componentId) &&
                    previewFamilyDef.TryGetStatsForComponent(componentId, out var defStats))
                {
                    wingStats = ShipComponentAbilityStats.ScaleStatsByTransform(defStats, wt, componentId);
                    resolved = true;
                }

                if (!resolved && previewFamilyDef != null && previewFamilyDef.components != null)
                {
                    int wingEntryCounter = -1;
                    for (int e = 0; e < previewFamilyDef.components.Count; e++)
                    {
                        var entry = previewFamilyDef.components[e];
                        if (entry == null || string.IsNullOrEmpty(entry.componentId))
                            continue;
                        string partType = ShipComponentAbilityStats.ResolvePartTypeForSuggestedStats(entry.componentId);
                        if (!string.Equals(partType, "Wing", System.StringComparison.OrdinalIgnoreCase))
                            continue;
                        wingEntryCounter++;
                        if (wingEntryCounter != i)
                            continue;

                        wingStats = ShipComponentAbilityStats.ScaleStatsByTransform(entry.stats, wt, entry.componentId);
                        break;
                    }
                }

                wingTractorBeams.Add(new WingTractorBeamSlot(
                    wt,
                    wingStats.tractorBeamDistance,
                    wingStats.tractorBeamDistancePerLevel,
                    wingStats.tractorBeamPower,
                    wingStats.tractorBeamPowerPerLevel,
                    wingStats.maxGems,
                    wingStats.maxGemsPerLevel));
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
            ClearAllEquipmentFromServer();
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
            RefillCannonEnergyFromServer();
        }

        /// <summary>Server only: removes all equipped cards. Called when ship levels up.</summary>
        private void ClearAllCardsFromServer()
        {
            if (!IsServer) return;
            if (equippedCards != null) equippedCards.Clear();
            if (equippedCardIds != null) equippedCardIds.Clear();
            _cardStatsCacheFrame = -1;
            ClampCarriedResourcesToCapacity();
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

        private float ComputeMaxHealthLocal()
        {
            float baseWithCards = maxHealth + _equippedComponentStatSum.healthCap + GetCardMaxHealthAdd();
            float attrScale = 1f + attrMaxHealth.Value * ATTR_MULTIPLIER_PER_LEVEL;
            return Mathf.Max(1f, baseWithCards * attrScale);
        }

        private float ComputeEnergyCapacityLocal()
        {
            float baseWithCards = HasWeaponComponentEnergy
                ? GetSummedWeaponEnergyCapacityBase() + GetCardEnergyCapacityAdd()
                : energyCapacity + _equippedComponentStatSum.energyCap + GetCardEnergyCapacityAdd();
            float attrScale = 1f + attrEnergyCapacity.Value * ATTR_MULTIPLIER_PER_LEVEL;
            return Mathf.Max(0.1f, baseWithCards * attrScale);
        }

        private float ComputeGemCapacityLocal()
        {
            float baseWithCards = gemCapacity + _equippedComponentStatSum.maxGems + GetCardGemCapacityAdd();
            float attrScale = 1f + attrGemCapacity.Value * ATTR_MULTIPLIER_PER_LEVEL;
            return Mathf.Max(0f, baseWithCards * attrScale);
        }

        private float ComputePeopleCapacityLocal()
        {
            float baseWithCards = peopleCapacity + _equippedComponentStatSum.maxPeople + GetCardPeopleCapacityAdd();
            float attrScale = 1f + attrPeopleCapacity.Value * ATTR_MULTIPLIER_PER_LEVEL;
            return Mathf.Max(0f, baseWithCards * attrScale);
        }

        /// <summary>Server: push authoritative caps so client HUD matches gameplay limits.</summary>
        private void RefreshSyncedCapacitiesOnServer()
        {
            if (!IsServer || !IsSpawned) return;
            networkGemCapacity.Value = ComputeGemCapacityLocal();
            networkPeopleCapacity.Value = ComputePeopleCapacityLocal();
            networkMaxHealth.Value = ComputeMaxHealthLocal();
            networkEnergyCapacity.Value = ComputeEnergyCapacityLocal();
        }

        /// <summary>Server: keep carried resources and health/energy within current capacity after loadout or chassis changes.</summary>
        private void ClampCarriedResourcesToCapacity()
        {
            if (!IsServer || !IsSpawned) return;
            RefreshSyncedCapacitiesOnServer();
            currentGems.Value = Mathf.Clamp(currentGems.Value, 0f, networkGemCapacity.Value);
            currentPeople.Value = Mathf.Clamp(currentPeople.Value, 0f, networkPeopleCapacity.Value);
            currentHealth.Value = Mathf.Clamp(currentHealth.Value, 0f, networkMaxHealth.Value);
            currentEnergy.Value = Mathf.Clamp(currentEnergy.Value, 0f, networkEnergyCapacity.Value);
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
                equippedCardIds.Add(new EquippedCardId { cardId = new FixedString64Bytes(card.GetStableCardId()) });
                _cardStatsCacheFrame = -1;
                ClampCarriedResourcesToCapacity();
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
            ClampCarriedResourcesToCapacity();
        }

        /// <summary>Client calls this to request removal of a card at the given slot. Only the ship owner can remove cards.</summary>
        [ServerRpc(RequireOwnership = true)]
        public void RemoveCardServerRpc(int slotIndex)
        {
            RemoveCardFromServer(slotIndex);
        }

        /// <summary>Server-only: add a store item to the first available equipment slot.</summary>
        public bool AddEquipmentFromServer(StoreItemType itemType, int? overrideCharges = null)
        {
            if (!IsServer) return false;
            if (StoreItemData.IsShipComponent(itemType)) return false;
            if (equippedEquipment == null) equippedEquipment = new List<EquippedEquipmentEntry>();
            if (equippedEquipmentEntries == null) return false;
            if (equippedEquipment.Count >= EquipmentSlotCount) return false;

            int charges = overrideCharges
                ?? (StoreItemData.IsDrone(itemType) ? StoreItemData.GetDroneMaxHp(itemType) : StoreItemData.GetPackSize(itemType));
            var entry = new EquippedEquipmentEntry
            {
                itemType = (int)itemType,
                remainingCharges = Mathf.Max(1, charges),
                componentId = default
            };
            equippedEquipment.Add(entry);
            equippedEquipmentEntries.Add(entry);
            return true;
        }

        /// <summary>Server-only: add a ship-family component to the first available equipment slot.</summary>
        public bool AddComponentEquipmentFromServer(string componentId)
        {
            if (!IsServer || string.IsNullOrWhiteSpace(componentId)) return false;
            if (HasComponentEquipped(componentId)) return false;
            if (equippedEquipment == null) equippedEquipment = new List<EquippedEquipmentEntry>();
            if (equippedEquipmentEntries == null) return false;
            if (equippedEquipment.Count >= EquipmentSlotCount) return false;

            string trimmedId = componentId.Trim();
            var entry = new EquippedEquipmentEntry
            {
                itemType = (int)StoreItemType.ShipComponent,
                remainingCharges = 1,
                componentId = new Unity.Collections.FixedString64Bytes(trimmedId)
            };
            int slotIndex = equippedEquipment.Count;
            equippedEquipment.Add(entry);
            ApplyDefaultPlacementForNewComponent(slotIndex, trimmedId);
            equippedEquipmentEntries.Add(equippedEquipment[slotIndex]);
            RecalculateEquippedComponentStatSum();
            ClampCarriedResourcesToCapacity();
            return true;
        }

        /// <summary>Server-only: add a ship-family component with saved placement (map restore).</summary>
        public bool AddComponentEquipmentFromServerWithPlacement(string componentId, Vector3 localPosition, Vector3 localEuler)
        {
            if (!IsServer || string.IsNullOrWhiteSpace(componentId)) return false;
            if (HasComponentEquipped(componentId)) return false;
            if (equippedEquipment == null) equippedEquipment = new List<EquippedEquipmentEntry>();
            if (equippedEquipmentEntries == null) return false;
            if (equippedEquipment.Count >= EquipmentSlotCount) return false;

            string trimmedId = componentId.Trim();
            var entry = new EquippedEquipmentEntry
            {
                itemType = (int)StoreItemType.ShipComponent,
                remainingCharges = 1,
                componentId = new Unity.Collections.FixedString64Bytes(trimmedId)
            };
            EquippedComponentPlacementUtility.ApplyPlacementToEntry(ref entry, localPosition, Quaternion.Euler(localEuler));
            equippedEquipment.Add(entry);
            equippedEquipmentEntries.Add(entry);
            RecalculateEquippedComponentStatSum();
            ClampCarriedResourcesToCapacity();
            return true;
        }

        [ServerRpc(RequireOwnership = true)]
        public void UpdateEquippedComponentPlacementServerRpc(int slotIndex, float posX, float posY, float posZ, float rotX, float rotY, float rotZ)
        {
            if (!IsServer || equippedEquipment == null || equippedEquipmentEntries == null) return;
            if (slotIndex < 0 || slotIndex >= equippedEquipment.Count) return;

            EquippedEquipmentEntry entry = equippedEquipment[slotIndex];
            if (!entry.IsShipComponent) return;

            entry.localPosX = posX;
            entry.localPosY = posY;
            entry.localPosZ = posZ;
            Vector3 snappedEuler = EquippedComponentPlacementUtility.SnapEulerAngles(new Vector3(rotX, rotY, rotZ));
            entry.localRotX = snappedEuler.x;
            entry.localRotY = snappedEuler.y;
            entry.localRotZ = snappedEuler.z;
            equippedEquipment[slotIndex] = entry;
            if (slotIndex < equippedEquipmentEntries.Count)
                equippedEquipmentEntries[slotIndex] = entry;

            RebuildEquippedComponentVisuals();
        }

        private void SubscribeEquippedEquipmentVisuals()
        {
            if (_subscribedEquippedEquipmentVisuals || equippedEquipmentEntries == null)
                return;
            equippedEquipmentEntries.OnListChanged += OnEquippedEquipmentListChanged;
            _subscribedEquippedEquipmentVisuals = true;
        }

        private void UnsubscribeEquippedEquipmentVisuals()
        {
            if (!_subscribedEquippedEquipmentVisuals || equippedEquipmentEntries == null)
                return;
            equippedEquipmentEntries.OnListChanged -= OnEquippedEquipmentListChanged;
            _subscribedEquippedEquipmentVisuals = false;
        }

        private void OnEquippedEquipmentListChanged(NetworkListEvent<EquippedEquipmentEntry> changeEvent)
        {
            if (_equipmentVisualRebuildSuppressDepth > 0)
                return;

            ApplyLocalChassisLayoutsForEquippedComponents();
            RebuildEquippedComponentVisuals();
        }

        public void RebuildEquippedComponentVisuals()
        {
            Transform root = GetCardVisualRoot();
            if (root == null || lastVisualApplyPrefab == null)
                return;

            ShipFamilyDefinition family = currentVisualFamilyDefinition ?? ResolveFamilyForEquipment();
            EquippedComponentVisualBuilder.RebuildAll(
                this,
                root,
                lastVisualApplyPrefab,
                family,
                GetEquippedEquipmentForDisplay());
        }

        private void ApplyDefaultPlacementForNewComponent(int slotIndex, string componentId)
        {
            if (equippedEquipment == null || slotIndex < 0 || slotIndex >= equippedEquipment.Count)
                return;

            string partType = EquippedComponentPlacementUtility.ResolvePartType(componentId);
            SynchronizeEquipmentLayoutForPartType(partType);
        }

        private void SynchronizeEquipmentLayoutForPartType(string partType)
        {
            if (string.IsNullOrEmpty(partType))
                return;

            _equipmentVisualRebuildSuppressDepth++;
            try
            {
                ApplyEquipmentLayoutForPartType(partType, writeEquippedEntries: IsServer);
            }
            finally
            {
                _equipmentVisualRebuildSuppressDepth--;
            }
        }

        private void ApplyLocalChassisLayoutsForEquippedComponents()
        {
            var partTypes = CollectEquippedComponentPartTypes();
            for (int i = 0; i < partTypes.Count; i++)
                ApplyEquipmentLayoutForPartType(partTypes[i], writeEquippedEntries: false);

            RestoreAuthoredChassisForUnusedPartTypes(partTypes);
        }

        private List<string> CollectEquippedComponentPartTypes()
        {
            var types = new List<string>();
            IReadOnlyList<EquippedEquipmentEntry> equipment = GetEquippedEquipmentForDisplay();
            if (equipment == null)
                return types;

            for (int i = 0; i < equipment.Count; i++)
            {
                if (!equipment[i].IsShipComponent)
                    continue;
                string partType = EquippedComponentPlacementUtility.ResolvePartType(equipment[i].ComponentId);
                if (string.IsNullOrEmpty(partType))
                    continue;
                bool exists = false;
                for (int t = 0; t < types.Count; t++)
                {
                    if (string.Equals(types[t], partType, System.StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                    types.Add(partType);
            }
            return types;
        }

        private void RestoreAuthoredChassisForUnusedPartTypes(List<string> activePartTypes)
        {
            RestoreAuthoredIfInactive("Wing", activePartTypes);
            RestoreAuthoredIfInactive("Weapon", activePartTypes);
            RestoreAuthoredIfInactive("Cockpit", activePartTypes);
            RestoreAuthoredIfInactive("Part", activePartTypes);
        }

        private void RestoreAuthoredIfInactive(string partType, List<string> activePartTypes)
        {
            for (int i = 0; i < activePartTypes.Count; i++)
            {
                if (string.Equals(activePartTypes[i], partType, System.StringComparison.OrdinalIgnoreCase))
                    return;
            }
            RestoreAuthoredChassisPlacements(partType);
        }

        private void ApplyEquipmentLayoutForPartType(string partType, bool writeEquippedEntries)
        {
            if (equippedEquipment == null || string.IsNullOrEmpty(partType))
                return;

            int chassisCount = CountChassisOfPartType(partType);
            var equippedSlots = new List<int>();
            IReadOnlyList<EquippedEquipmentEntry> source = writeEquippedEntries
                ? equippedEquipment
                : GetEquippedEquipmentForDisplay();

            if (source == null)
                return;

            for (int i = 0; i < source.Count; i++)
            {
                if (!source[i].IsShipComponent)
                    continue;
                string t = EquippedComponentPlacementUtility.ResolvePartType(source[i].ComponentId);
                if (string.Equals(t, partType, System.StringComparison.OrdinalIgnoreCase))
                    equippedSlots.Add(i);
            }

            if (equippedSlots.Count == 0)
            {
                RestoreAuthoredChassisPlacements(partType);
                return;
            }

            int totalCount = chassisCount + equippedSlots.Count;
            if (totalCount <= 0)
                return;

            var reference = BuildPlacementReference(partType);
            var positions = new List<Vector3>();
            var rotations = new List<Quaternion>();
            EquippedComponentPlacementUtility.ComputeAllPlacementsForType(
                partType, totalCount, in reference, positions, rotations);

            ApplyChassisPlacementsForPartType(partType, positions, rotations, chassisCount);

            for (int e = 0; e < equippedSlots.Count; e++)
            {
                int slot = equippedSlots[e];
                int placementIndex = chassisCount + e;
                if (placementIndex >= positions.Count)
                    continue;

                if (!writeEquippedEntries)
                    continue;

                EquippedEquipmentEntry entry = equippedEquipment[slot];
                EquippedComponentPlacementUtility.ApplyPlacementToEntry(
                    ref entry,
                    positions[placementIndex],
                    rotations[placementIndex]);
                equippedEquipment[slot] = entry;
                if (equippedEquipmentEntries != null && slot < equippedEquipmentEntries.Count)
                    equippedEquipmentEntries[slot] = entry;
            }
        }

        private void RestoreAuthoredChassisPlacements(string partType)
        {
            if (string.Equals(partType, "Wing", System.StringComparison.OrdinalIgnoreCase))
            {
                RestoreAuthoredPlacementList(wingScaleTransforms, wingBasePositions, _authoredWingPositions, _authoredWingRotations);
                return;
            }

            if (string.Equals(partType, "Weapon", System.StringComparison.OrdinalIgnoreCase))
            {
                RestoreAuthoredPlacementList(weaponScaleTransforms, weaponBasePositions, _authoredWeaponPositions, _authoredWeaponRotations);
                return;
            }

            if (string.Equals(partType, "Cockpit", System.StringComparison.OrdinalIgnoreCase))
            {
                RestoreAuthoredCockpitPlacements();
                return;
            }

            if (string.Equals(partType, "Part", System.StringComparison.OrdinalIgnoreCase))
                RestoreAuthoredPlacementList(partScaleTransforms, partBasePositions, _authoredPartPositions, _authoredPartRotations);
        }

        private void RestoreAuthoredCockpitPlacements()
        {
            if (cockpitScaleTransforms == null || cockpitBasePositions == null)
                return;

            int authoredIndex = 0;
            for (int i = 0; i < cockpitScaleTransforms.Count; i++)
            {
                Transform t = cockpitScaleTransforms[i];
                if (t == null || t.name == "Hull")
                    continue;
                if (authoredIndex >= _authoredCockpitPositions.Count)
                    break;

                Vector3 pos = _authoredCockpitPositions[authoredIndex];
                t.localPosition = pos;
                if (i < cockpitBasePositions.Count)
                    cockpitBasePositions[i] = pos;
                if (authoredIndex < _authoredCockpitRotations.Count)
                    t.localRotation = _authoredCockpitRotations[authoredIndex];
                authoredIndex++;
            }
        }

        private static void RestoreAuthoredPlacementList(
            List<Transform> transforms,
            List<Vector3> basePositions,
            List<Vector3> authoredPositions,
            List<Quaternion> authoredRotations)
        {
            if (transforms == null || basePositions == null || authoredPositions == null)
                return;

            int applyCount = Mathf.Min(transforms.Count, authoredPositions.Count);
            for (int i = 0; i < applyCount; i++)
            {
                Transform t = transforms[i];
                if (t == null)
                    continue;

                Vector3 pos = authoredPositions[i];
                t.localPosition = pos;
                if (i < basePositions.Count)
                    basePositions[i] = pos;
                if (authoredRotations != null && i < authoredRotations.Count)
                    t.localRotation = authoredRotations[i];
            }
        }

        private EquippedComponentPlacementUtility.PlacementReference BuildPlacementReference(string partType)
        {
            var reference = new EquippedComponentPlacementUtility.PlacementReference
            {
                positions = new List<Vector3>(),
                rotations = new List<Quaternion>()
            };

            CollectAuthoredReferenceForPartType(partType, "Wing", _authoredWingPositions, _authoredWingRotations, reference);
            CollectAuthoredReferenceForPartType(partType, "Weapon", _authoredWeaponPositions, _authoredWeaponRotations, reference);
            CollectAuthoredReferenceForPartType(partType, "Cockpit", _authoredCockpitPositions, _authoredCockpitRotations, reference);
            CollectAuthoredReferenceForPartType(partType, "Part", _authoredPartPositions, _authoredPartRotations, reference);

            if (reference.positions.Count == 0 &&
                (string.Equals(partType, "Tail", System.StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(partType, "Fin", System.StringComparison.OrdinalIgnoreCase)))
            {
                CollectRearReferenceFromVisualRoot(reference);
            }

            if (reference.positions.Count == 0 && engineBasePositions != null && engineBasePositions.Count > 0)
            {
                for (int i = 0; i < engineBasePositions.Count; i++)
                    reference.positions.Add(engineBasePositions[i]);
            }

            return reference;
        }

        private static void CollectAuthoredReferenceForPartType(
            string targetPartType,
            string listPartType,
            List<Vector3> authoredPositions,
            List<Quaternion> authoredRotations,
            EquippedComponentPlacementUtility.PlacementReference reference)
        {
            if (!string.Equals(targetPartType, listPartType, System.StringComparison.OrdinalIgnoreCase))
                return;
            if (authoredPositions == null || authoredPositions.Count == 0)
                return;

            for (int i = 0; i < authoredPositions.Count; i++)
            {
                reference.positions.Add(authoredPositions[i]);
                if (authoredRotations != null && i < authoredRotations.Count)
                    reference.rotations.Add(authoredRotations[i]);
                else
                    reference.rotations.Add(Quaternion.identity);
            }
        }

        private void CollectRearReferenceFromVisualRoot(EquippedComponentPlacementUtility.PlacementReference reference)
        {
            Transform root = GetCardVisualRoot();
            if (root == null)
                return;

            float minZ = 0f;
            bool found = false;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child == null || child.name.StartsWith(EquippedComponentVisualBuilder.VisualNamePrefix))
                    continue;

                string lower = child.name.ToLowerInvariant();
                if (lower.IndexOf("_tail", System.StringComparison.Ordinal) >= 0 ||
                    lower.IndexOf("_fin", System.StringComparison.Ordinal) >= 0 ||
                    lower.IndexOf("_engine", System.StringComparison.Ordinal) >= 0)
                {
                    if (!found || child.localPosition.z < minZ)
                    {
                        minZ = child.localPosition.z;
                        found = true;
                    }
                    reference.positions.Add(child.localPosition);
                    reference.rotations.Add(child.localRotation);
                }
            }

            if (!found)
                reference.positions.Add(new Vector3(0f, 0f, -EquippedComponentPlacementUtility.DefaultRearOffset));
        }

        private int CountChassisOfPartType(string partType)
        {
            if (string.Equals(partType, "Wing", System.StringComparison.OrdinalIgnoreCase))
                return wingScaleTransforms != null ? wingScaleTransforms.Count : 0;
            if (string.Equals(partType, "Weapon", System.StringComparison.OrdinalIgnoreCase))
                return weaponScaleTransforms != null ? weaponScaleTransforms.Count : 0;
            if (string.Equals(partType, "Cockpit", System.StringComparison.OrdinalIgnoreCase))
            {
                int count = 0;
                if (cockpitScaleTransforms != null)
                {
                    for (int i = 0; i < cockpitScaleTransforms.Count; i++)
                    {
                        Transform t = cockpitScaleTransforms[i];
                        if (t != null && t.name != "Hull")
                            count++;
                    }
                }
                return count;
            }
            if (string.Equals(partType, "Tail", System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(partType, "Fin", System.StringComparison.OrdinalIgnoreCase))
                return CountRearChassisParts(partType);
            if (string.Equals(partType, "Part", System.StringComparison.OrdinalIgnoreCase))
                return partScaleTransforms != null ? partScaleTransforms.Count : 0;
            return 0;
        }

        private int CountRearChassisParts(string partType)
        {
            Transform root = GetCardVisualRoot();
            if (root == null)
                return 0;

            int count = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child == null || child.name.StartsWith(EquippedComponentVisualBuilder.VisualNamePrefix))
                    continue;

                string lower = child.name.ToLowerInvariant();
                bool isTail = lower.IndexOf("_tail", System.StringComparison.Ordinal) >= 0;
                bool isFin = lower.IndexOf("_fin", System.StringComparison.Ordinal) >= 0;
                if (string.Equals(partType, "Tail", System.StringComparison.OrdinalIgnoreCase) && isTail)
                    count++;
                else if (string.Equals(partType, "Fin", System.StringComparison.OrdinalIgnoreCase) && isFin)
                    count++;
            }
            return count;
        }

        private void ApplyChassisPlacementsForPartType(
            string partType,
            List<Vector3> positions,
            List<Quaternion> rotations,
            int chassisCount)
        {
            if (positions == null || chassisCount <= 0)
                return;

            if (string.Equals(partType, "Wing", System.StringComparison.OrdinalIgnoreCase))
            {
                ApplyChassisPlacementList(wingScaleTransforms, wingBasePositions, positions, rotations, chassisCount);
                return;
            }

            if (string.Equals(partType, "Weapon", System.StringComparison.OrdinalIgnoreCase))
            {
                ApplyChassisPlacementList(weaponScaleTransforms, weaponBasePositions, positions, rotations, chassisCount);
                return;
            }

            if (string.Equals(partType, "Cockpit", System.StringComparison.OrdinalIgnoreCase))
            {
                ApplyChassisCockpitPlacements(positions, rotations, chassisCount);
                return;
            }

            if (string.Equals(partType, "Part", System.StringComparison.OrdinalIgnoreCase))
                ApplyChassisPlacementList(partScaleTransforms, partBasePositions, positions, rotations, chassisCount);
        }

        private void ApplyChassisCockpitPlacements(List<Vector3> positions, List<Quaternion> rotations, int chassisCount)
        {
            if (cockpitScaleTransforms == null || cockpitBasePositions == null || positions == null)
                return;

            int cockpitIndex = 0;
            int applyCount = Mathf.Min(chassisCount, positions.Count);
            for (int i = 0; i < cockpitScaleTransforms.Count && cockpitIndex < applyCount; i++)
            {
                Transform t = cockpitScaleTransforms[i];
                if (t == null || t.name == "Hull")
                    continue;

                Vector3 pos = positions[cockpitIndex];
                t.localPosition = pos;
                if (i < cockpitBasePositions.Count)
                    cockpitBasePositions[i] = pos;
                if (rotations != null && cockpitIndex < rotations.Count)
                    t.localRotation = rotations[cockpitIndex];
                cockpitIndex++;
            }
        }

        private static void ApplyChassisPlacementList(
            List<Transform> transforms,
            List<Vector3> basePositions,
            List<Vector3> positions,
            List<Quaternion> rotations,
            int count)
        {
            if (transforms == null || basePositions == null)
                return;

            int applyCount = Mathf.Min(count, transforms.Count, positions.Count);
            for (int i = 0; i < applyCount; i++)
            {
                Transform t = transforms[i];
                if (t == null)
                    continue;

                Vector3 pos = positions[i];
                t.localPosition = pos;
                if (i < basePositions.Count)
                    basePositions[i] = pos;

                if (rotations != null && i < rotations.Count)
                    t.localRotation = rotations[i];
            }
        }

        public bool HasComponentEquipped(string componentId)
        {
            if (string.IsNullOrWhiteSpace(componentId) || equippedEquipment == null)
                return false;
            string id = componentId.Trim();
            for (int i = 0; i < equippedEquipment.Count; i++)
            {
                if (!equippedEquipment[i].IsShipComponent) continue;
                if (string.Equals(equippedEquipment[i].ComponentId, id, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void RecalculateEquippedComponentStatSum()
        {
            _equippedComponentStatSum = default;
            if (equippedEquipment == null || equippedEquipment.Count == 0)
                return;

            ShipFamilyDefinition family = ResolveFamilyForEquipment();
            if (family == null)
                return;

            int level = ShipLevel;
            for (int i = 0; i < equippedEquipment.Count; i++)
            {
                EquippedEquipmentEntry entry = equippedEquipment[i];
                if (!entry.IsShipComponent) continue;
                if (!family.TryGetComponentEntry(entry.ComponentId, out ShipFamilyComponentEntry componentEntry) || componentEntry == null)
                    continue;
                ShipComponentAbilityStats effective = ShipComponentStoreData.GetEffectiveStatsAtShipLevel(componentEntry.stats, level);
                _equippedComponentStatSum.AddInPlace(effective);
            }
        }

        private ShipFamilyDefinition ResolveFamilyForEquipment()
        {
            if (Systems.CardShopSystem.Instance == null)
                return null;
            return Systems.CardShopSystem.Instance.GetShipFamilyForShip(this);
        }

        /// <summary>Server-only: remove equipment at slot index.</summary>
        public void RemoveEquipmentFromServer(int slotIndex, bool skipDroneDespawn = false)
        {
            if (!IsServer) return;
            if (equippedEquipment == null) return;
            if (slotIndex < 0 || slotIndex >= equippedEquipment.Count) return;

            EquippedEquipmentEntry removed = equippedEquipment[slotIndex];
            bool removedComponent = removed.IsShipComponent;
            string removedPartType = removedComponent
                ? EquippedComponentPlacementUtility.ResolvePartType(removed.ComponentId)
                : null;

            _equipmentVisualRebuildSuppressDepth++;
            try
            {
                equippedEquipment.RemoveAt(slotIndex);
                if (equippedEquipmentEntries != null && slotIndex < equippedEquipmentEntries.Count)
                    equippedEquipmentEntries.RemoveAt(slotIndex);

                if (removedComponent && !string.IsNullOrEmpty(removedPartType))
                    SynchronizeEquipmentLayoutForPartType(removedPartType);
            }
            finally
            {
                _equipmentVisualRebuildSuppressDepth--;
            }

            ApplyLocalChassisLayoutsForEquippedComponents();
            RebuildEquippedComponentVisuals();

            RecalculateEquippedComponentStatSum();
            ClampCarriedResourcesToCapacity();
        }

        /// <summary>Server: drone in an equipment slot was destroyed — clear the slot.</summary>
        public void NotifyEquipmentDroneDestroyed(int slotIndex)
        {
            if (!IsServer) return;
            if (equippedEquipment == null || slotIndex < 0 || slotIndex >= equippedEquipment.Count) return;
            var entry = equippedEquipment[slotIndex];
            if (!StoreItemData.IsDrone(entry.ItemType)) return;
            RemoveEquipmentFromServer(slotIndex);
        }

        [ServerRpc(RequireOwnership = true)]
        public void RemoveEquipmentServerRpc(int slotIndex)
        {
            RemoveEquipmentFromServer(slotIndex);
        }

        /// <summary>Server: remove drone rows from equipment on death. Ship components stay equipped.</summary>
        public void StripDroneEquipmentFromServer()
        {
            if (!IsServer || equippedEquipment == null || equippedEquipmentEntries == null) return;
            for (int i = equippedEquipment.Count - 1; i >= 0; i--)
            {
                if (!StoreItemData.IsDrone(equippedEquipment[i].ItemType)) continue;
                equippedEquipment.RemoveAt(i);
                if (i < equippedEquipmentEntries.Count)
                    equippedEquipmentEntries.RemoveAt(i);
            }
        }

        /// <summary>Server: apply bullet damage to a drone equipment slot (HP stored in remainingCharges).</summary>
        public void ApplyDroneSlotDamage(int slotIndex, float damage, TeamManager.Team attackerTeam, ulong attackerShipNetworkId)
        {
            if (!IsServer || equippedEquipment == null || equippedEquipmentEntries == null) return;
            if (slotIndex < 0 || slotIndex >= equippedEquipment.Count) return;

            var entry = equippedEquipment[slotIndex];
            if (!StoreItemData.IsDrone(entry.ItemType)) return;
            if (attackerTeam == shipTeam.Value) return;

            int previousHp = entry.remainingCharges;
            entry.remainingCharges = Mathf.Max(0, entry.remainingCharges - Mathf.RoundToInt(damage));
            equippedEquipment[slotIndex] = entry;
            if (slotIndex < equippedEquipmentEntries.Count)
                equippedEquipmentEntries[slotIndex] = entry;

            if (previousHp > 0 && entry.remainingCharges <= 0 && attackerShipNetworkId != 0 && ScoreSystem.Instance != null)
            {
                var spawnManager = NetworkManager.Singleton != null ? NetworkManager.Singleton.SpawnManager : null;
                if (spawnManager != null && spawnManager.SpawnedObjects.TryGetValue(attackerShipNetworkId, out NetworkObject attackerObj))
                {
                    Starship attackerShip = attackerObj != null ? attackerObj.GetComponent<Starship>() : null;
                    if (attackerShip != null)
                        ScoreSystem.Instance.AwardEnemyKill(attackerShip);
                }
                NotifyEquipmentDroneDestroyed(slotIndex);
            }
        }

        private bool TryConsumeFromEquipment(StoreItemType itemType)
        {
            if (!IsServer || equippedEquipment == null) return false;
            for (int i = 0; i < equippedEquipment.Count; i++)
            {
                EquippedEquipmentEntry entry = equippedEquipment[i];
                if (entry.ItemType != itemType || entry.remainingCharges <= 0)
                    continue;

                entry.remainingCharges--;
                if (entry.remainingCharges <= 0)
                {
                    RemoveEquipmentFromServer(i);
                }
                else
                {
                    equippedEquipment[i] = entry;
                    if (equippedEquipmentEntries != null && i < equippedEquipmentEntries.Count)
                        equippedEquipmentEntries[i] = entry;
                }
                return true;
            }
            return false;
        }

        private int[] CaptureEquipmentItemTypes()
        {
            if (equippedEquipment == null || equippedEquipment.Count == 0)
                return System.Array.Empty<int>();
            var types = new int[equippedEquipment.Count];
            for (int i = 0; i < equippedEquipment.Count; i++)
                types[i] = equippedEquipment[i].itemType;
            return types;
        }

        private int[] CaptureEquipmentCharges()
        {
            if (equippedEquipment == null || equippedEquipment.Count == 0)
                return System.Array.Empty<int>();
            var charges = new int[equippedEquipment.Count];
            for (int i = 0; i < equippedEquipment.Count; i++)
                charges[i] = equippedEquipment[i].remainingCharges;
            return charges;
        }

        private string[] CaptureEquipmentComponentIds()
        {
            if (equippedEquipment == null || equippedEquipment.Count == 0)
                return System.Array.Empty<string>();
            var ids = new string[equippedEquipment.Count];
            for (int i = 0; i < equippedEquipment.Count; i++)
                ids[i] = equippedEquipment[i].IsShipComponent ? equippedEquipment[i].ComponentId : string.Empty;
            return ids;
        }

        private float[] CaptureEquipmentPlacement()
        {
            if (equippedEquipment == null || equippedEquipment.Count == 0)
                return System.Array.Empty<float>();
            var placement = new float[equippedEquipment.Count * 6];
            for (int i = 0; i < equippedEquipment.Count; i++)
            {
                EquippedEquipmentEntry entry = equippedEquipment[i];
                int o = i * 6;
                placement[o] = entry.localPosX;
                placement[o + 1] = entry.localPosY;
                placement[o + 2] = entry.localPosZ;
                placement[o + 3] = entry.localRotX;
                placement[o + 4] = entry.localRotY;
                placement[o + 5] = entry.localRotZ;
            }
            return placement;
        }

        private static bool TryReadEquipmentPlacement(float[] placement, int slotIndex, out Vector3 localPosition, out Vector3 localEuler)
        {
            localPosition = Vector3.zero;
            localEuler = Vector3.zero;
            if (placement == null || slotIndex < 0)
                return false;

            int o = slotIndex * 6;
            if (placement.Length < o + 6)
                return false;

            localPosition = new Vector3(placement[o], placement[o + 1], placement[o + 2]);
            localEuler = new Vector3(placement[o + 3], placement[o + 4], placement[o + 5]);
            return EquippedComponentPlacementUtility.HasPlacement(new EquippedEquipmentEntry
            {
                localPosX = placement[o],
                localPosY = placement[o + 1],
                localPosZ = placement[o + 2],
                localRotX = placement[o + 3],
                localRotY = placement[o + 4],
                localRotZ = placement[o + 5]
            });
        }

        private void RestoreEquipmentFromSnapshot(in PlayerShipProgressSnapshot snapshot)
        {
            if (snapshot.EquipmentItemTypes != null && snapshot.EquipmentItemTypes.Length > 0)
            {
                int count = snapshot.EquipmentItemTypes.Length;
                for (int i = 0; i < count; i++)
                {
                    if (equippedEquipment.Count >= EquipmentSlotCount) break;
                    int charges = snapshot.EquipmentCharges != null && i < snapshot.EquipmentCharges.Length
                        ? snapshot.EquipmentCharges[i]
                        : 1;
                    var itemType = (StoreItemType)snapshot.EquipmentItemTypes[i];
                    if (StoreItemData.IsShipComponent(itemType))
                    {
                        string componentId = snapshot.EquipmentComponentIds != null && i < snapshot.EquipmentComponentIds.Length
                            ? snapshot.EquipmentComponentIds[i]
                            : null;
                        if (!string.IsNullOrWhiteSpace(componentId))
                        {
                            if (TryReadEquipmentPlacement(snapshot.EquipmentPlacement, i, out Vector3 pos, out Vector3 euler))
                                AddComponentEquipmentFromServerWithPlacement(componentId, pos, euler);
                            else
                                AddComponentEquipmentFromServer(componentId);
                        }
                    }
                    else
                    {
                        if (StoreItemData.IsDrone(itemType) && charges <= 1)
                            charges = StoreItemData.GetDroneMaxHp(itemType);
                        AddEquipmentFromServer(itemType, charges);
                    }
                }
                RecalculateEquippedComponentStatSum();
                RebuildEquippedComponentVisuals();
                return;
            }

            TryAddLegacyInventoryAsEquipment(StoreItemType.SmallRockets, snapshot.SmallRockets);
            TryAddLegacyInventoryAsEquipment(StoreItemType.LargeRockets, snapshot.LargeRockets);
            TryAddLegacyInventoryAsEquipment(StoreItemType.SmallMines, snapshot.SmallMines);
            TryAddLegacyInventoryAsEquipment(StoreItemType.LargeMines, snapshot.LargeMines);
        }

        private void TryAddLegacyInventoryAsEquipment(StoreItemType itemType, int legacyCount)
        {
            if (legacyCount <= 0) return;
            if (equippedEquipment != null && equippedEquipment.Count >= EquipmentSlotCount) return;
            AddEquipmentFromServer(itemType, legacyCount);
        }

        private void ClearAllEquipmentFromServer()
        {
            if (!IsServer || equippedEquipment == null) return;
            for (int i = equippedEquipment.Count - 1; i >= 0; i--)
                RemoveEquipmentFromServer(i);
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
