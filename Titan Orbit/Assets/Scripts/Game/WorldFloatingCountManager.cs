using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Simulation;
using TMPro;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Client-side world floating +/- popups with per-channel visibility toggles.
    /// Spawned by EcsFloatingCountPresenter on replicated state deltas. Singleton accessed via Instance.
    /// </summary>
    public class WorldFloatingCountManager : MonoBehaviour
    {
        public static WorldFloatingCountManager Instance { get; private set; }

        [Header("Floating Count Popups")]
        [SerializeField] FloatingCountFeedbackSettings floatingCountFeedbackSettings;
        [SerializeField] FloatingCountChannelVisibility floatingCountVisibility = new FloatingCountChannelVisibility();
        [SerializeField] TMP_FontAsset floatingCountFont;
        [SerializeField] Sprite floatingCountGemIcon;
        [SerializeField] Sprite floatingCountDamageIcon;
        [SerializeField] Sprite floatingCountHealthIcon;
        [SerializeField] Sprite floatingCountPeopleIcon;
        [SerializeField] Color floatingCountPeopleColor = new Color(1f, 0.9f, 0.25f, 1f);

        [SerializeField] float floatingCountDuration = 1.7f;
        [SerializeField] float floatingCountRiseSpeed = 2.5f;
        [SerializeField] float floatingCountFontSize = 10f;
        [SerializeField] float floatingCountIconScale = 0.1f;
        [SerializeField] Vector3 floatingCountIconLocalOffset = new Vector3(-0.35f, 0f, 0f);
        [SerializeField] float floatingCountVerticalOffset = 3.5f;
        [Header("Ship-Local Popups")]
        [Tooltip("Font size for popups parented to the ship (toroidal / follow-ship mode).")]
        [SerializeField] float shipFloatingFontSize = 32f;
        [Tooltip("Base screen-up clearance on the XZ play plane, plus a hull-radius multiplier below.")]
        [SerializeField] float shipFloatingVerticalOffset = 1.75f;
        [SerializeField] float shipFloatingOffsetHullRadiusMultiplier = 6f;
        [SerializeField] float shipFloatingOffsetMin = 2f;
        [SerializeField] float shipFloatingOffsetMax = 5f;
        [SerializeField] float shipFloatingIconScale = 0.3f;
        [SerializeField] float shipFloatingStackLineSpacing = 1.25f;
        [Tooltip("Optional tiny screen-plane jitter; keep near 0 to stay centered above the ship.")]
        [SerializeField] float shipFloatingSpawnJitterRadius = 0.05f;
        [SerializeField] float shipFloatingLateralDriftMax = 0f;
        [Header("Floating Count Spread")]
        [SerializeField] float floatingCountSpawnJitterRadius = 1.05f;
        [SerializeField] float floatingCountSpawnRingStep = 0.32f;
        [SerializeField] float floatingCountMaxSpreadRadius = 3.75f;
        [SerializeField] int floatingCountSpiralPeriod = 40;
        [SerializeField] float floatingCountLateralDriftMax = 0.55f;
        [SerializeField] float floatingCountStackLineSpacing = 0.82f;

        [SerializeField] Color floatingCountDamageFallbackColor = new Color(1f, 0.3f, 0.3f, 1f);
        [SerializeField] Color floatingCountHealthPositiveColor = new Color(0.2f, 0.9f, 0.3f, 1f);
        [SerializeField] Color floatingCountHealthNegativeColor = new Color(0.95f, 0.25f, 0.2f, 1f);
        [SerializeField] Color floatingCountImpactForceColor = new Color(1f, 0.75f, 0.2f, 1f);

        public FloatingCountChannelVisibility FloatingCountVisibility => floatingCountVisibility;

        void Awake()
        {
            // --- Singleton guard for floating count spawner ---
            if (floatingCountVisibility == null)
                floatingCountVisibility = new FloatingCountChannelVisibility();

            if (Instance == null)
                Instance = this;
            else if (Instance != this)
            {
                Destroy(this);
                return;
            }

            EnsureDefaultAssets();
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        void EnsureDefaultAssets()
        {
#if UNITY_EDITOR
            if (floatingCountGemIcon == null)
            {
                floatingCountGemIcon = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(
                    "Assets/CleanFlatIcon/png_128/icon_line/icon_line_store/icon_line_store_25.png");
            }

            if (floatingCountDamageIcon == null)
            {
                floatingCountDamageIcon = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(
                    "Assets/CleanFlatIcon/png_128/icon_line/icon_line_game/icon_line_game_165.png");
            }

            if (floatingCountHealthIcon == null)
            {
                floatingCountHealthIcon = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(
                    "Assets/CleanFlatIcon/png_128/icon/icon_shield/icon_shield_20.png");
            }
#endif
        }

        /// <summary>
        /// Spawns a single-line popup parented to a ship hull anchor for the given channel delta.
        /// </summary>
        public void ShowFloatingCount(Transform shipAnchor, FloatingCountChannel channel, float signedAmount, TeamId team)
        {
            if (shipAnchor == null)
                return;

            if (!TryBuildFloatingCountVisual(channel, signedAmount, team, out string message, out Sprite icon, out Color color,
                    out TMP_FontAsset fontToUse))
                return;

            SpawnPopupAttached(message, icon, color, $"FloatingCountPopup_{channel}", shipAnchor, fontToUse);
        }

        /// <summary>
        /// Spawns a floating ±N popup at a fixed world position (does not follow a ship hull).
        /// Used by people transports so −1/+1 appear at the sphere leave/consume points.
        /// Matches ship-attached popup scale (<see cref="BodyCollisionMath.ShipPresentationScale"/>).
        /// When <paramref name="avoidRadius"/> &gt; 0, parks the popup outside that sphere so planet
        /// meshes do not clip the text.
        /// </summary>
        public void ShowFloatingCountAtWorldPosition(
            Vector3 worldPosition,
            FloatingCountChannel channel,
            float signedAmount,
            TeamId team,
            Vector3 avoidCenter = default,
            float avoidRadius = 0f)
        {
            if (!TryBuildFloatingCountVisual(channel, signedAmount, team, out string message, out Sprite icon, out Color color,
                    out TMP_FontAsset fontToUse))
                return;

            SpawnPopupAtWorldPosition(
                message, icon, color, $"FloatingCountPopup_{channel}", worldPosition, fontToUse,
                avoidCenter, avoidRadius);
        }

        /// <summary>
        /// Shared label/icon/color formatting for ship-attached and world-position popups.
        /// </summary>
        bool TryBuildFloatingCountVisual(
            FloatingCountChannel channel,
            float signedAmount,
            TeamId team,
            out string message,
            out Sprite icon,
            out Color color,
            out TMP_FontAsset fontToUse)
        {
            message = null;
            icon = null;
            color = Color.white;
            fontToUse = floatingCountFont != null ? floatingCountFont : TMP_Settings.defaultFontAsset;

            if (!IsFloatingCountChannelVisible(channel))
                return false;
            if (fontToUse == null)
                return false;

            float abs = Mathf.Abs(signedAmount);
            if (abs < 0.01f)
                return false;

            int amountInt = Mathf.RoundToInt(abs);
            if (amountInt <= 0)
                amountInt = 1;

            char sign = signedAmount >= 0f ? '+' : '-';
            bool compactGemLabel = channel == FloatingCountChannel.GemPickup || channel == FloatingCountChannel.GemDeposit;
            // People transports use compact ±N so the popup reads at the sphere, not a long "People" label.
            bool compactPeopleLabel = channel == FloatingCountChannel.PeopleLoad || channel == FloatingCountChannel.PeopleUnload;
            string label = channel switch
            {
                FloatingCountChannel.GemPickup => "Gems",
                FloatingCountChannel.GemDeposit => "Gems",
                FloatingCountChannel.DamageAsteroid => "Damage",
                FloatingCountChannel.DamageShipOrDrone => "Damage",
                FloatingCountChannel.DamageMoon => "Damage",
                FloatingCountChannel.HealthChange => "Health",
                FloatingCountChannel.PeopleLoad => "People",
                FloatingCountChannel.PeopleUnload => "People",
                FloatingCountChannel.Healing => "Health",
                FloatingCountChannel.HealthRegen => "Health",
                FloatingCountChannel.Energy => "Energy",
                FloatingCountChannel.Upgrades => "Upgrade",
                _ => "Value"
            };

            message = compactGemLabel || compactPeopleLabel ? $"{sign}{amountInt}" : $"{sign}{amountInt} {label}";

            switch (channel)
            {
                case FloatingCountChannel.GemPickup:
                case FloatingCountChannel.GemDeposit:
                    icon = floatingCountGemIcon;
                    color = team != TeamId.None ? team.ToColor() : new Color(0.85f, 0.95f, 1f, 1f);
                    break;

                case FloatingCountChannel.DamageAsteroid:
                case FloatingCountChannel.DamageShipOrDrone:
                case FloatingCountChannel.DamageMoon:
                    icon = floatingCountDamageIcon;
                    color = team != TeamId.None ? team.ToColor() : floatingCountDamageFallbackColor;
                    break;

                case FloatingCountChannel.HealthChange:
                case FloatingCountChannel.Healing:
                case FloatingCountChannel.HealthRegen:
                    icon = floatingCountHealthIcon;
                    color = channel == FloatingCountChannel.HealthChange
                        ? signedAmount >= 0f ? floatingCountHealthPositiveColor : floatingCountHealthNegativeColor
                        : floatingCountHealthPositiveColor;
                    break;

                case FloatingCountChannel.PeopleLoad:
                case FloatingCountChannel.PeopleUnload:
                    icon = floatingCountFeedbackSettings != null && floatingCountFeedbackSettings.peopleIcon != null
                        ? floatingCountFeedbackSettings.peopleIcon
                        : floatingCountPeopleIcon;
                    color = floatingCountFeedbackSettings != null
                        ? floatingCountFeedbackSettings.peopleColor
                        : floatingCountPeopleColor;
                    break;

                case FloatingCountChannel.Energy:
                    color = new Color(0.35f, 0.75f, 1f, 1f);
                    break;

                case FloatingCountChannel.Upgrades:
                    color = new Color(0.95f, 0.85f, 0.35f, 1f);
                    break;
            }

            return true;
        }

        public void ShowAsteroidFeedback(Transform shipAnchor, AsteroidFloatingFeedback feedback)
        {
            if (shipAnchor == null)
                return;

            FloatingCountStackLine[] lines = BuildAsteroidFeedbackLines(feedback);
            if (lines == null || lines.Length == 0)
                return;

            TMP_FontAsset fontToUse = floatingCountFont != null ? floatingCountFont : TMP_Settings.defaultFontAsset;
            if (fontToUse == null)
                return;

            var go = new GameObject("FloatingCountStack_AsteroidHit");
            ComputeShipFollowSpawn(shipAnchor, out float screenUpOffset, out Vector3 initialMotionOffset);
            go.transform.position = shipAnchor.position;
            ApplyShipFollowTransformScale(go.transform, shipAnchor);

            var popup = go.AddComponent<FloatingCountStackPopup>();
            popup.Initialize(
                lines,
                fontToUse,
                shipFloatingFontSize,
                shipFloatingStackLineSpacing,
                floatingCountDuration,
                floatingCountRiseSpeed,
                Mathf.Max(0f, shipFloatingLateralDriftMax),
                followAnchor: shipAnchor,
                followScreenUpOffset: screenUpOffset,
                initialWorldMotionOffset: initialMotionOffset);
        }

        FloatingCountStackLine[] BuildAsteroidFeedbackLines(AsteroidFloatingFeedback feedback)
        {
            if (floatingCountVisibility == null)
                return System.Array.Empty<FloatingCountStackLine>();

            var lines = new List<FloatingCountStackLine>(4);
            TeamId team = feedback.Team;

            if (floatingCountVisibility.IsAsteroidDamageEnabled()
                && feedback.Damage.HasValue
                && feedback.Damage.Value > 0.0001f)
            {
                int damageInt = Mathf.Max(1, Mathf.RoundToInt(feedback.Damage.Value));
                Color damageColor = team != TeamId.None ? team.ToColor() : floatingCountDamageFallbackColor;
                lines.Add(new FloatingCountStackLine($"+{damageInt} Damage", damageColor));
            }

            if (floatingCountVisibility.IsAsteroidHealthRemainingEnabled()
                && feedback.RemainingHealth.HasValue)
            {
                int hp = Mathf.Max(0, Mathf.RoundToInt(feedback.RemainingHealth.Value));
                lines.Add(new FloatingCountStackLine($"HP Left: {hp}", floatingCountHealthPositiveColor));
            }

            if (floatingCountVisibility.IsAsteroidGemsRemainingEnabled()
                && feedback.RemainingGems.HasValue)
            {
                int gems = Mathf.Max(0, Mathf.RoundToInt(feedback.RemainingGems.Value));
                Color gemsColor = team != TeamId.None ? team.ToColor() : new Color(0.85f, 0.95f, 1f, 1f);
                lines.Add(new FloatingCountStackLine($"Gems: {gems}", gemsColor));
            }

            if (floatingCountVisibility.IsAsteroidImpactForceEnabled()
                && feedback.ImpactForceNewtons.HasValue
                && feedback.ImpactForceNewtons.Value >= 1f)
            {
                int force = Mathf.RoundToInt(feedback.ImpactForceNewtons.Value);
                lines.Add(new FloatingCountStackLine($"{force:N0} Force", floatingCountImpactForceColor));
            }

            return lines.Count == 0 ? System.Array.Empty<FloatingCountStackLine>() : lines.ToArray();
        }

        bool IsFloatingCountChannelVisible(FloatingCountChannel channel) =>
            floatingCountVisibility == null || floatingCountVisibility.IsEnabled(channel);

        void ComputeShipFollowSpawn(Transform shipAnchor, out float screenUpOffset, out Vector3 initialMotionOffset)
        {
            screenUpOffset = ResolveShipFloatingOffset(shipAnchor);
            initialMotionOffset = ComputeSpawnJitterOffset();
        }

        Vector3 ComputeSpawnJitterOffset()
        {
            if (shipFloatingSpawnJitterRadius <= 0.001f)
                return Vector3.zero;

            var cam = Camera.main;
            Vector3 playUp = GetPlayPlaneUp(cam);
            Vector3 playRight = Vector3.Cross(Vector3.up, playUp);
            if (playRight.sqrMagnitude < 1e-8f)
                playRight = Vector3.right;
            playRight.Normalize();

            Vector2 jitter = Random.insideUnitCircle * shipFloatingSpawnJitterRadius;
            return playRight * jitter.x + playUp * jitter.y;
        }

        float ResolveShipFloatingOffset(Transform shipAnchor)
        {
            float hullRadius = BodyCollisionMath.MinShipHullRadiusWorld;
            if (shipAnchor != null)
            {
                float presentationScale = Mathf.Max(0.0001f, shipAnchor.lossyScale.x);
                float ecsScale = presentationScale / BodyCollisionMath.ShipPresentationScale;
                hullRadius = BodyCollisionMath.GetShipHullRadiusWorld(ecsScale);
            }

            float total = shipFloatingVerticalOffset + hullRadius * shipFloatingOffsetHullRadiusMultiplier;
            return Mathf.Clamp(total, shipFloatingOffsetMin, shipFloatingOffsetMax);
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

        void ApplyShipFollowTransformScale(Transform popupRoot, Transform shipAnchor)
        {
            float shipScale = shipAnchor != null
                ? Mathf.Max(0.0001f, shipAnchor.lossyScale.x)
                : BodyCollisionMath.ShipPresentationScale;
            popupRoot.localScale = Vector3.one * shipScale;
        }

        void SpawnPopupAttached(
            string message,
            Sprite icon,
            Color color,
            string popupName,
            Transform shipAnchor,
            TMP_FontAsset fontToUse)
        {
            if (string.IsNullOrEmpty(message) || shipAnchor == null)
                return;

            if (Camera.main == null)
                return;

            var go = new GameObject(popupName);
            ComputeShipFollowSpawn(shipAnchor, out float screenUpOffset, out Vector3 initialMotionOffset);
            go.transform.position = shipAnchor.position;
            ApplyShipFollowTransformScale(go.transform, shipAnchor);

            var popup = go.AddComponent<FloatingCountPopup>();
            popup.Initialize(
                message,
                icon,
                color,
                fontToUse,
                shipFloatingFontSize,
                floatingCountDuration,
                floatingCountRiseSpeed,
                shipFloatingIconScale,
                floatingCountIconLocalOffset,
                Mathf.Max(0f, shipFloatingLateralDriftMax),
                followAnchor: shipAnchor,
                followScreenUpOffset: screenUpOffset,
                initialWorldMotionOffset: initialMotionOffset);
        }

        /// <summary>
        /// World-space popup that rises in place (no hull follow) — people transport leave/consume.
        /// Uses ship presentation scale so font size matches hull-attached popups.
        /// </summary>
        void SpawnPopupAtWorldPosition(
            string message,
            Sprite icon,
            Color color,
            string popupName,
            Vector3 worldPosition,
            TMP_FontAsset fontToUse,
            Vector3 avoidCenter,
            float avoidRadius)
        {
            if (string.IsNullOrEmpty(message))
                return;
            if (Camera.main == null)
                return;

            // --- Place outside planet (or other body) when needed ---
            Vector3 spawnPos = worldPosition;
            Vector3 outwardBias = Vector3.zero;
            if (avoidRadius > 0.01f)
                spawnPos = PlaceOutsideAvoidSphere(worldPosition, avoidCenter, avoidRadius, out outwardBias);

            var go = new GameObject(popupName);
            go.transform.position = spawnPos;
            // [TITAN-ORBIT] Ship-attached popups inherit hull lossyScale (~ShipPresentationScale).
            // World popups must match that scale or TMP at shipFloatingFontSize looks huge.
            go.transform.localScale = Vector3.one * BodyCollisionMath.ShipPresentationScale;

            var popup = go.AddComponent<FloatingCountPopup>();
            popup.Initialize(
                message,
                icon,
                color,
                fontToUse,
                shipFloatingFontSize,
                floatingCountDuration,
                floatingCountRiseSpeed,
                shipFloatingIconScale,
                floatingCountIconLocalOffset,
                Mathf.Max(0f, shipFloatingLateralDriftMax),
                followAnchor: null,
                followScreenUpOffset: 0f,
                // Bias the first rise frame away from the planet so fade-up stays in empty space.
                initialWorldMotionOffset: outwardBias);
        }

        /// <summary>
        /// Parks a world popup just outside a planet/body sphere, then nudges along screen-up
        /// so the label sits in empty space beside the mesh rather than rising through it.
        /// </summary>
        Vector3 PlaceOutsideAvoidSphere(
            Vector3 hintPosition,
            Vector3 avoidCenter,
            float avoidRadius,
            out Vector3 outwardBias)
        {
            const float ClearanceMargin = 1.75f;
            const float ScreenUpNudge = 1.1f;

            Vector3 flatHint = hintPosition;
            flatHint.y = 0f;
            Vector3 flatCenter = avoidCenter;
            flatCenter.y = 0f;

            Vector3 radial = flatHint - flatCenter;
            if (radial.sqrMagnitude < 1e-6f)
            {
                // Degenerate: tip is at the center — push along camera play-plane right.
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

            float standOff = avoidRadius + ClearanceMargin;
            Vector3 pos = flatCenter + radial * standOff;
            Vector3 playUpNudge = GetPlayPlaneUp(Camera.main) * ScreenUpNudge;
            pos += playUpNudge;
            pos.y = Mathf.Max(hintPosition.y, 4f);

            // Small outward seed so FloatingCountPopup rise stays clear of the shell.
            outwardBias = radial * 0.35f + playUpNudge * 0.15f;
            return pos;
        }
    }
}
