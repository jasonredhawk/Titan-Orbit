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
    /// Prefabs resolve from <see cref="ThrusterVfxBank"/>: T-key debug cycle first,
    /// then the player's Customize Ship style, otherwise the shared default
    /// (AstroEagle). Family id and remapped <see cref="ShipPartVisualSource"/> no
    /// longer pick a unique flame. Fallback is <see cref="LoadDefaultSettings"/> / ModularJetFlame2.
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
        /// <summary>Studio jets sit a bit small vs the match follow-cam; +10% on the hull.</summary>
        const float PreviewJetScaleMul = 1.1f;

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
        bool _previewMode;
        float _previewForward = 1f;
        float _previewTurn;
        bool _previewJetsPrimed;
        TeamId _previewTeam = TeamId.TeamA;
        string _familyPrefix = "AstroEagle";
        ShipFamilyDefinition _family;
        Settings _settings;
        bool _initialized;
        float _megaVfxScale = 1f;
        bool _isMega;

        readonly List<GameObject> _engineVfxInstances = new List<GameObject>();
        readonly List<GameObject> _thrusterVfxInstances = new List<GameObject>();
        readonly List<JetBind> _thrusterJets = new List<JetBind>();
        readonly List<JetBind> _engineJets = new List<JetBind>();
        readonly List<ParticleSystem> _engineParticleSystems = new List<ParticleSystem>();
        readonly List<ParticleSystem> _thrusterParticleSystems = new List<ParticleSystem>();
        static readonly List<ShipPropulsionVisualApplier> s_Live = new List<ShipPropulsionVisualApplier>(8);

        bool _lastEngineMoving;
        bool _lastThrusterActive;
        int _appliedDebugCycleKey = int.MinValue;
        int _appliedStyleIndex = int.MinValue;
        string _appliedFlameColorName;
        Gradient _appliedLifetime;
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
            public Color[] originalStartColors;
            public Material[] tintMaterials;
            public TrailRenderer[] trails;
            public bool[] authoredColEnabled;
            public Gradient[] authoredColGradients;
            /// <summary>
            /// Rear nozzle in mount-local space (mesh AABB, not world AABB).
            /// World <c>Renderer.bounds</c> is axis-aligned, so ClosestPoint jumped every yaw.
            /// </summary>
            public Vector3 mountLocalRear;
            public bool hasMountLocalRear;
            /// <summary>−1 port … 0 center … +1 starboard, from ship-local X.</summary>
            public float lateral;
            public float blend;
            public float appliedBlend;
            public float appliedSizeMul;
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
        /// Studio hull with no ghost. LateUpdate holds a steady flame so Customize
        /// Ship can show the live jet without an ECS ship.
        /// </summary>
        public void BindPreview(Settings settings, ShipFamilyDefinition family, TeamId team)
        {
            _previewMode = true;
            _previewTeam = team == TeamId.None ? TeamId.TeamA : team;
            Bind(Entity.Null, family != null ? family.familyId : "AstroEagle", settings, family);
        }

        /// <summary>0..1 thrust and −1..1 turn for studio jet length (same remap as the match).</summary>
        public void SetPreviewMotion(float forward01, float turn)
        {
            _previewForward = Mathf.Clamp01(forward01);
            _previewTurn = Mathf.Clamp(turn, -1f, 1f);
        }

        /// <summary>
        /// A–E preview strip. Follow-team swaps the authored colored JetFlame
        /// prefab; locked color only retints the current instances.
        /// </summary>
        public void SetPreviewTeam(TeamId team)
        {
            _previewTeam = team == TeamId.None ? TeamId.TeamA : team;
            RefreshFromCurrentStyle();
        }

        /// <summary>
        /// Links this applier to a ship ghost entity and rebuilds particle instances from chassis mounts.
        /// Called by <see cref="EcsWorldVisualizer"/> after the hybrid hull proxy is Instantiated.
        /// </summary>
        public void Bind(
            Entity shipEntity,
            string familyPrefix,
            Settings settings,
            ShipFamilyDefinition family = null)
        {
            // Visualizer Bind is never preview — BindPreview sets the flag first.
            if (shipEntity != Entity.Null)
                _previewMode = false;

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

        /// <summary>Rebuilds this proxy's jets after a studio style change.</summary>
        public void RebuildJets()
        {
            _appliedDebugCycleKey = CurrentDebugCycleKey();
            RebuildVfx();
        }

        /// <summary>
        /// Paint / lifetime stop change: retint. Type change: rebuild instances.
        /// Visualizer used to RebuildJets on every accent CacheKey, which destroyed
        /// the looping systems and looked like the flame was switching off.
        /// </summary>
        public void RefreshFromCurrentStyle()
        {
            int style = LocalPlayerThrusterStyle.ResolveStyleIndex(ResolveThrusterStyle());
            string color = ResolveFlameColorName();
            int debugKey = CurrentDebugCycleKey();
            if (style != _appliedStyleIndex ||
                !string.Equals(color, _appliedFlameColorName, StringComparison.Ordinal) ||
                debugKey != _appliedDebugCycleKey)
            {
                _appliedDebugCycleKey = debugKey;
                RebuildVfx();
                return;
            }

            ApplyCurrentTint();
        }

        /// <summary>
        /// Locked picker: keep the prefab Color-over-Lifetime (white + fade) so the
        /// stream stays continuous, and paint each particle layer from the lifetime
        /// wells via startColor. Default's mesh/glow have CoL off — age-based CoL
        /// only hit the 0.1s blast and pulsed. Team follow leaves the prefab as-is.
        /// </summary>
        public void ApplyCurrentTint()
        {
            var style = ResolveThrusterStyle();
            // Follow-team color lives on the prefab. Do not stamp the name here —
            // that would hide a needed RebuildVfx after the A–E preview strip.
            if (style.UseTeamColor)
                return;

            _appliedFlameColorName = ResolveFlameColorName();
            ApplyLockedJetAppearance();
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
                if (applier._shipEntity != Entity.Null || applier._previewMode)
                    applier.RebuildVfx();
            }
        }

        /// <summary>Tints every live proxy from current prefs / ghost (picker drag).</summary>
        public static void ApplyTintToAllLive()
        {
            for (int i = s_Live.Count - 1; i >= 0; i--)
            {
                ShipPropulsionVisualApplier applier = s_Live[i];
                if (applier == null)
                {
                    s_Live.RemoveAt(i);
                    continue;
                }

                if (applier._shipEntity != Entity.Null || applier._previewMode)
                    applier.ApplyCurrentTint();
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
        /// Instantiates one flame ring at <see cref="ChassisComponentStats"/> nozzle sites
        /// (thruster VFX mounts, or Engine_* when the hull has none).
        /// Sets <c>_initialized</c> only when at least one particle instance was created — otherwise
        /// LateUpdate exits early and the ship stays without thrust VFX.
        /// </summary>
        void RebuildVfx()
        {
            ClearVfxInstances(immediate: true);
            DestroyOrphanJetInstances(transform, immediate: true);
            _thrusterJets.Clear();
            _engineJets.Clear();
            _lastEngineMoving = false;
            _lastThrusterActive = false;
            _forceRestartPending = false;
            _yawSampleInitialized = false;
            _previewJetsPrimed = false;

            // --- Find Engine_* / VFX-enabled thruster mounts on the hybrid hull ---
            // [TITAN-ORBIT] thrusterVfxTransforms = enablePropulsionVfx only
            // (Thrusters_Big / Tiny_Thrusters yes; Thruster_Place / Cover no).
            // thrusterTransforms is the attribute-scale group (includes covers) — not used for particles.
            bool mega = _isMega;
            var stats = ChassisComponentStats.FromTransform(
                transform,
                mega ? string.Empty : _familyPrefix,
                mega ? null : _family);

            SuppressAuthoredJetFlames();

            // One flame ring only. Thruster VFX mounts are the nozzles (AstroEagle
            // stacks Engine_2 just ahead of Thruster). Spawning on both stacked a
            // second ring that bloom made obvious. Engine_* is fallback for
            // engine-only hulls (no enablePropulsionVfx mounts).
            GameObject stylePrefab = ResolveStylePrefab();
            bool hasThrusterJets = false;
            for (int i = 0; i < stats.thrusterVfxTransforms.Count; i++)
            {
                if (stats.thrusterVfxTransforms[i] != null)
                {
                    hasThrusterJets = true;
                    break;
                }
            }

            if (!hasThrusterJets)
            {
                foreach (Transform t in stats.engineTransforms)
                {
                    if (t == null || IsAlreadyThrusterVfxMount(t, stats))
                        continue;

                    GameObject prefab = stylePrefab != null ? stylePrefab : _settings.engineVfxPrefab;
                    if (prefab == null)
                        continue;

                    SpawnStyleJet(
                        prefab,
                        t,
                        mountScale: 1f,
                        _engineVfxInstances,
                        _engineJets,
                        _engineParticleSystems);
                }
            }

            for (int i = 0; i < stats.thrusterVfxTransforms.Count; i++)
            {
                Transform t = stats.thrusterVfxTransforms[i];
                if (t == null)
                    continue;

                GameObject prefab = stylePrefab != null ? stylePrefab : _settings.thrusterVfxPrefab;
                if (prefab == null)
                    continue;

                float mountScale = 1f;
                if (stats.thrusterVfxScales != null && i < stats.thrusterVfxScales.Count)
                    mountScale = Mathf.Max(0.01f, stats.thrusterVfxScales[i]);

                SpawnStyleJet(
                    prefab,
                    t,
                    mountScale,
                    _thrusterVfxInstances,
                    _thrusterJets,
                    _thrusterParticleSystems);
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
            if (!_initialized)
                return;

            if (_previewMode)
            {
                TickPreviewJets();
                return;
            }

            // --- Guard: unbound entity ---
            if (_shipEntity == Entity.Null)
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
        /// Customize Ship: same thrust + turn length remap as the match
        /// (<see cref="UpdateJetBlends"/>).
        /// </summary>
        void TickPreviewJets()
        {
            UpdateJetBlends(_previewForward, _previewTurn);
            UpdateJetPoses();
            _lastThrusterActive = true;
            _lastEngineMoving = true;
            _forceRestartPending = false;
            KeepPreviewJetsEmitting();
            _previewJetsPrimed = true;
        }

        /// <summary>Play any system that died. Does not Clear or deactivate instances.</summary>
        void KeepPreviewJetsEmitting()
        {
            PlayIfStopped(_engineParticleSystems);
            for (int i = 0; i < _thrusterJets.Count; i++)
            {
                JetBind jet = _thrusterJets[i];
                if (jet == null || jet.instance == null)
                    continue;
                if (!jet.instance.activeSelf)
                    jet.instance.SetActive(true);
                ParticleSystem[] systems = jet.particles;
                if (systems == null)
                    continue;
                for (int p = 0; p < systems.Length; p++)
                {
                    ParticleSystem ps = systems[p];
                    if (ps != null && !ps.isPlaying)
                        ps.Play();
                }
            }
        }

        static void PlayIfStopped(List<ParticleSystem> systems)
        {
            for (int i = 0; i < systems.Count; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps != null && !ps.isPlaying)
                    ps.Play();
            }
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
        /// Same helper the Customize Ship preview uses via <see cref="SetPreviewMotion"/>.
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
        /// port→starboard to full / medium / idle (right turn: left full, center
        /// medium, right idle). Match and Customize Ship share this remap.
        /// </summary>
        bool UpdateJetBlends(float forward, float turn)
        {
            float idle = ResolveIdleBlend();
            float baseLen = Mathf.Lerp(idle, 1f, Mathf.Clamp01(forward));
            float turnAbs = Mathf.Clamp01(Mathf.Abs(turn));
            float speed = Mathf.Max(0.01f, _settings.thrusterVfxTransitionSpeed);
            float dt = _previewMode ? Time.unscaledDeltaTime : Time.deltaTime;
            float step = speed * dt;
            bool moving = false;
            moving |= StepJetBlends(_thrusterJets, idle, baseLen, turn, turnAbs, step);
            moving |= StepJetBlends(_engineJets, idle, baseLen, turn, turnAbs, step);
            return moving;
        }

        bool StepJetBlends(
            List<JetBind> jets,
            float idle,
            float baseLen,
            float turn,
            float turnAbs,
            float step)
        {
            bool moving = false;
            for (int i = 0; i < jets.Count; i++)
            {
                JetBind jet = jets[i];
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
            bool thrusterSpread = AssignJetSides(_thrusterJets);
            bool engineSpread = AssignJetSides(_engineJets);
            _hasLateralSpread = thrusterSpread || engineSpread;
        }

        bool AssignJetSides(List<JetBind> jets)
        {
            float maxAbsX = 0f;
            for (int i = 0; i < jets.Count; i++)
            {
                JetBind jet = jets[i];
                if (jet == null || jet.mount == null)
                    continue;
                float x = Mathf.Abs(transform.InverseTransformPoint(jet.mount.position).x);
                if (x > maxAbsX)
                    maxAbsX = x;
            }

            bool spread = maxAbsX > 0.08f;
            for (int i = 0; i < jets.Count; i++)
            {
                JetBind jet = jets[i];
                if (jet == null || jet.mount == null)
                {
                    if (jet != null)
                        jet.lateral = 0f;
                    continue;
                }

                float x = transform.InverseTransformPoint(jet.mount.position).x;
                jet.lateral = spread ? Mathf.Clamp(x / maxAbsX, -1f, 1f) : 0f;
            }

            return spread;
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

        GameObject ResolveStylePrefab()
        {
            // T-key only overrides in-match. The studio preview always uses the
            // TYPE pick — CycleAllThrusterVfx used to pin DebugCycleIndex and
            // ignore Customize Ship entirely.
            int index = LocalPlayerThrusterStyle.ResolveStyleIndex(ResolveThrusterStyle());
            if (!_previewMode && TitanOrbitDebugFlags.CycleAllThrusterVfx)
                index = ThrusterVfxBank.WrapStyleIndex(ThrusterVfxBank.DebugCycleIndex);
            _appliedStyleIndex = index;
            _appliedFlameColorName = ResolveFlameColorName();
            return ThrusterVfxBank.LoadStylePrefab(index, _appliedFlameColorName);
        }

        string ResolveFlameColorName()
        {
            return LocalPlayerThrusterStyle.ResolveFlameColorName(ResolveThrusterStyle(), ResolveShipTeam());
        }

        static bool IsAlreadyThrusterVfxMount(Transform mount, ChassisComponentStats stats)
        {
            if (mount == null || stats == null || stats.thrusterVfxTransforms == null)
                return false;
            for (int i = 0; i < stats.thrusterVfxTransforms.Count; i++)
            {
                if (stats.thrusterVfxTransforms[i] == mount)
                    return true;
            }

            return false;
        }

        void SpawnStyleJet(
            GameObject prefab,
            Transform mount,
            float mountScale,
            List<GameObject> instances,
            List<JetBind> binds,
            List<ParticleSystem> particles)
        {
            if (prefab == null)
                return;

            Vector3 authoredScale = prefab.transform.localScale;
            GameObject go = Instantiate(prefab, transform);
            go.name = ThrusterVfxBank.JetInstanceName;
            VfxUrpCompat.PrepareVfxInstance(go, playParticles: false);
            ConfigureThrusterParticles(go, worldSpace: false, previewSteady: _previewMode);
            CopyLayerRecursive(go, gameObject.layer);

            var bind = new JetBind
            {
                instance = go,
                mount = mount,
                authoredLocalScale = authoredScale,
                chassisMountScale = Mathf.Max(0.01f, mountScale),
                mountRenderers = CollectMountRenderers(mount),
                particles = go.GetComponentsInChildren<ParticleSystem>(true),
                blend = ResolveIdleBlend()
            };
            go.SetActive(true);
            bind.hasMountLocalRear = TryComputeMountLocalRear(bind, ResolveShipAft(), out bind.mountLocalRear);
            if (!ResolveThrusterStyle().UseTeamColor)
                ApplyLockedJetAppearance(bind);
            instances.Add(go);
            binds.Add(bind);
            CollectParticleSystems(go, particles);
        }

        /// <summary>
        /// Hull prefabs can ship baked ModularJetFlame children. Those stay Modular
        /// across type changes and hide the player style — switch them off.
        /// </summary>
        void SuppressAuthoredJetFlames()
        {
            ParticleSystem[] systems = GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null)
                    continue;

                Transform flameRoot = FindAuthoredFlameRoot(ps.transform);
                if (flameRoot == null)
                    continue;

                flameRoot.gameObject.SetActive(false);
            }
        }

        Transform FindAuthoredFlameRoot(Transform start)
        {
            Transform walk = start;
            while (walk != null && walk != transform)
            {
                if (walk.name == ThrusterVfxBank.JetInstanceName)
                    return null;
                if (LooksLikeAuthoredFlameName(walk.name))
                    return walk;
                walk = walk.parent;
            }

            return null;
        }

        static bool LooksLikeAuthoredFlameName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            return name.IndexOf("JetFlame", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("ModularJet", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("ExhaustDust", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Owner prefs immediately; remotes read ghosted <see cref="ShipAccentColors"/>.</summary>
        LocalPlayerThrusterStyle.Style ResolveThrusterStyle()
        {
            if (_previewMode || _shipEntity == Entity.Null)
                return LocalPlayerThrusterStyle.Get();

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated || !world.EntityManager.Exists(_shipEntity))
                return LocalPlayerThrusterStyle.Get();

            var em = world.EntityManager;
            ShipAccentColors ghost = default;
            if (em.HasComponent<ShipAccentColors>(_shipEntity))
                ghost = em.GetComponentData<ShipAccentColors>(_shipEntity);

            // Same owner test as the visualizer (NetworkId), not LocalPlayerShipTag —
            // that tag can lag and left locked color reading a Default ghost.
            return LocalPlayerThrusterStyle.ResolveForPresentation(IsStyleOwner(em), ghost);
        }

        bool IsStyleOwner(EntityManager em)
        {
            int localId = EcsGameBridge.GetLocalNetworkId();
            if (localId > 0 && em.HasComponent<GhostOwner>(_shipEntity))
            {
                if (em.GetComponentData<GhostOwner>(_shipEntity).NetworkId == localId)
                    return true;
            }

            return IsLocalOwnerProxy(em);
        }

        static readonly int TintColorId = Shader.PropertyToID("_TintColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        void CaptureAppliedLifetime(Gradient source)
        {
            if (source == null)
                return;
            if (_appliedLifetime == null)
                _appliedLifetime = new Gradient();
            _appliedLifetime.mode = source.mode;
            _appliedLifetime.SetKeys(source.colorKeys, source.alphaKeys);
        }

        void ApplyLockedJetAppearance()
        {
            ApplyLockedJetAppearance(null);
        }

        void ApplyLockedJetAppearance(JetBind only)
        {
            var style = ResolveThrusterStyle();
            TeamId team = ResolveShipTeam();
            Color32 tint = LocalPlayerThrusterStyle.ResolveTint(style, team);
            CaptureAppliedLifetime(LocalPlayerThrusterStyle.ResolveLifetime(style, team));
            if (only != null)
            {
                TintJetParticles(only, tint, _appliedLifetime);
                return;
            }

            for (int i = 0; i < _thrusterJets.Count; i++)
                TintJetParticles(_thrusterJets[i], tint, _appliedLifetime);
            for (int i = 0; i < _engineJets.Count; i++)
                TintJetParticles(_engineJets[i], tint, _appliedLifetime);
        }

        static readonly GradientAlphaKey[] OpaqueAlphaKeys =
        {
            new GradientAlphaKey(1f, 0f),
            new GradientAlphaKey(1f, 1f),
        };

        void TintJetParticles(JetBind jet, Color32 tint32, Gradient lifetime)
        {
            if (jet == null || jet.instance == null)
                return;

            Color tint = tint32;
            tint.a = 1f;

            if (jet.particles == null)
                jet.particles = jet.instance.GetComponentsInChildren<ParticleSystem>(true);

            if (jet.originalStartColors == null || jet.originalStartColors.Length != jet.particles.Length)
            {
                jet.originalStartColors = new Color[jet.particles.Length];
                for (int i = 0; i < jet.particles.Length; i++)
                {
                    ParticleSystem ps = jet.particles[i];
                    jet.originalStartColors[i] = ps != null
                        ? ps.main.startColor.color
                        : Color.white;
                }
            }

            CacheAuthoredColorOverLifetime(jet);

            if (jet.tintMaterials == null)
                jet.tintMaterials = CollectInstanceMaterials(jet.instance);
            if (jet.trails == null)
                jet.trails = jet.instance.GetComponentsInChildren<TrailRenderer>(true);

            int layerCount = jet.particles.Length;
            for (int i = 0; i < layerCount; i++)
            {
                ParticleSystem ps = jet.particles[i];
                if (ps == null)
                    continue;

                RestoreAuthoredColorOverLifetime(jet, i);

                Color orig = jet.originalStartColors[i];
                Color next;
                if (lifetime != null)
                    next = SampleLayerColor(lifetime, i, layerCount);
                else
                {
                    Color.RGBToHSV(orig, out _, out float s, out float v);
                    next = s < 0.2f && v > 0.65f
                        ? Color.Lerp(Color.white, tint, 0.72f)
                        : Color.Lerp(orig, tint, 0.92f);
                }

                next.a = orig.a > 0.05f ? orig.a : 1f;
                var main = ps.main;
                main.startColor = new ParticleSystem.MinMaxGradient(next);

                if (lifetime == null)
                    continue;

                var rend = ps.GetComponent<ParticleSystemRenderer>();
                if (rend == null)
                    continue;
                Material[] mats = rend.materials;
                for (int m = 0; m < mats.Length; m++)
                {
                    if (mats[m] != null)
                        ApplyLayerMaterialTint(mats[m], next);
                }
            }

            if (lifetime != null)
            {
                Color trailStart = SampleLayerColor(lifetime, 0, Mathf.Max(1, layerCount));
                Color trailEnd = SampleLayerColor(lifetime, Mathf.Max(0, layerCount - 1), Mathf.Max(1, layerCount));
                trailEnd.a = 0.12f;
                for (int i = 0; i < jet.trails.Length; i++)
                {
                    TrailRenderer trail = jet.trails[i];
                    if (trail == null)
                        continue;
                    trail.startColor = trailStart;
                    trail.endColor = trailEnd;
                }

                return;
            }

            for (int i = 0; i < jet.tintMaterials.Length; i++)
            {
                Material mat = jet.tintMaterials[i];
                if (mat == null)
                    continue;
                ApplyMaterialTint(mat, tint);
            }

            Color fallbackEnd = tint;
            fallbackEnd.a = 0.12f;
            for (int i = 0; i < jet.trails.Length; i++)
            {
                TrailRenderer trail = jet.trails[i];
                if (trail == null)
                    continue;
                trail.startColor = tint;
                trail.endColor = fallbackEnd;
            }
        }

        static Color SampleLayerColor(Gradient lifetime, int layerIndex, int layerCount)
        {
            float t = layerCount <= 1
                ? 0.35f
                : (layerIndex + 0.5f) / layerCount;
            Color c = lifetime.Evaluate(t);
            Color.RGBToHSV(c, out float h, out float s, out float v);
            if (v < 0.28f)
                v = 0.28f;
            c = Color.HSVToRGB(h, s, v);
            c.a = 1f;
            return c;
        }

        static void CacheAuthoredColorOverLifetime(JetBind jet)
        {
            if (jet.particles == null)
                return;
            if (jet.authoredColEnabled != null &&
                jet.authoredColEnabled.Length == jet.particles.Length)
                return;

            int n = jet.particles.Length;
            jet.authoredColEnabled = new bool[n];
            jet.authoredColGradients = new Gradient[n];
            for (int i = 0; i < n; i++)
            {
                ParticleSystem ps = jet.particles[i];
                if (ps == null)
                    continue;

                var col = ps.colorOverLifetime;
                jet.authoredColEnabled[i] = col.enabled;
                if (col.enabled)
                    jet.authoredColGradients[i] = CopyMinMaxGradient(col.color);
            }
        }

        static void RestoreAuthoredColorOverLifetime(JetBind jet, int index)
        {
            if (jet.particles == null || index < 0 || index >= jet.particles.Length)
                return;

            ParticleSystem ps = jet.particles[index];
            if (ps == null || jet.authoredColEnabled == null || index >= jet.authoredColEnabled.Length)
                return;

            var col = ps.colorOverLifetime;
            col.enabled = jet.authoredColEnabled[index];
            if (!col.enabled ||
                jet.authoredColGradients == null ||
                jet.authoredColGradients[index] == null)
                return;

            col.color = new ParticleSystem.MinMaxGradient(jet.authoredColGradients[index]);
        }

        static Gradient CopyMinMaxGradient(ParticleSystem.MinMaxGradient mm)
        {
            Gradient src = null;
            switch (mm.mode)
            {
                case ParticleSystemGradientMode.TwoGradients:
                    src = mm.gradientMax;
                    break;
                case ParticleSystemGradientMode.Gradient:
                case ParticleSystemGradientMode.RandomColor:
                    src = mm.gradient;
                    break;
            }

            var copy = new Gradient();
            if (src != null)
            {
                copy.mode = src.mode;
                copy.SetKeys(src.colorKeys, src.alphaKeys);
                return copy;
            }

            Color solid = mm.color;
            copy.SetKeys(
                new[] { new GradientColorKey(solid, 0f), new GradientColorKey(solid, 1f) },
                OpaqueAlphaKeys);
            return copy;
        }

        static void ApplyLayerMaterialTint(Material mat, Color layer)
        {
            if (mat.HasProperty(ColorId))
                mat.SetColor(ColorId, layer);
            if (mat.HasProperty(BaseColorId))
                mat.SetColor(BaseColorId, layer);
            if (mat.HasProperty(TintColorId))
            {
                Color particleTint = layer;
                particleTint.a = 0.5f;
                mat.SetColor(TintColorId, particleTint);
            }
        }

        static void ApplyMaterialTint(Material mat, Color tint)
        {
            if (mat.HasProperty(TintColorId))
                mat.SetColor(TintColorId, tint);
            if (mat.HasProperty(ColorId))
                mat.SetColor(ColorId, tint);
            if (mat.HasProperty(BaseColorId))
                mat.SetColor(BaseColorId, tint);
            if (mat.HasProperty(EmissionColorId))
                mat.SetColor(EmissionColorId, tint * 1.6f);
            mat.color = tint;
        }

        static Material[] CollectInstanceMaterials(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            int count = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    count += renderers[i].sharedMaterials.Length;
            }

            var mats = new Material[count];
            int n = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer rend = renderers[i];
                if (rend == null)
                    continue;
                Material[] instanced = rend.materials;
                for (int m = 0; m < instanced.Length; m++)
                    mats[n++] = instanced[m];
            }

            return mats;
        }

        static void CopyLayerRecursive(GameObject go, int layer)
        {
            if (go == null)
                return;
            go.layer = layer;
            Transform t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                CopyLayerRecursive(t.GetChild(i).gameObject, layer);
        }

        TeamId ResolveShipTeam()
        {
            if (_previewMode)
                return _previewTeam;

            if (_shipEntity == Entity.Null)
                return TeamId.None;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated || !world.EntityManager.Exists(_shipEntity))
                return TeamId.None;

            var em = world.EntityManager;
            if (!em.HasComponent<ShipState>(_shipEntity))
                return TeamId.None;
            return em.GetComponentData<ShipState>(_shipEntity).Team;
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
            _engineJets.Clear();
            _engineParticleSystems.Clear();
            _thrusterParticleSystems.Clear();
            _initialized = false;
        }

        /// <summary>
        /// Local simulation so the cone stays on the nozzle. Authored trails / ribbons stay on
        /// (V1 JetFlame ribbon). Start size / speed / lifetime stay untouched.
        /// </summary>
        static void ConfigureThrusterParticles(GameObject root, bool worldSpace, bool previewSteady)
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
                main.loop = true;
                main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
                main.simulationSpace = worldSpace
                    ? ParticleSystemSimulationSpace.World
                    : ParticleSystemSimulationSpace.Local;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                if (!previewSteady)
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

                if (worldSpace)
                {
                    var ribbon = ps.trails;
                    if (ribbon.enabled)
                        ribbon.lifetimeMultiplier = Mathf.Max(ribbon.lifetimeMultiplier, 1.2f);
                }
            }

            if (!worldSpace)
                return;

            TrailRenderer[] trails = root.GetComponentsInChildren<TrailRenderer>(true);
            for (int i = 0; i < trails.Length; i++)
            {
                if (trails[i] == null)
                    continue;
                trails[i].time = Mathf.Max(trails[i].time, 0.85f);
                trails[i].minVertexDistance = Mathf.Min(trails[i].minVertexDistance, 0.08f);
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

            for (int i = 0; i < _engineJets.Count; i++)
            {
                JetBind jet = _engineJets[i];
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
            // Ignore sub-percent hierarchy noise (bank / interpolation) so Z length
            // is not rewritten every frame while the player is holding thrust.
            if (jet.appliedSizeMul > 0.01f &&
                Mathf.Abs(sizeMul - jet.appliedSizeMul) < jet.appliedSizeMul * 0.01f)
                sizeMul = jet.appliedSizeMul;
            else
                jet.appliedSizeMul = sizeMul;
            Vector3 authored = jet.authoredLocalScale;
            if (authored.sqrMagnitude < 0.0001f)
                authored = Vector3.one;

            float thickness = _previewMode ? PreviewJetScaleMul : 1f;
            float lengthFactor = Mathf.Clamp(jet.blend, MinIdleBlend, 1f);
            if (_previewMode)
                lengthFactor *= PreviewJetScaleMul;
            jet.appliedBlend = lengthFactor;
            Vector3 nextScale = new Vector3(
                authored.x * sizeMul * thickness,
                authored.y * sizeMul * thickness,
                authored.z * sizeMul * lengthFactor);
            // Rewriting an identical scale still dirties the transform and can
            // hitch Hierarchy-scaled ParticleSystems (reads as a blink).
            if ((go.transform.localScale - nextScale).sqrMagnitude > 1e-8f)
                go.transform.localScale = nextScale;
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
