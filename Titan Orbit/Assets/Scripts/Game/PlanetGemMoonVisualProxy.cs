using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Gem-moon visual child of a planet GameObject proxy. Positions the moon as
    /// parent-planet display pose + shared ring offset (<see cref="PlanetGemMoonMath.GetMoonOrbitOffset"/>)
    /// on the <see cref="PlanetGemMoonOrbitClock"/> (NetCode ServerTick seconds), spins the mesh
    /// cosmetically, and wires orbit zone, matrix shield, and stats label children. Render only —
    /// moon combat/shield sim is server ECS. Staying glued to the planet proxy tile keeps the moon
    /// on the same hysteresis copy as the orbit ring across toroidal seams.
    /// </summary>
    public class PlanetGemMoonVisualProxy : MonoBehaviour
    {
        /// <summary>Cosmetic spin rate for moon mesh (degrees per second).</summary>
        const float SpinSpeed = 9f;
        const float HomeScaleMultiplier = 1.5f;
        const string MoonRootName = "GemMoonVisual";
        const string MoonSpinMeshName = "GemMoonSpinMesh";

        Transform _moonRoot;
        Transform _moonSpinVisual;
        float _planetSize;
        int _planetLevel;
        int _planetId;
        bool _isHome;
        TeamId _team = TeamId.None;
        Material _moonMaterial;
        GemMoonWorldStatsLabel _statsLabel;

        public int PlanetId => _planetId;

        public float PlanetSize => _planetSize;

        public bool IsHome => _isHome;

        /// <summary>
        /// Moon body radius in moon-root local space. Planet roots are unit-scale, so this
        /// equals <see cref="MoonBodyRadiusWorld"/> (no inherited planet scale).
        /// </summary>
        public float MoonBodyRadiusLocal => MoonBodyRadiusWorld;

        /// <summary>Dock snap radius in moon-root local (= world under unit planet roots).</summary>
        public float MoonDockSnapRadiusLocal =>
            PlanetGemMoonMath.GetMoonDockRadiusWorld(_planetSize, _isHome);

        /// <summary>Orbit-zone shell outer radius in moon-root local (= world).</summary>
        public float MoonVisualShellOuterRadiusLocal =>
            PlanetGemMoonMath.GetMoonVisualShellOuterRadiusWorld(_planetSize, _isHome);

        /// <summary>Matrix shield outer radius in moon-root local (= world).</summary>
        public float MoonShieldOuterRadiusLocal =>
            PlanetGemMoonMath.GetMoonShieldOuterRadiusWorld(_planetSize, _isHome);

        public float CurrentShieldRatio
        {
            get
            {
                if (EcsGameBridge.TryGetPlanetGemMoonStateByPlanetId(_planetId, out PlanetGemMoonState state)
                    && state.MaxShield > 0.001f)
                    return Mathf.Clamp01(state.CurrentShield / state.MaxShield);
                return 1f;
            }
        }

        public Vector3 MoonWorldPosition =>
            _moonRoot != null ? _moonRoot.position : transform.position;

        public Vector3 SpinAxisWorld
        {
            get
            {
                float3 spinAxisLocal = PlanetOrbitMath.GetLevelBandsSpinAxisLocal();
                return transform.TransformDirection(new Vector3(spinAxisLocal.x, spinAxisLocal.y, spinAxisLocal.z));
            }
        }

        public float MoonBodyRadiusWorld
        {
            get
            {
                float homeMul = _isHome ? HomeScaleMultiplier : 1f;
                return PlanetGemMoonMath.GetMoonBodyRadiusWorld(_planetSize, _isHome);
            }
        }

        void OnEnable()
        {
            if (_planetId > 0)
                PlanetGemMoonVisualRegistry.Register(this);
        }

        void OnDisable() => PlanetGemMoonVisualRegistry.Unregister(this);

        /// <summary>
        /// [UNITY] Called from planet visual applier when proxy spawns — sets size, level, team, material.
        /// </summary>
        public void Configure(float planetSize, int planetLevel, bool isHome, int planetId, Material moonMaterial, TeamId team = TeamId.None)
        {
            // --- Cache identity from planet proxy create / later refresh ---
            // [HYBRID] WorldBodyVisualApplier calls this at Instantiates and again on level-up /
            // capture without Destroy+Instantiate (TransformQuarantine cannot rebuild via DrawPlanets).
            _planetSize = Mathf.Max(0.01f, planetSize);
            _planetLevel = Mathf.Max(1, planetLevel);
            _planetId = planetId;
            _isHome = isHome;
            _team = team;
            // [TITAN-ORBIT] null material = keep existing (level-only refresh). Non-null replaces tint.
            if (moonMaterial != null)
                _moonMaterial = moonMaterial;
            // --- Build hierarchy once, then refresh scale/material/children ---
            EnsureMoonVisual();
            ApplyMoonScale();
            ApplyMoonMaterial();
            EnsureOrbitZoneVisual();
            EnsureMatrixShieldVisual();
            EnsureStatsLabel();
            if (_planetId > 0 && isActiveAndEnabled)
                PlanetGemMoonVisualRegistry.Register(this);
        }

        void EnsureMoonVisual()
        {
            if (_moonRoot != null && _moonSpinVisual != null)
                return;

            Transform existing = transform.Find(MoonRootName);
            if (existing != null)
            {
                _moonRoot = existing;
                Transform spin = existing.Find(MoonSpinMeshName);
                if (spin != null)
                {
                    _moonSpinVisual = spin;
                    return;
                }

                MigrateLegacyMoonRoot(existing);
                return;
            }

            var rootGo = new GameObject(MoonRootName);
            rootGo.transform.SetParent(transform, false);
            _moonRoot = rootGo.transform;
            _moonSpinVisual = CreateSpinSphere(_moonRoot);
        }

        void MigrateLegacyMoonRoot(Transform root)
        {
            _moonRoot = root;

            var legacyRenderer = root.GetComponent<Renderer>();
            if (legacyRenderer != null && _moonMaterial == null)
                _moonMaterial = legacyRenderer.material;

            Vector3 legacyScale = root.localScale;
            _moonSpinVisual = CreateSpinSphere(root);

            if (legacyScale != Vector3.zero && legacyScale != Vector3.one)
                _moonSpinVisual.localScale = legacyScale;

            DestroyColliderAndMeshOnRoot(root);
            root.localScale = Vector3.one;
        }

        static Transform CreateSpinSphere(Transform parent)
        {
            var vis = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            vis.name = MoonSpinMeshName;
            var col = vis.GetComponent<Collider>();
            if (col != null)
                Destroy(col); // [HYBRID] Hull collision is ECS kinematic sphere — see PlanetGemMoonColliderSystems.
            vis.transform.SetParent(parent, false);
            return vis.transform;
        }

        static void DestroyColliderAndMeshOnRoot(Transform root)
        {
            var col = root.GetComponent<Collider>();
            if (col != null)
                Destroy(col);
            var filter = root.GetComponent<MeshFilter>();
            if (filter != null)
                Destroy(filter);
            var renderer = root.GetComponent<Renderer>();
            if (renderer != null)
                Destroy(renderer);
        }

        void ApplyMoonScale()
        {
            if (_moonSpinVisual == null)
                return;

            // Unit-scale planet root — moon mesh uses true world uniform size.
            float homeMul = _isHome ? HomeScaleMultiplier : 1f;
            float worldUniform = PlanetGemMoonMath.ComputeVisualWorldUniformScale(_planetSize, homeMul);
            _moonSpinVisual.localScale = Vector3.one * worldUniform;
        }

        void ApplyMoonMaterial()
        {
            if (_moonSpinVisual == null)
                return;

            var renderer = _moonSpinVisual.GetComponent<Renderer>();
            if (renderer == null)
                return;

            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            if (_moonMaterial != null)
                renderer.material = _moonMaterial;
        }

        void EnsureOrbitZoneVisual()
        {
            if (_moonRoot == null)
                return;

            GemMoonOrbitZoneVisual.EnsureOnMoonRoot(_moonRoot, this);
        }

        void EnsureMatrixShieldVisual()
        {
            if (_moonRoot == null)
                return;

            GemMoonMatrixShieldVisual.EnsureOnMoonRoot(_moonRoot, this, _team);
        }

        void EnsureStatsLabel()
        {
            if (_moonRoot == null)
                return;

            if (_statsLabel == null)
                _statsLabel = _moonRoot.GetComponent<GemMoonWorldStatsLabel>();
            if (_statsLabel == null)
                _statsLabel = _moonRoot.gameObject.AddComponent<GemMoonWorldStatsLabel>();
            // Moon-root local == world under unit planet roots.
            _statsLabel.Configure(_planetId, MoonBodyRadiusWorld);
        }

        /// <summary>
        /// [UNITY] LateUpdate — moon rides the parent planet proxy tile + analytic ring offset;
        /// mesh spin is cosmetic only.
        /// </summary>
        void LateUpdate()
        {
            if (_moonRoot == null || _moonSpinVisual == null)
                return;

            // --- Shared ServerTick orbit clock (matches colliders / shield / dock) ---
            // [TITAN-ORBIT] Never World.Time.ElapsedTime or Time.timeAsDouble — those diverge on late-join.
            double elapsed = PlanetGemMoonOrbitClock.TryGetElapsedSeconds(out double orbitElapsed, includeTickFraction: true)
                ? orbitElapsed
                : Time.timeAsDouble;

            // --- Moon on the same display tile as the planet proxy ---
            // [TITAN-ORBIT] Parent <see cref="transform"/> is already hysteresis-tiled by
            // <c>EcsWorldVisualizer</c>. Offset with the shared ring formula so the moon, orbit
            // ring, and planet stay glued. Independent GetMoonWorldPositionNear (no hysteresis)
            // used to jump a full map width while the planet lagged — stepped orbit across seams.
            var offset = PlanetGemMoonMath.GetMoonOrbitOffset(
                _planetSize, _planetLevel, _isHome, _planetId, elapsed);
            _moonRoot.position = transform.position + new Vector3(offset.x, offset.y, offset.z);

            _moonRoot.rotation = Quaternion.identity;
            float3 spinAxisLocal = PlanetOrbitMath.GetLevelBandsSpinAxisLocal();
            Vector3 spinAxisWorld = transform.TransformDirection(new Vector3(spinAxisLocal.x, spinAxisLocal.y, spinAxisLocal.z));
            _moonSpinVisual.Rotate(spinAxisWorld, SpinSpeed * Time.deltaTime, Space.World);
        }
    }
}
