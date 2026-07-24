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
    /// [HYBRID] Client gem GameObject presenter driven only by <b>server ghost data</b>.
    /// <para>
    /// Contract (no client invent):
    /// 1. The gem GO is created only after the gem ghost Instantiates
    ///    (<see cref="EcsWorldVisualizer"/> / urgent Instantiates queue).
    /// 2. Pose authority is the ghosted <see cref="LocalTransform"/>.
    /// 3. Flight uses ghosted <see cref="GemKinematics.Velocity"/> / AngularVelocity — the same
    ///    fields the server writes in <see cref="GemMotionSystem"/>.
    /// 4. When LT samples advance, we blend toward them. When LT is stale during coast, we
    ///    extrapolate with the latest ghost Velocity (no second invented burst, no extra damping —
    ///    server already damped the ghosted Velocity).
    /// </para>
    /// Why not “copy LT only”: if LT snapshots lag during coast, a GO stuck on the spawn sample
    /// looks invisible until the idle pose finally arrives. Velocity is ghosted so the client can
    /// present the server flight without inventing a local burst.
    /// </summary>
    public sealed class GemClientMotionApplier : MonoBehaviour
    {
        Entity _entity;
        float3 _logicalPos;
        float3 _velocity;
        float3 _angularVelocity;
        float3 _lastGhostPos;
        bool _bound;
        bool _hasGhostSample;

        /// <summary>
        /// Binds this GO to a gem ghost that has already Instantiated.
        /// Starts presentation at the ghost's current logical pose.
        /// </summary>
        /// <param name="entity">Instantiated gem ghost entity.</param>
        /// <param name="logicalPosition">Ghost <see cref="LocalTransform.Position"/> at bind time.</param>
        public void Bind(Entity entity, float3 logicalPosition)
        {
            _entity = entity;
            _logicalPos = logicalPosition;
            _lastGhostPos = logicalPosition;
            _hasGhostSample = true;
            _bound = true;
            _velocity = float3.zero;
            _angularVelocity = float3.zero;
        }

        /// <summary>
        /// Clears bind/velocity so a pooled gem can be rented again without chasing a dead entity.
        /// Called from <see cref="GemVisualPool.TryReturn"/>.
        /// </summary>
        public void Unbind()
        {
            _entity = Entity.Null;
            _logicalPos = float3.zero;
            _lastGhostPos = float3.zero;
            _velocity = float3.zero;
            _angularVelocity = float3.zero;
            _bound = false;
            _hasGhostSample = false;
        }

        /// <summary>
        /// Seeds presentation velocity from the ghost's current <see cref="GemKinematics"/>
        /// (call at proxy create). Pass server values only — do not invent a launch direction.
        /// </summary>
        /// <param name="velocity">Ghosted linear velocity (XZ).</param>
        /// <param name="angularVelocity">Ghosted tumble (rad/s).</param>
        public void SeedVelocity(float3 velocity, float3 angularVelocity)
        {
            _velocity = new float3(velocity.x, 0f, velocity.z);
            _angularVelocity = angularVelocity;
        }

        /// <summary>
        /// [UNITY] LateUpdate: present ghost pose + kinematics on the hybrid GO.
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

            float dt = math.min(Time.deltaTime, 0.05f);
            var settings = GemExplosionSettingsCache.ResolveOrDefault();
            bool underTractor = false;

            // --- Authoritative ghost pose sample ---
            var serverLt = em.GetComponentData<LocalTransform>(_entity);
            float3 ghostPos = serverLt.Position;
            bool ghostPoseAdvanced = !_hasGhostSample ||
                                     math.distancesq(ghostPos, _lastGhostPos) > 1e-8f;
            _lastGhostPos = ghostPos;
            _hasGhostSample = true;

            // --- Ghost kinematics (server Velocity / AngularVelocity) ---
            bool adoptedFreshGhostVelocity = false;
            if (em.HasComponent<GemKinematics>(_entity))
            {
                var kin = em.GetComponentData<GemKinematics>(_entity);
                float3 ghostVel = new float3(kin.Velocity.x, 0f, kin.Velocity.z);
                // Replace local coast state whenever the ghost sample differs (new server tick).
                if (math.distancesq(ghostVel, _velocity) > 1e-8f ||
                    math.distancesq(kin.AngularVelocity, _angularVelocity) > 1e-8f)
                {
                    _velocity = ghostVel;
                    _angularVelocity = kin.AngularVelocity;
                    adoptedFreshGhostVelocity = true;
                }
            }

            // --- Optional tractor pull from ghost lock only ---
            // [TITAN-ORBIT] May resolve a ship by NetworkId — skip during Instantiates storms.
            if (!ClientJoinSettleCache.ShouldSkipShipEntityQueries &&
                em.HasComponent<GemMotionState>(_entity))
            {
                var motion = em.GetComponentData<GemMotionState>(_entity);
                if (GemTractorBeamClientLogic.TryGetPullVelocityFromGhostLock(
                        _entity, motion, _logicalPos, out float3 tractorVel))
                {
                    _velocity = tractorVel;
                    underTractor = true;
                    adoptedFreshGhostVelocity = true;
                }
            }

            bool nearlyStopped = math.lengthsq(_velocity) <
                                 settings.StopSpeedThreshold * settings.StopSpeedThreshold;

            if (underTractor || ghostPoseAdvanced || nearlyStopped)
            {
                // --- Trust / blend to the latest ghost LocalTransform ---
                float correctRate = underTractor ? 3f : (nearlyStopped ? 14f : 10f);
                float correct = 1f - math.exp(-correctRate * dt);
                _logicalPos = math.lerp(_logicalPos, ghostPos, correct);
            }
            else
            {
                // --- LT sample unchanged while still flying: coast with ghost Velocity ---
                // If Velocity snapshots are also sparse, apply the same damping step as the server
                // so we do not fly forever on a stale seed. When a fresh ghost Velocity arrives,
                // skip damping this frame (server already applied it for that sample).
                if (!adoptedFreshGhostVelocity)
                {
                    _velocity = GemExplosionMath.IntegrateLinearVelocity(
                        _velocity, settings.LinearDamping, settings.StopSpeedThreshold, dt);
                    _angularVelocity = GemExplosionMath.IntegrateAngularVelocity(
                        _angularVelocity, settings.AngularDamping, dt);
                }

                _logicalPos += _velocity * dt;
            }

            // --- Toroidal display (ship reference) — same as other world bodies ---
            Vector3 displayPos;
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
            else if (underTractor || ghostPoseAdvanced || nearlyStopped)
            {
                float correctRate = nearlyStopped ? 14f : 10f;
                float correct = 1f - math.exp(-correctRate * dt);
                transform.rotation = Quaternion.Slerp(transform.rotation, serverLt.Rotation, correct);
            }
        }
    }
}
