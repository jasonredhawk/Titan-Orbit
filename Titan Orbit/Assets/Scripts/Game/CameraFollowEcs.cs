using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
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

        /// <summary>Gameplay follow camera — used by bullet tracers to stay readable at MEGA height.</summary>
        public static CameraFollowEcs Instance { get; private set; }

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

            // --- Compose final camera pose ---
            // [TITAN-ORBIT] Ship XZ is hard-locked to presentation (NetCode owns flight feel).
            // Only look-ahead + height use SmoothDamp — that is framing, not a second chase of the hull.
            transform.position = shipPos + _lookAheadCurrent + new Vector3(0f, _currentHeight, 0f);
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            _lastShipPos = shipPos;
            _hasLastShipPos = true;
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
