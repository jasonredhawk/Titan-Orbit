using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.Rendering;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-only cosmetic smoke that streams out of damaged engine and thruster nozzles.
    /// Hull damage keeps a grey plume. OVERDRIVE adds a second plume on the same nozzles,
    /// tinted with the ship team color, so both can run together.
    /// Profile comes from <see cref="ShipDamageSmokeSettings"/> on
    /// <see cref="ShipFamilyDefinition.damageSmokeSettings"/> (shared asset today, per-family later).
    /// Damage reads ghosted hull HP. Overdrive on this machine uses pending Shift/thrust;
    /// other ships use ghosted <see cref="ShipState.OverdriveBurstActive"/>.
    /// The team plume is created on the first burst and stays stopped afterward.
    /// Attached by <see cref="EcsWorldVisualizer"/> on hybrid ship proxies.
    /// <para>
    /// One prefab instance is spawned per propulsion nozzle (thruster VFX mounts, plus engine
    /// mounts when those are the jets). Instances stay parented to the hull proxy so mount scale
    /// does not resize the puffs, and snap to the aft face of each nozzle every frame.
    /// On death, <see cref="HandoffEmittersToDebris"/> parents those same systems to the wreck
    /// chunks so they keep pouring at full intensity while the debris spirals.
    /// Ships with no engine or thruster fall back to a single hull-root emitter.
    /// </para>
    /// <para>
    /// Prefab loads via the settings asset (usually <c>Resources/ShipDamageSmoke</c>).
    /// Particles use world simulation space so smoke stays behind the moving hull.
    /// Instances are excluded from attribute-scale groups — triangle / OVERDRIVE bloom
    /// used to rewrite emitter <c>localScale</c> and blink the trail.
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
        readonly List<Vector3> _nozzleLocalRear = new List<Vector3>();
        readonly List<bool> _nozzleHasRear = new List<bool>();
        readonly List<ParticleSystem> _particleSystems = new List<ParticleSystem>();
        readonly List<ShipPropulsionVisualApplier.PropulsionNozzle> _nozzleScratch =
            new List<ShipPropulsionVisualApplier.PropulsionNozzle>(8);

        /// <summary>True after emitters were parented onto death-debris chunks.</summary>
        bool _handedToDebris;

        /// <summary>True once death has written full-intensity emission (do not rewrite every frame).</summary>
        bool _deathEmissionLocked;

        /// <summary>Smoothed 0–1 smoke intensity (cosmetic blend).</summary>
        float _intensity;

        /// <summary>Last applied intensity bucket — skip particle writes when nearly unchanged.</summary>
        float _lastAppliedIntensity = -1f;

        /// <summary>Last applied speed factor — refresh trail density when speed changes meaningfully.</summary>
        float _lastAppliedSpeedFactor = -1f;

        /// <summary>True while systems should be emitting.</summary>
        bool _wasEmitting;

        /// <summary>Soft grey used for hull-damage smoke. The overdrive plume does not use this.</summary>
        static readonly Color DamageSmokeGrey = new Color(0.78f, 0.78f, 0.8f, 0.85f);

        /// <summary>Team-colored nozzle smoke, parallel to <see cref="_smokeInstances"/>.</summary>
        readonly List<GameObject> _overdriveInstances = new List<GameObject>();

        /// <summary>Particle systems on <see cref="_overdriveInstances"/> only.</summary>
        readonly List<ParticleSystem> _overdriveParticles = new List<ParticleSystem>();

        /// <summary>Materials owned by the overdrive plume, so team tint never touches damage smoke.</summary>
        readonly List<Material> _overdriveMaterials = new List<Material>();

        /// <summary>True while the overdrive plume should be emitting.</summary>
        bool _overdriveWasEmitting;

        /// <summary>Last speed factor written to the overdrive rate-over-distance.</summary>
        float _overdriveSpeedFactor = -1f;

        /// <summary>Team last written onto the overdrive materials.</summary>
        TeamId _overdriveTintTeam;

        /// <summary>True after a team tint has been applied at least once.</summary>
        bool _overdriveTintSet;

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
        /// Instantiates one smoke prefab per engine/thruster nozzle, parented to the hull proxy root.
        /// Engine/Thruster mounts inherit tiny chassis scales which made particles invisible, so
        /// instances stay on the hull and snap to the nozzle's aft face instead.
        /// </summary>
        void RebuildSmoke()
        {
            ClearSmoke();
            _intensity = 0f;
            _lastAppliedIntensity = -1f;
            _lastAppliedSpeedFactor = -1f;
            _wasEmitting = false;
            _handedToDebris = false;
            _deathEmissionLocked = false;
            _overdriveWasEmitting = false;
            _overdriveSpeedFactor = -1f;
            _overdriveTintSet = false;

            if (_config == null || !_config.enabled || _config.smokePrefab == null)
                return;

            // --- Same nozzles as live jets (aft face of each engine / thruster mesh) ---
            _nozzleScratch.Clear();
            var propulsion = GetComponent<ShipPropulsionVisualApplier>();
            if (propulsion != null)
                propulsion.CopyPropulsionNozzles(_nozzleScratch);

            if (_nozzleScratch.Count == 0)
                CollectChassisNozzleFallback();

            for (int i = 0; i < _nozzleScratch.Count; i++)
            {
                ShipPropulsionVisualApplier.PropulsionNozzle nozzle = _nozzleScratch[i];
                if (nozzle.Mount == null)
                    continue;
                SpawnSmokeInstance(nozzle.Mount, nozzle.LocalRear, nozzle.HasLocalRear, i);
            }

            // --- Fallback: hull-root emitter when this chassis has no engine or thruster ---
            if (_smokeInstances.Count == 0)
                SpawnSmokeInstance(anchor: null, localRear: Vector3.zero, hasRear: false, index: 0);

            SyncSmokeToAnchors();
            ApplyWorldNormalizedScale(0.35f);
            ApplySmokeIntensity(0f, 0f, forceStop: true);
            _initialized = _particleSystems.Count > 0;
        }

        /// <summary>
        /// Thruster VFX mounts, or engine meshes when the hull has no thruster nozzles.
        /// Used only when jets have not been spawned yet.
        /// </summary>
        void CollectChassisNozzleFallback()
        {
            var stats = ChassisComponentStats.FromTransform(transform, _familyPrefix, _family);
            AppendMounts(stats.thrusterVfxTransforms);
            if (_nozzleScratch.Count > 0)
                return;

            AppendMounts(stats.engineTransforms);
        }

        void AppendMounts(List<Transform> mounts)
        {
            if (mounts == null)
                return;

            for (int i = 0; i < mounts.Count; i++)
            {
                Transform mount = mounts[i];
                if (mount == null)
                    continue;

                _nozzleScratch.Add(new ShipPropulsionVisualApplier.PropulsionNozzle
                {
                    Mount = mount,
                    LocalRear = Vector3.zero,
                    HasLocalRear = false,
                });
            }
        }

        /// <summary>
        /// Instantiates one settings prefab on the hull root and tracks an engine or thruster nozzle.
        /// </summary>
        /// <param name="anchor">Nozzle mount to follow, or null for hull-root fallback.</param>
        /// <param name="localRear">Aft-face point in <paramref name="anchor"/> local space.</param>
        /// <param name="hasRear">When false, follow the mount pivot.</param>
        /// <param name="index">Instance index for the GameObject name.</param>
        void SpawnSmokeInstance(Transform anchor, Vector3 localRear, bool hasRear, int index)
        {
            GameObject go = Instantiate(_config.smokePrefab, transform);
            // Keep "Thruster" out of the name — ChassisComponentStats substring
            // classification would otherwise treat these as scale mounts.
            go.name = anchor != null
                ? $"ShipDamageSmoke_{index}"
                : "ShipDamageSmoke";

            if (anchor == null)
            {
                go.transform.localPosition = _config.localOffset;
                go.transform.localRotation = Quaternion.Euler(_config.localEuler);
            }

            // [HYBRID] Soft alpha-blended grey smoke (not opaque squares).
            // Team overdrive smoke is a second prefab instance — this material stays grey.
            EnsureUrpSmokeMaterials(go, DamageSmokeGrey, "ShipDamageSmoke_SoftGrey", null);
            CollectAndConfigureParticleSystems(go, _particleSystems);

            _smokeInstances.Add(go);
            _smokeAnchors.Add(anchor);
            _nozzleLocalRear.Add(localRear);
            _nozzleHasRear.Add(hasRear);
        }

        /// <summary>
        /// Puts each hull-parented emitter on the aft face of its engine or thruster, aimed aft
        /// so the plume leaves the nozzle. Attribute-scale / OVERDRIVE can move mounts after Bind.
        /// </summary>
        void SyncSmokeToAnchors()
        {
            if (_config == null)
                return;

            Vector3 aft = -transform.forward;
            aft.y = 0f;
            if (aft.sqrMagnitude < 0.0001f)
                aft = -transform.forward;
            else
                aft.Normalize();
            Quaternion aim = Quaternion.LookRotation(aft, transform.up);
            SyncEmitterList(_smokeInstances, aim);
            if (_overdriveInstances.Count > 0)
                SyncEmitterList(_overdriveInstances, aim);
        }

        /// <summary>
        /// Snaps one emitter list onto the shared nozzle aft faces.
        /// </summary>
        void SyncEmitterList(List<GameObject> instances, Quaternion aim)
        {
            for (int i = 0; i < instances.Count; i++)
            {
                GameObject go = instances[i];
                Transform anchor = i < _smokeAnchors.Count ? _smokeAnchors[i] : null;
                if (go == null || anchor == null)
                    continue;

                bool hasRear = i < _nozzleHasRear.Count && _nozzleHasRear[i];
                Vector3 localRear = i < _nozzleLocalRear.Count ? _nozzleLocalRear[i] : Vector3.zero;
                Vector3 pos = hasRear ? anchor.TransformPoint(localRear) : anchor.position;
                if ((go.transform.position - pos).sqrMagnitude > 1e-8f)
                    go.transform.position = pos;
                if (Quaternion.Angle(go.transform.rotation, aim) > 0.1f)
                    go.transform.rotation = aim;
            }
        }

        /// <summary>
        /// Sets local scale so the emitter's world scale matches a target, independent of ship size.
        /// </summary>
        /// <param name="worldScaleMul">0–1 multiplier on <see cref="ShipDamageSmokeSettings.maxWorldScale"/>.</param>
        void ApplyWorldNormalizedScale(float worldScaleMul)
        {
            ApplyWorldNormalizedScale(_smokeInstances, worldScaleMul);
        }

        /// <summary>
        /// Sets local scale so <paramref name="instances"/> match a target world size.
        /// </summary>
        void ApplyWorldNormalizedScale(List<GameObject> instances, float worldScaleMul)
        {
            if (_config == null || instances == null || instances.Count == 0)
                return;

            float targetWorld = Mathf.Max(0.05f, _config.maxWorldScale * Mathf.Clamp01(worldScaleMul));
            float parentLossy = transform.lossyScale.x;
            if (parentLossy < 0.0001f)
                parentLossy = 0.0001f;
            Vector3 local = Vector3.one * (targetWorld / parentLossy);
            for (int i = 0; i < instances.Count; i++)
            {
                GameObject go = instances[i];
                if (go == null)
                    continue;
                // Assigning the same localScale still dirties the transform and can
                // restart ParticleSystems (triangle / OVERDRIVE used to blink this way).
                Vector3 current = go.transform.localScale;
                if ((current - local).sqrMagnitude < 1e-8f)
                    continue;
                go.transform.localScale = local;
            }
        }

        /// <summary>
        /// Builds a soft, alpha-blended smoke material so puffs are cloudy — not opaque squares.
        /// </summary>
        /// <param name="tint">Albedo multiplied with the particle vertex color (white).</param>
        /// <param name="materialName">Instance material name.</param>
        /// <param name="created">
        /// When set, receives each new material so a later tint can edit it
        /// without touching the other plume.
        /// </param>
        static void EnsureUrpSmokeMaterials(GameObject root, Color tint, string materialName, List<Material> created)
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
                edit.name = string.IsNullOrEmpty(materialName) ? "ShipDamageSmoke_Soft" : materialName;
                ApplySoftTransparentParticleMaterial(edit, smokeTex, tint);
                pr.sharedMaterial = edit;
                if (created != null)
                    created.Add(edit);
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
        /// Rewrites smoke localScale from the live hull lossy scale. Intensity-only updates
        /// skip this, so a scale captured while the hull was shrunk on a pad would stay huge
        /// after the ship leaves the turret.
        /// </summary>
        public void ReapplyThrusterAnchorScale()
        {
            if (!_initialized || _config == null)
                return;

            float scaleMul = Mathf.Lerp(0.55f, 1f, _intensity);
            ApplyWorldNormalizedScale(scaleMul);
            if (_overdriveInstances.Count > 0)
                ApplyWorldNormalizedScale(_overdriveInstances, 1f);
        }

        /// <summary>
        /// Each frame: map hull HP + speed → smoke intensity from the settings asset.
        /// </summary>
        void LateUpdate()
        {
            if (_handedToDebris)
            {
                if (TryGetShip(out ShipState alive) && !alive.IsDead)
                {
                    _handedToDebris = false;
                    _deathEmissionLocked = false;
                    RebuildSmoke();
                }

                return;
            }

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

                StopOverdriveEmission();
                return;
            }

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            if (!em.Exists(_shipEntity) || !em.HasComponent<ShipState>(_shipEntity))
                return;

            var ship = em.GetComponentData<ShipState>(_shipEntity);

            // Death used to StopEmitting here, so the wreck popped out with no trail.
            // Hold the heaviest plume; ShipDeathDebrisDriver reparents these systems
            // onto the chunks before the live hull is hidden.
            if (ship.IsDead)
            {
                // Wreck keeps the grey damage plume. The team plume stops with the burst.
                StopOverdriveEmission();
                HoldMaximumEmission();
                return;
            }

            _deathEmissionLocked = false;

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

            // Empty hull is full smoke immediately. The ease is only for the climb
            // from the start threshold down toward smokeFullAtHealthFraction.
            if (healthFraction <= _config.smokeFullAtHealthFraction || targetIntensity >= 0.999f)
                _intensity = 1f;
            else
            {
                float transition = math.max(0.01f, _config.intensityTransitionSpeed);
                _intensity = Mathf.MoveTowards(_intensity, targetIntensity, transition * Time.deltaTime);
            }

            bool shouldEmit = _intensity > 0.01f;
            float intensityDelta = math.abs(_intensity - _lastAppliedIntensity);
            float speedDelta = math.abs(speedFactor - _lastAppliedSpeedFactor);
            bool emitToggled = shouldEmit != _wasEmitting;
            bool intensityChanged = intensityDelta >= 0.02f;
            bool speedChanged = shouldEmit && speedDelta >= 0.05f;

            // Speed jumps (territory triangle / OVERDRIVE) must not rewrite startSize /
            // startLifetime / localScale — those ParticleSystem writes restart in-flight puffs.
            if (emitToggled || intensityChanged)
            {
                ApplySmokeIntensity(_intensity, speedFactor);
                _lastAppliedIntensity = _intensity;
                _lastAppliedSpeedFactor = speedFactor;
                _wasEmitting = shouldEmit;
            }
            else if (speedChanged)
            {
                ApplyRateOverDistanceOnly(_intensity, speedFactor);
                _lastAppliedSpeedFactor = speedFactor;
            }

            if (shouldEmit && !(emitToggled || intensityChanged))
                ResumeStoppedEmitters();

            UpdateOverdriveSmoke(em, ship, speed);
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

        /// <summary>
        /// Speed-only trail density. Does not touch startSize / lifetime / localScale.
        /// </summary>
        void ApplyRateOverDistanceOnly(float intensity, float speedFactor)
        {
            if (_config == null)
                return;

            intensity = math.saturate(intensity);
            speedFactor = math.saturate(speedFactor);
            float sizeT = intensity * intensity * (3f - 2f * intensity);
            float rateOverDistance = _config.maxRateOverDistance * sizeT * speedFactor;

            for (int i = 0; i < _particleSystems.Count; i++)
            {
                ParticleSystem ps = _particleSystems[i];
                if (ps == null)
                    continue;

                var emission = ps.emission;
                emission.rateOverDistance = rateOverDistance;
            }
        }

        /// <summary>
        /// Parent-scale writes can stop a ParticleSystem without clearing emit intent.
        /// Play() without Stop+Clear keeps in-flight puffs (same self-heal as thruster jets).
        /// </summary>
        void ResumeStoppedEmitters()
        {
            for (int i = 0; i < _particleSystems.Count; i++)
            {
                ParticleSystem ps = _particleSystems[i];
                if (ps != null && !ps.isPlaying)
                    ps.Play();
            }
        }

        /// <summary>Caches particle systems on <paramref name="root"/> and forces world simulation space.</summary>
        /// <param name="dest">Damage or overdrive list. The two plumes must not share a list.</param>
        void CollectAndConfigureParticleSystems(GameObject root, List<ParticleSystem> dest)
        {
            if (root == null || dest == null)
                return;

            ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null)
                    continue;

                var main = ps.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                // Hierarchy: localScale is (targetWorld / hullLossy) so world size stays
                // ~maxWorldScale. Local mode used that 5× compensation as the particle
                // size and made puffs huge.
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                main.emitterVelocityMode = ParticleSystemEmitterVelocityMode.Transform;
                main.playOnAwake = false;
                main.loop = true;
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.15f, 0.55f);
                main.startColor = new ParticleSystem.MinMaxGradient(Color.white);

                var inheritVelocity = ps.inheritVelocity;
                inheritVelocity.enabled = false;

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
                dest.Add(ps);
            }
        }

        /// <summary>
        /// Parents live nozzle emitters onto the wreck chunks that own their engine or thruster
        /// so the plume keeps looping as those chunks spiral. Clears ownership here — destroying
        /// the proxy must not destroy the wreck smoke. Idempotent.
        /// </summary>
        /// <param name="parts">Debris source transforms, parallel to <paramref name="chunkRoots"/>.</param>
        /// <param name="chunkRoots">Chunk transform each part was parented under.</param>
        public void HandoffEmittersToDebris(List<Transform> parts, List<Transform> chunkRoots)
        {
            if (_handedToDebris || _smokeInstances.Count == 0)
                return;
            if (parts == null || chunkRoots == null || parts.Count == 0 || chunkRoots.Count == 0)
                return;

            HoldMaximumEmission();

            for (int i = 0; i < _smokeInstances.Count; i++)
            {
                GameObject go = _smokeInstances[i];
                if (go == null)
                    continue;

                Transform mount = i < _smokeAnchors.Count ? _smokeAnchors[i] : null;
                Transform chunk = FindChunkForMount(mount != null ? mount : go.transform, parts, chunkRoots);
                if (chunk == null)
                    continue;

                go.transform.SetParent(chunk, true);
                ResumeEmittersOn(go);
            }

            _smokeInstances.Clear();
            _smokeAnchors.Clear();
            _nozzleLocalRear.Clear();
            _nozzleHasRear.Clear();
            _particleSystems.Clear();
            _initialized = false;
            _handedToDebris = true;
        }

        static Transform FindChunkForMount(Transform mount, List<Transform> parts, List<Transform> chunkRoots)
        {
            Transform walk = mount;
            while (walk != null)
            {
                int count = Mathf.Min(parts.Count, chunkRoots.Count);
                for (int i = 0; i < count; i++)
                {
                    if (parts[i] == walk && chunkRoots[i] != null)
                        return chunkRoots[i];
                }

                walk = walk.parent;
            }

            for (int i = 0; i < chunkRoots.Count; i++)
            {
                if (chunkRoots[i] != null)
                    return chunkRoots[i];
            }

            return null;
        }

        /// <summary>
        /// Writes full emission once. Repeat calls skip module writes so in-flight puffs are not restarted.
        /// </summary>
        void HoldMaximumEmission()
        {
            if (_deathEmissionLocked || _config == null)
                return;

            _intensity = 1f;
            ApplySmokeIntensity(1f, 1f, forceStop: false);
            _lastAppliedIntensity = 1f;
            _lastAppliedSpeedFactor = 1f;
            _wasEmitting = true;
            _deathEmissionLocked = true;
        }

        static void ResumeEmittersOn(GameObject root)
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
                main.loop = true;
                var emission = ps.emission;
                emission.enabled = true;
                if (!ps.isPlaying)
                    ps.Play();
            }
        }

        bool TryGetShip(out ShipState ship)
        {
            ship = default;
            if (_shipEntity == Entity.Null)
                return false;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return false;

            var em = world.EntityManager;
            if (!em.Exists(_shipEntity) || !em.HasComponent<ShipState>(_shipEntity))
                return false;

            ship = em.GetComponentData<ShipState>(_shipEntity);
            return true;
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
            _nozzleLocalRear.Clear();
            _nozzleHasRear.Clear();
            _particleSystems.Clear();
            for (int i = 0; i < _overdriveInstances.Count; i++)
            {
                if (_overdriveInstances[i] != null)
                    Destroy(_overdriveInstances[i]);
            }

            _overdriveInstances.Clear();
            _overdriveParticles.Clear();
            for (int i = 0; i < _overdriveMaterials.Count; i++)
            {
                if (_overdriveMaterials[i] != null)
                    Destroy(_overdriveMaterials[i]);
            }

            _overdriveMaterials.Clear();
            _overdriveWasEmitting = false;
            _overdriveSpeedFactor = -1f;
            _overdriveTintSet = false;
            _initialized = false;
        }

        /// <summary>
        /// True while this hull's OVERDRIVE burst should pour nozzle smoke.
        /// Local owner uses pending Shift + thrust (same rule as engine bloom) so the plume
        /// matches the button. Remotes use ghosted <see cref="ShipState.OverdriveBurstActive"/>
        /// because their <see cref="ShipInput"/> is not in the client snapshot.
        /// MEGA hulls never burst — Shift is heading lock.
        /// </summary>
        bool ResolveOverdriveBurst(EntityManager em, in ShipState ship)
        {
            if (ship.IsDead)
                return false;

            bool isMega = em.HasComponent<MegaShipState>(_shipEntity)
                && em.GetComponentData<MegaShipState>(_shipEntity).IsMega;
            if (isMega)
                return false;

            if (!IsLocalOwnerProxy(em))
                return ship.OverdriveBurstActive;

            bool thrustHeld;
            bool shiftHeld;
            if (ShipPendingInput.HasValue)
            {
                thrustHeld = ShipPendingInput.Latest.Thrust;
                shiftHeld = ShipPendingInput.Latest.Overdrive;
            }
            else if (em.HasComponent<ShipInput>(_shipEntity))
            {
                var input = em.GetComponentData<ShipInput>(_shipEntity);
                thrustHeld = input.Thrust;
                shiftHeld = input.Overdrive;
            }
            else
            {
                return ship.OverdriveBurstActive;
            }

            // Presentation does not run the orbit motor. Burst already requires thrust,
            // which is what takes the ship out of passive orbit.
            return ShipOverdriveTuning.IsBurstActive(
                shiftHeld,
                thrustHeld,
                useOrbit: false,
                ship.CurrentEnergy,
                ship.OverdriveLockout);
        }

        /// <summary>
        /// True only for this machine's predicted hull. A stale <see cref="LocalPlayerShipTag"/>
        /// on a remote must not read local Shift.
        /// </summary>
        bool IsLocalOwnerProxy(EntityManager em)
        {
            if (em.HasComponent<GhostOwnerIsLocal>(_shipEntity) &&
                em.IsComponentEnabled<GhostOwnerIsLocal>(_shipEntity))
                return true;

            if (!em.HasComponent<LocalPlayerShipTag>(_shipEntity))
                return false;

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
        /// Team hue lifted slightly toward white so the puff still reads as smoke.
        /// Alpha matches <see cref="DamageSmokeGrey"/>.
        /// </summary>
        static Color TeamSmokeTint(TeamId team)
        {
            Color c = Color.Lerp(team.ToColor(), Color.white, 0.18f);
            c.a = DamageSmokeGrey.a;
            return c;
        }

        /// <summary>
        /// Starts or stops the team-colored plume. Damage emitters are not written here.
        /// The prefab copies are created on the first burst, then left stopped.
        /// </summary>
        void UpdateOverdriveSmoke(EntityManager em, in ShipState ship, float speed)
        {
            bool burst = ResolveOverdriveBurst(em, ship);
            if (!burst && _overdriveInstances.Count == 0)
                return;

            if (burst)
                EnsureOverdriveEmitters();
            if (_overdriveInstances.Count == 0)
                return;

            if (!_overdriveTintSet || ship.Team != _overdriveTintTeam)
            {
                _overdriveTintTeam = ship.Team;
                _overdriveTintSet = true;
                TintOverdriveMaterials(TeamSmokeTint(ship.Team));
            }

            // Spawned this frame, after the earlier anchor sync.
            SyncSmokeToAnchors();

            float speedFactor = math.saturate(speed / math.max(0.01f, _config.trailSpeedReference));
            bool emitToggled = burst != _overdriveWasEmitting;
            bool speedChanged = burst && math.abs(speedFactor - _overdriveSpeedFactor) >= 0.05f;
            if (emitToggled)
            {
                ApplyOverdriveEmission(burst, speedFactor);
                _overdriveWasEmitting = burst;
                _overdriveSpeedFactor = speedFactor;
                return;
            }

            if (speedChanged)
            {
                ApplyOverdriveRateOverDistance(speedFactor);
                _overdriveSpeedFactor = speedFactor;
            }

            if (burst)
                ResumeOverdriveEmitters();
        }

        /// <summary>
        /// One team-colored prefab per damage nozzle. Names avoid "Thruster" so attribute
        /// scale does not treat them as mounts.
        /// </summary>
        void EnsureOverdriveEmitters()
        {
            if (_overdriveInstances.Count > 0 || _config == null || _config.smokePrefab == null)
                return;
            if (_smokeAnchors.Count == 0)
                return;

            for (int i = 0; i < _smokeAnchors.Count; i++)
            {
                GameObject go = Instantiate(_config.smokePrefab, transform);
                go.name = $"ShipOverdriveSmoke_{i}";
                Transform anchor = _smokeAnchors[i];
                if (anchor == null)
                {
                    go.transform.localPosition = _config.localOffset;
                    go.transform.localRotation = Quaternion.Euler(_config.localEuler);
                }

                EnsureUrpSmokeMaterials(go, TeamSmokeTint(TeamId.None), "ShipOverdriveSmoke_Team", _overdriveMaterials);
                CollectAndConfigureParticleSystems(go, _overdriveParticles);
                _overdriveInstances.Add(go);
            }

            _overdriveTintSet = false;
            ApplyWorldNormalizedScale(_overdriveInstances, 1f);
            ConfigureOverdriveMainModules();
        }

        /// <summary>
        /// Lifetime and size are written once, before any puffs exist. Later bursts only change
        /// emission rate — main-module writes restart the system and delete the trail.
        /// </summary>
        void ConfigureOverdriveMainModules()
        {
            if (_config == null)
                return;

            float life = _config.maxLifetime;
            float startSize = _config.maxStartSize;
            for (int i = 0; i < _overdriveParticles.Count; i++)
            {
                ParticleSystem ps = _overdriveParticles[i];
                if (ps == null)
                    continue;

                var main = ps.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.8f, life);
                main.startSize = new ParticleSystem.MinMaxCurve(startSize * 0.65f, startSize);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.15f, 0.55f);
                main.startColor = new ParticleSystem.MinMaxGradient(Color.white);

                var emission = ps.emission;
                emission.enabled = true;
                emission.rateOverTime = 0f;
                emission.rateOverDistance = 0f;
            }
        }

        /// <summary>Stops spawning team puffs. Already-born puffs keep fading in world space.</summary>
        void StopOverdriveEmission()
        {
            if (!_overdriveWasEmitting && _overdriveInstances.Count == 0)
                return;

            ApplyOverdriveEmission(false, 0f);
            _overdriveWasEmitting = false;
            _overdriveSpeedFactor = 0f;
        }

        /// <summary>
        /// Turns the team plume on or off by emission rate only. The system stays playing, so
        /// puffs already in the world keep fading and the next burst starts ahead of them.
        /// </summary>
        void ApplyOverdriveEmission(bool emit, float speedFactor)
        {
            if (_config == null)
                return;

            speedFactor = math.saturate(speedFactor);
            float rateOverTime = emit ? _config.maxEmissionRate : 0f;
            float rateOverDistance = emit ? _config.maxRateOverDistance * speedFactor : 0f;

            for (int i = 0; i < _overdriveInstances.Count; i++)
            {
                GameObject go = _overdriveInstances[i];
                if (go != null && !go.activeSelf)
                    go.SetActive(true);
            }

            for (int i = 0; i < _overdriveParticles.Count; i++)
            {
                ParticleSystem ps = _overdriveParticles[i];
                if (ps == null)
                    continue;

                // Keep the module enabled. Stop() / disabling it latches "do not emit", and the
                // only public restart (Stop + Clear + Play) deletes the trail still on screen.
                var emission = ps.emission;
                emission.enabled = true;
                emission.rateOverTime = rateOverTime;
                emission.rateOverDistance = rateOverDistance;

                if (emit && !ps.isPlaying)
                    ps.Play();
            }
        }

        /// <summary>Speed-only trail density for the team plume.</summary>
        void ApplyOverdriveRateOverDistance(float speedFactor)
        {
            if (_config == null)
                return;

            float rateOverDistance = _config.maxRateOverDistance * math.saturate(speedFactor);
            for (int i = 0; i < _overdriveParticles.Count; i++)
            {
                ParticleSystem ps = _overdriveParticles[i];
                if (ps == null)
                    continue;

                var emission = ps.emission;
                emission.rateOverDistance = rateOverDistance;
            }
        }

        /// <summary>
        /// Play() if a parent-scale write stopped the team plume while the burst is still held.
        /// Does not clear particles — a Stop+Clear here blinks out the previous trail.
        /// </summary>
        void ResumeOverdriveEmitters()
        {
            if (_config == null)
                return;

            for (int i = 0; i < _overdriveParticles.Count; i++)
            {
                ParticleSystem ps = _overdriveParticles[i];
                if (ps == null || ps.isPlaying)
                    continue;

                var emission = ps.emission;
                emission.enabled = true;
                if (emission.rateOverTime.constant <= 0f)
                    emission.rateOverTime = _config.maxEmissionRate;
                ps.Play();
            }
        }

        /// <summary>Writes the team color onto overdrive materials only.</summary>
        void TintOverdriveMaterials(Color tint)
        {
            for (int i = 0; i < _overdriveMaterials.Count; i++)
            {
                Material mat = _overdriveMaterials[i];
                if (mat == null)
                    continue;

                if (mat.HasProperty("_BaseColor"))
                    mat.SetColor("_BaseColor", tint);
                if (mat.HasProperty("_Color"))
                    mat.SetColor("_Color", tint);
                if (mat.HasProperty("_TintColor"))
                    mat.SetColor("_TintColor", tint);
                mat.color = tint;
            }
        }
    }
}
