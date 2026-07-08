using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Published each frame by <see cref="EcsWorldVisualizer"/> — single display pose for camera, toroidal ref, etc.
    /// </summary>
    public static class ShipDisplayPose
    {
        public static bool HasLocalPose { get; private set; }
        public static Vector3 LocalPosition { get; private set; }
        public static Quaternion LocalRotation { get; private set; }

        internal static void SetLocalPose(Vector3 position, Quaternion rotation)
        {
            LocalPosition = position;
            LocalRotation = rotation;
            HasLocalPose = true;
        }

        internal static void ClearLocalPose() => HasLocalPose = false;
    }
}
