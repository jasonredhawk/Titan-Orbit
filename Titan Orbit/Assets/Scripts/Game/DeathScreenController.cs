using TitanOrbit.ECS;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-only death telemetry plaque while the local ship is destroyed and waiting to respawn.
    /// Reads <see cref="EcsGameBridge"/> local ship death state each frame and shows a countdown
    /// from the server-authoritative respawn timer. Hidden when not in-game or when the ship is alive.
    /// <para>
    /// [TITAN-ORBIT] This is presentation only — the server still owns death and respawn
    /// (<see cref="ShipDeathRecordingSystem"/> / <see cref="ShipRespawnSystem"/>). We never write
    /// ship state from this overlay. The plaque sits at the bottom of the screen so the hull
    /// explosion stays visible in the camera centre (the old centred block of text covered it).
    /// Visual language matches the dark space HUD: void glass, thin warning rail, corner brackets,
    /// uppercase telemetry copy. Paired with <c>ShipStatTooltipChrome</c> palette, built locally
    /// because this type lives in the TitanOrbit.Game assembly.
    /// </para>
    /// </summary>
    public class DeathScreenController : MonoBehaviour
    {
        /// <summary>
        /// Plaque root we show/hide. Built at runtime if the Inspector fields are empty
        /// (SampleScene ships both as null).
        /// </summary>
        [SerializeField] GameObject overlayRoot;

        /// <summary>Left-column status line the player reads as "what happened" (SHIP DESTROYED).</summary>
        [SerializeField] TextMeshProUGUI messageText;

        /// <summary>Large countdown digits on the right (seconds remaining until server respawn).</summary>
        TextMeshProUGUI _timerText;

        /// <summary>Small uppercase caption above the digits (REASSEMBLY / REBOOT).</summary>
        TextMeshProUGUI _timerCaption;

        /// <summary>
        /// Thin reassembly fill. Width grows from 0 at death toward full at respawn so the
        /// player sees progress, not only a number.
        /// </summary>
        RectTransform _progressFill;

        /// <summary>Full width of the progress track in canvas units. Used to scale the fill.</summary>
        float _progressTrackWidth;

        /// <summary>Last integer second we painted. Skips TMP writes when the count has not ticked.</summary>
        int _lastShownSeconds = int.MinValue;

        /// <summary>True while the overlay is up this death. Used to detect the alive→dead edge.</summary>
        bool _wasDead;

        /// <summary>
        /// <c>Time.time</c> when this client first saw the local ship as dead.
        /// Fallback only — used if the ghost has not received <see cref="ShipDeathState"/> yet.
        /// </summary>
        float _clientDeathStartTime = -1f;

        // --- Palette: same void glass as ShipStatTooltipChrome; amber rail = hull-critical warning ---
        static readonly Color FillColor = new Color(0.012f, 0.016f, 0.028f, 0.94f);
        static readonly Color CaptionPlateColor = new Color(0.018f, 0.028f, 0.045f, 1f);
        static readonly Color CaptionTextColor = new Color(0.62f, 0.78f, 0.95f, 0.92f);
        static readonly Color BodyTextColor = new Color(0.88f, 0.92f, 0.98f, 1f);
        static readonly Color WarningAccent = new Color(0.95f, 0.48f, 0.28f, 0.95f);
        static readonly Color WarningDim = new Color(0.95f, 0.55f, 0.32f, 0.55f);
        static readonly Color TrackColor = new Color(0.10f, 0.14f, 0.20f, 0.90f);
        static readonly Color BracketColor = new Color(0.55f, 0.38f, 0.28f, 0.70f);
        static readonly Color FrameTint = new Color(0.32f, 0.22f, 0.18f, 0.45f);

        /// <summary>
        /// [UNITY] Awake runs when the GameObject wakes (scene load or AddComponent).
        /// We build the plaque once and keep it hidden until the local ship dies.
        /// </summary>
        void Awake()
        {
            EnsureUi();
            Hide();
        }

        /// <summary>
        /// Per-frame presentation tick. Reads the local ghost's <c>ShipState.IsDead</c> and,
        /// when dead, paints remaining seconds from <see cref="ShipDeathState.RespawnAtTime"/>.
        /// Does not run ship sim and does not send RPCs.
        /// </summary>
        void Update()
        {
            // --- Guard: only show overlay when in-game with a local ship ghost ---
            // [HYBRID] EcsGameBridge is the GameObject-side window into the client ECS world.
            if (!EcsGameBridge.IsNetworkInGame() || !EcsGameBridge.HasLocalPlayerShip())
            {
                if (_wasDead)
                    Hide();
                _wasDead = false;
                return;
            }

            if (!EcsGameBridge.TryGetLocalShipState(out var ship))
            {
                if (_wasDead)
                    Hide();
                _wasDead = false;
                return;
            }

            // --- Alive again: dismiss overlay ---
            // [NETCODE] IsDead is a ghost field. When the server respawns, this flips false
            // and we hide so the player is not staring at a stale countdown.
            if (!ship.IsDead)
            {
                if (_wasDead)
                    Hide();
                _wasDead = false;
                _clientDeathStartTime = -1f;
                return;
            }

            // First dead frame this life — latch a client clock for the fallback countdown.
            if (!_wasDead)
                _clientDeathStartTime = Time.time;

            _wasDead = true;
            Show();

            // --- Countdown from server RespawnAtTime (authoritative) ---
            // Ghost — NetCode's replica of the ship on this client (not a visual "ghost sprite").
            // ElapsedTime is the client world's sim clock; RespawnAtTime was written on the server
            // as now + ShipRespawnSystem.RespawnDelaySeconds (10s).
            float remaining = ShipRespawnSystem.RespawnDelaySeconds;
            if (EcsGameBridge.TryGetLocalShipDeathState(out var death))
            {
                var world = EcsGameBridge.GetVisualizationWorld();
                if (world != null && world.IsCreated)
                {
                    double elapsed = world.Time.ElapsedTime;
                    remaining = Mathf.Max(0f, death.RespawnAtTime - (float)elapsed);
                }
            }
            else if (_clientDeathStartTime >= 0f)
            {
                // [TITAN-ORBIT] DeathState can arrive a tick later than IsDead. Count locally
                // so the plaque never sits at a frozen 10 while we wait for the ghost field.
                remaining = Mathf.Max(0f, ShipRespawnSystem.RespawnDelaySeconds - (Time.time - _clientDeathStartTime));
            }

            PaintCountdown(remaining);
            PulseTimer(remaining);
        }

        /// <summary>
        /// Writes status, digits, and reassembly fill for the current remaining time.
        /// Skips TMP string assigns when the displayed second has not changed.
        /// </summary>
        /// <param name="remaining">Seconds until server respawn. 0 means the reboot is imminent.</param>
        void PaintCountdown(float remaining)
        {
            int seconds = remaining > 0.05f ? Mathf.CeilToInt(remaining) : 0;
            bool assembling = remaining <= 0.05f;

            // --- Progress fill (grows toward respawn) ---
            // [TITAN-ORBIT] Fill-up reads as "reassembly", not a depleting health bar.
            if (_progressFill != null && _progressTrackWidth > 1f)
            {
                float delay = Mathf.Max(0.01f, ShipRespawnSystem.RespawnDelaySeconds);
                float t = assembling ? 1f : 1f - Mathf.Clamp01(remaining / delay);
                _progressFill.sizeDelta = new Vector2(_progressTrackWidth * t, _progressFill.sizeDelta.y);
            }

            if (seconds == _lastShownSeconds && _timerText != null && _timerText.text.Length > 0)
                return;
            _lastShownSeconds = seconds;

            if (messageText != null)
                messageText.text = assembling ? "REASSEMBLING" : "SHIP DESTROYED";

            if (_timerCaption != null)
                _timerCaption.text = assembling ? "REBOOT" : "REASSEMBLY";

            if (_timerText != null)
                _timerText.text = assembling ? "--" : seconds.ToString("00");
        }

        /// <summary>
        /// Soft heartbeat on the countdown digits so the plaque reads as live telemetry,
        /// not a frozen screenshot. Faster pulse in the last three seconds.
        /// </summary>
        /// <param name="remaining">Seconds until respawn. Unused when the timer label is missing.</param>
        void PulseTimer(float remaining)
        {
            if (_timerText == null)
                return;

            float hz = remaining <= 3.05f ? 5.2f : 2.4f;
            float wave = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * hz);
            Color c = WarningAccent;
            c.a = 0.72f + 0.28f * wave;
            _timerText.color = c;
        }

        /// <summary>Makes the plaque visible. Builds UI on first show if Awake was skipped.</summary>
        void Show()
        {
            EnsureUi();
            if (overlayRoot != null)
                overlayRoot.SetActive(true);
        }

        /// <summary>Hides the plaque and clears the last painted second so the next death refreshes text.</summary>
        void Hide()
        {
            if (overlayRoot != null)
                overlayRoot.SetActive(false);
            _lastShownSeconds = int.MinValue;
        }

        /// <summary>
        /// Builds the bottom-centre telemetry plaque once. Recreates if we find the old
        /// centred "Ship destroyed" label from an earlier version (hot-reload / lingering children).
        /// </summary>
        void EnsureUi()
        {
            if (overlayRoot != null && messageText != null && _timerText != null)
                return;

            // --- Tear down a stale overlay (old centred text, or a hot-reload leftover) ---
            Transform existingCanvas = transform.Find("DeathOverlay");
            if (existingCanvas != null)
                Destroy(existingCanvas.gameObject);
            else if (overlayRoot != null)
                Destroy(overlayRoot);

            overlayRoot = null;
            messageText = null;
            _timerText = null;
            _timerCaption = null;
            _progressFill = null;

            // [UNITY] Screen Space Overlay paints on top of the 3D view. No GraphicRaycaster —
            // this plaque must not steal clicks from the game or other HUD.
            var canvasGo = new GameObject("DeathOverlay");
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 8500;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            // --- Plaque: compact HUD card, bottom-centre, well below the explosion ---
            const float plaqueW = 420f;
            const float plaqueH = 78f;
            var plaqueGo = new GameObject("DeathPlaque");
            plaqueGo.transform.SetParent(canvasGo.transform, false);
            var plaqueRt = plaqueGo.AddComponent<RectTransform>();
            plaqueRt.anchorMin = new Vector2(0.5f, 0f);
            plaqueRt.anchorMax = new Vector2(0.5f, 0f);
            plaqueRt.pivot = new Vector2(0.5f, 0f);
            plaqueRt.anchoredPosition = new Vector2(0f, 36f);
            plaqueRt.sizeDelta = new Vector2(plaqueW, plaqueH);
            overlayRoot = plaqueGo;

            // Dark void glass — same fill family as tooltip chrome. No bright Shift "Filled" plate.
            Image fill = CreateChildImage(plaqueGo.transform, "Fill", stretch: true);
            fill.color = FillColor;
            fill.raycastTarget = false;

            var outline = plaqueGo.AddComponent<Outline>();
            outline.effectColor = FrameTint;
            outline.effectDistance = new Vector2(1.2f, -1.2f);

            // Thin amber rail along the top edge — the only saturated colour on the card.
            Image accent = CreateChildImage(plaqueGo.transform, "Accent", stretch: false);
            RectTransform accentRt = accent.rectTransform;
            accentRt.anchorMin = new Vector2(0f, 1f);
            accentRt.anchorMax = new Vector2(1f, 1f);
            accentRt.pivot = new Vector2(0.5f, 1f);
            accentRt.anchoredPosition = Vector2.zero;
            accentRt.sizeDelta = new Vector2(-16f, 2.5f);
            accent.color = WarningAccent;
            accent.raycastTarget = false;

            // Left warning stub — cockpit "caution" motif next to the caption.
            Image stub = CreateChildImage(plaqueGo.transform, "CautionStub", stretch: false);
            RectTransform stubRt = stub.rectTransform;
            stubRt.anchorMin = new Vector2(0f, 1f);
            stubRt.anchorMax = new Vector2(0f, 1f);
            stubRt.pivot = new Vector2(0f, 1f);
            stubRt.anchoredPosition = new Vector2(10f, -10f);
            stubRt.sizeDelta = new Vector2(8f, 8f);
            stub.color = WarningAccent;
            stub.raycastTarget = false;

            // --- Caption plate (HULL CRITICAL) ---
            GameObject captionGo = new GameObject("CaptionBar");
            captionGo.transform.SetParent(plaqueGo.transform, false);
            RectTransform captionRt = captionGo.AddComponent<RectTransform>();
            captionRt.anchorMin = new Vector2(0f, 1f);
            captionRt.anchorMax = new Vector2(1f, 1f);
            captionRt.pivot = new Vector2(0.5f, 1f);
            captionRt.anchoredPosition = new Vector2(0f, -8f);
            captionRt.sizeDelta = new Vector2(-20f, 18f);

            Image captionBg = captionGo.AddComponent<Image>();
            captionBg.color = CaptionPlateColor;
            captionBg.raycastTarget = false;

            TextMeshProUGUI captionLabel = CreateLabel(
                captionGo.transform, "Caption", "HULL CRITICAL", 11f, CaptionTextColor, TextAlignmentOptions.MidlineLeft);
            captionLabel.fontStyle = FontStyles.Bold;
            captionLabel.characterSpacing = 3f;
            Stretch(captionLabel.rectTransform, 22f, 2f, 88f, 2f);

            // REASSEMBLY sits on the same caption rail as HULL CRITICAL (right-aligned).
            _timerCaption = CreateLabel(
                captionGo.transform, "TimerCaption", "REASSEMBLY", 10f, CaptionTextColor, TextAlignmentOptions.MidlineRight);
            _timerCaption.characterSpacing = 2.2f;
            Stretch(_timerCaption.rectTransform, 160f, 2f, 6f, 2f);

            // --- Status (left) + countdown digits (right) ---
            messageText = CreateLabel(
                plaqueGo.transform, "Status", "SHIP DESTROYED", 18f, BodyTextColor, TextAlignmentOptions.MidlineLeft);
            messageText.fontStyle = FontStyles.Bold;
            messageText.characterSpacing = 1.2f;
            RectTransform statusRt = messageText.rectTransform;
            statusRt.anchorMin = new Vector2(0f, 0f);
            statusRt.anchorMax = new Vector2(0.68f, 1f);
            statusRt.offsetMin = new Vector2(14f, 20f);
            statusRt.offsetMax = new Vector2(-4f, -30f);

            _timerText = CreateLabel(
                plaqueGo.transform, "Timer", "10", 30f, WarningAccent, TextAlignmentOptions.MidlineRight);
            _timerText.fontStyle = FontStyles.Bold;
            RectTransform timerRt = _timerText.rectTransform;
            timerRt.anchorMin = new Vector2(0.62f, 0f);
            timerRt.anchorMax = new Vector2(1f, 1f);
            timerRt.offsetMin = new Vector2(0f, 16f);
            timerRt.offsetMax = new Vector2(-16f, -28f);

            // --- Reassembly track (bottom of the plaque) ---
            const float trackInset = 14f;
            const float trackH = 4f;
            Image track = CreateChildImage(plaqueGo.transform, "ProgressTrack", stretch: false);
            RectTransform trackRt = track.rectTransform;
            trackRt.anchorMin = new Vector2(0f, 0f);
            trackRt.anchorMax = new Vector2(1f, 0f);
            trackRt.pivot = new Vector2(0f, 0f);
            trackRt.anchoredPosition = new Vector2(trackInset, 10f);
            trackRt.sizeDelta = new Vector2(-(trackInset * 2f), trackH);
            track.color = TrackColor;
            track.raycastTarget = false;

            _progressTrackWidth = plaqueW - trackInset * 2f;
            Image progress = CreateChildImage(track.transform, "ProgressFill", stretch: false);
            _progressFill = progress.rectTransform;
            _progressFill.anchorMin = new Vector2(0f, 0f);
            _progressFill.anchorMax = new Vector2(0f, 1f);
            _progressFill.pivot = new Vector2(0f, 0.5f);
            _progressFill.anchoredPosition = Vector2.zero;
            _progressFill.sizeDelta = new Vector2(0f, 0f);
            progress.color = WarningAccent;
            progress.raycastTarget = false;

            // Corner brackets — targeting-HUD L marks, same motif as stat tooltips.
            const float bracket = 10f;
            const float thick = 1.4f;
            const float inset = 4f;
            CreateCornerBracket(plaqueGo.transform, "BracketTL", new Vector2(0f, 1f),
                new Vector2(inset, -inset), bracket, thick, openRight: true, openDown: true);
            CreateCornerBracket(plaqueGo.transform, "BracketTR", new Vector2(1f, 1f),
                new Vector2(-inset, -inset), bracket, thick, openRight: false, openDown: true);
            CreateCornerBracket(plaqueGo.transform, "BracketBL", new Vector2(0f, 0f),
                new Vector2(inset, inset), bracket, thick, openRight: true, openDown: false);
            CreateCornerBracket(plaqueGo.transform, "BracketBR", new Vector2(1f, 0f),
                new Vector2(-inset, inset), bracket, thick, openRight: false, openDown: false);

            // Hairline under the caption so status text does not collide with HULL CRITICAL.
            Image sep = CreateChildImage(plaqueGo.transform, "CaptionSeparator", stretch: false);
            RectTransform sepRt = sep.rectTransform;
            sepRt.anchorMin = new Vector2(0f, 1f);
            sepRt.anchorMax = new Vector2(1f, 1f);
            sepRt.pivot = new Vector2(0.5f, 1f);
            sepRt.anchoredPosition = new Vector2(0f, -26f);
            sepRt.sizeDelta = new Vector2(-28f, 1f);
            sep.color = WarningDim;
            sep.raycastTarget = false;
        }

        /// <summary>Creates a full-stretch or empty-rect child <see cref="Image"/>.</summary>
        static Image CreateChildImage(Transform parent, string name, bool stretch)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            if (stretch)
            {
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
            }

            return go.AddComponent<Image>();
        }

        /// <summary>
        /// Builds a TMP label with the HUD Rajdhani face when available.
        /// Shift fonts live outside Resources, so player builds fall back to TMP default.
        /// </summary>
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
            ApplyHudFont(tmp);
            return tmp;
        }

        /// <summary>Prefers Shift Rajdhani so this plaque matches other HUD chrome.</summary>
        static void ApplyHudFont(TextMeshProUGUI tmp)
        {
            if (tmp == null)
                return;

            TMP_FontAsset font = Resources.Load<TMP_FontAsset>("Rajdhani-SemiBold SDF");
            if (font != null)
                tmp.font = font;
            else if (TMP_Settings.defaultFontAsset != null)
                tmp.font = TMP_Settings.defaultFontAsset;
        }

        /// <summary>Insets a stretched rect (left, bottom, right, top) in canvas units.</summary>
        static void Stretch(RectTransform rt, float left, float bottom, float right, float top)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>
        /// Two thin bars forming an L at a corner — classic targeting-HUD motif.
        /// <paramref name="openRight"/> / <paramref name="openDown"/> choose which way the arms grow.
        /// </summary>
        static void CreateCornerBracket(
            Transform parent,
            string name,
            Vector2 corner,
            Vector2 anchoredPos,
            float armLength,
            float thickness,
            bool openRight,
            bool openDown)
        {
            var holder = new GameObject(name);
            holder.transform.SetParent(parent, false);
            var holderRt = holder.AddComponent<RectTransform>();
            holderRt.anchorMin = corner;
            holderRt.anchorMax = corner;
            holderRt.pivot = corner;
            holderRt.anchoredPosition = anchoredPos;
            holderRt.sizeDelta = new Vector2(armLength, armLength);

            Image h = CreateChildImage(holder.transform, "H", stretch: false);
            RectTransform hRt = h.rectTransform;
            hRt.pivot = new Vector2(openRight ? 0f : 1f, openDown ? 1f : 0f);
            hRt.anchorMin = hRt.pivot;
            hRt.anchorMax = hRt.pivot;
            hRt.anchoredPosition = Vector2.zero;
            hRt.sizeDelta = new Vector2(armLength, thickness);
            h.color = BracketColor;
            h.raycastTarget = false;

            Image v = CreateChildImage(holder.transform, "V", stretch: false);
            RectTransform vRt = v.rectTransform;
            vRt.pivot = new Vector2(openRight ? 0f : 1f, openDown ? 1f : 0f);
            vRt.anchorMin = vRt.pivot;
            vRt.anchorMax = vRt.pivot;
            vRt.anchoredPosition = Vector2.zero;
            vRt.sizeDelta = new Vector2(thickness, armLength);
            v.color = BracketColor;
            v.raycastTarget = false;
        }
    }
}
