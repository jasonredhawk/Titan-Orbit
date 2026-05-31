using UnityEngine;
using UnityEngine.Serialization;

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
        [Tooltip("Damage numbers on asteroids (bullets, ram, mines).")]
        public bool damageAsteroid = true;
        [InspectorName("Damage — ship / drone")]
        public bool damageShipOrDrone = true;
        [InspectorName("Damage — moon")]
        public bool damageMoon = true;
        [InspectorName("Health change")]
        [Tooltip("Positive/negative health deltas on your ship.")]
        public bool healthChange = true;
        [InspectorName("People — load")]
        [Tooltip("People beaming from a friendly planet to your ship.")]
        public bool peopleLoad = true;
        [InspectorName("People — unload")]
        [Tooltip("People beaming from your ship to a planet.")]
        public bool peopleUnload = true;
        [InspectorName("Asteroid stats overlay")]
        [Tooltip("HP Left / Gems remaining text when hitting asteroids.")]
        [FormerlySerializedAs("healing")]
        public bool asteroidStatsOverlay = true;
        [InspectorName("Asteroid impact force")]
        [Tooltip("Impact force numbers on ship-asteroid collisions.")]
        [FormerlySerializedAs("healthRegen")]
        public bool asteroidImpactForce = true;

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
                default: return true;
            }
        }

        public bool IsAsteroidStatsOverlayEnabled() => asteroidStatsOverlay;

        public bool IsAsteroidImpactForceEnabled() => asteroidImpactForce;
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

        [Header("People (load / unload)")]
        [Tooltip("Default yellow for +N People popups.")]
        public Color peopleColor = new Color(1f, 0.9f, 0.25f, 1f);
        [Tooltip("Optional: e.g. Shift UI Friends icon (Assets/Shift - Complete Sci-Fi UI/Textures/Icon/Friends.png).")]
        public Sprite peopleIcon;
    }
}
