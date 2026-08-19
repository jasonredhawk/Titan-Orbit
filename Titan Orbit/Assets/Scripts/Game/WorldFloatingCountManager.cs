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
    /// </summary>
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
        }

        readonly Stack<FloatingCountPopup> _popupPool = new Stack<FloatingCountPopup>(16);
        readonly Dictionary<FloatingCountKey, LiveSlot> _slots = new Dictionary<FloatingCountKey, LiveSlot>(32);
        readonly Dictionary<FloatingCountPopup, FloatingCountKey> _keyByPopup =
            new Dictionary<FloatingCountPopup, FloatingCountKey>(32);
        readonly List<FloatingCountKey> _expireScratch = new List<FloatingCountKey>(8);

        Camera _cachedCamera;
        FloatingText _runtimeFallback;

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
            ExpireStaleSlots();
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
            int lane = ResolveStackLane(channel);
            float spacing = settings != null ? settings.StackLineSpacing : 1.25f;

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

                if (!TryBuildFloatingCountVisual(channel, slot.Accumulated, team, out string message, out Sprite refreshIcon,
                        out Color color, out _, ignoreChannelVisibility))
                    return;

                slot.Popup.Refresh(message, color, anchor, Vector3.zero, lane, spacing, refreshIcon, bodyRadius,
                    clearShipHull);
                return;
            }

            if (!TryBuildFloatingCountVisual(channel, signedAmount, team, out string spawnMessage, out Sprite icon,
                    out Color spawnColor, out TMP_FontAsset fontToUse, ignoreChannelVisibility))
                return;

            slot = new LiveSlot
            {
                Accumulated = signedAmount,
                StreakDeadline = now + window,
                Expired = false,
                Channel = channel,
                Team = team,
                Anchor = anchor,
                ParkWorld = park,
            };

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
                return;

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

                if (!TryBuildFloatingCountVisual(channel, slot.Accumulated, team, out string message, out Sprite refreshIcon,
                        out Color color, out _))
                    return;

                slot.Popup.RelocateWorld(spawnPos, bodyRadius: 0f);
                slot.Popup.Refresh(message, color, followAnchor: null, followWorldOffset: Vector3.zero,
                    stackLane: 0, stackSpacing: 0f, refreshIcon, bodyRadius: 0f);
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

            slot = new LiveSlot
            {
                Popup = popup,
                Accumulated = signedAmount,
                StreakDeadline = now + window,
                Expired = false,
                Channel = channel,
                Team = team,
                ParkWorld = spawnPos,
            };
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
            int lane = ResolveStackLane(keyChannel);
            float spacing = settings != null ? settings.StackLineSpacing : 1.25f;

            if (_slots.TryGetValue(key, out LiveSlot slot) && slot.Popup != null)
            {
                slot.Expired = false;
                slot.StreakDeadline = now + window;
                slot.Anchor = anchor;
                slot.ParkWorld = parkWorld;
                slot.Popup.Refresh(message, color, anchor, Vector3.zero, lane, spacing, icon, bodyRadius,
                    clearShipHull);
                return;
            }

            slot = new LiveSlot
            {
                Accumulated = 0f,
                StreakDeadline = now + window,
                Expired = false,
                Channel = keyChannel,
                Anchor = anchor,
                ParkWorld = parkWorld,
            };

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
                return;

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
                    _slots.Remove(key);
            }

            popup.OnFinished = null;
            popup.gameObject.SetActive(false);
            popup.transform.SetParent(transform, false);
            _popupPool.Push(popup);
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
