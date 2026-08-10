using TitanOrbit.Core;
using UnityEngine;
using UnityEngine.Serialization;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Identifies which gameplay action spawned a floating count popup.
    /// Visibility is configured on <see cref="WorldFloatingCountManager"/>.
    /// </summary>
    public enum FloatingCountChannel
    {
        // --- Economy ---
        /// <summary>Loose gem collected into ship cargo.</summary>
        GemPickup = 0,
        /// <summary>Gems credited to planet or moon while docked.</summary>
        GemDeposit = 1,

        // --- Combat damage ---
        DamageAsteroid = 2,
        DamageShipOrDrone = 3,
        DamageMoon = 4,

        // --- Ship vitals and crew ---
        /// <summary>Hull damage or repair delta on ships.</summary>
        HealthChange = 5,
        PeopleLoad = 6,
        PeopleUnload = 7,
        Healing = 8,
        HealthRegen = 9,
        Energy = 10,

        // --- Progression ---
        /// <summary>Card purchase, component install, hull upgrade.</summary>
        Upgrades = 11,
    }

    /// <summary>
    /// Optional fields for stacked asteroid-hit feedback (damage, HP, gems, impact force).
    /// </summary>
    public struct AsteroidFloatingFeedback
    {
        public TeamId Team;
        public float? Damage;
        public float? RemainingHealth;
        public float? RemainingGems;
        public float? ImpactForceNewtons;
    }

    /// <summary>
    /// Per-channel visibility for world floating count popups (Inspector toggles on <see cref="WorldFloatingCountManager"/>).
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

        [Header("Asteroid hit feedback")]
        [InspectorName("Asteroid — damage dealt")]
        [Tooltip("Damage number when bullets or ramming hit an asteroid.")]
        [FormerlySerializedAs("damageAsteroid")]
        public bool asteroidDamage = true;

        [InspectorName("Asteroid — HP remaining")]
        [Tooltip("HP Left line after damaging an asteroid.")]
        [FormerlySerializedAs("asteroidStatsOverlay")]
        public bool asteroidHealthRemaining = true;

        [InspectorName("Asteroid — gems remaining")]
        [Tooltip("Gems remaining line after damaging an asteroid.")]
        public bool asteroidGemsRemaining = true;

        [InspectorName("Asteroid — impact force")]
        [Tooltip("Collision impact force (Newtons) on ship-asteroid hits.")]
        [FormerlySerializedAs("healthRegen")]
        public bool asteroidImpactForce = true;

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

        /// <summary>Whether floating popups are enabled for a given gameplay channel.</summary>
        public bool IsEnabled(FloatingCountChannel channel)
        {
            // --- Map gameplay channel to Inspector toggle ---
            switch (channel)
            {
                case FloatingCountChannel.GemPickup: return gemPickup;
                case FloatingCountChannel.GemDeposit: return gemDeposit;
                case FloatingCountChannel.DamageAsteroid: return asteroidDamage;
                case FloatingCountChannel.DamageShipOrDrone: return damageShipOrDrone;
                case FloatingCountChannel.DamageMoon: return damageMoon;
                case FloatingCountChannel.HealthChange: return healthChange;
                case FloatingCountChannel.PeopleLoad: return peopleLoad;
                case FloatingCountChannel.PeopleUnload: return peopleUnload;
                default: return true;
            }
        }

        public bool IsAsteroidDamageEnabled() => asteroidDamage;
        public bool IsAsteroidHealthRemainingEnabled() => asteroidHealthRemaining;
        public bool IsAsteroidGemsRemainingEnabled() => asteroidGemsRemaining;
        public bool IsAsteroidImpactForceEnabled() => asteroidImpactForce;
    }

    /// <summary>
    /// Optional asset for people load/unload icon and color on <see cref="WorldFloatingCountManager"/>.
    /// </summary>
    [CreateAssetMenu(menuName = "Titan Orbit/Floating Count Feedback Settings", fileName = "FloatingCountFeedbackSettings")]
    public class FloatingCountFeedbackSettings : ScriptableObject
    {
        public const int MaxChannelIndex = (int)FloatingCountChannel.Upgrades;

        [Header("People (load / unload)")]
        public Color peopleColor = new Color(1f, 0.9f, 0.25f, 1f);
        public Sprite peopleIcon;
    }
}
