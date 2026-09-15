using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client gem GameObject presenter. Event-hydrated gems already integrate on the client
    /// sim tick — this copies <see cref="LocalTransform"/> and applies toroidal display.
    /// </summary>
    public sealed class GemClientMotionApplier : MonoBehaviour
    {
        int _bindSerial;
        Entity _entity;
        float3 _logicalPos;
        bool _bound;

        /// <summary>
        /// Increments on each <see cref="Bind"/>. Collect-hide must not revive a pooled shell
        /// that was rebound to a different gem.
        /// </summary>
        public int BindSerial => _bindSerial;

        /// <summary>Local gem entity this shell is currently posing; <see cref="Entity.Null"/> when unbound.</summary>
        public Entity BoundEntity => _entity;

        /// <summary>
        /// Binds this GO to a hydrated gem entity that has already Instantiated.
        /// </summary>
        /// <param name="entity">Client gem entity.</param>
        /// <param name="logicalPosition"><see cref="LocalTransform.Position"/> at bind time.</param>
        public void Bind(Entity entity, float3 logicalPosition)
        {
            _bindSerial++;
            _entity = entity;
            _logicalPos = logicalPosition;
            _bound = true;
        }

        /// <summary>
        /// Estimated server-now logical XZ pose (interpolated LT + velocity × delay).
        /// Same unbounded space as ECS pickup.
        /// </summary>
        public bool TryGetLogicalPosition(out float3 logicalPos)
        {
            logicalPos = _logicalPos;
            return _bound && _entity != Entity.Null;
        }

        /// <summary>
        /// Clears bind so a pooled gem can be rented again without chasing a dead entity.
        /// Called from <see cref="GemVisualPool.TryReturn"/>.
        /// </summary>
        public void Unbind()
        {
            _entity = Entity.Null;
            _logicalPos = float3.zero;
            _bound = false;
        }

        /// <summary>
        /// [LEGACY] No-op kept so older visualizer call sites compile. Pose comes from
        /// interpolated LT + ghosted velocity, not a seeded local integrator.
        /// </summary>
        public void SeedVelocity(float3 velocity, float3 angularVelocity)
        {
            _ = velocity;
            _ = angularVelocity;
        }

        /// <summary>LateUpdate: copy the local sim pose, then toroidal display retile.</summary>
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

            var lt = em.GetComponentData<LocalTransform>(_entity);
            _logicalPos = lt.Position;

            if (!ToroidalDisplay.ResolveMapSize(default, out _, out _))
                return;
            if (!ToroidalDisplay.TryGetReferencePosition(out var reference))
                return;

            Vector3 displayPos = ToroidalDisplay.ToDisplayPositionWithHysteresis(
                _entity, _logicalPos, reference);
            transform.SetPositionAndRotation(displayPos, lt.Rotation);
        }
    }
}
