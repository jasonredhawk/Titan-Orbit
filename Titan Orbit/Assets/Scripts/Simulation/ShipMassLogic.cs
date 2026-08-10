using TitanOrbit.Data;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Hull mass helpers for orbit/recoil, HUD estimates, combat bounce, and ComponentSize storage.
    /// <para>
    /// [TITAN-ORBIT] Live hull size (box extents × attribute grow × tier scale) is computed in
    /// <c>ShipHullColliderLogic.ComputeLiveHullComponentMass</c>, then fed here:
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="ComputeHullMassReference"/> — ComponentSize stored on
    /// <c>ShipMotorConfig.HullMassReference</c> (feeds mobility totalMass via
    /// <c>MassPerComponentSize</c>).</item>
    /// <item><see cref="ComputeMovementMass"/> — orbit / recoil scalar (hull bulk + current cargo).
    /// Cargo uses <see cref="ShipCargoMobilitySettings.massPerGem"/> /
    /// <see cref="ShipCargoMobilitySettings.massPerPerson"/>.</item>
    /// <item><see cref="ComputeRammingMass"/> — bounce / energy transfer via ShipCollisionImpulseLogic.</item>
    /// </list>
    /// Flight acceleration no longer divides by this mass — subtractive mass tax sets accel directly.
    /// </summary>
    public static class ShipMassLogic
    {
        public const float DefaultBaseMass = 1f;
        /// <summary>Scales authored chassis size into HullMassReference / ComponentSize.</summary>
        public const float HullMassScale = 0.7f;
        /// <summary>Exponent &lt; 1 softens HP bulk for orbit/recoil mass (0.4 = fourth-root scaling).</summary>
        public const float MovementHullBulkExponent = 0.4f;
        /// <summary>Gems count heavier for ramming collisions than for movement mass.</summary>
        public const float RammingGemMassScale = 2.5f;
        /// <summary>People count heavier for ramming than for movement mass.</summary>
        public const float RammingPersonMassScale = 2f;
        public const float DefaultBrakeDeceleration = 7f;
        public const float MinMass = 0.5f;

        /// <summary>
        /// Softens HP-based mass increase for orbit/recoil. Level-10 ships aren't 10× harder to capture.
        /// </summary>
        public static float GetMovementBulkScale(float maxHealth, float chassisReferenceHealth)
        {
            // --- Compute value ---
            float bulk = maxHealth / math.max(1f, chassisReferenceHealth);
            if (bulk <= 1f || MovementHullBulkExponent >= 0.999f)
                return bulk;
            if (MovementHullBulkExponent <= 0.001f)
                return 1f;
            return math.pow(bulk, MovementHullBulkExponent);
        }

        /// <summary>Linear HP scaling for ramming — bigger ships hit harder.</summary>
        public static float GetRammingBulkScale(float maxHealth, float chassisReferenceHealth) =>
            maxHealth / Mathf.Max(1f, chassisReferenceHealth);

        /// <summary>
        /// ComponentSize reference before HP bulk and cargo — stored as HullMassReference.
        /// </summary>
        public static float ComputeHullMassReference(float componentSize, float baseMass = DefaultBaseMass)
        {
            float hull = componentSize > 0f ? componentSize : baseMass;
            return Mathf.Max(MinMass, hull * HullMassScale);
        }

        /// <summary>
        /// Mass scalar for orbit align / recoil (softened hull bulk + current gems/people).
        /// Reads MassPerGem / MassPerPerson from <see cref="ShipCargoMobilitySettingsCache"/>.
        /// </summary>
        /// <param name="currentPeople">Colonists currently aboard (0 when unused).</param>
        public static float ComputeMovementMass(
            float hullMassReference,
            float maxHealth,
            float chassisReferenceHealth,
            float currentGems,
            float baseMass = DefaultBaseMass,
            float currentPeople = 0f)
        {
            ResolveCargoMassWeights(out float massPerGem, out float massPerPerson);
            return ComputeMovementMass(
                hullMassReference,
                maxHealth,
                chassisReferenceHealth,
                currentGems,
                baseMass,
                currentPeople,
                massPerGem,
                massPerPerson);
        }

        /// <summary>
        /// [ECS/DOTS] Burst-safe movement mass with explicit cargo weights from settings.
        /// </summary>
        public static float ComputeMovementMass(
            float hullMassReference,
            float maxHealth,
            float chassisReferenceHealth,
            float currentGems,
            float baseMass,
            float currentPeople,
            float massPerGem,
            float massPerPerson)
        {
            float hullRef = hullMassReference > 0f
                ? hullMassReference
                : math.max(MinMass, baseMass * HullMassScale);
            float bulkScale = GetMovementBulkScale(maxHealth, chassisReferenceHealth);
            float cargoMass = Mathf.Max(0f, currentGems) * math.max(0f, massPerGem)
                              + Mathf.Max(0f, currentPeople) * math.max(0f, massPerPerson);
            return math.max(MinMass, hullRef * bulkScale + cargoMass);
        }

        /// <summary>Hull-only mass baseline for ramming damage calculations.</summary>
        public static float ComputeRammingHullMassBaseline(
            float hullMassReference,
            float maxHealth,
            float chassisReferenceHealth,
            float baseMass = DefaultBaseMass)
        {
            float hullRef = hullMassReference > 0f
                ? hullMassReference
                : ComputeHullMassReference(0f, baseMass);
            float bulkScale = GetRammingBulkScale(maxHealth, chassisReferenceHealth);
            return Mathf.Max(MinMass, hullRef * bulkScale);
        }

        /// <summary>Total mass for ship-vs-ship ramming (hull + weighted gems + people).</summary>
        /// <param name="currentPeople">Colonists currently aboard (0 when unused).</param>
        public static float ComputeRammingMass(
            float hullMassReference,
            float maxHealth,
            float chassisReferenceHealth,
            float currentGems,
            float baseMass = DefaultBaseMass,
            float currentPeople = 0f)
        {
            ResolveCargoMassWeights(out float massPerGem, out float massPerPerson);
            return ComputeRammingMass(
                hullMassReference,
                maxHealth,
                chassisReferenceHealth,
                currentGems,
                baseMass,
                currentPeople,
                massPerGem,
                massPerPerson);
        }

        /// <summary>[ECS/DOTS] Burst-safe ramming mass with explicit cargo weights.</summary>
        public static float ComputeRammingMass(
            float hullMassReference,
            float maxHealth,
            float chassisReferenceHealth,
            float currentGems,
            float baseMass,
            float currentPeople,
            float massPerGem,
            float massPerPerson)
        {
            float hullMass = ComputeRammingHullMassBaseline(
                hullMassReference, maxHealth, chassisReferenceHealth, baseMass);
            float gemMass = Mathf.Max(0f, currentGems) * math.max(0f, massPerGem) * Mathf.Max(1f, RammingGemMassScale);
            float peopleMass = Mathf.Max(0f, currentPeople) * math.max(0f, massPerPerson) * Mathf.Max(1f, RammingPersonMassScale);
            return Mathf.Max(MinMass, hullMass + gemMass + peopleMass);
        }

        /// <summary>Reads MassPerGem / MassPerPerson from the mobility settings asset.</summary>
        static void ResolveCargoMassWeights(out float massPerGem, out float massPerPerson)
        {
            ShipCargoMobilitySettings settings = ShipCargoMobilitySettingsCache.ResolveOrDefault();
            massPerGem = settings != null ? settings.massPerGem : 0.01f;
            massPerPerson = settings != null ? settings.massPerPerson : 0.15f;
        }
    }
}
