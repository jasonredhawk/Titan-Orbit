using UnityEngine;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Identifies which gameplay action spawned a floating count popup. Used for per-action toggles in <see cref="FloatingCountFeedbackSettings"/>.
    /// </summary>
    public enum FloatingCountChannel
    {
        GemPickup = 0,
        GemDeposit = 1,
        DamageAsteroid = 2,
        DamageShipOrDrone = 3,
        DamageMoon = 4,
        HealthChange = 5,
        PeopleLoad = 6,
        PeopleUnload = 7,
        Healing = 8,
        HealthRegen = 9,
        Energy = 10,
        Upgrades = 11,
    }

    /// <summary>
    /// Assign on <see cref="VisualEffectsManager"/> to enable/disable floating counts per action and tune people color/icon.
    /// Create via Assets → Create → Titan Orbit → Floating Count Feedback Settings.
    /// </summary>
    [CreateAssetMenu(menuName = "Titan Orbit/Floating Count Feedback Settings", fileName = "FloatingCountFeedbackSettings")]
    public class FloatingCountFeedbackSettings : ScriptableObject
    {
        public const int MaxChannelIndex = (int)FloatingCountChannel.Upgrades;

        [Header("Show floating text for…")]
        [Tooltip("Picking up loose gems in space.")]
        public bool showGemPickup = true;
        [Tooltip("Crediting gems to a planet (moon dock, flying gem, etc.).")]
        public bool showGemDeposit = true;
        public bool showDamageAsteroid = true;
        public bool showDamageShipOrDrone = true;
        public bool showDamageMoon = true;
        public bool showHealthChange = true;
        [Tooltip("People beaming from a friendly planet to your ship.")]
        public bool showPeopleLoad = true;
        [Tooltip("People beaming from your ship to a planet.")]
        public bool showPeopleUnload = true;
        public bool showHealing = true;
        public bool showHealthRegen = true;
        public bool showEnergy = true;
        public bool showUpgrades = true;

        [Header("People (load / unload)")]
        [Tooltip("Default yellow for +N People popups.")]
        public Color peopleColor = new Color(1f, 0.9f, 0.25f, 1f);
        [Tooltip("Optional: e.g. Shift UI Friends icon (Assets/Shift - Complete Sci-Fi UI/Textures/Icon/Friends.png).")]
        public Sprite peopleIcon;

        public bool IsEnabled(FloatingCountChannel channel)
        {
            switch (channel)
            {
                case FloatingCountChannel.GemPickup: return showGemPickup;
                case FloatingCountChannel.GemDeposit: return showGemDeposit;
                case FloatingCountChannel.DamageAsteroid: return showDamageAsteroid;
                case FloatingCountChannel.DamageShipOrDrone: return showDamageShipOrDrone;
                case FloatingCountChannel.DamageMoon: return showDamageMoon;
                case FloatingCountChannel.HealthChange: return showHealthChange;
                case FloatingCountChannel.PeopleLoad: return showPeopleLoad;
                case FloatingCountChannel.PeopleUnload: return showPeopleUnload;
                case FloatingCountChannel.Healing: return showHealing;
                case FloatingCountChannel.HealthRegen: return showHealthRegen;
                case FloatingCountChannel.Energy: return showEnergy;
                case FloatingCountChannel.Upgrades: return showUpgrades;
                default: return true;
            }
        }
    }
}
