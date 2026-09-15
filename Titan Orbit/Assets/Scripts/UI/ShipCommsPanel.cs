using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using TitanOrbit.Input;
using TitanOrbit.NetCode;
using TitanOrbit.Services;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Hold-S comms matrix: a centered dark-glass HUD card with keyword tiles. The player
    /// holds S, clicks 1–5 words in order (3 free; 4th/5th after ads), then releases S to send that sentence above
    /// their ship. An All / Team toggle (remembered in PlayerPrefs) picks who sees it.
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

        const float RecentColWidth = 156f;
        const float RootGap = 10f;
        const float HeaderHeight = 26f;
        const float AudienceToggleWidth = 52f;
        const float AudienceToggleGap = 4f;
        const float BannerHeight = 14f;
        const float SectionGap = 6f;
        const float PanelPad = 12f;
        const float RecentRowHeight = 22f;

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

        /// <summary>Keyword bytes chosen this hold, in click order (max 5).</summary>
        readonly List<byte> _sequence = new List<byte>(ShipCommsKeywordCatalog.MaxSequenceLength);

        readonly List<KeywordTile> _tiles = new List<KeywordTile>(40);
        readonly PreviewSlot[] _preview = new PreviewSlot[ShipCommsKeywordCatalog.MaxSequenceLength];
        readonly RecentSlot[] _recent = new RecentSlot[ShipCommsHistory.MaxEntries];

        Canvas _canvas;
        CanvasGroup _group;
        RectTransform _panel;
        PlayerInputHandler _input;
        TextMeshProUGUI _headerSub;
        Image _allFill;
        Image _teamFill;
        Outline _allOutline;
        Outline _teamOutline;
        TextMeshProUGUI _allLabel;
        TextMeshProUGUI _teamLabel;
        bool _wasHeld;
        bool _built;
        RectTransform _minimapDock;
        float _overlayW;
        float _dockSize;
        bool _minimapDocked;

        /// <summary>Highlighted RECENT row while S is held. -1 = none.</summary>
        int _recentCursor = -1;

        /// <summary>One keyword button in the matrix.</summary>
        struct KeywordTile
        {
            public byte Index;
            public Image Fill;
            public Outline Outline;
            public TextMeshProUGUI Label;
            public TextMeshProUGUI OrderBadge;
            public Image Caret;
            /// <summary>Cyan for ordinary words; faction RGB for Red / Blue / Green / Orange / Purple.</summary>
            public Color Accent;
            /// <summary>True when this tile is one of the five team color keywords.</summary>
            public bool IsTeamColor;
        }

        /// <summary>One of the five sequence chips at the top of the card.</summary>
        struct PreviewSlot
        {
            public Image Fill;
            public TextMeshProUGUI Label;
            public TextMeshProUGUI LockLabel;
            public Outline Outline;
        }

        /// <summary>Compose-panel banner. TEAM is color names; others follow catalog category.</summary>
        enum MatrixSection : byte
        {
            Team = 0,
            Tactical = 1,
            Subject = 2,
            Social = 3,
        }

        /// <summary>One of the last-sent sentence chips in the RECENT column.</summary>
        struct RecentSlot
        {
            public Image Fill;
            public TextMeshProUGUI Label;
            public Outline Outline;
            public Button Button;
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
            // [TITAN-ORBIT] MPPM clones share one PlayerPrefs store — suffix the key so
            // Player 2's All/Team choice does not overwrite Player 1.
            ShipCommsClientState.BindPrefsKey(
                TitanOrbitPlayModeUtility.GetInstancePlayerPrefsKey(ShipCommsClientState.TeamOnlyPrefsKey));
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
            else if (!held)
            {
                SetOpen(false, clearSequence: false);
                // Recover a map left under the dock after a prior close that disabled it first.
                if (_minimapDock != null && _minimapDock.childCount > 0)
                    UndockMinimap();
            }

            _wasHeld = held && canUse;
            TrySamplePlayAim();
            if (ShipCommsClientState.IsOpen)
                TryStepRecentFromWheel();
            if (ShipCommsClientState.IsOpen && ShipCommsClientState.ConsumeWaypointChipDirty())
                EnsureMapPointChip();
        }

        /// <summary>
        /// Mouse wheel steps the RECENT column: up = newer (toward the top),
        /// down = older. First notch loads the latest sentence.
        /// </summary>
        void TryStepRecentFromWheel()
        {
            if (!TryReadScrollY(out float scrollY))
                return;

            int count = ShipCommsHistory.Recent.Count;
            if (count < 1)
                return;

            int delta = scrollY > 0f ? -1 : 1;
            int next = _recentCursor < 0 ? 0 : _recentCursor + delta;
            next = Mathf.Clamp(next, 0, count - 1);
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

            UnityEngine.Camera cam = UnityEngine.Camera.main;
            if (_input != null && _input.TryGetMouseWorldPosition(cam, out Vector3 world))
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
                if (TitanOrbitEntitlements.IsRemoveAdsOwned)
                    ShipCommsClientState.SetExtraKeywordSlots(2);
                _recentCursor = -1;
                PaintRecent();
                PaintAudience();
                PaintSequence();
                ShipCommsClientState.ClearPendingWaypoint();
                ShipCommsClientState.ClearPendingYou();
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

            // --- Channel ---
            // [TITAN-ORBIT] PlayerPrefs-backed All / Team toggle. The server re-checks
            // the speaker's team — this byte is a request, not a faction the client picks.
            byte teamOnly = ShipCommsClientState.TeamOnly ? (byte)1 : (byte)0;
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

        /// <summary>Locks the closest-in-range ship when the player clicks "You".</summary>
        void TryLockYouOnClick(byte index)
        {
            var catalog = ShipCommsKeywordCatalog.LoadDefault();
            if (!catalog.TryGetLabel(index, out string label)
                || !string.Equals(label, "You", System.StringComparison.OrdinalIgnoreCase))
                return;

            Vector3 aim = ShipCommsClientState.HasLastPlayAim
                ? ShipCommsClientState.LastPlayAim
                : Vector3.zero;
            if (ShipCommsCalloutGraphics.TryResolveYou(
                    aim, EcsGameBridge.GetLocalNetworkId(), out int youId))
                ShipCommsClientState.SetPendingYou(youId);
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
        /// </summary>
        void OnRecentClicked(int index)
        {
            if (!ShipCommsClientState.IsOpen)
                return;
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

            if (sentence.HasWaypoint != 0)
                ShipCommsClientState.SetPendingWaypoint(
                    new Vector3(sentence.WaypointX, 0f, sentence.WaypointZ));
            else
                ShipCommsClientState.ClearPendingWaypoint();

            ShipCommsClientState.ClearPendingYou();
            _recentCursor = index;
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
                    label = word;

                PreviewSlot slot = _preview[i];
                if (slot.Fill != null)
                    slot.Fill.color = locked ? CaptionPlateColor : (filled ? TileSelected : PreviewEmpty);
                if (slot.Outline != null)
                    slot.Outline.enabled = filled && !locked;
                if (slot.Label != null)
                {
                    slot.Label.text = locked ? string.Empty : (filled ? label.ToUpperInvariant() : (i + 1).ToString());
                    slot.Label.color = filled ? BodyTextColor : CaptionTextColor;
                }

                if (slot.LockLabel != null)
                {
                    slot.LockLabel.enabled = locked;
                    slot.LockLabel.text = locked ? "AD" : string.Empty;
                }
            }

            for (int t = 0; t < _tiles.Count; t++)
            {
                KeywordTile tile = _tiles[t];
                int order = IndexOfSequence(tile.Index);
                bool selected = order >= 0;
                bool isTeamColor = tile.IsTeamColor;
                if (tile.Fill != null)
                {
                    // Color words keep a faction wash so "Purple" reads as the purple team.
                    Color idle = isTeamColor ? Color.Lerp(TileIdle, tile.Accent, 0.28f) : TileIdle;
                    Color picked = isTeamColor ? Color.Lerp(TileSelected, tile.Accent, 0.4f) : TileSelected;
                    tile.Fill.color = selected ? picked : idle;
                }
                if (tile.Outline != null)
                {
                    tile.Outline.effectColor = tile.Accent;
                    tile.Outline.enabled = selected;
                }
                if (tile.Caret != null)
                {
                    tile.Caret.color = tile.Accent;
                    tile.Caret.enabled = selected;
                }
                if (tile.OrderBadge != null)
                {
                    tile.OrderBadge.text = selected ? (order + 1).ToString() : string.Empty;
                    tile.OrderBadge.color = tile.Accent;
                    tile.OrderBadge.enabled = selected;
                }
                if (tile.Label != null && isTeamColor)
                    tile.Label.color = Color.Lerp(BodyTextColor, tile.Accent, 0.55f);
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

        /// <summary>Fills the RECENT column from <see cref="ShipCommsHistory"/>.</summary>
        void PaintRecent()
        {
            var catalog = ShipCommsKeywordCatalog.LoadDefault();
            for (int i = 0; i < _recent.Length; i++)
            {
                RecentSlot slot = _recent[i];
                bool filled = ShipCommsHistory.TryGet(i, out ShipCommsHistory.Sentence sentence);
                bool selected = filled && i == _recentCursor;
                if (slot.Fill != null)
                    slot.Fill.color = selected
                        ? new Color(0.08f, 0.20f, 0.34f, 0.98f)
                        : (filled ? TileSelected : PreviewEmpty);
                if (slot.Outline != null)
                    slot.Outline.enabled = filled;
                if (slot.Button != null)
                    slot.Button.interactable = filled;
                if (slot.Label != null)
                {
                    slot.Label.text = filled
                        ? catalog.FormatSentence(
                            sentence.Count,
                            sentence.K0,
                            sentence.K1,
                            sentence.K2,
                            sentence.K3,
                            sentence.K4)
                        : "—";
                    slot.Label.color = filled ? BodyTextColor : CaptionTextColor;
                }
            }
        }

        /// <summary>
        /// Builds a fixed-size card in the middle of the screen: compact title, 1/2/3 rail,
        /// keyword grids, and a slim RECENT column on the right.
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
            int teamCount = CountSection(words, MatrixSection.Team);
            int tacticalCount = CountSection(words, MatrixSection.Tactical);
            int subjectCount = CountSection(words, MatrixSection.Subject);
            int socialCount = CountSection(words, MatrixSection.Social);

            float mainW = KeywordColumns * TileWidth + (KeywordColumns - 1) * TileGap;
            float overlayW = PanelPad + mainW + RootGap + RecentColWidth + PanelPad;
            float overlayH = PanelPad
                + HeaderHeight + SectionGap
                + TileHeight + SectionGap
                + CategoryBlockHeight(teamCount) + SectionGap
                + CategoryBlockHeight(tacticalCount) + SectionGap
                + CategoryBlockHeight(subjectCount) + SectionGap
                + CategoryBlockHeight(socialCount)
                + PanelPad;
            _overlayW = overlayW;
            _dockSize = Mathf.Clamp(overlayH, 220f, 340f);

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
            BuildCategory(main, ref y, mainW, "TEAM", words, MatrixSection.Team);
            y += SectionGap;
            BuildCategory(main, ref y, mainW, "TACTICAL", words, MatrixSection.Tactical);
            y += SectionGap;
            BuildCategory(main, ref y, mainW, "SUBJECT", words, MatrixSection.Subject);
            y += SectionGap;
            BuildCategory(main, ref y, mainW, "SOCIAL", words, MatrixSection.Social);

            BuildMinimapDock();

            RectTransform recent = CreateTopLeft(
                _panel,
                "Recent",
                PanelPad + mainW + RootGap,
                PanelPad,
                RecentColWidth,
                overlayH - PanelPad * 2f);
            BuildRecentColumn(recent);

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
        /// Two-line header: COMMS MATRIX + HOLD S · N WORDS, with an All / Team
        /// channel switch on the right. The switch is remembered in PlayerPrefs.
        /// </summary>
        void BuildHeader(Transform parent, ref float y, float width)
        {
            RectTransform plate = CreateTopLeft(parent, "Header", 0f, y, width, HeaderHeight);
            var bg = plate.gameObject.AddComponent<Image>();
            bg.color = CaptionPlateColor;
            bg.raycastTarget = false;

            // Leave room on the right for the All / Team pills (two 52px tiles + gap + inset).
            float toggleReserve = AudienceToggleWidth * 2f + AudienceToggleGap + 10f;

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
        /// Two compact pills on the header's right: ALL (everyone) and TEAM (teammates).
        /// Clicking one writes <see cref="ShipCommsClientState.SetTeamOnly"/>.
        /// </summary>
        void BuildAudienceToggle(RectTransform header, float headerWidth)
        {
            float teamX = headerWidth - AudienceToggleWidth - 6f;
            float allX = teamX - AudienceToggleGap - AudienceToggleWidth;
            float y = (HeaderHeight - TileHeight + 8f) * 0.5f;
            float h = HeaderHeight - 6f;

            _allFill = CreateTile(header, "AllChannel", allX, y, AudienceToggleWidth, h, TileIdle);
            _allLabel = CreateLabel(_allFill.transform, "Label", "ALL", 9f, BodyTextColor, TextAlignmentOptions.Center);
            Stretch(_allLabel.rectTransform, 2f);
            _allOutline = _allFill.gameObject.AddComponent<Outline>();
            _allOutline.effectColor = AccentColor;
            _allOutline.effectDistance = new Vector2(1f, -1f);
            _allOutline.useGraphicAlpha = false;
            _allFill.gameObject.GetComponent<Button>().onClick.AddListener(() => OnAudienceClicked(false));

            _teamFill = CreateTile(header, "TeamChannel", teamX, y, AudienceToggleWidth, h, TileIdle);
            _teamLabel = CreateLabel(_teamFill.transform, "Label", "TEAM", 9f, BodyTextColor, TextAlignmentOptions.Center);
            Stretch(_teamLabel.rectTransform, 2f);
            _teamOutline = _teamFill.gameObject.AddComponent<Outline>();
            _teamOutline.effectColor = TeamChannelColor;
            _teamOutline.effectDistance = new Vector2(1f, -1f);
            _teamOutline.useGraphicAlpha = false;
            _teamFill.gameObject.GetComponent<Button>().onClick.AddListener(() => OnAudienceClicked(true));
        }

        /// <summary>
        /// Header pill click: remember All vs Team and repaint the switch.
        /// Safe to tap while composing — it does not clear the 1/2/3 rail.
        /// </summary>
        /// <param name="teamOnly">True = teammates; false = every client.</param>
        void OnAudienceClicked(bool teamOnly)
        {
            if (!ShipCommsClientState.IsOpen)
                return;

            ShipCommsClientState.SetTeamOnly(teamOnly);
            PaintAudience();
        }

        /// <summary>
        /// Highlights the active All / Team pill and updates the HOLD S subtitle
        /// so the channel is readable without staring at the switch.
        /// </summary>
        void PaintAudience()
        {
            bool teamOnly = ShipCommsClientState.TeamOnly;

            if (_allFill != null)
                _allFill.color = teamOnly ? TileIdle : TileSelected;
            if (_teamFill != null)
                _teamFill.color = teamOnly ? Color.Lerp(TileSelected, TeamChannelColor, 0.35f) : TileIdle;
            if (_allOutline != null)
                _allOutline.enabled = !teamOnly;
            if (_teamOutline != null)
                _teamOutline.enabled = teamOnly;
            if (_allLabel != null)
                _allLabel.color = teamOnly ? CaptionTextColor : BodyTextColor;
            if (_teamLabel != null)
                _teamLabel.color = teamOnly ? TeamChannelColor : CaptionTextColor;
            if (_headerSub != null)
            {
                int allowed = ShipCommsClientState.AllowedSequenceLength;
                string words = allowed <= 3 ? "3 WORDS" : allowed + " WORDS";
                _headerSub.text = teamOnly
                    ? "HOLD S  ·  " + words + "  ·  TEAM"
                    : "HOLD S  ·  " + words + "  ·  ALL";
            }
        }

        /// <summary>Five clickable sequence chips. Slots 4–5 start locked behind a rewarded ad.</summary>
        void BuildPreviewRail(Transform parent, ref float y)
        {
            RectTransform rail = CreateTopLeft(
                parent, "PreviewRail", 0f, y,
                ShipCommsKeywordCatalog.MaxSequenceLength * TileWidth
                + (ShipCommsKeywordCatalog.MaxSequenceLength - 1) * TileGap,
                TileHeight);

            for (int i = 0; i < _preview.Length; i++)
            {
                int slot = i;
                float x = i * (TileWidth + TileGap);
                Image tile = CreateTile(rail, "Preview" + (i + 1), x, 0f, TileWidth, TileHeight, PreviewEmpty);
                var label = CreateLabel(tile.transform, "Label", (i + 1).ToString(), 10f, CaptionTextColor, TextAlignmentOptions.Center);
                Stretch(label.rectTransform, 4f);
                var lockLabel = CreateLabel(tile.transform, "Lock", "AD", 8f, TeamChannelColor, TextAlignmentOptions.Center);
                Stretch(lockLabel.rectTransform, 2f);
                lockLabel.enabled = i >= ShipCommsKeywordCatalog.DefaultSequenceLength;
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
                    LockLabel = lockLabel,
                    Outline = outline,
                };
            }

            y += TileHeight;
        }

        /// <summary>Slim right-rail of the last ten sentences.</summary>
        void BuildRecentColumn(RectTransform parent)
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

            float y = BannerHeight + 4f;
            for (int i = 0; i < _recent.Length; i++)
            {
                int slot = i;
                Image tile = CreateTile(parent, "Recent" + i, 0f, y, RecentColWidth, RecentRowHeight, PreviewEmpty);
                var label = CreateLabel(tile.transform, "Label", "—", 7f, CaptionTextColor, TextAlignmentOptions.Left);
                Stretch(label.rectTransform, 4f);
                var outline = tile.gameObject.AddComponent<Outline>();
                outline.effectColor = AccentColor;
                outline.effectDistance = new Vector2(0.8f, -0.8f);
                outline.useGraphicAlpha = false;
                outline.enabled = false;
                var btn = tile.gameObject.GetComponent<Button>();
                btn.onClick.AddListener(() => OnRecentClicked(slot));
                btn.interactable = false;

                _recent[i] = new RecentSlot
                {
                    Fill = tile,
                    Label = label,
                    Outline = outline,
                    Button = btn,
                };
                y += RecentRowHeight + 4f;
            }
        }

        /// <summary>One banner plus a wrapping row of tiles for that section.</summary>
        void BuildCategory(
            Transform parent,
            ref float y,
            float width,
            string banner,
            IReadOnlyList<ShipCommsKeyword> words,
            MatrixSection section)
        {
            var bannerLabel = CreateLabel(parent, banner + "Banner", "> " + banner, 8f, AccentColor, TextAlignmentOptions.Left);
            var bannerRt = bannerLabel.rectTransform;
            bannerRt.anchorMin = new Vector2(0f, 1f);
            bannerRt.anchorMax = new Vector2(0f, 1f);
            bannerRt.pivot = new Vector2(0f, 1f);
            bannerRt.anchoredPosition = new Vector2(0f, -y);
            bannerRt.sizeDelta = new Vector2(width, BannerHeight);
            bannerLabel.characterSpacing = 1.4f;
            y += BannerHeight;

            int placed = 0;
            for (int i = 0; i < words.Count; i++)
            {
                if (!MatchesSection(words[i], section))
                    continue;

                int col = placed % KeywordColumns;
                int row = placed / KeywordColumns;
                float x = col * (TileWidth + TileGap);
                float tileY = y + row * (TileHeight + TileGap);
                byte index = (byte)i;
                KeywordTile tile = CreateKeywordTile(parent, index, words[i].label, x, tileY);
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
            Color accent = AccentColor;
            bool isTeamColor = false;
            if (TeamIdExtensions.TryParseColorName(label, out TeamId team))
            {
                accent = team.ToColor();
                isTeamColor = true;
            }

            Color idleFill = isTeamColor ? Color.Lerp(TileIdle, accent, 0.28f) : TileIdle;
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

            Color labelColor = isTeamColor ? Color.Lerp(BodyTextColor, accent, 0.55f) : BodyTextColor;
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

            var outline = fill.gameObject.AddComponent<Outline>();
            outline.effectColor = accent;
            outline.effectDistance = new Vector2(1.1f, -1.1f);
            outline.useGraphicAlpha = false;
            outline.enabled = false;

            fill.gameObject.GetComponent<Button>().onClick.AddListener(() => OnKeywordClicked(index));

            return new KeywordTile
            {
                Index = index,
                Fill = fill,
                Outline = outline,
                Label = text,
                OrderBadge = badge,
                Caret = caret,
                Accent = accent,
                IsTeamColor = isTeamColor,
            };
        }

        /// <summary>
        /// TEAM = color names. Tactical / Subject / Social follow catalog category.
        /// "Mine" is hidden (mining is the Tactical "Mining" tile). Color names never
        /// also appear under SUBJECT.
        /// </summary>
        static bool MatchesSection(in ShipCommsKeyword word, MatrixSection section)
        {
            if (string.IsNullOrWhiteSpace(word.label))
                return false;
            if (ShipCommsKeywordCatalog.IsHiddenFromMatrix(word.label))
                return false;

            bool isColor = TeamIdExtensions.TryParseColorName(word.label, out _);
            switch (section)
            {
                case MatrixSection.Team:
                    return isColor;
                case MatrixSection.Tactical:
                    return word.category == ShipCommsKeywordCategory.Tactical;
                case MatrixSection.Subject:
                    return !isColor
                        && (word.category == ShipCommsKeywordCategory.Subject
                            || word.category == ShipCommsKeywordCategory.Objects);
                case MatrixSection.Social:
                    return word.category == ShipCommsKeywordCategory.Social;
                default:
                    return false;
            }
        }

        /// <summary>How many labeled rows belong under this banner.</summary>
        static int CountSection(IReadOnlyList<ShipCommsKeyword> words, MatrixSection section)
        {
            int count = 0;
            for (int i = 0; i < words.Count; i++)
            {
                if (MatchesSection(words[i], section))
                    count++;
            }

            return count;
        }

        /// <summary>Banner plus wrapping tile rows for one category.</summary>
        static float CategoryBlockHeight(int count)
        {
            int rows = Mathf.Max(1, Mathf.CeilToInt(count / (float)KeywordColumns));
            return BannerHeight + rows * TileHeight + Mathf.Max(0, rows - 1) * TileGap;
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
        /// One rewarded ad unlocks the next extra slot (4th, then 5th) for this match.
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

                ShipCommsClientState.SetExtraKeywordSlots(ShipCommsClientState.ExtraKeywordSlots + 1);
                PaintSequence();
                PaintAudience();
            });
        }

        /// <summary>
        /// Empty host to the left of the compose card. The live minimap reparents here
        /// while S is held so the player can ping a world point.
        /// </summary>
        void BuildMinimapDock()
        {
            var go = new GameObject("CommsMinimapDock", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(transform, false);
            _minimapDock = go.GetComponent<RectTransform>();
            _minimapDock.anchorMin = new Vector2(0.5f, 0.5f);
            _minimapDock.anchorMax = new Vector2(0.5f, 0.5f);
            _minimapDock.pivot = new Vector2(0.5f, 0.5f);
            _minimapDock.sizeDelta = new Vector2(_dockSize, _dockSize);
            _minimapDock.anchoredPosition = new Vector2(
                -_overlayW * 0.5f - RootGap - _dockSize * 0.5f,
                0f);
            var bg = go.GetComponent<Image>();
            bg.color = FillColor;
            bg.raycastTarget = false;
            go.SetActive(false);
        }

        /// <summary>Reparents the HUD minimap into the dock as a smaller full-map view.</summary>
        void DockMinimap()
        {
            if (_minimapDocked || _minimapDock == null)
                return;

            var minimap = FindMinimapController();
            if (minimap == null)
                return;

            _minimapDock.gameObject.SetActive(true);
            minimap.AttachToCommsDock(_minimapDock);
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
