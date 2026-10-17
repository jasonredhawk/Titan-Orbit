using TitanOrbit.NetCode;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Small signal icon in the top-right, just left of the team leaderboard.
    /// Three bars: green when the match link is healthy, amber when it is late,
    /// one red bar when snapshots have stopped. Presentation only — it never
    /// sends RPCs or changes the connection.
    /// <para>
    /// A dead Relay socket often never raises <see cref="ConnectionState.State.Disconnected"/>.
    /// <see cref="TitanOrbitSessionManager.IsClientGameplayReady"/> stays true, so the
    /// existing drop watcher never runs. Local prediction keeps moving the ship for a
    /// while, and server-owned ghosts (enemy turrets) freeze on the last snapshot.
    /// The icon stays up for the whole match so that stall is visible at a glance.
    /// </para>
    /// <para>
    /// It is a child of <see cref="TeamLeaderboardHUD"/>'s panel, not its own canvas.
    /// A separate overlay drew over the orbit menu and sat in the middle of the screen,
    /// and it hid whenever the link was fine — so a normal match showed nothing.
    /// The leaderboard plate fades this icon with the rest of the gameplay HUD.
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
        static readonly Color BarDim = new Color(0.22f, 0.26f, 0.32f, 0.55f);

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

        /// <summary>Leaderboard we last parented under. Cleared when that object is destroyed.</summary>
        TeamLeaderboardHUD _leaderboard;

        /// <summary>Signal plate. A child of the leaderboard panel, sitting just outside its top-left.</summary>
        GameObject _icon;

        /// <summary>Short, middle, and tall bars. Index 0 is the shortest (left).</summary>
        readonly Image[] _bars = new Image[3];

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
        /// One read of the client connection and the snapshot clock.
        /// Shows LAG while packets are late, and NO LINK once snapshots stop or the
        /// connection entity disappears. Clears itself when a new snapshot arrives.
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
                Apply(LinkFeel.Lost);
                return;
            }

            if (!ready || !EnsureQueries(world))
            {
                Apply(_shown == LinkFeel.Lost ? LinkFeel.Lost : LinkFeel.Off);
                return;
            }

            bool hasAck = _ackQuery.TryGetSingleton<NetworkSnapshotAck>(out NetworkSnapshotAck ack);
            bool hasTime = _timeQuery.TryGetSingleton<NetworkTimeSystemData>(out NetworkTimeSystemData timeData);
            if (!hasAck || !hasTime || !timeData.latestSnapshot.IsValid)
            {
                // Still joining — no snapshot yet is normal. A live connection still
                // shows green. Only a later stall, after we have seen a tick, stays red.
                if (_seenSnapshot && _shown == LinkFeel.Lost)
                    Apply(LinkFeel.Lost);
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

            // --- Link is gone ---
            // The connection component can still say "in game" while the socket is dead.
            // Prediction keeps the local ship moving; turrets stay on the last snapshot.
            if (silence >= LostSilenceSeconds)
            {
                _lagStreak = 0;
                _clearStreak = 0;
                Apply(LinkFeel.Lost);
                return;
            }

            // A new snapshot after a drop means the server is talking again.
            // Fall through so the bars become green or amber instead of vanishing.
            // Leaving the match never reaches here — IsInGame is already false.
            if (_shown == LinkFeel.Lost)
            {
                _lagStreak = 0;
                _clearStreak = 0;
                Debug.Log("[ClientLink] Server snapshots resumed.");
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

        /// <summary>Forgets snapshot memory when the match ends so the next join starts clean.</summary>
        void ResetMatchMemory()
        {
            _wasReady = false;
            _seenSnapshot = false;
            _lastSnapshot = NetworkTick.Invalid;
            _lastSnapshotAdvanceTime = 0f;
            _lagStreak = 0;
            _clearStreak = 0;
        }

        /// <summary>
        /// Shows or hides the signal pip and repaints the bars when the state changes.
        /// </summary>
        /// <param name="feel">Off, connected, late, or dropped.</param>
        void Apply(LinkFeel feel)
        {
            if (feel == LinkFeel.Off)
            {
                _shown = LinkFeel.Off;
                if (_icon != null && _icon.activeSelf)
                    _icon.SetActive(false);
                return;
            }

            // Leaderboard may not exist yet (loading). Do not latch the color
            // until the pip actually exists, or a later sample would skip the build.
            if (!EnsureIcon())
                return;

            bool changed = feel != _shown;
            if (feel == LinkFeel.Lost && changed)
            {
                Debug.LogWarning(
                    "[ClientLink] No live server link. Local flight can continue on prediction; " +
                    "server ghosts such as enemy turrets stay on the last snapshot.");
            }

            _shown = feel;
            if (!_icon.activeSelf)
                _icon.SetActive(true);
            if (changed)
                PaintBars(feel);
        }

        /// <summary>
        /// Lights the bars for one state. Connected fills all three, lag fills two,
        /// a drop leaves only the shortest bar.
        /// </summary>
        /// <param name="feel">Connected, lag, or lost. Off is not painted.</param>
        void PaintBars(LinkFeel feel)
        {
            Color lit = feel == LinkFeel.Lost ? LostLit
                : feel == LinkFeel.Lag ? LagLit
                : ConnectedLit;
            int litCount = feel == LinkFeel.Lost ? 1 : feel == LinkFeel.Lag ? 2 : 3;
            for (int i = 0; i < _bars.Length; i++)
            {
                if (_bars[i] != null)
                    _bars[i].color = i < litCount ? lit : BarDim;
            }
        }

        /// <summary>
        /// Builds the pip on the leaderboard's top-left, just outside the plate.
        /// No extra canvas — a private overlay was the wide banner in the middle
        /// of the screen, including during load.
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
