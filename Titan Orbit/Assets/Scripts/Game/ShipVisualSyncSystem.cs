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
        int _dbgMicroReverseWindow;
        int _dbgMicroReverseCount;
        float _dbgDeltaSum;
        float _dbgDeltaMax;
        int _dbgDeltaSamples;
        int _dbgSimBatchMax;
        double _dbgNextAggLog;
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
        /// <summary>
        /// 1 Hz only — basics28 dense AppendAllText made Local Host choppy (FPS collapsed to ~1).
        /// </summary>
        void AgentLogLocalShipPresentation()
        {
            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(World, out var localShip))
                return;
            if (!EntityManager.HasComponent<LocalTransform>(localShip))
                return;

            var lt = EntityManager.GetComponentData<LocalTransform>(localShip);
            float3 pos = lt.Position;

            float displayDelta = 0f;
            bool microReverse = false;
            if (_dbgHasDisplayPos)
            {
                float3 delta3 = pos - _dbgLastDisplayPos;
                displayDelta = math.length(delta3);
                float2 moveXz = new float2(delta3.x, delta3.z);
                // H32: tiny opposing frame deltas — "blurry" jitter (not stepped).
                if (_dbgHasMoveXz && math.lengthsq(moveXz) > 1e-6f && math.lengthsq(_dbgLastMoveXz) > 1e-6f)
                    microReverse = math.dot(math.normalize(moveXz), math.normalize(_dbgLastMoveXz)) < -0.2f;
                if (math.lengthsq(moveXz) > 1e-6f)
                {
                    _dbgLastMoveXz = moveXz;
                    _dbgHasMoveXz = true;
                }
            }
            _dbgLastDisplayPos = pos;
            _dbgHasDisplayPos = true;

            int batchSize = 0;
            float speed = 0f;
            float commandAge = 0f;
            float estimatedRtt = 0f;
            int numPredicted = 0;
            uint targetSlack = 0;
            uint lastSnapLocal = 0;
            uint predictTarget = 0;
            bool hasServer = ClientServerBootstrap.HasServerWorld;
            if (SystemAPI.TryGetSingleton<NetworkTime>(out var nt))
            {
                batchSize = nt.SimulationStepBatchSize;
                numPredicted = nt.NumPredictedTicksExpected;
            }

            if (SystemAPI.TryGetSingleton<ClientTickRate>(out var ctr))
                targetSlack = ctr.TargetCommandSlack;

            if (SystemAPI.TryGetSingleton<NetworkSnapshotAck>(out var ack))
            {
                commandAge = ack.ServerCommandAge / 256f;
                estimatedRtt = ack.EstimatedRTT;
                if (ack.LastReceivedSnapshotByLocal.IsValid)
                    lastSnapLocal = ack.LastReceivedSnapshotByLocal.TickIndexForValidTick;
            }

            if (SystemAPI.TryGetSingleton<NetworkTimeSystemData>(out var ntsd) && ntsd.predictTargetTick.IsValid)
                predictTarget = ntsd.predictTargetTick.TickIndexForValidTick;

            if (EntityManager.HasComponent<ShipKinematics>(localShip))
                speed = math.length(EntityManager.GetComponentData<ShipKinematics>(localShip).Velocity);

            _dbgMicroReverseWindow++;
            _dbgDeltaSum += displayDelta;
            if (displayDelta > _dbgDeltaMax)
                _dbgDeltaMax = displayDelta;
            _dbgDeltaSamples++;
            if (batchSize > _dbgSimBatchMax)
                _dbgSimBatchMax = batchSize;
            if (microReverse)
                _dbgMicroReverseCount++;

            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (now < _dbgNextAggLog || _dbgDeltaSamples <= 0)
                return;

            _dbgNextAggLog = now + 1.0;
            float avgDelta = _dbgDeltaSum / _dbgDeltaSamples;
            float fps = UnityEngine.Time.unscaledDeltaTime > 1e-6f
                ? 1f / UnityEngine.Time.unscaledDeltaTime
                : 0f;
            string agg =
                "{\"hasServer\":" + (hasServer ? "true" : "false") +
                ",\"microRev\":" + _dbgMicroReverseCount +
                ",\"frames\":" + _dbgMicroReverseWindow +
                ",\"avgDelta\":" + avgDelta.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"maxDelta\":" + _dbgDeltaMax.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"cmdAge\":" + commandAge.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"targetSlack\":" + targetSlack +
                ",\"predictLead\":" + (predictTarget > 0 && lastSnapLocal > 0
                    ? ((int)predictTarget - (int)lastSnapLocal).ToString()
                    : "na") +
                ",\"rttMs\":" + estimatedRtt.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"numPred\":" + numPredicted +
                ",\"simBatchMax\":" + _dbgSimBatchMax +
                ",\"speed\":" + speed.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"fps\":" + fps.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "}";
            ShipFlightSmoothDebugLog.Write("H32", "ShipVisualSyncSystem.agg", "1s presentation jitter aggregate", agg);
            _dbgMicroReverseWindow = 0;
            _dbgMicroReverseCount = 0;
            _dbgDeltaSum = 0f;
            _dbgDeltaMax = 0f;
            _dbgDeltaSamples = 0;
            _dbgSimBatchMax = 0;
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
