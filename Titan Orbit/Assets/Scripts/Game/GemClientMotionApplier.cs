using TitanOrbit.ECS;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Client gem GameObject presenter — <b>ghost pose only</b>.
    /// <para>
    /// Server owns kinematics + LocalTransform. This component copies the interpolated ghost
    /// LocalTransform (and tumble from ghost AngularVelocity). It does <b>not</b>:
    /// integrate a second velocity, soft-reconcile, invent tractor pull, or seed from local VFX.
    /// That dual path was the mid-flight direction flip.
    /// </para>
    /// Between sparse snapshots, a short velocity extrapolation (≤33 ms) uses only ghosted
    /// <see cref="GemKinematics.Velocity"/> — never a client-recomputed tractor direction.
    /// </summary>
    public sealed class GemClientMotionApplier : MonoBehaviour
    {
        /// <summary>Max seconds beyond last ghost pose sample (~1 frame at 30 Hz MaxSendRate).</summary>
        const float MaxExtrapolateSeconds = 0.033f;

        Entity _entity;
        bool _bound;
        float3 _lastGhostPos;
        float3 _velocity;
        float3 _angularVelocity;
        float _ghostPoseSampleTime;

        /// <summary>Binds this GO to a gem ghost. Pose follows the ghost from the next LateUpdate.</summary>
        public void Bind(Entity entity, float3 logicalPosition)
        {
            _entity = entity;
            _bound = true;
            _lastGhostPos = logicalPosition;
            _velocity = float3.zero;
            _angularVelocity = float3.zero;
            _ghostPoseSampleTime = Time.time;
        }

        /// <summary>Clears bind so a pooled gem can be rented again.</summary>
        public void Unbind()
        {
            _entity = Entity.Null;
            _lastGhostPos = float3.zero;
            _velocity = float3.zero;
            _angularVelocity = float3.zero;
            _bound = false;
            _ghostPoseSampleTime = 0f;
        }

        /// <summary>
        /// Optional seed from ghost kinematics at proxy create. Not used for local-burst handoff.
        /// </summary>
        public void SeedVelocity(float3 velocity, float3 angularVelocity)
        {
            _velocity = new float3(velocity.x, 0f, velocity.z);
            _angularVelocity = angularVelocity;
        }

        /// <summary>[UNITY] LateUpdate: copy ghost pose (+ brief Velocity extrapolate) to the GO.</summary>
        void LateUpdate()
        {
            if (!_bound || _entity == Entity.Null)
                return;

            // No ship queries here anymore (tractor rewrite removed) — still skip during Instantiates
            // storms so we do not touch EntityManager while GhostSpawn is busy if other systems race.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            if (!em.Exists(_entity) || !em.HasComponent<LocalTransform>(_entity))
                return;

            float now = Time.time;
            float dt = math.min(Time.deltaTime, 0.05f);

            // --- Authoritative interpolated pose ---
            var serverLt = em.GetComponentData<LocalTransform>(_entity);
            float3 ghostPos = serverLt.Position;

            if (math.distancesq(ghostPos, _lastGhostPos) > 1e-8f)
                _ghostPoseSampleTime = now;
            _lastGhostPos = ghostPos;

            // --- Ghost kinematics only (no tractor rewrite — that disagreed with Velocity snapshots) ---
            if (em.HasComponent<GemKinematics>(_entity))
            {
                var kin = em.GetComponentData<GemKinematics>(_entity);
                _velocity = kin.Velocity;
                _angularVelocity = kin.AngularVelocity;
            }

            float age = math.max(0f, now - _ghostPoseSampleTime);
            float extrap = math.min(age, MaxExtrapolateSeconds);
            float3 presentLogical = _lastGhostPos + _velocity * extrap;

            Vector3 displayPos;
            if (ToroidalDisplay.TryGetReferencePosition(out var reference))
                displayPos = ToroidalDisplay.ToDisplayPositionWithHysteresis(_entity, presentLogical, reference);
            else
                displayPos = new Vector3(presentLogical.x, presentLogical.y, presentLogical.z);

            transform.position = displayPos;

            if (math.lengthsq(_angularVelocity) > 0.0001f)
            {
                float angle = math.length(_angularVelocity) * dt;
                float3 axis = math.normalizesafe(_angularVelocity, new float3(0f, 1f, 0f));
                transform.rotation = math.mul(quaternion.AxisAngle(axis, angle), (quaternion)transform.rotation);
            }
            else
            {
                transform.rotation = serverLt.Rotation;
            }
        }
    }
}
