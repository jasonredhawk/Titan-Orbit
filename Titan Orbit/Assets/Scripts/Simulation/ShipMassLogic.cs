using UnityEngine;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Movement and ramming mass rules ported from legacy <c>Starship</c>.
    /// Hull component mass and gem cargo increase mass; HP bulk is softened for movement via <see cref="MovementHullBulkExponent"/>.
    /// </summary>
    public static class ShipMassLogic
    {
        public const float DefaultBaseMass = 1f;
        public const float HullMassScale = 0.7f;
        public const float MassPerGem = 0.01f;
        public const float MovementHullBulkExponent = 0.4f;
        public const float RammingGemMassScale = 2.5f;
        public const float DefaultBrakeDeceleration = 7f;
        public const float MinMass = 0.5f;

        public static float GetMovementBulkScale(float maxHealth, float chassisReferenceHealth)
        {
            float bulk = maxHealth / Mathf.Max(1f, chassisReferenceHealth);
            if (bulk <= 1f || MovementHullBulkExponent >= 0.999f)
                return bulk;
            if (MovementHullBulkExponent <= 0.001f)
                return 1f;
            return Mathf.Pow(bulk, MovementHullBulkExponent);
        }

        public static float GetRammingBulkScale(float maxHealth, float chassisReferenceHealth) =>
            maxHealth / Mathf.Max(1f, chassisReferenceHealth);

        public static float ComputeHullMassReference(float componentMass, float baseMass = DefaultBaseMass)
        {
            float hull = componentMass > 0f ? componentMass : baseMass;
            return Mathf.Max(MinMass, hull * HullMassScale);
        }

        /// <summary>Mass used by the motor each tick (softened hull bulk + gem cargo).</summary>
        public static float ComputeMovementMass(
            float hullMassReference,
            float maxHealth,
            float chassisReferenceHealth,
            float currentGems,
            float baseMass = DefaultBaseMass)
        {
            float hullRef = hullMassReference > 0f
                ? hullMassReference
                : ComputeHullMassReference(0f, baseMass);
            float bulkScale = GetMovementBulkScale(maxHealth, chassisReferenceHealth);
            return Mathf.Max(MinMass, hullRef * bulkScale + currentGems * MassPerGem);
        }

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

        public static float ComputeRammingMass(
            float hullMassReference,
            float maxHealth,
            float chassisReferenceHealth,
            float currentGems,
            float baseMass = DefaultBaseMass)
        {
            float hullMass = ComputeRammingHullMassBaseline(
                hullMassReference, maxHealth, chassisReferenceHealth, baseMass);
            float gemMass = currentGems * MassPerGem * Mathf.Max(1f, RammingGemMassScale);
            return Mathf.Max(MinMass, hullMass + gemMass);
        }
    }

}
