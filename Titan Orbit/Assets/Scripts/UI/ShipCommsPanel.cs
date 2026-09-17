using System;
using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Input;
using TitanOrbit.NetCode;
using TitanOrbit.Services;
using TitanOrbit.Shared;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Hold-S comms matrix: a centered dark-glass HUD card with keyword tiles. The player
    /// holds S, clicks 1–5 words in order (3 free; one ad unlocks the 4th and 5th), then releases S to send that sentence above
    /// their ship. Top-level rails stay TACTICAL / SUBJECT / SOCIAL / COMMANDER (plus TEAM).
    /// Inside each rail, slim telemetry captions (STRIKE, WHO, GEAR, …) keep related
    /// words on the same 5-wide row. An All / Team / Commander toggle and the RECENT chip list are remembered in PlayerPrefs
    /// so both survive a new match. Free players get three RECENT rows; one ad unlocks the rest.
    /// Commander is the top-three command deck: it unlocks
    /// Everyone / Escort / Form Up and paints gold chrome.
    /// <para>
    /// Client presentation only. Sending goes through <see cref="ShipCommsRpcClient"/>
    /// (RPC — Remote Procedure Call: the client asks the server to broadcast or target
    /// teammates). This panel never writes ship ghosts. Desktop only for v1 — phones have
    /// no S key mapping yet.
    /// </para>
    /// Layout is explicit RectTransforms (no nested ContentSizeFitters). Spawn style
    /// matches <see cref="SpaceBrakesHUD"/>: <c>RuntimeInitializeOnLoadMethod</c> plus
    /// <c>DontDestroyOnLoad</c>. Paired with <see cref="ShipCommsBubblePresenter"/>
    /// (world chips) and <see cref="ShipCommsClientState"/> (fire-suppression flag).
    /// </summary>
    [DefaultExecutionOrder(66240)]
    public sealed class ShipCommsPanel : MonoBehaviour
    {
        /// <summary>
        /// Wide HUD button, not a square. World chips in <see cref="ShipCommsBubblePresenter"/>
        /// use these same pixel sizes so a word like NO PROBLEM fits on one line.
        /// </summary>
        public const float TileWidth = 100f;
        public const float TileHeight = 26f;
        public const float TileGap = 4f;
        public const int KeywordColumns = 5;

        /// <summary>RECENT row is one-third smaller than the matrix; chips sit inside that row.</summary>
        const float RecentScale = 2f / 3f;
        const float RecentChipFit = 0.85f;
        const float RecentChipWidth = TileWidth * RecentScale * RecentChipFit;
        const float RecentChipHeight = TileHeight * RecentScale * RecentChipFit;
        const float RecentChipGap = TileGap * RecentScale;
        const float RecentChipFont = 6.5f;
        const float RecentRowPadLeft = 8f;
        const float RecentRowPadRight = 4f;
        const float RecentColWidth =
            RecentRowPadLeft
            + ShipCommsKeywordCatalog.MaxSequenceLength * RecentChipWidth
            + (ShipCommsKeywordCatalog.MaxSequenceLength - 1) * RecentChipGap
            + RecentRowPadRight;
        const float RootGap = 10f;
        /// <summary>Keeps the docked map circle inside the chrome instead of kissing the card edge.</summary>
        const float MinimapCircleInset = 20f;
        const float HeaderHeight = 26f;
        const float AudienceToggleWidth = 48f;
        const float AudienceToggleGap = 4f;
        const float BannerHeight = 14f;
        /// <summary>Top-level TACTICAL / SUBJECT rail — same role as the old section titles.</summary>
        const float SectionBannerHeight = 15f;
        /// <summary>Slim telemetry caption for STRIKE / WHO / GEAR rows (not a second banner).</summary>
        const float ThemeCaptionHeight = 8f;
        const float SectionGap = 6f;
        /// <summary>Tight gap between themed 5-wide rows in the same family (STRIKE→HOLD).</summary>
        const float ThemeRowGap = 3f;
        /// <summary>Air after a family (verbs → nouns) before the next section rail.</summary>
        const float FamilyGap = 8f;
        const float PanelPad = 12f;
        const float RecentRowHeight = TileHeight * RecentScale;

        static readonly Color FillColor = new Color(0.012f, 0.016f, 0.028f, 1f);
        static readonly Color CaptionPlateColor = new Color(0.018f, 0.028f, 0.045f, 1f);
        static readonly Color FrameTint = new Color(0.22f, 0.32f, 0.45f, 0.55f);
        static readonly Color CaptionTextColor = new Color(0.62f, 0.78f, 0.95f, 0.92f);
        static readonly Color BodyTextColor = new Color(0.88f, 0.92f, 0.98f, 1f);
        static readonly Color AccentColor = new Color(0.35f, 0.72f, 0.95f, 0.95f);
        static readonly Color BracketColor = new Color(0.35f, 0.55f, 0.75f, 0.55f);
        static readonly Color TileIdle = new Color(0.03f, 0.05f, 0.09f, 0.96f);
        static readonly Color TileSelected = new Color(0.05f, 0.12f, 0.22f, 0.98f);
        static readonly Color PreviewEmpty = new Color(0.02f, 0.04f, 0.07f, 0.92f);
        static readonly Color LabelOutline = new Color(0.02f, 0.04f, 0.08f, 0.85f);
        static readonly Color SeparatorColor = new Color(0.12f, 0.18f, 0.28f, 0.85f);
        static readonly Color TeamChannelColor = new Color(0.95f, 0.62f, 0.22f, 0.95f);
        /// <summary>Same white plate as world-chip All frames.</summary>
        static readonly Color AllChannelColor = new Color(1f, 1f, 1f, 0.92f);
        /// <summary>Command-deck brass — same gold as the leaderboard Command Deck.</summary>
        static readonly Color CommanderChannelColor = TeamCommanderRules.Gold;
        /// <summary>Near-void plate so a locked command tile does not read as a live chip.</summary>
        static readonly Color CommanderLockedFill = new Color(0.008f, 0.008f, 0.012f, 0.92f);
        /// <summary>Faded brass for the LOCK stamp — readable, not a gold “selected” glow.</summary>
        static readonly Color CommanderLockedStamp = new Color(0.42f, 0.34f, 0.18f, 0.70f);
        /// <summary>Word under LOCK — low contrast so it cannot be mistaken for a pick.</summary>
        static readonly Color CommanderLockedWord = new Color(0.28f, 0.30f, 0.34f, 0.38f);
        /// <summary>Veil over a whole locked group so one ad reads as one lock, not a stamp per chip.</summary>
        static readonly Color AdGateVeil = new Color(0.010f, 0.012f, 0.020f, 0.82f);
        /// <summary>Amber plate for the single watch-ad CTA (same hue as the old AD stamp).</summary>
        static readonly Color AdGatePlate = new Color(0.07f, 0.045f, 0.018f, 0.96f);

        /// <summary>Keyword bytes chosen this hold, in click order (max 5).</summary>
        readonly List<byte> _sequence = new List<byte>(ShipCommsKeywordCatalog.MaxSequenceLength);

        readonly List<KeywordTile> _tiles = new List<KeywordTile>(40);
        readonly PreviewSlot[] _preview = new PreviewSlot[ShipCommsKeywordCatalog.MaxSequenceLength];
        /// <summary>
        /// RECENT rows built to fill the card. Allocated in <see cref="BuildUi"/> after
        /// the keyword grid sets the panel height — not a compile-time 10-slot rail.
        /// </summary>
        RecentSlot[] _recent = Array.Empty<RecentSlot>();

        Canvas _canvas;
        CanvasGroup _group;
        RectTransform _panel;
        PlayerInputHandler _input;
        Keyboard _cachedKeyboard;
        UnityEngine.Camera _playAimCamera;
        TextMeshProUGUI _headerSub;
        Image _allFill;
        Image _teamFill;
        Image _commanderFill;
        Outline _allOutline;
        Outline _teamOutline;
        Outline _commanderOutline;
        TextMeshProUGUI _allLabel;
        TextMeshProUGUI _teamLabel;
        TextMeshProUGUI _commanderLabel;
        bool _wasHeld;
        bool _built;
        RectTransform _minimapDock;
        RectTransform _minimapHost;
        float _overlayW;
        float _dockSize;
        bool _minimapDocked;

        /// <summary>Highlighted RECENT row while S is held. -1 = none.</summary>
        int _recentCursor = -1;

        /// <summary>One plate over compose slots 4+5. Hidden after the keyword unlock ad.</summary>
        UnlockGate _slotUnlockGate;
        /// <summary>One plate over every RECENT row past the free three. Hidden after that unlock ad.</summary>
        UnlockGate _recentUnlockGate;

        /// <summary>One keyword button in the matrix.</summary>
        struct KeywordTile
        {
            public byte Index;
            public Image Fill;
            public Outline Outline;
            public TextMeshProUGUI Label;
            public TextMeshProUGUI OrderBadge;
            public TextMeshProUGUI LockLabel;
            public Button Button;
            public Image Caret;
            /// <summary>Cyan for ordinary words; faction RGB for Red / Blue / Green / Orange / Purple.</summary>
            public Color Accent;
            /// <summary>True when this tile is one of the five team color keywords.</summary>
            public bool IsTeamColor;
            /// <summary>True when this word paints a colored comms line (Heal, Attack, …).</summary>
            public bool IsLineColor;
            /// <summary>True when this word is a command-deck unlock (Everyone, Us, Form Up, …).</summary>
            public bool IsCommanderWord;
        }

        /// <summary>One of the five sequence chips at the top of the card.</summary>
        struct PreviewSlot
        {
            public Image Fill;
            public TextMeshProUGUI Label;
            public Outline Outline;
            public Button Button;
        }

        /// <summary>
        /// One rewarded-ad plate that covers a whole locked group. Slots 4+5 share
        /// <see cref="_slotUnlockGate"/>; every paid RECENT row shares
        /// <see cref="_recentUnlockGate"/>. Per-chip AD stamps made it look like
        /// each button needed its own video.
        /// </summary>
        sealed class UnlockGate
        {
            public GameObject Root;
            public Button Button;
        }

        /// <summary>
        /// One themed 5-wide row on the compose card. Catalog categories
        /// (Tactical / Subject / Social / Commander) still decide which words exist;
        /// these clusters only change the slim caption and tile order. Wire indices never move.
        /// </summary>
        enum MatrixCluster : byte
        {
            Team = 0,
            Strike = 1,
            Hold = 2,
            Work = 3,
            Who = 4,
            Where = 5,
            World = 6,
            Gear = 7,
            Reply = 8,
            Thanks = 9,
            React = 10,
            Squad = 11,
            Orders = 12,
        }

        /// <summary>
        /// Top-level compose rails — the same TACTICAL / SUBJECT / SOCIAL /
        /// COMMANDER (plus TEAM) blocks the matrix used before theme rows.
        /// </summary>
        enum MatrixFamily : byte
        {
            Team = 0,
            Tactical = 1,
            Subject = 2,
            Social = 3,
            Commander = 4,
        }

        /// <summary>
        /// One slim theme caption plus its 5-wide tile row. <see cref="FamilyBreakAfter"/>
        /// uses the wider <see cref="FamilyGap"/> so TEAM / TACTICAL / SUBJECT /
        /// SOCIAL / COMMANDER still read as the old sections.
        /// </summary>
        struct ClusterSpec
        {
            public MatrixCluster Id;
            public MatrixFamily Family;
            public string Banner;
            public bool CommanderChrome;
            public bool FamilyBreakAfter;
        }

        /// <summary>
        /// Top-to-bottom cluster list. TEAM stays faction order; every other row is
        /// a sentence theme (strike verbs together, who-nouns together, …).
        /// </summary>
        static readonly ClusterSpec[] ClusterLayout =
        {
            new ClusterSpec { Id = MatrixCluster.Team, Family = MatrixFamily.Team, Banner = "TEAM", FamilyBreakAfter = true },
            new ClusterSpec { Id = MatrixCluster.Strike, Family = MatrixFamily.Tactical, Banner = "STRIKE" },
            new ClusterSpec { Id = MatrixCluster.Hold, Family = MatrixFamily.Tactical, Banner = "HOLD" },
            new ClusterSpec { Id = MatrixCluster.Work, Family = MatrixFamily.Tactical, Banner = "WORK", FamilyBreakAfter = true },
            new ClusterSpec { Id = MatrixCluster.Who, Family = MatrixFamily.Subject, Banner = "WHO" },
            new ClusterSpec { Id = MatrixCluster.Where, Family = MatrixFamily.Subject, Banner = "WHERE" },
            new ClusterSpec { Id = MatrixCluster.World, Family = MatrixFamily.Subject, Banner = "WORLD" },
            new ClusterSpec { Id = MatrixCluster.Gear, Family = MatrixFamily.Subject, Banner = "GEAR", FamilyBreakAfter = true },
            new ClusterSpec { Id = MatrixCluster.Reply, Family = MatrixFamily.Social, Banner = "REPLY" },
            new ClusterSpec { Id = MatrixCluster.Thanks, Family = MatrixFamily.Social, Banner = "THANKS" },
            new ClusterSpec { Id = MatrixCluster.React, Family = MatrixFamily.Social, Banner = "REACT", FamilyBreakAfter = true },
            new ClusterSpec { Id = MatrixCluster.Squad, Family = MatrixFamily.Commander, Banner = "SQUAD", CommanderChrome = true },
            new ClusterSpec { Id = MatrixCluster.Orders, Family = MatrixFamily.Commander, Banner = "ORDERS", CommanderChrome = true },
        };

        /// <summary>
        /// Display order inside each cluster. Labels only — the RPC still sends the
        /// catalog byte. Index matches <see cref="MatrixCluster"/>. TEAM is empty so
        /// Red…Purple keep catalog (faction) order. A new catalog word that is not
        /// listed here still appears, tacked on the last cluster of its family.
        /// </summary>
        static readonly string[][] ClusterThemeLabels =
        {
            Array.Empty<string>(),
            new[] { "Attack", "Kill", "Capture", "Push", "Incoming" },
            new[] { "Defend", "Hold", "Retreat", "Help", "Heal" },
            new[] { "Mining", "Transport", "Deposit", "Follow", "Wait" },
            new[] { "You", "Me", "Us", "Ally", "Enemy" },
            new[] { "Here", "Home", "Base", "Pad", "Team" },
            new[] { "Planet", "Moon", "Titan", "Asteroid", "Ship" },
            new[] { "Gems", "Troops", "Turret", "Mines", "Rocket" },
            new[] { "Yes", "No", "Ready", "Go", "Later" },
            new[] { "Thanks", "No Problem", "Sorry", "Good Luck", "GG" },
            new[] { "Good", "Bad", "Nice", "Wow", "Oops" },
            new[] { "Everyone", "Escort", "Form Up", "Spread", "Focus" },
            new[] { "Advance", "Cover", "Orders", "Report", "Status" },
        };

        /// <summary>One keyword chip inside a RECENT row.</summary>
        struct RecentChip
        {
            public Image Fill;
            public TextMeshProUGUI Label;
            public Outline Outline;
        }

        /// <summary>One of the last-sent sentences: a selectable row of up to five chips.</summary>
        sealed class RecentSlot
        {
            public Image Fill;
            public Image Caret;
            public Outline Outline;
            public Button Button;
            public readonly RecentChip[] Chips = new RecentChip[ShipCommsKeywordCatalog.MaxSequenceLength];
        }

        /// <summary>
        /// [UNITY] Creates the HUD once after the first scene load.
        /// Do not wrap this in <c>#if UNITY_SERVER</c> — the Editor Dedicated Server build
        /// target defines that symbol and would strip the panel in Play Mode. Use
        /// <see cref="TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation"/> instead
        /// (true in Editor, false only on a real headless binary).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureExists()
        {
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            if (FindFirstObjectByType<ShipCommsPanel>() != null)
                return;

            var go = new GameObject(nameof(ShipCommsPanel));
            DontDestroyOnLoad(go);
            go.AddComponent<ShipCommsPanel>();
        }

        /// <summary>Builds the overlay once, then starts hidden.</summary>
        void Awake()
        {
            // [TITAN-ORBIT] MPPM clones share one PlayerPrefs store — suffix the keys so
            // Player 2's All/Team choice and RECENT list do not overwrite Player 1.
            ShipCommsClientState.BindPrefsKey(
                TitanOrbitPlayModeUtility.GetInstancePlayerPrefsKey(ShipCommsClientState.TeamOnlyPrefsKey));
            ShipCommsHistory.BindPrefsKey(
                TitanOrbitPlayModeUtility.GetInstancePlayerPrefsKey(ShipCommsHistory.HistoryPrefsKey));
            BuildUi();
            SetOpen(false, clearSequence: true);
        }

        /// <summary>
        /// [UNITY] Domain Reload / scene teardown. Clear the shared fire-suppression flag
        /// so the next Play does not start with guns muted.
        /// </summary>
        void OnDestroy()
        {
            UndockMinimap();
            HUDController.SetCommsMatrixObscuresHud(false);
            if (ShipCommsClientState.IsOpen)
                ShipCommsClientState.SetOpen(false);
        }

        /// <summary>
        /// Samples S, opens/closes the card, and sends on release. Runs after
        /// <see cref="PlayerInputHandler"/> so <c>CommsHeld</c> is this frame's value.
        /// </summary>
        void LateUpdate()
        {
            if (!_built)
                return;

            if (_input == null)
                _input = FindFirstObjectByType<PlayerInputHandler>();

            bool held = IsCommsKeyHeld();
            bool canUse = CanUseComms();

            // --- Cancel while blocked ---
            // Escape, orbit station, death, or the title screen must not leave a stuck overlay.
            if (_wasHeld && (!canUse || InGameEscapeMenuController.IsOpen))
            {
                SetOpen(false, clearSequence: true);
                _wasHeld = false;
                return;
            }

            if (held && canUse)
            {
                // --- S down / hold ---
                if (!_wasHeld)
                {
                    _sequence.Clear();
                    PaintSequence();
                }

                SetOpen(true, clearSequence: false);
            }
            else if (_wasHeld && ShipCommsClientState.IsOpen)
            {
                // --- S up ---
                // A non-empty sentence sends; an empty hold is a cancel.
                TrySendSequence();
                SetOpen(false, clearSequence: true);
            }
            else if (!held && ShipCommsClientState.IsOpen)
            {
                SetOpen(false, clearSequence: false);
            }
            else if (!held && _minimapDock != null && _minimapDock.childCount > 0)
            {
                // Recover a map left under the dock after a prior close that disabled it first.
                UndockMinimap();
            }

            _wasHeld = held && canUse;

            // Aim / recent-wheel only matter while the matrix is up. Sampling Camera.main
            // and IsPointerOverGameObject every closed frame showed up as extra LateUpdate work.
            if (held && canUse)
                TrySamplePlayAim();
            if (ShipCommsClientState.IsOpen)
            {
                // Rank can change while S is held (a teammate deposits). Drop Commander
                // if this machine falls out of the top three so locked words cannot send.
                EnsureCommanderChannelStillValid();
                TryStepRecentFromWheel();
                if (ShipCommsClientState.ConsumeWaypointChipDirty())
                    EnsureMapPointChip();
            }
        }

        /// <summary>
        /// Mouse wheel steps the RECENT column: up = newer (toward the top),
        /// down = older. First notch loads the latest sentence. Locked rows
        /// stay skipped until the one RECENT unlock ad runs.
        /// </summary>
        void TryStepRecentFromWheel()
        {
            if (!TryReadScrollY(out float scrollY))
                return;

            int count = ShipCommsHistory.Recent.Count;
            if (count < 1)
                return;

            // Free users only wheel through the first three filled chips.
            int walkable = ShipCommsClientState.RecentRowsUnlocked
                ? count
                : Mathf.Min(count, ShipCommsClientState.FreeRecentRows);
            if (walkable < 1)
                return;

            int delta = scrollY > 0f ? -1 : 1;
            int next = _recentCursor < 0 ? 0 : _recentCursor + delta;
            next = Mathf.Clamp(next, 0, walkable - 1);
            if (next == _recentCursor)
                return;

            OnRecentClicked(next);
        }

        static bool TryReadScrollY(out float scrollY)
        {
            scrollY = 0f;
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current == null)
                return false;
            scrollY = Mouse.current.scroll.ReadValue().y;
#else
            scrollY = UnityEngine.Input.mouseScrollDelta.y;
#endif
            return Mathf.Abs(scrollY) > 0.01f;
        }

        /// <summary>
        /// Play-plane aim while the pointer is off the HUD. "You" / planet / asteroid
        /// resolve from this so a click on the tile does not unproject through the card.
        /// </summary>
        void TrySamplePlayAim()
        {
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                return;

            if (_playAimCamera == null)
                _playAimCamera = UnityEngine.Camera.main;
            if (_input != null && _input.TryGetMouseWorldPosition(_playAimCamera, out Vector3 world))
                ShipCommsClientState.SetLastPlayAim(world);
        }

        /// <summary>
        /// True when this machine may open the matrix: in a match, flying, desktop, no
        /// blocking overlay. Mobile is v2 (no S key).
        /// </summary>
        bool CanUseComms()
        {
            if (Application.isMobilePlatform)
                return false;
            if (ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
                return false;
            if (!EcsGameBridge.IsNetworkInGame() || !EcsGameBridge.HasLocalPlayerShip())
                return false;
            if (HUDController.LocalPlayerDeathHidesHud)
                return false;
            if (MoonOrbitClientState.IsOrbitMenuVisible)
                return false;
            if (InGameEscapeMenuController.IsOpen)
                return false;
            if (DeathScreenController.IsShowing)
                return false;
            return true;
        }

        /// <summary>
        /// S held this frame. Prefers <see cref="PlayerInputHandler.CommsHeld"/>, then polls
        /// the keyboard directly so an unfocused Game view or a missing handler still works.
        /// </summary>
        bool IsCommsKeyHeld()
        {
            if (_input != null && _input.CommsHeld)
                return true;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
                keyboard = _cachedKeyboard;
            if (keyboard == null)
            {
                foreach (var device in InputSystem.devices)
                {
                    if (device is Keyboard found)
                    {
                        keyboard = found;
                        break;
                    }
                }
            }

            _cachedKeyboard = keyboard;
            return keyboard != null && keyboard.sKey.isPressed;
        }

        /// <summary>
        /// Shows or hides the card and publishes <see cref="ShipCommsClientState.IsOpen"/>
        /// so guns and the combat cursor yield to keyword clicks.
        /// </summary>
        void SetOpen(bool open, bool clearSequence)
        {
            bool wasOpen = ShipCommsClientState.IsOpen;
            if (clearSequence)
            {
                _sequence.Clear();
                _recentCursor = -1;
                PaintSequence();
            }

            // Put the HUD map back before hiding the dock. The live minimap is a child of
            // the dock while S is held — SetActive(false) on the dock would disable it,
            // clear MinimapController.Instance, and skip the reparent.
            if (!open && wasOpen)
            {
                UndockMinimap();
                HUDController.SetCommsMatrixObscuresHud(false);
                if (clearSequence)
                {
                    ShipCommsClientState.ClearPendingWaypoint();
                    ShipCommsClientState.ClearPendingYou();
                }
            }

            if (_group != null)
            {
                _group.alpha = open ? 1f : 0f;
                _group.blocksRaycasts = open;
                _group.interactable = open;
            }

            if (_panel != null)
                _panel.gameObject.SetActive(open);
            if (_minimapDock != null)
                _minimapDock.gameObject.SetActive(open);

            if (open && !wasOpen)
            {
                GrantOwnedAdUnlocks();
                _recentCursor = -1;
                PaintRecent();
                PaintAudience();
                PaintSequence();
                ShipCommsClientState.ClearPendingWaypoint();
                ShipCommsClientState.ClearPendingYou();
                HUDController.SetCommsMatrixObscuresHud(true);
                DockMinimap();
            }

            ShipCommsClientState.SetOpen(open);
        }

        /// <summary>
        /// Releases S: send 1–5 indices (and an optional minimap ping), paint an
        /// optimistic local bubble, then clear.
        /// </summary>
        void TrySendSequence()
        {
            // --- Channel ---
            // [TITAN-ORBIT] PlayerPrefs-backed All / Team / Commander toggle. The server
            // re-checks team and commander rank — this byte is a request, not a rank the
            // client can spoof. Commander words never leave this machine unless the
            // command deck is unlocked on this hold.
            ShipCommsChannel channel = ResolveSendableChannel();
            if (channel != ShipCommsChannel.Commander)
                StripCommanderKeywordsFromSequence();

            int count = _sequence.Count;
            if (count < 1)
                return;

            int allowed = ShipCommsClientState.AllowedSequenceLength;
            if (count > allowed)
                count = allowed;

            byte k0 = _sequence[0];
            byte k1 = count >= 2 ? _sequence[1] : (byte)0;
            byte k2 = count >= 3 ? _sequence[2] : (byte)0;
            byte k3 = count >= 4 ? _sequence[3] : (byte)0;
            byte k4 = count >= 5 ? _sequence[4] : (byte)0;
            byte teamOnly = (byte)channel;
            byte hasWaypoint = 0;
            float waypointX = 0f;
            float waypointZ = 0f;
            if (ShipCommsClientState.HasPendingWaypoint)
            {
                hasWaypoint = 1;
                Vector3 ping = ShipCommsClientState.PendingWaypoint;
                waypointX = ping.x;
                waypointZ = ping.z;
            }

            var payload = new ShipCommsInbox.Callout
            {
                NetworkId = EcsGameBridge.GetLocalNetworkId(),
                Count = (byte)count,
                K0 = k0,
                K1 = k1,
                K2 = k2,
                K3 = k3,
                K4 = k4,
                TeamOnly = teamOnly,
                HasWaypoint = hasWaypoint,
                WaypointX = waypointX,
                WaypointZ = waypointZ,
            };

            ShipCommsCalloutGraphics.BindResolvedTargets(ref payload);

            ShipCommsRpcClient.TrySend(payload);
            // Remember a player-picked Here ping only. Asteroid / planet re-resolve on reuse.
            byte persistPing = payload.FocusKind == ShipCommsInbox.FocusKind.MapPing
                ? payload.HasWaypoint
                : (byte)0;
            ShipCommsHistory.Record(
                (byte)count, k0, k1, k2, k3, k4,
                persistPing, payload.WaypointX, payload.WaypointZ);

            // --- Optimistic local chips ---
            // [TITAN-ORBIT] Enqueue through the ECS inbox (same path as the broadcast RPC)
            // so the speaker does not wait on round-trip. The echo replaces the same bubble.
            if (payload.NetworkId > 0)
                ShipCommsInbox.Enqueue(payload);

            ShipCommsClientState.ClearPendingWaypoint();
            ShipCommsClientState.ClearPendingYou();
        }

        /// <summary>
        /// Adds a keyword, or removes it when that tile is already on so the player can
        /// correct the sentence without clearing the whole hold.
        /// </summary>
        void OnKeywordClicked(byte index)
        {
            if (!ShipCommsClientState.IsOpen)
                return;

            // Locked command-deck tiles stay visible so the squad can see the unlock,
            // but they do not enter the sentence until CMDR is live.
            if (ShipCommsKeywordCatalog.LoadDefault().IsCommanderKeyword(index)
                && !CommanderKeywordsUnlocked())
                return;

            int existing = IndexOfSequence(index);
            if (existing >= 0)
            {
                _sequence.RemoveAt(existing);
                if (!SequenceHasYou())
                    ShipCommsClientState.ClearPendingYou();
                if (!SequenceHasHere())
                    ShipCommsClientState.ClearPendingWaypoint();
                PaintSequence();
                return;
            }

            if (_sequence.Count >= ShipCommsClientState.AllowedSequenceLength)
                return;

            _sequence.Add(index);
            TryLockYouOnClick(index);
            PaintSequence();
        }

        /// <summary>
        /// Locks the closest-in-range ship when the player clicks "You".
        /// [TITAN-ORBIT] No hull in range (or only the local ship) leaves You empty.
        /// Send must not rewrite that as Me — "You Asteroid" then draws the rock only.
        /// </summary>
        void TryLockYouOnClick(byte index)
        {
            var catalog = ShipCommsKeywordCatalog.LoadDefault();
            if (!catalog.TryGetLabel(index, out string label)
                || !string.Equals(label, "You", System.StringComparison.OrdinalIgnoreCase))
                return;

            // Play-plane aim — the last world point under the pointer, not the HUD tile.
            Vector3 aim = ShipCommsClientState.HasLastPlayAim
                ? ShipCommsClientState.LastPlayAim
                : Vector3.zero;
            int localId = EcsGameBridge.GetLocalNetworkId();
            if (ShipCommsCalloutGraphics.TryResolveYou(aim, localId, out int youId)
                && youId > 0
                && youId != localId)
                ShipCommsClientState.SetPendingYou(youId);
            else
                ShipCommsClientState.ClearPendingYou();
        }

        bool SequenceHasYou() => SequenceHasLabel("You");

        bool SequenceHasHere() => SequenceHasLabel("Here");

        bool SequenceHasLabel(string label)
        {
            var catalog = ShipCommsKeywordCatalog.LoadDefault();
            for (int i = 0; i < _sequence.Count; i++)
            {
                if (catalog.TryGetLabel(_sequence[i], out string word)
                    && string.Equals(word, label, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Minimap ping becomes a Here chip in the sentence so Recent can replay the point.
        /// </summary>
        void EnsureMapPointChip()
        {
            var catalog = ShipCommsKeywordCatalog.LoadDefault();
            if (!catalog.TryGetIndex("Here", out byte here))
                return;

            int existing = IndexOfSequence(here);
            if (existing < 0)
            {
                if (_sequence.Count >= ShipCommsClientState.AllowedSequenceLength)
                    _sequence[_sequence.Count - 1] = here;
                else
                    _sequence.Add(here);
            }

            PaintSequence();
        }

        /// <summary>
        /// Clicking preview slot N drops that word and everything after it so the player
        /// can rewrite the tail of the sentence without clearing the whole hold.
        /// </summary>
        void OnPreviewClicked(int slot)
        {
            if (!ShipCommsClientState.IsOpen)
                return;
            if (slot < 0 || slot >= ShipCommsKeywordCatalog.MaxSequenceLength)
                return;

            if (slot >= ShipCommsClientState.AllowedSequenceLength)
            {
                TryUnlockNextSlot();
                return;
            }

            if (slot >= _sequence.Count)
                return;

            _sequence.RemoveRange(slot, _sequence.Count - slot);
            if (!SequenceHasYou())
                ShipCommsClientState.ClearPendingYou();
            if (!SequenceHasHere())
                ShipCommsClientState.ClearPendingWaypoint();
            PaintSequence();
        }

        /// <summary>
        /// Loads a previous sentence into the 1/2/3 rail so release-S sends it again.
        /// Rows past the free first three sit under one unlock plate until that ad runs.
        /// </summary>
        void OnRecentClicked(int index)
        {
            if (!ShipCommsClientState.IsOpen)
                return;

            if (!ShipCommsClientState.IsRecentRowUnlocked(index))
            {
                TryUnlockRecentRows(index);
                return;
            }

            ApplyRecentSentence(index);
        }

        /// <summary>
        /// Copies history slot <paramref name="index"/> onto the compose rail.
        /// Caller already checked that the row is unlocked and exists.
        /// </summary>
        void ApplyRecentSentence(int index)
        {
            if (!ShipCommsHistory.TryGet(index, out ShipCommsHistory.Sentence sentence))
                return;

            _sequence.Clear();
            int allowed = ShipCommsClientState.AllowedSequenceLength;
            _sequence.Add(sentence.K0);
            if (sentence.Count >= 2 && allowed >= 2)
                _sequence.Add(sentence.K1);
            if (sentence.Count >= 3 && allowed >= 3)
                _sequence.Add(sentence.K2);
            if (sentence.Count >= 4 && allowed >= 4)
                _sequence.Add(sentence.K3);
            if (sentence.Count >= 5 && allowed >= 5)
                _sequence.Add(sentence.K4);

            // Here pings replay the clicked map point. Asteroid / planet / You resolve
            // again from the speaker so "Asteroid" is a new closest rock.
            if (sentence.HasWaypoint != 0 && SequenceHasHere())
                ShipCommsClientState.SetPendingWaypoint(
                    new Vector3(sentence.WaypointX, 0f, sentence.WaypointZ));
            else
                ShipCommsClientState.ClearPendingWaypoint();

            ShipCommsClientState.ClearPendingYou();
            ShipCommsClientState.ClearLastPlayAim();

            // Recent rows that used command words snap back to the Commander channel
            // when this machine still owns a command-deck seat. Otherwise strip them.
            if (SequenceHasCommanderKeyword())
            {
                if (IsLocalCommander())
                    ShipCommsClientState.SetChannel(ShipCommsChannel.Commander);
                else
                    StripCommanderKeywordsFromSequence();
            }

            _recentCursor = index;
            PaintAudience();
            PaintSequence();
            PaintRecent();
        }

        /// <summary>Refreshes preview chips and the 1/2/3 badges on keyword tiles.</summary>
        void PaintSequence()
        {
            var catalog = ShipCommsKeywordCatalog.LoadDefault();

            int allowed = ShipCommsClientState.AllowedSequenceLength;
            for (int i = 0; i < _preview.Length; i++)
            {
                bool locked = i >= allowed;
                bool filled = i < _sequence.Count;
                string label = (i + 1).ToString();
                if (filled && catalog.TryGetLabel(_sequence[i], out string word))
                {
                    label = word;
                    if (ShipCommsCalloutGraphics.TryResolvePendingHereDisplayLabel(word, out string planetName))
                        label = planetName;
                }

                PreviewSlot slot = _preview[i];
                Color previewFill = locked ? CaptionPlateColor : (filled ? TileSelected : PreviewEmpty);
                Color previewLabel = filled ? BodyTextColor : CaptionTextColor;
                Color previewAccent = AccentColor;
                if (filled)
                    ShipCommsCalloutGraphics.ResolveChipPaint(label, selected: true, out previewFill, out previewLabel, out previewAccent);
                if (slot.Fill != null)
                    slot.Fill.color = previewFill;
                if (slot.Outline != null)
                {
                    slot.Outline.effectColor = previewAccent;
                    slot.Outline.enabled = filled && !locked;
                }
                if (slot.Label != null)
                {
                    slot.Label.text = locked ? string.Empty : (filled ? label.ToUpperInvariant() : (i + 1).ToString());
                    slot.Label.color = previewLabel;
                }

                if (slot.Button != null)
                    slot.Button.interactable = !locked;
            }

            PaintUnlockGate(_slotUnlockGate, allowed < ShipCommsKeywordCatalog.MaxSequenceLength);

            bool commanderLive = CommanderKeywordsUnlocked();
            for (int t = 0; t < _tiles.Count; t++)
            {
                KeywordTile tile = _tiles[t];
                int order = IndexOfSequence(tile.Index);
                bool selected = order >= 0;
                string word = catalog.TryGetLabel(tile.Index, out string painted) ? painted : string.Empty;
                ShipCommsCalloutGraphics.ResolveChipPaint(word, selected, out Color fill, out Color labelColor, out Color accent);
                bool commanderLocked = tile.IsCommanderWord && !commanderLive;
                if (commanderLocked)
                {
                    // Dark plate + stamp so the tile cannot be mistaken for a live keyword.
                    fill = CommanderLockedFill;
                    labelColor = CommanderLockedWord;
                    accent = CommanderLockedStamp;
                    selected = false;
                }
                if (tile.Fill != null)
                    tile.Fill.color = fill;
                if (tile.Outline != null)
                {
                    tile.Outline.effectColor = accent;
                    tile.Outline.enabled = selected;
                }
                if (tile.Caret != null)
                {
                    tile.Caret.color = accent;
                    tile.Caret.enabled = selected;
                }
                if (tile.OrderBadge != null)
                {
                    tile.OrderBadge.text = selected ? (order + 1).ToString() : string.Empty;
                    tile.OrderBadge.color = accent;
                    tile.OrderBadge.enabled = selected;
                }
                if (tile.Label != null)
                {
                    string tileText = word;
                    if (ShipCommsCalloutGraphics.TryResolvePendingHereDisplayLabel(word, out string planetName))
                        tileText = planetName;
                    tile.Label.text = tileText.ToUpperInvariant();
                    tile.Label.color = labelColor;
                }
                if (tile.LockLabel != null)
                {
                    tile.LockLabel.enabled = commanderLocked;
                    tile.LockLabel.text = commanderLocked ? "LOCK" : string.Empty;
                    tile.LockLabel.color = CommanderLockedStamp;
                }
                if (tile.Button != null)
                    tile.Button.interactable = !commanderLocked;
            }
        }

        /// <summary>First sequence slot holding <paramref name="index"/>, or -1.</summary>
        int IndexOfSequence(byte index)
        {
            for (int i = 0; i < _sequence.Count; i++)
            {
                if (_sequence[i] == index)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Fills the RECENT column from <see cref="ShipCommsHistory"/>. Rows past the
        /// free first three stay ghosted under one unlock plate until that video runs.
        /// </summary>
        void PaintRecent()
        {
            var catalog = ShipCommsKeywordCatalog.LoadDefault();
            for (int i = 0; i < _recent.Length; i++)
            {
                RecentSlot slot = _recent[i];
                if (slot == null)
                    continue;

                bool locked = !ShipCommsClientState.IsRecentRowUnlocked(i);
                bool hasSentence = ShipCommsHistory.TryGet(i, out ShipCommsHistory.Sentence sentence);
                bool filled = hasSentence;
                bool selected = !locked && filled && i == _recentCursor;
                if (slot.Fill != null)
                    slot.Fill.color = locked
                        ? CaptionPlateColor
                        : (selected
                            ? Color.Lerp(TileSelected, AllChannelColor, 0.42f)
                            : (filled ? TileSelected : PreviewEmpty));
                if (slot.Outline != null)
                {
                    slot.Outline.effectColor = AllChannelColor;
                    slot.Outline.effectDistance = selected ? new Vector2(2f, -2f) : new Vector2(0.8f, -0.8f);
                    slot.Outline.enabled = selected;
                }
                if (slot.Caret != null)
                {
                    slot.Caret.color = AllChannelColor;
                    slot.Caret.enabled = selected;
                }
                if (slot.Button != null)
                    slot.Button.interactable = !locked && filled;

                for (int c = 0; c < slot.Chips.Length; c++)
                {
                    bool on = filled && c < sentence.Count;
                    PaintRecentChip(
                        slot.Chips[c],
                        catalog,
                        on ? SentenceKeyword(in sentence, c) : (byte)0,
                        on,
                        ghost: locked);
                }
            }

            PaintUnlockGate(
                _recentUnlockGate,
                !ShipCommsClientState.RecentRowsUnlocked
                    && _recent.Length > ShipCommsClientState.FreeRecentRows);
        }

        /// <summary>Paints one RECENT chip like a selected matrix / preview tile.</summary>
        /// <param name="ghost">True under the unlock veil so paid rows still preview the payoff.</param>
        static void PaintRecentChip(RecentChip chip, ShipCommsKeywordCatalog catalog, byte index, bool on, bool ghost)
        {
            if (chip.Fill != null)
                chip.Fill.gameObject.SetActive(on);
            if (!on)
                return;

            string label = catalog.TryGetLabel(index, out string word) ? word : "?";
            ShipCommsCalloutGraphics.ResolveChipPaint(label, selected: true, out Color fill, out Color body, out Color accent);
            if (ghost)
            {
                fill = Color.Lerp(fill, CaptionPlateColor, 0.55f);
                fill.a *= 0.55f;
                body = Color.Lerp(body, CaptionTextColor, 0.45f);
                body.a *= 0.55f;
                accent.a *= 0.35f;
            }

            if (chip.Fill != null)
                chip.Fill.color = fill;
            if (chip.Outline != null)
            {
                chip.Outline.effectColor = accent;
                chip.Outline.enabled = !ghost;
            }
            if (chip.Label != null)
            {
                chip.Label.text = label.ToUpperInvariant();
                chip.Label.color = body;
            }
        }

        static byte SentenceKeyword(in ShipCommsHistory.Sentence sentence, int slot)
        {
            switch (slot)
            {
                case 0: return sentence.K0;
                case 1: return sentence.K1;
                case 2: return sentence.K2;
                case 3: return sentence.K3;
                case 4: return sentence.K4;
                default: return 0;
            }
        }

        /// <summary>
        /// Builds a fixed-size card in the middle of the screen: compact title, 1/2/3 rail,
        /// keyword grids, and a RECENT column that fills the right rail to the bottom pad.
        /// </summary>
        void BuildUi()
        {
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 150;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();

            _group = gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;

            var catalog = ShipCommsKeywordCatalog.LoadDefault();
            IReadOnlyList<ShipCommsKeyword> words = catalog.GetEffectiveKeywords();

            float mainW = KeywordColumns * TileWidth + (KeywordColumns - 1) * TileGap;
            float overlayW = PanelPad + mainW + RootGap + RecentColWidth + PanelPad;
            float overlayH = PanelPad
                + HeaderHeight + SectionGap
                + TileHeight + SectionGap
                + MeasureClusterStack(words)
                + PanelPad;
            // --- RECENT fill ---
            // [TITAN-ORBIT] The keyword grid owns panel height. Pack as many RECENT
            // rows as fit so the list meets the bottom pad instead of stopping at 10.
            float recentInnerH = overlayH - PanelPad * 2f;
            int recentRows = CountRecentRowsThatFill(recentInnerH);
            ShipCommsHistory.BindCapacity(recentRows);
            _recent = new RecentSlot[recentRows];
            _overlayW = overlayW;
            _dockSize = overlayH;

            var panelGo = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panelGo.transform.SetParent(transform, false);
            _panel = panelGo.GetComponent<RectTransform>();
            _panel.anchorMin = new Vector2(0.5f, 0.5f);
            _panel.anchorMax = new Vector2(0.5f, 0.5f);
            _panel.pivot = new Vector2(0.5f, 0.5f);
            _panel.anchoredPosition = Vector2.zero;
            _panel.sizeDelta = new Vector2(overlayW, overlayH);

            var panelImage = panelGo.GetComponent<Image>();
            panelImage.color = FillColor;
            panelImage.raycastTarget = true;

            BuildSciFiChrome(_panel);
            RectTransform main = CreateTopLeft(_panel, "Main", PanelPad, PanelPad, mainW, overlayH - PanelPad * 2f);
            float y = 0f;
            BuildHeader(main, ref y, mainW);
            y += SectionGap;
            BuildPreviewRail(main, ref y);
            y += SectionGap;
            BuildClusterStack(main, ref y, mainW, words);

            BuildMinimapDock();

            RectTransform recent = CreateTopLeft(
                _panel,
                "Recent",
                PanelPad + mainW + RootGap,
                PanelPad,
                RecentColWidth,
                overlayH - PanelPad * 2f);
            BuildRecentColumn(recent, recentRows, recentInnerH);

            _built = true;
        }

        /// <summary>
        /// Dark-glass frame, cyan top rail, and L-brackets — same cockpit language as
        /// <see cref="ShipStatTooltipChrome"/>.
        /// </summary>
        static void BuildSciFiChrome(Transform parent)
        {
            var frame = CreateIgnoredImage(parent, "Frame", FrameTint);
            var frameRt = frame.rectTransform;
            frameRt.anchorMin = Vector2.zero;
            frameRt.anchorMax = Vector2.one;
            frameRt.offsetMin = Vector2.zero;
            frameRt.offsetMax = Vector2.zero;

            var accent = CreateIgnoredImage(parent, "Accent", AccentColor);
            var accentRt = accent.rectTransform;
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot = new Vector2(0.5f, 1f);
            accentRt.sizeDelta = new Vector2(-16f, 2f);
            accentRt.anchoredPosition = Vector2.zero;

            AddCornerBracket(parent, "TL", new Vector2(0f, 1f), new Vector2(6f, -6f), true, true);
            AddCornerBracket(parent, "TR", new Vector2(1f, 1f), new Vector2(-6f, -6f), false, true);
            AddCornerBracket(parent, "BL", new Vector2(0f, 0f), new Vector2(6f, 6f), true, false);
            AddCornerBracket(parent, "BR", new Vector2(1f, 0f), new Vector2(-6f, 6f), false, false);
        }

        /// <summary>
        /// Two-line header: COMMS MATRIX + HOLD S · N WORDS, with an All / Team /
        /// Commander channel switch on the right. The switch is remembered in PlayerPrefs.
        /// </summary>
        void BuildHeader(Transform parent, ref float y, float width)
        {
            RectTransform plate = CreateTopLeft(parent, "Header", 0f, y, width, HeaderHeight);
            var bg = plate.gameObject.AddComponent<Image>();
            bg.color = CaptionPlateColor;
            bg.raycastTarget = false;

            // Leave room on the right for the All / Team / CMDR pills (three tiles + gaps).
            float toggleReserve = AudienceToggleWidth * 3f + AudienceToggleGap * 2f + 10f;

            var title = CreateLabel(plate, "Title", "COMMS MATRIX", 11f, AccentColor, TextAlignmentOptions.Left);
            var titleRt = title.rectTransform;
            titleRt.anchorMin = new Vector2(0f, 0.42f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.offsetMin = new Vector2(8f, 0f);
            titleRt.offsetMax = new Vector2(-toggleReserve, -1f);
            title.characterSpacing = 1.8f;
            title.fontStyle = FontStyles.Bold;

            _headerSub = CreateLabel(plate, "Sub", "HOLD S  ·  3 WORDS  ·  ALL", 8f, CaptionTextColor, TextAlignmentOptions.Left);
            var subRt = _headerSub.rectTransform;
            subRt.anchorMin = new Vector2(0f, 0f);
            subRt.anchorMax = new Vector2(1f, 0.48f);
            subRt.offsetMin = new Vector2(8f, 1f);
            subRt.offsetMax = new Vector2(-toggleReserve, 0f);
            _headerSub.characterSpacing = 0.8f;

            BuildAudienceToggle(plate, width);
            PaintAudience();

            y += HeaderHeight;
        }

        /// <summary>
        /// Two compact pills on the header's right: ALL (everyone), TEAM (teammates),
        /// and CMDR (command deck). Clicking one writes <see cref="ShipCommsClientState.SetChannel"/>.
        /// </summary>
        void BuildAudienceToggle(RectTransform header, float headerWidth)
        {
            float commanderX = headerWidth - AudienceToggleWidth - 6f;
            float teamX = commanderX - AudienceToggleGap - AudienceToggleWidth;
            float allX = teamX - AudienceToggleGap - AudienceToggleWidth;
            float y = (HeaderHeight - TileHeight + 8f) * 0.5f;
            float h = HeaderHeight - 6f;

            _allFill = CreateTile(header, "AllChannel", allX, y, AudienceToggleWidth, h, TileIdle);
            _allLabel = CreateLabel(_allFill.transform, "Label", "ALL", 9f, BodyTextColor, TextAlignmentOptions.Center);
            Stretch(_allLabel.rectTransform, 2f);
            _allOutline = _allFill.gameObject.AddComponent<Outline>();
            _allOutline.effectColor = AllChannelColor;
            _allOutline.effectDistance = new Vector2(1f, -1f);
            _allOutline.useGraphicAlpha = false;
            _allFill.gameObject.GetComponent<Button>().onClick.AddListener(
                () => OnAudienceClicked(ShipCommsChannel.All));

            _teamFill = CreateTile(header, "TeamChannel", teamX, y, AudienceToggleWidth, h, TileIdle);
            _teamLabel = CreateLabel(_teamFill.transform, "Label", "TEAM", 9f, BodyTextColor, TextAlignmentOptions.Center);
            Stretch(_teamLabel.rectTransform, 2f);
            _teamOutline = _teamFill.gameObject.AddComponent<Outline>();
            _teamOutline.effectColor = AllChannelColor;
            _teamOutline.effectDistance = new Vector2(1f, -1f);
            _teamOutline.useGraphicAlpha = false;
            _teamFill.gameObject.GetComponent<Button>().onClick.AddListener(
                () => OnAudienceClicked(ShipCommsChannel.Team));

            _commanderFill = CreateTile(header, "CommanderChannel", commanderX, y, AudienceToggleWidth, h, TileIdle);
            _commanderLabel = CreateLabel(
                _commanderFill.transform, "Label", "CMDR", 9f, BodyTextColor, TextAlignmentOptions.Center);
            Stretch(_commanderLabel.rectTransform, 2f);
            _commanderOutline = _commanderFill.gameObject.AddComponent<Outline>();
            _commanderOutline.effectColor = CommanderChannelColor;
            _commanderOutline.effectDistance = new Vector2(1f, -1f);
            _commanderOutline.useGraphicAlpha = false;
            _commanderFill.gameObject.GetComponent<Button>().onClick.AddListener(
                () => OnAudienceClicked(ShipCommsChannel.Commander));
        }

        /// <summary>
        /// Header pill click: remember All / Team / Commander and repaint the switch.
        /// Safe to tap while composing — it does not clear the 1/2/3 rail unless the
        /// new channel locks command-deck words that were already picked.
        /// </summary>
        /// <param name="channel">Audience the next send should request.</param>
        void OnAudienceClicked(ShipCommsChannel channel)
        {
            if (!ShipCommsClientState.IsOpen)
                return;

            // Non-commanders can see the CMDR pill but cannot arm it.
            if (channel == ShipCommsChannel.Commander && !IsLocalCommander())
                return;

            ShipCommsClientState.SetChannel(channel);
            if (channel != ShipCommsChannel.Commander)
                StripCommanderKeywordsFromSequence();
            PaintAudience();
            PaintSequence();
        }

        /// <summary>
        /// Local faction RGB for the TEAM pill — same palette as world-chip Team frames.
        /// White until Join Team so the pill is not the old amber stand-in.
        /// </summary>
        static Color ResolveLocalTeamAccent()
        {
            TeamId team = ClientTeamFlowState.ResolvePresentationTeam(TeamId.None);
            return team != TeamId.None ? team.ToColor() : AllChannelColor;
        }

        /// <summary>
        /// Highlights the active All / Team / CMDR pill and updates the HOLD S subtitle
        /// so the channel is readable without staring at the switch. The CMDR pill
        /// stays dim when this machine is not in the top three.
        /// </summary>
        void PaintAudience()
        {
            ShipCommsChannel channel = ResolveSendableChannel();
            Color teamAccent = ResolveLocalTeamAccent();
            bool commanderEligible = IsLocalCommander();
            bool allOn = channel == ShipCommsChannel.All;
            bool teamOn = channel == ShipCommsChannel.Team;
            bool commanderOn = channel == ShipCommsChannel.Commander;

            if (_allFill != null)
                _allFill.color = allOn ? Color.Lerp(TileSelected, AllChannelColor, 0.22f) : TileIdle;
            if (_teamFill != null)
                _teamFill.color = teamOn ? Color.Lerp(TileSelected, teamAccent, 0.35f) : TileIdle;
            if (_commanderFill != null)
            {
                _commanderFill.color = commanderOn
                    ? Color.Lerp(TileSelected, CommanderChannelColor, 0.38f)
                    : (commanderEligible ? TileIdle : Color.Lerp(TileIdle, CaptionPlateColor, 0.4f));
            }

            if (_allOutline != null)
            {
                _allOutline.effectColor = AllChannelColor;
                _allOutline.enabled = allOn;
            }
            if (_teamOutline != null)
            {
                _teamOutline.effectColor = teamAccent;
                _teamOutline.enabled = teamOn;
            }
            if (_commanderOutline != null)
            {
                _commanderOutline.effectColor = CommanderChannelColor;
                _commanderOutline.enabled = commanderOn;
            }

            if (_allLabel != null)
                _allLabel.color = allOn ? AllChannelColor : CaptionTextColor;
            if (_teamLabel != null)
                _teamLabel.color = teamOn ? teamAccent : CaptionTextColor;
            if (_commanderLabel != null)
            {
                _commanderLabel.color = commanderOn
                    ? CommanderChannelColor
                    : (commanderEligible ? CaptionTextColor : new Color(0.40f, 0.44f, 0.50f, 0.85f));
            }

            if (_headerSub != null)
            {
                int allowed = ShipCommsClientState.AllowedSequenceLength;
                string words = allowed <= 3 ? "3 WORDS" : allowed + " WORDS";
                string audience = allOn ? "ALL" : (commanderOn ? "CMDR" : "TEAM");
                _headerSub.text = "HOLD S  ·  " + words + "  ·  " + audience;
            }
        }

        /// <summary>
        /// Drops the Commander channel (and any command-deck words in the rail) when
        /// this machine is no longer in the top three. Called while S is held.
        /// </summary>
        void EnsureCommanderChannelStillValid()
        {
            if (ShipCommsClientState.Channel != ShipCommsChannel.Commander)
                return;
            if (IsLocalCommander())
                return;

            ShipCommsClientState.SetChannel(ShipCommsChannel.Team);
            StripCommanderKeywordsFromSequence();
            PaintAudience();
            PaintSequence();
        }

        /// <summary>
        /// Channel the next send may actually request. Commander collapses to Team
        /// when this machine is not on the command deck.
        /// </summary>
        static ShipCommsChannel ResolveSendableChannel()
        {
            ShipCommsChannel channel = ShipCommsClientState.Channel;
            if (channel == ShipCommsChannel.Commander && !IsLocalCommander())
                return ShipCommsChannel.Team;
            return channel;
        }

        /// <summary>
        /// True when command-deck tiles may enter the sentence: this machine is a
        /// top-three commander <b>and</b> the CMDR pill is armed.
        /// </summary>
        static bool CommanderKeywordsUnlocked()
        {
            return ResolveSendableChannel() == ShipCommsChannel.Commander && IsLocalCommander();
        }

        /// <summary>
        /// True when the local player is one of the top three scorers on their team.
        /// Prefers the live minimap list (same sort as the leaderboard, includes dead
        /// hulls). Falls back to the last nameplate rank flush when the map is empty.
        /// </summary>
        static bool IsLocalCommander()
        {
            int localId = EcsGameBridge.GetLocalNetworkId();
            if (localId <= 0)
                return false;

            if (TryGetLocalCommanderFromMinimap(localId, out bool fromMap))
                return fromMap;

            return ShipMatchScoreLogic.IsCommander(localId);
        }

        /// <summary>
        /// Ranks the local team from minimap ship anchors. Same score weights and
        /// NetworkId tie-break as <see cref="TeamLeaderboardHUD"/>. Returns false
        /// when the cache has no teammates yet (join / first frames).
        /// </summary>
        /// <param name="localId">Local GhostOwner.NetworkId.</param>
        /// <param name="isCommander">True when localId is rank 1–3 on that team.</param>
        static bool TryGetLocalCommanderFromMinimap(int localId, out bool isCommander)
        {
            isCommander = false;
            var sync = MinimapEcsEntitySync.Instance;
            IReadOnlyList<MinimapBlipAnchor> ships = sync != null ? sync.Ships : null;
            if (ships == null || ships.Count == 0)
                return false;

            TeamId team = TeamId.None;
            if (EcsGameBridge.TryGetLocalShipState(out var localShip))
                team = localShip.Team;
            if (team == TeamId.None)
                team = ClientTeamFlowState.ResolvePresentationTeam(TeamId.None);
            if (team == TeamId.None)
                return false;

            int betterOrEqual = 0;
            int myScore = -1;
            int seen = 0;
            for (int i = 0; i < ships.Count; i++)
            {
                MinimapBlipAnchor a = ships[i];
                if (a == null || a.Kind != MinimapBlipKind.Ship)
                    continue;
                if (a.Team != team || a.AwaitingTeamSelection || a.OwnerNetworkId <= 0)
                    continue;

                seen++;
                int score = ShipMatchScoreLogic.ComputeCombinedScore(
                    Mathf.Max(0, a.Kills),
                    Mathf.Max(0, a.GemsDeposited),
                    Mathf.Max(0, a.PeopleDelivered));
                if (a.OwnerNetworkId == localId)
                    myScore = score;
            }

            if (seen == 0 || myScore < 0)
                return false;

            // Rank = 1 + how many teammates sort strictly ahead (score desc, id asc).
            for (int i = 0; i < ships.Count; i++)
            {
                MinimapBlipAnchor a = ships[i];
                if (a == null || a.Kind != MinimapBlipKind.Ship)
                    continue;
                if (a.Team != team || a.AwaitingTeamSelection || a.OwnerNetworkId <= 0)
                    continue;
                if (a.OwnerNetworkId == localId)
                    continue;

                int score = ShipMatchScoreLogic.ComputeCombinedScore(
                    Mathf.Max(0, a.Kills),
                    Mathf.Max(0, a.GemsDeposited),
                    Mathf.Max(0, a.PeopleDelivered));
                bool ahead = score > myScore
                    || (score == myScore && a.OwnerNetworkId < localId);
                if (ahead)
                    betterOrEqual++;
            }

            isCommander = TeamCommanderRules.IsCommanderRank(betterOrEqual + 1);
            return true;
        }

        /// <summary>Removes command-deck words from the current compose rail.</summary>
        void StripCommanderKeywordsFromSequence()
        {
            var catalog = ShipCommsKeywordCatalog.LoadDefault();
            for (int i = _sequence.Count - 1; i >= 0; i--)
            {
                if (catalog.IsCommanderKeyword(_sequence[i]))
                    _sequence.RemoveAt(i);
            }
        }

        /// <summary>True when the current rail holds at least one command-deck word.</summary>
        bool SequenceHasCommanderKeyword()
        {
            var catalog = ShipCommsKeywordCatalog.LoadDefault();
            for (int i = 0; i < _sequence.Count; i++)
            {
                if (catalog.IsCommanderKeyword(_sequence[i]))
                    return true;
            }

            return false;
        }

        /// <summary>Five clickable sequence chips. Slots 4–5 start under one unlock plate.</summary>
        void BuildPreviewRail(Transform parent, ref float y)
        {
            float railW = ShipCommsKeywordCatalog.MaxSequenceLength * TileWidth
                + (ShipCommsKeywordCatalog.MaxSequenceLength - 1) * TileGap;
            RectTransform rail = CreateTopLeft(parent, "PreviewRail", 0f, y, railW, TileHeight);

            for (int i = 0; i < _preview.Length; i++)
            {
                int slot = i;
                float x = i * (TileWidth + TileGap);
                Image tile = CreateTile(rail, "Preview" + (i + 1), x, 0f, TileWidth, TileHeight, PreviewEmpty);
                var label = CreateLabel(tile.transform, "Label", (i + 1).ToString(), 10f, CaptionTextColor, TextAlignmentOptions.Center);
                Stretch(label.rectTransform, 4f);
                label.enableWordWrapping = false;
                label.overflowMode = TextOverflowModes.Ellipsis;
                var outline = tile.gameObject.AddComponent<Outline>();
                outline.effectColor = AccentColor;
                outline.effectDistance = new Vector2(1.2f, -1.2f);
                outline.useGraphicAlpha = false;
                outline.enabled = false;

                var btn = tile.gameObject.GetComponent<Button>();
                btn.onClick.AddListener(() => OnPreviewClicked(slot));

                _preview[i] = new PreviewSlot
                {
                    Fill = tile,
                    Label = label,
                    Outline = outline,
                    Button = btn,
                };
            }

            // --- One ad, both extra slots ---
            // [TITAN-ORBIT] A stamp on 4 and another on 5 reads as two videos.
            // One plate over both chips is the same unlock as TryUnlockNextSlot.
            int lockedStart = ShipCommsKeywordCatalog.DefaultSequenceLength;
            int lockedCount = ShipCommsKeywordCatalog.MaxSequenceLength - lockedStart;
            if (lockedCount > 0)
            {
                float gateX = lockedStart * (TileWidth + TileGap);
                float gateW = lockedCount * TileWidth + (lockedCount - 1) * TileGap;
                _slotUnlockGate = CreateUnlockGate(
                    rail,
                    "UnlockSlots",
                    gateX,
                    0f,
                    gateW,
                    TileHeight,
                    compact: true,
                    title: "WATCH AD · UNLOCK ALL",
                    sub: string.Empty,
                    onClick: TryUnlockNextSlot);
            }

            y += TileHeight;
        }

        /// <summary>
        /// Right-rail of saved sentences. Row count fills the card; leftover height
        /// becomes even gaps so the last chip sits on the bottom pad.
        /// </summary>
        /// <param name="parent">RECENT column rect (already sized to the card inner height).</param>
        /// <param name="rowCount">Slots to build — same value bound on <see cref="ShipCommsHistory"/>.</param>
        /// <param name="innerHeight">Column height in canvas units (panel minus pads).</param>
        void BuildRecentColumn(RectTransform parent, int rowCount, float innerHeight)
        {
            var rail = CreateIgnoredImage(parent, "Rail", SeparatorColor);
            var railRt = rail.rectTransform;
            railRt.anchorMin = new Vector2(0f, 0f);
            railRt.anchorMax = new Vector2(0f, 1f);
            railRt.pivot = new Vector2(0f, 0.5f);
            railRt.sizeDelta = new Vector2(1f, 0f);
            railRt.anchoredPosition = new Vector2(-6f, 0f);

            var banner = CreateLabel(parent, "RecentBanner", "RECENT", 8f, CaptionTextColor, TextAlignmentOptions.Left);
            var bannerRt = banner.rectTransform;
            bannerRt.anchorMin = new Vector2(0f, 1f);
            bannerRt.anchorMax = new Vector2(1f, 1f);
            bannerRt.pivot = new Vector2(0f, 1f);
            bannerRt.anchoredPosition = Vector2.zero;
            bannerRt.sizeDelta = new Vector2(0f, BannerHeight);
            banner.characterSpacing = 1.4f;

            // --- Even gaps ---
            // n * row + n * gap = space under the banner, so the last row's bottom
            // lands on the column edge instead of leaving a dead strip.
            float gap = ComputeRecentRowGap(innerHeight, rowCount);
            float y = BannerHeight + gap;
            for (int i = 0; i < rowCount; i++)
            {
                int slot = i;
                Image tile = CreateTile(parent, "Recent" + i, 0f, y, RecentColWidth, RecentRowHeight, PreviewEmpty);
                var caretGo = new GameObject("Caret", typeof(RectTransform), typeof(Image));
                caretGo.transform.SetParent(tile.transform, false);
                var caretRt = caretGo.GetComponent<RectTransform>();
                caretRt.anchorMin = new Vector2(0f, 0f);
                caretRt.anchorMax = new Vector2(0f, 1f);
                caretRt.pivot = new Vector2(0f, 0.5f);
                caretRt.sizeDelta = new Vector2(4f, -4f);
                caretRt.anchoredPosition = new Vector2(2f, 0f);
                var caret = caretGo.GetComponent<Image>();
                caret.color = AllChannelColor;
                caret.raycastTarget = false;
                caret.enabled = false;
                var outline = tile.gameObject.AddComponent<Outline>();
                outline.effectColor = AllChannelColor;
                outline.effectDistance = new Vector2(2f, -2f);
                outline.useGraphicAlpha = false;
                outline.enabled = false;
                var btn = tile.gameObject.GetComponent<Button>();
                btn.onClick.AddListener(() => OnRecentClicked(slot));
                btn.interactable = false;

                var row = new RecentSlot
                {
                    Fill = tile,
                    Caret = caret,
                    Outline = outline,
                    Button = btn,
                };
                for (int c = 0; c < row.Chips.Length; c++)
                    row.Chips[c] = CreateRecentChip(tile.transform, c);
                _recent[i] = row;
                y += RecentRowHeight + gap;
            }

            // --- One ad, every paid row ---
            // [TITAN-ORBIT] An AD stamp on each extra row reads as one video per
            // sentence. One veil over the whole paid block is the same unlock.
            int lockedStart = ShipCommsClientState.FreeRecentRows;
            if (rowCount > lockedStart)
            {
                float lockedY = BannerHeight + gap + lockedStart * (RecentRowHeight + gap);
                float lockedH = (rowCount - lockedStart) * (RecentRowHeight + gap) - gap;
                _recentUnlockGate = CreateUnlockGate(
                    parent,
                    "UnlockRecent",
                    0f,
                    lockedY,
                    RecentColWidth,
                    Mathf.Max(RecentRowHeight, lockedH),
                    compact: lockedH < 48f,
                    title: lockedH < 48f ? "WATCH AD · UNLOCK ALL" : "WATCH AD",
                    sub: "UNLOCK ALL",
                    onClick: () => TryUnlockRecentRows(-1));
            }
        }

        /// <summary>
        /// How many RECENT rows fit under the banner using the compact chip gap.
        /// Extra leftover height is later turned into even spacing, not more rows.
        /// </summary>
        /// <param name="innerHeight">Column height in canvas units (panel minus pads).</param>
        /// <returns>At least 1, at most <see cref="ShipCommsHistory.HardMaxEntries"/>.</returns>
        static int CountRecentRowsThatFill(float innerHeight)
        {
            // Banner, then n stacks of (gap + row). Floor so we never overflow the card.
            float budget = innerHeight - BannerHeight;
            float stride = RecentRowHeight + RecentChipGap;
            if (budget < stride)
                return 1;

            int n = Mathf.FloorToInt(budget / stride);
            return Mathf.Clamp(n, 1, ShipCommsHistory.HardMaxEntries);
        }

        /// <summary>
        /// Gap after the banner and between rows so the last chip sits on the column bottom.
        /// </summary>
        /// <param name="innerHeight">Column height in canvas units (panel minus pads).</param>
        /// <param name="rowCount">Slots actually built.</param>
        static float ComputeRecentRowGap(float innerHeight, int rowCount)
        {
            if (rowCount < 1)
                return RecentChipGap;

            float leftover = innerHeight - BannerHeight - rowCount * RecentRowHeight;
            float gap = leftover / rowCount;
            return Mathf.Max(RecentChipGap, gap);
        }

        /// <summary>One matrix-sized chip inside a RECENT row. Clicks go through to the row.</summary>
        static RecentChip CreateRecentChip(Transform parent, int slot)
        {
            float x = RecentRowPadLeft + slot * (RecentChipWidth + RecentChipGap);
            var go = new GameObject("Chip" + slot, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(x, 0f);
            rt.sizeDelta = new Vector2(RecentChipWidth, RecentChipHeight);
            var fill = go.GetComponent<Image>();
            fill.color = TileSelected;
            fill.raycastTarget = false;

            var label = CreateLabel(go.transform, "Label", string.Empty, RecentChipFont, BodyTextColor, TextAlignmentOptions.Center);
            Stretch(label.rectTransform, 2f);
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;

            var outline = go.AddComponent<Outline>();
            outline.effectColor = AccentColor;
            outline.effectDistance = new Vector2(1.1f, -1.1f);
            outline.useGraphicAlpha = false;
            outline.enabled = false;

            go.SetActive(false);
            return new RecentChip
            {
                Fill = fill,
                Label = label,
                Outline = outline,
            };
        }

        /// <summary>
        /// Walks <see cref="ClusterLayout"/> top to bottom. Each family opens with
        /// the old TACTICAL / SUBJECT rail; themed rows inside it use a slim caption.
        /// </summary>
        void BuildClusterStack(
            Transform parent,
            ref float y,
            float width,
            IReadOnlyList<ShipCommsKeyword> words)
        {
            MatrixFamily shown = (MatrixFamily)255;
            for (int i = 0; i < ClusterLayout.Length; i++)
            {
                ClusterSpec spec = ClusterLayout[i];
                if (spec.Family != shown)
                {
                    Color sectionColor = spec.CommanderChrome ? CommanderChannelColor : AccentColor;
                    BuildSectionHeader(parent, ref y, width, FamilyTitle(spec.Family), sectionColor);
                    shown = spec.Family;
                }

                BuildCategory(parent, ref y, width, spec, words);
                if (i >= ClusterLayout.Length - 1)
                    continue;
                y += spec.FamilyBreakAfter ? FamilyGap : ThemeRowGap;
            }
        }

        /// <summary>
        /// Cockpit section rail: left pip, <c>&gt; TACTICAL</c>, and a hairline.
        /// Same language as <see cref="ShipStatTooltipChrome.AppendSectionBanner"/>.
        /// </summary>
        static void BuildSectionHeader(Transform parent, ref float y, float width, string title, Color accent)
        {
            RectTransform plate = CreateTopLeft(parent, title + "Section", 0f, y, width, SectionBannerHeight);

            var pip = CreateIgnoredImage(plate, "Pip", accent);
            var pipRt = pip.rectTransform;
            pipRt.anchorMin = new Vector2(0f, 0.5f);
            pipRt.anchorMax = new Vector2(0f, 0.5f);
            pipRt.pivot = new Vector2(0f, 0.5f);
            pipRt.anchoredPosition = new Vector2(0f, 1f);
            pipRt.sizeDelta = new Vector2(2f, 9f);

            var label = CreateLabel(plate, "Title", "> " + title, 8f, accent, TextAlignmentOptions.Left);
            var labelRt = label.rectTransform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(6f, 2f);
            labelRt.offsetMax = new Vector2(0f, -1f);
            label.characterSpacing = 2.4f;
            label.fontStyle = FontStyles.Bold;

            Color rail = accent;
            rail.a *= 0.38f;
            var rule = CreateIgnoredImage(plate, "Rule", rail);
            var ruleRt = rule.rectTransform;
            ruleRt.anchorMin = new Vector2(0f, 0f);
            ruleRt.anchorMax = new Vector2(1f, 0f);
            ruleRt.pivot = new Vector2(0.5f, 0f);
            ruleRt.sizeDelta = new Vector2(0f, 1f);
            ruleRt.anchoredPosition = Vector2.zero;

            y += SectionBannerHeight;
        }

        /// <summary>
        /// Micro telemetry tag for a themed row (<c>// STRIKE</c> plus a dim rail).
        /// Stays smaller than the TACTICAL / SUBJECT section so grouping does not shout.
        /// </summary>
        static void BuildThemeCaption(Transform parent, ref float y, float width, string title, Color accent)
        {
            RectTransform row = CreateTopLeft(parent, title + "Theme", 0f, y, width, ThemeCaptionHeight);

            Color caption = Color.Lerp(CaptionTextColor, accent, 0.28f);
            caption.a = 0.78f;
            var label = CreateLabel(row, "Caption", "// " + title, 6.25f, caption, TextAlignmentOptions.Left);
            var labelRt = label.rectTransform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;
            label.characterSpacing = 2.6f;

            Color rail = SeparatorColor;
            rail.a = 0.7f;
            var rule = CreateIgnoredImage(row, "Rail", rail);
            var ruleRt = rule.rectTransform;
            ruleRt.anchorMin = new Vector2(0f, 0.5f);
            ruleRt.anchorMax = new Vector2(1f, 0.5f);
            ruleRt.pivot = new Vector2(0f, 0.5f);
            ruleRt.offsetMin = new Vector2(68f, -0.5f);
            ruleRt.offsetMax = new Vector2(0f, 0.5f);

            y += ThemeCaptionHeight;
        }

        /// <summary>Optional slim theme caption plus a wrapping 5-wide row of tiles.</summary>
        void BuildCategory(
            Transform parent,
            ref float y,
            float width,
            ClusterSpec spec,
            IReadOnlyList<ShipCommsKeyword> words)
        {
            if (FamilyHasMultipleClusters(spec.Family))
            {
                Color themeAccent = spec.CommanderChrome ? CommanderChannelColor : AccentColor;
                BuildThemeCaption(parent, ref y, width, spec.Banner, themeAccent);
            }

            int placed = 0;
            int[] order = CollectClusterOrder(words, spec.Id);
            for (int p = 0; p < order.Length; p++)
            {
                int i = order[p];
                int col = placed % KeywordColumns;
                int row = placed / KeywordColumns;
                float x = col * (TileWidth + TileGap);
                float tileY = y + row * (TileHeight + TileGap);
                KeywordTile tile = CreateKeywordTile(parent, (byte)i, words[i].label, x, tileY);
                _tiles.Add(tile);
                placed++;
            }

            int rows = Mathf.Max(1, Mathf.CeilToInt(placed / (float)KeywordColumns));
            y += rows * TileHeight + Mathf.Max(0, rows - 1) * TileGap;
        }

        /// <summary>One clickable keyword chip with an optional 1/2/3 order badge.</summary>
        KeywordTile CreateKeywordTile(Transform parent, byte index, string label, float x, float y)
        {
            // --- Faction tint ---
            // [TITAN-ORBIT] Red / Blue / Green / Orange / Purple use the same RGB as hulls
            // so "Attack Purple Base" is scannable in the SUBJECT grid.
            ShipCommsCalloutGraphics.ResolveChipPaint(label, selected: false, out Color idleFill, out Color labelColor, out Color accent);
            bool isTeamColor = TeamIdExtensions.TryParseColorName(label, out _);
            bool isLineColor = !isTeamColor && ShipCommsCalloutGraphics.TryGetLineColor(label, out _);
            bool isCommanderWord = ShipCommsKeywordCatalog.IsCommanderLabel(label);
            Image fill = CreateTile(parent, "Kw" + index, x, y, TileWidth, TileHeight, idleFill);

            var caretGo = new GameObject("Caret", typeof(RectTransform), typeof(Image));
            caretGo.transform.SetParent(fill.transform, false);
            var caretRt = caretGo.GetComponent<RectTransform>();
            caretRt.anchorMin = new Vector2(0f, 0f);
            caretRt.anchorMax = new Vector2(0f, 1f);
            caretRt.pivot = new Vector2(0f, 0.5f);
            caretRt.sizeDelta = new Vector2(2f, 0f);
            caretRt.anchoredPosition = Vector2.zero;
            var caret = caretGo.GetComponent<Image>();
            caret.color = accent;
            caret.raycastTarget = false;
            caret.enabled = false;

            var text = CreateLabel(fill.transform, "Label", label.ToUpperInvariant(), 10f, labelColor, TextAlignmentOptions.Center);
            Stretch(text.rectTransform, 4f);
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;

            var badge = CreateLabel(fill.transform, "Order", string.Empty, 8f, accent, TextAlignmentOptions.TopRight);
            var badgeRt = badge.rectTransform;
            badgeRt.anchorMin = new Vector2(1f, 1f);
            badgeRt.anchorMax = new Vector2(1f, 1f);
            badgeRt.pivot = new Vector2(1f, 1f);
            badgeRt.sizeDelta = new Vector2(12f, 12f);
            badgeRt.anchoredPosition = new Vector2(-1f, -1f);
            badge.enabled = false;

            // Hidden until the command deck is locked so a live CMDR tile does not look gated.
            var lockLabel = CreateLabel(
                fill.transform, "Lock", "LOCK", 8f, CommanderLockedStamp, TextAlignmentOptions.Center);
            Stretch(lockLabel.rectTransform, 2f);
            lockLabel.characterSpacing = 1.2f;
            lockLabel.fontStyle = FontStyles.Bold;
            lockLabel.enabled = false;

            var outline = fill.gameObject.AddComponent<Outline>();
            outline.effectColor = accent;
            outline.effectDistance = new Vector2(1.1f, -1.1f);
            outline.useGraphicAlpha = false;
            outline.enabled = false;

            var btn = fill.gameObject.GetComponent<Button>();
            btn.onClick.AddListener(() => OnKeywordClicked(index));

            return new KeywordTile
            {
                Index = index,
                Fill = fill,
                Outline = outline,
                Label = text,
                OrderBadge = badge,
                LockLabel = lockLabel,
                Button = btn,
                Caret = caret,
                Accent = accent,
                IsTeamColor = isTeamColor,
                IsLineColor = isLineColor,
                IsCommanderWord = isCommanderWord,
            };
        }

        /// <summary>
        /// Card height of every section rail, slim theme caption, and tile row.
        /// Used once in <see cref="BuildUi"/> so the RECENT rail can fill the same height.
        /// </summary>
        static float MeasureClusterStack(IReadOnlyList<ShipCommsKeyword> words)
        {
            float height = 0f;
            MatrixFamily shown = (MatrixFamily)255;
            for (int i = 0; i < ClusterLayout.Length; i++)
            {
                ClusterSpec spec = ClusterLayout[i];
                if (spec.Family != shown)
                {
                    height += SectionBannerHeight;
                    shown = spec.Family;
                }

                if (FamilyHasMultipleClusters(spec.Family))
                    height += ThemeCaptionHeight;
                height += TileBlockHeight(CountCluster(words, spec.Id));
                if (i >= ClusterLayout.Length - 1)
                    continue;
                height += spec.FamilyBreakAfter ? FamilyGap : ThemeRowGap;
            }

            return height;
        }

        /// <summary>Player-facing rail for a top-level compose family.</summary>
        static string FamilyTitle(MatrixFamily family)
        {
            switch (family)
            {
                case MatrixFamily.Team: return "TEAM";
                case MatrixFamily.Tactical: return "TACTICAL";
                case MatrixFamily.Subject: return "SUBJECT";
                case MatrixFamily.Social: return "SOCIAL";
                case MatrixFamily.Commander: return "COMMANDER";
                default: return "COMMS";
            }
        }

        /// <summary>
        /// True when this family has more than one themed row, so STRIKE / WHO
        /// captions are worth the extra line. TEAM is a single row and skips them.
        /// </summary>
        static bool FamilyHasMultipleClusters(MatrixFamily family)
        {
            int n = 0;
            for (int i = 0; i < ClusterLayout.Length; i++)
            {
                if (ClusterLayout[i].Family != family)
                    continue;
                n++;
                if (n > 1)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Picks the themed 5-wide row for this catalog word. Hidden leftovers
        /// (old "Mine" / Subject "Transport") stay off the card. Color names
        /// stay on TEAM. Commander words stay on SQUAD / ORDERS — never WHO.
        /// A brand-new catalog label that is not in <see cref="ClusterThemeLabels"/>
        /// still shows: it lands on the last row of its family (WORK / GEAR /
        /// REACT / ORDERS).
        /// </summary>
        static bool TryGetCluster(in ShipCommsKeyword word, out MatrixCluster cluster)
        {
            cluster = MatrixCluster.Team;
            if (string.IsNullOrWhiteSpace(word.label))
                return false;
            if (ShipCommsKeywordCatalog.IsHiddenFromMatrix(in word))
                return false;

            if (TeamIdExtensions.TryParseColorName(word.label, out _))
            {
                cluster = MatrixCluster.Team;
                return true;
            }

            if (TryMatchThemeLabel(word.label, out cluster))
                return true;

            if (ShipCommsKeywordCatalog.IsCommanderWord(in word))
            {
                cluster = MatrixCluster.Orders;
                return true;
            }

            if (word.category == ShipCommsKeywordCategory.Tactical)
            {
                cluster = MatrixCluster.Work;
                return true;
            }

            if (word.category == ShipCommsKeywordCategory.Social)
            {
                cluster = MatrixCluster.React;
                return true;
            }

            if (word.category == ShipCommsKeywordCategory.Subject
                || word.category == ShipCommsKeywordCategory.Objects)
            {
                cluster = MatrixCluster.Gear;
                return true;
            }

            if (word.category == ShipCommsKeywordCategory.Commander)
            {
                cluster = MatrixCluster.Orders;
                return true;
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="label"/> is listed on a themed row.
        /// TEAM is skipped — those five color names use faction order, not this table.
        /// </summary>
        static bool TryMatchThemeLabel(string label, out MatrixCluster cluster)
        {
            for (int c = 1; c < ClusterThemeLabels.Length; c++)
            {
                string[] theme = ClusterThemeLabels[c];
                for (int i = 0; i < theme.Length; i++)
                {
                    if (!string.Equals(theme[i], label, StringComparison.OrdinalIgnoreCase))
                        continue;
                    cluster = (MatrixCluster)c;
                    return true;
                }
            }

            cluster = MatrixCluster.Team;
            return false;
        }

        /// <summary>
        /// Wire indices for one themed row. TEAM keeps catalog order (Red…Purple).
        /// Other clusters follow <see cref="ClusterThemeLabels"/>, then leftover
        /// unlisted words in catalog order. Indices themselves never move.
        /// </summary>
        static int[] CollectClusterOrder(IReadOnlyList<ShipCommsKeyword> words, MatrixCluster cluster)
        {
            int n = 0;
            for (int i = 0; i < words.Count; i++)
            {
                if (TryGetCluster(words[i], out MatrixCluster found) && found == cluster)
                    n++;
            }

            var order = new int[n];
            int w = 0;
            for (int i = 0; i < words.Count; i++)
            {
                if (!TryGetCluster(words[i], out MatrixCluster found) || found != cluster)
                    continue;
                order[w++] = i;
            }

            if (cluster == MatrixCluster.Team)
                return order;

            for (int a = 1; a < order.Length; a++)
            {
                int key = order[a];
                int keyRank = ThemeRank(cluster, words[key].label);
                int b = a - 1;
                while (b >= 0)
                {
                    int other = order[b];
                    int otherRank = ThemeRank(cluster, words[other].label);
                    if (otherRank < keyRank || (otherRank == keyRank && other < key))
                        break;
                    order[b + 1] = other;
                    b--;
                }

                order[b + 1] = key;
            }

            return order;
        }

        /// <summary>
        /// Left-to-right slot on this themed row. Unlisted leftovers sort after
        /// the designed five so a newly appended catalog word still appears.
        /// </summary>
        static int ThemeRank(MatrixCluster cluster, string label)
        {
            int index = (int)cluster;
            if (index < 0 || index >= ClusterThemeLabels.Length)
                return int.MaxValue;

            string[] theme = ClusterThemeLabels[index];
            for (int i = 0; i < theme.Length; i++)
            {
                if (string.Equals(theme[i], label, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return theme.Length;
        }

        /// <summary>How many tiles belong under this themed row.</summary>
        static int CountCluster(IReadOnlyList<ShipCommsKeyword> words, MatrixCluster cluster)
        {
            int count = 0;
            for (int i = 0; i < words.Count; i++)
            {
                if (TryGetCluster(words[i], out MatrixCluster found) && found == cluster)
                    count++;
            }

            return count;
        }

        /// <summary>Wrapping tile rows for one themed cluster (no section or caption).</summary>
        static float TileBlockHeight(int count)
        {
            int rows = Mathf.Max(1, Mathf.CeilToInt(count / (float)KeywordColumns));
            return rows * TileHeight + Mathf.Max(0, rows - 1) * TileGap;
        }

        /// <summary>Child rect pinned to the parent's top-left, sized in canvas units.</summary>
        static RectTransform CreateTopLeft(Transform parent, string name, float x, float yFromTop, float width, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -yFromTop);
            rt.sizeDelta = new Vector2(width, height);
            return rt;
        }

        /// <summary>Non-layout Image used for frame / rails.</summary>
        static Image CreateIgnoredImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>L-bracket targeting mark in one corner (HUD / cockpit motif).</summary>
        static void AddCornerBracket(Transform parent, string name, Vector2 anchor, Vector2 inset, bool openRight, bool openDown)
        {
            const float arm = 10f;
            const float thick = 1.2f;
            var holder = new GameObject("Bracket" + name, typeof(RectTransform));
            holder.transform.SetParent(parent, false);
            var rt = holder.GetComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = inset;
            rt.sizeDelta = new Vector2(arm, arm);

            var h = CreateIgnoredImage(holder.transform, "H", BracketColor);
            var hRt = h.rectTransform;
            hRt.pivot = new Vector2(openRight ? 0f : 1f, openDown ? 1f : 0f);
            hRt.anchorMin = hRt.pivot;
            hRt.anchorMax = hRt.pivot;
            hRt.anchoredPosition = Vector2.zero;
            hRt.sizeDelta = new Vector2(arm, thick);

            var v = CreateIgnoredImage(holder.transform, "V", BracketColor);
            var vRt = v.rectTransform;
            vRt.pivot = new Vector2(openRight ? 0f : 1f, openDown ? 1f : 0f);
            vRt.anchorMin = vRt.pivot;
            vRt.anchorMax = vRt.pivot;
            vRt.anchoredPosition = Vector2.zero;
            vRt.sizeDelta = new Vector2(thick, arm);
        }

        /// <summary>Image + Button tile placed from the parent's top-left.</summary>
        static Image CreateTile(Transform parent, string name, float x, float yFromTop, float width, float height, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -yFromTop);
            rt.sizeDelta = new Vector2(width, height);
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = true;
            var btn = go.GetComponent<Button>();
            btn.transition = Selectable.Transition.None;
            return img;
        }

        /// <summary>TMP label using the shared Rajdhani HUD font when present.</summary>
        static TextMeshProUGUI CreateLabel(
            Transform parent,
            string name,
            string text,
            float size,
            Color color,
            TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.outlineWidth = 0.16f;
            tmp.outlineColor = LabelOutline;
            TMP_FontAsset font = Resources.Load<TMP_FontAsset>("Rajdhani-SemiBold SDF");
            if (font != null)
                tmp.font = font;
            return tmp;
        }

        /// <summary>
        /// Orbit Unlocked / remove-ads skips both comms videos for this match:
        /// 4th + 5th keyword chips and every RECENT row past the free first three.
        /// </summary>
        void GrantOwnedAdUnlocks()
        {
            if (!TitanOrbitEntitlements.IsRemoveAdsOwned)
                return;

            ShipCommsClientState.SetExtraKeywordSlots(2);
            ShipCommsClientState.SetRecentRowsUnlocked(true);
        }

        /// <summary>
        /// One rewarded ad unlocks both extra compose slots (4th and 5th) for this match.
        /// Orbit Unlocked / remove-ads grants both immediately.
        /// </summary>
        void TryUnlockNextSlot()
        {
            if (ShipCommsClientState.ExtraKeywordSlots >= 2)
                return;

            if (TitanOrbitEntitlements.IsRemoveAdsOwned)
            {
                ShipCommsClientState.SetExtraKeywordSlots(2);
                PaintSequence();
                PaintAudience();
                return;
            }

            if (!TitanOrbitRewardedAds.CanOfferRewarded || TitanOrbitRewardedAds.IsShowing)
                return;

            TitanOrbitRewardedAds.Show(TitanOrbitRewardedAds.PlacementCommsSlot, result =>
            {
                if (result != TitanOrbitRewardedAdResult.Completed)
                    return;

                ShipCommsClientState.SetExtraKeywordSlots(2);
                PaintSequence();
                PaintAudience();
            });
        }

        /// <summary>
        /// One rewarded ad unlocks every RECENT row past the free first three.
        /// After the video, a filled row that started the click is loaded onto the rail.
        /// </summary>
        /// <param name="pendingIndex">Row the player tapped, or -1 if none should auto-load.</param>
        void TryUnlockRecentRows(int pendingIndex)
        {
            if (ShipCommsClientState.RecentRowsUnlocked)
            {
                if (pendingIndex >= 0)
                    ApplyRecentSentence(pendingIndex);
                return;
            }

            if (TitanOrbitEntitlements.IsRemoveAdsOwned)
            {
                ShipCommsClientState.SetRecentRowsUnlocked(true);
                PaintRecent();
                if (pendingIndex >= 0)
                    ApplyRecentSentence(pendingIndex);
                return;
            }

            if (!TitanOrbitRewardedAds.CanOfferRewarded || TitanOrbitRewardedAds.IsShowing)
                return;

            TitanOrbitRewardedAds.Show(TitanOrbitRewardedAds.PlacementCommsRecent, result =>
            {
                if (result != TitanOrbitRewardedAdResult.Completed)
                    return;

                ShipCommsClientState.SetRecentRowsUnlocked(true);
                PaintRecent();
                if (pendingIndex >= 0)
                    ApplyRecentSentence(pendingIndex);
            });
        }

        /// <summary>
        /// Space-glass card to the left of the compose panel, same height, same chrome.
        /// The live minimap reparents into the inner host while S is held so the player
        /// can ping a world point.
        /// </summary>
        void BuildMinimapDock()
        {
            var go = new GameObject("CommsMinimapDock", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(transform, false);
            _minimapDock = go.GetComponent<RectTransform>();
            _minimapDock.anchorMin = new Vector2(0.5f, 0.5f);
            _minimapDock.anchorMax = new Vector2(0.5f, 0.5f);
            _minimapDock.pivot = new Vector2(0.5f, 0.5f);
            LayoutMinimapDock(_dockSize);
            var bg = go.GetComponent<Image>();
            bg.color = FillColor;
            bg.raycastTarget = false;
            BuildSciFiChrome(_minimapDock);

            var hostGo = new GameObject("MapHost", typeof(RectTransform));
            hostGo.transform.SetParent(_minimapDock, false);
            _minimapHost = hostGo.GetComponent<RectTransform>();
            _minimapHost.anchorMin = new Vector2(0.5f, 0.5f);
            _minimapHost.anchorMax = new Vector2(0.5f, 0.5f);
            _minimapHost.pivot = new Vector2(0.5f, 0.5f);
            _minimapHost.anchoredPosition = Vector2.zero;
            LayoutMinimapDock(_dockSize);

            go.SetActive(false);
        }

        /// <summary>Square card whose side matches the compose panel height.</summary>
        void LayoutMinimapDock(float panelHeight)
        {
            float side = Mathf.Max(80f, panelHeight);
            _dockSize = side;
            // Shift the pair so minimap + matrix sit as one centered group.
            float groupShift = (side + RootGap) * 0.5f;
            if (_panel != null)
                _panel.anchoredPosition = new Vector2(groupShift, 0f);

            if (_minimapDock != null)
            {
                _minimapDock.sizeDelta = new Vector2(side, side);
                _minimapDock.anchoredPosition = new Vector2(
                    groupShift - _overlayW * 0.5f - RootGap - side * 0.5f,
                    0f);
            }

            if (_minimapHost != null)
            {
                float mapSide = Mathf.Max(72f, side - MinimapCircleInset * 2f);
                _minimapHost.sizeDelta = new Vector2(mapSide, mapSide);
            }
        }

        /// <summary>Reparents the HUD minimap into the dock as a full-height map view.</summary>
        void DockMinimap()
        {
            if (_minimapDocked || _minimapHost == null)
                return;

            var minimap = FindMinimapController();
            if (minimap == null)
                return;

            float panelH = _panel != null ? _panel.sizeDelta.y : _dockSize;
            LayoutMinimapDock(panelH);
            _minimapDock.gameObject.SetActive(true);
            float mapSide = _minimapHost.sizeDelta.x;
            minimap.AttachToCommsDock(_minimapHost, mapSide);
            _minimapDocked = true;
        }

        /// <summary>Returns the HUD minimap to its corner circle.</summary>
        void UndockMinimap()
        {
            var minimap = FindMinimapController();
            if (minimap != null && minimap.IsCommsDocked)
                minimap.DetachFromCommsDock();

            _minimapDocked = false;
            if (_minimapDock != null)
                _minimapDock.gameObject.SetActive(false);
        }

        /// <summary>
        /// Live HUD map, including when it is still a child of a disabled comms dock
        /// (that path clears <see cref="MinimapController.Instance"/> in OnDisable).
        /// </summary>
        static MinimapController FindMinimapController()
        {
            if (MinimapController.Instance != null)
                return MinimapController.Instance;

            return FindFirstObjectByType<MinimapController>(FindObjectsInactive.Include);
        }

        /// <summary>
        /// Builds one clickable plate over a locked group. Compact mode is a single
        /// chip (compose slots 4+5). Tall mode veils the paid RECENT block and
        /// centers a CTA so the extra rows stay visible underneath.
        /// </summary>
        UnlockGate CreateUnlockGate(
            Transform parent,
            string name,
            float x,
            float yFromTop,
            float width,
            float height,
            bool compact,
            string title,
            string sub,
            UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -yFromTop);
            rt.sizeDelta = new Vector2(width, height);
            var fill = go.GetComponent<Image>();
            fill.color = compact ? AdGatePlate : AdGateVeil;
            fill.raycastTarget = true;
            var btn = go.GetComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(onClick);

            var accent = CreateIgnoredImage(go.transform, "Accent", TeamChannelColor);
            var accentRt = accent.rectTransform;
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot = new Vector2(0.5f, 1f);
            accentRt.sizeDelta = new Vector2(-8f, 2f);
            accentRt.anchoredPosition = Vector2.zero;

            Transform labelParent = go.transform;
            if (!compact)
            {
                float plateW = Mathf.Min(width - 12f, 220f);
                float plateH = Mathf.Min(40f, height - 8f);
                var plate = CreateIgnoredImage(go.transform, "Cta", AdGatePlate);
                var plateRt = plate.rectTransform;
                plateRt.anchorMin = new Vector2(0.5f, 0.5f);
                plateRt.anchorMax = new Vector2(0.5f, 0.5f);
                plateRt.pivot = new Vector2(0.5f, 0.5f);
                plateRt.anchoredPosition = Vector2.zero;
                plateRt.sizeDelta = new Vector2(plateW, plateH);
                var plateOutline = plate.gameObject.AddComponent<Outline>();
                plateOutline.effectColor = TeamChannelColor;
                plateOutline.effectDistance = new Vector2(1.1f, -1.1f);
                plateOutline.useGraphicAlpha = false;
                labelParent = plate.transform;
            }

            bool twoLine = !compact && !string.IsNullOrEmpty(sub) && height >= 48f;
            var titleLabel = CreateLabel(
                labelParent,
                "Title",
                title,
                compact ? 8f : 10f,
                TeamChannelColor,
                TextAlignmentOptions.Center);
            titleLabel.fontStyle = FontStyles.Bold;
            titleLabel.characterSpacing = 1.1f;
            if (twoLine)
            {
                var titleRt = titleLabel.rectTransform;
                titleRt.anchorMin = new Vector2(0f, 0.42f);
                titleRt.anchorMax = new Vector2(1f, 1f);
                titleRt.offsetMin = new Vector2(6f, 0f);
                titleRt.offsetMax = new Vector2(-6f, -2f);
                var subLabel = CreateLabel(
                    labelParent, "Sub", sub, 8f, CaptionTextColor, TextAlignmentOptions.Center);
                subLabel.characterSpacing = 0.8f;
                var subRt = subLabel.rectTransform;
                subRt.anchorMin = new Vector2(0f, 0f);
                subRt.anchorMax = new Vector2(1f, 0.48f);
                subRt.offsetMin = new Vector2(6f, 2f);
                subRt.offsetMax = new Vector2(-6f, 0f);
            }
            else
            {
                Stretch(titleLabel.rectTransform, 4f);
            }

            go.transform.SetAsLastSibling();
            return new UnlockGate
            {
                Root = go,
                Button = btn,
            };
        }

        /// <summary>Shows or hides a group unlock plate and blocks clicks while a video is up.</summary>
        static void PaintUnlockGate(UnlockGate gate, bool visible)
        {
            if (gate?.Root == null)
                return;

            gate.Root.SetActive(visible);
            if (gate.Button != null)
                gate.Button.interactable = visible && !TitanOrbitRewardedAds.IsShowing;
        }

        /// <summary>Stretches a rect to its parent with a uniform inset.</summary>
        static void Stretch(RectTransform rt, float inset)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
        }
    }
}
