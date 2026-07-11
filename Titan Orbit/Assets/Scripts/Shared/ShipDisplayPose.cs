using UnityEngine;

namespace TitanOrbit.Shared
{
    /// <summary>
    /// Static holder for the local player's ship pose after presentation sync. Written each frame
    /// by <see cref="Game.EcsWorldVisualizer"/> from <see cref="Game.GhostPresentationTransformCache"/>
    /// (post-NetCode interpolation). Camera (<see cref="Game.CameraFollowEcs"/>) and parallax
    /// background read this — not raw sim ECS or double-smoothed proxy copies. Client only.
    /// </summary>
    public static class ShipDisplayPose
    {
        /// <summary>True after SetLocalPose ran at least once this session with a valid ship.</summary>
        public static bool HasLocalPose { get; private set; }

        /// <summary>World position of the local ship visual proxy (presentation phase).</summary>
        public static Vector3 LocalPosition { get; private set; }

        /// <summary>World rotation of the local ship visual proxy (presentation phase).</summary>
        public static Quaternion LocalRotation { get; private set; }

        /// <summary>
        /// Called from EcsWorldVisualizer when the local ship GameObject proxy is synced.
        /// </summary>
        public static void SetLocalPose(Vector3 position, Quaternion rotation)
        {
            // --- Cache presentation pose for camera / parallax ---
            // [HYBRID] Caller (EcsWorldVisualizer) already read GhostPresentationTransformCache.
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
