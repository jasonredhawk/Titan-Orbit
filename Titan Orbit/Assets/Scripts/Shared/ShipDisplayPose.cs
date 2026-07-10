using UnityEngine;

namespace TitanOrbit.Shared
{
    /// <summary>
    /// Interpolated local-player display pose, published from NetCode presentation each frame.
    /// Camera, background, and UI should read this instead of raw ECS or smoothed proxy copies.
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
