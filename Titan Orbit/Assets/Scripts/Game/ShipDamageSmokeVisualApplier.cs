using System.Collections.Generic;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-only cosmetic smoke that leaks from a damaged ship hull.
    /// Profile comes from <see cref="ShipDamageSmokeSettings"/> on
    /// <see cref="ShipFamilyDefinition.damageSmokeSettings"/> (shared asset today, per-family later).
    /// Reads ghosted <see cref="ShipState.Health"/> / <see cref="ShipState.MaxHealth"/> — no server
    /// sim change. Attached by <see cref="EcsWorldVisualizer"/> on hybrid ship proxies.
    /// <para>
    /// One prefab instance is spawned per <see cref="ChassisComponentStats.thrusterVfxTransforms"/>
    /// mount (same list as <see cref="ShipPropulsionVisualApplier"/> jets). Instances parent to the
    /// hull proxy root so Hierarchy scale stays readable, then snap to each mount every frame.
    /// Ships with no VFX thrusters fall back to a single hull-root emitter.
    /// </para>
    /// <para>
    /// Prefab loads via the settings asset (usually <c>Resources/ShipDamageSmoke</c>).
    /// Particles use world simulation space so smoke stays behind the moving hull.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(105)]
    public class ShipDamageSmokeVisualApplier : MonoBehaviour
    {
        Entity _shipEntity;
        ShipDamageSmokeSettings _config;
        string _familyPrefix = "AstroEagle";
        ShipFamilyDefinition _family;
        bool _initialized;

        readonly List<GameObject> _smokeInstances = new List<GameObject>();
        readonly List<Transform> _smokeAnchors = new List<Transform>();
        readonly List<ParticleSystem> _particleSystems = new List<ParticleSystem>();

        /// <summary>Smoothed 0–1 smoke intensity (cosmetic blend).</summary>
        float _intensity;

        /// <summary>Last applied intensity bucket — skip particle writes when nearly unchanged.</summary>
        float _lastAppliedIntensity = -1f;

        /// <summary>Last applied speed factor — refresh trail density when speed changes meaningfully.</summary>
        float _lastAppliedSpeedFactor = -1f;

        /// <summary>True while systems should be emitting.</summary>
        bool _wasEmitting;

        /// <summary>
        /// Links this applier to a ship ghost and rebuilds smoke from the family settings asset.
        /// Called by <see cref="EcsWorldVisualizer"/> after the hybrid hull proxy is Instantiated
        /// (and after <see cref="ShipPropulsionVisualApplier.Bind"/> so VFX mounts are classified).
        /// </summary>
        /// <param name="shipEntity">ECS ship ghost this proxy follows.</param>
        /// <param name="config">
        /// Family damage-smoke profile. Null or <see cref="ShipDamageSmokeSettings.enabled"/> false
        /// clears smoke (toggle off).
        /// </param>
        /// <param name="familyPrefix">Chassis family name for mount parsing (e.g. AstroEagle).</param>
        /// <param name="family">Optional family for baked <c>enablePropulsionVfx</c> flags.</param>
        public void Bind(
            Entity shipEntity,
            ShipDamageSmokeSettings config,
            string familyPrefix = null,
            ShipFamilyDefinition family = null)
        {
            _shipEntity = shipEntity;
            _config = config;
            _family = family;
            if (!string.IsNullOrWhiteSpace(familyPrefix))
                _familyPrefix = familyPrefix.Trim();

            if (_config != null)
                _config.EnsureRuntimeDefaults();

            // Master toggle / missing asset — no emitter on this proxy.
            if (_config == null || !_config.enabled || _config.smokePrefab == null)
            {
                ClearSmoke();
                return;
            }

            RebuildSmoke();
        }

        void OnDestroy() => ClearSmoke();

        /// <summary>
        /// Instantiates one smoke prefab per thruster VFX mount, parented to the hull proxy root.
        /// Engine/Thruster mounts inherit tiny chassis scales which made particles invisible, so
        /// instances stay on the hull and snap to mount world pose instead.
        /// </summary>
        void RebuildSmoke()
        {
            ClearSmoke();
            _intensity = 0f;
            _lastAppliedIntensity = -1f;
            _lastAppliedSpeedFactor = -1f;
            _wasEmitting = false;

            if (_config == null || !_config.enabled || _config.smokePrefab == null)
                return;

            // --- Same sites as live thruster jet instances ---
            // Prefer the propulsion applier's spawned flames (exact VFX pose, including local offset).
            // Fall back to ChassisComponentStats mounts when jets have not been Instantiated yet.
            var anchors = new List<Transform>(8);
            var propulsion = GetComponent<ShipPropulsionVisualApplier>();
            if (propulsion != null)
                propulsion.CopyThrusterVfxAnchors(anchors);

            if (anchors.Count == 0)
            {
                // [TITAN-ORBIT] thrusterVfxTransforms = enablePropulsionVfx only
                // (Thrusters_Big / Tiny_Thrusters yes; Thruster_Place / Cover no).
                var stats = ChassisComponentStats.FromTransform(transform, _familyPrefix, _family);
                for (int i = 0; i < stats.thrusterVfxTransforms.Count; i++)
                {
                    Transform mount = stats.thrusterVfxTransforms[i];
                    if (mount != null)
                        anchors.Add(mount);
                }
            }

            for (int i = 0; i < anchors.Count; i++)
            {
                Transform mount = anchors[i];
                if (mount == null)
                    continue;
                SpawnSmokeInstance(mount, i);
            }

            // --- Fallback: hull-root emitter when this chassis has no VFX thrusters ---
            if (_smokeInstances.Count == 0)
                SpawnSmokeInstance(anchor: null, index: 0);

            SyncSmokeToAnchors();
            ApplyWorldNormalizedScale(0.35f);
            ApplySmokeIntensity(0f, 0f, forceStop: true);
            _initialized = _particleSystems.Count > 0;
        }

        /// <summary>
        /// Instantiates one settings prefab on the hull root and optionally tracks a thruster mount.
        /// </summary>
        /// <param name="anchor">Thruster VFX mount to follow, or null for hull-root fallback.</param>
        /// <param name="index">Instance index for the GameObject name.</param>
        void SpawnSmokeInstance(Transform anchor, int index)
        {
            GameObject go = Instantiate(_config.smokePrefab, transform);
            go.name = anchor != null
                ? $"ShipDamageSmoke_{index}_{anchor.name}"
                : "ShipDamageSmoke";

            if (anchor == null)
            {
                go.transform.localPosition = _config.localOffset;
                go.transform.localRotation = Quaternion.Euler(_config.localEuler);
            }

            // [HYBRID] Soft alpha-blended grey smoke (not opaque squares).
            EnsureUrpSmokeMaterials(go);
            CollectAndConfigureParticleSystems(go);

            _smokeInstances.Add(go);
            _smokeAnchors.Add(anchor);
        }

        /// <summary>
        /// Copies each tracked thruster mount's world pose onto the matching hull-parented emitter.
        /// Attribute-scale / OVERDRIVE can move mounts after Bind — keep smoke glued every frame.
        /// </summary>
        void SyncSmokeToAnchors()
        {
            if (_config == null)
                return;

            Quaternion smokeLocal = Quaternion.Euler(_config.localEuler);
            for (int i = 0; i < _smokeInstances.Count; i++)
            {
                GameObject go = _smokeInstances[i];
                Transform anchor = i < _smokeAnchors.Count ? _smokeAnchors[i] : null;
                if (go == null || anchor == null)
                    continue;

                // Exact thruster VFX mount world position (same sites as jet flames).
                // localOffset stays for the no-thruster hull-root fallback only.
                go.transform.position = anchor.position;
                // Billow in hull space (same as the old single emitter), not the nozzle's jet yaw.
                go.transform.rotation = transform.rotation * smokeLocal;
            }
        }

        /// <summary>
        /// Sets local scale so the emitter's world scale matches a target, independent of ship size.
        /// </summary>
        /// <param name="worldScaleMul">0–1 multiplier on <see cref="ShipDamageSmokeSettings.maxWorldScale"/>.</param>
        void ApplyWorldNormalizedScale(float worldScaleMul)
        {
            if (_config == null || _smokeInstances.Count == 0)
                return;

            float targetWorld = Mathf.Max(0.05f, _config.maxWorldScale * Mathf.Clamp01(worldScaleMul));
            float parentLossy = transform.lossyScale.x;
            if (parentLossy < 0.0001f)
                parentLossy = 0.0001f;
            Vector3 local = Vector3.one * (targetWorld / parentLossy);
            for (int i = 0; i < _smokeInstances.Count; i++)
            {
                GameObject go = _smokeInstances[i];
                if (go != null)
                    go.transform.localScale = local;
            }
        }

        /// <summary>
        /// Builds a soft, alpha-blended smoke material so puffs are cloudy — not opaque grey squares.
        /// </summary>
        static void EnsureUrpSmokeMaterials(GameObject root)
        {
            if (root == null)
                return;

            Shader urp = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            Shader legacy = Shader.Find("Particles/Standard Unlit");

            var renderers = root.GetComponentsInChildren<ParticleSystemRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                ParticleSystemRenderer pr = renderers[i];
                if (pr == null)
                    continue;

                pr.maxParticleSize = Mathf.Min(Mathf.Max(pr.maxParticleSize, 0.22f), 0.28f);
                pr.minParticleSize = 0f;
                pr.renderMode = ParticleSystemRenderMode.Billboard;

                Material src = pr.sharedMaterial;
                Texture smokeTex = ResolveSmokeTexture(src);

                Shader shader = urp != null ? urp : (src != null ? src.shader : legacy);
                if (shader == null)
                    shader = legacy;
                if (shader == null)
                    continue;

                Material edit = new Material(shader);
                edit.name = "ShipDamageSmoke_SoftGrey";
                // Mid grey — readable on dark maps; alpha comes from the soft smoke texture.
                ApplySoftTransparentParticleMaterial(edit, smokeTex, new Color(0.78f, 0.78f, 0.8f, 0.85f));
                pr.sharedMaterial = edit;
            }
        }

        /// <summary>Pulls the soft smoke albedo (with alpha edges) from the prefab material.</summary>
        static Texture ResolveSmokeTexture(Material src)
        {
            if (src == null)
                return null;
            if (src.HasProperty("_BaseMap") && src.GetTexture("_BaseMap") != null)
                return src.GetTexture("_BaseMap");
            if (src.HasProperty("_MainTex") && src.GetTexture("_MainTex") != null)
                return src.GetTexture("_MainTex");
            return src.mainTexture;
        }

        /// <summary>
        /// Configures a particle material for soft alpha-blended smoke (texture alpha + no ZWrite).
        /// </summary>
        static void ApplySoftTransparentParticleMaterial(Material mat, Texture smokeTex, Color tint)
        {
            if (mat == null)
                return;

            if (smokeTex != null)
            {
                if (mat.HasProperty("_BaseMap"))
                    mat.SetTexture("_BaseMap", smokeTex);
                if (mat.HasProperty("_MainTex"))
                    mat.SetTexture("_MainTex", smokeTex);
                mat.mainTexture = smokeTex;
            }

            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", tint);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", tint);
            if (mat.HasProperty("_TintColor"))
                mat.SetColor("_TintColor", tint);
            mat.color = tint;

            if (mat.HasProperty("_Surface"))
                mat.SetFloat("_Surface", 1f);
            if (mat.HasProperty("_Blend"))
                mat.SetFloat("_Blend", 0f);
            if (mat.HasProperty("_AlphaClip"))
                mat.SetFloat("_AlphaClip", 0f);
            if (mat.HasProperty("_Cutoff"))
                mat.SetFloat("_Cutoff", 0f);
            if (mat.HasProperty("_ZWrite"))
                mat.SetFloat("_ZWrite", 0f);
            if (mat.HasProperty("_Cull"))
                mat.SetFloat("_Cull", 0f);
            if (mat.HasProperty("_Mode"))
                mat.SetFloat("_Mode", 2f);
            if (mat.HasProperty("_ColorMode"))
                mat.SetFloat("_ColorMode", 0f);
            if (mat.HasProperty("_SoftParticlesEnabled"))
                mat.SetFloat("_SoftParticlesEnabled", 0f);

            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_SrcBlendAlpha"))
                mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            if (mat.HasProperty("_DstBlendAlpha"))
                mat.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);

            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.DisableKeyword("_ALPHAMODULATE_ON");
            mat.DisableKeyword("_COLOROVERLAY_ON");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_FADING_ON");

            mat.SetOverrideTag("RenderType", "Transparent");
            mat.renderQueue = (int)RenderQueue.Transparent;
            mat.SetShaderPassEnabled("DepthOnly", false);
            mat.SetShaderPassEnabled("SHADOWCASTER", false);
        }

        /// <summary>
        /// Each frame: map hull HP + speed → smoke intensity from the settings asset.
        /// </summary>
        void LateUpdate()
        {
            if (!_initialized || _shipEntity == Entity.Null || _config == null)
                return;

            // Keep emitters on moving / attribute-scaled thruster mounts.
            SyncSmokeToAnchors();

            // Live toggle off — stop without destroying (Bind will clear on next proxy rebuild).
            if (!_config.enabled)
            {
                if (_wasEmitting || _intensity > 0.001f)
                {
                    _intensity = 0f;
                    ApplySmokeIntensity(0f, 0f, forceStop: true);
                    _wasEmitting = false;
                    _lastAppliedIntensity = 0f;
                    _lastAppliedSpeedFactor = 0f;
                }
                return;
            }

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            if (!em.Exists(_shipEntity) || !em.HasComponent<ShipState>(_shipEntity))
                return;

            var ship = em.GetComponentData<ShipState>(_shipEntity);

            if (ship.IsDead)
            {
                if (_wasEmitting || _intensity > 0.001f)
                {
                    _intensity = 0f;
                    ApplySmokeIntensity(0f, 0f, forceStop: true);
                    _wasEmitting = false;
                    _lastAppliedIntensity = 0f;
                    _lastAppliedSpeedFactor = 0f;
                }
                return;
            }

            // --- Health window from the ScriptableObject (e.g. start at 50% HP) ---
            float maxHp = math.max(1f, ship.MaxHealth);
            float healthFraction = math.saturate(ship.Health / maxHp);
            float targetIntensity = _config.EvaluateIntensity(healthFraction);

            float speed = 0f;
            if (em.HasComponent<ShipKinematics>(_shipEntity))
            {
                float3 vel = em.GetComponentData<ShipKinematics>(_shipEntity).Velocity;
                vel.y = 0f;
                speed = math.length(vel);
            }

            float speedFactor = math.saturate(speed / math.max(0.01f, _config.trailSpeedReference));

            float transition = math.max(0.01f, _config.intensityTransitionSpeed);
            _intensity = Mathf.MoveTowards(_intensity, targetIntensity, transition * Time.deltaTime);

            bool shouldEmit = _intensity > 0.01f;
            float intensityDelta = math.abs(_intensity - _lastAppliedIntensity);
            float speedDelta = math.abs(speedFactor - _lastAppliedSpeedFactor);
            bool needsRefresh =
                shouldEmit != _wasEmitting ||
                intensityDelta >= 0.02f ||
                (shouldEmit && speedDelta >= 0.05f);
            if (!needsRefresh)
                return;

            ApplySmokeIntensity(_intensity, speedFactor);
            _lastAppliedIntensity = _intensity;
            _lastAppliedSpeedFactor = speedFactor;
            _wasEmitting = shouldEmit;
        }

        /// <summary>
        /// Writes emission rate, lifetime, size, and rate-over-distance from intensity + speed.
        /// </summary>
        void ApplySmokeIntensity(float intensity, float speedFactor, bool forceStop = false)
        {
            if (_config == null)
                return;

            intensity = math.saturate(intensity);
            speedFactor = math.saturate(speedFactor);
            bool emit = !forceStop && intensity > 0.01f;

            float scaleMul = Mathf.Lerp(0.55f, 1f, intensity);
            ApplyWorldNormalizedScale(scaleMul);
            for (int i = 0; i < _smokeInstances.Count; i++)
            {
                GameObject go = _smokeInstances[i];
                if (go != null && !go.activeSelf)
                    go.SetActive(true);
            }

            float life = Mathf.Lerp(_config.minLifetime, _config.maxLifetime, intensity);
            life *= Mathf.Lerp(1f, 1.12f, speedFactor);

            float sizeT = intensity * intensity * (3f - 2f * intensity);
            float startSize = Mathf.Lerp(_config.minStartSize, _config.maxStartSize, sizeT);
            float rateOverTime = Mathf.Lerp(_config.minEmissionRate, _config.maxEmissionRate, sizeT);
            float rateOverDistance = _config.maxRateOverDistance * sizeT * speedFactor;

            for (int i = 0; i < _particleSystems.Count; i++)
            {
                ParticleSystem ps = _particleSystems[i];
                if (ps == null)
                    continue;

                var main = ps.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.8f, life);
                main.startSize = new ParticleSystem.MinMaxCurve(startSize * 0.65f, startSize);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.15f, 0.55f);
                main.startColor = new ParticleSystem.MinMaxGradient(Color.white);

                var emission = ps.emission;
                emission.enabled = emit;
                emission.rateOverTime = emit ? rateOverTime : 0f;
                emission.rateOverDistance = emit ? rateOverDistance : 0f;

                if (forceStop || !emit)
                {
                    if (ps.isPlaying)
                        ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
                else if (!ps.isPlaying)
                {
                    ps.Play();
                }
            }
        }

        /// <summary>Caches particle systems on <paramref name="root"/> and forces world simulation space.</summary>
        void CollectAndConfigureParticleSystems(GameObject root)
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
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                main.playOnAwake = false;
                main.loop = true;
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.15f, 0.55f);
                main.startColor = new ParticleSystem.MinMaxGradient(Color.white);

                var colorOverLifetime = ps.colorOverLifetime;
                colorOverLifetime.enabled = true;
                var fade = new Gradient();
                fade.SetKeys(
                    new[]
                    {
                        new GradientColorKey(Color.white, 0f),
                        new GradientColorKey(new Color(0.92f, 0.92f, 0.94f), 0.45f),
                        new GradientColorKey(new Color(0.85f, 0.85f, 0.88f), 1f),
                    },
                    new[]
                    {
                        new GradientAlphaKey(0f, 0f),
                        new GradientAlphaKey(0.7f, 0.12f),
                        new GradientAlphaKey(0.45f, 0.5f),
                        new GradientAlphaKey(0f, 1f),
                    });
                colorOverLifetime.color = fade;

                var sizeOverLifetime = ps.sizeOverLifetime;
                sizeOverLifetime.enabled = true;
                sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                    new Keyframe(0f, 0.35f),
                    new Keyframe(0.25f, 1f),
                    new Keyframe(1f, 0.15f)));

                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                _particleSystems.Add(ps);
            }
        }

        /// <summary>Destroys every smoke instance and clears cached particle systems.</summary>
        void ClearSmoke()
        {
            for (int i = 0; i < _smokeInstances.Count; i++)
            {
                if (_smokeInstances[i] != null)
                    Destroy(_smokeInstances[i]);
            }

            _smokeInstances.Clear();
            _smokeAnchors.Clear();
            _particleSystems.Clear();
            _initialized = false;
        }
    }
}
