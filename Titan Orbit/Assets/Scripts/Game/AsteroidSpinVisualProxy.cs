using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Deterministic gentle tumble for asteroid presentation proxies. Position-hash seeds
    /// axis and speed so each asteroid looks unique but stable across frames. Render only.
    /// <para>
    /// Spin runs on a <b>child pivot</b>, not the proxy root. The root Transform is owned by
    /// <see cref="EcsWorldVisualizer"/> for toroidal position/scale. Rotating the root every
    /// LateUpdate (old behavior) dirtied ~230 asteroid Transforms every frame even when position
    /// sync skipped writes — post-fix4 still showed ~25 ms wallMs with bodyWrites:0.
    /// Same pattern as <see cref="PlanetSpinVisualProxy"/> (child pivot).
    /// </para>
    /// </summary>
    public class AsteroidSpinVisualProxy : MonoBehaviour
    {
        const string SpinPivotName = "AsteroidSpinPivot";

        /// <summary>Child that receives the tumble; root stays static for position sync.</summary>
        Transform _spinPivot;

        Vector3 _rotationAxis;
        float _rotationSpeed;
        bool _configured;

        /// <summary>
        /// Seeds spin from world XZ and ensures the mesh hierarchy lives under the spin pivot.
        /// Called once when the hybrid asteroid proxy is created.
        /// </summary>
        /// <param name="worldPosition">Logical/display spawn position used as RNG seed.</param>
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

            // --- Pivot under root so EcsWorldVisualizer can leave root rotation alone ---
            EnsureSpinPivot();
            _configured = true;
        }

        /// <summary>
        /// Creates <see cref="SpinPivotName"/>, reparents visual children, and migrates any
        /// MeshFilter/MeshRenderer that lived on the root (so the root Transform can stay still).
        /// </summary>
        void EnsureSpinPivot()
        {
            if (_spinPivot != null)
                return;

            Transform existing = transform.Find(SpinPivotName);
            if (existing != null)
            {
                _spinPivot = existing;
                return;
            }

            var pivotGo = new GameObject(SpinPivotName);
            _spinPivot = pivotGo.transform;
            _spinPivot.SetParent(transform, false);
            _spinPivot.localPosition = Vector3.zero;
            _spinPivot.localRotation = Quaternion.identity;
            _spinPivot.localScale = Vector3.one;

            // --- Reparent existing visual children under the pivot ---
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child == _spinPivot)
                    continue;
                child.SetParent(_spinPivot, true);
            }

            // --- Root-level mesh (prefab with MeshFilter on the GO itself) ---
            MigrateRootMeshToPivot();
        }

        /// <summary>
        /// Moves MeshFilter/MeshRenderer from this root onto a child under the spin pivot.
        /// Leaves the root as a pure pose holder for the visualizer.
        /// </summary>
        void MigrateRootMeshToPivot()
        {
            var mf = GetComponent<MeshFilter>();
            var mr = GetComponent<MeshRenderer>();
            if (mf == null && mr == null)
                return;

            var meshGo = new GameObject("AsteroidMesh");
            meshGo.transform.SetParent(_spinPivot, false);

            if (mf != null)
            {
                var newMf = meshGo.AddComponent<MeshFilter>();
                newMf.sharedMesh = mf.sharedMesh;
                Destroy(mf);
            }

            if (mr != null)
            {
                var newMr = meshGo.AddComponent<MeshRenderer>();
                newMr.sharedMaterials = mr.sharedMaterials;
                newMr.shadowCastingMode = mr.shadowCastingMode;
                newMr.receiveShadows = mr.receiveShadows;
                Destroy(mr);
            }
        }

        void LateUpdate()
        {
            // --- Cosmetic world-space rotation on the pivot only ---
            if (!_configured || _spinPivot == null || _rotationAxis.sqrMagnitude < 0.01f)
                return;

            _spinPivot.Rotate(_rotationAxis, _rotationSpeed * Time.deltaTime, Space.World);
        }
    }
}
