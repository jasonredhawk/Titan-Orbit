using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side engine and thruster jet VFX on ship GameObject proxies (ported from legacy Starship).
    /// Reads ShipKinematics velocity and ShipInput.Thrust from the visualization ECS world each LateUpdate;
    /// does not drive simulation. Attached by EcsWorldVisualizer when spawning ship hull proxies.
    /// Cosmetic smoothing of particle emission is intentional — never applied to ship transform position.
    /// <para>
    /// Prefabs must load via <c>Resources.Load</c> in player builds — SampleScene often leaves the
    /// propulsion bank empty and falls back to <see cref="LoadDefaultSettings"/>. Editor-only
    /// AssetDatabase paths return null on Windows clients, which left ships with no flames.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(90)]
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
        Settings _settings;
        bool _initialized;

        readonly List<GameObject> _engineVfxInstances = new List<GameObject>();
        readonly List<GameObject> _thrusterVfxInstances = new List<GameObject>();
        readonly List<ParticleSystem> _engineParticleSystems = new List<ParticleSystem>();
        readonly List<ParticleSystem> _thrusterParticleSystems = new List<ParticleSystem>();

        bool _lastEngineMoving;
        bool _lastThrusterActive;
        float _thrusterVfxBlend;

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
        public void Bind(Entity shipEntity, string familyPrefix, Settings settings)
        {
            // --- Cache binding ---
            _shipEntity = shipEntity;
            if (!string.IsNullOrWhiteSpace(familyPrefix))
                _familyPrefix = familyPrefix.Trim();

            if (settings.thrusterJetFlameBank == null)
                settings.thrusterJetFlameBank = new List<ThrusterVfxColorPrefab>();

            _settings = settings;
            RebuildVfx();
        }

        void OnDestroy() => ClearVfxInstances();

        /// <summary>
        /// Instantiates engine/thruster flame prefabs at <see cref="ChassisComponentStats"/> mount sites.
        /// Sets <c>_initialized</c> only when at least one particle instance was created — otherwise
        /// LateUpdate exits early and the ship stays without thrust VFX.
        /// </summary>
        void RebuildVfx()
        {
            ClearVfxInstances();
            _lastEngineMoving = false;
            _lastThrusterActive = false;
            _thrusterVfxBlend = 0f;

            // --- Find Engine_* / Thruster_* mounts on the hybrid hull ---
            var stats = ChassisComponentStats.FromTransform(transform, _familyPrefix);

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
                    go.transform.localScale = Vector3.one;
                    // [HYBRID] URP material fixups so Sci-Fi Arsenal particles render in player builds.
                    VfxUrpCompat.PrepareVfxInstance(go);
                    _engineVfxInstances.Add(go);
                    CollectParticleSystems(go, _engineParticleSystems);
                }
            }

            if (_settings.HasAnyThrusterPrefab)
            {
                foreach (Transform t in stats.thrusterTransforms)
                {
                    if (t == null)
                        continue;

                    GameObject prefab = ResolveThrusterVfxPrefabForTransform(t);
                    if (prefab == null)
                        continue;

                    GameObject go = Instantiate(prefab, t);
                    go.transform.localPosition = _settings.thrusterVfxLocalOffset;
                    go.transform.localRotation = Quaternion.Euler(_settings.thrusterVfxLocalEuler);
                    go.transform.localScale = Vector3.one * Mathf.Clamp01(_settings.thrusterVfxIdleScale);
                    VfxUrpCompat.PrepareVfxInstance(go);
                    _thrusterVfxInstances.Add(go);
                    CollectParticleSystems(go, _thrusterParticleSystems);
                }
            }

            _initialized = _engineVfxInstances.Count > 0 || _thrusterVfxInstances.Count > 0;
        }

        /// <summary>
        /// Each frame: read ECS velocity + thrust input and drive particle emission / scale.
        /// Runs after ship proxy transforms have been synced for the frame.
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
                    return;
                }
            }

            // --- Derive motion state from ECS kinematics + input ---
            float speed = 0f;
            if (em.HasComponent<ShipKinematics>(_shipEntity))
            {
                float3 vel = em.GetComponentData<ShipKinematics>(_shipEntity).Velocity;
                vel.y = 0f;
                speed = math.length(vel);
            }

            // [NETCODE] ShipInput.Thrust — same flag the predicted motor uses for forward thrust.
            bool thrusting = em.HasComponent<ShipInput>(_shipEntity)
                && em.GetComponentData<ShipInput>(_shipEntity).Thrust;

            // Engines: on whenever the ship is moving above the idle threshold.
            // Thrusters: by default only while accelerating (moving + thrust held).
            bool moving = speed >= EngineSpeedThreshold;
            bool accelerating = moving && thrusting;
            bool showThrusters = _settings.useThrusterVfxForAcceleration ? accelerating : moving;
            float targetThrusterBlend = showThrusters ? 1f : 0f;
            float transitionSpeed = Mathf.Max(0.01f, _settings.thrusterVfxTransitionSpeed);
            // [TITAN-ORBIT] Cosmetic blend — acceptable on VFX only, not ship transform.
            _thrusterVfxBlend = Mathf.MoveTowards(_thrusterVfxBlend, targetThrusterBlend, transitionSpeed * Time.deltaTime);
            bool thrusterTransitionActive = Mathf.Abs(_thrusterVfxBlend - targetThrusterBlend) > 0.0001f;

            if (moving == _lastEngineMoving && showThrusters == _lastThrusterActive && !thrusterTransitionActive)
                return;

            _lastEngineMoving = moving;
            _lastThrusterActive = showThrusters;

            SetEngineVfxActive(moving, moving ? EngineEmissionRate : 0f);
            SetThrusterVfxBlend(_thrusterVfxBlend);
        }

        void SetEngineVfxActive(bool active, float emissionRate)
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
                if (active && !ps.isPlaying)
                    ps.Play();
            }
        }

        void SetThrusterVfxBlend(float blend)
        {
            float scaleLerp = Mathf.Lerp(Mathf.Clamp01(_settings.thrusterVfxIdleScale), 1f, blend);

            for (int i = 0; i < _thrusterVfxInstances.Count; i++)
            {
                GameObject go = _thrusterVfxInstances[i];
                if (go == null)
                    continue;

                go.transform.localScale = Vector3.one * scaleLerp;
                bool visible = scaleLerp > 0.0005f;
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
                if (blend > 0.001f && !ps.isPlaying)
                    ps.Play();
            }
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
