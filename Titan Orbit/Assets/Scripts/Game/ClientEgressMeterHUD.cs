using TitanOrbit;
using TitanOrbit.NetCode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TitanOrbit.Game
{
    /// <summary>
    /// On-screen telemetry for per-player NetCode egress while you play.
    /// <para>
    /// Enable <b>HUD → Show Egress Meter</b> on <c>GameManager</c> (NceGameRoot).
    /// When off (default), this MonoBehaviour does no ECS work and draws nothing —
    /// same contract as <c>ShipSpeedometerHUD</c> (no LateUpdate / no OnGUI work).
    /// </para>
    /// <para>
    /// <b>EGRESS THIS PLAYER</b> is client receive (snapshots + inbound RPCs) — the bytes
    /// Unity Relay / GCE sent toward you. That is the number that drives player-side Relay bills.
    /// Command packets are upload (client → server) and shown separately so they are not
    /// mixed into egress. Server per-connection EndSend is not hooked (Burst Local Host
    /// crashed on that path) — Local Host still shows this client's receive.
    /// </para>
    /// <para>
    /// UDP/IP, DTLS/WSS, and Relay encapsulation are not in the payload counters. The overlay
    /// adds an estimated header line from packet count × (UTP MaxHeaderSize + 28 + Relay guess).
    /// UTP heartbeats are not in GhostSend/Rpc EndSend — treat ~1 KB/s as slack.
    /// </para>
    /// <para>
    /// Shift+F8 or the RESET button zeros session totals so a quiet cruise vs a fight is easy
    /// to isolate. Instruction Image Capture also uses Shift+F8 to cancel — leave that tool off
    /// while metering. Client presentation only — dedicated servers skip install via
    /// <see cref="TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation"/>.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(70150)]
    public sealed class ClientEgressMeterHUD : MonoBehaviour
    {
        /// <summary>UDP + IPv4 header bytes assumed per datagram (no IP options).</summary>
        const int UdpIpv4HeaderBytes = 28;

        /// <summary>Extra Relay encapsulation guess when a Relay join allocation is stored.</summary>
        const int RelayHeaderGuessBytes = 16;

        /// <summary>Smoothing factor for the 10 s EMA at 1 Hz samples (≈ 1 − exp(−1/10)).</summary>
        const float EmaAlpha = 0.1f;

        /// <summary>Last GameManager overlay value we applied (so turning on resets the session).</summary>
        bool _wasEnabled;

        /// <summary>Realtime at session start / last reset.</summary>
        float _sessionStartRealtime;

        /// <summary>Last 1 Hz sample time.</summary>
        float _lastSampleRealtime;

        /// <summary>Previous 1 Hz client recv snapshot+rpc bytes (for instant KB/s).</summary>
        ulong _prevRecvBytes;

        /// <summary>Previous 1 Hz client recv packet count (for header estimate).</summary>
        ulong _prevRecvPackets;

        /// <summary>Instantaneous payload KB/s from the last 1 s window.</summary>
        float _instantKBps;

        /// <summary>Exponential moving average of 1 s rates (~10 s time constant).</summary>
        float _emaKBps;

        /// <summary>Peak 1 s KB/s this session.</summary>
        float _peakKBps;

        /// <summary>Estimated UDP+UTP (+Relay) header KB/s from the last 1 s packet delta.</summary>
        float _headerKBps;

        /// <summary>True after the first 1 s sample so we do not treat join as a spike from zero.</summary>
        bool _havePrevSample;

        /// <summary>
        /// [UNITY] Auto-install after scene load. Dedicated servers skip entirely.
        /// The component stays idle until the GameManager toggle is on.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstalled()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            if (FindAnyObjectByType<ClientEgressMeterHUD>() != null)
                return;

            var session = FindAnyObjectByType<TitanOrbitSessionManager>();
            if (session != null)
            {
                session.gameObject.AddComponent<ClientEgressMeterHUD>();
                return;
            }

            var go = new GameObject("ClientEgressMeterHUD");
            DontDestroyOnLoad(go);
            go.AddComponent<ClientEgressMeterHUD>();
        }

        /// <summary>
        /// [UNITY] Update — sample rates at 1 Hz and accept Shift+F8 reset.
        /// Returns immediately when the GameManager toggle is off (no ECS, no HUD math).
        /// </summary>
        void Update()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            bool enabled = TitanOrbitDebugFlags.EgressMeterEnabled;
            if (!enabled)
            {
                _wasEnabled = false;
                return;
            }

            if (!_wasEnabled)
            {
                BeginSession();
                _wasEnabled = true;
            }

            var keyboard = Keyboard.current;
            if (keyboard != null &&
                (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed) &&
                keyboard.f8Key.wasPressedThisFrame)
            {
                ResetSession();
            }

            float now = Time.realtimeSinceStartup;
            if (now - _lastSampleRealtime < 1f)
                return;

            SampleOneSecond(now);
        }

        /// <summary>
        /// Dark telemetry overlay (debug HUD, not a light settings card).
        /// Drawn only while the toggle is on and this is a client process.
        /// Rates are precomputed at 1 Hz so OnGUI does no ECS work.
        /// </summary>
        void OnGUI()
        {
            if (!TitanOrbitDebugFlags.EgressMeterEnabled ||
                !TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            Color prevBg = GUI.backgroundColor;
            Color prevContent = GUI.contentColor;
            GUI.backgroundColor = new Color(0.012f, 0.016f, 0.028f, 0.94f);
            GUI.contentColor = new Color(0.88f, 0.92f, 0.98f);

            const float width = 440f;
            float height = 28f + 18f * 16f;
            if (TitanOrbitEgressMeter.ServerConnectionCount > 0)
                height += 18f * (2 + TitanOrbitEgressMeter.ServerConnectionCount);

            // Top-right so we do not cover the stutter isolator (top-left) or speedometer (top-center).
            float x = Mathf.Max(12f, Screen.width - width - 12f);
            GUILayout.BeginArea(new Rect(x, 12f, width, height), GUI.skin.box);
            GUILayout.Label("EGRESS THIS PLAYER");
            GUILayout.Label(
                "recv  " + FormatKBps(_instantKBps) +
                "   avg " + FormatKBps(_emaKBps) +
                "   peak " + FormatKBps(_peakKBps));

            ulong recvTotal = TitanOrbitEgressMeter.ClientRecvSnapshotBytes +
                               TitanOrbitEgressMeter.ClientRecvRpcBytes;
            float elapsed = Mathf.Max(0.01f, Time.realtimeSinceStartup - _sessionStartRealtime);
            GUILayout.Label(
                "session  " + FormatMb(recvTotal) +
                "  in " + FormatElapsed(elapsed) +
                (TitanOrbitEgressMeter.HasSample ? string.Empty : "  (waiting for connection)"));

            float snapKBps = BytesPerSecToKBps(TitanOrbitEgressMeter.ClientRecvSnapshotBytes, elapsed);
            float rpcKBps = BytesPerSecToKBps(TitanOrbitEgressMeter.ClientRecvRpcBytes, elapsed);
            GUILayout.Label(
                "snap " + FormatKBps(snapKBps) +
                "   rpc " + FormatKBps(rpcKBps) +
                "   pkts " + TitanOrbitEgressMeter.ClientRecvPacketCount);

            DrawGhostRows();

            float uploadKBps = BytesPerSecToKBps(
                TitanOrbitEgressMeter.ClientSendCommandBytes + TitanOrbitEgressMeter.ClientSendRpcBytes,
                elapsed);
            GUILayout.Label(
                "upload (commands+client RPCs, not egress)  " + FormatKBps(uploadKBps));

            ulong headerBytes = EstimateHeaderBytes(
                TitanOrbitEgressMeter.ClientRecvPacketCount,
                TitanOrbitEgressMeter.UtpHeaderBytes,
                TitanOrbitEgressMeter.RelayActive);
            GUILayout.Label(
                "est. UDP+UTP headers  " + FormatKBps(_headerKBps) +
                "  (" + FormatMb(headerBytes) + " session)" +
                "   Relay: " + (TitanOrbitEgressMeter.RelayActive ? "yes" : "off"));
            GUILayout.Label("UTP heartbeats ~1 KB/s slack, not in payload");

            if (TitanOrbitEgressMeter.ServerConnectionCount > 0)
            {
                GUILayout.Label("SERVER SEND (this process)");
                for (int i = 0; i < TitanOrbitEgressMeter.ServerConnectionCount; i++)
                {
                    var row = TitanOrbitEgressMeter.ServerConnections[i];
                    ulong send = row.SendSnapshotBytes + row.SendRpcBytes;
                    GUILayout.Label(
                        "nid " + row.NetworkId +
                        "  " + FormatKBps(BytesPerSecToKBps(send, elapsed)) +
                        "  snap " + FormatMb(row.SendSnapshotBytes) +
                        "  rpc " + FormatMb(row.SendRpcBytes) +
                        "  pkts " + row.SendPackets);
                }
            }

            if (GUILayout.Button("RESET session (Shift+F8)"))
                ResetSession();

            GUILayout.EndArea();

            GUI.backgroundColor = prevBg;
            GUI.contentColor = prevContent;
        }

        /// <summary>Starts a fresh session clock and zeros running totals.</summary>
        void BeginSession()
        {
            ResetSession();
        }

        /// <summary>
        /// Zeros Shared + ECS counters (copy systems apply ResetVersion) and HUD rate state.
        /// </summary>
        void ResetSession()
        {
            TitanOrbitEgressMeter.RequestReset();
            _sessionStartRealtime = Time.realtimeSinceStartup;
            _lastSampleRealtime = _sessionStartRealtime;
            _prevRecvBytes = 0;
            _prevRecvPackets = 0;
            _instantKBps = 0f;
            _emaKBps = 0f;
            _peakKBps = 0f;
            _headerKBps = 0f;
            _havePrevSample = false;
        }

        /// <summary>Derives instant / EMA / peak KB/s from the last 1 s of client receive.</summary>
        void SampleOneSecond(float now)
        {
            ulong recv = TitanOrbitEgressMeter.ClientRecvSnapshotBytes +
                         TitanOrbitEgressMeter.ClientRecvRpcBytes;
            ulong packets = TitanOrbitEgressMeter.ClientRecvPacketCount;
            float dt = Mathf.Max(0.001f, now - _lastSampleRealtime);
            _lastSampleRealtime = now;

            if (_havePrevSample)
            {
                ulong delta = recv >= _prevRecvBytes ? recv - _prevRecvBytes : 0UL;
                _instantKBps = (delta / 1024f) / dt;
                _emaKBps = _emaKBps <= 0.001f
                    ? _instantKBps
                    : Mathf.Lerp(_emaKBps, _instantKBps, EmaAlpha);
                if (_instantKBps > _peakKBps)
                    _peakKBps = _instantKBps;

                ulong pktDelta = packets >= _prevRecvPackets ? packets - _prevRecvPackets : 0UL;
                ulong headerDelta = EstimateHeaderBytes(
                    pktDelta,
                    TitanOrbitEgressMeter.UtpHeaderBytes,
                    TitanOrbitEgressMeter.RelayActive);
                _headerKBps = (headerDelta / 1024f) / dt;
            }

            _prevRecvBytes = recv;
            _prevRecvPackets = packets;
            _havePrevSample = true;
        }

        /// <summary>
        /// Editor ghost-type rows (ships / planets / gems). Hidden when stats are empty.
        /// SizeInBits is the last received snapshot, shown as KB of that snapshot (not a 60 Hz guess).
        /// </summary>
        static void DrawGhostRows()
        {
            bool any = TitanOrbitEgressMeter.GhostShipCount +
                       TitanOrbitEgressMeter.GhostPlanetCount +
                       TitanOrbitEgressMeter.GhostGemCount +
                       TitanOrbitEgressMeter.GhostOtherCount > 0;
            if (!any)
                return;

            GUILayout.Label(
                "last snap  ships " + FormatBitsKb(TitanOrbitEgressMeter.GhostShipBits) +
                " x" + TitanOrbitEgressMeter.GhostShipCount +
                "   planets " + FormatBitsKb(TitanOrbitEgressMeter.GhostPlanetBits) +
                " x" + TitanOrbitEgressMeter.GhostPlanetCount +
                "   gems " + FormatBitsKb(TitanOrbitEgressMeter.GhostGemBits) +
                " x" + TitanOrbitEgressMeter.GhostGemCount);
            if (TitanOrbitEgressMeter.GhostOtherCount > 0)
            {
                GUILayout.Label(
                    "           other " + FormatBitsKb(TitanOrbitEgressMeter.GhostOtherBits) +
                    " x" + TitanOrbitEgressMeter.GhostOtherCount);
            }
        }

        /// <summary>Session-average KB/s from a running byte total over elapsed seconds.</summary>
        static float BytesPerSecToKBps(ulong bytes, float elapsedSeconds)
        {
            if (elapsedSeconds <= 0.01f)
                return 0f;
            return (bytes / 1024f) / elapsedSeconds;
        }

        /// <summary>GhostMetrics SizeInBits → last-snapshot KB (not a rate).</summary>
        static string FormatBitsKb(ulong bits)
        {
            float kb = bits / 8f / 1024f;
            return kb.ToString("0.00") + " KB";
        }

        /// <summary>Formats a KB/s value for the overlay.</summary>
        static string FormatKBps(float kbps)
        {
            return kbps.ToString("0.0") + " KB/s";
        }

        /// <summary>Formats a byte count as MiB (1024-based) for session totals.</summary>
        static string FormatMb(ulong bytes)
        {
            double mb = bytes / (1024.0 * 1024.0);
            return mb.ToString("0.00") + " MB";
        }

        /// <summary>Formats elapsed session time as m:ss.</summary>
        static string FormatElapsed(float seconds)
        {
            int total = Mathf.Max(0, Mathf.FloorToInt(seconds));
            int m = total / 60;
            int s = total % 60;
            return m.ToString() + ":" + s.ToString("00");
        }

        /// <summary>
        /// Estimates transport headers for the counted Data packets (not a capture of the NIC).
        /// UDP+IPv4 + UTP pipeline header + optional Relay guess.
        /// </summary>
        static ulong EstimateHeaderBytes(ulong packetCount, int utpHeaderBytes, bool relay)
        {
            int perPacket = UdpIpv4HeaderBytes + Mathf.Max(0, utpHeaderBytes);
            if (relay)
                perPacket += RelayHeaderGuessBytes;
            return packetCount * (ulong)perPacket;
        }
    }
}
