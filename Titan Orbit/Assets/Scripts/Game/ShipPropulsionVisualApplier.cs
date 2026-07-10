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
    /// </summary>
    [DefaultExecutionOrder(90)]
    public class ShipPropulsionVisualApplier : MonoBehaviour
    {
        const string DefaultJetFlamePath =
            "Assets/Archanor/Sci-Fi Arsenal/Sci-Fi Effects/Prefabs/Interactive/JetFlame/V2/ModularJetFlame2.prefab";

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

        /// <summary>Default VFX settings with jet-flame bank for color-matched thrusters.</summary>
        public static Settings LoadDefaultSettings()
        {
            GameObject defaultFlame = LoadDefaultJetFlamePrefab();
            var bank = new List<ThrusterVfxColorPrefab>();
            if (defaultFlame != null)
            {
                bank.Add(new ThrusterVfxColorPrefab { colorName = "Blue", prefab = defaultFlame });
                bank.Add(new ThrusterVfxColorPrefab { colorName = "Green", prefab = defaultFlame });
                bank.Add(new ThrusterVfxColorPrefab { colorName = "Purple", prefab = defaultFlame });
                bank.Add(new ThrusterVfxColorPrefab { colorName = "Red", prefab = defaultFlame });
                bank.Add(new ThrusterVfxColorPrefab { colorName = "Yellow", prefab = defaultFlame });
            }

            return new Settings
            {
                engineVfxPrefab = null,
                thrusterVfxPrefab = null,
                useThrusterVfxForAcceleration = true,
                thrusterJetFlameBank = bank,
                thrusterVfxLocalOffset = new Vector3(0f, 0f, -0.2f),
                thrusterVfxLocalEuler = new Vector3(0f, 180f, 0f),
                thrusterVfxIdleScale = 0.1f,
                thrusterVfxTransitionSpeed = 3f,
            };
        }

        static GameObject LoadDefaultJetFlamePrefab()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(DefaultJetFlamePath);
#else
            return null;
#endif
        }

        /// <summary>Links this applier to a ship entity and rebuilds particle instances from chassis transforms.</summary>
        public void Bind(Entity shipEntity, string familyPrefix, Settings settings)
        {
            _shipEntity = shipEntity;
            if (!string.IsNullOrWhiteSpace(familyPrefix))
                _familyPrefix = familyPrefix.Trim();

            if (settings.thrusterJetFlameBank == null)
                settings.thrusterJetFlameBank = new List<ThrusterVfxColorPrefab>();

            _settings = settings;
            RebuildVfx();
        }

        void OnDestroy() => ClearVfxInstances();

        /// <summary>Instantiates engine/thruster prefabs at ChassisComponentStats transform sites.</summary>
        void RebuildVfx()
        {
            ClearVfxInstances();
            _lastEngineMoving = false;
            _lastThrusterActive = false;
            _thrusterVfxBlend = 0f;

            var stats = ChassisComponentStats.FromTransform(transform, _familyPrefix);

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

        void LateUpdate()
        {
            if (!_initialized || _shipEntity == Entity.Null)
                return;

            // [TITAN-ORBIT] Read presentation/visualization world — not raw predicted sim.
            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            if (!em.Exists(_shipEntity))
                return;

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

            bool thrusting = em.HasComponent<ShipInput>(_shipEntity)
                && em.GetComponentData<ShipInput>(_shipEntity).Thrust;

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
