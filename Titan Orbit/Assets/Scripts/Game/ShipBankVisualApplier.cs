using TitanOrbit.Data;
using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side roll banking on ship GameObject proxies (ported from legacy Starship ApplyVisualBanking).
    /// Root transform yaw comes from ECS presentation sync; roll is applied on a BankPivot child so
    /// EcsWorldVisualizer does not overwrite it. Bank follows yaw rate only — no forward thrust
    /// required. Suppressed during moon dock. Cosmetic only — no sim effect.
    /// <para>
    /// Tune Max Bank / Sensitivity / Smoothing / Reference Turn on <see cref="ShipBankVisualSettings"/>
    /// (family field, <see cref="MegaShipCatalog.bankVisualSettings"/>, or Resources default).
    /// Bound assets are sampled each frame so Inspector tweaks apply without respawning.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(85)]
    public class ShipBankVisualApplier : MonoBehaviour
    {
        const string BankPivotName = "BankPivot";
        const string PrefabContainerName = "Prefab";
        /// <summary>Ignore interpolation noise at rest. Intentional yaw (including slow MEGA turns) is above this.</summary>
        const float RestBankAngularVelDeadbandDegPerSec = 2f;

        /// <summary>
        /// Optional per-proxy override for peak roll (°). ≤ 0 means use
        /// the bound <see cref="ShipBankVisualSettings"/> / cache.
        /// </summary>
        [SerializeField] float maxBankAngleOverride = -1f;

        /// <summary>
        /// Optional per-proxy override for smoothing. ≤ 0 means use
        /// the bound <see cref="ShipBankVisualSettings"/> / cache.
        /// </summary>
        [SerializeField] float bankSmoothingOverride = -1f;

        Entity _shipEntity;
        ShipBankVisualSettings _settings;
        Transform _bankPivot;
        float _currentBankAngle;
        float _cachedBankAngularVelDegPerSec;
        float _prevBankYawDeg;
        bool _bankYawInitialized;
        bool _bankingInitialized;

        /// <summary>Links to ship entity and ensures BankPivot hierarchy exists under the proxy root.</summary>
        /// <param name="shipEntity">ECS ship ghost this proxy follows.</param>
        /// <param name="settings">
        /// Family or shared bank profile. Null falls back to
        /// <see cref="ShipBankVisualSettingsCache"/> (Resources default).
        /// </param>
        /// <param name="maxBankDegrees">
        /// Peak roll override (°). ≤ 0 keeps the bound asset / cache.
        /// </param>
        /// <param name="bankSmooth">
        /// Smoothing override. ≤ 0 keeps the bound asset / cache.
        /// </param>
        public void Bind(
            Entity shipEntity,
            ShipBankVisualSettings settings = null,
            float maxBankDegrees = -1f,
            float bankSmooth = -1f)
        {
            // --- Bind ---
            _shipEntity = shipEntity;
            _settings = settings != null ? settings : ShipBankVisualSettings.LoadDefault();
            if (maxBankDegrees > 0f)
                maxBankAngleOverride = maxBankDegrees;
            if (bankSmooth > 0f)
                bankSmoothingOverride = bankSmooth;
            EnsureBankPivotHierarchy();
            ResetBankingState();
        }

        /// <summary>
        /// Creates BankPivot → Prefab container and reparents existing mesh children so roll
        /// does not fight yaw written by EcsWorldVisualizer on the root transform.
        /// </summary>
        void EnsureBankPivotHierarchy()
        {
            // --- Ensure setup ---
            Transform existing = transform.Find(BankPivotName);
            if (existing != null)
            {
                _bankPivot = existing;
                return;
            }

            var pivot = new GameObject(BankPivotName).transform;
            pivot.SetParent(transform, false);
            pivot.localPosition = Vector3.zero;
            pivot.localRotation = Quaternion.identity;
            pivot.localScale = Vector3.one;

            var prefabContainer = new GameObject(PrefabContainerName).transform;
            prefabContainer.SetParent(pivot, false);
            prefabContainer.localPosition = Vector3.zero;
            prefabContainer.localRotation = Quaternion.identity;
            prefabContainer.localScale = Vector3.one;

            var children = new Transform[transform.childCount];
            int childCount = 0;
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child == pivot)
                    continue;
                children[childCount++] = child;
            }

            for (int i = 0; i < childCount; i++)
                children[i].SetParent(prefabContainer, true);

            _bankPivot = pivot;
        }

        /// <summary>Clears smoothed yaw/bank so a fresh bind does not inherit the previous hull's lean.</summary>
        void ResetBankingState()
        {
            // --- ResetBankingState ---
            _prevBankYawDeg = GetPlanarYawDegrees(transform.rotation);
            _bankYawInitialized = true;
            _cachedBankAngularVelDegPerSec = 0f;
            _currentBankAngle = 0f;
            _bankingInitialized = false;
            if (_bankPivot != null)
                _bankPivot.localRotation = Quaternion.identity;
        }

        /// <summary>
        /// [UNITY] After presentation pose is written: sample yaw rate, compute target bank, lerp roll.
        /// </summary>
        void LateUpdate()
        {
            // --- Per-frame refresh ---
            if (_shipEntity == Entity.Null || _bankPivot == null)
                return;

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
                    return;
            }

            // [TITAN-ORBIT] Moon dock cinematic owns transform — skip banking.
            if (em.HasComponent<ShipMoonDockState>(_shipEntity))
            {
                var moonDock = em.GetComponentData<ShipMoonDockState>(_shipEntity);
                if (moonDock.MoonPlanetId != 0 && moonDock.LandingProgress > 0.001f)
                {
                    _bankPivot.localRotation = Quaternion.identity;
                    _currentBankAngle = 0f;
                    return;
                }
            }

            float dt = Time.deltaTime;
            float smoothing = ResolveSmoothing();
            SampleBankAngularVelocity(dt, smoothing);
            ApplyVisualBanking(dt, smoothing);
        }

        /// <summary>Peak roll from per-proxy override, else the bound asset / cache.</summary>
        float ResolveMaxBankAngle()
        {
            if (maxBankAngleOverride > 0f)
                return maxBankAngleOverride;
            return _settings != null
                ? _settings.ClampedMaxBankAngleDegrees
                : ShipBankVisualSettingsCache.MaxBankAngleDegrees;
        }

        /// <summary>Smoothing from per-proxy override, else the bound asset / cache.</summary>
        float ResolveSmoothing()
        {
            if (bankSmoothingOverride > 0f)
                return bankSmoothingOverride;
            return _settings != null
                ? _settings.ClampedBankSmoothing
                : ShipBankVisualSettingsCache.BankSmoothing;
        }

        /// <summary>Turn-rate → bank multiplier from the bound asset / cache.</summary>
        float ResolveSensitivity() =>
            _settings != null
                ? _settings.ClampedBankSensitivity
                : ShipBankVisualSettingsCache.BankSensitivity;

        /// <summary>
        /// Yaw rate (°/s) treated as full turn. MEGA assets author a low reference so
        /// slow hulls still reach peak roll.
        /// </summary>
        float ResolveReferenceTurn() =>
            _settings != null
                ? _settings.ResolveReferenceTurnDegreesPerSecond()
                : ShipBankVisualSettingsCache.ReferenceTurnDegreesPerSecond;

        /// <summary>Smooths yaw rate from proxy root rotation (presentation pose).</summary>
        void SampleBankAngularVelocity(float dt, float smoothing)
        {
            // --- SampleBankAngularVelocity ---
            float yawDeg = GetPlanarYawDegrees(transform.rotation);
            if (!_bankYawInitialized)
            {
                _prevBankYawDeg = yawDeg;
                _bankYawInitialized = true;
                _cachedBankAngularVelDegPerSec = 0f;
                return;
            }

            dt = Mathf.Max(1e-5f, dt);
            float instantAngularVel = Mathf.DeltaAngle(_prevBankYawDeg, yawDeg) / dt;
            _prevBankYawDeg = yawDeg;

            float velT = 1f - Mathf.Exp(-smoothing * dt);
            _cachedBankAngularVelDegPerSec = Mathf.Lerp(_cachedBankAngularVelDegPerSec, instantAngularVel, velT);
        }

        /// <summary>
        /// Maps smoothed yaw rate → target bank (via propulsion helper + sensitivity) and lerps the pivot.
        /// </summary>
        void ApplyVisualBanking(float dt, float smoothing)
        {
            // --- Apply changes ---
            if (!_bankingInitialized)
            {
                _currentBankAngle = 0f;
                _bankingInitialized = true;
                _bankPivot.localRotation = Quaternion.identity;
                return;
            }

            float signedAngularVelDegPerSec = _cachedBankAngularVelDegPerSec;

            // [TITAN-ORBIT] Kill rest-pose interpolation noise only — rotating in place still banks.
            if (Mathf.Abs(signedAngularVelDegPerSec) < RestBankAngularVelDeadbandDegPerSec)
                signedAngularVelDegPerSec = 0f;

            // --- Target bank from turn rate + bound asset (MEGA catalog or family) ---
            float targetBankAngle = ShipPropulsionAggregation.ComputeVisualBankTargetAngle(
                signedAngularVelDegPerSec,
                ResolveMaxBankAngle(),
                ResolveReferenceTurn(),
                ResolveSensitivity());

            float bankT = 1f - Mathf.Exp(-smoothing * dt);
            _currentBankAngle = Mathf.Lerp(_currentBankAngle, targetBankAngle, bankT);
            _bankPivot.localRotation = Quaternion.Euler(0f, 0f, -_currentBankAngle);
        }

        /// <summary>Planar yaw (degrees) from a world rotation — ignores pitch so bank tracks turn only.</summary>
        static float GetPlanarYawDegrees(Quaternion rotation)
        {
            // --- Compute value ---
            Vector3 fwd = rotation * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-8f)
                return 0f;
            return Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        }
    }
}
