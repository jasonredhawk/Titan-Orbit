using UnityEngine;

namespace TitanOrbit.Shared
{
    /// <summary>
    /// Presentation-phase local-player pose published from EcsWorldVisualizer after reading
    /// GhostPresentationTransformCache (post-NetCode interpolation). Camera and parallax
    /// background read this — not raw sim ECS or double-smoothed proxy copies (see ship-simulation rule).
    /// </summary>
    public static class ShipDisplayPose
    {
        public static bool HasLocalPose { get; private set; }
        public static Vector3 LocalPosition { get; private set; }
        public static Quaternion LocalRotation { get; private set; }

        /// <summary>Called from EcsWorldVisualizer when the local ship proxy is synced.</summary>
        public static void SetLocalPose(Vector3 position, Quaternion rotation)
        {
            LocalPosition = position;
            LocalRotation = rotation;
            HasLocalPose = true;
        }

        public static void ClearLocalPose() => HasLocalPose = false;
    }
}
