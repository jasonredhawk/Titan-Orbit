using System.Collections.Generic;
using TitanOrbit.Core;
using TMPro;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-side world floating +/- popups with per-channel visibility toggles (Inspector).
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

        int floatingPopupSequence;

        public FloatingCountChannelVisibility FloatingCountVisibility => floatingCountVisibility;

        void Awake()
        {
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

        public void ShowFloatingCount(Vector3 position, FloatingCountChannel channel, float signedAmount, TeamId team)
        {
            if (!IsFloatingCountChannelVisible(channel))
                return;

            TMP_FontAsset fontToUse = floatingCountFont != null ? floatingCountFont : TMP_Settings.defaultFontAsset;
            if (fontToUse == null)
                return;

            float abs = Mathf.Abs(signedAmount);
            if (abs < 0.01f)
                return;

            int amountInt = Mathf.RoundToInt(abs);
            if (amountInt <= 0)
                amountInt = 1;

            char sign = signedAmount >= 0f ? '+' : '-';
            bool compactGemLabel = channel == FloatingCountChannel.GemPickup || channel == FloatingCountChannel.GemDeposit;
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

            string message = compactGemLabel ? $"{sign}{amountInt}" : $"{sign}{amountInt} {label}";

            Sprite icon = null;
            Color color = Color.white;

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

            SpawnPopup(message, icon, color, $"FloatingCountPopup_{channel}", position, fontToUse);
        }

        public void ShowAsteroidFeedback(Vector3 position, AsteroidFloatingFeedback feedback)
        {
            FloatingCountStackLine[] lines = BuildAsteroidFeedbackLines(feedback);
            if (lines == null || lines.Length == 0)
                return;

            TMP_FontAsset fontToUse = floatingCountFont != null ? floatingCountFont : TMP_Settings.defaultFontAsset;
            if (fontToUse == null)
                return;

            Vector3 spawnPos = GetStackSpawnPosition(position);
            var go = new GameObject("FloatingCountStack_AsteroidHit");
            go.transform.position = spawnPos;

            var popup = go.AddComponent<FloatingCountStackPopup>();
            popup.Initialize(
                lines,
                fontToUse,
                floatingCountFontSize,
                floatingCountStackLineSpacing,
                floatingCountDuration,
                floatingCountRiseSpeed,
                Mathf.Max(0f, floatingCountLateralDriftMax));
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

        Vector3 GetStackSpawnPosition(Vector3 position)
        {
            Vector3 spawnPos = position;
            spawnPos.y = floatingCountVerticalOffset;
            if (spawnPos.y < 4f)
                spawnPos.y = 4f;
            return spawnPos;
        }

        Vector3 GetSpreadSpawnPosition(Vector3 position)
        {
            int n = floatingPopupSequence++;
            const float goldenAngle = 2.39996323f;
            float phase = (position.x * 12.9898f + position.z * 78.233f) * 0.215f;
            float angle = phase + goldenAngle * n;
            int period = Mathf.Max(8, floatingCountSpiralPeriod);
            float ringR = Mathf.Sqrt((n % period) + 1) * Mathf.Max(0.01f, floatingCountSpawnRingStep);
            ringR = Mathf.Min(ringR, Mathf.Max(0.5f, floatingCountMaxSpreadRadius));

            Vector3 ring = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * ringR;
            Vector2 jitter = Random.insideUnitCircle * Mathf.Max(0f, floatingCountSpawnJitterRadius);
            Vector3 spawnPos = position + ring + new Vector3(jitter.x, floatingCountVerticalOffset, jitter.y);

            if (spawnPos.y < 4f)
                spawnPos.y = 4f;
            return spawnPos;
        }

        void SpawnPopup(string message, Sprite icon, Color color, string popupName, Vector3 position, TMP_FontAsset fontToUse)
        {
            if (string.IsNullOrEmpty(message))
                return;

            if (Camera.main == null)
                return;

            Vector3 spawnPos = GetSpreadSpawnPosition(position);
            var go = new GameObject(popupName);
            go.transform.position = spawnPos;

            var popup = go.AddComponent<FloatingCountPopup>();
            popup.Initialize(
                message,
                icon,
                color,
                fontToUse,
                floatingCountFontSize,
                floatingCountDuration,
                floatingCountRiseSpeed,
                floatingCountIconScale,
                floatingCountIconLocalOffset,
                Mathf.Max(0f, floatingCountLateralDriftMax));
        }
    }
}
