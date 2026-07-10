using TitanOrbit.Data;
using TitanOrbit.ECS;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side roll banking on ship GameObject proxies (ported from legacy Starship ApplyVisualBanking).
    /// Root transform yaw comes from ECS presentation sync; roll is applied on a BankPivot child so
    /// EcsWorldVisualizer does not overwrite it. Reads ShipKinematics for idle detection and yaw rate
    /// from proxy rotation. Suppressed during moon dock. Cosmetic only — no sim effect.
    /// </summary>
    [DefaultExecutionOrder(85)]
    public class ShipBankVisualApplier : MonoBehaviour
    {
        const string BankPivotName = "BankPivot";
        const string PrefabContainerName = "Prefab";
        const float IdleVisualLinearSpeedThreshold = 0.12f;
        const float IdleBankAngularVelDeadbandDegPerSec = 18f;

        [SerializeField] float maxBankAngle = ShipPropulsionAggregation.VisualBankReferenceMaxAngleDegrees;
        [SerializeField] float bankSmoothing = 8f;

        Entity _shipEntity;
        Transform _bankPivot;
        float _currentBankAngle;
        float _cachedBankAngularVelDegPerSec;
        float _prevBankYawDeg;
        bool _bankYawInitialized;
        bool _bankingInitialized;

        /// <summary>Links to ship entity and ensures BankPivot hierarchy exists under the proxy root.</summary>
        public void Bind(Entity shipEntity, float maxBankDegrees = -1f, float bankSmooth = -1f)
        {
            _shipEntity = shipEntity;
            if (maxBankDegrees > 0f)
                maxBankAngle = maxBankDegrees;
            if (bankSmooth > 0f)
                bankSmoothing = bankSmooth;
            EnsureBankPivotHierarchy();
            ResetBankingState();
        }

        /// <summary>
        /// Creates BankPivot → Prefab container and reparents existing mesh children so roll
        /// does not fight yaw written by EcsWorldVisualizer on the root transform.
        /// </summary>
        void EnsureBankPivotHierarchy()
        {
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

        void ResetBankingState()
        {
            _prevBankYawDeg = GetPlanarYawDegrees(transform.rotation);
            _bankYawInitialized = true;
            _cachedBankAngularVelDegPerSec = 0f;
            _currentBankAngle = 0f;
            _bankingInitialized = false;
            if (_bankPivot != null)
                _bankPivot.localRotation = Quaternion.identity;
        }

        void LateUpdate()
        {
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
            SampleBankAngularVelocity(dt);
            ApplyVisualBanking(dt);
        }

        /// <summary>Smooths yaw rate from proxy root rotation (presentation pose).</summary>
        void SampleBankAngularVelocity(float dt)
        {
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

            float velT = 1f - Mathf.Exp(-bankSmoothing * dt);
            _cachedBankAngularVelDegPerSec = Mathf.Lerp(_cachedBankAngularVelDegPerSec, instantAngularVel, velT);
        }

        void ApplyVisualBanking(float dt)
        {
            if (!_bankingInitialized)
            {
                _currentBankAngle = 0f;
                _bankingInitialized = true;
                _bankPivot.localRotation = Quaternion.identity;
                return;
            }

            float signedAngularVelDegPerSec = _cachedBankAngularVelDegPerSec;

            Vector3 velFlat = Vector3.zero;
            var world = EcsGameBridge.GetVisualizationWorld();
            if (world != null && world.IsCreated && world.EntityManager.HasComponent<ShipKinematics>(_shipEntity))
            {
                float3 vel = world.EntityManager.GetComponentData<ShipKinematics>(_shipEntity).Velocity;
                velFlat = new Vector3(vel.x, 0f, vel.z);
            }

            // [TITAN-ORBIT] Zero bank when nearly stationary to avoid jitter at rest.
            if (velFlat.sqrMagnitude < IdleVisualLinearSpeedThreshold * IdleVisualLinearSpeedThreshold
                && Mathf.Abs(signedAngularVelDegPerSec) < IdleBankAngularVelDeadbandDegPerSec)
                signedAngularVelDegPerSec = 0f;

            float globalMaxTurnDegPerSec = ShipPropulsionAggregation.GetGlobalMaxTurnSpeedDegreesPerSecond();
            float targetBankAngle = ShipPropulsionAggregation.ComputeVisualBankTargetAngle(
                signedAngularVelDegPerSec,
                maxBankAngle,
                globalMaxTurnDegPerSec);

            float bankT = 1f - Mathf.Exp(-bankSmoothing * dt);
            _currentBankAngle = Mathf.Lerp(_currentBankAngle, targetBankAngle, bankT);
            _bankPivot.localRotation = Quaternion.Euler(0f, 0f, -_currentBankAngle);
        }

        static float GetPlanarYawDegrees(Quaternion rotation)
        {
            Vector3 fwd = rotation * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-8f)
                return 0f;
            return Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        }
    }
}
