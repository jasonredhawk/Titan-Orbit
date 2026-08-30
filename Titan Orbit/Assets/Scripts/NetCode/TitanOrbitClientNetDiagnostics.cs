using TitanOrbit.Diagnostics;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Client-only NetCode timing diagnostics. Every 10 seconds while in-game, logs RTT, snapshot spacing,
    /// and a plain-English guess at whether rollbacks are caused by server slowness, network, or local FPS.
    /// Auto-installed via RuntimeInitializeOnLoad — no scene wiring. Pair with
    /// <see cref="TitanOrbitServerSimulationDiagnosticsSystem"/> on the dedicated server log.
    /// </summary>
    public sealed class TitanOrbitClientNetDiagnostics : MonoBehaviour
    {
        const float LogIntervalSeconds = 10f;

        /// <summary>Next Time.realtimeSinceStartup when we emit a summary line.</summary>
        float _nextLogTime;

        /// <summary>Counts NetCode "Large serverTick prediction error" lines since last summary.</summary>
        int _rollbackWarningsSinceLastLog;

        /// <summary>
        /// [UNITY] Hidden DontDestroyOnLoad object on player builds (not UNITY_SERVER).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstalled()
        {
#if UNITY_SERVER
            return;
#endif
            if (FindAnyObjectByType<TitanOrbitClientNetDiagnostics>() != null)
                return;

            var go = new GameObject(nameof(TitanOrbitClientNetDiagnostics));
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<TitanOrbitClientNetDiagnostics>();
        }

        void OnEnable()
        {
            // [NETCODE] NetDebug.LogError for large rollbacks — count them between summaries.
            Application.logMessageReceived += OnLogMessage;
        }

        void OnDisable()
        {
            Application.logMessageReceived -= OnLogMessage;
        }

        void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception)
                return;

            if (condition != null && condition.Contains("Large serverTick prediction error"))
                _rollbackWarningsSinceLastLog++;
        }

        void Update()
        {
            if (Time.realtimeSinceStartup < _nextLogTime)
                return;

            _nextLogTime = Time.realtimeSinceStartup + LogIntervalSeconds;
            TryLogSummary();
        }

        /// <summary>
        /// Reads NetworkSnapshotAck + NetworkTimeSystemData from ClientWorld and logs one interpretive line.
        /// </summary>
        void TryLogSummary()
        {
            var world = ClientServerBootstrap.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            if (!TitanOrbitSessionManager.IsClientGameplayReady(world))
                return;

            var em = world.EntityManager;
            if (!em.CreateEntityQuery(typeof(NetworkSnapshotAck)).TryGetSingleton<NetworkSnapshotAck>(out var ack))
                return;

            if (!em.CreateEntityQuery(typeof(NetworkTimeSystemData)).TryGetSingleton<NetworkTimeSystemData>(out var timeData))
                return;

            float rttMs = ack.EstimatedRTT;
            float jitterMs = ack.DeviationRTT;
            float commandAge = ack.ServerCommandAge / 256f;
            var loss = ack.SnapshotPacketLoss;
            float snapshotSpacingTicks = timeData.avgDeltaSimTicks;
            float snapshotSpacingMs = timeData.avgPacketInterArrival;
            float estimateAgeTicks = timeData.latestSnapshotAge / 256f;
            float clientFps = 1f / Mathf.Max(0.0001f, Time.smoothDeltaTime);
            int simHz = 0;
            if (em.CreateEntityQuery(typeof(ClientServerTickRate)).TryGetSingleton<ClientServerTickRate>(out var tickRate))
                simHz = tickRate.SimulationTickRate;
            int rollbacks = _rollbackWarningsSinceLastLog;
            _rollbackWarningsSinceLastLog = 0;

            string likely = InterpretLikelyCause(
                rttMs,
                jitterMs,
                snapshotSpacingTicks,
                snapshotSpacingMs,
                commandAge,
                clientFps,
                rollbacks);

            string line =
                "[NetDiagnostics/Client] RTT=" + rttMs.ToString("F0") + "±" + jitterMs.ToString("F0") + "ms" +
                " cmdAge=" + commandAge.ToString("F2") + " ticks" +
                " snapSpacing=" + snapshotSpacingTicks.ToString("F2") + " simTicks (~" +
                snapshotSpacingMs.ToString("F1") + "ms)" +
                " estErr=" + estimateAgeTicks.ToString("F2") + " ticks" +
                " fps≈" + clientFps.ToString("F0") +
                " rollbacks=" + rollbacks +
                " | Likely: " + likely;

            Debug.Log(line);
            DedicatedServerFileLog.Append("netdiag-client", line);

            // #region agent log
            AgentDebugNdjson.Write(
                "A",
                "TitanOrbitClientNetDiagnostics.cs:TryLogSummary",
                "client netdiag",
                "{\"rttMs\":" + rttMs.ToString("F1") +
                ",\"jitterMs\":" + jitterMs.ToString("F1") +
                ",\"cmdAge\":" + commandAge.ToString("F2") +
                ",\"snapTicks\":" + snapshotSpacingTicks.ToString("F2") +
                ",\"snapMs\":" + snapshotSpacingMs.ToString("F1") +
                ",\"estErr\":" + estimateAgeTicks.ToString("F2") +
                ",\"fps\":" + clientFps.ToString("F1") +
                ",\"simHz\":" + simHz +
                ",\"hasRelay\":" + (TitanOrbitRelayState.HasClientRelay ? "true" : "false") +
                ",\"dedicatedOnline\":" + (TitanOrbitSessionManager.IsDedicatedOnlineClient ? "true" : "false") +
                ",\"snapRecv\":" + loss.NumPacketsReceived +
                ",\"snapDrop\":" + loss.NumPacketsDroppedNeverArrived +
                ",\"snapClobber\":" + loss.NumPacketsCulledAsArrivedOnSameFrame +
                ",\"snapOOO\":" + loss.NumPacketsCulledOutOfOrder +
                ",\"lossPct\":" + (loss.CombinedPacketLossPercent * 100.0).ToString("F1") +
                ",\"rollbacks\":" + rollbacks +
                ",\"likely\":\"" + likely.Replace("\"", "'") + "\"}");
            // #endregion
        }

        /// <summary>
        /// Heuristic labels — not authoritative, but separates server snapshot pacing from RTT vs client FPS.
        /// </summary>
        static string InterpretLikelyCause(
            float rttMs,
            float jitterMs,
            float snapshotSpacingTicks,
            float snapshotSpacingMs,
            float commandAge,
            float clientFps,
            int rollbacks)
        {
            // --- Server / snapshot pacing ---
            // [NETCODE] At 60 Hz sim + 60 Hz network, avgDeltaSimTicks ≈ 1 and avgPacketInterArrival ≈ 16.7 ms.
            bool serverSnapshotsSlow = snapshotSpacingTicks > 1.35f ||
                                       (snapshotSpacingMs > 22f && rttMs < 120f);

            // --- Network ---
            bool networkLatency = rttMs > 120f;
            bool networkJitter = jitterMs > 40f;

            // --- Client ---
            bool clientSlow = clientFps < 50f;
            bool clientTooFarAhead = commandAge < -2f;

            if (serverSnapshotsSlow && !networkLatency)
                return "server sending snapshots slower than 60 Hz (check GCE CPU / server FPS log)";

            if (serverSnapshotsSlow && networkLatency)
                return "server snapshot pacing slow AND high RTT — both server perf and network";

            if (networkLatency && networkJitter)
                return "network latency + jitter (Relay path)";

            if (networkLatency)
                return "network latency (RTT high) — less likely pure server CPU";

            if (clientTooFarAhead && rollbacks > 0)
                return "client predicted ahead of server; if RTT is OK, server may have stalled briefly";

            if (clientSlow && rollbacks > 0)
                return "local client FPS low — prediction catch-up rollbacks";

            if (rollbacks > 0)
                return "transient rollbacks (" + rollbacks + " in " + LogIntervalSeconds + "s) — watch snapSpacing";

            if (clientSlow)
                return "local FPS low (may cause prediction stress)";

            return "healthy — metrics nominal";
        }
    }
}
