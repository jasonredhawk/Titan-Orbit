using UnityEngine;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Ramming vs asteroids: restitution (bounce) scales with ramming power — high power dissipates normal motion into the rock;
    /// low power reflects more energy back. Continuous push force into the surface can chip the asteroid (grind).
    /// </summary>
    public static class AsteroidRammingBehavior
    {
        /// <summary>
        /// Maps ramming power to a coefficient of restitution between <paramref name="maxRestitution"/> (bouncy)
        /// and <paramref name="minRestitution"/> (inelastic). Only ramming above <paramref name="restitutionRammingThreshold"/>
        /// reduces bounce; that excess is blended with <paramref name="referenceExcessPower"/> (halfway blend when excess equals this).
        /// </summary>
        public static float ComputeRestitution(
            float maxRestitution,
            float minRestitution,
            float rammingPower,
            float restitutionRammingThreshold,
            float referenceExcessPower)
        {
            maxRestitution = Mathf.Clamp01(maxRestitution);
            minRestitution = Mathf.Clamp01(minRestitution);
            if (minRestitution > maxRestitution)
                (minRestitution, maxRestitution) = (maxRestitution, minRestitution);

            float excess = Mathf.Max(0f, rammingPower - Mathf.Max(0f, restitutionRammingThreshold));
            float refP = Mathf.Max(1e-4f, referenceExcessPower);
            float t = Mathf.Clamp01(excess / (excess + refP));
            return Mathf.Lerp(maxRestitution, minRestitution, t);
        }

        /// <summary>
        /// Scalar push into the surface (Newtons): outward asteroid normal <paramref name="surfaceOutwardNormalXZ"/>, drive force in XZ.
        /// </summary>
        public static float ComputeNormalPushNewtons(Vector3 surfaceOutwardNormalXZ, Vector3 driveForceXZ)
        {
            if (surfaceOutwardNormalXZ.sqrMagnitude < 1e-8f) return 0f;
            return Mathf.Max(0f, -Vector3.Dot(driveForceXZ, surfaceOutwardNormalXZ));
        }
    }
}
