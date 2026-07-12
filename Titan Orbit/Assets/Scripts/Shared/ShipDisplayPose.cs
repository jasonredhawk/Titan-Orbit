using UnityEngine;

namespace TitanOrbit.Shared
{
    /// <summary>
    /// Static holder for the local player's ship pose. Updated from ECS presentation transforms.
    /// Camera (<see cref="Game.CameraFollowEcs"/>) and parallax background may read this. Client only.
    /// </summary>
    public static class ShipDisplayPose
    {
        /// <summary>True after SetLocalPose ran at least once this session with a valid ship.</summary>
        public static bool HasLocalPose { get; private set; }

        /// <summary>World position of the local ship visual proxy (presentation phase).</summary>
        public static Vector3 LocalPosition { get; private set; }

        /// <summary>World rotation of the local ship visual proxy (presentation phase).</summary>
        public static Quaternion LocalRotation { get; private set; }

        /// <summary>Caches presentation pose for camera / parallax readers.</summary>
        public static void SetLocalPose(Vector3 position, Quaternion rotation)
        {
            LocalPosition = position;
            LocalRotation = rotation;
            HasLocalPose = true;
        }

        /// <summary>Clears pose when local ship despawns or player disconnects.</summary>
        public static void ClearLocalPose()
        {
            // --- Invalidate so readers fall back to default camera rig ---
            HasLocalPose = false;
        }
    }
}
