using System;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Static MEGA part block — base values only. MEGAs are not Extra-Level or
    /// bottom-bar upgradable, so there are no <c>*PerExtraLevel</c> fields.
    /// </summary>
    [Serializable]
    public struct MegaShipPartStats
    {
        public float firePower;
        public float bulletSpeed;
        [Tooltip("Auto-fire acquire + bullet travel range in world units. Keep this short so MEGA guns only scan nearby ships.")]
        public float bulletRange;
        public float fireRate;
        public float rammingPower;
        public float healthCap;
        public float healthRegen;
        public float energyCap;
        public float energyRegen;
        public float moveSpeed;
        public float accelerationCap;
        public float turnSpeed;
        /// <summary>
        /// Degrees per second this weapon mount yaws toward its target.
        /// Hull <c>turnSpeed</c> is the ship body; this is per-barrel turret traverse.
        /// 0 in the catalog stays 0; in-game <see cref="ApplyRuntimeDefaultsAndMinimums"/> fills it.
        /// </summary>
        [Tooltip("Weapon mount yaw speed in degrees/sec. Hull turnSpeed is the ship body — this is the turret.")]
        public float weaponRotationSpeed;
        [InspectorName("Troop Cap")]
        public float maxPeople;

        /// <summary>
        /// Gem-cargo base for one cockpit, wing, or cargo copy.
        /// Live hold is this base plus 2 × ship level (<see cref="MegaShipStatsCalculator.GemCapPerHoldCopy"/>).
        /// 0 on a hold part means the regular starter base of 8. Guns and engines stay 0 and add no cargo.
        /// </summary>
        [InspectorName("Gem Cap")]
        [Tooltip("Gem base for one cockpit, wing, or cargo copy. Live hold is this + 2 × ship level. 0 uses the starter base of 8.")]
        public float maxGems;

        /// <summary>Copies into the shared ship-stat struct with every PerExtraLevel left at 0.</summary>
        public ShipComponentAbilityStats ToAbilityStats()
        {
            return new ShipComponentAbilityStats
            {
                firePower = firePower,
                bulletSpeed = bulletSpeed,
                bulletRange = bulletRange,
                fireRate = fireRate,
                rammingPower = rammingPower,
                healthCap = healthCap,
                healthRegen = healthRegen,
                energyCap = energyCap,
                energyRegen = energyRegen,
                moveSpeed = moveSpeed,
                accelerationCap = accelerationCap,
                turnSpeed = turnSpeed,
                maxPeople = maxPeople,
                maxGems = maxGems,
            };
        }

        /// <summary>
        /// Adds one part onto a hull total. Speed and range keep the highest weapon value
        /// so a single long gun does not get averaged away.
        /// </summary>
        public static MegaShipPartStats Sum(in MegaShipPartStats a, in MegaShipPartStats b)
        {
            return new MegaShipPartStats
            {
                firePower = a.firePower + b.firePower,
                bulletSpeed = Mathf.Max(a.bulletSpeed, b.bulletSpeed),
                bulletRange = Mathf.Max(a.bulletRange, b.bulletRange),
                fireRate = a.fireRate + b.fireRate,
                rammingPower = a.rammingPower + b.rammingPower,
                healthCap = a.healthCap + b.healthCap,
                healthRegen = a.healthRegen + b.healthRegen,
                energyCap = a.energyCap + b.energyCap,
                energyRegen = a.energyRegen + b.energyRegen,
                moveSpeed = a.moveSpeed + b.moveSpeed,
                accelerationCap = a.accelerationCap + b.accelerationCap,
                turnSpeed = a.turnSpeed + b.turnSpeed,
                // One fast turret should not be averaged away by unarmed parts.
                weaponRotationSpeed = Mathf.Max(a.weaponRotationSpeed, b.weaponRotationSpeed),
                maxPeople = a.maxPeople + b.maxPeople,
                maxGems = a.maxGems + b.maxGems,
            };
        }

        /// <summary>
        /// Multiplies only the health and energy pools, plus their regen.
        /// A family with more plates would otherwise stack a much larger hull
        /// and battery from the same per-part numbers. Guns, ramming, cruise,
        /// turn, troops, and gems stay on the catalog values — the family knob
        /// is not an offensive buff. A scale of 1, or an unset 0, returns
        /// <paramref name="stats"/> unchanged.
        /// </summary>
        /// <param name="stats">One component block, or a hull sum of those blocks.</param>
        /// <param name="scale">Family stat scale from <see cref="MegaShipCatalog.GetStatScaleForFamily"/>.</param>
        public static MegaShipPartStats ApplyFamilyStatScale(in MegaShipPartStats stats, float scale)
        {
            // --- Identity ---
            // [TITAN-ORBIT] 1 is the authored catalog. Skipping the multiply keeps
            // older assets (scale still 0, read as 1) on the numbers designers already tuned.
            if (scale <= 0.001f || Mathf.Abs(scale - 1f) <= 0.0001f)
                return stats;

            var scaled = stats;
            scaled.healthCap *= scale;
            scaled.healthRegen *= scale;
            scaled.energyCap *= scale;
            scaled.energyRegen *= scale;
            return scaled;
        }

        /// <summary>
        /// True when any stat except firePower is effectively zero. Firepower may stay 0
        /// (unarmed hulls do not shoot).
        /// </summary>
        public static bool HasMissingNonFirepower(in MegaShipPartStats s)
        {
            return s.bulletSpeed < 0.01f
                   || s.bulletRange < 0.01f
                   || s.fireRate < 0.01f
                   || s.rammingPower < 0.01f
                   || s.healthCap < 0.01f
                   || s.healthRegen < 0.01f
                   || s.energyCap < 0.01f
                   || s.energyRegen < 0.01f
                   || s.moveSpeed < 0.01f
                   || s.accelerationCap < 0.01f
                   || s.turnSpeed < 0.01f
                   || s.maxPeople < 0.01f
                   // Armed hulls need a traverse rate; unarmed (firePower 0) may leave this 0.
                   || (s.firePower > 0.01f && s.weaponRotationSpeed < 0.01f);
        }

        /// <summary>
        /// In-game resolve: zeros (except firePower) become <paramref name="defaults"/>,
        /// then every non-firepower value is raised to <paramref name="minimums"/>.
        /// Catalog stored sums stay raw — this is applied at runtime / UI only.
        /// </summary>
        public static MegaShipPartStats ApplyRuntimeDefaultsAndMinimums(
            in MegaShipPartStats raw,
            in MegaShipPartStats defaults,
            in MegaShipPartStats minimums)
        {
            return new MegaShipPartStats
            {
                firePower = raw.firePower,
                bulletSpeed = Resolve(raw.bulletSpeed, defaults.bulletSpeed, minimums.bulletSpeed),
                bulletRange = Resolve(raw.bulletRange, defaults.bulletRange, minimums.bulletRange),
                fireRate = Resolve(raw.fireRate, defaults.fireRate, minimums.fireRate),
                rammingPower = Resolve(raw.rammingPower, defaults.rammingPower, minimums.rammingPower),
                healthCap = Resolve(raw.healthCap, defaults.healthCap, minimums.healthCap),
                healthRegen = Resolve(raw.healthRegen, defaults.healthRegen, minimums.healthRegen),
                energyCap = Resolve(raw.energyCap, defaults.energyCap, minimums.energyCap),
                energyRegen = Resolve(raw.energyRegen, defaults.energyRegen, minimums.energyRegen),
                moveSpeed = Resolve(raw.moveSpeed, defaults.moveSpeed, minimums.moveSpeed),
                accelerationCap = Resolve(raw.accelerationCap, defaults.accelerationCap, minimums.accelerationCap),
                turnSpeed = Resolve(raw.turnSpeed, defaults.turnSpeed, minimums.turnSpeed),
                weaponRotationSpeed = Resolve(
                    raw.weaponRotationSpeed, defaults.weaponRotationSpeed, minimums.weaponRotationSpeed),
                maxPeople = Resolve(raw.maxPeople, defaults.maxPeople, minimums.maxPeople),
                // Gem cap is per hold part, not a hull-wide default. A 0 here must stay 0
                // so runtime defaults cannot invent cargo on a gun-only sum.
                maxGems = raw.maxGems,
            };
        }

        static float Resolve(float raw, float fallbackDefault, float minimum)
        {
            float value = raw > 0.01f ? raw : fallbackDefault;
            if (value > 0.01f && minimum > 0.01f && value < minimum)
                return minimum;
            return value;
        }
    }
}
