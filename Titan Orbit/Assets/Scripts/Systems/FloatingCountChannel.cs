using UnityEngine;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Identifies which gameplay action spawned a floating count popup. Visibility is configured on <see cref="FloatingCountChannelVisibility"/> (VisualEffectsManager).
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
    /// Per-channel visibility for world floating count popups. Serialized on <see cref="VisualEffectsManager"/> so each scene lists every toggle in the Inspector.
    /// </summary>
    [System.Serializable]
    public class FloatingCountChannelVisibility
    {
        [InspectorName("Gem pickup")]
        [Tooltip("Picking up loose gems in space.")]
        public bool gemPickup = true;
        [InspectorName("Gem deposit")]
        [Tooltip("Crediting gems to a planet (moon dock, flying gem, etc.).")]
        public bool gemDeposit = true;
        [InspectorName("Damage — asteroid")]
        [Tooltip("Damage dealt to asteroids (including HP/gems left overlay).")]
        public bool damageAsteroid = true;
        [InspectorName("Damage — ship / drone")]
        public bool damageShipOrDrone = true;
        [InspectorName("Damage — moon")]
        public bool damageMoon = true;
        [InspectorName("Health change")]
        [Tooltip("Positive/negative health deltas on your ship (not regen/healing sources).")]
        public bool healthChange = true;
        [InspectorName("People — load")]
        [Tooltip("People beaming from a friendly planet to your ship.")]
        public bool peopleLoad = true;
        [InspectorName("People — unload")]
        [Tooltip("People beaming from your ship to a planet.")]
        public bool peopleUnload = true;
        [InspectorName("Healing")]
        public bool healing = true;
        [InspectorName("Health regen")]
        public bool healthRegen = true;
        [InspectorName("Energy")]
        public bool energy = true;
        [InspectorName("Upgrades")]
        public bool upgrades = true;

        public bool IsEnabled(FloatingCountChannel channel)
        {
            switch (channel)
            {
                case FloatingCountChannel.GemPickup: return gemPickup;
                case FloatingCountChannel.GemDeposit: return gemDeposit;
                case FloatingCountChannel.DamageAsteroid: return damageAsteroid;
                case FloatingCountChannel.DamageShipOrDrone: return damageShipOrDrone;
                case FloatingCountChannel.DamageMoon: return damageMoon;
                case FloatingCountChannel.HealthChange: return healthChange;
                case FloatingCountChannel.PeopleLoad: return peopleLoad;
                case FloatingCountChannel.PeopleUnload: return peopleUnload;
                case FloatingCountChannel.Healing: return healing;
                case FloatingCountChannel.HealthRegen: return healthRegen;
                case FloatingCountChannel.Energy: return energy;
                case FloatingCountChannel.Upgrades: return upgrades;
                default: return true;
            }
        }
    }

    /// <summary>
    /// Optional asset for people load/unload icon and color on <see cref="VisualEffectsManager"/>.
    /// Per-channel visibility is set on the Visual Effects Manager (Floating Count Visibility).
    /// Create via Assets → Create → Titan Orbit → Floating Count Feedback Settings.
    /// </summary>
    [CreateAssetMenu(menuName = "Titan Orbit/Floating Count Feedback Settings", fileName = "FloatingCountFeedbackSettings")]
    public class FloatingCountFeedbackSettings : ScriptableObject
    {
        public const int MaxChannelIndex = (int)FloatingCountChannel.Upgrades;

        [Header("Channel toggles (not used at runtime)")]
        [Tooltip("Runtime visibility is set on VisualEffectsManager → Floating Count Visibility. These remain for older assets / reference.")]
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
