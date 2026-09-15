using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.Input;
using TitanOrbit.Shared;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Serialization;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Top-down gameplay camera that follows the local ship using NetCode presentation pose.
    /// [HYBRID] Reads <see cref="ShipDisplayPose"/> (filled by <see cref="ShipVisualSyncSystem"/>),
    /// never drives ship sim. Client only; execution order 67001 so it runs after presentation sync.
    /// <para>
    /// When the ship wraps, this camera jumps the same delta the same frame and draws a short
    /// fade (<see cref="MapWrapTransition"/>) so the world pop is a beat, not a streak.
    /// [TITAN-ORBIT] Framing knobs live on a <see cref="CameraFollowSettings"/> ScriptableObject.
    /// Each <see cref="ShipFamilyDefinition"/> can point at its own profile; this component watches
    /// the local ship's ghosted <c>ShipFamilyConfigIndex</c> and calls <see cref="SetSettings"/> when
    /// the family changes (team spawn or moon-dock purchase). The camera hard-locks to the ship,
    /// then adds a gently smoothed look-ahead on XZ and a smoothly eased height zoom from ship level.
    /// Family <see cref="ShipFamilySpecialBonuses.cameraHeightMul"/> scales that height (zoom out / in)
    /// without needing a unique CameraFollowSettings asset. MEGA hulls skip the family mul — the
    /// MEGA catalog owns framing. <see cref="CurrentHeightZoomFactor"/> exposes that zoom proportion
    /// for the collapsed minimap (<see cref="TitanOrbit.UI.MinimapController"/>) so both views stay in sync.
    /// During gem Instantiates (<see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/>) look-ahead
    /// freezes, ship level holds last-good, and MEGA vs family camera stays latched — avoids false
    /// zoom when MEGA plow Instantiates gem ghosts.
    /// Ship flight smoothing stays owned by NetCode — we only SmoothDamp camera composition.
    /// </para>
    /// Moon-dock cinematic overrides the follow target with a hard lock on the spinning hull.
    /// Planetary defense turret possession follows the pad and zooms out so the pad's
    /// engage/bullet range fits on screen (<see cref="PlanetaryDefenseTurretClientState.DesiredViewRadiusWorld"/>).
    /// <para>
    /// Idle theatrical mode: after the local ship sits still in space (no thrust / fire /
    /// hull turn, low drift) the camera leaves top-down follow and rides a random
    /// Catmull-Rom orbit from <see cref="CameraTheatricalOrbit"/>. Thrust exits; mouse
    /// aim does not. Never starts on a gem-moon landing. Gameplay height / look-ahead
    /// keep ticking so exit has a live target. Client only.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(67001)]
    public class CameraFollowEcs : MonoBehaviour
    {
        [Header("Profile")]
        [Tooltip(
            "Fallback camera follow profile when the local ship's family has no Camera Follow Settings assigned. " +
            "Usually DefaultCameraFollowSettings. Family-specific profiles on ShipFamilyDefinition override this " +
            "automatically when ShipFamilyConfigIndex changes.")]
        [FormerlySerializedAs("settings")]
        [SerializeField] CameraFollowSettings defaultSettings;

        [Header("Theatrical Idle Camera")]
        [Tooltip("When the local player is idle in space (no thrust, fire, or hull turn), orbit the ship.")]
        [SerializeField] bool theatricalModeEnabled = true;

        [Tooltip("Seconds of true idle (no thrust / fire / turn, low drift) before theatrical mode begins.")]
        [Min(0.5f)]
        [SerializeField] float theatricalIdleDurationSeconds = 3f;

        [Tooltip("Planar speed (m/s) at or below this counts as not moving while inputs are released.")]
        [Min(0f)]
        [SerializeField] float theatricalIdleMaxPlanarSpeed = 0.35f;

        [Tooltip("Yaw rate (deg/s) above this counts as still turning — countdown does not start.")]
        [Min(0.5f)]
        [SerializeField] float theatricalIdleMaxYawDegPerSec = 10f;

        [Tooltip("Aim vs hull yaw (degrees) above this counts as still turning toward the cursor.")]
        [Min(0.1f)]
        [SerializeField] float theatricalAimAlignmentToleranceDeg = 2.5f;

        [Tooltip("Seconds to blend from gameplay top-down into the moving orbit.")]
        [Min(0.05f)]
        [SerializeField] float theatricalEnterBlendDuration = 1.25f;

        [Tooltip("Seconds to blend back to the normal top-down follow when the player acts.")]
        [Min(0.05f)]
        [SerializeField] float theatricalExitBlendDuration = 2f;

        [Tooltip("Look-at focus smoothing while orbiting (higher = slower, more cinematic).")]
        [Min(0.05f)]
        [SerializeField] float theatricalLookSmoothTime = 0.22f;

        [Tooltip("Rotation smoothing while orbiting (higher = slower).")]
        [Min(0.05f)]
        [SerializeField] float theatricalRotationSmoothTime = 0.28f;

        [Tooltip("FOV zoom smoothing while orbiting (higher = slower).")]
        [Min(0.05f)]
        [SerializeField] float theatricalFovSmoothTime = 0.55f;

        [Tooltip("Closed-path waypoint count (regenerated each loop for variety).")]
        [Range(5, 14)]
        [SerializeField] int theatricalWaypointCount = 8;

        [Tooltip("Min elevation in degrees relative to the ship focus (− = below horizon).")]
        [SerializeField] float theatricalMinElevationDeg = -32f;

        [Tooltip("Max elevation in degrees relative to the ship focus.")]
        [SerializeField] float theatricalMaxElevationDeg = 52f;

        [Tooltip("Near-orbit standoff as a multiple of ship visual size.")]
        [SerializeField] float theatricalRadiusMinMultiplier = 2.8f;

        [Tooltip("Far-orbit standoff as a multiple of ship visual size. Higher = more zoom-out on the far legs.")]
        [SerializeField] float theatricalRadiusMaxMultiplier = 9.5f;

        [Tooltip("Seconds for one full orbit around the ship (left / right / above / below).")]
        [Min(8f)]
        [SerializeField] float theatricalPathDurationMinSeconds = 22f;

        [Tooltip("Upper bound for one full orbit loop.")]
        [Min(8f)]
        [SerializeField] float theatricalPathDurationMaxSeconds = 36f;

        [Tooltip("Perspective FOV at closest orbit (zoomed in).")]
        [Range(18f, 55f)]
        [SerializeField] float theatricalFovMin = 26f;

        [Tooltip("Perspective FOV at farthest orbit (zoomed out).")]
        [Range(18f, 80f)]
        [SerializeField] float theatricalFovMax = 64f;

        /// <summary>Gameplay follow camera — used by bullet tracers to stay readable at MEGA height.</summary>
        public static CameraFollowEcs Instance { get; private set; }

        /// <summary>
        /// True while idle theatrical orbit is running or blending in.
        /// Planet / moon world labels read this so they can face the tilted camera.
        /// </summary>
        public bool IsTheatricalEngaged => _theatricalModeActive && !_theatricalReturning;

        /// <summary>
        /// True for the whole cinematic take, including the exit blend back to top-down.
        /// Distant planet rings stay on until the lens is gameplay-follow again.
        /// </summary>
        public bool IsTheatricalPresentationActive => _theatricalModeActive || _theatricalReturning;

        /// <summary>Ends theatrical orbit and blends back to gameplay follow.</summary>
        public void TriggerTheatricalReturn() => BeginTheatricalReturn();

        /// <summary>Last MEGA hull-top Y in display space (0 when the local ship is not a MEGA).</summary>
        public float MegaHullTopDisplayY { get; private set; }

        /// <summary>
        /// Runtime-active profile (family override or <see cref="defaultSettings"/>).
        /// Null-safe: falls back to an in-memory default matching ScriptableObject field defaults.
        /// </summary>
        public CameraFollowSettings Settings
        {
            get
            {
                if (_activeSettings != null)
                    return _activeSettings;
                if (defaultSettings != null)
                    return defaultSettings;
                return FallbackSettings;
            }
        }

        /// <summary>
        /// Current smoothed world-Y height above the ship (after SmoothDamp).
        /// Before the first follow lock, returns the profile target for the last-known ship level.
        /// [TITAN-ORBIT] Minimap collapsed zoom reads this so the circle radius tracks the live camera zoom.
        /// </summary>
        public float CurrentHeight
        {
            get
            {
                if (_initialized && _currentHeight > 0.01f)
                    return _currentHeight;
                return ApplyFamilyCameraHeightMul(
                    Settings.ComputeTargetHeight(Mathf.Max(1, _lastKnownShipLevel)));
            }
        }

        /// <summary>
        /// How far the camera has zoomed out relative to level-1 height
        /// (<c>CurrentHeight / heightAtLevel1</c>). Level 1 → 1; higher levels → larger.
        /// Used by the minimap so collapsed world radius scales with the gameplay camera.
        /// </summary>
        public float CurrentHeightZoomFactor
        {
            get
            {
                // --- Same proportion as perspective top-down framing ---
                // Fixed FOV ⇒ visible world radius ∝ camera height. Minimap radius multiplies by this.
                float baseHeight = Mathf.Max(0.01f, Settings.heightAtLevel1);
                return Mathf.Max(0.01f, CurrentHeight / baseHeight);
            }
        }

        /// <summary>
        /// Lazy in-memory defaults used only when both the family slot and Inspector fallback are empty.
        /// </summary>
        static CameraFollowSettings _fallbackSettings;

        /// <summary>
        /// Profile currently driving framing. Set by <see cref="SetSettings"/> / family sync — never overwrites
        /// the serialized <see cref="defaultSettings"/> slot so we can restore when a family leaves it empty.
        /// </summary>
        CameraFollowSettings _activeSettings;

        /// <summary>
        /// Last <see cref="ShipState.ShipFamilyConfigIndex"/> we applied a profile for.
        /// <c>int.MinValue</c> means "never synced yet".
        /// </summary>
        int _syncedFamilyConfigIndex = int.MinValue;

        /// <summary>Last profile instance applied for <see cref="_syncedFamilyConfigIndex"/> (identity compare).</summary>
        CameraFollowSettings _syncedProfile;

        /// <summary>
        /// Cached <see cref="ShipFamilySpecialBonuses.cameraHeightMul"/> for the local family.
        /// 1 when unset, MEGA, or the family has not synced yet. Applied on top of
        /// <see cref="CameraFollowSettings.ComputeTargetHeight"/>.
        /// </summary>
        float _familyCameraHeightMul = 1f;

        /// <summary>
        /// Last resolved MEGA vs family camera. Held while gem Instantiates skip ship gathers
        /// so MEGA plow does not swap to the family profile for a frame (zoom flicker).
        /// </summary>
        bool _latchedIsMega;

        /// <summary>[UNITY] Cached Camera on this GameObject (may be null if misconfigured).</summary>
        UnityEngine.Camera cam;

        /// <summary>
        /// Current smoothed look-ahead on XZ (Y always 0). Applied on top of the ship position each frame.
        /// </summary>
        Vector3 _lookAheadCurrent;

        /// <summary>[UNITY] Velocity term for SmoothDamp on look-ahead (do not edit in Inspector).</summary>
        Vector3 _lookAheadSmoothVelocity;

        /// <summary>Current smoothed camera height (world Y offset).</summary>
        float _currentHeight;

        /// <summary>[UNITY] Velocity term for SmoothDamp on height.</summary>
        float _heightSmoothVelocity;

        /// <summary>True after the first successful follow frame — avoids SmoothDamp starting from 0,0,0.</summary>
        bool _initialized;

        /// <summary>Previous frame ship position — used to estimate planar velocity when ECS velocity is unavailable.</summary>
        Vector3 _lastShipPos;

        /// <summary>True once <see cref="_lastShipPos"/> has a valid sample.</summary>
        bool _hasLastShipPos;

        /// <summary>
        /// Last successfully read <see cref="ShipState.ShipLevel"/>. Never fall back to 1 during
        /// <see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/> (gem Instantiates after
        /// asteroid destroy) — that caused SmoothDamp height zoom-in/out.
        /// </summary>
        int _lastKnownShipLevel = 1;

        /// <summary>Cached MEGA follow offset (collider center minus pivot) on XZ.</summary>
        Vector3 _megaFollowOffset;

        /// <summary>Cached MEGA view radius including catalog padding.</summary>
        float _megaViewRadius;

        /// <summary>True when the local ship is a MEGA and hull framing is valid.</summary>
        bool _hasMegaView;

        /// <summary>Random closed spline the idle camera rides. Allocated once; no per-frame GC.</summary>
        readonly CameraTheatricalOrbit _theatricalOrbit = new CameraTheatricalOrbit();

        /// <summary>True after the orbit helper received the current Inspector path knobs.</summary>
        bool _theatricalOrbitConfigured;

        /// <summary>True while the cinematic orbit owns the camera pose (including enter blend).</summary>
        bool _theatricalModeActive;

        /// <summary>True while blending from orbit back to top-down gameplay follow.</summary>
        bool _theatricalReturning;

        /// <summary>Seconds the local ship has been idle (reset on thrust / fire / turn).</summary>
        float _theatricalIdleTimer;

        /// <summary>Elapsed seconds in the current enter or exit blend.</summary>
        float _theatricalBlendElapsed;

        /// <summary>Gameplay camera pose snapped when theatrical started (enter blend from).</summary>
        Vector3 _theatricalBlendStartPosition;

        /// <summary>Gameplay camera rotation snapped when theatrical started.</summary>
        Quaternion _theatricalBlendStartRotation = Quaternion.identity;

        /// <summary>Gameplay FOV snapped when theatrical started.</summary>
        float _theatricalBlendStartFov;

        /// <summary>Cached pullback world point for the intro (stable — not recomputed each frame).</summary>
        Vector3 _theatricalPullbackPosition;

        /// <summary>Orbit look-at after SmoothDamp so the hull does not jitter.</summary>
        Vector3 _theatricalSmoothedLookTarget;

        /// <summary>[UNITY] Velocity term for SmoothDamp on the look-at point.</summary>
        Vector3 _theatricalLookVelocity;

        /// <summary>True after the look-at SmoothDamp has a valid seed.</summary>
        bool _hasTheatricalSmoothedLookTarget;

        /// <summary>Orbit rotation after SmoothDamp.</summary>
        Quaternion _theatricalSmoothedRotation = Quaternion.identity;

        /// <summary>True after the rotation SmoothDamp has a valid seed.</summary>
        bool _hasTheatricalSmoothedRotation;

        /// <summary>Orbit FOV after SmoothDamp.</summary>
        float _theatricalSmoothedFov;

        /// <summary>[UNITY] Velocity term for SmoothDamp on FOV.</summary>
        float _theatricalFovVelocity;

        /// <summary>True after the FOV SmoothDamp has a valid seed.</summary>
        bool _hasTheatricalSmoothedFov;

        /// <summary>Last local <see cref="ShipState.IsDead"/> — rising edge starts theatrical immediately.</summary>
        bool _wasLocalShipDead;

        /// <summary>Last hull yaw (degrees) used to measure turn rate for the idle countdown.</summary>
        float _lastIdleShipYawDeg;

        /// <summary>True after the first idle-yaw sample this session.</summary>
        bool _hasLastIdleShipYaw;

        /// <summary>
        /// True while the opening crane is pitching off top-down. The surround path
        /// does not run yet — LookAt on the look-down pole was the opening spin.
        /// </summary>
        bool _theatricalIntroActive;

        /// <summary>Distance from focus to the gameplay camera when the crane started.</summary>
        float _theatricalIntroStartDistance = 8f;

        /// <summary>Distance from focus at the end of the crane (already off the pole).</summary>
        float _theatricalIntroEndDistance = 16f;

        /// <summary>Cached hull size for orbit radii. Refreshed on network-id / MEGA change, not every frame.</summary>
        float _cachedTheatricalRadius = 4f;

        /// <summary>Network id the cached radius belongs to (0 = none).</summary>
        int _cachedTheatricalRadiusNetworkId;

        /// <summary>True when <see cref="_cachedTheatricalRadius"/> used MEGA hull view.</summary>
        bool _cachedTheatricalRadiusWasMega;

        /// <summary>Cached mesh-top lift used for a slight focus Y nudge. Same chassis snapshot as radius.</summary>
        float _cachedTheatricalLiftFromPivot;

        /// <summary>[UNITY] Cached input handler so LateUpdate does not Find every frame.</summary>
        PlayerInputHandler _cachedInput;

        /// <summary>
        /// Code defaults matching <see cref="CameraFollowSettings"/> field defaults.
        /// Created once; never written to disk.
        /// </summary>
        static CameraFollowSettings FallbackSettings
        {
            get
            {
                if (_fallbackSettings == null)
                {
                    // [UNITY] CreateInstance — runtime-only SO; not an asset on disk.
                    _fallbackSettings = ScriptableObject.CreateInstance<CameraFollowSettings>();
                    _fallbackSettings.name = "CameraFollowSettings (Fallback Defaults)";
                    _fallbackSettings.ClampValues();
                }

                return _fallbackSettings;
            }
        }

        /// <summary>
        /// Assigns a new follow profile (e.g. when the player purchases a different ship family).
        /// Updates FOV immediately; height / look-ahead ease toward the new knobs via SmoothDamp.
        /// Does not change the Inspector <see cref="defaultSettings"/> fallback reference.
        /// </summary>
        /// <param name="newSettings">
        /// Profile to use. Null clears the runtime override so <see cref="defaultSettings"/> / code fallback apply.
        /// </param>
        public void SetSettings(CameraFollowSettings newSettings)
        {
            _activeSettings = newSettings;
            ApplyLensFromSettings();
        }

        /// <summary>
        /// [UNITY] Awake — cache the Camera, seed active profile from the scene fallback, lock top-down euler.
        /// </summary>
        void Awake()
        {
            cam = GetComponent<UnityEngine.Camera>();
            Instance = this;

            // --- Seed runtime profile from the Inspector fallback until a local ship family resolves ---
            if (_activeSettings == null)
                _activeSettings = defaultSettings;

            ApplyLensFromSettings();

            // [TITAN-ORBIT] Top-down: pitch 90° so +Y is "out of the screen," XZ is the play plane.
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        void OnDisable()
        {
            if (Instance == this)
                Instance = null;
            MapWrapTransition.Reset();
            ResetTheatricalState();
        }

        /// <summary>
        /// [UNITY] Fullscreen fade while the local ship wraps. Hides the world pop for
        /// <see cref="MapWrapTransition.DurationSeconds"/>.
        /// </summary>
        void OnGUI()
        {
            float fade = MapWrapTransition.Fade01;
            if (fade <= 0.01f)
                return;

            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, fade * MapWrapTransition.PeakAlpha);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = prev;
        }

#if UNITY_EDITOR
        /// <summary>
        /// [EDITOR] Live-tweak FOV when you drag values on the assigned asset or swap the slot in the Inspector.
        /// </summary>
        void OnValidate()
        {
            if (cam == null)
                cam = GetComponent<UnityEngine.Camera>();

            // Keep edit-mode preview on the fallback unless play mode already swapped via family sync.
            if (!Application.isPlaying || _activeSettings == null)
                _activeSettings = defaultSettings;

            ApplyLensFromSettings();
        }
#endif

        /// <summary>
        /// Copies perspective FOV from the active profile onto the Camera component.
        /// Skips the FOV write while theatrical owns the lens so a family-profile swap
        /// cannot snap the cinematic shot.
        /// </summary>
        void ApplyLensFromSettings()
        {
            var profile = Settings;
            profile.ClampValues();

            if (cam == null)
                return;

            // [UNITY] Orthographic would ignore FOV; gameplay uses perspective looking straight down.
            cam.orthographic = false;
            if (_theatricalModeActive || _theatricalReturning)
                return;

            cam.fieldOfView = profile.gameplayFieldOfView;
        }

        /// <summary>
        /// [UNITY] LateUpdate — presentation pose is published during ECS Update on this frame,
        /// so LateUpdate is the safe window for MonoBehaviour camera readers.
        /// </summary>
        void LateUpdate()
        {
            // --- Family profile sync (before framing) ---
            // [TITAN-ORBIT] Ghosted ShipFamilyConfigIndex changes on moon-dock purchase / team spawn.
            // Reuse EcsGameBridge.TryGetLocalShipState — no new ship ECS gathers.
            SyncProfileFromLocalShipFamily();

            if (!TryResolveFollowTarget(out var shipPos, out bool isMoonDockOverride))
                return;

            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            MapWrapTransition.Tick(dt);

            // --- Same-frame wrap: snap velocity sample so look-ahead does not spike ---
            // Camera hard-locks to ship XZ, so the hull stays on-screen; the world pops.
            if (_hasLastShipPos && ToroidalMap.IsWrapJump(_lastShipPos, shipPos))
            {
                MapWrapTransition.NotifyWrap();
                Vector3 wrapDelta = shipPos - _lastShipPos;
                SnapTheatricalWorldPoints(wrapDelta);
                _lastShipPos = shipPos;
                _lookAheadSmoothVelocity = Vector3.zero;
            }

            var profile = Settings;
            profile.ClampValues();

            RefreshMegaHullFraming(ref shipPos, isMoonDockOverride);

            // --- Seed SmoothDamp state on first lock ---
            // Without this, the first frame would ease from (0,0,0) and the camera would fly in from origin.
            if (!_initialized)
            {
                int level = ResolveShipLevel();
                _currentHeight = ApplyFamilyCameraHeightMul(profile.ComputeTargetHeight(level));
                _lookAheadCurrent = Vector3.zero;
                _lookAheadSmoothVelocity = Vector3.zero;
                _heightSmoothVelocity = 0f;
                _lastShipPos = shipPos;
                _hasLastShipPos = true;
                _initialized = true;
            }

            // --- Height zoom ---
            // Default: ship-level curve from the active family profile.
            // Turret possession: raise height so the pad's engage/bullet radius fits the viewport
            // (never zoom in closer than the normal ship framing).
            // MEGA: raise just enough to fit the collider box, then cap so tracers stay readable.
            float targetHeight = ApplyFamilyCameraHeightMul(profile.ComputeTargetHeight(ResolveShipLevel()));
            if (Shared.PlanetaryDefenseTurretClientState.IsControlling &&
                Shared.PlanetaryDefenseTurretClientState.DesiredViewRadiusWorld > 0.01f)
            {
                float turretHeight = ComputeHeightForViewRadius(
                    Shared.PlanetaryDefenseTurretClientState.DesiredViewRadiusWorld,
                    profile.gameplayFieldOfView);
                targetHeight = Mathf.Max(targetHeight, turretHeight);
            }
            else if (_hasMegaView && _megaViewRadius > 0.01f)
            {
                float megaHeight = ComputeHeightForViewRadius(_megaViewRadius, profile.gameplayFieldOfView);
                var megaCatalog = MegaShipCatalog.Load();
                float cap = megaCatalog != null ? megaCatalog.GetCameraMaxHeight() : MegaShipCatalog.DefaultCameraMaxHeight;
                targetHeight = Mathf.Min(cap, Mathf.Max(targetHeight, megaHeight));
            }

            _currentHeight = Mathf.SmoothDamp(
                _currentHeight,
                targetHeight,
                ref _heightSmoothVelocity,
                profile.heightSmoothTime,
                Mathf.Infinity,
                dt);

            // --- Look-ahead from planar velocity ---
            // Moon-dock cinematic keeps a hard hull lock: no lead (spinning on the surface would yank framing).
            // [TITAN-ORBIT] During ship/gem Instantiates, velocity reads fail and pose-delta is near-zero
            // (soft-track) — SmoothDamp toward zero then back out feels like zoom. Freeze lead instead.
            bool freezeLookAhead = ClientJoinSettleCache.ShouldSkipShipEntityQueries;
            if (!isMoonDockOverride && !freezeLookAhead)
            {
                Vector3 planarVel = ResolvePlanarVelocity(shipPos, dt);
                Vector3 desiredLookAhead = profile.ComputeDesiredLookAhead(planarVel);
                _lookAheadCurrent = Vector3.SmoothDamp(
                    _lookAheadCurrent,
                    desiredLookAhead,
                    ref _lookAheadSmoothVelocity,
                    profile.lookAheadSmoothTime,
                    Mathf.Infinity,
                    dt);
            }
            else if (isMoonDockOverride)
            {
                // Ease lead out while docked (do not freeze a stale combat lead over the moon).
                _lookAheadCurrent = Vector3.SmoothDamp(
                    _lookAheadCurrent,
                    Vector3.zero,
                    ref _lookAheadSmoothVelocity,
                    profile.lookAheadSmoothTime,
                    Mathf.Infinity,
                    dt);
            }
            // else: backlog — keep _lookAheadCurrent / velocity as-is.

            // Force Y=0 so look-ahead never lifts/drops the camera (height owns Y).
            _lookAheadCurrent.y = 0f;

            // --- Compose gameplay pose first ---
            // [TITAN-ORBIT] Height / look-ahead keep ticking during theatrical so exit has a
            // live top-down target and the minimap zoom factor does not drift.
            Vector3 gameplayPosition = shipPos + _lookAheadCurrent + new Vector3(0f, _currentHeight, 0f);
            Quaternion gameplayRotation = Quaternion.Euler(90f, 0f, 0f);
            float gameplayFov = profile.gameplayFieldOfView;

            bool playerThrusting = IsPlayerThrustingOrCoasting(shipPos, dt);
            bool playerFiring = IsPlayerFiringWeapons();
            bool playerTurning = IsShipStillTurning(dt);
            UpdateTheatricalModeState(
                playerThrusting,
                playerFiring,
                playerTurning,
                isMoonDockOverride,
                shipPos);

            if (_theatricalModeActive || _theatricalReturning)
            {
                ApplyTheatricalOrReturnCamera(
                    gameplayPosition,
                    gameplayRotation,
                    gameplayFov,
                    shipPos,
                    dt,
                    out Vector3 theatricalPosition,
                    out Quaternion theatricalRotation,
                    out float theatricalFov);
                transform.position = theatricalPosition;
                transform.rotation = theatricalRotation;
                if (cam != null && !cam.orthographic)
                    cam.fieldOfView = theatricalFov;
            }
            else
            {
                transform.position = gameplayPosition;
                transform.rotation = gameplayRotation;
                if (cam != null && !cam.orthographic)
                    cam.fieldOfView = gameplayFov;
            }

            _lastShipPos = shipPos;
            _hasLastShipPos = true;
        }

        /// <summary>
        /// True while the local player is thrusting or still coasting above the idle
        /// speed cap. Moon dock is handled as a suppress, not as "idle in space."
        /// </summary>
        /// <param name="shipPos">Current follow target (for planar-speed fallback).</param>
        /// <param name="dt">Frame delta in seconds.</param>
        bool IsPlayerThrustingOrCoasting(Vector3 shipPos, float dt)
        {
            if (_cachedInput == null)
                _cachedInput = FindAnyObjectByType<PlayerInputHandler>();

            if (_cachedInput != null)
            {
                if (_cachedInput.MoveForwardPressed)
                    return true;
            }
            else if (ShipPendingInput.HasValue)
            {
                if (ShipPendingInput.Latest.Thrust)
                    return true;
            }

            Vector3 planarVel = ResolvePlanarVelocity(shipPos, dt);
            planarVel.y = 0f;
            return planarVel.magnitude > theatricalIdleMaxPlanarSpeed;
        }

        /// <summary>
        /// True while the player is holding fire. Blocks the idle countdown so we do not
        /// enter cinematic mid-spray. Does not exit theatrical — only thrust does that.
        /// </summary>
        bool IsPlayerFiringWeapons()
        {
            if (_cachedInput == null)
                _cachedInput = FindAnyObjectByType<PlayerInputHandler>();

            return _cachedInput != null && _cachedInput.ShootPressed;
        }

        /// <summary>
        /// True while the hull is still yawing — either catching the cursor or spinning
        /// faster than <see cref="theatricalIdleMaxYawDegPerSec"/>. Countdown waits for this.
        /// Does not exit theatrical (aim is frozen there).
        /// </summary>
        /// <param name="dt">Frame delta in seconds.</param>
        bool IsShipStillTurning(float dt)
        {
            Quaternion rot = ShipDisplayPose.HasLocalPose
                ? ShipDisplayPose.LocalRotation
                : transform.rotation;
            float yawDeg = rot.eulerAngles.y;

            bool turning = false;
            if (_hasLastIdleShipYaw && dt > 1e-5f)
            {
                float yawDelta = Mathf.Abs(Mathf.DeltaAngle(_lastIdleShipYawDeg, yawDeg));
                if (yawDelta / dt > theatricalIdleMaxYawDegPerSec)
                    turning = true;
            }

            _lastIdleShipYawDeg = yawDeg;
            _hasLastIdleShipYaw = true;

            if (turning)
                return true;

            if (!ShipPendingInput.HasValue)
                return false;

            float2 aim = ShipPendingInput.Latest.AimPlanarDir;
            if (math.lengthsq(aim) < 0.0001f)
                return false;

            Vector3 forward = rot * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                return false;

            Vector3 aimWorld = new Vector3(aim.x, 0f, aim.y);
            return Vector3.Angle(forward, aimWorld) > theatricalAimAlignmentToleranceDeg;
        }

        /// <summary>
        /// True when the local ship is fully landed on a gem moon or the orbit store is up.
        /// Theatrical is space-idle only.
        /// </summary>
        static bool IsLocalShipLandedOnMoon()
        {
            if (MoonOrbitClientState.IsOrbitMenuVisible)
                return true;

            return EcsGameBridge.TryGetLocalShipMoonDockState(out var dock) && dock.IsFullyLanded;
        }

        /// <summary>
        /// Idle timer, death enter, turret / moon / join-settle suppress, and exit on thrust.
        /// Does not write the camera pose — <see cref="ApplyTheatricalOrReturnCamera"/> does that.
        /// </summary>
        /// <param name="playerThrusting">True when thrust / coasting should exit cinematic.</param>
        /// <param name="playerFiring">True when fire should delay enter (not exit).</param>
        /// <param name="playerTurning">True when hull yaw should delay enter (not exit).</param>
        /// <param name="isMoonDockOverride">True when the moon-dock cinematic owns the follow target.</param>
        /// <param name="shipPos">This-frame follow target (after MEGA offset) for path focus.</param>
        void UpdateTheatricalModeState(
            bool playerThrusting,
            bool playerFiring,
            bool playerTurning,
            bool isMoonDockOverride,
            Vector3 shipPos)
        {
            bool isDead = EcsGameBridge.TryGetLocalShipState(out var ship) && ship.IsDead;
            bool justDied = isDead && !_wasLocalShipDead;
            bool justRespawned = !isDead && _wasLocalShipDead;
            _wasLocalShipDead = isDead;

            // --- Suppress: turret, moon landing, join Instantiates ---
            // [TITAN-ORBIT] Theatrical is space-idle only. Moon dock already has its
            // own camera. Join settle has no reliable idle signal (velocity reads fail).
            bool suppress =
                !theatricalModeEnabled ||
                isMoonDockOverride ||
                IsLocalShipLandedOnMoon() ||
                PlanetaryDefenseTurretClientState.IsControlling ||
                ClientJoinSettleCache.ShouldSkipShipEntityQueries;

            if (suppress)
            {
                _theatricalIdleTimer = 0f;
                if (_theatricalModeActive)
                    BeginTheatricalReturn();
                return;
            }

            // --- Death: start immediately; respawn returns to gameplay ---
            if (justDied)
            {
                BeginTheatricalMode(shipPos);
                return;
            }

            if (justRespawned)
            {
                _theatricalIdleTimer = 0f;
                if (_theatricalModeActive)
                    BeginTheatricalReturn();
                return;
            }

            // Thrust (or coasting above the idle cap) is the only gameplay exit.
            // Mouse aim does not leave cinematic and does not yaw the hull there.
            if (playerThrusting)
            {
                _theatricalIdleTimer = 0f;
                if (_theatricalModeActive && !_theatricalReturning)
                    BeginTheatricalReturn();
                return;
            }

            if (_theatricalModeActive || _theatricalReturning)
                return;

            // Still top-down: fire or hull turn means the player is busy — wait for
            // true idle before the countdown starts.
            if (playerFiring || playerTurning)
            {
                _theatricalIdleTimer = 0f;
                return;
            }

            _theatricalIdleTimer += Time.deltaTime;
            if (_theatricalIdleTimer >= theatricalIdleDurationSeconds)
                BeginTheatricalMode(shipPos);
        }

        /// <summary>
        /// Snapshots the live gameplay camera and starts a world-X pitch crane off
        /// top-down. The surround path is built only after that crane finishes so
        /// LookAt never runs on the look-down pole.
        /// </summary>
        /// <param name="shipPos">This-frame follow target after MEGA offset.</param>
        void BeginTheatricalMode(Vector3 shipPos)
        {
            EnsureTheatricalOrbitConfigured();

            _theatricalBlendStartPosition = transform.position;
            _theatricalBlendStartRotation = transform.rotation;
            _theatricalBlendStartFov = cam != null ? cam.fieldOfView : Settings.gameplayFieldOfView;

            ResolveTheatricalFocus(shipPos, out Vector3 focus, out float radius, out Quaternion shipRot);
            _theatricalOrbit.SetCharacteristicRadius(radius);

            Quaternion invShip = Quaternion.Inverse(shipRot);
            Vector3 anchorLocal = invShip * (_theatricalBlendStartPosition - focus);
            Vector3 pullbackLocal = CameraTheatricalOrbit.ComputePullbackLocal(
                anchorLocal,
                radius,
                theatricalRadiusMaxMultiplier);
            _theatricalPullbackPosition = focus + shipRot * pullbackLocal;

            _theatricalIntroStartDistance = Mathf.Max(
                0.5f,
                Vector3.Distance(_theatricalBlendStartPosition, focus));
            _theatricalIntroEndDistance = Mathf.Max(
                _theatricalIntroStartDistance * 1.2f,
                radius * 3.8f);

            _theatricalModeActive = true;
            _theatricalReturning = false;
            _theatricalIntroActive = true;
            _theatricalBlendElapsed = 0f;
            _theatricalIdleTimer = 0f;
            _hasTheatricalSmoothedLookTarget = false;
            _hasTheatricalSmoothedRotation = false;
            _hasTheatricalSmoothedFov = false;
            _theatricalLookVelocity = Vector3.zero;
            _theatricalFovVelocity = 0f;
        }

        /// <summary>
        /// Starts the exit blend from the current cinematic pose back to live gameplay follow.
        /// No-ops if already returning or theatrical is not active.
        /// </summary>
        void BeginTheatricalReturn()
        {
            if (_theatricalReturning || !_theatricalModeActive)
                return;

            _theatricalBlendStartPosition = transform.position;
            _theatricalBlendStartRotation = transform.rotation;
            _theatricalBlendStartFov = cam != null ? cam.fieldOfView : Settings.gameplayFieldOfView;

            _theatricalModeActive = false;
            _theatricalReturning = true;
            _theatricalIntroActive = false;
            _theatricalBlendElapsed = 0f;
            _hasTheatricalSmoothedLookTarget = false;
            _hasTheatricalSmoothedRotation = false;
            _hasTheatricalSmoothedFov = false;
        }

        /// <summary>Clears idle / blend flags (disable, or leaving play).</summary>
        void ResetTheatricalState()
        {
            _theatricalModeActive = false;
            _theatricalReturning = false;
            _theatricalIdleTimer = 0f;
            _theatricalBlendElapsed = 0f;
            _hasTheatricalSmoothedLookTarget = false;
            _hasTheatricalSmoothedRotation = false;
            _hasTheatricalSmoothedFov = false;
            _wasLocalShipDead = false;
            _hasLastIdleShipYaw = false;
            _theatricalIntroActive = false;
        }

        /// <summary>
        /// Pushes cached theatrical world points by the same wrap delta as the ship so
        /// enter/exit blends do not lerp the long way across the torus.
        /// </summary>
        /// <param name="wrapDelta">World delta the follow target just jumped.</param>
        void SnapTheatricalWorldPoints(Vector3 wrapDelta)
        {
            if (!_theatricalModeActive && !_theatricalReturning)
                return;

            _theatricalBlendStartPosition += wrapDelta;
            _theatricalPullbackPosition += wrapDelta;
            _theatricalSmoothedLookTarget += wrapDelta;
        }

        /// <summary>
        /// Samples the crane, orbit, or exit lerp and writes the camera pose LateUpdate applies.
        /// </summary>
        void ApplyTheatricalOrReturnCamera(
            Vector3 gameplayPosition,
            Quaternion gameplayRotation,
            float gameplayFov,
            Vector3 shipPos,
            float dt,
            out Vector3 finalPosition,
            out Quaternion finalRotation,
            out float finalFov)
        {
            finalPosition = gameplayPosition;
            finalRotation = gameplayRotation;
            finalFov = gameplayFov;

            ResolveTheatricalFocus(shipPos, out Vector3 focus, out float radius, out Quaternion shipRot);
            _theatricalOrbit.SetCharacteristicRadius(radius);

            if (_theatricalModeActive && !_theatricalReturning)
            {
                if (_theatricalIntroActive)
                {
                    ApplyTheatricalIntroCrane(focus, shipRot, dt, out finalPosition, out finalRotation, out finalFov);
                    return;
                }

                // Scene objects may still hold the first-compile glacial knobs (3.5s enter,
                // 1.8s look). Clamp those so the orbit actually travels around the ship.
                float lookSmooth = theatricalLookSmoothTime > 1f ? 0.22f : theatricalLookSmoothTime;
                float rotSmooth = theatricalRotationSmoothTime > 1f ? 0.28f : theatricalRotationSmoothTime;
                float fovSmooth = theatricalFovSmoothTime > 1.2f ? 0.55f : theatricalFovSmoothTime;

                _theatricalOrbit.Advance(dt, focus, shipRot);

                _theatricalOrbit.Sample(
                    focus,
                    shipRot,
                    out Vector3 orbitPosition,
                    out Vector3 lookTarget,
                    out float zoomT);

                if (!_hasTheatricalSmoothedLookTarget)
                {
                    _theatricalSmoothedLookTarget = lookTarget;
                    _hasTheatricalSmoothedLookTarget = true;
                }

                _theatricalSmoothedLookTarget = Vector3.SmoothDamp(
                    _theatricalSmoothedLookTarget,
                    lookTarget,
                    ref _theatricalLookVelocity,
                    lookSmooth,
                    Mathf.Infinity,
                    dt);

                Quaternion orbitRotation = LookAtShip(
                    orbitPosition,
                    _theatricalSmoothedLookTarget);

                if (!_hasTheatricalSmoothedRotation)
                {
                    _theatricalSmoothedRotation = _theatricalBlendStartRotation;
                    _hasTheatricalSmoothedRotation = true;
                }

                _theatricalSmoothedRotation = Quaternion.Slerp(
                    _theatricalSmoothedRotation,
                    orbitRotation,
                    1f - Mathf.Exp(-dt / Mathf.Max(0.05f, rotSmooth)));

                float fovMin = theatricalFovMin;
                float fovMax = theatricalFovMax < 58f ? 64f : theatricalFovMax;
                float targetFov = Mathf.Lerp(fovMax, fovMin, zoomT);
                if (!_hasTheatricalSmoothedFov)
                {
                    _theatricalSmoothedFov = _theatricalBlendStartFov;
                    _hasTheatricalSmoothedFov = true;
                }

                _theatricalSmoothedFov = Mathf.SmoothDamp(
                    _theatricalSmoothedFov,
                    targetFov,
                    ref _theatricalFovVelocity,
                    fovSmooth,
                    Mathf.Infinity,
                    dt);

                finalPosition = orbitPosition;
                finalRotation = _theatricalSmoothedRotation;
                finalFov = _theatricalSmoothedFov;
                return;
            }

            if (_theatricalReturning)
            {
                _theatricalBlendElapsed += dt;
                float exitT = theatricalExitBlendDuration > 0.0001f
                    ? Mathf.Clamp01(_theatricalBlendElapsed / theatricalExitBlendDuration)
                    : 1f;
                float exitEase = exitT * exitT * (3f - 2f * exitT);

                finalPosition = Vector3.Lerp(_theatricalBlendStartPosition, gameplayPosition, exitEase);
                finalRotation = Quaternion.Slerp(
                    _theatricalBlendStartRotation,
                    gameplayRotation,
                    exitEase);
                finalFov = Mathf.Lerp(_theatricalBlendStartFov, gameplayFov, exitEase);

                if (exitT >= 1f - 0.0001f)
                {
                    _theatricalReturning = false;
                    _theatricalIdleTimer = 0f;
                }
            }
        }

        /// <summary>
        /// Opening crane: pitch only around world X (same frame as gameplay Euler(90,0,0)).
        /// No yaw, no LookAt — that pair was the wild spin on the look-down pole.
        /// When the crane finishes, the surround path starts from this already-tilted pose.
        /// </summary>
        void ApplyTheatricalIntroCrane(
            Vector3 focus,
            Quaternion shipRot,
            float dt,
            out Vector3 finalPosition,
            out Quaternion finalRotation,
            out float finalFov)
        {
            float enterDur = theatricalEnterBlendDuration > 2.2f ? 1.6f : theatricalEnterBlendDuration;
            if (enterDur < 1.45f)
                enterDur = 1.6f;

            _theatricalBlendElapsed += dt;
            float blendT = Mathf.SmoothStep(
                0f,
                1f,
                Mathf.Clamp01(_theatricalBlendElapsed / enterDur));

            const float IntroEndPitchDeg = 40f;
            float pitch = Mathf.Lerp(90f, IntroEndPitchDeg, blendT);
            float dist = Mathf.Lerp(_theatricalIntroStartDistance, _theatricalIntroEndDistance, blendT);
            Quaternion pitched = Quaternion.Euler(pitch, 0f, 0f);
            finalPosition = focus - pitched * Vector3.forward * dist;
            // Stay on the pitch crane until we are off the look-down pole, then
            // ease onto a level (world-up) look-at so the orbit inherits a horizon.
            finalRotation = pitched;
            if (pitch < 55f)
            {
                Quaternion levelLook = LookAtShip(finalPosition, focus);
                float levelT = Mathf.InverseLerp(55f, IntroEndPitchDeg, pitch);
                finalRotation = Quaternion.Slerp(pitched, levelLook, levelT);
            }

            float midFov = theatricalFovMax < 58f
                ? 44f
                : Mathf.Lerp(theatricalFovMin, theatricalFovMax, 0.45f);
            finalFov = Mathf.Lerp(_theatricalBlendStartFov, midFov, blendT);

            _theatricalSmoothedRotation = finalRotation;
            _hasTheatricalSmoothedRotation = true;
            _theatricalSmoothedLookTarget = focus;
            _hasTheatricalSmoothedLookTarget = true;
            _theatricalSmoothedFov = finalFov;
            _hasTheatricalSmoothedFov = true;

            if (blendT < 1f - 0.0001f)
                return;

            _theatricalIntroActive = false;
            _theatricalOrbit.BeginPathFromCamera(finalPosition, focus, shipRot, pullBackFirstWaypoint: false);
        }

        /// <summary>
        /// Level look-at: world up is the horizon. Previous-frame up was kept after the
        /// crane and left the camera rolled while it flew. Only use gameplay −Z up when
        /// looking almost straight up or down (LookRotation vs world-up is degenerate).
        /// </summary>
        static Quaternion LookAtShip(Vector3 cameraPosition, Vector3 lookTarget)
        {
            Vector3 toFocus = lookTarget - cameraPosition;
            if (toFocus.sqrMagnitude < 1e-8f)
                return Quaternion.Euler(90f, 0f, 0f);

            toFocus.Normalize();
            Vector3 up = Vector3.up;
            if (Mathf.Abs(Vector3.Dot(toFocus, Vector3.up)) > 0.92f)
                up = new Vector3(0f, 0f, -1f);

            Vector3 projected = up - toFocus * Vector3.Dot(up, toFocus);
            if (projected.sqrMagnitude < 1e-6f)
                return Quaternion.Euler(90f, 0f, 0f);

            return Quaternion.LookRotation(toFocus, projected.normalized);
        }

        /// <summary>
        /// Focus = presentation hull (plus MEGA offset already in <paramref name="shipPos"/>
        /// via LateUpdate). Radius comes from the hull-clearance cache, not a per-frame mesh walk.
        /// </summary>
        void ResolveTheatricalFocus(
            Vector3 shipPos,
            out Vector3 focus,
            out float radius,
            out Quaternion shipRotation)
        {
            // [HYBRID] shipPos is this frame's follow target (MEGA offset already applied).
            focus = shipPos;
            shipRotation = ShipDisplayPose.HasLocalPose
                ? ShipDisplayPose.LocalRotation
                : Quaternion.identity;

            RefreshTheatricalRadiusCache();
            radius = _cachedTheatricalRadius;
            focus.y += _cachedTheatricalLiftFromPivot * 0.1f;
        }

        /// <summary>
        /// Copies Inspector path knobs onto the orbit helper once (and again if counts change).
        /// Cheap field writes — not a per-frame allocation.
        /// </summary>
        void EnsureTheatricalOrbitConfigured()
        {
            if (_theatricalOrbitConfigured)
            {
                ApplyTheatricalPathGeneration();
                return;
            }

            ApplyTheatricalPathGeneration();
            _theatricalOrbitConfigured = true;
        }

        /// <summary>
        /// Pushes Inspector orbit knobs onto the path helper. Clamps loop duration so a
        /// scene that still has the old 720–1080s values actually travels around the ship.
        /// </summary>
        void ApplyTheatricalPathGeneration()
        {
            float durMin = Mathf.Clamp(theatricalPathDurationMinSeconds, 10f, 40f);
            float durMax = Mathf.Clamp(theatricalPathDurationMaxSeconds, durMin, 55f);
            // Scene objects may still hold 2.4–5.2× from the first compile — bump the far
            // standoff so the random cycle pulls back into a wide shot.
            float radiusMin = Mathf.Max(2.6f, theatricalRadiusMinMultiplier);
            float radiusMax = theatricalRadiusMaxMultiplier < 8f ? 9.5f : theatricalRadiusMaxMultiplier;
            _theatricalOrbit.ConfigurePathGeneration(
                theatricalWaypointCount,
                theatricalMinElevationDeg,
                theatricalMaxElevationDeg,
                radiusMin,
                radiusMax,
                durMin,
                durMax);
        }

        /// <summary>
        /// Reads cached hull clearance (or MEGA view radius) when the local ship identity changes.
        /// [TITAN-ORBIT] Do not walk Renderers here — that hitch is why clearance is snapshotted
        /// on chassis swap in <see cref="ShipWeaponProxyRegistry"/>.
        /// </summary>
        void RefreshTheatricalRadiusCache()
        {
            int netId = EcsGameBridge.GetLocalNetworkId();
            if (netId == _cachedTheatricalRadiusNetworkId &&
                _hasMegaView == _cachedTheatricalRadiusWasMega &&
                _cachedTheatricalRadius > 0.5f)
                return;

            _cachedTheatricalRadiusNetworkId = netId;
            _cachedTheatricalRadiusWasMega = _hasMegaView;

            if (_hasMegaView && _megaViewRadius > 0.01f)
            {
                _cachedTheatricalRadius = Mathf.Max(3f, _megaViewRadius);
                _cachedTheatricalLiftFromPivot = _megaViewRadius * 0.35f;
                return;
            }

            if (netId > 0 &&
                ShipWeaponProxyRegistry.TryGetCachedHullClearance(netId, out float lift, out float xz))
            {
                _cachedTheatricalRadius = Mathf.Max(3f, xz * 2.8f);
                _cachedTheatricalLiftFromPivot = lift;
                return;
            }

            _cachedTheatricalRadius = 4f;
            _cachedTheatricalLiftFromPivot = 0f;
        }

        /// <summary>
        /// Resolves the local ship's family from ghosted <c>ShipFamilyConfigIndex</c> and applies that
        /// family's <see cref="ShipFamilyDefinition.cameraFollowSettings"/> (or the scene fallback).
        /// Also caches <see cref="ShipFamilySpecialBonuses.cameraHeightMul"/> so height zoom can
        /// differ per family without a unique CameraFollowSettings asset.
        /// </summary>
        void SyncProfileFromLocalShipFamily()
        {
            // [HYBRID] Tiny tagged/seeded read — safe during GhostSpawnBacklog / TeamChoice Instantiates.
            if (!EcsGameBridge.TryGetLocalShipState(out var state))
                return;

            int familyIndex = state.ShipFamilyConfigIndex;

            // --- Resolve desired profile + family camera-height mul ---
            // Prefer the family's authored asset; fall back to the Main Camera defaultSettings slot.
            CameraFollowSettings desired = defaultSettings;
            float heightMul = 1f;
            PlanetShipFamilyConfig config = ShipStatApplyLogic.Config;
            if (config != null)
            {
                PlanetShipFamilyConfig.ShipFamilyEntry entry = config.GetFamilyByConfigIndex(familyIndex);
                ShipFamilyDefinition family = entry != null ? entry.shipFamilyDefinition : null;
                if (family != null)
                {
                    if (family.cameraFollowSettings != null)
                        desired = family.cameraFollowSettings;
                    // [TITAN-ORBIT] Family identity zoom — same shared profile, different height.
                    heightMul = family.specialBonuses.ResolveCameraHeightMul();
                }
            }

            // --- MEGA catalog camera (latch through gem Instantiates) ---
            // [TITAN-ORBIT] TryGetLocalShipEntityOnWorld is false during GhostSpawnBacklog
            // (MEGA plow → asteroid destroy → gem ghosts). Treating that miss as “not MEGA”
            // swapped this camera onto the family profile for a frame — zoom / UI flicker.
            bool isMega;
            if (EcsGameBridge.TryGetLocalMegaShipState(out _))
                isMega = true;
            else if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                isMega = _latchedIsMega;
            else
                isMega = false;
            _latchedIsMega = isMega;
            if (isMega)
            {
                var megaCatalog = MegaShipCatalog.Load();
                if (megaCatalog != null && megaCatalog.cameraFollowSettings != null)
                    desired = megaCatalog.cameraFollowSettings;
                // MEGA catalog owns framing — do not stack family zoom on the capital-ship camera.
                heightMul = 1f;
            }

            _familyCameraHeightMul = heightMul;

            // --- Skip SetSettings if we already applied this family index + same profile instance ---
            if (familyIndex == _syncedFamilyConfigIndex && ReferenceEquals(desired, _syncedProfile))
                return;

            _syncedFamilyConfigIndex = familyIndex;
            _syncedProfile = desired;
            SetSettings(desired);
        }

        /// <summary>
        /// Scales a profile height by the cached family camera-height mul.
        /// Unset / zero muls stay at 1 so a missing family never pins the camera to the hull.
        /// </summary>
        /// <param name="profileHeight">World-Y from <see cref="CameraFollowSettings.ComputeTargetHeight"/>.</param>
        /// <returns>Height after the family zoom identity multiplier.</returns>
        float ApplyFamilyCameraHeightMul(float profileHeight)
        {
            float mul = _familyCameraHeightMul > 0.0001f ? _familyCameraHeightMul : 1f;
            return profileHeight * mul;
        }

        /// <summary>
        /// Reads local <see cref="ShipState.ShipLevel"/> when the bridge can resolve it.
        /// Holds the last good value when the read fails (join backlog / gem Instantiates) so height
        /// does not SmoothDamp toward level-1 and back — that looked like combat zoom.
        /// </summary>
        int ResolveShipLevel()
        {
            // [HYBRID] Tiny tagged/seeded read via EcsGameBridge — safe during GhostSpawnBacklog.
            if (EcsGameBridge.TryGetLocalShipState(out var state))
            {
                _lastKnownShipLevel = Mathf.Max(1, state.ShipLevel);
                return _lastKnownShipLevel;
            }

            return Mathf.Max(1, _lastKnownShipLevel);
        }

        /// <summary>
        /// Planar (XZ) velocity used for look-ahead. Prefers ghosted <see cref="ShipKinematics"/>;
        /// falls back to presentation-pose delta so framing still works when ship ECS queries are skipped.
        /// </summary>
        /// <param name="shipPos">Current follow target position this frame.</param>
        /// <param name="dt">Frame delta time (seconds).</param>
        Vector3 ResolvePlanarVelocity(Vector3 shipPos, float dt)
        {
            // --- Prefer authoritative gameplay velocity mirror ---
            // [NETCODE] ShipKinematics is ghost-serialized; TryGetLocalShipVelocity may return false
            // during ShouldSkipShipEntityQueries (Join Team Instantiates) — that is intentional.
            if (EcsGameBridge.TryGetLocalShipVelocity(out var ecsVel))
            {
                return new Vector3(ecsVel.x, 0f, ecsVel.z);
            }

            // --- Fallback: presentation pose delta ---
            // [HYBRID] Same motion the player sees. Slightly noisier than kinematics, but query-free.
            if (_hasLastShipPos && dt > 1e-5f)
            {
                Vector3 delta = shipPos - _lastShipPos;
                return new Vector3(delta.x, 0f, delta.z) / dt;
            }

            return Vector3.zero;
        }

        /// <summary>
        /// Centers the follow target on the MEGA collider box and caches view radius / hull-top Y
        /// so tracers can ride above the mesh. Skips new ship queries during join settle.
        /// </summary>
        void RefreshMegaHullFraming(ref Vector3 shipPos, bool isMoonDockOverride)
        {
            MegaHullTopDisplayY = 0f;
            if (isMoonDockOverride)
            {
                _hasMegaView = false;
                return;
            }

            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                if (_hasMegaView)
                {
                    shipPos += _megaFollowOffset;
                    MegaHullTopDisplayY = shipPos.y + _megaViewRadius * 0.35f;
                }
                return;
            }

            _hasMegaView = false;
            _megaFollowOffset = Vector3.zero;
            _megaViewRadius = 0f;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;
            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out var shipEntity))
                return;

            var em = world.EntityManager;
            if (!em.HasComponent<MegaShipState>(shipEntity)
                || !em.GetComponentData<MegaShipState>(shipEntity).IsMega
                || !em.HasComponent<LocalTransform>(shipEntity))
                return;

            var xf = em.GetComponentData<LocalTransform>(shipEntity);
            if (!MegaShipCombatAim.TryGetHullView(em, shipEntity, xf, out float3 center, out float radius, out float hullTopY))
                return;

            var catalog = MegaShipCatalog.Load();
            float padding = catalog != null
                ? catalog.GetCameraHullViewPadding()
                : MegaShipCatalog.DefaultCameraHullViewPadding;

            Vector3 displayCenter = shipPos;
            if (ShipDisplayPose.HasLocalPose)
            {
                Vector3 localOff = (Vector3)(center - xf.Position);
                displayCenter = ShipDisplayPose.LocalPosition + ShipDisplayPose.LocalRotation * localOff;
                displayCenter.y = shipPos.y;
            }
            else
            {
                displayCenter = new Vector3(center.x, shipPos.y, center.z);
            }

            _megaFollowOffset = displayCenter - shipPos;
            _megaFollowOffset.y = 0f;
            _megaViewRadius = radius + padding;
            _hasMegaView = true;
            MegaHullTopDisplayY = hullTopY;
            shipPos += _megaFollowOffset;
        }

        /// <summary>
        /// Converts a desired on-screen world radius into camera height for a top-down
        /// perspective lens (rotation 90° pitch). Fixed FOV ⇒ height ∝ visible radius.
        /// </summary>
        /// <param name="viewRadiusWorld">Half-extent of the circle that must fit on screen.</param>
        /// <param name="fieldOfViewDegrees">Vertical FOV from <see cref="CameraFollowSettings"/>.</param>
        /// <returns>World-Y height that just fits the circle on the narrower viewport axis.</returns>
        float ComputeHeightForViewRadius(float viewRadiusWorld, float fieldOfViewDegrees)
        {
            // --- Perspective top-down framing ---
            // Looking straight down: visible half-height = height * tan(vFOV/2).
            // Visible half-width = half-height * aspect. A circle of radius R must fit in both.
            float radius = Mathf.Max(0.5f, viewRadiusWorld);
            float fov = Mathf.Clamp(fieldOfViewDegrees, 10f, 120f);
            float tanHalf = Mathf.Tan(0.5f * fov * Mathf.Deg2Rad);
            if (tanHalf < 0.001f)
                return radius * 4f;

            float aspect = 1f;
            if (cam != null && cam.aspect > 0.01f)
                aspect = cam.aspect;

            // Narrower axis limits the fit: portrait → width; landscape → height.
            float axisFactor = Mathf.Min(1f, aspect);
            return radius / (tanHalf * axisFactor);
        }

        /// <summary>
        /// Resolves world follow position. Moon-dock cinematic overrides presentation when active
        /// (ship hull through landing, surface spin, and takeoff). Planetary defense turret control
        /// follows the pad pose (ship hull is stowed/hidden).
        /// </summary>
        /// <param name="targetPos">World point under the camera (before look-ahead / height).</param>
        /// <param name="isMoonDockOverride">True when following the moon-dock hull applier.</param>
        /// <returns>True when a follow target exists this frame.</returns>
        static bool TryResolveFollowTarget(out Vector3 targetPos, out bool isMoonDockOverride)
        {
            isMoonDockOverride = false;

            // [HYBRID] Moon dock GameObject applier overrides during landing/dock/takeoff —
            // follow the spinning hull, not the moon center.
            if (ShipMoonDockVisualApplier.TryGetLocalFollowPosition(out targetPos))
            {
                isMoonDockOverride = true;
                return true;
            }

            // --- Planetary defense turret possession ---
            // [TITAN-ORBIT] Hull is SetActive(false); follow the pad so aim framing stays useful.
            // Treat like moon-dock override (no look-ahead yank while stationary on the pad).
            if (Shared.PlanetaryDefenseTurretClientState.IsControlling &&
                Shared.PlanetaryDefenseTurretClientState.HasPadWorldPosition)
            {
                targetPos = Shared.PlanetaryDefenseTurretClientState.PadWorldPosition;
                isMoonDockOverride = true;
                return true;
            }

            // [NETCODE] Presentation pose from ShipVisualSyncSystem — not raw sim.
            if (ShipDisplayPose.HasLocalPose)
            {
                targetPos = ShipDisplayPose.LocalPosition;
                return true;
            }

            if (EcsGameBridge.TryGetLocalShipPresentationPosition(out targetPos))
                return true;

            return EcsGameBridge.TryGetLocalShipPosition(out targetPos);
        }
    }
}
