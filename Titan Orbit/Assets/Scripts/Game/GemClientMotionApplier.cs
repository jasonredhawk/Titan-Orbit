using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Client gem GameObject presenter. One job: put the crystal on the interpolated
    /// ghost pose so the player sees the same gem the server will scoop.
    /// <para>
    /// Contract (no client invent):
    /// 1. The GO is created only after the gem ghost Instantiates.
    /// 2. Pose authority is ghosted <see cref="LocalTransform"/> after NetCode interpolation.
    /// 3. We do <b>not</b> integrate <see cref="GemKinematics"/> ourselves, and we do not invent
    ///    tractor pull. A second integrator put the crystal up to 8 units away from the
    ///    collectable ghost — beams latched onto that lie, and flying over it never consumed.
    /// </para>
    /// [TITAN-ORBIT] Ships use presentation pose after NetCode smoothing (ship-simulation rule).
    /// Gems are interpolated ghosts, so client <c>LocalTransform</c> already <b>is</b> that pose.
    /// One smoothing owner — copy it, then retile for the torus display.
    /// </summary>
    public sealed class GemClientMotionApplier : MonoBehaviour
    {
        Entity _entity;
        float3 _logicalPos;
        bool _bound;

        /// <summary>
        /// Binds this GO to a gem ghost that has already Instantiated.
        /// Starts presentation at the ghost's current interpolated pose.
        /// </summary>
        /// <param name="entity">Instantiated gem ghost entity.</param>
        /// <param name="logicalPosition">Ghost <see cref="LocalTransform.Position"/> at bind time.</param>
        public void Bind(Entity entity, float3 logicalPosition)
        {
            _entity = entity;
            _logicalPos = logicalPosition;
            _bound = true;
        }

        /// <summary>
        /// Interpolated logical XZ pose this frame (ghost <c>LocalTransform</c>).
        /// Same unbounded space as ECS pickup — safe for toroidal reach tests if a caller needs it.
        /// </summary>
        /// <param name="logicalPos">Unbounded sim-space position.</param>
        /// <returns>False when this shell is unbound (pooled / not yet Bind'd).</returns>
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
        /// [LEGACY] No-op. Callers used to seed a client-side coast integrator. Pose is
        /// interpolated <c>LocalTransform</c> now — velocity is not applied on the GO.
        /// </summary>
        public void SeedVelocity(float3 velocity, float3 angularVelocity)
        {
            _ = velocity;
            _ = angularVelocity;
        }

        /// <summary>
        /// [UNITY] LateUpdate: copy interpolated ghost pose onto the hybrid GO, then retile
        /// for the local ship's map tile.
        /// </summary>
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

            // --- Authoritative interpolated pose ---
            // [NETCODE] Gems are Interpolated ghosts. After GhostUpdate, LocalTransform is the
            // buffered past pose — the collectable position, delayed by interpolation.
            // [TITAN-ORBIT] Do not lerp or integrate on top (double-smooth / second sim).
            var serverLt = em.GetComponentData<LocalTransform>(_entity);
            _logicalPos = serverLt.Position;

            // --- Toroidal display (ship reference) ---
            // [TITAN-ORBIT] Latch rolled map size before retile. Missing size → skip display
            // (never invent 1000 — wrap-tile gems would land on the wrong copy).
            if (!ToroidalDisplay.ResolveMapSize(default, out _, out _))
                return;

            if (!ToroidalDisplay.TryGetReferencePosition(out var reference))
                return;

            Vector3 displayPos = ToroidalDisplay.ToDisplayPositionWithHysteresis(
                _entity, _logicalPos, reference);
            transform.SetPositionAndRotation(displayPos, serverLt.Rotation);
        }
    }
}
