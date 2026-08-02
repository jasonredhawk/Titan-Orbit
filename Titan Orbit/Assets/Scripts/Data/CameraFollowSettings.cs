using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// [UNITY] Designer-tunable camera follow profile for the top-down gameplay camera.
    /// Create multiple assets (Assets → Create → Titan Orbit → Camera Follow Settings) and assign
    /// or swap them on CameraFollowEcs (Game assembly).
    /// <para>
    /// [TITAN-ORBIT] Starblast-style framing knobs live here — look-ahead lead while moving,
    /// and height zoom that grows with ship level. Later, ship families can each reference a
    /// different profile without changing camera code.
    /// </para>
    /// </summary>
    [CreateAssetMenu(
        fileName = "CameraFollowSettings",
        menuName = "Titan Orbit/Camera Follow Settings",
        order = 40)]
    public class CameraFollowSettings : ScriptableObject
    {
        // -------------------------------------------------------------------------
        // Height / zoom (world Y above the ship — higher = more of the map visible)
        // -------------------------------------------------------------------------

        [Header("Height / Zoom")]
        [Tooltip("Camera world-Y height above the ship at ship level 1. Higher = zoomed further out.")]
        [Min(1f)]
        public float heightAtLevel1 = 25f;

        [Tooltip("Extra world-Y height added for each ship level above 1 (L2 = base+1×, L3 = base+2×, …).")]
        [Min(0f)]
        public float heightPerLevel = 3f;

        [Tooltip("SmoothDamp time (seconds) when easing toward the target height after a level-up. Larger = slower zoom.")]
        [Min(0.01f)]
        public float heightSmoothTime = 0.4f;

        // -------------------------------------------------------------------------
        // Look-ahead (Starblast-style: nudge the framing slightly ahead of travel)
        // -------------------------------------------------------------------------

        [Header("Look-Ahead (Starblast-style)")]
        [Tooltip("Max horizontal world units the framing shifts ahead of the ship at full reference speed.")]
        [Min(0f)]
        public float lookAheadDistance = 6f;

        [Tooltip("Ship planar speed (units/sec) at which look-ahead reaches lookAheadDistance. Below this, shift scales down.")]
        [Min(0.01f)]
        public float lookAheadReferenceSpeed = 14f;

        [Tooltip("Planar speed below which look-ahead returns to zero (centered on the ship).")]
        [Min(0f)]
        public float lookAheadMinSpeed = 0.75f;

        [Tooltip("SmoothDamp time (seconds) for the look-ahead offset. Larger = softer / slower lead.")]
        [Min(0.01f)]
        public float lookAheadSmoothTime = 0.55f;

        // -------------------------------------------------------------------------
        // Lens
        // -------------------------------------------------------------------------

        [Header("Lens")]
        [Tooltip("Perspective field of view (degrees) for the gameplay camera.")]
        [Range(10f, 120f)]
        public float gameplayFieldOfView = 45f;

        /// <summary>
        /// World-Y height for a given ship level. Level is clamped to at least 1.
        /// </summary>
        /// <param name="shipLevel">Current local ship level from ShipState.</param>
        /// <returns>Target camera height in world units.</returns>
        public float ComputeTargetHeight(int shipLevel)
        {
            // levelsAbove1: L1 → 0, L2 → 1, L6 → 5, …
            int levelsAbove1 = Mathf.Max(0, shipLevel - 1);
            return heightAtLevel1 + heightPerLevel * levelsAbove1;
        }

        /// <summary>
        /// Maps planar velocity to a desired look-ahead offset on XZ.
        /// Zero below <see cref="lookAheadMinSpeed"/>; scales up to <see cref="lookAheadDistance"/>
        /// as speed approaches <see cref="lookAheadReferenceSpeed"/>.
        /// </summary>
        /// <param name="planarVelocity">Ship velocity with Y forced to 0.</param>
        /// <returns>Desired world-space look-ahead (Y always 0).</returns>
        public Vector3 ComputeDesiredLookAhead(Vector3 planarVelocity)
        {
            float speed = planarVelocity.magnitude;
            if (speed < lookAheadMinSpeed || lookAheadDistance <= 0f)
                return Vector3.zero;

            // Normalize direction of travel; scale by how close we are to the reference speed.
            float refSpeed = Mathf.Max(0.01f, lookAheadReferenceSpeed);
            float t = Mathf.Clamp01(speed / refSpeed);
            Vector3 dir = planarVelocity / speed;
            Vector3 offset = dir * (lookAheadDistance * t);
            offset.y = 0f;
            return offset;
        }

        /// <summary>
        /// Keeps times and distances sane after Inspector edits.
        /// </summary>
        public void ClampValues()
        {
            heightAtLevel1 = Mathf.Max(1f, heightAtLevel1);
            heightPerLevel = Mathf.Max(0f, heightPerLevel);
            heightSmoothTime = Mathf.Max(0.01f, heightSmoothTime);

            lookAheadDistance = Mathf.Max(0f, lookAheadDistance);
            lookAheadReferenceSpeed = Mathf.Max(0.01f, lookAheadReferenceSpeed);
            lookAheadMinSpeed = Mathf.Max(0f, lookAheadMinSpeed);
            lookAheadSmoothTime = Mathf.Max(0.01f, lookAheadSmoothTime);

            gameplayFieldOfView = Mathf.Clamp(gameplayFieldOfView, 10f, 120f);
        }

        /// <summary>[UNITY] Inspector edit → clamp so SmoothDamp times stay positive.</summary>
        void OnValidate() => ClampValues();
    }
}
