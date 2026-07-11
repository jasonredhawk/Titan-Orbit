using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Deterministic gentle tumble for asteroid presentation proxies. Position-hash seeds
    /// axis and speed so each asteroid looks unique but stable across frames. Render only.
    /// </summary>
    public class AsteroidSpinVisualProxy : MonoBehaviour
    {
        Vector3 _rotationAxis;
        float _rotationSpeed;
        bool _configured;

        public void Configure(Vector3 worldPosition)
        {
            // --- Seed RNG from world XZ for stable per-asteroid spin ---
            int hash = (int)(worldPosition.x * 1000f + worldPosition.z * 1000f);
            var rng = new System.Random(hash);
            _rotationAxis = new Vector3(
                (float)(rng.NextDouble() * 2d - 1d),
                0f,
                (float)(rng.NextDouble() * 2d - 1d));
            if (_rotationAxis.sqrMagnitude < 0.01f)
                _rotationAxis = Vector3.right;
            else
                _rotationAxis.Normalize();

            _rotationSpeed = 20f + (float)(rng.NextDouble() * 30d);
            _configured = true;
        }

        void LateUpdate()
        {
            // --- Cosmetic world-space rotation ---
            if (!_configured || _rotationAxis.sqrMagnitude < 0.01f)
                return;

            transform.Rotate(_rotationAxis, _rotationSpeed * Time.deltaTime, Space.World);
        }
    }
}
