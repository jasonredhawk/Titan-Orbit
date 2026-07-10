using UnityEngine;

namespace TitanOrbit.Shared
{
    /// <summary>
    /// Presentation-phase local-player pose published from <see cref="EcsWorldVisualizer"/>.
    /// Camera and background read this — not raw sim ECS or double-smoothed proxy copies.
    /// </summary>
    public static class ShipDisplayPose
    {
        public static bool HasLocalPose { get; private set; }
        public static Vector3 LocalPosition { get; private set; }
        public static Quaternion LocalRotation { get; private set; }

        public static void SetLocalPose(Vector3 position, Quaternion rotation)
        {
            LocalPosition = position;
            LocalRotation = rotation;
            HasLocalPose = true;
        }

        public static void ClearLocalPose() => HasLocalPose = false;
    }
}
