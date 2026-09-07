using System;
using System.Collections.Generic;
using System.Globalization;
using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.Simulation;
using TMPro;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Identity for one live floating-count streak: same target + channel + sign reuse one popup.
    /// </summary>
    public readonly struct FloatingCountKey : IEquatable<FloatingCountKey>
    {
        public readonly int TargetId;
        public readonly FloatingCountChannel Channel;
        public readonly int Sign;

        public FloatingCountKey(int targetId, FloatingCountChannel channel, int sign)
        {
            TargetId = targetId;
            Channel = channel;
            Sign = sign >= 0 ? 1 : -1;
        }

        public bool Equals(FloatingCountKey other) =>
            TargetId == other.TargetId && Channel == other.Channel && Sign == other.Sign;

        public override bool Equals(object obj) => obj is FloatingCountKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = TargetId;
                hash = (hash * 397) ^ (int)Channel;
                hash = (hash * 397) ^ Sign;
                return hash;
            }
        }
    }

    /// <summary>
    /// [HYBRID] Client-side world floating +/- popups. Tunables and icons live on
    /// <see cref="FloatingText"/>. One live popup per target+channel+sign; hits inside
    /// a rolling streak window accumulate. Pools <see cref="FloatingCountPopup"/> GameObjects.
    /// <para>
    /// Rapid +N ticks (heal, mining, remaining HP) only mark the slot dirty. TMP mesh
    /// rebuilds flush once per frame (and at most ~20 Hz) so a burst cannot ForceMeshUpdate
    /// on every hit. LateUpdate is 67049 so the flush lands before
    /// <see cref="FloatingCountPopup"/> (67050) billsboards.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(67049)]
    public class WorldFloatingCountManager : MonoBehaviour
    {
        public static WorldFloatingCountManager Instance { get; private set; }

        const float ZoomScaleMin = 1f;
        const float ZoomScaleMax = 3.5f;
        const int TargetKindShip = unchecked((int)0x01000000);
        const int TargetKindAsteroid = unchecked((int)0x0A000000);
        const int TargetKindPlanet = unchecked((int)0x02000000);
        const int TargetKindWorld = unchecked((int)0x04000000);

        [Tooltip("Icons, colors, type toggles, layout, and streak timing. Defaults to Resources/FloatingText.")]
        [SerializeField] FloatingText floatingText;

        sealed class LiveSlot
        {
            public FloatingCountPopup Popup;
            public float Accumulated;
            public float StreakDeadline;
            public bool Expired;
            public FloatingCountChannel Channel;
            public TeamId Team;
            public Transform Anchor;
            public Vector3 ParkWorld;
            public float BodyRadius;
            public bool ClearShipHull;
            public bool WorldParked;
            public bool VisualDirty;
            public bool PendingWorldRelocate;
            public string LabeledMessage;
            public Sprite PendingIcon;
            public Color PendingColor;
            public float LastVisualFlushTime;
            public float LastPopTime;
            public int LastFormatKey;

            public void Reset()
            {
                Popup = null;
                Accumulated = 0f;
                StreakDeadline = 0f;
                Expired = false;
                Channel = FloatingCountChannel.GemPickup;
                Team = TeamId.None;
                Anchor = null;
                ParkWorld = Vector3.zero;
                BodyRadius = 0f;
                ClearShipHull = false;
                WorldParked = false;
                VisualDirty = false;
                PendingWorldRelocate = false;
                LabeledMessage = null;
                PendingIcon = null;
                PendingColor = Color.white;
                LastVisualFlushTime = 0f;
                LastPopTime = 0f;
                LastFormatKey = int.MinValue;
            }
        }

        /// <summary>
        /// [TITAN-ORBIT] Profiler: heal / mining bursts called Refresh + ForceMeshUpdate per hit.
        /// Coalesce to one mesh rebuild per slot, and never faster than this while the streak is hot.
        /// </summary>
        const float MinVisualRefreshSeconds = 0.05f;
        const float MinPopReplaySeconds = 0.15f;

        readonly Stack<FloatingCountPopup> _popupPool = new Stack<FloatingCountPopup>(16);
        readonly Stack<LiveSlot> _slotPool = new Stack<LiveSlot>(16);
        readonly Dictionary<FloatingCountKey, LiveSlot> _slots = new Dictionary<FloatingCountKey, LiveSlot>(32);
        readonly Dictionary<FloatingCountPopup, FloatingCountKey> _keyByPopup =
            new Dictionary<FloatingCountPopup, FloatingCountKey>(32);
        readonly List<FloatingCountKey> _expireScratch = new List<FloatingCountKey>(8);
        readonly List<LiveSlot> _flushScratch = new List<LiveSlot>(16);

        Camera _cachedCamera;
        FloatingText _runtimeFallback;

        int _frameCacheTick = -1;
        float _frameZoom = 1f;
        bool _frameHasShipClearance;
        Vector3 _frameShipPos;
        float _frameShipTopY;
        float _frameShipRadius;

        public FloatingCountChannelVisibility FloatingCountVisibility =>
            Settings != null ? Settings.show : null;

        /// <summary>Active FloatingText asset (scene assignment, then Resources, then a runtime default).</summary>
        public FloatingText Settings
        {
            get
            {
                if (floatingText != null)
                    return floatingText;
                if (_runtimeFallback == null)
                    _runtimeFallback = FloatingText.LoadDefault();
                return _runtimeFallback;
            }
        }

        /// <summary>
        /// World-scale multiplier so text stays readable as the top-down camera rises with ship level.
        /// L1 → 1; clamped so MEGA framing does not produce giant type.
        /// </summary>
        public static float ResolveCameraZoomScale()
        {
            var inst = Instance;
            if (inst != null)
            {
                inst.EnsureFrameCache();
                return inst._frameZoom;
            }

            return ResolveCameraZoomScaleUncached();
        }

        static float ResolveCameraZoomScaleUncached()
        {
            var follow = CameraFollowEcs.Instance;
            if (follow == null)
                return 1f;
            return Mathf.Clamp(follow.CurrentHeightZoomFactor, ZoomScaleMin, ZoomScaleMax);
        }

        public static int TargetIdForShip(int networkId) =>
            TargetKindShip | (networkId & 0x00FFFFFF);

        public static int TargetIdForAsteroid(Entity entity)
        {
            unchecked
            {
                return TargetKindAsteroid ^ (entity.Index * 73856093) ^ (entity.Version * 19349663);
            }
        }

        public static int TargetIdForPlanet(int planetId) =>
            TargetKindPlanet | (planetId & 0x00FFFFFF);

        public static int TargetIdForWorldPosition(Vector3 worldPosition)
        {
            int x = Mathf.RoundToInt(worldPosition.x / 4f);
            int z = Mathf.RoundToInt(worldPosition.z / 4f);
            unchecked
            {
                return TargetKindWorld ^ (x * 397) ^ z;
            }
        }

        public static float ResolveShipBodyRadius(Transform shipAnchor)
        {
            if (shipAnchor == null)
                return BodyCollisionMath.MinShipHullRadiusWorld;

            float presentationScale = Mathf.Max(0.0001f, shipAnchor.lossyScale.x);
            float ecsScale = presentationScale / BodyCollisionMath.ShipPresentationScale;
            return BodyCollisionMath.GetShipHullRadiusWorld(ecsScale);
        }

        /// <summary>
        /// Local hull pose plus the clearance snapshot taken at hull spawn / chassis swap.
        /// Live position only — does not remesh.
        /// </summary>
        public bool TryGetLocalShipVisualClearance(out Vector3 shipPos, out float visualTopY, out float xzRadius)
        {
            EnsureFrameCache();
            shipPos = _frameShipPos;
            visualTopY = _frameShipTopY;
            xzRadius = _frameShipRadius;
            return _frameHasShipClearance;
        }

        bool TryGetLocalShipVisualClearanceUncached(out Vector3 shipPos, out float visualTopY, out float xzRadius)
        {
            shipPos = Vector3.zero;
            visualTopY = 0f;
            xzRadius = 0f;

            int localId = EcsGameBridge.GetLocalNetworkId();
            if (localId <= 0 ||
                !ShipWeaponProxyRegistry.TryGetHull(localId, out Transform hull) ||
                hull == null ||
                !ShipWeaponProxyRegistry.TryGetCachedHullClearance(localId, out float liftFromPivot, out xzRadius))
                return false;

            shipPos = hull.position;
            visualTopY = hull.position.y + liftFromPivot;
            return true;
        }

        void EnsureFrameCache()
        {
            int tick = Time.frameCount;
            if (_frameCacheTick == tick)
                return;

            _frameCacheTick = tick;
            _frameZoom = ResolveCameraZoomScaleUncached();
            _frameHasShipClearance = TryGetLocalShipVisualClearanceUncached(
                out _frameShipPos, out _frameShipTopY, out _frameShipRadius);
        }

        void Awake()
        {
            if (floatingText == null)
                floatingText = FloatingText.LoadDefault();

            if (Instance == null)
                Instance = this;
            else if (Instance != this)
            {
                Destroy(this);
                return;
            }
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        void Update()
        {
            EnsureFrameCache();
            ExpireStaleSlots();
        }

        void LateUpdate()
        {
            EnsureFrameCache();
            FlushDirtyVisuals(force: false);
        }

        Sprite ResolveTypeIcon(FloatingCountChannel channel)
        {
            var settings = Settings;
            Sprite fromAsset = settings != null ? settings.ResolveIcon(channel) : null;
            if (fromAsset != null)
                return fromAsset;

            return channel switch
            {
                FloatingCountChannel.GemPickup or FloatingCountChannel.GemDeposit => WorldStatLabelIcons.Gem,
                FloatingCountChannel.HealthChange or FloatingCountChannel.Healing
                    or FloatingCountChannel.HealthRegen => WorldStatLabelIcons.Shield,
                _ => WorldStatLabelIcons.Gem
            };
        }

        /// <summary>
        /// Ship-hull convenience: parks above the cached hull height and accumulates on <paramref name="networkId"/>.
        /// </summary>
        public void ShowOrAccumulateOnShip(
            int networkId,
            Transform shipAnchor,
            FloatingCountChannel channel,
            float signedAmount,
            TeamId team)
        {
            ShowOrAccumulate(
                TargetIdForShip(networkId),
                shipAnchor,
                ResolveShipBodyRadius(shipAnchor),
                channel,
                signedAmount,
                team,
                clearShipHull: true);
        }

        /// <summary>
        /// Legacy entry — still parks on the hull. Prefer <see cref="ShowOrAccumulateOnShip"/>.
        /// </summary>
        public void ShowFloatingCount(Transform shipAnchor, FloatingCountChannel channel, float signedAmount, TeamId team)
        {
            if (shipAnchor == null)
                return;
            ShowOrAccumulate(
                TargetIdForShip(shipAnchor.GetInstanceID()),
                shipAnchor,
                ResolveShipBodyRadius(shipAnchor),
                channel,
                signedAmount,
                team,
                clearShipHull: true);
        }

        /// <summary>
        /// Show or add to the live streak for this target. Parks on the target mid-center.
        /// </summary>
        public void ShowOrAccumulate(
            int targetId,
            Transform anchor,
            float bodyRadius,
            FloatingCountChannel channel,
            float signedAmount,
            TeamId team,
            Vector3? impactWorldPosition = null,
            bool ignoreChannelVisibility = false,
            bool clearShipHull = false)
        {
            if (anchor == null)
                return;
            if (TitanOrbitDebugFlags.IsolateDisableFloatingCounts)
                return;
            if (!TryPrepareAmount(channel, signedAmount, out int sign, ignoreChannelVisibility))
                return;

            _ = impactWorldPosition;
            Vector3 park = anchor.position;
            var key = new FloatingCountKey(targetId, channel, sign);
            var settings = Settings;
            float now = Time.unscaledTime;
            float window = settings != null ? settings.AccumulationWindowSeconds : 1f;

            if (_slots.TryGetValue(key, out LiveSlot slot) && slot.Popup != null)
            {
                if (!slot.Expired && now < slot.StreakDeadline)
                    slot.Accumulated += signedAmount;
                else
                    slot.Accumulated = signedAmount;

                slot.Expired = false;
                slot.StreakDeadline = now + window;
                slot.Team = team;
                slot.Channel = channel;
                slot.Anchor = anchor;
                slot.ParkWorld = park;
                slot.BodyRadius = bodyRadius;
                slot.ClearShipHull = clearShipHull;
                slot.WorldParked = false;
                slot.LabeledMessage = null;
                slot.VisualDirty = true;
                return;
            }

            if (!TryBuildFloatingCountVisual(channel, signedAmount, team, out string spawnMessage, out Sprite icon,
                    out Color spawnColor, out TMP_FontAsset fontToUse, ignoreChannelVisibility))
                return;

            int lane = ResolveStackLane(channel);
            float spacing = settings != null ? settings.StackLineSpacing : 1.25f;

            slot = RentSlot();
            slot.Accumulated = signedAmount;
            slot.StreakDeadline = now + window;
            slot.Expired = false;
            slot.Channel = channel;
            slot.Team = team;
            slot.Anchor = anchor;
            slot.ParkWorld = park;
            slot.BodyRadius = bodyRadius;
            slot.ClearShipHull = clearShipHull;
            slot.WorldParked = false;
            slot.LastVisualFlushTime = now;
            slot.LastPopTime = now;

            var popup = SpawnPopupAttached(
                spawnMessage,
                icon,
                spawnColor,
                $"FloatingCountPopup_{channel}",
                anchor,
                Vector3.zero,
                lane,
                spacing,
                fontToUse,
                bodyRadius,
                clearShipHull);
            if (popup == null)
            {
                ReturnSlot(slot);
                return;
            }

            slot.Popup = popup;
            _slots[key] = slot;
            _keyByPopup[popup] = key;
        }

        /// <summary>
        /// World-position popup (people transports). Accumulates on <paramref name="targetId"/>.
        /// </summary>
        public void ShowFloatingCountAtWorldPosition(
            Vector3 worldPosition,
            FloatingCountChannel channel,
            float signedAmount,
            TeamId team,
            Vector3 avoidCenter = default,
            float avoidRadius = 0f,
            int targetId = 0)
        {
            if (targetId == 0)
                targetId = TargetIdForWorldPosition(worldPosition);
            if (TitanOrbitDebugFlags.IsolateDisableFloatingCounts)
                return;

            if (!TryPrepareAmount(channel, signedAmount, out int sign))
                return;

            Vector3 spawnPos = worldPosition;
            if (avoidRadius > 0.01f)
                spawnPos = PlaceOutsideAvoidSphere(worldPosition, avoidCenter, avoidRadius);

            var key = new FloatingCountKey(targetId, channel, sign);
            var settings = Settings;
            float now = Time.unscaledTime;
            float window = settings != null ? settings.AccumulationWindowSeconds : 1f;

            // Recalled load flights refund the planet (+N) after leave pops accumulated −N.
            // Same planet + channel, opposite sign: replace the old total instead of leaving −N up.
            if (IsPeopleChannel(channel) && avoidRadius > 0.01f &&
                TryTakeOppositePeopleSlot(targetId, channel, sign, out LiveSlot flipped))
            {
                flipped.Accumulated = signedAmount;
                flipped.Expired = false;
                flipped.StreakDeadline = now + window;
                flipped.Team = team;
                flipped.Channel = channel;
                flipped.ParkWorld = spawnPos;

                if (!TryBuildFloatingCountVisual(channel, flipped.Accumulated, team, out string flipMessage,
                        out Sprite flipIcon, out Color flipColor, out _))
                    return;

                flipped.Popup.RelocateWorld(spawnPos, bodyRadius: 0f);
                flipped.Popup.Refresh(flipMessage, flipColor, followAnchor: null, followWorldOffset: Vector3.zero,
                    stackLane: 0, stackSpacing: 0f, flipIcon, bodyRadius: 0f);
                flipped.VisualDirty = false;
                flipped.LastVisualFlushTime = now;
                flipped.LastPopTime = now;
                _slots[key] = flipped;
                _keyByPopup[flipped.Popup] = key;
                return;
            }

            if (_slots.TryGetValue(key, out LiveSlot slot) && slot.Popup != null)
            {
                if (!slot.Expired && now < slot.StreakDeadline)
                    slot.Accumulated += signedAmount;
                else
                    slot.Accumulated = signedAmount;

                slot.Expired = false;
                slot.StreakDeadline = now + window;
                slot.Team = team;
                slot.Channel = channel;
                slot.ParkWorld = spawnPos;
                slot.BodyRadius = 0f;
                slot.ClearShipHull = false;
                slot.WorldParked = true;
                slot.PendingWorldRelocate = true;
                slot.LabeledMessage = null;
                slot.VisualDirty = true;
                return;
            }

            if (!TryBuildFloatingCountVisual(channel, signedAmount, team, out string spawnMessage, out Sprite icon,
                    out Color spawnColor, out TMP_FontAsset fontToUse))
                return;

            var popup = SpawnPopupAtWorldPosition(
                spawnMessage,
                icon,
                spawnColor,
                $"FloatingCountPopup_{channel}",
                spawnPos,
                fontToUse,
                bodyRadius: 0f);
            if (popup == null)
                return;

            slot = RentSlot();
            slot.Popup = popup;
            slot.Accumulated = signedAmount;
            slot.StreakDeadline = now + window;
            slot.Expired = false;
            slot.Channel = channel;
            slot.Team = team;
            slot.ParkWorld = spawnPos;
            slot.BodyRadius = 0f;
            slot.WorldParked = true;
            slot.LastVisualFlushTime = now;
            slot.LastPopTime = now;
            _slots[key] = slot;
            _keyByPopup[popup] = key;
        }

        /// <summary>
        /// Asteroid mining floats on the target mid-center. Damage accumulates; remaining HP is a separate stacked popup.
        /// </summary>
        public void ShowAsteroidFeedback(
            int targetId,
            Transform targetAnchor,
            float bodyRadius,
            AsteroidFloatingFeedback feedback,
            Vector3? impactWorldPosition = null)
        {
            if (targetAnchor == null)
                return;

            _ = impactWorldPosition;

            if (feedback.Damage.HasValue
                && feedback.Damage.Value > 0.0001f
                && (Settings == null || Settings.IsAsteroidDamageEnabled()))
            {
                ShowOrAccumulate(
                    targetId,
                    targetAnchor,
                    bodyRadius,
                    FloatingCountChannel.DamageAsteroid,
                    feedback.Damage.Value,
                    feedback.Team);
            }

            if (feedback.RemainingHealth.HasValue
                && (Settings == null || Settings.IsAsteroidHealthRemainingEnabled()))
            {
                ShowRemainingHealth(
                    targetId,
                    targetAnchor,
                    bodyRadius,
                    feedback.RemainingHealth.Value);
            }
        }

        /// <summary>
        /// Stacked remaining-HP label (replaces, does not sum). Same lane as asteroid HP Left.
        /// Callers own visibility (asteroid HP-remaining vs ship Health Change).
        /// </summary>
        public void ShowRemainingHealth(
            int targetId,
            Transform targetAnchor,
            float bodyRadius,
            float remainingHealth,
            bool clearShipHull = false)
        {
            if (targetAnchor == null)
                return;

            var settings = Settings;
            Color hpColor = settings != null ? settings.healthColor : new Color(0.2f, 0.9f, 0.3f, 1f);
            ShowOrRefreshLabeled(
                targetId,
                targetAnchor,
                targetAnchor.position,
                FloatingCountChannel.HealthChange,
                FormatUnsignedAmount(remainingHealth),
                hpColor,
                ResolveTypeIcon(FloatingCountChannel.HealthChange),
                bodyRadius,
                clearShipHull);
        }

        bool TryPrepareAmount(
            FloatingCountChannel channel,
            float signedAmount,
            out int sign,
            bool ignoreChannelVisibility = false)
        {
            sign = signedAmount >= 0f ? 1 : -1;
            if (!ignoreChannelVisibility && !IsFloatingCountChannelVisible(channel))
                return false;
            return Mathf.Abs(signedAmount) >= 0.01f;
        }

        bool TryBuildFloatingCountVisual(
            FloatingCountChannel channel,
            float signedAmount,
            TeamId team,
            out string message,
            out Sprite icon,
            out Color color,
            out TMP_FontAsset fontToUse,
            bool ignoreChannelVisibility = false)
        {
            message = null;
            icon = null;
            color = Color.white;
            var settings = Settings;
            fontToUse = settings != null ? settings.ResolveFont() : TMP_Settings.defaultFontAsset;

            if (!ignoreChannelVisibility && !IsFloatingCountChannelVisible(channel))
                return false;
            if (fontToUse == null)
                return false;
            if (Mathf.Abs(signedAmount) < 0.01f)
                return false;

            message = FormatSignedAmount(signedAmount);
            icon = ResolveTypeIcon(channel);
            color = settings != null ? settings.ResolveColor(channel, team) : Color.white;
            return true;
        }

        /// <summary>+/− amount: whole numbers stay bare, fractions get one decimal (5 → +5, 5.3 → +5.3).</summary>
        static string FormatSignedAmount(float signedAmount)
        {
            string body = FormatUnsignedAmount(Mathf.Abs(signedAmount));
            return signedAmount >= 0f ? $"+{body}" : $"-{body}";
        }

        static string FormatUnsignedAmount(float amount)
        {
            float abs = Mathf.Max(0f, amount);
            return abs.ToString("0.#", CultureInfo.InvariantCulture);
        }

        bool IsFloatingCountChannelVisible(FloatingCountChannel channel) =>
            Settings == null || Settings.IsEnabled(channel);

        static bool IsPeopleChannel(FloatingCountChannel channel) =>
            channel == FloatingCountChannel.PeopleLoad || channel == FloatingCountChannel.PeopleUnload;

        /// <summary>
        /// Pulls a live opposite-sign people slot off the same planet so a leave→return
        /// (or return→leave) can start a fresh total on the existing popup.
        /// </summary>
        bool TryTakeOppositePeopleSlot(
            int targetId,
            FloatingCountChannel channel,
            int sign,
            out LiveSlot slot)
        {
            slot = null;
            var opposite = new FloatingCountKey(targetId, channel, -sign);
            if (!_slots.TryGetValue(opposite, out LiveSlot existing) || existing.Popup == null)
                return false;

            _slots.Remove(opposite);
            _keyByPopup.Remove(existing.Popup);
            slot = existing;
            return true;
        }

        void ShowOrRefreshLabeled(
            int targetId,
            Transform anchor,
            Vector3 parkWorld,
            FloatingCountChannel keyChannel,
            string message,
            Color color,
            Sprite icon,
            float bodyRadius,
            bool clearShipHull = false)
        {
            if (anchor == null || string.IsNullOrEmpty(message))
                return;
            if (TitanOrbitDebugFlags.IsolateDisableFloatingCounts)
                return;

            var settings = Settings;
            TMP_FontAsset fontToUse = settings != null ? settings.ResolveFont() : TMP_Settings.defaultFontAsset;
            if (fontToUse == null)
                return;

            var key = new FloatingCountKey(targetId, keyChannel, 1);
            float now = Time.unscaledTime;
            float window = settings != null ? settings.AccumulationWindowSeconds : 1f;

            if (_slots.TryGetValue(key, out LiveSlot slot) && slot.Popup != null)
            {
                slot.Expired = false;
                slot.StreakDeadline = now + window;
                slot.Anchor = anchor;
                slot.ParkWorld = parkWorld;
                slot.BodyRadius = bodyRadius;
                slot.ClearShipHull = clearShipHull;
                slot.WorldParked = false;
                slot.LabeledMessage = message;
                slot.PendingColor = color;
                slot.PendingIcon = icon;
                slot.VisualDirty = true;
                return;
            }

            int lane = ResolveStackLane(keyChannel);
            float spacing = settings != null ? settings.StackLineSpacing : 1.25f;

            slot = RentSlot();
            slot.Accumulated = 0f;
            slot.StreakDeadline = now + window;
            slot.Expired = false;
            slot.Channel = keyChannel;
            slot.Anchor = anchor;
            slot.ParkWorld = parkWorld;
            slot.BodyRadius = bodyRadius;
            slot.ClearShipHull = clearShipHull;
            slot.LabeledMessage = message;
            slot.PendingColor = color;
            slot.PendingIcon = icon;
            slot.LastVisualFlushTime = now;
            slot.LastPopTime = now;

            var popup = SpawnPopupAttached(
                message,
                icon,
                color,
                $"FloatingCountPopup_{keyChannel}",
                anchor,
                Vector3.zero,
                lane,
                spacing,
                fontToUse,
                bodyRadius,
                clearShipHull);
            if (popup == null)
            {
                ReturnSlot(slot);
                return;
            }

            slot.Popup = popup;
            _slots[key] = slot;
            _keyByPopup[popup] = key;
        }

        static int ResolveStackLane(FloatingCountChannel channel)
        {
            switch (channel)
            {
                case FloatingCountChannel.DamageAsteroid:
                case FloatingCountChannel.DamageShipOrDrone:
                case FloatingCountChannel.DamageMoon:
                    return 0;
                case FloatingCountChannel.HealthChange:
                case FloatingCountChannel.Healing:
                case FloatingCountChannel.HealthRegen:
                    return 1;
                case FloatingCountChannel.GemPickup:
                case FloatingCountChannel.GemDeposit:
                    return 2;
                default:
                    return 0;
            }
        }

        static Vector3 GetPlayPlaneUp(Camera cam)
        {
            if (cam == null)
                return Vector3.forward;

            Vector3 dir = cam.transform.up;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-8f)
                dir = Vector3.ProjectOnPlane(-cam.transform.forward, Vector3.up);
            if (dir.sqrMagnitude < 1e-8f)
                dir = Vector3.forward;
            return dir.normalized;
        }

        FloatingCountPopup SpawnPopupAttached(
            string message,
            Sprite icon,
            Color color,
            string popupName,
            Transform anchor,
            Vector3 followWorldOffset,
            int stackLane,
            float stackSpacing,
            TMP_FontAsset fontToUse,
            float bodyRadius,
            bool clearShipHull = false)
        {
            if (string.IsNullOrEmpty(message) || anchor == null)
                return null;

            if (_cachedCamera == null)
                _cachedCamera = Camera.main;
            if (_cachedCamera == null)
                return null;

            var settings = Settings;
            var popup = RentPopup(popupName);
            popup.transform.position = anchor.position + followWorldOffset + (settings != null ? settings.worldOffset : Vector3.zero);
            popup.Initialize(
                message,
                icon,
                color,
                fontToUse,
                settings,
                followAnchor: anchor,
                followWorldOffset: followWorldOffset,
                stackLane: stackLane,
                stackSpacing: stackSpacing,
                bodyRadius: bodyRadius,
                clearShipHull: clearShipHull);
            return popup;
        }

        FloatingCountPopup SpawnPopupAtWorldPosition(
            string message,
            Sprite icon,
            Color color,
            string popupName,
            Vector3 worldPosition,
            TMP_FontAsset fontToUse,
            float bodyRadius)
        {
            if (string.IsNullOrEmpty(message))
                return null;
            if (_cachedCamera == null)
                _cachedCamera = Camera.main;
            if (_cachedCamera == null)
                return null;

            var settings = Settings;
            var popup = RentPopup(popupName);
            popup.transform.position = worldPosition + (settings != null ? settings.worldOffset : Vector3.zero);
            popup.Initialize(
                message,
                icon,
                color,
                fontToUse,
                settings,
                followAnchor: null,
                followWorldOffset: Vector3.zero,
                stackLane: 0,
                stackSpacing: 0f,
                bodyRadius: bodyRadius);
            return popup;
        }

        FloatingCountPopup RentPopup(string popupName)
        {
            FloatingCountPopup popup = null;
            while (_popupPool.Count > 0 && popup == null)
                popup = _popupPool.Pop();

            if (popup == null)
            {
                var go = new GameObject(popupName);
                popup = go.AddComponent<FloatingCountPopup>();
            }
            else
            {
                popup.gameObject.name = popupName;
                popup.gameObject.SetActive(true);
            }

            popup.OnFinished = ReturnPopup;
            return popup;
        }

        void ReturnPopup(FloatingCountPopup popup)
        {
            if (popup == null)
                return;

            if (_keyByPopup.TryGetValue(popup, out FloatingCountKey key))
            {
                _keyByPopup.Remove(popup);
                if (_slots.TryGetValue(key, out LiveSlot slot) && slot.Popup == popup)
                {
                    _slots.Remove(key);
                    ReturnSlot(slot);
                }
            }

            popup.OnFinished = null;
            popup.gameObject.SetActive(false);
            popup.transform.SetParent(transform, false);
            _popupPool.Push(popup);
        }

        LiveSlot RentSlot()
        {
            if (_slotPool.Count > 0)
            {
                LiveSlot rented = _slotPool.Pop();
                rented.Reset();
                return rented;
            }

            return new LiveSlot();
        }

        void ReturnSlot(LiveSlot slot)
        {
            if (slot == null)
                return;
            slot.Reset();
            _slotPool.Push(slot);
        }

        void FlushDirtyVisuals(bool force)
        {
            if (_slots.Count == 0)
                return;

            _flushScratch.Clear();
            foreach (var kv in _slots)
            {
                if (kv.Value.VisualDirty)
                    _flushScratch.Add(kv.Value);
            }

            for (int i = 0; i < _flushScratch.Count; i++)
                FlushSlotVisual(_flushScratch[i], force);
        }

        void FlushSlotVisual(LiveSlot slot, bool force)
        {
            if (slot == null || slot.Popup == null || !slot.VisualDirty)
                return;

            float now = Time.unscaledTime;
            bool restartingFade = !slot.Popup.IsHot;
            if (!force && !restartingFade &&
                slot.LastVisualFlushTime > 0f &&
                now - slot.LastVisualFlushTime < MinVisualRefreshSeconds)
                return;

            int formatKey = slot.LabeledMessage != null
                ? slot.LabeledMessage.GetHashCode()
                : Mathf.RoundToInt(slot.Accumulated * 10f);
            if (!force && !restartingFade && formatKey == slot.LastFormatKey)
            {
                slot.VisualDirty = false;
                return;
            }

            string message;
            Sprite icon;
            Color color;
            if (slot.LabeledMessage != null)
            {
                message = slot.LabeledMessage;
                icon = slot.PendingIcon;
                color = slot.PendingColor;
            }
            else if (!TryBuildFloatingCountVisual(slot.Channel, slot.Accumulated, slot.Team,
                         out message, out icon, out color, out _))
            {
                slot.VisualDirty = false;
                return;
            }

            if (slot.PendingWorldRelocate)
            {
                slot.Popup.RelocateWorld(slot.ParkWorld, slot.BodyRadius);
                slot.PendingWorldRelocate = false;
            }

            var settings = Settings;
            int lane = slot.WorldParked ? 0 : ResolveStackLane(slot.Channel);
            float spacing = slot.WorldParked ? 0f : (settings != null ? settings.StackLineSpacing : 1.25f);
            bool replayPop = restartingFade ||
                             slot.LastPopTime <= 0f ||
                             now - slot.LastPopTime >= MinPopReplaySeconds;
            if (replayPop)
                slot.LastPopTime = now;

            slot.Popup.Refresh(
                message,
                color,
                slot.WorldParked ? null : slot.Anchor,
                Vector3.zero,
                lane,
                spacing,
                icon,
                slot.BodyRadius,
                slot.ClearShipHull,
                replayPop);

            slot.LastVisualFlushTime = now;
            slot.LastFormatKey = formatKey;
            slot.VisualDirty = false;
        }

        void ExpireStaleSlots()
        {
            if (_slots.Count == 0)
                return;

            float now = Time.unscaledTime;
            _expireScratch.Clear();
            foreach (var kv in _slots)
            {
                if (!kv.Value.Expired && now >= kv.Value.StreakDeadline)
                    _expireScratch.Add(kv.Key);
            }

            var settings = Settings;
            float fade = settings != null ? settings.PostStreakFadeSeconds : 0.6f;
            for (int i = 0; i < _expireScratch.Count; i++)
            {
                FloatingCountKey key = _expireScratch[i];
                if (!_slots.TryGetValue(key, out LiveSlot slot))
                    continue;

                if (slot.VisualDirty)
                    FlushSlotVisual(slot, force: true);

                slot.Expired = true;
                slot.Accumulated = 0f;
                slot.Popup?.BeginFadeOut(fade);
            }
        }

        /// <summary>
        /// Parks people-transport text on the play plane, just outside the planet sphere
        /// along the leave/land radial — visible around the planet without sitting above it.
        /// </summary>
        Vector3 PlaceOutsideAvoidSphere(Vector3 hintPosition, Vector3 avoidCenter, float avoidRadius)
        {
            var settings = Settings;
            float clearance = settings != null ? settings.PlanetClearance : 1.25f;
            float height = settings != null ? settings.WorldPopupHeight : 0.4f;

            Vector3 flatHint = hintPosition;
            flatHint.y = 0f;
            Vector3 flatCenter = avoidCenter;
            flatCenter.y = 0f;

            Vector3 radial = flatHint - flatCenter;
            if (radial.sqrMagnitude < 1e-6f)
            {
                var cam = Camera.main;
                Vector3 playUp = GetPlayPlaneUp(cam);
                Vector3 playRight = Vector3.Cross(Vector3.up, playUp);
                if (playRight.sqrMagnitude < 1e-8f)
                    playRight = Vector3.right;
                radial = playRight.normalized;
            }
            else
            {
                radial.Normalize();
            }

            Vector3 pos = flatCenter + radial * (avoidRadius + clearance);
            pos.y = height;
            return pos;
        }
    }
}
