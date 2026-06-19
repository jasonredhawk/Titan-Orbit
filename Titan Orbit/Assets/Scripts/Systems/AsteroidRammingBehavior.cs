using UnityEngine;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Ramming vs asteroids: restitution (bounce) scales down with ramming power and effective hull mass — heavy ships stick
    /// and slide; light ships rebound. Suppressed bounce energy is applied as impact damage instead of velocity reversal.
    /// Continuous push force into the surface can chip the asteroid (grind).
    /// </summary>
    public static class AsteroidRammingBehavior
    {
        /// <summary>
        /// Maps a stat (ramming power, mass ratio, etc.) to a coefficient of restitution between
        /// <paramref name="maxRestitution"/> (bouncy) and <paramref name="minRestitution"/> (inelastic).
        /// Only values above <paramref name="restitutionThreshold"/> reduce bounce; that excess is blended with
        /// <paramref name="referenceExcess"/> (halfway blend when excess equals this).
        /// </summary>
        public static float ComputeRestitution(
            float maxRestitution,
            float minRestitution,
            float value,
            float restitutionThreshold,
            float referenceExcess)
        {
            maxRestitution = Mathf.Clamp01(maxRestitution);
            minRestitution = Mathf.Clamp01(minRestitution);
            if (minRestitution > maxRestitution)
                (minRestitution, maxRestitution) = (maxRestitution, minRestitution);

            float excess = Mathf.Max(0f, value - Mathf.Max(0f, restitutionThreshold));
            float refP = Mathf.Max(1e-4f, referenceExcess);
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
