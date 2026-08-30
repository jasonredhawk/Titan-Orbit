using TitanOrbit.Core;
using TitanOrbit.Diagnostics;
using TitanOrbit.Generation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client-only prediction switching for ships (Unity NCE official path).
    /// <para>
    /// Docs: predict the local controller and ghosts it is colliding with; interpolate the rest.
    /// Owner-only remotes (kinematic, interpolation timeline) stopped the 8-step FPS spiral
    /// (e2d7d2) but left Player 2 choppy on ram — predicted hull vs stale interpolated wall,
    /// then snapshot correction. This system predicts at most
    /// <see cref="MaxPredictedRemotes"/> nearest remotes inside <see cref="PredictRadius"/>
    /// (hysteresis <see cref="DropRadius"/>). A 20-ship pile still predicts 1 neighbor, not 20.
    /// Server predicts every hull and remains two-body authority.
    /// </para>
    /// Do not bake OwnerPredicted — that mode cannot be switched on demand.
    /// World: ClientSimulation. Group: GhostSimulationSystemGroup (after receive, before switch apply).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostReceiveSystem))]
    [UpdateBefore(typeof(GhostPredictionSwitchingSystem))]
    public partial struct ShipClientPredictionSwitchSystem : ISystem
    {
        /// <summary>Structural converts per tick so a late-join snapshot does not hitch one frame.</summary>
        public const int MaxSwitchPerFrame = 8;

        /// <summary>Hard cap: local + this many remotes. Scales a scrum to O(1) predicted hulls.</summary>
        public const int MaxPredictedRemotes = 1;

        /// <summary>
        /// Start predicting only at hull overlap (e2d7d2: 24u switched at ~19u and both
        /// clients predicted the other ship while still flying — stutter on both).
        /// Contact logs sat at nearestDist ≈ 1.1.
        /// </summary>
        public const float PredictRadius = 6f;

        /// <summary>Keep predicting through a short peel-off (hysteresis — Unity docs).</summary>
        public const float DropRadius = 10f;

        /// <summary>Need the official NCE switch queues and an in-game connection.</summary>
        static double s_NextAgentLogRealtime;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GhostPredictionSwitchingQueues>();
            state.RequireForUpdate<NetworkStreamInGame>();
            state.RequireForUpdate<NetworkId>();
        }

        /// <summary>
        /// Keeps the local ship Predicted; predicts the nearest in-range remote; interpolates others.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Join-crash gate ---
            // [TITAN-ORBIT] Same window as ShipPhysicsDriveSystem. Do NOT use
            // ShouldSkipShipEntityQueries — map Instantiates keep GhostSpawnBacklog true after
            // Join Team and would leave every remote Predicted through the MaxSteps=8 catch-up.
            if (ClientJoinSettleCache.ShouldSkipShipSimulation)
                return;

            if (!SystemAPI.TryGetSingleton<NetworkId>(out var networkId))
                return;

            Entity localShip = Entity.Null;
            foreach (var (_, entity) in SystemAPI
                         .Query<RefRO<ShipTag>>()
                         .WithAll<GhostOwnerIsLocal, GhostInstance>()
                         .WithEntityAccess())
            {
                localShip = entity;
                break;
            }

            if (localShip == Entity.Null)
            {
                int localId = networkId.Value;
                foreach (var (owner, entity) in SystemAPI
                             .Query<RefRO<GhostOwner>>()
                             .WithAll<ShipTag, GhostInstance>()
                             .WithEntityAccess())
                {
                    if (owner.ValueRO.NetworkId != localId)
                        continue;
                    localShip = entity;
                    break;
                }
            }

            if (localShip == Entity.Null || !state.EntityManager.HasComponent<LocalTransform>(localShip))
                return;

            float3 localPos = state.EntityManager.GetComponentData<LocalTransform>(localShip).Position;

            // #region agent log
            if (UnityEngine.Time.realtimeSinceStartupAsDouble >= s_NextAgentLogRealtime)
            {
                s_NextAgentLogRealtime = UnityEngine.Time.realtimeSinceStartupAsDouble + 2.0;
                bool localPredicted = state.EntityManager.HasComponent<PredictedGhost>(localShip);
                AgentDebugNdjson.Write(
                    "C",
                    "ShipClientPredictionSwitchSystem.cs:OnUpdate",
                    "prediction switch",
                    "{\"localPredicted\":" + (localPredicted ? "true" : "false") +
                    ",\"localShip\":" + localShip.Index + "}");
            }
            // #endregion
            bool torus = ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH);

            Entity nearest = Entity.Null;
            float nearestDist = float.MaxValue;
            bool nearestPredicted = false;

            foreach (var (transform, entity) in SystemAPI
                         .Query<RefRO<LocalTransform>>()
                         .WithAll<ShipTag, GhostInstance>()
                         .WithEntityAccess())
            {
                if (entity == localShip)
                    continue;
                if (state.EntityManager.HasComponent<SwitchPredictionSmoothing>(entity))
                    continue;

                float dist = torus
                    ? ToroidalMapEcs.ToroidalDistance(localPos, transform.ValueRO.Position, mapW, mapH)
                    : math.distance(new float2(localPos.x, localPos.z),
                        new float2(transform.ValueRO.Position.x, transform.ValueRO.Position.z));
                if (dist >= nearestDist)
                    continue;

                nearest = entity;
                nearestDist = dist;
                nearestPredicted = state.EntityManager.HasComponent<PredictedGhost>(entity);
            }

            bool keepNearestPredicted = nearest != Entity.Null &&
                                        (nearestPredicted
                                            ? nearestDist < DropRadius
                                            : nearestDist < PredictRadius);

            ref var queues = ref SystemAPI.GetSingletonRW<GhostPredictionSwitchingQueues>().ValueRW;
            int budget = MaxSwitchPerFrame;

            // --- Local hull must stay on the prediction timeline ---
            if (!state.EntityManager.HasComponent<PredictedGhost>(localShip))
            {
                if (budget > 0)
                {
                    queues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
                    {
                        TargetEntity = localShip,
                        TransitionDurationSeconds = 0f,
                    });
                    budget--;
                }
            }

            // --- Remotes: nearest in-range stays Predicted (same timeline as local). Others interpolate. ---
            foreach (var (_, entity) in SystemAPI
                         .Query<RefRO<ShipTag>>()
                         .WithAll<GhostInstance>()
                         .WithEntityAccess())
            {
                if (entity == localShip)
                    continue;
                if (state.EntityManager.HasComponent<SwitchPredictionSmoothing>(entity))
                    continue;

                bool isPredicted = state.EntityManager.HasComponent<PredictedGhost>(entity);
                bool wantPredicted = keepNearestPredicted && entity == nearest;
                if (wantPredicted == isPredicted || budget <= 0)
                    continue;

                if (wantPredicted)
                {
                    queues.ConvertToPredictedQueue.Enqueue(new ConvertPredictionEntry
                    {
                        TargetEntity = entity,
                        TransitionDurationSeconds = 0f,
                    });
                }
                else
                {
                    queues.ConvertToInterpolatedQueue.Enqueue(new ConvertPredictionEntry
                    {
                        TargetEntity = entity,
                        TransitionDurationSeconds = 0f,
                    });
                }

                budget--;
            }
        }
    }
}
