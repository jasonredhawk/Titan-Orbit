using TitanOrbit.Diagnostics;
using TitanOrbit.ECS;
using TitanOrbit.Shared;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Publishes NetCode presentation-phase ship poses once per frame for camera, hybrid leftovers,
    /// and parallax. [NETCODE] <see cref="LocalTransform"/> here is already predicted (local) or
    /// interpolated (remotes) — we do <b>not</b> add a second soft-follow layer.
    /// World: ClientSimulation. Group: PresentationSystemGroup (OrderLast).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup), OrderLast = true)]
    public partial class ShipVisualSyncSystem : SystemBase
    {
        // #region agent log
        float3 _dbgLastDisplayPos;
        bool _dbgHasDisplayPos;
        float2 _dbgLastMoveXz;
        bool _dbgHasMoveXz;
        uint _dbgLastServerTick;
        float3 _dbgPosAtTickBoundary;
        bool _dbgHasTickPos;
        int _dbgFrameLogBudget;
        // #endregion

        protected override void OnUpdate()
        {
            // --- Frame stamp for LateUpdate readers ---
            GhostPresentationTransformCache.BeginPublish(UnityEngine.Time.frameCount);

            // --- All ship ghosts (presentation LocalTransform after NetCode) ---
            foreach (var (lt, entity) in SystemAPI
                         .Query<RefRO<LocalTransform>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                PublishShip(entity, lt.ValueRO);
            }

            // --- People transports (same presentation phase) ---
            foreach (var (lt, entity) in SystemAPI
                         .Query<RefRO<LocalTransform>>()
                         .WithAll<PeopleTransportTag>()
                         .WithEntityAccess())
            {
                GhostPresentationTransformCache.PublishPeopleTransport(entity, ToSnapshot(lt.ValueRO));
            }

            // --- Local owner → ShipDisplayPose for camera / space background ---
            PublishLocalShipDisplayPose();

            // #region agent log
            AgentLogLocalShipPresentation();
            // #endregion
        }

        // #region agent log
        /// <summary>Debug NDJSON: display frame steps vs sim tick steps (session 6b87b4).</summary>
        void AgentLogLocalShipPresentation()
        {
            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(World, out var localShip))
                return;
            if (!EntityManager.HasComponent<LocalTransform>(localShip))
                return;

            var lt = EntityManager.GetComponentData<LocalTransform>(localShip);
            float3 pos = lt.Position;

            float displayDelta = 0f;
            float2 moveXz = float2.zero;
            bool reversed = false;
            if (_dbgHasDisplayPos)
            {
                float3 delta3 = pos - _dbgLastDisplayPos;
                displayDelta = math.length(delta3);
                moveXz = new float2(delta3.x, delta3.z);
                // H9: consecutive planar moves pointing opposite = rubber-band (forward then back).
                if (_dbgHasMoveXz && math.lengthsq(moveXz) > 0.01f && math.lengthsq(_dbgLastMoveXz) > 0.01f)
                    reversed = math.dot(math.normalize(moveXz), math.normalize(_dbgLastMoveXz)) < -0.3f;
                if (math.lengthsq(moveXz) > 0.0001f)
                {
                    _dbgLastMoveXz = moveXz;
                    _dbgHasMoveXz = true;
                }
            }
            _dbgLastDisplayPos = pos;
            _dbgHasDisplayPos = true;

            uint serverTick = 0;
            float tickFrac = 1f;
            bool isPartial = false;
            int batchSize = 0;
            int maxBatch = 0;
            int maxSteps = 0;
            float speed = 0f;
            float commandAge = 0f;
            float estimatedRtt = 0f;
            int numPredicted = 0;
            bool hasServer = ClientServerBootstrap.HasServerWorld;
            if (SystemAPI.TryGetSingleton<NetworkTime>(out var nt))
            {
                serverTick = nt.ServerTick.IsValid ? nt.ServerTick.TickIndexForValidTick : 0u;
                tickFrac = nt.ServerTickFraction;
                isPartial = nt.IsPartialTick;
                batchSize = nt.SimulationStepBatchSize;
                numPredicted = nt.NumPredictedTicksExpected;
            }

            if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var csr))
            {
                maxBatch = csr.MaxSimulationStepBatchSize;
                maxSteps = csr.MaxSimulationStepsPerFrame;
            }

            if (SystemAPI.TryGetSingleton<NetworkSnapshotAck>(out var ack))
            {
                commandAge = ack.ServerCommandAge / 256f;
                estimatedRtt = ack.EstimatedRTT;
            }

            if (EntityManager.HasComponent<ShipKinematics>(localShip))
                speed = math.length(EntityManager.GetComponentData<ShipKinematics>(localShip).Velocity);

            float simStepDelta = -1f;
            if (_dbgHasTickPos && serverTick != 0 && serverTick != _dbgLastServerTick)
            {
                simStepDelta = math.distance(pos, _dbgPosAtTickBoundary);
                _dbgPosAtTickBoundary = pos;
                _dbgLastServerTick = serverTick;
            }
            else if (!_dbgHasTickPos && serverTick != 0)
            {
                _dbgPosAtTickBoundary = pos;
                _dbgLastServerTick = serverTick;
                _dbgHasTickPos = true;
            }

            if (_dbgFrameLogBudget <= 0)
                _dbgFrameLogBudget = 120;
            bool interesting = reversed || displayDelta > 0.5f || simStepDelta > 0.5f ||
                               (speed > 1f && displayDelta > 0.0001f);
            if (!interesting || _dbgFrameLogBudget-- <= 0)
                return;

            string data =
                "{\"displayDelta\":" + displayDelta.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"simStepDelta\":" + simStepDelta.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"speed\":" + speed.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"reversed\":" + (reversed ? "true" : "false") +
                ",\"serverTick\":" + serverTick +
                ",\"tickFrac\":" + tickFrac.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"isPartial\":" + (isPartial ? "true" : "false") +
                ",\"simBatch\":" + batchSize +
                ",\"maxBatch\":" + maxBatch +
                ",\"maxSteps\":" + maxSteps +
                ",\"cmdAge\":" + commandAge.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"rttMs\":" + estimatedRtt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"numPred\":" + numPredicted +
                ",\"hasServer\":" + (hasServer ? "true" : "false") +
                ",\"frame\":" + UnityEngine.Time.frameCount +
                ",\"dt\":" + UnityEngine.Time.deltaTime.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "}";

            string hyp = reversed ? "H9" : (simStepDelta > 1.0f ? "H6" : "H5");
            ShipFlightSmoothDebugLog.Write(hyp, "ShipVisualSyncSystem.OnUpdate", "local ship presentation sample", data);
        }
        // #endregion

        /// <summary>
        /// Copies the local ship's presentation transform into <see cref="ShipDisplayPose"/>.
        /// </summary>
        void PublishLocalShipDisplayPose()
        {
            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(World, out var localShip))
            {
                ShipDisplayPose.ClearLocalPose();
                return;
            }

            // Prefer the just-published presentation cache (same LocalTransform we wrote above).
            if (GhostPresentationTransformCache.TryGetShip(localShip, out var snapshot))
            {
                ShipDisplayPose.SetLocalPose(
                    (Vector3)snapshot.Position,
                    (Quaternion)snapshot.Rotation);
                return;
            }

            if (!EntityManager.HasComponent<LocalTransform>(localShip))
                return;

            var lt = EntityManager.GetComponentData<LocalTransform>(localShip);
            ShipDisplayPose.SetLocalPose((Vector3)lt.Position, (Quaternion)lt.Rotation);
        }

        static void PublishShip(Entity entity, in LocalTransform transform) =>
            GhostPresentationTransformCache.PublishShip(entity, ToSnapshot(transform));

        static GhostPresentationTransformCache.Snapshot ToSnapshot(in LocalTransform transform) =>
            new GhostPresentationTransformCache.Snapshot
            {
                Position = transform.Position,
                Rotation = transform.Rotation,
                Scale = transform.Scale,
            };
    }
}
