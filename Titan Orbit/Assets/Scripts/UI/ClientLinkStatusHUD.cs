using TitanOrbit.NetCode;
using TMPro;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Small signal icon in the top-right, just left of the team leaderboard, plus a
    /// top banner when the match link is actually failing. Three bars: green when the
    /// link is healthy, amber when it is late, one red bar when snapshots have stopped.
    /// It never sends gameplay RPCs. After a few seconds with no new snapshot it asks
    /// <see cref="TitanOrbitSessionManager"/> to return to the menu.
    /// <para>
    /// A dead Relay socket often never raises <see cref="ConnectionState.State.Disconnected"/>.
    /// <see cref="TitanOrbitSessionManager.IsClientGameplayReady"/> stays true, so the
    /// existing drop watcher never runs. Local prediction keeps moving the ship, and
    /// server-owned ghosts (enemy turrets) freeze on the last snapshot. The 12×9 pip
    /// is the glance signal. The banner is the notice a player can actually read,
    /// including while the orbit station has faded the leaderboard.
    /// </para>
    /// <para>
    /// The pip is a child of <see cref="TeamLeaderboardHUD"/>'s panel. The banner is
    /// its own overlay and only exists while commands are unacked or snapshots have
    /// stopped, so a healthy match does not draw a card over the orbit menu.
    /// </para>
    /// </summary>
    public sealed class ClientLinkStatusHUD : MonoBehaviour
    {
        /// <summary>What the three bars should show. Off means the icon is not on screen.</summary>
        enum LinkFeel : byte
        {
            /// <summary>Main menu, or still joining. The icon stays inactive.</summary>
            Off = 0,

            /// <summary>Snapshots are arriving and the round-trip is ordinary. All three bars lit green.</summary>
            Connected = 1,

            /// <summary>Packets are late or the round-trip is high. Two bars lit amber.</summary>
            Lag = 2,

            /// <summary>No new server snapshot, or the connection entity is gone. One bar lit red.</summary>
            Lost = 3,

            /// <summary>
            /// Snapshots are still arriving, but the server has stopped acknowledging
            /// this client's commands. The ship still flies locally; those controls
            /// are not landing on the match.
            /// </summary>
            Stalled = 4,
        }

        /// <summary>How often we read NetCode. Not every frame — the numbers move slowly.</summary>
        const float SampleIntervalSeconds = 0.25f;

        /// <summary>
        /// Gap with no new snapshot before we say LAG. About 30 missed ticks at 60 Hz.
        /// A single long frame does not trip this; the clock is the snapshot tick, not FPS.
        /// </summary>
        const float LagSilenceSeconds = 0.5f;

        /// <summary>
        /// Gap with no new snapshot before we say the link is gone.
        /// Unity Transport's disconnect timer is much longer (tens of seconds), and a
        /// dead WebSocket may never report disconnect at all. Two seconds is long
        /// enough to skip a hitch and short enough that frozen turrets are explained.
        /// </summary>
        const float LostSilenceSeconds = 2f;

        /// <summary>
        /// How long <see cref="LinkFeel.Lost"/> must hold before we leave the match.
        /// Two seconds of silence raises the banner; four more seconds returns to the
        /// menu. Waiting for the transport's ~30s disconnect left the ship flying a
        /// frozen world with no obvious notice.
        /// </summary>
        const float LostHoldBeforeResetSeconds = 4f;

        /// <summary>
        /// Unacknowledged command age, in simulation ticks, that means the server
        /// stopped eating this client's input. Healthy dedicated play sits near −2.
        /// A join hitch can touch the mid-teens, and the ship-visual hold is 8, so
        /// 30 is past both. A dead session can jump to hundreds of ticks while
        /// snapshots are still moving — that is this state, not ordinary lag.
        /// </summary>
        const float CommandStallTicks = 30f;

        /// <summary>Command age must fall back under this before a stall banner clears.</summary>
        const float CommandStallClearTicks = 12f;

        /// <summary>Consecutive 0.25s samples above <see cref="CommandStallTicks"/> before the banner.</summary>
        const int EnterStallSamples = 2;

        /// <summary>Round-trip, in milliseconds, that turns the chip on. Ordinary Relay (~70 ms) stays quiet.</summary>
        const float LagRttMs = 200f;

        /// <summary>Round-trip under this clears LAG, so a value jittering around the enter line does not flicker.</summary>
        const float ClearRttMs = 140f;

        /// <summary>Jitter (RTT deviation), in milliseconds, that turns the chip on.</summary>
        const float LagJitterMs = 70f;

        /// <summary>Jitter under this is allowed to clear LAG.</summary>
        const float ClearJitterMs = 40f;

        /// <summary>
        /// Average time between snapshots, in milliseconds. At 60 Hz this sits near 17 ms.
        /// Above ~55 ms the server or the path is delivering ghosts late.
        /// </summary>
        const float LagPacketGapMs = 55f;

        /// <summary>Packet gap under this is allowed to clear LAG.</summary>
        const float ClearPacketGapMs = 32f;

        /// <summary>Bad samples in a row before LAG appears. One spiked sample stays hidden.</summary>
        const int EnterLagSamples = 2;

        /// <summary>Healthy samples in a row before LAG hides again.</summary>
        const int ClearLagSamples = 2;

        /// <summary>Gap between the icon's right edge and the leaderboard's left edge.</summary>
        const float GapLeftOfLeaderboard = 4f;

        /// <summary>
        /// Icon plate in canvas pixels. A status pip, not a banner — the old
        /// 156×26 label read as a loading title.
        /// </summary>
        const float IconWidth = 12f;
        const float IconHeight = 9f;

        static readonly Color PlateFill = new Color(0.012f, 0.016f, 0.028f, 0.92f);
        static readonly Color ConnectedLit = new Color(0.35f, 0.90f, 0.55f, 1f);
        static readonly Color LagLit = new Color(0.95f, 0.72f, 0.28f, 1f);
        static readonly Color LostLit = new Color(0.92f, 0.28f, 0.26f, 1f);
        static readonly Color StallLit = new Color(0.95f, 0.45f, 0.22f, 1f);
        static readonly Color BarDim = new Color(0.22f, 0.26f, 0.32f, 0.55f);
        static readonly Color AlertTitle = new Color(0.98f, 0.90f, 0.84f, 1f);
        static readonly Color AlertBody = new Color(0.88f, 0.92f, 0.98f, 1f);

        /// <summary>Client world the cached queries belong to. A new world means the old queries are dead.</summary>
        World _queryWorld;

        /// <summary>One <see cref="NetworkSnapshotAck"/> on the client connection. Round-trip lives here.</summary>
        EntityQuery _ackQuery;

        /// <summary>Singleton clock. <see cref="NetworkTimeSystemData.latestSnapshot"/> is the last server tick we got.</summary>
        EntityQuery _timeQuery;

        /// <summary>True after <see cref="EnsureQueries"/> succeeded for <see cref="_queryWorld"/>.</summary>
        bool _queriesReady;

        /// <summary>Unscaled time of the next NetCode read.</summary>
        float _nextSampleTime;

        /// <summary>True after this match has had a gameplay-ready connection. Used to notice the entity vanishing.</summary>
        bool _wasReady;

        /// <summary>True after at least one real snapshot tick. Join, before the first ghost packet, is not a drop.</summary>
        bool _seenSnapshot;

        /// <summary>Last <see cref="NetworkTimeSystemData.latestSnapshot"/> we observed.</summary>
        NetworkTick _lastSnapshot;

        /// <summary>Unscaled time when <see cref="_lastSnapshot"/> last moved forward.</summary>
        float _lastSnapshotAdvanceTime;

        /// <summary>Consecutive samples that looked late. LAG waits for <see cref="EnterLagSamples"/>.</summary>
        int _lagStreak;

        /// <summary>Consecutive samples that looked healthy. LAG clears after <see cref="ClearLagSamples"/>.</summary>
        int _clearStreak;

        /// <summary>What the icon showed last sample. Used so we log the drop once, not every quarter-second.</summary>
        LinkFeel _shown = LinkFeel.Off;

        /// <summary>Consecutive samples with command age at or above <see cref="CommandStallTicks"/>.</summary>
        int _stallStreak;

        /// <summary>
        /// Unscaled time when snapshot silence (or a vanished connection) first became
        /// <see cref="LinkFeel.Lost"/>. Zero while the link can still recover.
        /// </summary>
        float _lostSince;

        /// <summary>True after we have already asked the session to return to the menu.</summary>
        bool _resetRequested;

        /// <summary>Leaderboard we last parented under. Cleared when that object is destroyed.</summary>
        TeamLeaderboardHUD _leaderboard;

        /// <summary>Signal plate. A child of the leaderboard panel, sitting just outside its top-left.</summary>
        GameObject _icon;

        /// <summary>Short, middle, and tall bars. Index 0 is the shortest (left).</summary>
        readonly Image[] _bars = new Image[3];

        /// <summary>Top-of-screen overlay. Built on the first stall or drop, hidden the rest of the time.</summary>
        GameObject _alertRoot;

        /// <summary>Uppercase alarm title on <see cref="_alertRoot"/>.</summary>
        TextMeshProUGUI _alertTitle;

        /// <summary>One-line explanation under the title.</summary>
        TextMeshProUGUI _alertBody;

        /// <summary>Thin top rail. Orange while commands are unacked, red once snapshots stop.</summary>
        Image _alertRail;

        /// <summary>
        /// Creates the hidden runner after the first scene loads. Dedicated server builds
        /// skip it — there is no local player to warn.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstalled()
        {
#if UNITY_SERVER
            return;
#else
            if (FindAnyObjectByType<ClientLinkStatusHUD>() != null)
                return;

            var go = new GameObject(nameof(ClientLinkStatusHUD));
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<ClientLinkStatusHUD>();
#endif
        }

        void OnDestroy()
        {
            DisposeQueries();
        }

        void Update()
        {
#if UNITY_SERVER
            return;
#else
            // --- Throttle ---
            // [TITAN-ORBIT] Reading the client world every frame is wasted work, and a
            // fresh EntityQuery each time leaked the WebGL heap (see IsClientConnectionReady).
            if (Time.unscaledTime < _nextSampleTime)
                return;
            _nextSampleTime = Time.unscaledTime + SampleIntervalSeconds;
            SampleLink();
#endif
        }

        /// <summary>
        /// One read of the client connection, the snapshot clock, and command age.
        /// Shows LAG while packets are late, LINK STALLED when the server stops
        /// acknowledging commands, and LINK LOST once snapshots stop. A lost link
        /// that holds starts the menu reset. A stall does not — snapshots are
        /// still moving, and kicking the player would be the disconnect itself.
        /// </summary>
        void SampleLink()
        {
            // --- Not in a match ---
            // Main menu and the post-drop reset both clear IsInGame. Hide the chip and
            // forget the old snapshot so the next join does not inherit a "lost" clock.
            TitanOrbitSessionManager session = TitanOrbitSessionManager.Instance;
            if (session == null || !session.IsInGame)
            {
                ResetMatchMemory();
                Apply(LinkFeel.Off);
                return;
            }

            World world = ClientServerBootstrap.ClientWorld;
            bool ready = world != null && world.IsCreated && TitanOrbitSessionManager.IsClientGameplayReady(world);
            if (ready)
                _wasReady = true;

            // Connection entity gone after we had one. This is a real NetCode drop
            // (the 90-frame menu reset has not run yet). Say so immediately.
            if (_wasReady && !ready)
            {
                _lagStreak = 0;
                _clearStreak = 0;
                _stallStreak = 0;
                NoteUnrecoverable(session);
                Apply(LinkFeel.Lost);
                return;
            }

            if (!ready || !EnsureQueries(world))
            {
                if (_seenSnapshot && _shown == LinkFeel.Lost)
                {
                    NoteUnrecoverable(session);
                    Apply(LinkFeel.Lost);
                }
                else
                    Apply(LinkFeel.Off);
                return;
            }

            bool hasAck = _ackQuery.TryGetSingleton<NetworkSnapshotAck>(out NetworkSnapshotAck ack);
            bool hasTime = _timeQuery.TryGetSingleton<NetworkTimeSystemData>(out NetworkTimeSystemData timeData);
            if (!hasAck || !hasTime || !timeData.latestSnapshot.IsValid)
            {
                // Still joining — no snapshot yet is normal. A live connection still
                // shows green. Only a later stall, after we have seen a tick, stays red.
                if (_seenSnapshot && _shown == LinkFeel.Lost)
                {
                    NoteUnrecoverable(session);
                    Apply(LinkFeel.Lost);
                }
                else
                    Apply(LinkFeel.Connected);
                return;
            }

            float now = Time.unscaledTime;
            if (!_seenSnapshot || timeData.latestSnapshot != _lastSnapshot)
            {
                _seenSnapshot = true;
                _lastSnapshot = timeData.latestSnapshot;
                _lastSnapshotAdvanceTime = now;
            }

            float silence = now - _lastSnapshotAdvanceTime;
            float rtt = ack.EstimatedRTT;
            float jitter = ack.DeviationRTT;
            float gapMs = timeData.avgPacketInterArrival;
            // ServerCommandAge is 16.16 fixed-point ticks. Healthy dedicated play sits near −2.
            float commandAge = ack.ServerCommandAge / 256f;

            // --- Link is gone ---
            // The connection component can still say "in game" while the socket is dead.
            // Prediction keeps the local ship moving; turrets stay on the last snapshot.
            if (silence >= LostSilenceSeconds)
            {
                _lagStreak = 0;
                _clearStreak = 0;
                _stallStreak = 0;
                NoteUnrecoverable(session);
                Apply(LinkFeel.Lost);
                return;
            }

            // Snapshots moved within the last two seconds. This is not a dead socket.
            // A command stall can still be up; that banner stays until commands land again.
            if (!_resetRequested)
                _lostSince = 0f;

            // A new snapshot after a drop means the server is talking again.
            // Fall through so the bars become green or amber instead of vanishing.
            // Leaving the match never reaches here — IsInGame is already false.
            if (_shown == LinkFeel.Lost)
            {
                _lagStreak = 0;
                _clearStreak = 0;
                Debug.Log("[ClientLink] Server snapshots resumed.");
            }

            // --- Commands are not landing ---
            // Snapshots can keep a ~60 Hz average for several seconds after the
            // client stops being acknowledged. That is the stretch where flight
            // feels broken and the pip still looks fine.
            if (commandAge >= CommandStallTicks)
                _stallStreak++;
            else if (commandAge < CommandStallClearTicks)
                _stallStreak = 0;

            bool stalled = _stallStreak >= EnterStallSamples
                || (_shown == LinkFeel.Stalled && commandAge >= CommandStallClearTicks);
            if (stalled)
            {
                Apply(LinkFeel.Stalled);
                return;
            }

            bool late = silence >= LagSilenceSeconds
                || rtt >= LagRttMs
                || jitter >= LagJitterMs
                || gapMs >= LagPacketGapMs;
            bool calm = silence < LagSilenceSeconds * 0.5f
                && rtt < ClearRttMs
                && jitter < ClearJitterMs
                && gapMs < ClearPacketGapMs;

            if (late)
            {
                _clearStreak = 0;
                _lagStreak++;
            }
            else if (calm)
            {
                _lagStreak = 0;
                _clearStreak++;
            }

            if (_shown == LinkFeel.Lag && _clearStreak < ClearLagSamples)
            {
                Apply(LinkFeel.Lag);
                return;
            }

            if (_lagStreak >= EnterLagSamples)
            {
                Apply(LinkFeel.Lag);
                return;
            }

            // Healthy match. The icon stays on — a hidden "all good" was invisible
            // for the whole flight, which is why the old chip never showed up.
            Apply(LinkFeel.Connected);
        }

        /// <summary>
        /// Starts the menu reset once a dead link has held long enough to skip a hitch.
        /// A command stall does not call this. Snapshots are still arriving, and
        /// ending the session would disconnect a player the server is still talking to.
        /// </summary>
        /// <param name="session">Live session. Already known to be in a match.</param>
        void NoteUnrecoverable(TitanOrbitSessionManager session)
        {
            if (_resetRequested)
                return;

            float now = Time.unscaledTime;
            if (_lostSince <= 0f)
                _lostSince = now;
            if (now - _lostSince < LostHoldBeforeResetSeconds)
                return;

            _resetRequested = true;
            session.NotifyDedicatedLinkUnrecoverable();
        }

        /// <summary>Forgets snapshot memory when the match ends so the next join starts clean.</summary>
        void ResetMatchMemory()
        {
            _wasReady = false;
            _seenSnapshot = false;
            _lastSnapshot = NetworkTick.Invalid;
            _lastSnapshotAdvanceTime = 0f;
            _lagStreak = 0;
            _clearStreak = 0;
            _stallStreak = 0;
            _lostSince = 0f;
            _resetRequested = false;
        }

        /// <summary>
        /// Shows or hides the signal pip and the failure banner.
        /// The banner is shown even when the leaderboard pip cannot be built yet.
        /// </summary>
        /// <param name="feel">Off, connected, late, stalled, or dropped.</param>
        void Apply(LinkFeel feel)
        {
            if (feel == LinkFeel.Off)
            {
                _shown = LinkFeel.Off;
                if (_icon != null && _icon.activeSelf)
                    _icon.SetActive(false);
                HideAlert();
                return;
            }

            bool changed = feel != _shown;
            if (changed && feel == LinkFeel.Lost)
            {
                Debug.LogWarning(
                    "[ClientLink] No live server link. Local flight can continue on prediction; " +
                    "server ghosts such as enemy turrets stay on the last snapshot.");
            }
            else if (changed && feel == LinkFeel.Stalled)
            {
                Debug.LogWarning(
                    "[ClientLink] Server stopped acknowledging ship commands. " +
                    "Prediction is still running; controls may not be reaching the match.");
            }

            _shown = feel;
            // Leaderboard may not exist yet. The banner does not wait on it —
            // the pip is parented there and fades with the station UI.
            if (EnsureIcon())
            {
                if (!_icon.activeSelf)
                    _icon.SetActive(true);
                PaintBars(feel);
            }

            if (feel == LinkFeel.Lost || feel == LinkFeel.Stalled)
                ShowAlert(feel);
            else
                HideAlert();
        }

        /// <summary>
        /// Lights the bars for one state. Connected fills all three, lag and a
        /// command stall fill two, a drop leaves only the shortest bar.
        /// </summary>
        /// <param name="feel">Connected, lag, stalled, or lost. Off is not painted.</param>
        void PaintBars(LinkFeel feel)
        {
            Color lit;
            int litCount;
            if (feel == LinkFeel.Lost)
            {
                lit = LostLit;
                litCount = 1;
            }
            else if (feel == LinkFeel.Stalled)
            {
                lit = StallLit;
                litCount = 2;
            }
            else if (feel == LinkFeel.Lag)
            {
                lit = LagLit;
                litCount = 2;
            }
            else
            {
                lit = ConnectedLit;
                litCount = 3;
            }

            for (int i = 0; i < _bars.Length; i++)
            {
                if (_bars[i] != null)
                    _bars[i].color = i < litCount ? lit : BarDim;
            }
        }

        /// <summary>
        /// Puts the failure card on screen. Built once, then only the words and the
        /// rail color change. Sits above the orbit station (sort 200) and under the
        /// loading overlay (sort 500), so a station menu cannot hide it and a
        /// scene load still covers it.
        /// </summary>
        /// <param name="feel"><see cref="LinkFeel.Lost"/> or <see cref="LinkFeel.Stalled"/>.</param>
        void ShowAlert(LinkFeel feel)
        {
            EnsureAlert();
            if (!_alertRoot.activeSelf)
                _alertRoot.SetActive(true);

            if (feel == LinkFeel.Stalled)
            {
                _alertRail.color = StallLit;
                _alertTitle.text = "LINK STALLED";
                _alertBody.text = "SERVER IS NOT TAKING YOUR CONTROLS";
                return;
            }

            _alertRail.color = LostLit;
            _alertTitle.text = "LINK LOST";
            _alertBody.text = _resetRequested
                ? "RETURNING TO THE MENU — JOIN THE MATCH AGAIN"
                : "MATCH SERVER STOPPED ANSWERING";
        }

        /// <summary>Hides the failure card. Healthy play and the main menu leave it off.</summary>
        void HideAlert()
        {
            if (_alertRoot != null && _alertRoot.activeSelf)
                _alertRoot.SetActive(false);
        }

        /// <summary>
        /// Builds the top banner the first time the link fails. Its own canvas, not a
        /// child of the leaderboard — that plate fades while the orbit station is open,
        /// which is when a tiny pip is easiest to miss.
        /// </summary>
        void EnsureAlert()
        {
            if (_alertRoot != null)
                return;

            _alertRoot = new GameObject("LinkAlert", typeof(RectTransform));
            _alertRoot.transform.SetParent(transform, false);

            var canvas = _alertRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 280;
            var scaler = _alertRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var plateGo = new GameObject("Plate", typeof(RectTransform), typeof(Image));
            plateGo.transform.SetParent(_alertRoot.transform, false);
            var plateRt = plateGo.GetComponent<RectTransform>();
            plateRt.anchorMin = new Vector2(0.22f, 1f);
            plateRt.anchorMax = new Vector2(0.78f, 1f);
            plateRt.pivot = new Vector2(0.5f, 1f);
            plateRt.anchoredPosition = new Vector2(0f, -10f);
            plateRt.sizeDelta = new Vector2(0f, 58f);
            var plate = plateGo.GetComponent<Image>();
            plate.color = new Color(PlateFill.r, PlateFill.g, PlateFill.b, 0.94f);
            plate.raycastTarget = false;

            var railGo = new GameObject("Rail", typeof(RectTransform), typeof(Image));
            railGo.transform.SetParent(plateGo.transform, false);
            var railRt = railGo.GetComponent<RectTransform>();
            railRt.anchorMin = new Vector2(0f, 1f);
            railRt.anchorMax = new Vector2(1f, 1f);
            railRt.pivot = new Vector2(0.5f, 1f);
            railRt.sizeDelta = new Vector2(0f, 2f);
            _alertRail = railGo.GetComponent<Image>();
            _alertRail.color = LostLit;
            _alertRail.raycastTarget = false;

            _alertTitle = CreateAlertLine(plateGo.transform, "Title", 16f, FontStyles.Bold, AlertTitle, -8f, 20f);
            _alertBody = CreateAlertLine(plateGo.transform, "Body", 13f, FontStyles.Normal, AlertBody, -30f, 18f);
        }

        /// <summary>
        /// One centered telemetry line on the failure card.
        /// </summary>
        /// <param name="parent">Plate the line sits on.</param>
        /// <param name="objectName">Child name, for the hierarchy.</param>
        /// <param name="fontSize">Point size.</param>
        /// <param name="style">Bold for the title, regular for the line under it.</param>
        /// <param name="color">Title is warm near-white; the body is the cockpit ice color.</param>
        /// <param name="top">Y offset down from the plate's top edge.</param>
        /// <param name="height">Line height in canvas pixels.</param>
        /// <returns>The label, ready for text.</returns>
        static TextMeshProUGUI CreateAlertLine(
            Transform parent,
            string objectName,
            float fontSize,
            FontStyles style,
            Color color,
            float top,
            float height)
        {
            var go = new GameObject(objectName, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, top);
            rt.sizeDelta = new Vector2(-16f, height);

            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
            return tmp;
        }

        /// <summary>
        /// Builds the pip on the leaderboard's top-left, just outside the plate.
        /// The pip has no canvas of its own. The failure card is <c>LinkAlert</c>,
        /// a separate overlay that stays hidden until the link stalls or drops.
        /// </summary>
        /// <returns>False when the leaderboard panel is not in the scene yet.</returns>
        bool EnsureIcon()
        {
            if (_icon != null)
                return true;

            StripLegacyBanner();

            RectTransform panel = ResolveLeaderboardPanel();
            if (panel == null)
                return false;

            _icon = new GameObject("LinkStatusIcon", typeof(RectTransform), typeof(Image));
            _icon.transform.SetParent(panel, false);
            var rt = _icon.GetComponent<RectTransform>();
            // Panel pivot is top-right. Anchor (0, 1) is its top-left corner.
            // Pivot top-right plus a negative X parks the pip just left of that corner.
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-GapLeftOfLeaderboard, 0f);
            rt.sizeDelta = new Vector2(IconWidth, IconHeight);
            rt.localScale = Vector3.one;

            var plate = _icon.GetComponent<Image>();
            plate.color = PlateFill;
            plate.raycastTarget = false;

            // Bottom-aligned bars, shortest on the left. Width stays inside the 12px plate.
            float barWidth = 2f;
            float gap = 1f;
            float[] heights = { 3f, 5f, 7f };
            float total = _bars.Length * barWidth + (_bars.Length - 1) * gap;
            float start = (IconWidth - total) * 0.5f;
            for (int i = 0; i < _bars.Length; i++)
            {
                var barGo = new GameObject("Bar" + i, typeof(RectTransform), typeof(Image));
                barGo.transform.SetParent(_icon.transform, false);
                var barRt = barGo.GetComponent<RectTransform>();
                barRt.anchorMin = new Vector2(0f, 0f);
                barRt.anchorMax = new Vector2(0f, 0f);
                barRt.pivot = new Vector2(0f, 0f);
                barRt.anchoredPosition = new Vector2(start + i * (barWidth + gap), 1f);
                barRt.sizeDelta = new Vector2(barWidth, heights[i]);
                Image bar = barGo.GetComponent<Image>();
                bar.raycastTarget = false;
                bar.color = BarDim;
                _bars[i] = bar;
            }

            _icon.transform.SetAsLastSibling();
            return true;
        }

        /// <summary>
        /// Finds the leaderboard plate this pip hangs off. Cached until that object dies.
        /// </summary>
        RectTransform ResolveLeaderboardPanel()
        {
            if (_leaderboard == null)
                _leaderboard = FindAnyObjectByType<TeamLeaderboardHUD>();
            if (_leaderboard == null)
                return null;

            Transform panel = _leaderboard.transform.Find("TeamLeaderboardPanel");
            return panel as RectTransform;
        }

        /// <summary>
        /// Removes the old full-width top-center label and the canvas that carried it.
        /// A script reload in the same Play session would otherwise leave that banner up.
        /// </summary>
        void StripLegacyBanner()
        {
            Transform legacy = transform.Find("LinkChip");
            if (legacy != null)
                Destroy(legacy.gameObject);

            Canvas canvas = GetComponent<Canvas>();
            if (canvas != null)
                Destroy(canvas);
            CanvasScaler scaler = GetComponent<CanvasScaler>();
            if (scaler != null)
                Destroy(scaler);
            CanvasGroup group = GetComponent<CanvasGroup>();
            if (group != null)
                Destroy(group);
        }

        /// <summary>
        /// Keeps one ack query and one time query for the current client world.
        /// Creating a query every sample used to grow the WebGL heap until Chrome died.
        /// </summary>
        /// <param name="world">Live <see cref="ClientServerBootstrap.ClientWorld"/>.</param>
        /// <returns>True when both queries can be read.</returns>
        bool EnsureQueries(World world)
        {
            if (world == null || !world.IsCreated)
            {
                DisposeQueries();
                return false;
            }

            if (_queriesReady && _queryWorld == world)
                return true;

            DisposeQueries();
            EntityManager em = world.EntityManager;
            _ackQuery = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkSnapshotAck>());
            _timeQuery = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTimeSystemData>());
            _queryWorld = world;
            _queriesReady = true;
            return true;
        }

        /// <summary>
        /// Drops the cached queries. A destroyed world has already freed them — disposing
        /// again throws — so we only dispose while that world is still created.
        /// </summary>
        void DisposeQueries()
        {
            if (_queriesReady && _queryWorld != null && _queryWorld.IsCreated)
            {
                _ackQuery.Dispose();
                _timeQuery.Dispose();
            }

            _queriesReady = false;
            _queryWorld = null;
        }
    }
}
