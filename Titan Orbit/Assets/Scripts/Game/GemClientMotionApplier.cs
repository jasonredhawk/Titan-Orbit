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
    /// Simple contract:
    /// 1. Server owns <see cref="GemKinematics"/> velocity + authoritative <see cref="LocalTransform"/>.
    /// 2. This component animates the GO on the client from velocity on the XZ plane each frame.
    /// 3. Softly reconciles toward the ghosted LocalTransform so pickup stays honest — except
    ///    during a short post-handoff “burst coast” where reconcile + kinematics overwrite used
    ///    to yank gems back toward the asteroid (looked like a mid-flight direction flip).
    /// 4. While a tractor beam is assigned, applies the same pull velocity formula as the server so
    ///    gems glide as soon as the beam is visible (not only after sparse Velocity snapshots).
    /// 5. Burst gems keep velocity radially away from the asteroid center when a burst origin is known.
    /// </summary>
    public sealed class GemClientMotionApplier : MonoBehaviour
    {
        Entity _entity;
        float3 _logicalPos;
        float3 _velocity;
        float3 _angularVelocity;
        bool _bound;

        /// <summary>
        /// Until this time, soft-reconcile toward the server is off (or tiny) so a local-burst
        /// handoff does not yank the gem back to a delayed ghost spawn sample.
        /// </summary>
        float _burstCoastUntil;

        /// <summary>
        /// True while we should prefer the seeded handoff velocity over sparse ghost kinematics.
        /// Cleared when the coast window ends or a tractor beam takes over.
        /// </summary>
        bool _preferHandoffVelocity;

        /// <summary>
        /// Asteroid logical center for this explosion. When set, LateUpdate clamps velocity to
        /// point away from this point on XZ. Zero = unknown (skip outward clamp).
        /// </summary>
        float3 _burstOriginLogical;

        /// <summary>True when <see cref="_burstOriginLogical"/> was provided by handoff or seed.</summary>
        bool _hasBurstOrigin;

        /// <summary>Binds to the gem ghost and seeds logical pose.</summary>
        /// <param name="entity">Gem ghost entity.</param>
        /// <param name="logicalPosition">Starting logical XZ (may be offset after burst handoff).</param>
        /// <param name="fromLocalBurstHandoff">
        /// True when this GO was just claimed from <see cref="ClientGemBurstPresenter"/> — enables
        /// burst-coast (no snap to ghost spawn) for the first ~0.75s.
        /// </param>
        /// <param name="burstOriginLogical">
        /// Asteroid center in logical space for outward flight. Used only when
        /// <paramref name="hasBurstOrigin"/> is true (so a rock at world origin still works).
        /// </param>
        /// <param name="hasBurstOrigin">True when <paramref name="burstOriginLogical"/> is valid.</param>
        public void Bind(
            Entity entity,
            float3 logicalPosition,
            bool fromLocalBurstHandoff = false,
            float3 burstOriginLogical = default,
            bool hasBurstOrigin = false)
        {
            _entity = entity;
            _logicalPos = logicalPosition;
            _bound = true;
            _preferHandoffVelocity = fromLocalBurstHandoff;
            // [TITAN-ORBIT] Burst gems already sit at explosion offsets; hard lerp to ghost
            // LocalTransform looked like a reconcile pop / reverse toward the rock. Coast longer
            // than one Instantiates lag so the crystal finishes flying outward first.
            _burstCoastUntil = fromLocalBurstHandoff ? Time.time + 0.75f : 0f;

            _hasBurstOrigin = hasBurstOrigin;
            _burstOriginLogical = burstOriginLogical;
        }

        /// <summary>
        /// Clears bind/velocity so a pooled gem can be rented again without chasing a dead entity.
        /// Called from <see cref="GemVisualPool.TryReturn"/>.
        /// </summary>
        public void Unbind()
        {
            _entity = Entity.Null;
            _logicalPos = float3.zero;
            _velocity = float3.zero;
            _angularVelocity = float3.zero;
            _bound = false;
            _burstCoastUntil = 0f;
            _preferHandoffVelocity = false;
            _burstOriginLogical = float3.zero;
            _hasBurstOrigin = false;
        }

        /// <summary>
        /// Seeds launch velocity (from local burst handoff or first ECS kinematics sample).
        /// When a burst origin is known, forces the seed to point away from that center.
        /// </summary>
        /// <param name="velocity">Launch velocity (XZ).</param>
        /// <param name="angularVelocity">Tumble rad/s.</param>
        /// <param name="burstOriginLogical">Optional asteroid logical center.</param>
        /// <param name="hasBurstOrigin">True when <paramref name="burstOriginLogical"/> is valid.</param>
        public void SeedVelocity(
            float3 velocity,
            float3 angularVelocity,
            float3 burstOriginLogical = default,
            bool hasBurstOrigin = false)
        {
            if (hasBurstOrigin)
            {
                _burstOriginLogical = burstOriginLogical;
                _hasBurstOrigin = true;
            }

            _velocity = velocity;
            _angularVelocity = angularVelocity;

            // Infer origin behind the gem along the launch dir when handoff forgot the center.
            if (!_hasBurstOrigin && math.lengthsq(new float3(velocity.x, 0f, velocity.z)) > 1e-6f)
            {
                float3 dir = math.normalizesafe(new float3(velocity.x, 0f, velocity.z), new float3(0f, 0f, 1f));
                // Place origin slightly “inward” so EnsureOutward keeps this launch direction.
                _burstOriginLogical = _logicalPos - dir * 0.25f;
                _hasBurstOrigin = true;
            }

            if (_hasBurstOrigin)
            {
                _velocity = GemExplosionMath.EnsureOutwardBurstVelocity(
                    _logicalPos, _burstOriginLogical, _velocity);
            }
        }

        /// <summary>
        /// [UNITY] LateUpdate after sim/presentation: integrate gem GO from pull or burst velocity.
        /// </summary>
        void LateUpdate()
        {
            if (!_bound || _entity == Entity.Null)
                return;

            // [TITAN-ORBIT] Ship queries inside client tractor pull must not run during Instantiates.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
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
            bool inBurstCoast = Time.time < _burstCoastUntil;

            // --- Tractor pull (presentation) ---
            // [TITAN-ORBIT] Only after deploy (extend + widen). During the line shot the gem stays
            // put so the beam can reach it; then pull matches server timing.
            if (GemTractorBeamClientLogic.TryGetClientPullVelocity(_entity, _logicalPos, out float3 tractorVel))
            {
                _velocity = tractorVel;
                underTractor = true;
                _preferHandoffVelocity = false;
                _burstCoastUntil = 0f;
            }
            else if (!_preferHandoffVelocity && em.HasComponent<GemKinematics>(_entity))
            {
                // --- Pull latest server kinematics when present (ghosted Velocity) ---
                // Skipped during burst coast: sparse / lagged snapshots were overwriting the
                // handoff launch dir and looked like a sudden mid-flight direction change.
                var kin = em.GetComponentData<GemKinematics>(_entity);
                if (math.lengthsq(kin.Velocity) > 0.0001f)
                    _velocity = kin.Velocity;
                if (math.lengthsq(kin.AngularVelocity) > 0.0001f)
                    _angularVelocity = kin.AngularVelocity;
            }

            // Coast window ended — allow ghost kinematics next frames; keep current vel this frame.
            if (_preferHandoffVelocity && !inBurstCoast)
                _preferHandoffVelocity = false;

            var serverLt = em.GetComponentData<LocalTransform>(_entity);

            // --- Animate from velocity (client presentation) ---
            // Skip explosion damping while tractoring — damping was fighting pull between snapshots.
            if (!underTractor)
            {
                _velocity = GemExplosionMath.IntegrateLinearVelocity(
                    _velocity, settings.LinearDamping, settings.StopSpeedThreshold, dt);

                // Always explode away from the asteroid center when we know it.
                if (_hasBurstOrigin && math.lengthsq(_velocity) > 1e-8f)
                {
                    _velocity = GemExplosionMath.EnsureOutwardBurstVelocity(
                        _logicalPos, _burstOriginLogical, _velocity);
                }
            }

            _angularVelocity = GemExplosionMath.IntegrateAngularVelocity(
                _angularVelocity, settings.AngularDamping, dt);

            _logicalPos += _velocity * dt;

            // Soft reconcile toward server pose so we do not drift forever from authority.
            // [TITAN-ORBIT] During burst coast, skip pose blend entirely. Ghost LocalTransform is
            // often still near spawn while the local GO already flew out — lerping pulled gems
            // back toward (or through) the asteroid while velocity still said “outward.”
            if (!inBurstCoast || underTractor)
            {
                bool allowPoseBlend = true;
                if (!underTractor && _hasBurstOrigin)
                {
                    // Never blend toward a server sample that is closer to the rock than we are —
                    // that is the exact “fly out then get yanked back” glitch.
                    float clientRadSq = math.lengthsq(new float3(
                        _logicalPos.x - _burstOriginLogical.x,
                        0f,
                        _logicalPos.z - _burstOriginLogical.z));
                    float serverRadSq = math.lengthsq(new float3(
                        serverLt.Position.x - _burstOriginLogical.x,
                        0f,
                        serverLt.Position.z - _burstOriginLogical.z));
                    if (serverRadSq + 0.05f < clientRadSq)
                        allowPoseBlend = false;
                }

                if (allowPoseBlend)
                {
                    float correctRate = underTractor ? 3f : 6f;
                    float correct = 1f - math.exp(-correctRate * dt);
                    _logicalPos = math.lerp(_logicalPos, serverLt.Position, correct);
                }

                // Re-assert outward so a sideways blend cannot leave inward residual motion.
                if (!underTractor && _hasBurstOrigin && math.lengthsq(_velocity) > 1e-8f)
                {
                    _velocity = GemExplosionMath.EnsureOutwardBurstVelocity(
                        _logicalPos, _burstOriginLogical, _velocity);
                }
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
            else if (!inBurstCoast || underTractor)
            {
                float correctRate = underTractor ? 3f : 6f;
                float correct = 1f - math.exp(-correctRate * dt);
                transform.rotation = Quaternion.Slerp(transform.rotation, serverLt.Rotation, correct);
            }
        }
    }
}
