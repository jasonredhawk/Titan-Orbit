using UnityEngine;

namespace TitanOrbit.Shared
{
    /// <summary>
    /// Client-only mirror for planetary defense turret possession UI / camera / aim.
    /// Updated each frame by <see cref="TitanOrbit.UI.PlanetaryDefenseTurretControlUi"/> from
    /// ghosted <c>ShipTurretControlState</c> + pad eligibility. Not networked.
    /// <para>
    /// [TITAN-ORBIT] While controlling, <see cref="DesiredViewRadiusWorld"/> drives
    /// <c>CameraFollowEcs</c> height so the pad's bullet engage range fits on screen.
    /// </para>
    /// </summary>
    public static class PlanetaryDefenseTurretClientState
    {
        /// <summary>
        /// [UNITY] Domain Reload off leaves statics hot across Play Mode — clear so camera / aim
        /// do not stay locked to a pad from the previous session.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ResetStaticsBeforeSceneLoad() => Clear();

        /// <summary>True when the local ship is currently piloting a defense pad.</summary>
        public static bool IsControlling { get; private set; }

        /// <summary>World position of the occupied (or eligible) pad for camera / aim.</summary>
        public static Vector3 PadWorldPosition { get; private set; }

        /// <summary>True when <see cref="PadWorldPosition"/> is valid this frame.</summary>
        public static bool HasPadWorldPosition { get; private set; }

        /// <summary>
        /// World-space radius the gameplay camera should cover while controlling
        /// (turret engage / bullet range + margin). 0 when not controlling or unknown.
        /// </summary>
        public static float DesiredViewRadiusWorld { get; private set; }

        /// <summary>Eligible Take Control target planet id (0 when none).</summary>
        public static int EligiblePlanetId { get; private set; }

        /// <summary>Eligible Take Control slot index.</summary>
        public static byte EligibleSlotIndex { get; private set; }

        /// <summary>
        /// True when Take Control may be shown (in zone, built, free).
        /// Stillness is <b>not</b> required for enter — only for server gem deposit.
        /// </summary>
        public static bool CanTakeControl { get; private set; }

        /// <summary>Reserved (deposit still-time lives on the server). Kept for API stability.</summary>
        public static float StillSecondsInZone { get; private set; }

        /// <summary>
        /// Writes controlling + pad pose + camera view radius for possession framing.
        /// </summary>
        /// <param name="controlling">True while piloting a pad.</param>
        /// <param name="padWorldPos">World pose of the occupied pad.</param>
        /// <param name="hasPad">True when <paramref name="padWorldPos"/> is valid.</param>
        /// <param name="desiredViewRadiusWorld">
        /// Engage/bullet range (+ margin) the camera should fit; 0 clears the override.
        /// </param>
        public static void SetControlling(
            bool controlling,
            Vector3 padWorldPos,
            bool hasPad,
            float desiredViewRadiusWorld)
        {
            IsControlling = controlling;
            PadWorldPosition = padWorldPos;
            HasPadWorldPosition = hasPad;
            DesiredViewRadiusWorld = controlling
                ? Mathf.Max(0f, desiredViewRadiusWorld)
                : 0f;
        }

        /// <summary>Writes Take Control eligibility for the HUD button.</summary>
        public static void SetEligibility(
            bool canTakeControl,
            int planetId,
            byte slotIndex,
            float stillSeconds,
            Vector3 padWorldPos,
            bool hasPad)
        {
            CanTakeControl = canTakeControl;
            EligiblePlanetId = planetId;
            EligibleSlotIndex = slotIndex;
            StillSecondsInZone = stillSeconds;
            if (!IsControlling)
            {
                PadWorldPosition = padWorldPos;
                HasPadWorldPosition = hasPad;
                DesiredViewRadiusWorld = 0f;
            }
        }

        /// <summary>Clears all client mirror state (disconnect / leave game).</summary>
        public static void Clear()
        {
            IsControlling = false;
            HasPadWorldPosition = false;
            PadWorldPosition = Vector3.zero;
            DesiredViewRadiusWorld = 0f;
            EligiblePlanetId = 0;
            EligibleSlotIndex = 0;
            CanTakeControl = false;
            StillSecondsInZone = 0f;
        }
    }
}
