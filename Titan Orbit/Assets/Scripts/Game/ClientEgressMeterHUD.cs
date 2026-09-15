using System;
using System.Globalization;
using System.IO;
using System.Text;
using TitanOrbit;
using TitanOrbit.NetCode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TitanOrbit.Game
{
    /// <summary>
    /// On-screen telemetry for how much NetCode payload this client is receiving.
    /// <para>
    /// Enable <b>HUD → Show Egress Meter</b> on <c>GameManager</c> (NceGameRoot).
    /// When off (default), this MonoBehaviour does no ECS work and draws nothing.
    /// </para>
    /// <para>
    /// The big number is <b>download</b>: world snapshots + inbound RPCs the dedicated
    /// server sent toward you over direct UDP (no Unity Relay). Upload is not sampled
    /// after the Burst crash fix. Shift+F8 or Reset zeros the session. Instruction
    /// Image Capture also uses Shift+F8 — leave that tool off while metering.
    /// </para>
    /// <para>
    /// While the overlay is on, 1 Hz samples are appended to
    /// <c>Titan Orbit/Logs/egress-meter-*.csv</c> (gitignored) so a session can be read
    /// after Play Mode. Nothing is stored if the meter was never enabled.
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

        bool _wasEnabled;
        float _sessionStartRealtime;
        float _lastSampleRealtime;
        ulong _prevRecvBytes;
        ulong _prevSnapBytes;
        ulong _prevRpcBytes;
        ulong _prevRecvPackets;
        float _instantKBps;
        float _emaKBps;
        float _peakKBps;
        float _instantSnapKBps;
        float _instantRpcKBps;
        float _headerKBps;
        bool _havePrevSample;

        GUIStyle _titleStyle;
        GUIStyle _sectionStyle;
        GUIStyle _bodyStyle;
        GUIStyle _mutedStyle;

        string _lineNow = "Starting…";
        string _lineAvgPeak = string.Empty;
        string _lineSession = string.Empty;
        string _lineSnap = string.Empty;
        string _lineRpc = string.Empty;
        string _lineGhosts = string.Empty;
        string _lineNote = string.Empty;
        string _lineLog = string.Empty;
        string _status = "Starting…";

        StreamWriter _log;
        string _logFileName = string.Empty;
        readonly StringBuilder _csv = new StringBuilder(256);

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
        /// Returns immediately when the GameManager toggle is off.
        /// </summary>
        void Update()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            bool enabled = TitanOrbitDebugFlags.EgressMeterEnabled;
            if (!enabled)
            {
                if (_wasEnabled)
                    CloseLog("meter off");
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

        void OnDisable()
        {
            CloseLog("disabled");
        }

        void OnDestroy()
        {
            CloseLog("destroyed");
        }

        /// <summary>
        /// Dark telemetry overlay. Draws cached 1 Hz strings only — no ECS work in OnGUI.
        /// </summary>
        void OnGUI()
        {
            if (!TitanOrbitDebugFlags.EgressMeterEnabled ||
                !TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            EnsureStyles();

            Color prevBg = GUI.backgroundColor;
            Color prevContent = GUI.contentColor;
            GUI.backgroundColor = new Color(0.012f, 0.016f, 0.028f, 0.94f);
            GUI.contentColor = new Color(0.88f, 0.92f, 0.98f);

            const float width = 420f;
            const float height = 340f;
            float x = Mathf.Max(12f, Screen.width - width - 12f);
            GUILayout.BeginArea(new Rect(x, 12f, width, height), GUI.skin.box);

            GUILayout.Label("Download  (server → you)", _titleStyle);
            GUILayout.Label(_status, _mutedStyle);
            GUILayout.Space(4f);

            GUILayout.Label(_lineNow, _bodyStyle);
            GUILayout.Label(_lineAvgPeak, _bodyStyle);
            GUILayout.Label(_lineSession, _bodyStyle);

            GUILayout.Space(6f);
            GUILayout.Label("What is arriving", _sectionStyle);
            GUILayout.Label(_lineSnap, _bodyStyle);
            GUILayout.Label(_lineRpc, _bodyStyle);

            if (!string.IsNullOrEmpty(_lineGhosts))
            {
                GUILayout.Space(6f);
                GUILayout.Label("Latest world snapshot", _sectionStyle);
                GUILayout.Label(_lineGhosts, _mutedStyle);
            }

            GUILayout.Space(6f);
            GUILayout.Label(_lineNote, _mutedStyle);
            GUILayout.Label(_lineLog, _mutedStyle);

            GUILayout.Space(4f);
            if (GUILayout.Button("Reset session  (Shift+F8)"))
                ResetSession();

            GUILayout.EndArea();

            GUI.backgroundColor = prevBg;
            GUI.contentColor = prevContent;
        }

        /// <summary>Starts a fresh session clock, log file, and zeros running totals.</summary>
        void BeginSession()
        {
            ResetSession();
        }

        /// <summary>
        /// Zeros Shared counters and HUD rate state. Keeps the same CSV and writes a RESET marker.
        /// </summary>
        void ResetSession()
        {
            TitanOrbitEgressMeter.RequestReset();
            _sessionStartRealtime = Time.realtimeSinceStartup;
            _lastSampleRealtime = _sessionStartRealtime;
            _prevRecvBytes = 0;
            _prevSnapBytes = 0;
            _prevRpcBytes = 0;
            _prevRecvPackets = 0;
            _instantKBps = 0f;
            _emaKBps = 0f;
            _peakKBps = 0f;
            _instantSnapKBps = 0f;
            _instantRpcKBps = 0f;
            _headerKBps = 0f;
            _havePrevSample = false;
            _status = "Waiting for a connection…";
            _lineNow = "Now          —";
            _lineAvgPeak = "Avg / peak   —";
            _lineSession = "This session —";
            _lineSnap = "World updates (snapshots)  —";
            _lineRpc = "Game events (RPCs)         —";
            _lineGhosts = string.Empty;
            _lineNote = "Game payload only — not a full internet capture.";
            OpenLogIfNeeded();
            WriteLogMarker("RESET");
            RebuildLogLine();
        }

        /// <summary>Derives instant / EMA / peak KB/s from the last 1 s of client receive.</summary>
        void SampleOneSecond(float now)
        {
            ulong snap = TitanOrbitEgressMeter.ClientRecvSnapshotBytes;
            ulong rpc = TitanOrbitEgressMeter.ClientRecvRpcBytes;
            ulong recv = snap + rpc;
            ulong packets = TitanOrbitEgressMeter.ClientRecvPacketCount;
            float dt = Mathf.Max(0.001f, now - _lastSampleRealtime);
            _lastSampleRealtime = now;

            if (_havePrevSample)
            {
                _instantKBps = BytesDeltaToKBps(recv, _prevRecvBytes, dt);
                _instantSnapKBps = BytesDeltaToKBps(snap, _prevSnapBytes, dt);
                _instantRpcKBps = BytesDeltaToKBps(rpc, _prevRpcBytes, dt);
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
            _prevSnapBytes = snap;
            _prevRpcBytes = rpc;
            _prevRecvPackets = packets;
            _havePrevSample = true;

            RebuildDisplay(recv, snap, rpc);
            WriteLogSample(recv, snap, rpc, packets);
        }

        /// <summary>Refreshes the cached overlay strings from the latest 1 s sample.</summary>
        void RebuildDisplay(ulong recv, ulong snap, ulong rpc)
        {
            if (!TitanOrbitEgressMeter.HasSample)
            {
                _status = "Waiting for a connection…";
                return;
            }

            _status = TitanOrbitEgressMeter.RelayActive
                ? "Unity Relay join is set — unexpected; this path should be direct UDP."
                : "Direct UDP  (you ↔ dedicated server)";

            _lineNow = "Now          " + FormatKBps(_instantKBps);
            _lineAvgPeak = "Avg (~10s)   " + FormatKBps(_emaKBps) +
                           "     Peak  " + FormatKBps(_peakKBps);

            float elapsed = Mathf.Max(0.01f, Time.realtimeSinceStartup - _sessionStartRealtime);
            _lineSession = "This session " + FormatMb(recv) + "  in  " + FormatElapsed(elapsed);

            _lineSnap = "World updates (snapshots)  " + FormatKBps(_instantSnapKBps) +
                        "     " + FormatMb(snap) + " total";
            _lineRpc = "Game events (RPCs)         " + FormatKBps(_instantRpcKBps) +
                       "     " + FormatMb(rpc) + " total";

            _lineGhosts = FormatGhostBreakdown();
            _lineNote = "Game payload only (not a NIC capture). UDP/UTP headers ~" +
                        FormatKBps(_headerKBps) + ". Keep-alives ~1 KB/s extra.";
            RebuildLogLine();
        }

        /// <summary>Editor ghost-type sizes from the last received snapshot (KB, not a rate).</summary>
        static string FormatGhostBreakdown()
        {
            bool any = TitanOrbitEgressMeter.GhostShipCount +
                       TitanOrbitEgressMeter.GhostPlanetCount +
                       TitanOrbitEgressMeter.GhostGemCount +
                       TitanOrbitEgressMeter.GhostOtherCount > 0;
            if (!any)
                return string.Empty;

            var sb = new StringBuilder(160);
            AppendGhostRow(sb, "ships", TitanOrbitEgressMeter.GhostShipCount, TitanOrbitEgressMeter.GhostShipBits);
            AppendGhostRow(sb, "planets", TitanOrbitEgressMeter.GhostPlanetCount, TitanOrbitEgressMeter.GhostPlanetBits);
            AppendGhostRow(sb, "gems", TitanOrbitEgressMeter.GhostGemCount, TitanOrbitEgressMeter.GhostGemBits);
            AppendGhostRow(sb, "other", TitanOrbitEgressMeter.GhostOtherCount, TitanOrbitEgressMeter.GhostOtherBits);
            return sb.ToString();
        }

        static void AppendGhostRow(StringBuilder sb, string name, uint count, ulong bits)
        {
            if (count == 0)
                return;
            if (sb.Length > 0)
                sb.Append("   ");
            sb.Append(count.ToString());
            sb.Append(' ');
            sb.Append(name);
            sb.Append("  ");
            sb.Append(FormatBitsKb(bits));
        }

        void RebuildLogLine()
        {
            _lineLog = string.IsNullOrEmpty(_logFileName)
                ? "Not writing a log file."
                : "Saving 1s samples to Logs/" + _logFileName;
        }

        void EnsureStyles()
        {
            if (_titleStyle != null)
                return;

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 14,
            };
            _sectionStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 12,
            };
            _bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
            };
            _mutedStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                wordWrap = true,
            };
            _mutedStyle.normal.textColor = new Color(0.62f, 0.68f, 0.76f);
        }

        void OpenLogIfNeeded()
        {
            if (_log != null)
                return;

            try
            {
                string dir = GetLogDirectory();
                Directory.CreateDirectory(dir);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                _logFileName = "egress-meter-" + stamp + ".csv";
                string path = Path.Combine(dir, _logFileName);
                _log = new StreamWriter(path, false, new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
                _log.WriteLine("# Titan Orbit egress meter — download payload this client received");
                _log.WriteLine("# started " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
                _log.WriteLine(
                    "elapsed_s,now_kbps,avg_kbps,peak_kbps,session_bytes,snap_bytes,rpc_bytes,packets,header_kbps,relay,ship_n,ship_kb,planet_n,planet_kb,gem_n,gem_kb,other_n,other_kb");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[EgressMeter] Could not write Logs CSV: " + ex.Message);
                _log = null;
                _logFileName = string.Empty;
            }
        }

        void WriteLogSample(ulong recv, ulong snap, ulong rpc, ulong packets)
        {
            if (_log == null)
                return;

            float elapsed = Mathf.Max(0f, Time.realtimeSinceStartup - _sessionStartRealtime);
            _csv.Length = 0;
            _csv.Append(elapsed.ToString("0.0", CultureInfo.InvariantCulture)).Append(',');
            _csv.Append(_instantKBps.ToString("0.00", CultureInfo.InvariantCulture)).Append(',');
            _csv.Append(_emaKBps.ToString("0.00", CultureInfo.InvariantCulture)).Append(',');
            _csv.Append(_peakKBps.ToString("0.00", CultureInfo.InvariantCulture)).Append(',');
            _csv.Append(recv).Append(',');
            _csv.Append(snap).Append(',');
            _csv.Append(rpc).Append(',');
            _csv.Append(packets).Append(',');
            _csv.Append(_headerKBps.ToString("0.00", CultureInfo.InvariantCulture)).Append(',');
            _csv.Append(TitanOrbitEgressMeter.RelayActive ? '1' : '0').Append(',');
            _csv.Append(TitanOrbitEgressMeter.GhostShipCount).Append(',');
            _csv.Append(BitsToKb(TitanOrbitEgressMeter.GhostShipBits)).Append(',');
            _csv.Append(TitanOrbitEgressMeter.GhostPlanetCount).Append(',');
            _csv.Append(BitsToKb(TitanOrbitEgressMeter.GhostPlanetBits)).Append(',');
            _csv.Append(TitanOrbitEgressMeter.GhostGemCount).Append(',');
            _csv.Append(BitsToKb(TitanOrbitEgressMeter.GhostGemBits)).Append(',');
            _csv.Append(TitanOrbitEgressMeter.GhostOtherCount).Append(',');
            _csv.Append(BitsToKb(TitanOrbitEgressMeter.GhostOtherBits));
            _log.WriteLine(_csv.ToString());
        }

        void WriteLogMarker(string tag)
        {
            if (_log == null)
                return;
            _log.WriteLine(
                "# " + tag + " " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        }

        void CloseLog(string reason)
        {
            if (_log == null)
                return;
            try
            {
                _log.WriteLine(
                    "# end " + reason +
                    " peak_kbps=" + _peakKBps.ToString("0.00", CultureInfo.InvariantCulture) +
                    " session_bytes=" + (_prevRecvBytes).ToString(CultureInfo.InvariantCulture));
                _log.Flush();
                _log.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[EgressMeter] Could not close Logs CSV: " + ex.Message);
            }

            _log = null;
        }

        /// <summary>Unity project Logs folder in Editor; persistentDataPath/Logs in a player build.</summary>
        static string GetLogDirectory()
        {
#if UNITY_EDITOR
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs"));
#else
            return Path.Combine(Application.persistentDataPath, "Logs");
#endif
        }

        static float BytesDeltaToKBps(ulong current, ulong previous, float dt)
        {
            ulong delta = current >= previous ? current - previous : 0UL;
            return (delta / 1024f) / dt;
        }

        static string BitsToKb(ulong bits)
        {
            return (bits / 8f / 1024f).ToString("0.000", CultureInfo.InvariantCulture);
        }

        static string FormatBitsKb(ulong bits)
        {
            return (bits / 8f / 1024f).ToString("0.00") + " KB";
        }

        static string FormatKBps(float kbps)
        {
            return kbps.ToString("0.0") + " KB/s";
        }

        static string FormatMb(ulong bytes)
        {
            return (bytes / (1024.0 * 1024.0)).ToString("0.00") + " MB";
        }

        static string FormatElapsed(float seconds)
        {
            int total = Mathf.Max(0, Mathf.FloorToInt(seconds));
            int m = total / 60;
            int s = total % 60;
            return m.ToString() + ":" + s.ToString("00");
        }

        /// <summary>
        /// Estimates transport headers for the counted Data packets (not a capture of the NIC).
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
