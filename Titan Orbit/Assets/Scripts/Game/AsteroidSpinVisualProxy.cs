using SpaceGraphicsToolkit;
using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Cosmetic tumble for asteroid presentation proxies. Each rock gets a stable
    /// random 3D spin axis and a speed rolled in
    /// [<see cref="AsteroidSettings.MinSpinSpeed"/>, <see cref="AsteroidSettings.MaxSpinSpeed"/>],
    /// seeded from world XZ so the same asteroid looks the same across frames. Render only —
    /// no sim impact.
    /// <para>
    /// Spin runs on a <b>child pivot</b>, not the proxy root. The root Transform is owned by
    /// <see cref="EcsWorldVisualizer"/> for toroidal position/scale. The visible mesh lives under
    /// the pivot: asteroids use <see cref="SgtPlanet"/> on the prefab root, so we migrate that
    /// component onto a child under the pivot (same pattern as <see cref="PlanetSpinVisualProxy"/>).
    /// Rotating an empty pivot while <c>SgtPlanet</c> stayed on the root made rocks look frozen.
    /// </para>
    /// </summary>
    public class AsteroidSpinVisualProxy : MonoBehaviour
    {
        /// <summary>Child GameObject name for the tumble pivot under the proxy root.</summary>
        const string SpinPivotName = "AsteroidSpinPivot";

        /// <summary>Child that holds the migrated <see cref="SgtPlanet"/> mesh under the pivot.</summary>
        const string AsteroidBodyName = "AsteroidBody";

        /// <summary>
        /// Fallback min/max when <see cref="AsteroidSettings"/> is missing — matches the old
        /// hardcoded 20–50 deg/s tumble range.
        /// </summary>
        const float DefaultMinSpinSpeedDegreesPerSecond = 20f;
        const float DefaultMaxSpinSpeedDegreesPerSecond = 50f;

        /// <summary>Child that receives the tumble; root stays static for position sync.</summary>
        Transform _spinPivot;

        /// <summary>Unit axis in world space — random per asteroid, stable after Configure.</summary>
        Vector3 _rotationAxis;

        /// <summary>
        /// Tumble rate in degrees per second, rolled once in [MinSpinSpeed, MaxSpinSpeed].
        /// </summary>
        float _rotationSpeed;

        /// <summary>True after Configure has seeded axis/speed and built the pivot hierarchy.</summary>
        bool _configured;

        /// <summary>
        /// Seeds spin from world XZ, rolls designer speed from <see cref="AsteroidSettingsCache"/>,
        /// and ensures the SgtPlanet / mesh hierarchy lives under the spin pivot.
        /// Called once when the hybrid asteroid proxy is created (or again safely if already set up).
        /// </summary>
        /// <param name="worldPosition">Logical/display spawn position used as RNG seed.</param>
        public void Configure(Vector3 worldPosition)
        {
            // --- Seed RNG from world XZ for stable per-asteroid tumble ---
            // [STANDARD] Same seed every call → same axis/speed if Configure runs twice.
            int hash = (int)(worldPosition.x * 1000f + worldPosition.z * 1000f);
            var rng = new System.Random(hash);

            // --- Random 3D axis (any direction, not just horizontal) ---
            // [TITAN-ORBIT] Full unit sphere so rocks tumble differently — Y was previously forced to 0.
            _rotationAxis = new Vector3(
                (float)(rng.NextDouble() * 2d - 1d),
                (float)(rng.NextDouble() * 2d - 1d),
                (float)(rng.NextDouble() * 2d - 1d));
            if (_rotationAxis.sqrMagnitude < 0.01f)
                _rotationAxis = Vector3.right;
            else
                _rotationAxis.Normalize();

            // --- Speed: uniform roll in [MinSpinSpeed, MaxSpinSpeed] from AsteroidSettings ---
            ResolveSpinSpeedRange(out float minSpeed, out float maxSpeed);
            float t = (float)rng.NextDouble();
            _rotationSpeed = Mathf.Lerp(minSpeed, maxSpeed, t);

            // --- Pivot under root so EcsWorldVisualizer can leave root rotation alone ---
            EnsureSpinPivot();
            _configured = true;
        }

        /// <summary>
        /// Reads <see cref="AsteroidSettings.MinSpinSpeed"/> / <see cref="AsteroidSettings.MaxSpinSpeed"/>
        /// from the cache, or the code defaults (20–50) when the asset has not loaded yet.
        /// </summary>
        /// <param name="minSpeed">Lower tumble rate (deg/s), ≥ 0.</param>
        /// <param name="maxSpeed">Upper tumble rate (deg/s), ≥ minSpeed.</param>
        static void ResolveSpinSpeedRange(out float minSpeed, out float maxSpeed)
        {
            var settings = AsteroidSettingsCache.Settings;
            if (settings == null)
            {
                minSpeed = DefaultMinSpinSpeedDegreesPerSecond;
                maxSpeed = DefaultMaxSpinSpeedDegreesPerSecond;
                return;
            }

            settings.ClampValues();
            minSpeed = settings.MinSpinSpeed;
            maxSpeed = settings.MaxSpinSpeed;
        }

        /// <summary>
        /// Creates <see cref="SpinPivotName"/>, reparents visual children, migrates root
        /// <see cref="SgtPlanet"/> under the pivot, and falls back to MeshFilter migration for
        /// non-SGT meshes.
        /// </summary>
        void EnsureSpinPivot()
        {
            // --- Find or create the spin pivot (idempotent) ---
            if (_spinPivot == null)
            {
                Transform existing = transform.Find(SpinPivotName);
                if (existing != null)
                    _spinPivot = existing;
                else
                {
                    var pivotGo = new GameObject(SpinPivotName);
                    _spinPivot = pivotGo.transform;
                    _spinPivot.SetParent(transform, false);
                    _spinPivot.localPosition = Vector3.zero;
                    _spinPivot.localRotation = Quaternion.identity;
                    _spinPivot.localScale = Vector3.one;
                }
            }

            // --- Reparent existing visual children under the pivot ---
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child == _spinPivot)
                    continue;
                child.SetParent(_spinPivot, true);
            }

            // --- SgtPlanet lives on the prefab root — move it under the pivot so tumble is visible ---
            // [HYBRID] Without this, LateUpdate spun an empty pivot while the rock mesh stayed still.
            MigrateSgtPlanetToPivot();

            // --- Root-level MeshFilter/MeshRenderer (non-SGT / primitive fallback) ---
            MigrateRootMeshToPivot();
        }

        /// <summary>
        /// Copies root <see cref="SgtPlanet"/> onto a child under the spin pivot, then removes the
        /// root component. Matches <see cref="PlanetSpinVisualProxy"/> so the visible mesh rotates.
        /// No-op when already migrated or when the prefab has no SgtPlanet.
        /// </summary>
        void MigrateSgtPlanetToPivot()
        {
            if (_spinPivot == null)
                return;

            // Already under the pivot (Configure called twice, or prior successful migrate).
            if (_spinPivot.Find(AsteroidBodyName) != null)
                return;

            var sgt = GetComponent<SgtPlanet>();
            if (sgt == null)
                return;

            var bodyGo = new GameObject(AsteroidBodyName);
            bodyGo.transform.SetParent(_spinPivot, false);
            bodyGo.transform.localPosition = Vector3.zero;
            bodyGo.transform.localRotation = Quaternion.identity;
            bodyGo.transform.localScale = Vector3.one;

            var newSgt = bodyGo.AddComponent<SgtPlanet>();
            CopySgtPlanet(sgt, newSgt);

            // Asteroids are dry rocks — still migrate water helpers if a prefab ever has them.
            var waterGradient = GetComponent<SgtPlanetWaterGradient>();
            if (waterGradient != null)
            {
                var newGradient = bodyGo.AddComponent<SgtPlanetWaterGradient>();
                CopyWaterGradient(waterGradient, newGradient);
                Destroy(waterGradient);
            }

            var waterTexture = GetComponent<SgtPlanetWaterTexture>();
            if (waterTexture != null)
            {
                var newTexture = bodyGo.AddComponent<SgtPlanetWaterTexture>();
                CopyWaterTexture(waterTexture, newTexture);
                Destroy(waterTexture);
            }

            Destroy(sgt);
        }

        /// <summary>
        /// Copies the fields SGT needs so the new body looks identical to the prefab root instance.
        /// </summary>
        static void CopySgtPlanet(SgtPlanet source, SgtPlanet destination)
        {
            destination.Mesh = source.Mesh;
            destination.MeshCollider = source.MeshCollider;
            destination.Radius = source.Radius;
            destination.Material = source.Material;
            destination.SharedMaterial = source.SharedMaterial;
            destination.CastShadows = source.CastShadows;
            destination.ReceiveShadows = source.ReceiveShadows;
            destination.WaterLevel = source.WaterLevel;
            destination.Displace = source.Displace;
            destination.Displacement = source.Displacement;
            destination.ClampWater = source.ClampWater;
        }

        /// <summary>Copies water-gradient tuning when present on an asteroid prefab.</summary>
        static void CopyWaterGradient(SgtPlanetWaterGradient source, SgtPlanetWaterGradient destination)
        {
            destination.Shallow = source.Shallow;
            destination.Deep = source.Deep;
            destination.Ease = source.Ease;
            destination.Sharpness = source.Sharpness;
            destination.Scale = source.Scale;
        }

        /// <summary>Copies water-texture tuning when present on an asteroid prefab.</summary>
        static void CopyWaterTexture(SgtPlanetWaterTexture source, SgtPlanetWaterTexture destination)
        {
            destination.BaseTexture = source.BaseTexture;
            destination.Strength = source.Strength;
            destination.Speed = source.Speed;
        }

        /// <summary>
        /// Moves MeshFilter/MeshRenderer from this root onto a child under the spin pivot.
        /// Leaves the root as a pure pose holder for the visualizer. Used when there is no SgtPlanet.
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

        /// <summary>
        /// [UNITY] After presentation sync — rotate the pivot in world space so the rock tumbles.
        /// Root Transform stays still so <see cref="EcsWorldVisualizer"/> position writes stay cheap.
        /// </summary>
        void LateUpdate()
        {
            if (!_configured || _spinPivot == null || _rotationAxis.sqrMagnitude < 0.01f)
                return;

            if (_rotationSpeed <= 0.0001f)
                return;

            _spinPivot.Rotate(_rotationAxis, _rotationSpeed * Time.deltaTime, Space.World);
        }
    }
}
