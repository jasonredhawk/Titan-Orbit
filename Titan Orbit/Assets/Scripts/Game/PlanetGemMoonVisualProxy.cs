using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Orbiting gem-moon visual parented to a planet proxy.</summary>
    public class PlanetGemMoonVisualProxy : MonoBehaviour
    {
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
        Material _moonMaterial;
        GemMoonWorldStatsLabel _statsLabel;

        public void Configure(float planetSize, int planetLevel, bool isHome, int planetId, Material moonMaterial)
        {
            _planetSize = Mathf.Max(0.01f, planetSize);
            _planetLevel = Mathf.Max(1, planetLevel);
            _planetId = planetId;
            _isHome = isHome;
            _moonMaterial = moonMaterial;
            EnsureMoonVisual();
            ApplyMoonScale();
            ApplyMoonMaterial();
            EnsureStatsLabel();
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
                Destroy(col);
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

        void LateUpdate()
        {
            if (_moonRoot == null || _moonSpinVisual == null)
                return;

            double elapsed = TryGetSimulationElapsedTime(out double simElapsed)
                ? simElapsed
                : Time.timeAsDouble;

            var offset = PlanetGemMoonMath.GetMoonOrbitOffset(_planetSize, _planetLevel, _isHome, _planetId, elapsed);
            _moonRoot.position = transform.position + new Vector3(offset.x, offset.y, offset.z);
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
    }
}
