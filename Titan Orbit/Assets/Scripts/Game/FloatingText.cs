using TitanOrbit.Core;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Designer asset for world floating-count popups (damage, HP, gems, people, …).
    /// Sole default: <c>Assets/Resources/FloatingText.asset</c>.
    /// Client presentation only — no sim / NetCode impact.
    /// </summary>
    [CreateAssetMenu(fileName = "FloatingText", menuName = "Titan Orbit/Floating Text", order = 60)]
    public class FloatingText : ScriptableObject
    {
        public const string DefaultResourcesName = "FloatingText";

        [Header("Show")]
        [Tooltip("Toggle which floating-text types appear.")]
        public FloatingCountChannelVisibility show = new FloatingCountChannelVisibility();

        [Header("Icons")]
        [Tooltip("Gem pickup and deposit.")]
        public Sprite gemIcon;
        [Tooltip("Damage dealt (asteroid, ship, moon).")]
        public Sprite damageIcon;
        [Tooltip("Health change and remaining HP.")]
        public Sprite healthIcon;
        [Tooltip("Troop load and unload.")]
        public Sprite peopleIcon;
        [Tooltip("Energy.")]
        public Sprite energyIcon;
        [Tooltip("Upgrades.")]
        public Sprite upgradeIcon;

        [Header("Colors")]
        public Color damageColor = new Color(1f, 0.3f, 0.3f, 1f);
        public Color healthColor = new Color(0.2f, 0.9f, 0.3f, 1f);
        public Color peopleColor = new Color(1f, 0.9f, 0.25f, 1f);
        [Tooltip("Used when the popup has no team color (gems use team color when available).")]
        public Color gemFallbackColor = new Color(0.85f, 0.95f, 1f, 1f);
        public Color energyColor = new Color(0.35f, 0.75f, 1f, 1f);
        public Color upgradeColor = new Color(0.95f, 0.85f, 0.35f, 1f);

        [Header("Type")]
        [Tooltip("Optional. Empty uses the project TMP default.")]
        public TMP_FontAsset font;
        [Tooltip("TMP world-space font size. Camera zoom scales the whole popup, not this.")]
        public float fontSize = 32f;

        [Header("Icon Layout")]
        [Tooltip("Local scale of the type icon (popup root is already ~0.155).")]
        public float iconScale = 2f;
        [Tooltip("Gap between the icon's right edge and the left of the digits. Raise if the icon overlaps the number.")]
        public float iconLeftPadding = 8f;

        [Header("World Placement")]
        [Tooltip("Added on top of the target's visual height. Small rocks/ships sit lower; large ones sit higher.")]
        [FormerlySerializedAs("worldLiftY")]
        public float extraHeight = 8f;
        [Tooltip("Extra world-space nudge applied to every popup (ships, asteroids, transports).")]
        public Vector3 worldOffset = Vector3.zero;
        [Tooltip("Play-plane gap between stacked types on the same target (damage vs HP vs gems).")]
        public float stackLineSpacing = 1.25f;

        [Header("Troops / Planet")]
        [Tooltip("How far past the planet radius troop-transport text sits on the play plane.")]
        public float planetClearance = 1.25f;
        [Tooltip("World Y for troop-transport popups. Keep near 0 so text sits beside the planet, not above it.")]
        public float worldPopupHeight = 0.4f;

        [Header("Streak")]
        [Tooltip("Hits on the same target + type add together while this window is open. Each hit restarts the countdown.")]
        public float accumulationWindowSeconds = 1f;
        [Tooltip("Fade length after the streak window expires with no new hit.")]
        public float postStreakFadeSeconds = 0.6f;

        public float FontSize => Mathf.Max(1f, fontSize);
        public float IconScale => Mathf.Max(0.05f, iconScale);
        public float IconLeftPadding => Mathf.Max(0f, iconLeftPadding);
        public float ExtraHeight => Mathf.Max(0f, extraHeight);

        /// <summary>Target visual height (from mesh / radius) plus <see cref="ExtraHeight"/>.</summary>
        public float ResolveLiftY(float targetHeight) =>
            Mathf.Max(0f, targetHeight) + ExtraHeight;
        public float StackLineSpacing => Mathf.Max(0.1f, stackLineSpacing);
        public float PlanetClearance => Mathf.Max(0f, planetClearance);
        public float WorldPopupHeight => Mathf.Max(0f, worldPopupHeight);
        public float AccumulationWindowSeconds => Mathf.Max(0.05f, accumulationWindowSeconds);
        public float PostStreakFadeSeconds => Mathf.Max(0.08f, postStreakFadeSeconds);

        public bool IsEnabled(FloatingCountChannel channel) =>
            show == null || show.IsEnabled(channel);

        public bool IsAsteroidDamageEnabled() =>
            show == null || show.IsAsteroidDamageEnabled();

        public bool IsAsteroidHealthRemainingEnabled() =>
            show == null || show.IsAsteroidHealthRemainingEnabled();

        public TMP_FontAsset ResolveFont() =>
            font != null ? font : TMP_Settings.defaultFontAsset;

        public Sprite ResolveIcon(FloatingCountChannel channel)
        {
            return channel switch
            {
                FloatingCountChannel.GemPickup or FloatingCountChannel.GemDeposit => gemIcon,
                FloatingCountChannel.DamageAsteroid or FloatingCountChannel.DamageShipOrDrone
                    or FloatingCountChannel.DamageMoon => damageIcon,
                FloatingCountChannel.HealthChange or FloatingCountChannel.Healing
                    or FloatingCountChannel.HealthRegen => healthIcon,
                FloatingCountChannel.PeopleLoad or FloatingCountChannel.PeopleUnload => peopleIcon,
                FloatingCountChannel.Energy => energyIcon,
                FloatingCountChannel.Upgrades => upgradeIcon,
                _ => damageIcon
            };
        }

        public Color ResolveColor(FloatingCountChannel channel, TeamId team)
        {
            switch (channel)
            {
                case FloatingCountChannel.GemPickup:
                case FloatingCountChannel.GemDeposit:
                    return team != TeamId.None ? team.ToColor() : gemFallbackColor;
                case FloatingCountChannel.DamageAsteroid:
                case FloatingCountChannel.DamageShipOrDrone:
                case FloatingCountChannel.DamageMoon:
                    return damageColor;
                case FloatingCountChannel.HealthChange:
                case FloatingCountChannel.Healing:
                case FloatingCountChannel.HealthRegen:
                    return healthColor;
                case FloatingCountChannel.PeopleLoad:
                case FloatingCountChannel.PeopleUnload:
                    return peopleColor;
                case FloatingCountChannel.Energy:
                    return energyColor;
                case FloatingCountChannel.Upgrades:
                    return upgradeColor;
                default:
                    return Color.white;
            }
        }

        public static FloatingText LoadDefault() =>
            Resources.Load<FloatingText>(DefaultResourcesName);
    }
}
