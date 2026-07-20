using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Client-side gem animation driver on the hybrid GameObject proxy.
    /// <para>
    /// Why this exists: NetCode gem ghosts were Static-optimized at MaxSendRate 2, and
    /// <see cref="GemMotionSystem"/> is server-only — so the GO only snapped rarely, and
    /// toroidal retile when the ship moved looked like the only “motion.”
    /// </para>
    /// Simple contract (what you asked for):
    /// 1. Server owns <see cref="GemKinematics"/> velocity + authoritative <see cref="LocalTransform"/>.
    /// 2. This component animates the GO on the client from velocity on the XZ plane each frame.
    /// 3. Softly reconciles toward the ghosted LocalTransform so pickup stays honest.
    /// </summary>
    public sealed class GemClientMotionApplier : MonoBehaviour
    {
        Entity _entity;
        float3 _logicalPos;
        float3 _velocity;
        float3 _angularVelocity;
        bool _bound;

        /// <summary>Binds to the gem ghost and seeds logical pose.</summary>
        public void Bind(Entity entity, float3 logicalPosition)
        {
            _entity = entity;
            _logicalPos = logicalPosition;
            _bound = true;
        }

        /// <summary>
        /// Seeds launch velocity (from local burst handoff or first ECS kinematics sample).
        /// </summary>
        public void SeedVelocity(float3 velocity, float3 angularVelocity)
        {
            _velocity = velocity;
            _angularVelocity = angularVelocity;
        }

        void LateUpdate()
        {
            if (!_bound || _entity == Entity.Null)
                return;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            if (!em.Exists(_entity) || !em.HasComponent<LocalTransform>(_entity))
                return;

            float dt = math.min(Time.deltaTime, 0.05f);
            var settings = GemExplosionSettingsCache.ResolveOrDefault();

            // --- Pull latest server kinematics when present (ghosted Velocity) ---
            if (em.HasComponent<GemKinematics>(_entity))
            {
                var kin = em.GetComponentData<GemKinematics>(_entity);
                // Prefer non-zero ECS velocity (authoritative / freshly replicated).
                if (math.lengthsq(kin.Velocity) > 0.0001f)
                    _velocity = kin.Velocity;
                if (math.lengthsq(kin.AngularVelocity) > 0.0001f)
                    _angularVelocity = kin.AngularVelocity;
            }

            var serverLt = em.GetComponentData<LocalTransform>(_entity);

            // --- Animate from velocity (client presentation) ---
            _velocity = GemExplosionMath.IntegrateLinearVelocity(
                _velocity, settings.LinearDamping, settings.StopSpeedThreshold, dt);
            _angularVelocity = GemExplosionMath.IntegrateAngularVelocity(
                _angularVelocity, settings.AngularDamping, dt);

            _logicalPos += _velocity * dt;

            // Soft reconcile toward server pose so we do not drift forever from authority.
            float correct = 1f - math.exp(-6f * dt);
            _logicalPos = math.lerp(_logicalPos, serverLt.Position, correct);

            // --- Toroidal display (ship reference) — same as other world bodies ---
            Vector3 displayPos = serverLt.Position;
            if (ToroidalDisplay.TryGetReferencePosition(out var reference))
                displayPos = ToroidalDisplay.ToDisplayPositionWithHysteresis(_entity, _logicalPos, reference);
            else
                displayPos = new Vector3(_logicalPos.x, _logicalPos.y, _logicalPos.z);

            transform.position = displayPos;

            if (math.lengthsq(_angularVelocity) > 0.0001f)
            {
                float angle = math.length(_angularVelocity) * dt;
                float3 axis = math.normalizesafe(_angularVelocity, new float3(0f, 1f, 0f));
                transform.rotation = math.mul(quaternion.AxisAngle(axis, angle), (quaternion)transform.rotation);
            }
            else
            {
                transform.rotation = Quaternion.Slerp(transform.rotation, serverLt.Rotation, correct);
            }
        }
    }
}
