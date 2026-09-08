using System;
using System.Collections.Generic;
using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side engine and thruster jet VFX on ship GameObject proxies (ported from legacy Starship).
    /// Thruster flames follow the <b>held thrust button</b> on the local hull, not hull speed.
    /// Local owner reads <see cref="ShipPendingInput"/> (written every Unity Update). Remotes
    /// cannot see owner <see cref="IInputComponentData"/> commands — they use server
    /// <see cref="ShipInput"/> on Local Host, else ghosted <see cref="ShipKinematics"/> speed.
    /// Does not drive simulation.
    /// Attached by <see cref="EcsWorldVisualizer"/> when spawning ship hull proxies.
    /// Cosmetic smoothing of particle emission is intentional — never applied to ship transform position.
    /// <para>
    /// Prefabs resolve per mount from <see cref="ThrusterVfxBank"/>: remapped parts use
    /// <see cref="ShipPartVisualSource"/> family id; unmarked mounts use the host family;
    /// otherwise <see cref="LoadDefaultSettings"/> / ModularJetFlame2.
    /// SampleScene often leaves the propulsion bank empty — Awake falls back to Resources.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Grinding an asteroid can zero velocity while the button stays down. Also,
    /// asteroid chips often spawn gem ghosts → <c>GhostSpawnBacklog</c> →
    /// <see cref="ShipInputApplySystem"/> skips copying pending input onto the ghost, so
    /// <see cref="ShipInput.Thrust"/> can read false mid-grind. Pending input stays true — use it.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Friendly territory triangles grow thruster mounts via
    /// <see cref="ShipComponentAttributeScaleApplier"/> (execution order 95). Parent scale changes
    /// can stop Sci-Fi Arsenal <c>ParticleSystem</c>s even while thrust stays held. This applier
    /// runs at order 100 (after scale) and re-<c>Play()</c>s stuck jets without requiring a
    /// release/re-click of the thrust button. Jets parent to the ship root and sit on the
    /// mount's mesh rear in mount-local space along <c>-ship.forward</c> (not world AABB).
    /// Size uses the mount's local-scale chain (not <c>lossyScale</c> — bank roll after an
    /// asteroid grind inflated that and ballooned the flames). Across the hull, turn
    /// remaps length idle→medium→full (center included). Jets stay on a small idle
    /// puff whenever the ship is alive.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class ShipPropulsionVisualApplier : MonoBehaviour
    {
        /// <summary>
        /// [EDITOR] Archanor source path — used only when iterating in the Editor before Resources import.
        /// </summary>
        const string DefaultJetFlamePath =
            "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Interactive/JetFlame/V2/ModularJetFlame2.prefab";

        /// <summary>
        /// [UNITY] Name for <see cref="Resources.Load"/> — asset lives at
        /// <c>Assets/Resources/ModularJetFlame2.prefab</c> so Windows/WebGL builds include it.
        /// </summary>
        const string DefaultJetFlameResourcesName = "ModularJetFlame2";

        const float EngineSpeedThreshold = 0.5f;
        const float EngineEmissionRate = 18f;
        /// <summary>Aim error (°) that counts as a full left/right differential.</summary>
        const float FullTurnAimDegrees = 55f;
        /// <summary>Ignore scrape / interpolation yaw when falling back to rate (°/s).</summary>
        const float TurnYawDeadbandDegPerSec = 8f;
        /// <summary>Floor for the always-on idle puff (tiny — not a half-length jet).</summary>
        const float MinIdleBlend = 0.05f;
        /// <summary>Caps jet size so a bad hierarchy cannot balloon flames.</summary>
        const float MaxVfxSizeMul = 6f;

        static readonly string[] VfxColorNames = { "Blue", "Green", "Orange", "Purple", "Red", "Yellow" };

        [Serializable]
        public class ThrusterVfxColorPrefab
        {
            public string colorName = "Blue";
            public GameObject prefab;
        }

        [Serializable]
        public struct Settings
        {
            public GameObject engineVfxPrefab;
            public GameObject thrusterVfxPrefab;
            /// <summary>
            /// Kept for scene serialization compatibility. Thruster jets always follow the held
            /// thrust button now; coast-only lighting from this flag was removed (grind at v≈0
            /// looked idle while the player was still pushing).
            /// </summary>
            public bool useThrusterVfxForAcceleration;
            public List<ThrusterVfxColorPrefab> thrusterJetFlameBank;
            public Vector3 thrusterVfxLocalOffset;
            public Vector3 thrusterVfxLocalEuler;
            [Range(0f, 1f)] public float thrusterVfxIdleScale;
            [Min(0.01f)] public float thrusterVfxTransitionSpeed;

            public bool HasAnyThrusterPrefab =>
                thrusterVfxPrefab != null || (thrusterJetFlameBank != null && thrusterJetFlameBank.Count > 0);
        }

        Entity _shipEntity;
        string _familyPrefix = "AstroEagle";
        ShipFamilyDefinition _family;
        Settings _settings;
        bool _initialized;
        float _megaVfxScale = 1f;
        bool _isMega;

        readonly List<GameObject> _engineVfxInstances = new List<GameObject>();
        readonly List<GameObject> _thrusterVfxInstances = new List<GameObject>();
        readonly List<JetBind> _thrusterJets = new List<JetBind>();
        readonly List<ParticleSystem> _engineParticleSystems = new List<ParticleSystem>();
        readonly List<ParticleSystem> _thrusterParticleSystems = new List<ParticleSystem>();
        static readonly List<ShipPropulsionVisualApplier> s_Live = new List<ShipPropulsionVisualApplier>(8);

        bool _lastEngineMoving;
        bool _lastThrusterActive;
        int _appliedDebugCycleKey = int.MinValue;
        float _prevYawDeg;
        bool _yawSampleInitialized;
        bool _hasLateralSpread;

        /// <summary>One live flame: ship-parented instance aimed aft from a mount's rear bounds.</summary>
        sealed class JetBind
        {
            public GameObject instance;
            public Transform mount;
            public Vector3 authoredLocalScale;
            public float chassisMountScale;
            public Renderer[] mountRenderers;
            public ParticleSystem[] particles;
            /// <summary>
            /// Rear nozzle in mount-local space (mesh AABB, not world AABB).
            /// World <c>Renderer.bounds</c> is axis-aligned, so ClosestPoint jumped every yaw.
            /// </summary>
            public Vector3 mountLocalRear;
            public bool hasMountLocalRear;
            /// <summary>−1 port … 0 center … +1 starboard, from ship-local X.</summary>
            public float lateral;
            public float blend;
        }

        /// <summary>ServerWorld ship entity for Local Host remote-input lookup (same GhostOwner).</summary>
        Entity _cachedServerShip;
        int _cachedServerOwnerId;

        /// <summary>
        /// Set by <see cref="ForceRefreshEmission"/> after attribute upgrade mount grow only
        /// (not territory/overdrive smooth lerp — that path used to blink jets every step).
        /// Next LateUpdate hard-restarts particle systems even if <c>isPlaying</c> still reads true.
        /// </summary>
        bool _forceRestartPending;

        /// <summary>
        /// Builds default VFX settings with a jet-flame bank for color-matched thrusters.
        /// Called from <see cref="EcsWorldVisualizer"/> Awake when the scene bank is empty.
        /// Player builds require <c>Resources/ModularJetFlame2</c>; without it the bank stays empty
        /// and LateUpdate never animates (ships look thrust-less).
        /// </summary>
        public static Settings LoadDefaultSettings()
        {
            // --- Resolve shared flame prefab (Editor + player) ---
            GameObject defaultFlame = LoadDefaultJetFlamePrefab();
            var bank = new List<ThrusterVfxColorPrefab>();
            if (defaultFlame != null)
            {
                // [TITAN-ORBIT] Same ModularJetFlame2 for every team color until per-color banks exist.
                bank.Add(new ThrusterVfxColorPrefab { colorName = "Blue", prefab = defaultFlame });
                bank.Add(new ThrusterVfxColorPrefab { colorName = "Green", prefab = defaultFlame });
                bank.Add(new ThrusterVfxColorPrefab { colorName = "Purple", prefab = defaultFlame });
                bank.Add(new ThrusterVfxColorPrefab { colorName = "Red", prefab = defaultFlame });
                bank.Add(new ThrusterVfxColorPrefab { colorName = "Yellow", prefab = defaultFlame });
            }

            return new Settings
            {
                // [TITAN-ORBIT] Leave engineVfxPrefab null — Engine_* mounts use identity local
                // rotation, so the ModularJetFlame prefab faces forward (wrong). Only Thruster_*
                // mounts get flames, with thrusterVfxLocalEuler yaw 180 so jets point aft.
                engineVfxPrefab = null,
                // [TITAN-ORBIT] Fallback when a thruster mount has no color-bank match.
                thrusterVfxPrefab = defaultFlame,
                useThrusterVfxForAcceleration = true,
                thrusterJetFlameBank = bank,
                thrusterVfxLocalOffset = new Vector3(0f, 0f, -0.2f),
                thrusterVfxLocalEuler = new Vector3(0f, 180f, 0f),
                thrusterVfxIdleScale = 0.05f,
                thrusterVfxTransitionSpeed = 3f,
            };
        }

        /// <summary>
        /// Loads the default ModularJetFlame2 prefab for thruster Instantiates.
        /// Prefers Resources (ships in Windows/WebGL builds); Editor can fall back to AssetDatabase.
        /// </summary>
        /// <returns>Flame prefab, or null if neither Resources nor Editor path resolves.</returns>
        static GameObject LoadDefaultJetFlamePrefab()
        {
            // [UNITY] Resources.Load — asset must live under Assets/Resources/ to be in player builds.
            // SampleScene often serializes an empty thrusterJetFlameBank; Awake then calls LoadDefaultSettings.
            GameObject fromResources = Resources.Load<GameObject>(DefaultJetFlameResourcesName);
            if (fromResources != null)
                return fromResources;

#if UNITY_EDITOR
            // [EDITOR] Iteration fallback when Resources copy is missing during local work.
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(DefaultJetFlamePath);
#else
            // [TITAN-ORBIT] Player with no Resources/ModularJetFlame2 — thrusters stay dark.
            return null;
#endif
        }

        /// <summary>
        /// Links this applier to a ship ghost entity and rebuilds particle instances from chassis mounts.
        /// Called by <see cref="EcsWorldVisualizer"/> after the hybrid hull proxy is Instantiated.
        /// </summary>
        /// <param name="shipEntity">ECS ship ghost this proxy follows.</param>
        /// <param name="familyPrefix">Chassis family name for mount parsing (e.g. AstroEagle).</param>
        /// <param name="settings">Flame prefabs and blend knobs from the visualizer.</param>
        /// <param name="family">Optional family for baked per-mount VFX flags/scales.</param>
        public void Bind(
            Entity shipEntity,
            string familyPrefix,
            Settings settings,
            ShipFamilyDefinition family = null)
        {
            // --- Cache binding ---
            _shipEntity = shipEntity;
            _cachedServerShip = Entity.Null;
            _cachedServerOwnerId = 0;
            if (!string.IsNullOrWhiteSpace(familyPrefix))
                _familyPrefix = familyPrefix.Trim();
            _family = family;

            if (settings.thrusterJetFlameBank == null)
                settings.thrusterJetFlameBank = new List<ThrusterVfxColorPrefab>();

            _settings = settings;
            _isMega = IsMegaShip(shipEntity);
            _megaVfxScale = _isMega ? ResolveMegaVfxScale(shipEntity) : 1f;
            if (_isMega && _megaVfxScale < 1.01f)
                _megaVfxScale = MegaShipCatalog.DefaultThrusterVfxScale;
            _appliedDebugCycleKey = CurrentDebugCycleKey();
            RebuildVfx();
        }

        void OnEnable()
        {
            if (!s_Live.Contains(this))
                s_Live.Add(this);
        }

        void OnDisable()
        {
            s_Live.Remove(this);
        }

        /// <summary>Rebuilds jets on every live ship proxy (T-key debug cycle).</summary>
        public static void RebuildAllLive()
        {
            for (int i = s_Live.Count - 1; i >= 0; i--)
            {
                ShipPropulsionVisualApplier applier = s_Live[i];
                if (applier == null)
                {
                    s_Live.RemoveAt(i);
                    continue;
                }

                applier._appliedDebugCycleKey = CurrentDebugCycleKey();
                if (applier._shipEntity != Entity.Null)
                    applier.RebuildVfx();
            }
        }

        static int CurrentDebugCycleKey()
        {
            return TitanOrbitDebugFlags.CycleAllThrusterVfx
                ? ThrusterVfxBank.DebugCycleIndex
                : int.MinValue;
        }

        static bool IsMegaShip(Entity shipEntity)
        {
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated || !world.EntityManager.Exists(shipEntity))
                return false;
            if (!world.EntityManager.HasComponent<MegaShipState>(shipEntity))
                return false;
            return world.EntityManager.GetComponentData<MegaShipState>(shipEntity).IsMega;
        }

        /// <summary>MEGA hulls shrink with per-family catalog scale — boost jet local scale so flames stay visible.</summary>
        static float ResolveMegaVfxScale(Entity shipEntity)
        {
            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated || !world.EntityManager.Exists(shipEntity))
                return 1f;
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return 1f;
            if (!world.EntityManager.HasComponent<MegaShipState>(shipEntity)
                || !world.EntityManager.GetComponentData<MegaShipState>(shipEntity).IsMega)
                return 1f;

            var catalog = MegaShipCatalog.Load();
            return catalog != null ? catalog.GetThrusterVfxScale() : MegaShipCatalog.DefaultThrusterVfxScale;
        }

        void OnDestroy() => ClearVfxInstances();

        /// <summary>
        /// Copies live thruster jet instance transforms (same objects <see cref="RebuildVfx"/> spawned).
        /// Used by <see cref="ShipDamageSmokeVisualApplier"/> so damage smoke sits on each flame.
        /// </summary>
        /// <param name="dest">Cleared then filled; null is ignored.</param>
        public void CopyThrusterVfxAnchors(List<Transform> dest)
        {
            if (dest == null)
                return;

            dest.Clear();
            for (int i = 0; i < _thrusterVfxInstances.Count; i++)
            {
                GameObject go = _thrusterVfxInstances[i];
                if (go != null)
                    dest.Add(go.transform);
            }
        }

        /// <summary>
        /// Instantiates engine/thruster flame prefabs at <see cref="ChassisComponentStats"/> mount sites.
        /// Sets <c>_initialized</c> only when at least one particle instance was created — otherwise
        /// LateUpdate exits early and the ship stays without thrust VFX.
        /// </summary>
        void RebuildVfx()
        {
            ClearVfxInstances(immediate: true);
            DestroyOrphanJetInstances(transform, immediate: true);
            _thrusterJets.Clear();
            _lastEngineMoving = false;
            _lastThrusterActive = false;
            _forceRestartPending = false;
            _yawSampleInitialized = false;

            // --- Find Engine_* / VFX-enabled thruster mounts on the hybrid hull ---
            // [TITAN-ORBIT] thrusterVfxTransforms = enablePropulsionVfx only
            // (Thrusters_Big / Tiny_Thrusters yes; Thruster_Place / Cover no).
            // thrusterTransforms is the attribute-scale group (includes covers) — not used for particles.
            bool mega = _isMega;
            var stats = ChassisComponentStats.FromTransform(
                transform,
                mega ? string.Empty : _familyPrefix,
                mega ? null : _family);

            // --- Engine mounts (main rear jets on AstroEagle-style hulls) ---
            if (_settings.engineVfxPrefab != null)
            {
                foreach (Transform t in stats.engineTransforms)
                {
                    if (t == null)
                        continue;

                    GameObject go = Instantiate(_settings.engineVfxPrefab, t);
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localRotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one * _megaVfxScale;
                    // [HYBRID] URP material fixups so Sci-Fi Arsenal particles render in player builds.
                    VfxUrpCompat.PrepareVfxInstance(go);
                    _engineVfxInstances.Add(go);
                    CollectParticleSystems(go, _engineParticleSystems);
                }
            }

            for (int i = 0; i < stats.thrusterVfxTransforms.Count; i++)
            {
                Transform t = stats.thrusterVfxTransforms[i];
                if (t == null)
                    continue;

                ThrusterVfxBank.Entry familyEntry = ResolveEntryForMount(t);
                GameObject prefab = ResolveThrusterVfxPrefabForTransform(t, familyEntry);
                if (prefab == null)
                    continue;

                float mountScale = 1f;
                if (stats.thrusterVfxScales != null && i < stats.thrusterVfxScales.Count)
                    mountScale = Mathf.Max(0.01f, stats.thrusterVfxScales[i]);

                // Parent to the ship root so world-aft rotation is not sheared by a
                // sideways mount. Keep the prefab's authored localScale; only live
                // component / ship size may multiply it (never a bind-time ratio).
                Vector3 authoredScale = prefab.transform.localScale;
                GameObject go = Instantiate(prefab, transform);
                go.name = ThrusterVfxBank.JetInstanceName;
                VfxUrpCompat.PrepareVfxInstance(go, playParticles: false);
                ConfigureThrusterParticles(go);

                var bind = new JetBind
                {
                    instance = go,
                    mount = t,
                    authoredLocalScale = authoredScale,
                    chassisMountScale = mountScale,
                    mountRenderers = CollectMountRenderers(t),
                    particles = go.GetComponentsInChildren<ParticleSystem>(true),
                    blend = ResolveIdleBlend()
                };
                go.SetActive(true);
                bind.hasMountLocalRear = TryComputeMountLocalRear(bind, ResolveShipAft(), out bind.mountLocalRear);
                _thrusterVfxInstances.Add(go);
                _thrusterJets.Add(bind);
                CollectParticleSystems(go, _thrusterParticleSystems);
            }

            AssignThrusterSides();
            UpdateJetPoses();

            _initialized = _engineVfxInstances.Count > 0 || _thrusterVfxInstances.Count > 0;
        }

        /// <summary>
        /// Forces emission / Play to re-apply on the next LateUpdate even when thrust state looks unchanged.
        /// Called by <see cref="ShipComponentAttributeScaleApplier"/> after attribute upgrade mesh grow
        /// (not on territory/overdrive display lerp — continuous scale uses self-heal via stopped particles).
        /// </summary>
        public void ForceRefreshEmission()
        {
            // --- Invalidate cached on/off so LateUpdate cannot early-out ---
            // [TITAN-ORBIT] Flip latches so they never match the next real engine/thruster booleans.
            _lastEngineMoving = !_lastEngineMoving;
            _lastThrusterActive = !_lastThrusterActive;
            // --- Hard restart on next apply ---
            // Parent scale can leave ModularJetFlame with isPlaying==true but no visible emission.
            _forceRestartPending = true;
        }

        /// <summary>
        /// Each frame: read held thrust, fade jet length, and Play/Stop when the fade finishes.
        /// Runs after ship proxy transforms have been synced for the frame, and after
        /// <see cref="ShipComponentAttributeScaleApplier"/> (order 95) so territory mount scale
        /// cannot leave jets stopped for a full frame.
        /// </summary>
        void LateUpdate()
        {
            // --- Guard: no prefab instances or unbound entity ---
            if (!_initialized || _shipEntity == Entity.Null)
                return;

            int debugKey = CurrentDebugCycleKey();
            if (debugKey != _appliedDebugCycleKey)
            {
                _appliedDebugCycleKey = debugKey;
                RebuildVfx();
                if (!_initialized)
                    return;
            }

            // [TITAN-ORBIT] Visualization world — presentation pose path, not a second motor.
            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            if (!em.Exists(_shipEntity))
                return;

            // --- Dead ships: kill emission immediately ---
            if (em.HasComponent<ShipState>(_shipEntity))
            {
                var ship = em.GetComponentData<ShipState>(_shipEntity);
                if (ship.IsDead)
                {
                    SetEngineVfxActive(false, 0f);
                    ZeroJetBlends();
                    ApplyJetEmission(hardRestart: false);
                    UpdateJetPoses();
                    _lastEngineMoving = false;
                    _lastThrusterActive = false;
                    return;
                }
            }

            // --- Held thrust (button), not hull speed ---
            // [TITAN-ORBIT] Must stay lit while grinding (v≈0) and while GhostSpawnBacklog skips
            // ShipInputApplySystem — see ResolveThrustHeld.
            bool thrusting = ResolveThrustHeld(em);
            float turn = ResolveTurnAmount(em);

            // Speed only for optional engine coast glow — never gates thruster jets.
            float speed = 0f;
            if (em.HasComponent<ShipKinematics>(_shipEntity))
            {
                float3 vel = em.GetComponentData<ShipKinematics>(_shipEntity).Velocity;
                vel.y = 0f;
                speed = math.length(vel);
            }

            bool moving = speed >= EngineSpeedThreshold;
            bool engineActive = moving || thrusting;
            bool thrusterTransitionActive = UpdateJetBlends(thrusting ? 1f : 0f, turn);

            // Always snap jets to the live mount rear — ship yaw and attribute-scale
            // grow must not leave a flame inside a sideways / flipped component.
            UpdateJetPoses();

            // --- Stuck particles while input still held ---
            // [TITAN-ORBIT] Territory AttributeScale (or any parent transform mutate) can stop
            // ModularJetFlame ParticleSystems without clearing ShipPendingInput.Thrust. The old
            // early-out trusted _lastThrusterActive alone → flames stayed dark until re-click.
            // Play() if a system stopped; only Clear+Play on attribute-scale ForceRefresh.
            // Rotation used to trip AnyParticleStopped → Restart (clear) and looked like a snap.
            bool thrusterNeedsPlay =
                _forceRestartPending ||
                AnyVisibleJetStopped();
            bool engineNeedsPlay =
                _forceRestartPending ||
                (engineActive && AnyParticleStopped(_engineParticleSystems));
            bool hardRestart = _forceRestartPending;

            // --- Skip particle writes when nothing changed and jets are healthy ---
            if (engineActive == _lastEngineMoving &&
                _lastThrusterActive &&
                !thrusterTransitionActive &&
                !thrusterNeedsPlay &&
                !engineNeedsPlay)
                return;

            _lastEngineMoving = engineActive;
            _lastThrusterActive = true;
            _forceRestartPending = false;

            SetEngineVfxActive(engineActive, engineActive ? EngineEmissionRate : 0f, hardRestart && engineActive);
            ApplyJetEmission(hardRestart);
        }

        /// <summary>
        /// True when any listed particle system should be emitting but <c>isPlaying</c> is false.
        /// Used to recover from parent-scale kills without a thrust button edge.
        /// </summary>
        /// <param name="systems">Cached engine or thruster particle systems on this proxy.</param>
        /// <returns>True if at least one non-null system is not playing.</returns>
        static bool AnyParticleStopped(List<ParticleSystem> systems)
        {
            for (int i = 0; i < systems.Count; i++)
            {
                ParticleSystem ps = systems[i];
                // [UNITY] Destroyed systems leave null entries after ClearVfx / hull rebuild.
                if (ps != null && !ps.isPlaying)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether this ship should show thrust jets right now.
        /// Local owner: <see cref="ShipPendingInput"/>. Remotes: Local Host server input,
        /// else ghosted <see cref="ShipInput.Thrust"/>, else <see cref="ShipKinematics"/> speed.
        /// </summary>
        /// <param name="em">Visualization world entity manager for this proxy's ship.</param>
        /// <returns>True while the thrust control is held (local) or the remote hull is thrusting / moving.</returns>
        bool ResolveThrustHeld(EntityManager em)
        {
            // --- Local owner: prefer pending input (Unity Update button state) ---
            // [NETCODE] GhostOwnerIsLocal is enableable and exists on every OwnerPredicted ship.
            // HasComponent is true on remotes too — only IsComponentEnabled marks this machine's hull.
            // [TITAN-ORBIT] LocalPlayerShipTag — hybrid host fallback when GhostOwnerIsLocal lags.
            // [TITAN-ORBIT] ShipInputApplySystem skips under ShouldSkipShipEntityQueries
            // (GhostSpawnBacklog). Grinding chips asteroids → gem Instantiates → backlog → ghost
            // ShipInput.Thrust can sit false while the player still holds the button. Pending stays true.
            if (IsLocalOwnerProxy(em) && ShipPendingInput.HasValue)
                return ShipPendingInput.Latest.Thrust;

            // --- Remotes: IInputComponentData is owner→server commands, not a client snapshot ---
            // Interpolated ghosts usually have Thrust=false. Local Host can read ServerWorld.
            if (TryReadLocalHostRemoteThrust(em, out bool hostThrust))
                return hostThrust;

            if (em.HasComponent<ShipInput>(_shipEntity) &&
                em.GetComponentData<ShipInput>(_shipEntity).Thrust)
                return true;

            return IsRemoteMovingFromKinematics(em);
        }

        /// <summary>
        /// Signed turn in −1..1 (positive = yaw right). Prefers aim error so asteroid
        /// scrape yaw does not flicker the jets; falls back to smoothed heading rate.
        /// </summary>
        float ResolveTurnAmount(EntityManager em)
        {
            if (TryResolveAimPlanar(em, out float2 aim))
            {
                Vector3 aim3 = new Vector3(aim.x, 0f, aim.y);
                Vector3 fwd = transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude > 0.0001f && aim3.sqrMagnitude > 0.0001f)
                {
                    float deg = Vector3.SignedAngle(fwd, aim3, Vector3.up);
                    return Mathf.Clamp(deg / FullTurnAimDegrees, -1f, 1f);
                }
            }

            float yawRate = SampleYawRateDegPerSec();
            if (Mathf.Abs(yawRate) < TurnYawDeadbandDegPerSec)
                return 0f;

            float refTurn = ShipBankVisualSettingsCache.ReferenceTurnDegreesPerSecond;
            if (refTurn < 1f)
                refTurn = 90f;
            return Mathf.Clamp(yawRate / refTurn, -1f, 1f);
        }

        bool TryResolveAimPlanar(EntityManager em, out float2 aim)
        {
            aim = float2.zero;
            if (IsLocalOwnerProxy(em) && ShipPendingInput.HasValue)
            {
                aim = ShipPendingInput.Latest.AimPlanarDir;
                return math.lengthsq(aim) > 0.01f;
            }

            if (em.HasComponent<ShipInput>(_shipEntity))
            {
                aim = em.GetComponentData<ShipInput>(_shipEntity).AimPlanarDir;
                return math.lengthsq(aim) > 0.01f;
            }

            return false;
        }

        float SampleYawRateDegPerSec()
        {
            float yawDeg = GetPlanarYawDegrees(transform.rotation);
            if (!_yawSampleInitialized)
            {
                _prevYawDeg = yawDeg;
                _yawSampleInitialized = true;
                return 0f;
            }

            float dt = Mathf.Max(1e-5f, Time.deltaTime);
            float rate = Mathf.DeltaAngle(_prevYawDeg, yawDeg) / dt;
            _prevYawDeg = yawDeg;
            return rate;
        }

        static float GetPlanarYawDegrees(Quaternion rotation)
        {
            Vector3 fwd = rotation * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-8f)
                return 0f;
            return Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Local Host only: remote player's <see cref="ShipInput.Thrust"/> from ServerWorld.
        /// Caches the server ship so LateUpdate does not gather every frame.
        /// </summary>
        bool TryReadLocalHostRemoteThrust(EntityManager clientEm, out bool thrust)
        {
            thrust = false;
            if (!EcsGameBridge.IsLocalHost())
                return false;
            if (!clientEm.HasComponent<GhostOwner>(_shipEntity))
                return false;

            int ownerId = clientEm.GetComponentData<GhostOwner>(_shipEntity).NetworkId;
            if (ownerId <= 0)
                return false;

            var server = EcsGameBridge.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;

            var sem = server.EntityManager;
            if (_cachedServerShip != Entity.Null &&
                _cachedServerOwnerId == ownerId &&
                sem.Exists(_cachedServerShip) &&
                sem.HasComponent<ShipInput>(_cachedServerShip))
            {
                thrust = sem.GetComponentData<ShipInput>(_cachedServerShip).Thrust;
                return true;
            }

            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return false;

            using var query = sem.CreateEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostOwner>(),
                ComponentType.ReadOnly<ShipInput>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != ownerId)
                    continue;
                _cachedServerShip = entities[i];
                _cachedServerOwnerId = ownerId;
                thrust = sem.GetComponentData<ShipInput>(entities[i]).Thrust;
                return true;
            }

            _cachedServerShip = Entity.Null;
            _cachedServerOwnerId = 0;
            return false;
        }

        /// <summary>True when ghosted planar speed says this remote hull is underway.</summary>
        bool IsRemoteMovingFromKinematics(EntityManager em)
        {
            if (!em.HasComponent<ShipKinematics>(_shipEntity))
                return false;

            float3 vel = em.GetComponentData<ShipKinematics>(_shipEntity).Velocity;
            vel.y = 0f;
            return math.length(vel) >= EngineSpeedThreshold;
        }

        /// <summary>
        /// True only for this machine's predicted hull — remotes must use ghosted <see cref="ShipInput"/>.
        /// </summary>
        bool IsLocalOwnerProxy(EntityManager em)
        {
            if (em.HasComponent<GhostOwnerIsLocal>(_shipEntity) &&
                em.IsComponentEnabled<GhostOwnerIsLocal>(_shipEntity))
                return true;

            if (!em.HasComponent<LocalPlayerShipTag>(_shipEntity))
                return false;

            // Stale tag on a remote must not read local mouse thrust.
            if (em.HasComponent<GhostOwner>(_shipEntity))
            {
                int ownerId = em.GetComponentData<GhostOwner>(_shipEntity).NetworkId;
                int localId = EcsGameBridge.GetLocalNetworkId();
                if (localId > 0 && ownerId != localId)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Enables or disables engine jet instances and sets emission rate.
        /// </summary>
        /// <param name="active">True while coasting or thrusting (engine glow path).</param>
        /// <param name="emissionRate">Particles per second when active; 0 when off.</param>
        /// <param name="forceRestart">
        /// When true, Stop+Clear+Play so a parent-scale kill cannot leave a zombie isPlaying state.
        /// </param>
        void SetEngineVfxActive(bool active, float emissionRate, bool forceRestart = false)
        {
            for (int i = 0; i < _engineVfxInstances.Count; i++)
            {
                GameObject go = _engineVfxInstances[i];
                if (go != null)
                    go.SetActive(active);
            }

            for (int i = 0; i < _engineParticleSystems.Count; i++)
            {
                ParticleSystem ps = _engineParticleSystems[i];
                if (ps == null)
                    continue;

                var emission = ps.emission;
                emission.enabled = true;
                emission.rateOverTime = emissionRate;
                // --- Play / restart ---
                // [UNITY] Play() alone is enough when isPlaying is false. forceRestart handles
                // territory mount scale leaving systems "playing" but emitting nothing.
                if (active && forceRestart)
                    RestartParticleSystem(ps);
                else if (active && !ps.isPlaying)
                    ps.Play();
            }
        }

        /// <summary>
        /// Per-jet Play/Stop from <see cref="JetBind.blend"/>. Length fade is in
        /// <see cref="UpdateJetPoses"/>; this only starts/stops so a finished fade goes dark.
        /// </summary>
        void ApplyJetEmission(bool hardRestart)
        {
            for (int i = 0; i < _thrusterJets.Count; i++)
            {
                JetBind jet = _thrusterJets[i];
                if (jet == null || jet.instance == null)
                    continue;

                bool active = jet.blend > 0.001f;
                if (active && !jet.instance.activeSelf)
                    jet.instance.SetActive(true);

                ParticleSystem[] systems = jet.particles;
                if (systems != null)
                {
                    for (int p = 0; p < systems.Length; p++)
                    {
                        ParticleSystem ps = systems[p];
                        if (ps == null)
                            continue;

                        if (!active)
                        {
                            if (ps.isPlaying)
                                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                            continue;
                        }

                        if (hardRestart)
                            RestartParticleSystem(ps);
                        else if (!ps.isPlaying)
                            ps.Play();
                    }
                }

                if (!active && jet.instance.activeSelf)
                    jet.instance.SetActive(false);
            }
        }

        void ZeroJetBlends()
        {
            for (int i = 0; i < _thrusterJets.Count; i++)
            {
                if (_thrusterJets[i] != null)
                    _thrusterJets[i].blend = 0f;
            }
        }

        bool AnyVisibleJetStopped()
        {
            for (int i = 0; i < _thrusterJets.Count; i++)
            {
                JetBind jet = _thrusterJets[i];
                if (jet == null || jet.blend <= 0.001f || jet.particles == null)
                    continue;

                ParticleSystem[] systems = jet.particles;
                for (int p = 0; p < systems.Length; p++)
                {
                    ParticleSystem ps = systems[p];
                    if (ps != null && !ps.isPlaying)
                        return true;
                }
            }

            return false;
        }

        float ResolveIdleBlend()
        {
            return Mathf.Clamp(_settings.thrusterVfxIdleScale, MinIdleBlend, 0.08f);
        }

        /// <summary>
        /// Idle is always on. Thrust raises every jet toward full. Turn remaps
        /// port→starboard to full / medium / idle (right turn: left full, center medium, right idle).
        /// </summary>
        bool UpdateJetBlends(float forward, float turn)
        {
            float idle = ResolveIdleBlend();
            float baseLen = Mathf.Lerp(idle, 1f, Mathf.Clamp01(forward));
            float turnAbs = Mathf.Clamp01(Mathf.Abs(turn));
            float speed = Mathf.Max(0.01f, _settings.thrusterVfxTransitionSpeed);
            float step = speed * Time.deltaTime;
            bool moving = false;
            for (int i = 0; i < _thrusterJets.Count; i++)
            {
                JetBind jet = _thrusterJets[i];
                if (jet == null)
                    continue;

                float target = baseLen;
                if (_hasLateralSpread && turnAbs > 0.001f)
                {
                    // +1 = this mount is the outside of the current turn (left when yawing right).
                    float turnAlign = -jet.lateral * turn;
                    float turned = Mathf.Lerp(idle, 1f, Mathf.InverseLerp(-1f, 1f, turnAlign));
                    target = Mathf.Lerp(baseLen, turned, turnAbs);
                }

                float next = Mathf.MoveTowards(jet.blend, target, step);
                if (Mathf.Abs(next - jet.blend) > 0.0001f)
                    moving = true;
                jet.blend = next;
            }

            return moving;
        }

        void AssignThrusterSides()
        {
            float maxAbsX = 0f;
            for (int i = 0; i < _thrusterJets.Count; i++)
            {
                JetBind jet = _thrusterJets[i];
                if (jet == null || jet.mount == null)
                    continue;
                float x = Mathf.Abs(transform.InverseTransformPoint(jet.mount.position).x);
                if (x > maxAbsX)
                    maxAbsX = x;
            }

            _hasLateralSpread = maxAbsX > 0.08f;
            for (int i = 0; i < _thrusterJets.Count; i++)
            {
                JetBind jet = _thrusterJets[i];
                if (jet == null || jet.mount == null)
                {
                    if (jet != null)
                        jet.lateral = 0f;
                    continue;
                }

                float x = transform.InverseTransformPoint(jet.mount.position).x;
                jet.lateral = _hasLateralSpread ? Mathf.Clamp(x / maxAbsX, -1f, 1f) : 0f;
            }
        }

        /// <summary>
        /// Hard-restarts a particle system so emission resumes after parent transform scale changes.
        /// </summary>
        /// <param name="ps">Non-null particle system on a jet instance.</param>
        static void RestartParticleSystem(ParticleSystem ps)
        {
            // [UNITY] StopEmittingAndClear drops in-flight particles; Play starts a clean cycle.
            // [TITAN-ORBIT] Needed when AttributeScale grows thruster mounts inside a territory triangle.
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Play();
        }

        /// <summary>
        /// Purchased remaps stamp <see cref="ShipPartVisualSource"/>; unmarked mounts
        /// use the host family row on <see cref="ThrusterVfxBank"/>.
        /// </summary>
        ThrusterVfxBank.Entry ResolveEntryForMount(Transform thrusterTransform)
        {
            ThrusterVfxBank bank = ThrusterVfxBank.LoadDefault();
            if (bank == null)
                return null;

            if (TitanOrbitDebugFlags.CycleAllThrusterVfx)
            {
                ThrusterVfxBank.Entry cycled = bank.GetEntry(ThrusterVfxBank.DebugCycleIndex);
                if (cycled != null)
                    return cycled;
            }

            if (thrusterTransform != null)
            {
                var marker = thrusterTransform.GetComponent<ShipPartVisualSource>();
                if (marker != null && !string.IsNullOrWhiteSpace(marker.sourceFamilyId))
                {
                    ThrusterVfxBank.Entry fromMarker = bank.GetEntryByFamilyId(marker.sourceFamilyId);
                    if (fromMarker != null)
                        return fromMarker;
                }
            }

            if (_family != null && !string.IsNullOrWhiteSpace(_family.familyId))
                return bank.GetEntryByFamilyId(_family.familyId);

            return bank.GetEntryByFamilyId(_familyPrefix);
        }

        /// <summary>
        /// Family signature / color bank first; then scene bank by mount color name;
        /// then ModularJetFlame2 fallback.
        /// </summary>
        GameObject ResolveThrusterVfxPrefabForTransform(
            Transform thrusterTransform,
            ThrusterVfxBank.Entry familyEntry)
        {
            if (familyEntry != null)
            {
                GameObject fromFamily = familyEntry.ResolvePrefab(
                    thrusterTransform != null ? thrusterTransform.name : null);
                if (fromFamily != null)
                    return fromFamily;
            }

            if (_settings.thrusterJetFlameBank != null && _settings.thrusterJetFlameBank.Count > 0)
            {
                string color = ExtractColorNameFromText(thrusterTransform != null ? thrusterTransform.name : null);
                if (!string.IsNullOrEmpty(color))
                {
                    for (int i = 0; i < _settings.thrusterJetFlameBank.Count; i++)
                    {
                        ThrusterVfxColorPrefab entry = _settings.thrusterJetFlameBank[i];
                        if (entry == null || entry.prefab == null || string.IsNullOrEmpty(entry.colorName))
                            continue;
                        if (string.Equals(entry.colorName, color, StringComparison.OrdinalIgnoreCase))
                            return entry.prefab;
                    }
                }

                for (int i = 0; i < _settings.thrusterJetFlameBank.Count; i++)
                {
                    ThrusterVfxColorPrefab entry = _settings.thrusterJetFlameBank[i];
                    if (entry != null && entry.prefab != null)
                        return entry.prefab;
                }
            }

            return _settings.thrusterVfxPrefab;
        }

        static string ExtractColorNameFromText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;

            for (int i = 0; i < VfxColorNames.Length; i++)
            {
                string color = VfxColorNames[i];
                if (value.IndexOf(color, StringComparison.OrdinalIgnoreCase) >= 0)
                    return color;
            }

            return null;
        }

        /// <summary>Stash-restore can leave untracked jet children; drop them before a rebuild.</summary>
        static void DestroyOrphanJetInstances(Transform root, bool immediate = false)
        {
            if (root == null)
                return;

            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = all.Length - 1; i >= 0; i--)
            {
                Transform t = all[i];
                if (t == null || t == root)
                    continue;
                if (t.name != ThrusterVfxBank.JetInstanceName)
                    continue;
                if (immediate)
                    UnityEngine.Object.DestroyImmediate(t.gameObject);
                else
                    UnityEngine.Object.Destroy(t.gameObject);
            }
        }

        static void CollectParticleSystems(GameObject root, List<ParticleSystem> target)
        {
            ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] != null)
                    target.Add(systems[i]);
            }
        }

        void ClearVfxInstances(bool immediate = false)
        {
            for (int i = 0; i < _engineVfxInstances.Count; i++)
            {
                if (_engineVfxInstances[i] != null)
                {
                    if (immediate)
                        DestroyImmediate(_engineVfxInstances[i]);
                    else
                        Destroy(_engineVfxInstances[i]);
                }
            }

            for (int i = 0; i < _thrusterVfxInstances.Count; i++)
            {
                if (_thrusterVfxInstances[i] != null)
                {
                    if (immediate)
                        DestroyImmediate(_thrusterVfxInstances[i]);
                    else
                        Destroy(_thrusterVfxInstances[i]);
                }
            }

            _engineVfxInstances.Clear();
            _thrusterVfxInstances.Clear();
            _thrusterJets.Clear();
            _engineParticleSystems.Clear();
            _thrusterParticleSystems.Clear();
            _initialized = false;
        }

        /// <summary>
        /// Local simulation so the cone stays on the nozzle. Authored trails / ribbons stay on
        /// (V1 JetFlame ribbon). Start size / speed / lifetime stay untouched.
        /// </summary>
        static void ConfigureThrusterParticles(GameObject root)
        {
            if (root == null)
                return;

            ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null)
                    continue;

                var main = ps.main;
                main.playOnAwake = false;
                main.simulationSpace = ParticleSystemSimulationSpace.Local;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        void UpdateJetPoses()
        {
            Vector3 aft = ResolveShipAft();
            for (int i = 0; i < _thrusterJets.Count; i++)
            {
                JetBind jet = _thrusterJets[i];
                if (jet == null || jet.instance == null)
                    continue;

                PlaceAndScaleJet(jet, aft);
            }
        }

        void PlaceAndScaleJet(JetBind jet, Vector3 aft)
        {
            GameObject go = jet.instance;
            Vector3 rear;
            if (jet.hasMountLocalRear && jet.mount != null)
                rear = jet.mount.TransformPoint(jet.mountLocalRear);
            else if (jet.mount != null)
                rear = jet.mount.position;
            else
                rear = transform.position;

            Quaternion rot = Quaternion.LookRotation(aft, transform.up);
            go.transform.SetPositionAndRotation(rear, rot);

            // Mount size vs the hull from localScale only. lossyScale is wrong when
            // BankPivot rolls (asteroid grind yaw) — Unity's approximation balloons.
            float componentRel = jet.mount != null
                ? LocalHierarchyScale(jet.mount, transform)
                : 1f;

            float sizeMul = Mathf.Max(0.01f, jet.chassisMountScale) * _megaVfxScale * componentRel;
            if (sizeMul > MaxVfxSizeMul)
                sizeMul = MaxVfxSizeMul;
            Vector3 authored = jet.authoredLocalScale;
            if (authored.sqrMagnitude < 0.0001f)
                authored = Vector3.one;

            // Length-only fade (local Z = ship-aft after LookRotation). XY stays at authored thickness.
            // blend is already idle…1 — do not lerp from 0 or the idle puff disappears.
            float lengthFactor = Mathf.Clamp(jet.blend, MinIdleBlend, 1f);
            go.transform.localScale = new Vector3(
                authored.x * sizeMul,
                authored.y * sizeMul,
                authored.z * sizeMul * lengthFactor);
        }

        Vector3 ResolveShipAft()
        {
            Vector3 aft = -transform.forward;
            aft.y = 0f;
            if (aft.sqrMagnitude < 0.0001f)
                aft = -transform.forward;
            return aft.normalized;
        }

        static float AverageAbsScale(Vector3 scale)
        {
            return (Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3f;
        }

        /// <summary>
        /// Product of localScale averages from <paramref name="t"/> up to (not including)
        /// <paramref name="stopExclusive"/>. Ignores parent rotation, unlike lossyScale.
        /// </summary>
        static float LocalHierarchyScale(Transform t, Transform stopExclusive)
        {
            float mul = 1f;
            Transform walk = t;
            while (walk != null && walk != stopExclusive)
            {
                mul *= AverageAbsScale(walk.localScale);
                walk = walk.parent;
            }

            if (stopExclusive != null && walk != stopExclusive)
                return 1f;
            if (mul < 0.0001f)
                return 1f;
            return mul;
        }

        static Renderer[] CollectMountRenderers(Transform mount)
        {
            if (mount == null)
                return Array.Empty<Renderer>();

            Renderer[] all = mount.GetComponentsInChildren<Renderer>(true);
            int count = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (IsMountMeshRenderer(all[i]))
                    count++;
            }

            if (count == 0)
                return Array.Empty<Renderer>();

            var filtered = new Renderer[count];
            int w = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (!IsMountMeshRenderer(all[i]))
                    continue;
                filtered[w++] = all[i];
            }

            return filtered;
        }

        static bool IsMountMeshRenderer(Renderer renderer)
        {
            if (renderer == null || !renderer.enabled)
                return false;
            if (renderer.transform.name == ThrusterVfxBank.JetInstanceName)
                return false;
            return !(renderer is ParticleSystemRenderer);
        }

        /// <summary>
        /// Rear of the mount mesh in mount-local space, along ship aft at bind time.
        /// Uses renderer local AABB corners so yaw does not pick a new world-AABB corner.
        /// </summary>
        static bool TryComputeMountLocalRear(JetBind jet, Vector3 worldAft, out Vector3 localRear)
        {
            localRear = Vector3.zero;
            if (jet == null || jet.mount == null)
                return false;

            if (jet.mountRenderers == null || jet.mountRenderers.Length == 0)
                jet.mountRenderers = CollectMountRenderers(jet.mount);

            if (!TryEncapsulateMountLocalBounds(jet.mount, jet.mountRenderers, out Bounds localBounds))
                return false;

            Vector3 localAft = jet.mount.InverseTransformDirection(worldAft);
            if (localAft.sqrMagnitude < 0.0001f)
                localAft = Vector3.back;
            localAft.Normalize();

            localRear = localBounds.ClosestPoint(
                localBounds.center + localAft * (localBounds.size.magnitude + 8f));
            return true;
        }

        static bool TryEncapsulateMountLocalBounds(Transform mount, Renderer[] renderers, out Bounds localBounds)
        {
            localBounds = default;
            if (mount == null)
                return false;

            bool any = false;
            if (renderers != null)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer r = renderers[i];
                    if (!IsMountMeshRenderer(r))
                        continue;

                    Bounds rb = r.localBounds;
                    Vector3 ext = rb.extents;
                    Vector3 center = rb.center;
                    for (int xi = -1; xi <= 1; xi += 2)
                    {
                        for (int yi = -1; yi <= 1; yi += 2)
                        {
                            for (int zi = -1; zi <= 1; zi += 2)
                            {
                                Vector3 world = r.transform.TransformPoint(
                                    center + new Vector3(ext.x * xi, ext.y * yi, ext.z * zi));
                                Vector3 mountLocal = mount.InverseTransformPoint(world);
                                if (!any)
                                {
                                    localBounds = new Bounds(mountLocal, Vector3.zero);
                                    any = true;
                                }
                                else
                                    localBounds.Encapsulate(mountLocal);
                            }
                        }
                    }
                }
            }

            if (any)
                return true;

            localBounds = new Bounds(Vector3.zero, Vector3.one * 0.35f);
            return true;
        }
    }
}
