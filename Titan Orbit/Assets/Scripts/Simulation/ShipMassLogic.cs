using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Ramming mass helpers for HUD, combat estimates, and mass-aware collision bounce.
    /// Movement acceleration uses <see cref="ComputeMovementMass"/> synced onto Unity Physics
    /// <c>PhysicsMass</c> each fixed step before thrust runs.
    /// Bounce / energy transfer uses <see cref="ComputeRammingMass"/> (linear HP bulk) via
    /// <c>ShipCollisionImpulseLogic</c> so heavy hulls feel heavier on impact than in the motor.
    /// </summary>
    public static class ShipMassLogic
    {
        public const float DefaultBaseMass = 1f;
        /// <summary>Scales authored chassis mass into movement reference.</summary>
        public const float HullMassScale = 0.7f;
        /// <summary>Each gem adds this much movement mass.</summary>
        public const float MassPerGem = 0.01f;
        /// <summary>Exponent &lt; 1 softens HP bulk for movement (0.4 = fourth-root scaling).</summary>
        public const float MovementHullBulkExponent = 0.4f;
        /// <summary>Gems count heavier for ramming collisions than for acceleration.</summary>
        public const float RammingGemMassScale = 2.5f;
        public const float DefaultBrakeDeceleration = 7f;
        public const float MinMass = 0.5f;

        /// <summary>
        /// Softens HP-based mass increase for movement. Level-10 ships aren't 10× harder to turn.
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

        /// <summary>Chassis component mass reference before HP bulk and gems.</summary>
        public static float ComputeHullMassReference(float componentMass, float baseMass = DefaultBaseMass)
        {
            float hull = componentMass > 0f ? componentMass : baseMass;
            return Mathf.Max(MinMass, hull * HullMassScale);
        }

        /// <summary>
        /// Mass used by the motor each tick (softened hull bulk + gem cargo).
        /// Heavier mass → slower acceleration, same top speed cap.
        /// </summary>
        public static float ComputeMovementMass(
            float hullMassReference,
            float maxHealth,
            float chassisReferenceHealth,
            float currentGems,
            float baseMass = DefaultBaseMass)
        {
            float hullRef = hullMassReference > 0f
                ? hullMassReference
                : math.max(MinMass, baseMass * HullMassScale);
            float bulkScale = GetMovementBulkScale(maxHealth, chassisReferenceHealth);
            return math.max(MinMass, hullRef * bulkScale + currentGems * MassPerGem);
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

        /// <summary>Total mass for ship-vs-ship ramming (hull + weighted gems).</summary>
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
