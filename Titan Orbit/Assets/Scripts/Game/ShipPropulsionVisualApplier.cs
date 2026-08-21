using System;
using System.Collections.Generic;
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
    /// <see cref="ShipInput"/> on Local Host. Do not treat bounce speed as thrust.
    /// Does not drive simulation.
    /// Attached by <see cref="EcsWorldVisualizer"/> when spawning ship hull proxies.
    /// Cosmetic smoothing of particle emission is intentional — never applied to ship transform position.
    /// <para>
    /// Prefabs must load via <c>Resources.Load</c> in player builds — SampleScene often leaves the
    /// propulsion bank empty and falls back to <see cref="LoadDefaultSettings"/>. Editor-only
    /// AssetDatabase paths return null on Windows clients, which left ships with no flames.
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
    /// release/re-click of the thrust button. OVERDRIVE thruster bloom comes from parent mount
    /// scale (<see cref="ShipComponentAttributeScaleApplier"/>) — do not multiply jet localScale
    /// again or flames double-grow.
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
        const float ThrusterEmissionRate = 15f;

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

        readonly List<GameObject> _engineVfxInstances = new List<GameObject>();
        readonly List<GameObject> _thrusterVfxInstances = new List<GameObject>();
        readonly List<float> _thrusterMountScales = new List<float>();
        readonly List<ParticleSystem> _engineParticleSystems = new List<ParticleSystem>();
        readonly List<ParticleSystem> _thrusterParticleSystems = new List<ParticleSystem>();

        bool _lastEngineMoving;
        bool _lastThrusterActive;
        float _thrusterVfxBlend;

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
                thrusterVfxIdleScale = 0.1f,
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
            _megaVfxScale = ResolveMegaVfxScale(shipEntity);
            RebuildVfx();
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
            ClearVfxInstances();
            _thrusterMountScales.Clear();
            _lastEngineMoving = false;
            _lastThrusterActive = false;
            _thrusterVfxBlend = 0f;
            _forceRestartPending = false;

            // --- Find Engine_* / VFX-enabled thruster mounts on the hybrid hull ---
            // [TITAN-ORBIT] thrusterVfxTransforms = enablePropulsionVfx only
            // (Thrusters_Big / Tiny_Thrusters yes; Thruster_Place / Cover no).
            // thrusterTransforms is the attribute-scale group (includes covers) — not used for particles.
            bool mega = _megaVfxScale > 1.01f;
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

            if (_settings.HasAnyThrusterPrefab)
            {
                for (int i = 0; i < stats.thrusterVfxTransforms.Count; i++)
                {
                    Transform t = stats.thrusterVfxTransforms[i];
                    if (t == null)
                        continue;

                    GameObject prefab = ResolveThrusterVfxPrefabForTransform(t);
                    if (prefab == null)
                        continue;

                    float mountScale = 1f;
                    if (stats.thrusterVfxScales != null && i < stats.thrusterVfxScales.Count)
                        mountScale = Mathf.Max(0.01f, stats.thrusterVfxScales[i]);

                    GameObject go = Instantiate(prefab, t);
                    go.transform.localPosition = _settings.thrusterVfxLocalOffset;
                    go.transform.localRotation = Quaternion.Euler(_settings.thrusterVfxLocalEuler);
                    go.transform.localScale = Vector3.one * (Mathf.Clamp01(_settings.thrusterVfxIdleScale) * mountScale * _megaVfxScale);
                    VfxUrpCompat.PrepareVfxInstance(go);
                    _thrusterVfxInstances.Add(go);
                    _thrusterMountScales.Add(mountScale);
                    CollectParticleSystems(go, _thrusterParticleSystems);
                }
            }

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
        /// Each frame: read held thrust (and optional coast speed) and drive particle emission / scale.
        /// Runs after ship proxy transforms have been synced for the frame, and after
        /// <see cref="ShipComponentAttributeScaleApplier"/> (order 95) so territory mount scale
        /// cannot leave jets stopped for a full frame.
        /// </summary>
        void LateUpdate()
        {
            // --- Guard: no prefab instances or unbound entity ---
            if (!_initialized || _shipEntity == Entity.Null)
                return;

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
                    SetThrusterVfxBlend(0f);
                    _lastEngineMoving = false;
                    _lastThrusterActive = false;
                    return;
                }
            }

            // --- Held thrust (button), not hull speed ---
            // [TITAN-ORBIT] Must stay lit while grinding (v≈0) and while GhostSpawnBacklog skips
            // ShipInputApplySystem — see ResolveThrustHeld.
            bool thrusting = ResolveThrustHeld(em);

            // Speed only for optional engine coast glow — never gates thruster jets.
            float speed = 0f;
            if (em.HasComponent<ShipKinematics>(_shipEntity))
            {
                float3 vel = em.GetComponentData<ShipKinematics>(_shipEntity).Velocity;
                vel.y = 0f;
                speed = math.length(vel);
            }

            bool moving = speed >= EngineSpeedThreshold;
            // Remotes: bounce speed must not light engines. Local keeps optional coast glow.
            bool engineActive = thrusting || (IsLocalOwnerProxy(em) && moving);
            // [TITAN-ORBIT] Thrusters = thrust button only. Do not AND with moving (grind bug).
            bool showThrusters = thrusting;
            float targetThrusterBlend = showThrusters ? 1f : 0f;
            float transitionSpeed = Mathf.Max(0.01f, _settings.thrusterVfxTransitionSpeed);
            // [TITAN-ORBIT] Cosmetic blend — acceptable on VFX only, not ship transform.
            _thrusterVfxBlend = Mathf.MoveTowards(_thrusterVfxBlend, targetThrusterBlend, transitionSpeed * Time.deltaTime);
            bool thrusterTransitionActive = Mathf.Abs(_thrusterVfxBlend - targetThrusterBlend) > 0.0001f;

            // --- Stuck particles while input still held ---
            // [TITAN-ORBIT] Territory AttributeScale (or any parent transform mutate) can stop
            // ModularJetFlame ParticleSystems without clearing ShipPendingInput.Thrust. The old
            // early-out trusted _lastThrusterActive alone → flames stayed dark until re-click.
            bool thrusterVfxNeedsRestart =
                _forceRestartPending ||
                (showThrusters && AnyParticleStopped(_thrusterParticleSystems));
            bool engineVfxNeedsRestart =
                _forceRestartPending ||
                (engineActive && AnyParticleStopped(_engineParticleSystems));

            // --- Skip particle writes when nothing changed and jets are healthy ---
            if (engineActive == _lastEngineMoving &&
                showThrusters == _lastThrusterActive &&
                !thrusterTransitionActive &&
                !thrusterVfxNeedsRestart &&
                !engineVfxNeedsRestart)
                return;

            _lastEngineMoving = engineActive;
            _lastThrusterActive = showThrusters;
            _forceRestartPending = false;

            // [TITAN-ORBIT] forceRestart clears + Play when territory scale (or similar) killed jets
            // while isPlaying may still read true on some Sci-Fi Arsenal setups.
            SetEngineVfxActive(engineActive, engineActive ? EngineEmissionRate : 0f, engineVfxNeedsRestart);
            SetThrusterVfxBlend(_thrusterVfxBlend, thrusterVfxNeedsRestart);
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
        /// else local <see cref="ShipKinematics.ThrustHeld"/> (not networked).
        /// </summary>
        /// <param name="em">Visualization world entity manager for this proxy's ship.</param>
        /// <returns>True while the thrust control is held — never bounce / coast speed.</returns>
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

            if (em.HasComponent<ShipKinematics>(_shipEntity) &&
                em.GetComponentData<ShipKinematics>(_shipEntity).ThrustHeld != 0)
                return true;

            return em.HasComponent<ShipInput>(_shipEntity) &&
                   em.GetComponentData<ShipInput>(_shipEntity).Thrust;
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
        /// Drives thruster jet local scale and emission from the 0–1 blend (idle → full thrust).
        /// </summary>
        /// <param name="blend">Current cosmetic blend toward held thrust (1 = full jets).</param>
        /// <param name="forceRestart">
        /// When true, hard-restart particle systems after thruster mount scale changes.
        /// </param>
        void SetThrusterVfxBlend(float blend, bool forceRestart = false)
        {
            float idle = Mathf.Clamp01(_settings.thrusterVfxIdleScale);
            float scaleLerp = Mathf.Lerp(idle, 1f, blend);

            for (int i = 0; i < _thrusterVfxInstances.Count; i++)
            {
                GameObject go = _thrusterVfxInstances[i];
                if (go == null)
                    continue;

                // [TITAN-ORBIT] Per-mount scale from ProfileSet (Big / Tiny) × idle→thrust blend.
                // OVERDRIVE size comes from parent thruster mount AttributeScale — not here.
                float mountScale = i < _thrusterMountScales.Count ? _thrusterMountScales[i] : 1f;
                float finalScale = scaleLerp * Mathf.Max(0.01f, mountScale) * _megaVfxScale;
                go.transform.localScale = Vector3.one * finalScale;
                bool visible = finalScale > 0.0005f;
                if (go.activeSelf != visible)
                    go.SetActive(visible);
            }

            for (int i = 0; i < _thrusterParticleSystems.Count; i++)
            {
                ParticleSystem ps = _thrusterParticleSystems[i];
                if (ps == null)
                    continue;

                var emission = ps.emission;
                emission.enabled = true;
                emission.rateOverTime = ThrusterEmissionRate * blend;
                // --- Play / restart while blend is lit ---
                if (blend > 0.001f && forceRestart)
                    RestartParticleSystem(ps);
                else if (blend > 0.001f && !ps.isPlaying)
                    ps.Play();
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

        /// <summary>Picks thruster prefab by color name embedded in transform name (e.g. "Thruster_Red").</summary>
        GameObject ResolveThrusterVfxPrefabForTransform(Transform thrusterTransform)
        {
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

        static void CollectParticleSystems(GameObject root, List<ParticleSystem> target)
        {
            ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] != null)
                    target.Add(systems[i]);
            }
        }

        void ClearVfxInstances()
        {
            for (int i = 0; i < _engineVfxInstances.Count; i++)
            {
                if (_engineVfxInstances[i] != null)
                    Destroy(_engineVfxInstances[i]);
            }

            for (int i = 0; i < _thrusterVfxInstances.Count; i++)
            {
                if (_thrusterVfxInstances[i] != null)
                    Destroy(_thrusterVfxInstances[i]);
            }

            _engineVfxInstances.Clear();
            _thrusterVfxInstances.Clear();
            _engineParticleSystems.Clear();
            _thrusterParticleSystems.Clear();
            _initialized = false;
        }
    }
}
