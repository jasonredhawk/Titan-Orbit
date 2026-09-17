using System.Collections.Generic;
using Shapes;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.NetCode;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] World-space keyword chips above a speaking ship. One speaker bubble per
    /// <c>GhostOwner.NetworkId</c> — a new callout replaces the old one. Commander
    /// sentences that tag Everyone / Us / You also plant the same gold chips
    /// <b>below</b> each tagged hull (past the nameplate) so the order is readable
    /// on every affected ship.
    /// <para>
    /// Client presentation only. Driven by <see cref="ShipCommsInbox"/> (RPC echo) and by
    /// <see cref="Show"/> for the speaker's optimistic local preview. Chip frames stay
    /// steel unless that word owns a color (Purple, Red, Heal, Attack, …). One plate
    /// outlines the whole row: white for All, the speaker's team color for Team,
    /// command gold for Commander.
    /// Anchors through <see cref="ShipWeaponProxyRegistry"/> so we follow the wrapped hull
    /// transform (display = sim; no extra wrap tiles).
    /// </para>
    /// Regular nameplates sit world −Z (screen-below). These chips sit world +Z (screen-above)
    /// on the same play-plane rotation (<c>Euler(-90,0,0)</c>, facing +Y) in gameplay. Theatrical
    /// idle orbit billboards them at the lens like nameplates and scales by camera-to-hull
    /// distance so remote callouts stay readable. A thin Shapes billboard line in the
    /// speaker's team color ties the chip row back to the hull.
    /// Borders are a sliced AA frame Image — not UGUI <c>Outline</c>, which crawls while the
    /// ship flies. Execution order 67012: after <see cref="EcsWorldVisualizer"/> and nameplates.
    /// </summary>
    [DefaultExecutionOrder(67012)]
    public sealed class ShipCommsBubblePresenter : ImmediateModeShapeDrawer
    {
        const float LifetimeSeconds = 4f;
        const float FadeSeconds = 0.65f;
        /// <summary>
        /// Same tiny Y lift as <see cref="ShipWorldNameplate"/> so gameplay chips sit on the
        /// play plane. Theatrical mode keeps this lift and changes rotation plus scale.
        /// </summary>
        const float HeightAbovePlane = 0.08f;

        /// <summary>Gap past the hull edge on the screen-above (+Z) side, beyond the chip half-height.</summary>
        const float PaddingPastHull = 0.7f;
        /// <summary>
        /// Extra world −Z so commander echo chips sit past the nameplate (nameplates
        /// cap around 1.6 units below the hull). Without this the gold row covers the plate.
        /// </summary>
        const float EchoBelowNameplatePad = 1.75f;

        const float FallbackXzRadius = 0.7f;
        const float WorldCanvasScale = 0.013f;

        /// <summary>
        /// Fallback L1 camera height. Theatrical chips grow with camera-to-hull distance
        /// over this so on-screen size matches gameplay when the lens is that far away.
        /// Live value comes from <see cref="CameraFollowEcs.Settings.heightAtLevel1"/>.
        /// </summary>
        const float TheatricalBillboardRefDistance = 25f;

        /// <summary>Close-up floor so a crane-in cannot shrink chips to unreadable.</summary>
        const float TheatricalBillboardScaleMin = 0.4f;

        /// <summary>Far-ship cap so a map-wide speaker cannot spawn a giant world canvas.</summary>
        const float TheatricalBillboardScaleMax = 8f;

        /// <summary>Screen-pixel thickness so the leader stays thin from top-down and theatrical.</summary>
        const float LeaderLineThicknessPixels = 1.8f;

        /// <summary>
        /// Nameplates use world −Z as screen-below. Chips sit on the opposite side so they
        /// read as “above the ship” without covering the plate.
        /// </summary>
        static readonly Vector3 ScreenAboveWorld = new Vector3(0f, 0f, 1f);

        /// <summary>
        /// Same wide HUD button as <c>ShipCommsPanel</c> tiles (100×26), not a square.
        /// Long words grow past this minimum so NO PROBLEM / TRANSPORT stay on one line.
        /// </summary>
        const float ChipWidth = 100f;
        const float ChipHeight = 26f;
        const float ChipGap = 4f;
        const float ChipPadX = 8f;
        const float FrameInset = 2f;
        /// <summary>Gap so the All / Team plate reads as one outline around every chip.</summary>
        const float ChannelPad = 4f;
        const int WorldSortingOrder = 5010;

        static readonly Color ChipFill = new Color(0.03f, 0.05f, 0.09f, 0.96f);
        static readonly Color ChipFrame = ShipCommsCalloutGraphics.ChipNeutralFrame;
        static readonly Color ChipText = new Color(0.88f, 0.92f, 0.98f, 1f);
        static readonly Color ChipOutline = new Color(0.02f, 0.04f, 0.08f, 0.85f);
        static readonly Color ChipCaret = new Color(0.35f, 0.72f, 0.95f, 0.95f);
        static readonly Color ChipAccent = ShipCommsCalloutGraphics.ChipDefaultAccent;
        static readonly Color AllChannelFrame = new Color(1f, 1f, 1f, 0.92f);

        static ShipCommsBubblePresenter s_Instance;
        static Sprite s_PlateSprite;

        readonly Dictionary<int, Bubble> _live = new Dictionary<int, Bubble>(16);
        /// <summary>Commander echo chips keyed by the tagged ship's NetworkId (not the speaker).</summary>
        readonly Dictionary<int, Bubble> _echo = new Dictionary<int, Bubble>(16);
        readonly int[] _tagScratch = new int[ShipCommsCalloutGraphics.MaxTaggedPlayers];
        readonly List<int> _deadIds = new List<int>(8);
        Camera _cachedCamera;

        /// <summary>One player's active chip row.</summary>
        sealed class Bubble
        {
            public int NetworkId;
            public GameObject Root;
            public Canvas WorldCanvas;
            public RectTransform CanvasRect;
            public CanvasGroup Group;
            public Image ChannelFrame;
            public readonly TextMeshProUGUI[] Labels = new TextMeshProUGUI[ShipCommsKeywordCatalog.MaxSequenceLength];
            public readonly GameObject[] Chips = new GameObject[ShipCommsKeywordCatalog.MaxSequenceLength];
            public readonly LayoutElement[] ChipLayouts = new LayoutElement[ShipCommsKeywordCatalog.MaxSequenceLength];
            public readonly RectTransform[] ChipRects = new RectTransform[ShipCommsKeywordCatalog.MaxSequenceLength];
            public readonly Image[] Frames = new Image[ShipCommsKeywordCatalog.MaxSequenceLength];
            public readonly Image[] Carets = new Image[ShipCommsKeywordCatalog.MaxSequenceLength];
            public readonly Image[] Fills = new Image[ShipCommsKeywordCatalog.MaxSequenceLength];
            public float Age;
            public byte Count;
            public byte TeamOnly;
            public bool HasLocalCenter;
            public Vector3 LocalXzCenter;
            public TeamId Team;
            public Color LineColor;
            public Vector3 LineFrom;
            public Vector3 LineTo;
            public bool HasLine;
            public ShipCommsInbox.Callout Callout;
            /// <summary>True when this row is a commander echo under a tagged hull.</summary>
            public bool IsEcho;
            /// <summary>Speaker who issued the order. Used to drop stale echoes from the same commander.</summary>
            public int SourceNetworkId;
            /// <summary>True = world −Z (below nameplate). False = world +Z (above hull).</summary>
            public bool ScreenBelow;
        }

        /// <summary>
        /// [UNITY] Creates the presenter once after the first scene load.
        /// Do not wrap this in <c>#if UNITY_SERVER</c> — the Editor Dedicated Server build
        /// target defines that symbol and would strip chips in Play Mode.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            if (FindFirstObjectByType<ShipCommsBubblePresenter>() != null)
                return;

            var go = new GameObject(nameof(ShipCommsBubblePresenter));
            DontDestroyOnLoad(go);
            go.AddComponent<ShipCommsBubblePresenter>();
        }

        /// <summary>Caches the singleton so <see cref="Show"/> can run before the first LateUpdate.</summary>
        void Awake()
        {
            s_Instance = this;
        }

        /// <summary>Drops live bubbles so a second Play does not keep stale world canvases.</summary>
        void OnDestroy()
        {
            if (s_Instance == this)
                s_Instance = null;

            foreach (var pair in _live)
            {
                if (pair.Value?.Root != null)
                    Destroy(pair.Value.Root);
            }

            foreach (var pair in _echo)
            {
                if (pair.Value?.Root != null)
                    Destroy(pair.Value.Root);
            }

            _live.Clear();
            _echo.Clear();
        }

        /// <summary>
        /// Paints chips above the speaker's hull. Replaces any bubble already showing
        /// for that player and restarts the 4s timer.
        /// </summary>
        public static void Show(in ShipCommsInbox.Callout callout)
        {
            if (s_Instance == null)
                EnsureExists();
            if (s_Instance == null)
                return;

            s_Instance.ApplyCallout(callout);
        }

        /// <summary>
        /// Copies live comms path segments (world XZ) for the minimap overlay.
        /// No alloc; respects the same on/off pulse as the world lines.
        /// </summary>
        public static int CopyLivePathSegments(
            Vector3[] from, Vector3[] to, Color[] colors, int[] ranks, int max)
        {
            if (s_Instance == null || from == null || to == null || colors == null || max <= 0)
                return 0;

            int written = 0;
            foreach (var pair in s_Instance._live)
            {
                Bubble bubble = pair.Value;
                if (bubble == null || written >= max)
                    break;
                if (bubble.Age >= LifetimeSeconds)
                    continue;

                written += ShipCommsCalloutGraphics.CopyVisiblePaths(
                    in bubble.Callout, bubble.Age, from, to, colors, ranks, written, max);
            }

            return written;
        }

        /// <summary>
        /// Drains the RPC inbox, then parks every live bubble above its hull.
        /// </summary>
        void LateUpdate()
        {
            // --- Inbox ---
            // [HYBRID] Client simulation enqueued rows; we Instantiates UI on the main thread.
            while (ShipCommsInbox.TryDequeue(out ShipCommsInbox.Callout callout))
                ApplyCallout(callout);

            if (_live.Count <= 0 && _echo.Count <= 0)
                return;

            if (_cachedCamera == null)
                _cachedCamera = Camera.main;

            float dt = Time.deltaTime;
            _deadIds.Clear();

            TickBubbles(_live, destroyEcho: false, dt);
            TickBubbles(_echo, destroyEcho: true, dt);
        }

        /// <summary>
        /// Ages one bubble map, fades the last slice, and follows hulls. Dead keys
        /// are destroyed after the walk so we never mutate the dictionary mid-foreach.
        /// </summary>
        /// <param name="map">Speaker bubbles or commander echoes.</param>
        /// <param name="destroyEcho">True when <paramref name="map"/> is <see cref="_echo"/>.</param>
        /// <param name="dt">Frame delta (seconds).</param>
        void TickBubbles(Dictionary<int, Bubble> map, bool destroyEcho, float dt)
        {
            if (map.Count <= 0)
                return;

            _deadIds.Clear();
            foreach (var pair in map)
            {
                Bubble bubble = pair.Value;
                if (bubble == null)
                {
                    _deadIds.Add(pair.Key);
                    continue;
                }

                bubble.Age += dt;

                // --- Expire ---
                if (bubble.Age >= LifetimeSeconds || bubble.Root == null)
                {
                    _deadIds.Add(pair.Key);
                    continue;
                }

                // --- Fade on the last slice of the lifetime ---
                if (bubble.Group != null)
                {
                    float fadeStart = LifetimeSeconds - FadeSeconds;
                    float alpha = bubble.Age < fadeStart
                        ? 1f
                        : 1f - Mathf.Clamp01((bubble.Age - fadeStart) / FadeSeconds);
                    bubble.Group.alpha = alpha;
                }

                if (!TryFollowHull(bubble))
                    _deadIds.Add(pair.Key);
            }

            for (int i = 0; i < _deadIds.Count; i++)
            {
                if (destroyEcho)
                    DestroyEcho(_deadIds[i]);
                else
                    DestroyBubble(_deadIds[i]);
            }
        }

        /// <summary>
        /// Creates or recycles the speaker's above-hull chips, then plants commander
        /// echo chips under every tagged teammate (Everyone / Us / You).
        /// </summary>
        void ApplyCallout(in ShipCommsInbox.Callout callout)
        {
            if (callout.NetworkId <= 0 || callout.Count < 1)
                return;

            PaintBubbleMap(
                _live,
                callout.NetworkId,
                in callout,
                screenBelow: false,
                sourceNetworkId: callout.NetworkId);
            ApplyCommanderEchoes(in callout);
        }

        /// <summary>
        /// Commander channel only: copy the sentence under each tagged hull. A new
        /// order from the same speaker drops their previous echo set so Us cannot
        /// leave stale Everyone chips behind.
        /// </summary>
        void ApplyCommanderEchoes(in ShipCommsInbox.Callout callout)
        {
            ShipCommsChannel channel = TeamCommanderRules.Sanitize(callout.TeamOnly);
            if (channel != ShipCommsChannel.Commander)
                return;

            int tagged = ShipCommsCalloutGraphics.CollectTaggedPlayerIds(in callout, _tagScratch);

            // --- Drop this commander's leftover targets ---
            _deadIds.Clear();
            foreach (var pair in _echo)
            {
                Bubble existing = pair.Value;
                if (existing == null || existing.SourceNetworkId != callout.NetworkId)
                    continue;
                if (!ContainsNetworkId(_tagScratch, tagged, pair.Key))
                    _deadIds.Add(pair.Key);
            }

            for (int i = 0; i < _deadIds.Count; i++)
                DestroyEcho(_deadIds[i]);

            for (int i = 0; i < tagged; i++)
            {
                int targetId = _tagScratch[i];
                if (targetId <= 0)
                    continue;
                PaintBubbleMap(
                    _echo,
                    targetId,
                    in callout,
                    screenBelow: true,
                    sourceNetworkId: callout.NetworkId);
            }
        }

        /// <summary>True when <paramref name="id"/> is among the first <paramref name="count"/> slots.</summary>
        static bool ContainsNetworkId(int[] ids, int count, int id)
        {
            for (int i = 0; i < count; i++)
            {
                if (ids[i] == id)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Writes one chip row into <paramref name="map"/> at <paramref name="anchorId"/>
        /// (speaker hull or tagged hull). Recycles the GameObject when that key is live.
        /// </summary>
        void PaintBubbleMap(
            Dictionary<int, Bubble> map,
            int anchorId,
            in ShipCommsInbox.Callout callout,
            bool screenBelow,
            int sourceNetworkId)
        {
            if (anchorId <= 0)
                return;

            if (!map.TryGetValue(anchorId, out Bubble bubble) || bubble == null || bubble.Root == null)
            {
                bubble = CreateBubble(anchorId, screenBelow);
                map[anchorId] = bubble;
            }

            bubble.Age = 0f;
            bubble.Count = callout.Count;
            bubble.TeamOnly = callout.TeamOnly;
            bubble.Callout = callout;
            bubble.IsEcho = screenBelow;
            bubble.ScreenBelow = screenBelow;
            bubble.SourceNetworkId = sourceNetworkId;
            bubble.HasLocalCenter = false;
            if (bubble.Group != null)
                bubble.Group.alpha = 1f;

            var catalog = ShipCommsKeywordCatalog.LoadDefault();
            float width = 0f;
            width += ApplyChip(bubble, 0, callout.Count >= 1, callout.K0, catalog);
            width += ApplyChip(bubble, 1, callout.Count >= 2, callout.K1, catalog);
            width += ApplyChip(bubble, 2, callout.Count >= 3, callout.K2, catalog);
            width += ApplyChip(bubble, 3, callout.Count >= 4, callout.K3, catalog);
            width += ApplyChip(bubble, 4, callout.Count >= 5, callout.K4, catalog);
            width += Mathf.Max(0, callout.Count - 1) * ChipGap;
            if (bubble.CanvasRect != null)
                bubble.CanvasRect.sizeDelta = new Vector2(width + ChannelPad * 2f, ChipHeight + ChannelPad * 2f);
            PaintChannelFrame(bubble);

            TryFollowHull(bubble);
        }

        /// <summary>
        /// Shows or hides one chip, writes its label, and sizes it like a panel button.
        /// Returns the chip width used for the row (0 when hidden).
        /// </summary>
        static float ApplyChip(
            Bubble bubble, int slot, bool visible, byte index, ShipCommsKeywordCatalog catalog)
        {
            GameObject chip = bubble.Chips[slot];
            if (chip != null)
                chip.SetActive(visible);

            if (!visible || bubble.Labels[slot] == null)
                return 0f;

            string text = catalog.TryGetLabel(index, out string label) ? label : "?";
            if (ShipCommsCalloutGraphics.TryResolveHereDisplayLabel(in bubble.Callout, text, out string planetName))
                text = planetName;
            TextMeshProUGUI tmp = bubble.Labels[slot];
            tmp.text = text.ToUpperInvariant();

            // Same paint as a selected matrix tile so Attack stays grey+red font, Red stays faction wash.
            // Per-chip border is the word color only. All / Team is the row plate.
            ShipCommsCalloutGraphics.ResolveChipPaint(text, selected: true, out Color fill, out Color body, out Color accent);
            Color frame = ShipCommsCalloutGraphics.ResolveChipFrameColor(text);

            if (bubble.Frames[slot] != null)
                bubble.Frames[slot].color = frame;
            if (bubble.Carets[slot] != null)
            {
                bubble.Carets[slot].color = accent;
                bubble.Carets[slot].enabled = true;
            }
            if (bubble.Fills[slot] != null)
                bubble.Fills[slot].color = fill;
            tmp.color = body;
            tmp.ForceMeshUpdate();

            float width = Mathf.Max(ChipWidth, tmp.preferredWidth + ChipPadX * 2f);
            if (bubble.ChipLayouts[slot] != null)
            {
                bubble.ChipLayouts[slot].preferredWidth = width;
                bubble.ChipLayouts[slot].minWidth = width;
            }

            if (bubble.ChipRects[slot] != null)
                bubble.ChipRects[slot].sizeDelta = new Vector2(width, ChipHeight);

            return width;
        }

        /// <summary>
        /// Parks the bubble screen-above the hull. Gameplay stays flat on XZ like
        /// <see cref="ShipWorldNameplate"/>; theatrical idle orbit billboards at the lens.
        /// </summary>
        bool TryFollowHull(Bubble bubble)
        {
            if (!ShipWeaponProxyRegistry.TryGetHull(bubble.NetworkId, out Transform hull) || hull == null)
                return false;

            float xzRadius = FallbackXzRadius;
            if (ShipWeaponProxyRegistry.TryGetCachedHullClearance(bubble.NetworkId, out _, out float cachedXz)
                && cachedXz > 0.001f)
                xzRadius = Mathf.Max(FallbackXzRadius, cachedXz);

            EnsureLocalHullCenter(hull, bubble);
            Vector3 centerWorld = hull.TransformPoint(bubble.LocalXzCenter);

            if (_cachedCamera == null)
                _cachedCamera = Camera.main;

            // [TITAN-ORBIT] Gameplay: Euler −90 X lays the canvas on XZ. Theatrical: same
            // camera-facing billboard as nameplates / planet labels so the row stays readable
            // off the top-down plane. Always rewritten so leaving theatrical cannot leave
            // a leftover billboard.
            bool theatrical = TitanOrbit.UI.TheatricalWorldSpaceLabelRotation.IsTheatricalEngaged();
            Quaternion rot = theatrical
                ? TitanOrbit.UI.TheatricalWorldSpaceLabelRotation.BillboardRotationFacingCamera()
                : Quaternion.Euler(-90f, 0f, 0f);

            // Gameplay: height zoom (L2+ / MEGA). Theatrical: camera-to-this-hull distance
            // so close crane-ins stay modest and remote speakers stay readable.
            float scale = ResolveChipWorldScale(theatrical, _cachedCamera, centerWorld);
            float canvasH = bubble.CanvasRect != null ? bubble.CanvasRect.sizeDelta.y : ChipHeight + ChannelPad * 2f;
            float halfChipWorld = canvasH * scale * 0.5f;

            // [TITAN-ORBIT] Nameplates sit world −Z (screen-below). Speaker chips use +Z
            // so they do not cover the plate. Commander echoes use −Z plus a nameplate
            // reserve so the gold row sits under the plate, not on top of it.
            // Anchor XZ from mesh bounds center — hull.position is often off the visual midline.
            Vector3 side = bubble.ScreenBelow ? -ScreenAboveWorld : ScreenAboveWorld;
            float extra = bubble.ScreenBelow ? EchoBelowNameplatePad : 0f;
            Vector3 pos = centerWorld
                + side * (xzRadius + PaddingPastHull + extra + halfChipWorld);
            pos.y = centerWorld.y + HeightAbovePlane;

            bubble.Root.transform.SetPositionAndRotation(pos, rot);
            bubble.Root.transform.localScale = new Vector3(scale, -scale, scale);

            // Leader: chip near-edge → hull visual center. Same play-plane Y as the chips
            // so top-down stays a short XZ stem; theatrical still reads as “from the row.”
            Vector3 shipAnchor = centerWorld;
            shipAnchor.y = pos.y;
            Vector3 toChip = pos - shipAnchor;
            float stemLen = toChip.magnitude;
            if (stemLen > 0.02f)
            {
                float inset = Mathf.Min(halfChipWorld, stemLen * 0.45f);
                bubble.LineFrom = pos - toChip * (inset / stemLen);
                bubble.LineTo = shipAnchor;
                bubble.HasLine = true;
            }
            else
            {
                bubble.HasLine = false;
            }

            if (bubble.Team == TeamId.None)
                TryAssignTeamColor(hull, bubble);
            PaintChannelFrame(bubble);

            if (bubble.WorldCanvas != null && _cachedCamera != null
                && bubble.WorldCanvas.worldCamera != _cachedCamera)
                bubble.WorldCanvas.worldCamera = _cachedCamera;

            return true;
        }

        /// <summary>
        /// Thin team-color stem from each live chip row to its hull. Game cameras only —
        /// Scene / preview cameras must not lock this pass the way territory fill once did.
        /// </summary>
        public override void DrawShapes(Camera cam)
        {
            if (cam == null || cam.cameraType != CameraType.Game)
                return;

            bool pendingPing = ShipCommsClientState.IsOpen && ShipCommsClientState.HasPendingWaypoint;
            bool pendingYou = ShipCommsClientState.IsOpen && ShipCommsClientState.HasPendingYou;
            if (_live.Count <= 0 && _echo.Count <= 0 && !pendingPing && !pendingYou)
                return;

            using (Draw.Command(cam))
            {
                Draw.ResetAllDrawStates();
                Draw.BlendMode = ShapesBlendMode.Transparent;
                Draw.ThicknessSpace = ThicknessSpace.Pixels;
                Draw.LineGeometry = LineGeometry.Billboard;

                DrawBubbleStems(_live, drawIntent: true);
                DrawBubbleStems(_echo, drawIntent: false);

                if (pendingPing)
                {
                    Vector3 ping = ShipCommsClientState.PendingWaypoint;
                    ping.y = HeightAbovePlane;
                    Draw.ThicknessSpace = ThicknessSpace.Meters;
                    Draw.Disc(ping, Vector3.up, 0.38f, ChipAccent);
                    Draw.ThicknessSpace = ThicknessSpace.Pixels;
                }

                if (pendingYou)
                    ShipCommsCalloutGraphics.DrawPendingYou(1f);
            }
        }

        /// <summary>
        /// Gold / team stem from each chip row back to its hull. Intent paths (Us /
        /// Everyone lines) draw once on the speaker bubble — echoes only show chips.
        /// </summary>
        /// <param name="map">Speaker or echo dictionary.</param>
        /// <param name="drawIntent">True only for the speaker map so paths are not drawn N times.</param>
        void DrawBubbleStems(Dictionary<int, Bubble> map, bool drawIntent)
        {
            foreach (var pair in map)
            {
                Bubble bubble = pair.Value;
                if (bubble == null)
                    continue;

                float alpha = bubble.Group != null ? bubble.Group.alpha : 1f;
                int pathOwner = bubble.SourceNetworkId > 0 ? bubble.SourceNetworkId : bubble.NetworkId;
                bool seePaths = ShipCommsCalloutGraphics.LocalViewerCanSeePaths(pathOwner);
                Color stem = bubble.LineColor.a > 0.01f ? bubble.LineColor : ChipAccent;
                if (TeamCommanderRules.Sanitize(bubble.TeamOnly) == ShipCommsChannel.Commander)
                    stem = TeamCommanderRules.Gold;
                stem.a = 0.9f * alpha;
                if (seePaths && bubble.HasLine && alpha >= 0.01f)
                    Draw.Line(bubble.LineFrom, bubble.LineTo, LeaderLineThicknessPixels, LineEndCap.None, stem);

                if (drawIntent)
                    ShipCommsCalloutGraphics.DrawIntent(in bubble.Callout, bubble.Age, LifetimeSeconds, alpha);
            }
        }

        /// <summary>
        /// Reads the hull nameplate's cached team (already painted by the visualizer).
        /// Local optimistic callouts fall back to the Join Team assign so the stem is
        /// not white for a frame. No ECS ship gather.
        /// </summary>
        static void TryAssignTeamColor(Transform hull, Bubble bubble)
        {
            TeamId team = TeamId.None;
            if (hull != null)
            {
                var plate = hull.GetComponent<ShipWorldNameplate>();
                if (plate != null)
                    team = plate.PresentationTeam;
            }

            if (team == TeamId.None &&
                bubble.NetworkId > 0 &&
                bubble.NetworkId == EcsGameBridge.GetLocalNetworkId())
                team = ClientTeamFlowState.ResolvePresentationTeam(TeamId.None);

            if (team == TeamId.None)
            {
                bubble.LineColor = ChipAccent;
                return;
            }

            bubble.Team = team;
            bubble.LineColor = team.ToColor();
            PaintChannelFrame(bubble);
        }

        /// <summary>
        /// One plate around the row: white for All, speaker faction RGB for Team,
        /// command gold for Commander. Sliced AA sprite — not UGUI Outline (that
        /// crawls while the hull moves).
        /// </summary>
        static void PaintChannelFrame(Bubble bubble)
        {
            if (bubble == null || bubble.ChannelFrame == null)
                return;

            Color color = AllChannelFrame;
            ShipCommsChannel channel = TeamCommanderRules.Sanitize(bubble.TeamOnly);
            if (channel == ShipCommsChannel.Commander)
            {
                color = TeamCommanderRules.Gold;
            }
            else if (channel == ShipCommsChannel.Team)
            {
                if (bubble.Team != TeamId.None)
                    color = bubble.Team.ToColor();
                else if (bubble.LineColor.a > 0.01f)
                    color = bubble.LineColor;
            }

            color.a = 0.94f;
            bubble.ChannelFrame.color = color;
        }

        /// <summary>
        /// World canvas scale. Gameplay follows top-down height zoom. Theatrical uses
        /// camera-to-speaker distance over L1 height so the chip holds a stable screen size.
        /// </summary>
        static float ResolveChipWorldScale(bool theatrical, Camera cam, Vector3 shipCenter)
        {
            if (!theatrical || cam == null)
                return WorldCanvasScale * WorldFloatingCountManager.ResolveCameraZoomScale();

            float dist = Vector3.Distance(cam.transform.position, shipCenter);
            float refDist = TheatricalBillboardRefDistance;
            var follow = CameraFollowEcs.Instance;
            if (follow != null)
                refDist = Mathf.Max(1f, follow.Settings.heightAtLevel1);

            return WorldCanvasScale * Mathf.Clamp(
                dist / refDist,
                TheatricalBillboardScaleMin,
                TheatricalBillboardScaleMax);
        }

        /// <summary>
        /// Builds a world-space canvas with up to five reusable chips. Echo rows use
        /// a distinct GameObject name so the hierarchy stays readable in the Editor.
        /// </summary>
        /// <param name="networkId">Hull this canvas follows.</param>
        /// <param name="echo">True when this is a commander order under a tagged ship.</param>
        Bubble CreateBubble(int networkId, bool echo)
        {
            var root = new GameObject((echo ? "ShipCommsEcho_" : "ShipCommsBubble_") + networkId);
            root.transform.SetParent(null, true);

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = WorldSortingOrder;
            canvas.pixelPerfect = false;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 32f;
            root.AddComponent<GraphicRaycaster>().enabled = false;

            var group = root.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            var rect = root.GetComponent<RectTransform>();
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(ChipWidth + ChannelPad * 2f, ChipHeight + ChannelPad * 2f);
            float zoom = WorldFloatingCountManager.ResolveCameraZoomScale();
            float scale = WorldCanvasScale * zoom;
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(-90f, 0f, 0f));
            root.transform.localScale = new Vector3(scale, -scale, scale);

            var channelGo = new GameObject("ChannelFrame", typeof(RectTransform), typeof(Image));
            channelGo.transform.SetParent(root.transform, false);
            Stretch(channelGo.GetComponent<RectTransform>(), 0f);
            var channelImage = channelGo.GetComponent<Image>();
            StylePlate(channelImage, AllChannelFrame);

            var row = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(root.transform, false);
            var rowRt = row.GetComponent<RectTransform>();
            rowRt.anchorMin = Vector2.zero;
            rowRt.anchorMax = Vector2.one;
            rowRt.offsetMin = new Vector2(ChannelPad, ChannelPad);
            rowRt.offsetMax = new Vector2(-ChannelPad, -ChannelPad);
            var h = row.GetComponent<HorizontalLayoutGroup>();
            h.spacing = ChipGap;
            h.childAlignment = TextAnchor.MiddleCenter;
            h.childControlWidth = false;
            h.childControlHeight = false;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = false;

            var bubble = new Bubble
            {
                NetworkId = networkId,
                Root = root,
                WorldCanvas = canvas,
                CanvasRect = rect,
                Group = group,
                ChannelFrame = channelImage,
                IsEcho = echo,
                ScreenBelow = echo,
                SourceNetworkId = networkId,
            };

            for (int i = 0; i < ShipCommsKeywordCatalog.MaxSequenceLength; i++)
            {
                var chipGo = new GameObject("Chip" + i, typeof(RectTransform), typeof(LayoutElement));
                chipGo.transform.SetParent(row.transform, false);
                var chipRt = chipGo.GetComponent<RectTransform>();
                chipRt.sizeDelta = new Vector2(ChipWidth, ChipHeight);
                var le = chipGo.GetComponent<LayoutElement>();
                le.preferredWidth = ChipWidth;
                le.preferredHeight = ChipHeight;
                le.minWidth = ChipWidth;
                le.minHeight = ChipHeight;

                // Neutral frame first (behind). Sliced AA sprite — not UGUI Outline, which jitters in flight.
                var frameGo = new GameObject("Frame", typeof(RectTransform), typeof(Image));
                frameGo.transform.SetParent(chipGo.transform, false);
                Stretch(frameGo.GetComponent<RectTransform>(), 0f);
                var frameImage = frameGo.GetComponent<Image>();
                StylePlate(frameImage, ChipFrame);

                var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
                fillGo.transform.SetParent(chipGo.transform, false);
                Stretch(fillGo.GetComponent<RectTransform>(), FrameInset);
                var fillImage = fillGo.GetComponent<Image>();
                StylePlate(fillImage, ChipFill);

                var caretGo = new GameObject("Caret", typeof(RectTransform), typeof(Image));
                caretGo.transform.SetParent(chipGo.transform, false);
                var caretRt = caretGo.GetComponent<RectTransform>();
                caretRt.anchorMin = new Vector2(0f, 0f);
                caretRt.anchorMax = new Vector2(0f, 1f);
                caretRt.pivot = new Vector2(0f, 0.5f);
                caretRt.sizeDelta = new Vector2(2f, -FrameInset * 2f);
                caretRt.anchoredPosition = new Vector2(FrameInset, 0f);
                var caretImage = caretGo.GetComponent<Image>();
                StylePlate(caretImage, ChipCaret);

                var label = CreateWorldLabel(chipGo.transform, "Label");
                bubble.Chips[i] = chipGo;
                bubble.ChipLayouts[i] = le;
                bubble.ChipRects[i] = chipRt;
                bubble.Labels[i] = label;
                bubble.Frames[i] = frameImage;
                bubble.Fills[i] = fillImage;
                bubble.Carets[i] = caretImage;
            }

            return bubble;
        }

        /// <summary>TMP chip label using the shared Rajdhani HUD font when present.</summary>
        static TextMeshProUGUI CreateWorldLabel(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(ChipPadX, 2f);
            rt.offsetMax = new Vector2(-ChipPadX, -2f);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.fontSize = 11f;
            tmp.color = ChipText;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.outlineWidth = 0.18f;
            tmp.outlineColor = ChipOutline;
            TMP_FontAsset font = Resources.Load<TMP_FontAsset>("Rajdhani-SemiBold SDF");
            if (font != null)
                tmp.font = font;
            return tmp;
        }

        /// <summary>Sliced AA plate so the border stays put while the hull moves.</summary>
        static void StylePlate(Image image, Color color)
        {
            image.sprite = GetPlateSprite();
            image.type = Image.Type.Sliced;
            image.color = color;
            image.raycastTarget = false;
        }

        /// <summary>
        /// 16×16 white sprite with a 1px transparent rim. Bilinear filtering turns that rim
        /// into a stable AA edge — UGUI <c>Outline</c> drew 4 offset copies that shimmered.
        /// </summary>
        static Sprite GetPlateSprite()
        {
            if (s_PlateSprite != null)
                return s_PlateSprite;

            const int dim = 16;
            var tex = new Texture2D(dim, dim, TextureFormat.RGBA32, false);
            tex.name = "ShipCommsChipPlateTex";
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.anisoLevel = 0;

            var pixels = new Color32[dim * dim];
            var clear = new Color32(255, 255, 255, 0);
            var solid = new Color32(255, 255, 255, 255);
            for (int y = 0; y < dim; y++)
            {
                for (int x = 0; x < dim; x++)
                {
                    bool rim = x == 0 || y == 0 || x == dim - 1 || y == dim - 1;
                    pixels[y * dim + x] = rim ? clear : solid;
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, true);

            s_PlateSprite = Sprite.Create(
                tex,
                new Rect(0f, 0f, dim, dim),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(1f, 1f, 1f, 1f));
            s_PlateSprite.name = "ShipCommsChipPlateSprite";
            return s_PlateSprite;
        }

        static void Stretch(RectTransform rt, float inset)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
        }

        /// <summary>
        /// Caches the mesh AABB center in hull-local XZ so chips sit over the visual
        /// midline, not the (often offset) prefab pivot.
        /// </summary>
        static void EnsureLocalHullCenter(Transform hull, Bubble bubble)
        {
            if (bubble.HasLocalCenter || hull == null)
                return;

            Renderer[] renderers = hull.GetComponentsInChildren<Renderer>(true);
            if (WorldBodyLabelLayout.TryEncapsulateBodyBounds(renderers, out Bounds bounds))
            {
                Vector3 local = hull.InverseTransformPoint(bounds.center);
                local.y = 0f;
                bubble.LocalXzCenter = local;
            }

            bubble.HasLocalCenter = true;
        }

        /// <summary>Destroys one speaker bubble GameObject and forgets the NetworkId mapping.</summary>
        void DestroyBubble(int networkId)
        {
            DestroyFromMap(_live, networkId);
        }

        /// <summary>Destroys one commander echo under a tagged hull.</summary>
        void DestroyEcho(int networkId)
        {
            DestroyFromMap(_echo, networkId);
        }

        /// <summary>Removes one entry from a bubble map and destroys its world canvas.</summary>
        static void DestroyFromMap(Dictionary<int, Bubble> map, int networkId)
        {
            if (!map.TryGetValue(networkId, out Bubble bubble))
                return;

            map.Remove(networkId);
            if (bubble?.Root != null)
                Destroy(bubble.Root);
        }
    }
}
