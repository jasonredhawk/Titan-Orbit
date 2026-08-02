using TitanOrbit.Data;
using TitanOrbit.Shared;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Top-down gameplay camera that follows the local ship using NetCode presentation pose.
    /// [HYBRID] Reads <see cref="ShipDisplayPose"/> (filled by <see cref="ShipVisualSyncSystem"/>),
    /// never drives ship sim. Client only; execution order 67001 so it runs after presentation sync.
    /// <para>
    /// [TITAN-ORBIT] Framing knobs live on a <see cref="CameraFollowSettings"/> ScriptableObject
    /// so you can author multiple profiles and swap them (e.g. later per ship family). The camera
    /// hard-locks to the ship, then adds a gently smoothed look-ahead on XZ and a smoothly eased
    /// height zoom from ship level. Ship flight smoothing stays owned by NetCode — we only
    /// SmoothDamp camera composition (look-ahead + height), not the hull.
    /// </para>
    /// Moon-dock cinematic overrides the follow target with a hard lock on the spinning hull.
    /// </summary>
    [DefaultExecutionOrder(67001)]
    public class CameraFollowEcs : MonoBehaviour
    {
        [Header("Profile")]
        [Tooltip(
            "Camera follow tuning asset (height, look-ahead, FOV). " +
            "Create via Assets → Create → Titan Orbit → Camera Follow Settings. " +
            "Swap at runtime with SetSettings() when ship families need different framing.")]
        [SerializeField] CameraFollowSettings settings;

        /// <summary>
        /// Active profile. Null-safe: falls back to an in-memory default matching ScriptableObject field defaults.
        /// </summary>
        public CameraFollowSettings Settings => settings != null ? settings : FallbackSettings;

        /// <summary>
        /// Lazy in-memory defaults used only when the Inspector slot is empty.
        /// Avoids NullReferenceException in play mode if someone forgets to assign an asset.
        /// </summary>
        static CameraFollowSettings _fallbackSettings;

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
        /// Assigns a new follow profile (e.g. when the player spawns a different ship family).
        /// Updates FOV immediately; height / look-ahead ease toward the new knobs via SmoothDamp.
        /// </summary>
        /// <param name="newSettings">Profile to use. Null clears the slot and uses fallback defaults.</param>
        public void SetSettings(CameraFollowSettings newSettings)
        {
            settings = newSettings;
            ApplyLensFromSettings();
        }

        /// <summary>
        /// [UNITY] Awake — cache the Camera, apply FOV from the profile, lock euler to top-down (90° pitch).
        /// </summary>
        void Awake()
        {
            cam = GetComponent<UnityEngine.Camera>();
            ApplyLensFromSettings();

            // [TITAN-ORBIT] Top-down: pitch 90° so +Y is "out of the screen," XZ is the play plane.
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

#if UNITY_EDITOR
        /// <summary>
        /// [EDITOR] Live-tweak FOV when you drag values on the assigned asset or swap the slot in the Inspector.
        /// </summary>
        void OnValidate()
        {
            if (cam == null)
                cam = GetComponent<UnityEngine.Camera>();
            ApplyLensFromSettings();
        }
#endif

        /// <summary>
        /// Copies perspective FOV from the active profile onto the Camera component.
        /// </summary>
        void ApplyLensFromSettings()
        {
            var profile = Settings;
            profile.ClampValues();

            if (cam == null)
                return;

            // [UNITY] Orthographic would ignore FOV; gameplay uses perspective looking straight down.
            cam.orthographic = false;
            cam.fieldOfView = profile.gameplayFieldOfView;
        }

        /// <summary>
        /// [UNITY] LateUpdate — presentation pose is published during ECS Update on this frame,
        /// so LateUpdate is the safe window for MonoBehaviour camera readers.
        /// </summary>
        void LateUpdate()
        {
            if (!TryResolveFollowTarget(out var shipPos, out bool isMoonDockOverride))
                return;

            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;

            var profile = Settings;
            profile.ClampValues();

            // --- Seed SmoothDamp state on first lock ---
            // Without this, the first frame would ease from (0,0,0) and the camera would fly in from origin.
            if (!_initialized)
            {
                int level = ResolveShipLevel();
                _currentHeight = profile.ComputeTargetHeight(level);
                _lookAheadCurrent = Vector3.zero;
                _lookAheadSmoothVelocity = Vector3.zero;
                _heightSmoothVelocity = 0f;
                _lastShipPos = shipPos;
                _hasLastShipPos = true;
                _initialized = true;
            }

            // --- Height zoom from ship level ---
            // [TITAN-ORBIT] Profile owns the curve; SmoothDamp so level-ups / profile swaps ease out.
            float targetHeight = profile.ComputeTargetHeight(ResolveShipLevel());
            _currentHeight = Mathf.SmoothDamp(
                _currentHeight,
                targetHeight,
                ref _heightSmoothVelocity,
                profile.heightSmoothTime,
                Mathf.Infinity,
                dt);

            // --- Look-ahead from planar velocity ---
            // Moon-dock cinematic keeps a hard hull lock: no lead (spinning on the surface would yank framing).
            Vector3 desiredLookAhead = Vector3.zero;
            if (!isMoonDockOverride)
            {
                Vector3 planarVel = ResolvePlanarVelocity(shipPos, dt);
                desiredLookAhead = profile.ComputeDesiredLookAhead(planarVel);
            }

            _lookAheadCurrent = Vector3.SmoothDamp(
                _lookAheadCurrent,
                desiredLookAhead,
                ref _lookAheadSmoothVelocity,
                profile.lookAheadSmoothTime,
                Mathf.Infinity,
                dt);

            // Force Y=0 so look-ahead never lifts/drops the camera (height owns Y).
            _lookAheadCurrent.y = 0f;

            // --- Compose final camera pose ---
            // [TITAN-ORBIT] Ship XZ is hard-locked to presentation (NetCode owns flight feel).
            // Only look-ahead + height use SmoothDamp — that is framing, not a second chase of the hull.
            transform.position = shipPos + _lookAheadCurrent + new Vector3(0f, _currentHeight, 0f);
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            _lastShipPos = shipPos;
            _hasLastShipPos = true;
        }

        /// <summary>
        /// Reads local <see cref="ShipState.ShipLevel"/> when the bridge can resolve it; defaults to 1.
        /// </summary>
        static int ResolveShipLevel()
        {
            // [HYBRID] Tiny tagged/seeded read via EcsGameBridge — safe during GhostSpawnBacklog.
            if (EcsGameBridge.TryGetLocalShipState(out var state))
                return Mathf.Max(1, state.ShipLevel);
            return 1;
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
        /// Resolves world follow position. Moon-dock cinematic overrides presentation when active
        /// (ship hull through landing, surface spin, and takeoff).
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
