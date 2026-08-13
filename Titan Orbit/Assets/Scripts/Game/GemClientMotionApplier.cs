using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Client gem GameObject presenter. Puts the crystal at <b>estimated server-now</b>
    /// so the mesh you fly over is the gem <c>GemPickupSystem</c> will scoop.
    /// <para>
    /// NetCode interpolated <c>LocalTransform</c> is the recent <em>past</em> (interpolation
    /// delay). For remote ships that is correct (pillar 2). For pickups it is wrong: the player
    /// overlaps yesterday's pose and the server gem has already moved. We start from that
    /// interpolated sample, then advance it by ghosted <see cref="GemKinematics.Velocity"/> ×
    /// the interpolation delay — the same velocity the server already applied. Cap the delay
    /// so a starved snapshot cannot throw the crystal across the map.
    /// </para>
    /// When the gem is idle (velocity ≈ 0) we copy interpolated pose as-is.
    /// Tractor pull is server-authored; once snapshots include the pull, velocity points at
    /// the wing and this extrapolation shows the gem coming in.
    /// </summary>
    public sealed class GemClientMotionApplier : MonoBehaviour
    {
        Entity _entity;
        float3 _logicalPos;
        bool _bound;

        /// <summary>Frame stamp for the shared interpolation-delay cache.</summary>
        static int s_delayFrame = -1;

        /// <summary>Seconds from InterpolationTick to ServerTick this frame (clamped).</summary>
        static float s_cachedDelaySeconds;

        /// <summary>
        /// Hard cap on how far we may lead the interpolated sample. 250 ms is well above a
        /// healthy interpolation buffer and well below a stale-snapshot runaway.
        /// </summary>
        const float MaxExtrapolationSeconds = 0.25f;

        /// <summary>
        /// Binds this GO to a gem ghost that has already Instantiated.
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

        /// <summary>
        /// [UNITY] LateUpdate: interpolated ghost pose, plus a short velocity lead to server-now,
        /// then toroidal display retile.
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

            // --- Interpolated sample (NetCode past) ---
            var serverLt = em.GetComponentData<LocalTransform>(_entity);
            float3 present = serverLt.Position;

            // --- Lead to estimated server-now ---
            // [NETCODE] InterpolationTick is what LocalTransform currently shows.
            // ServerTick is "now" on the server timeline. Velocity is ghosted from GemMotionSystem.
            if (em.HasComponent<GemKinematics>(_entity))
            {
                float3 vel = em.GetComponentData<GemKinematics>(_entity).Velocity;
                vel.y = 0f;
                float delay = GetInterpolationDelaySeconds(em);
                if (math.lengthsq(vel) > 0.0001f && delay > 0.0001f)
                    present += vel * delay;
            }

            _logicalPos = present;

            // --- Toroidal display ---
            if (!ToroidalDisplay.ResolveMapSize(default, out _, out _))
                return;
            if (!ToroidalDisplay.TryGetReferencePosition(out var reference))
                return;

            Vector3 displayPos = ToroidalDisplay.ToDisplayPositionWithHysteresis(
                _entity, _logicalPos, reference);
            transform.SetPositionAndRotation(displayPos, serverLt.Rotation);
        }

        /// <summary>
        /// Seconds between the interpolated tick and the server tick, computed once per frame.
        /// </summary>
        static float GetInterpolationDelaySeconds(EntityManager em)
        {
            if (Time.frameCount == s_delayFrame)
                return s_cachedDelaySeconds;

            s_delayFrame = Time.frameCount;
            s_cachedDelaySeconds = 0f;

            using var timeQuery = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>());
            if (timeQuery.IsEmptyIgnoreFilter)
                return 0f;

            var networkTime = timeQuery.GetSingleton<NetworkTime>();
            if (!networkTime.ServerTick.IsValid || !networkTime.InterpolationTick.IsValid)
                return 0f;

            int hz = PlanetGemMoonOrbitClock.FallbackSimulationHz;
            using var rateQuery = em.CreateEntityQuery(ComponentType.ReadOnly<ClientServerTickRate>());
            if (!rateQuery.IsEmptyIgnoreFilter)
                hz = math.max(1, rateQuery.GetSingleton<ClientServerTickRate>().SimulationTickRate);

            int ticks = networkTime.ServerTick.TicksSince(networkTime.InterpolationTick);
            float frac = networkTime.ServerTickFraction - networkTime.InterpolationTickFraction;
            float seconds = (ticks + frac) / hz;
            s_cachedDelaySeconds = math.clamp(seconds, 0f, MaxExtrapolationSeconds);
            return s_cachedDelaySeconds;
        }
    }
}
