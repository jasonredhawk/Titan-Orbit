using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Orbiting gem-moon visual parented to a planet proxy.</summary>
    public class PlanetGemMoonVisualProxy : MonoBehaviour
    {
        const float SpinSpeed = 9f;
        const float HomeScaleMultiplier = 1.5f;

        Transform _moonVisual;
        float _planetSize;
        int _planetLevel;
        float _phaseOffset;
        bool _isHome;
        Material _moonMaterial;

        public void Configure(float planetSize, int planetLevel, bool isHome, int planetId, Material moonMaterial)
        {
            _planetSize = Mathf.Max(0.01f, planetSize);
            _planetLevel = Mathf.Max(1, planetLevel);
            _isHome = isHome;
            _phaseOffset = PlanetOrbitMath.GetShipOrbitPhaseOffset(planetId);
            _moonMaterial = moonMaterial;
            EnsureMoonVisual();
            ApplyMoonScale();
            ApplyMoonMaterial();
        }

        void EnsureMoonVisual()
        {
            if (_moonVisual != null)
                return;

            var existing = transform.Find("GemMoonVisual");
            if (existing != null)
            {
                _moonVisual = existing;
                return;
            }

            var vis = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            vis.name = "GemMoonVisual";
            var col = vis.GetComponent<Collider>();
            if (col != null)
                Destroy(col);
            vis.transform.SetParent(transform, false);
            _moonVisual = vis.transform;
        }

        void ApplyMoonScale()
        {
            if (_moonVisual == null)
                return;

            float homeMul = _isHome ? HomeScaleMultiplier : 1f;
            float uniform = PlanetGemMoonMath.ComputeVisualUniformScale(_planetSize, homeMul);
            _moonVisual.localScale = Vector3.one * uniform;
        }

        void ApplyMoonMaterial()
        {
            if (_moonVisual == null)
                return;

            var renderer = _moonVisual.GetComponent<Renderer>();
            if (renderer == null)
                return;

            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            if (_moonMaterial != null)
                renderer.material = _moonMaterial;
        }

        void LateUpdate()
        {
            if (_moonVisual == null)
                return;

            double elapsed = TryGetSimulationElapsedTime(out double simElapsed)
                ? simElapsed
                : Time.timeAsDouble;

            var offset = PlanetOrbitMath.GetShipOrbitRingOffset(_planetSize, _planetLevel, _phaseOffset, elapsed);
            _moonVisual.position = transform.position + new Vector3(offset.x, offset.y, offset.z);
            _moonVisual.Rotate(0f, SpinSpeed * Time.deltaTime, 0f, Space.Self);
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
