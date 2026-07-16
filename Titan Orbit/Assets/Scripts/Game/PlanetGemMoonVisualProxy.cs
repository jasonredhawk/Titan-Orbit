using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Gem-moon visual child of a planet GameObject proxy. Positions moon from
    /// <see cref="PlanetOrbitMath"/> / ECS planet pose, spins mesh cosmetically, and wires orbit zone,
    /// matrix shield, and stats label children. Render only — moon combat/shield sim is server ECS.
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

        public float MoonBodyRadiusLocal =>
            PlanetGemMoonMath.GetMoonBodyRadiusLocal(_planetSize, _isHome);

        public float MoonDockSnapRadiusLocal =>
            PlanetGemMoonMath.GetMoonDockSnapRadiusLocal(_planetSize, _isHome);

        public float MoonVisualShellOuterRadiusLocal =>
            PlanetGemMoonMath.GetMoonVisualShellOuterRadiusLocal(_planetSize, _isHome);

        public float MoonShieldOuterRadiusLocal =>
            PlanetGemMoonMath.GetMoonShieldOuterRadiusLocal(_planetSize, _isHome);

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
            _planetSize = Mathf.Max(0.01f, planetSize);
            _planetLevel = Mathf.Max(1, planetLevel);
            _planetId = planetId;
            _isHome = isHome;
            _team = team;
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

            float homeMul = _isHome ? HomeScaleMultiplier : 1f;
            float uniform = PlanetGemMoonMath.ComputeVisualUniformScale(_planetSize, homeMul);
            _moonSpinVisual.localScale = Vector3.one * uniform;
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
            float homeMul = _isHome ? HomeScaleMultiplier : 1f;
            float moonLocalRadius = 0.5f * PlanetGemMoonMath.ComputeVisualUniformScale(_planetSize, homeMul);
            _statsLabel.Configure(_planetId, moonLocalRadius);
        }

        /// <summary>
        /// [UNITY] LateUpdate — moon orbit position from sim time + toroidal nearest copy; mesh spin cosmetic.
        /// </summary>
        void LateUpdate()
        {
            if (_moonRoot == null || _moonSpinVisual == null)
                return;

            // --- Prefer NetCode world elapsed time for orbit phase; fall back to Time ---
            double elapsed = TryGetSimulationElapsedTime(out double simElapsed)
                ? simElapsed
                : Time.timeAsDouble;

            // --- Moon world position: ECS pose when available, else analytic offset ---
            if (TryResolveMoonWorldPosition(elapsed, out float3 moonPos))
                _moonRoot.position = new Vector3(moonPos.x, moonPos.y, moonPos.z);
            else
            {
                var offset = PlanetGemMoonMath.GetMoonOrbitOffset(_planetSize, _planetLevel, _isHome, _planetId, elapsed);
                _moonRoot.position = transform.position + new Vector3(offset.x, offset.y, offset.z);
            }

            _moonRoot.rotation = Quaternion.identity;
            float3 spinAxisLocal = PlanetOrbitMath.GetLevelBandsSpinAxisLocal();
            Vector3 spinAxisWorld = transform.TransformDirection(new Vector3(spinAxisLocal.x, spinAxisLocal.y, spinAxisLocal.z));
            _moonSpinVisual.Rotate(spinAxisWorld, SpinSpeed * Time.deltaTime, Space.World);
        }

        static bool TryGetSimulationElapsedTime(out double elapsedSeconds)
        {
            elapsedSeconds = 0d;
            World world = null;
            if (ClientServerBootstrap.ServerWorld != null && ClientServerBootstrap.ServerWorld.IsCreated)
                world = ClientServerBootstrap.ServerWorld;
            else if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
                world = ClientServerBootstrap.ClientWorld;

            if (world == null || !world.IsCreated)
                return false;

            elapsedSeconds = world.Time.ElapsedTime;
            return true;
        }

        bool TryResolveMoonWorldPosition(double elapsed, out float3 moonPos)
        {
            moonPos = default;
            if (_planetId <= 0)
                return false;

            float mapW = ToroidalMapEcs.MapWidth;
            float mapH = ToroidalMapEcs.MapHeight;
            World world = null;
            if (ClientServerBootstrap.ServerWorld != null && ClientServerBootstrap.ServerWorld.IsCreated)
                world = ClientServerBootstrap.ServerWorld;
            else if (ClientServerBootstrap.ClientWorld != null && ClientServerBootstrap.ClientWorld.IsCreated)
                world = ClientServerBootstrap.ClientWorld;

            if (world != null && world.IsCreated)
            {
                using var mapQuery = world.EntityManager.CreateEntityQuery(typeof(MapStateSingleton));
                if (mapQuery.TryGetSingleton<MapStateSingleton>(out var map))
                {
                    mapW = math.max(100f, map.MapWidth);
                    mapH = math.max(100f, map.MapHeight);
                }
            }

            float3 reference = new float3(transform.position.x, 0f, transform.position.z);
            if (EcsGameBridge.TryGetLocalShipPosition(out var shipPos))
                reference = new float3(shipPos.x, 0f, shipPos.z);

            if (!EcsGameBridge.TryGetPlanetPoseByPlanetId(_planetId, out float3 logicalPlanet, out float planetScale, out var planetState))
                return false;

            moonPos = PlanetOrbitMath.GetMoonWorldPositionNear(
                reference,
                logicalPlanet,
                math.max(0.25f, planetScale),
                planetState.PlanetLevel,
                _planetId,
                elapsed,
                mapW,
                mapH);
            return true;
        }
    }
}
