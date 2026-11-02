using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Designer table of how many gems one point of each ship stat costs.
    /// A gun with 4 damage at 1.25 gems per damage costs 5 gems from damage alone.
    /// A plate with 6 health at 0.375 gems per health costs about 2 gems from health alone.
    /// The part's sticker is the sum of every stat it actually has.
    /// <para>
    /// [TITAN-ORBIT] Hull parts are priced after Extra Level, so a higher hull level
    /// costs more because the numbers got bigger. A gear card uses the same rates on
    /// the stats at the buy level, then multiplies by that level. Titan parts use this
    /// table on their catalog stats. The gear card, the orbit-tree hull pill, and the
    /// server debit all read <see cref="StatGemPricing"/>. Edit this asset, do not
    /// hard-code a new rate.
    /// </para>
    /// Lives at <c>Assets/Resources/StatGemRates.asset</c> so a dedicated server build
    /// loads the same numbers as the client. Create another via
    /// Assets → Create → Titan Orbit → Stat Gem Rates only if you mean to replace that file.
    /// </summary>
    [CreateAssetMenu(
        fileName = "StatGemRates",
        menuName = "Titan Orbit/Stat Gem Rates",
        order = 55)]
    public class StatGemRates : ScriptableObject
    {
        /// <summary>[UNITY] Name passed to <see cref="Resources.Load"/> (no folder, no extension).</summary>
        public const string ResourcesLoadName = "StatGemRates";

        [Header("Floor")]
        [Tooltip(
            "Lowest gem price of one part, even when every stat is zero. " +
            "A decorative cover with no numbers still occupies a slot. Set 0 to allow a free part.")]
        [Min(0f)]
        public float minimumPartGems = 0f;

        [Header("Offense — gems per 1 point")]
        [Tooltip("Gems for each point of damage (fire power) after Extra Level. A gun at 4 damage with 1.25 here costs 5 gems from damage.")]
        [Min(0f)]
        public float gemsPerDamage = 1.25f;

        [Tooltip("Gems for each world-unit per second of bullet speed. Typical guns are about 10, so 0.05 here is about half a gem.")]
        [Min(0f)]
        public float gemsPerBulletSpeed = 0.05f;

        [Tooltip("Gems for each world unit of bullet range. Typical guns are about 20, so 0.025 here is about half a gem.")]
        [Min(0f)]
        public float gemsPerBulletRange = 0.025f;

        [Tooltip("Gems for each shot per second. Typical guns are about 3, so 0.375 here is about 1 gem.")]
        [Min(0f)]
        public float gemsPerFireRate = 0.375f;

        [Tooltip("Gems for each point of ramming power.")]
        [Min(0f)]
        public float gemsPerRamming = 0.25f;

        [Header("Health — gems per 1 point")]
        [Tooltip("Gems for each hit point. A plate at 6 health with 0.375 here costs about 2 gems from health.")]
        [Min(0f)]
        public float gemsPerHealth = 0.375f;

        [Tooltip("Gems for each hit point regenerated per second. Regen numbers are small (often under 1).")]
        [Min(0f)]
        public float gemsPerHealthRegen = 0.75f;

        [Header("Energy — gems per 1 point")]
        [Tooltip("Gems for each point of energy capacity. Energy pools are often 20–40, so this rate stays under 1.")]
        [Min(0f)]
        public float gemsPerEnergy = 0.04375f;

        [Tooltip("Gems for each point of energy regenerated per second.")]
        [Min(0f)]
        public float gemsPerEnergyRegen = 0.125f;

        [Header("Movement — gems per 1 point")]
        [Tooltip("Gems for each point of move speed.")]
        [Min(0f)]
        public float gemsPerMoveSpeed = 0.1875f;

        [Tooltip("Gems for each point of acceleration.")]
        [Min(0f)]
        public float gemsPerAcceleration = 0.125f;

        [Tooltip(
            "Gems for each degree per second of turn. Fins are stored around 160 degrees, " +
            "so 0.00625 here is about 1 gem. Raise this if turn should cost more than a gun.")]
        [Min(0f)]
        public float gemsPerTurnSpeed = 0.00625f;

        [Header("Capacity — gems per 1 point")]
        [Tooltip("Gems for each gem-cargo slot on the part.")]
        [Min(0f)]
        public float gemsPerGemCap = 0.0625f;

        [Tooltip("Gems for each troop slot on the part.")]
        [Min(0f)]
        public float gemsPerTroopCap = 0.125f;

        [Tooltip("Gems for each world unit of wing tractor-beam reach.")]
        [Min(0f)]
        public float gemsPerTractorRange = 0.01875f;

        [Tooltip("Gems for each point of tractor-beam power.")]
        [Min(0f)]
        public float gemsPerTractorPower = 0.125f;

        [Tooltip(
            "Gems for a full +100% overdrive (extraSpeedPercent of 1). " +
            "A 50% overdrive is half of this. 0 leaves overdrive off the sticker.")]
        [Min(0f)]
        public float gemsPerOverdrive = 0f;

        static StatGemRates s_cached;

        /// <summary>
        /// The Resources asset, or a hidden in-memory copy of the field defaults if the file is missing.
        /// Called from the gear card and from the server debit. Not a per-frame hot path.
        /// </summary>
        public static StatGemRates Load()
        {
            if (s_cached != null)
                return s_cached;

            // [UNITY] Same file in the Editor, the player, and the dedicated server.
            StatGemRates fromResources = Resources.Load<StatGemRates>(ResourcesLoadName);
            if (fromResources != null)
            {
                fromResources.ClampRates();
                s_cached = fromResources;
                return s_cached;
            }

            StatGemRates fallback = CreateInstance<StatGemRates>();
            fallback.hideFlags = HideFlags.HideAndDontSave;
            fallback.ClampRates();
            s_cached = fallback;
            return s_cached;
        }

        /// <summary>
        /// Drops negative rates. A negative gems-per-health would pay the player to buy armor.
        /// </summary>
        public void ClampRates()
        {
            minimumPartGems = Mathf.Max(0f, minimumPartGems);
            gemsPerDamage = Mathf.Max(0f, gemsPerDamage);
            gemsPerBulletSpeed = Mathf.Max(0f, gemsPerBulletSpeed);
            gemsPerBulletRange = Mathf.Max(0f, gemsPerBulletRange);
            gemsPerFireRate = Mathf.Max(0f, gemsPerFireRate);
            gemsPerRamming = Mathf.Max(0f, gemsPerRamming);
            gemsPerHealth = Mathf.Max(0f, gemsPerHealth);
            gemsPerHealthRegen = Mathf.Max(0f, gemsPerHealthRegen);
            gemsPerEnergy = Mathf.Max(0f, gemsPerEnergy);
            gemsPerEnergyRegen = Mathf.Max(0f, gemsPerEnergyRegen);
            gemsPerMoveSpeed = Mathf.Max(0f, gemsPerMoveSpeed);
            gemsPerAcceleration = Mathf.Max(0f, gemsPerAcceleration);
            gemsPerTurnSpeed = Mathf.Max(0f, gemsPerTurnSpeed);
            gemsPerGemCap = Mathf.Max(0f, gemsPerGemCap);
            gemsPerTroopCap = Mathf.Max(0f, gemsPerTroopCap);
            gemsPerTractorRange = Mathf.Max(0f, gemsPerTractorRange);
            gemsPerTractorPower = Mathf.Max(0f, gemsPerTractorPower);
            gemsPerOverdrive = Mathf.Max(0f, gemsPerOverdrive);
        }
    }
}
