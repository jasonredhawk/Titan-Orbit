using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Identifies which gameplay action spawned a floating-count popup.
    /// Visibility is configured on <see cref="FloatingText"/>.
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
    /// Asteroid-hit feedback shown as stacked floating text (damage + remaining HP).
    /// </summary>
    public struct AsteroidFloatingFeedback
    {
        public TeamId Team;
        public float? Damage;
        public float? RemainingHealth;
    }

    /// <summary>
    /// Per-type on/off toggles for world floating-count popups (Inspector on <see cref="FloatingText"/>).
    /// </summary>
    [System.Serializable]
    public class FloatingCountChannelVisibility
    {
        [InspectorName("Gem pickup")]
        public bool gemPickup = true;

        [InspectorName("Gem deposit")]
        public bool gemDeposit = true;

        [Header("Asteroid")]
        [InspectorName("Asteroid — damage dealt")]
        public bool asteroidDamage = true;

        [InspectorName("Asteroid — HP remaining")]
        public bool asteroidHealthRemaining = true;

        [InspectorName("Damage — ship / drone")]
        public bool damageShipOrDrone = true;

        [InspectorName("Damage — moon")]
        public bool damageMoon = true;

        [InspectorName("Health change")]
        public bool healthChange = true;

        [InspectorName("Healing")]
        public bool healing = true;

        [InspectorName("Health regen")]
        public bool healthRegen = true;

        [InspectorName("People — load")]
        public bool peopleLoad = true;

        [InspectorName("People — unload")]
        public bool peopleUnload = true;

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
                case FloatingCountChannel.DamageAsteroid: return asteroidDamage;
                case FloatingCountChannel.DamageShipOrDrone: return damageShipOrDrone;
                case FloatingCountChannel.DamageMoon: return damageMoon;
                case FloatingCountChannel.HealthChange: return healthChange;
                case FloatingCountChannel.Healing: return healing;
                case FloatingCountChannel.HealthRegen: return healthRegen;
                case FloatingCountChannel.PeopleLoad: return peopleLoad;
                case FloatingCountChannel.PeopleUnload: return peopleUnload;
                case FloatingCountChannel.Energy: return energy;
                case FloatingCountChannel.Upgrades: return upgrades;
                default: return true;
            }
        }

        public bool IsAsteroidDamageEnabled() => asteroidDamage;
        public bool IsAsteroidHealthRemainingEnabled() => asteroidHealthRemaining;
    }
}
