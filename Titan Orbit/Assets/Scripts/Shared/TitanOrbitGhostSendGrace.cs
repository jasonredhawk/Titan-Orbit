namespace TitanOrbit
{
    /// <summary>
    /// Cross-assembly latch so server ship spawn can arm elevated GhostSend without ECS→NetCode
    /// references. <c>TeamManagementSystem</c> (ECS) calls <see cref="ArmShipSpawnGrace"/>;
    /// <c>TitanOrbitGhostSendTuneSystem</c> (NetCode) consumes remaining frames each tick.
    /// <para>
    /// [TITAN-ORBIT] Debug 1af271: CommandTarget alone ending elevated send left clients at
    /// Instantiates=map-meta with no hull. Explicit spawn arm keeps MaxSendChunks / first-send
    /// bias high until the ship snapshot has time to leave.
    /// </para>
    /// </summary>
    public static class TitanOrbitGhostSendGrace
    {
        /// <summary>Default frames of elevated send after TeamChoice ship Instantiates.</summary>
        public const int DefaultShipSpawnGraceFrames = 180;

        /// <summary>Remaining elevated-send frames (decremented by GhostSend tune).</summary>
        static int s_RemainingFrames;

        /// <summary>True when any grace frames remain.</summary>
        public static bool IsActive => s_RemainingFrames > 0;

        /// <summary>Current remaining frame count (read-only).</summary>
        public static int RemainingFrames => s_RemainingFrames;

        /// <summary>
        /// Arms elevated GhostSend for <paramref name="frames"/> (takes the max with current remaining).
        /// Call from server ship spawn immediately after Instantiates + CommandTarget assign.
        /// </summary>
        /// <param name="frames">Frames of elevated send; use <see cref="DefaultShipSpawnGraceFrames"/>.</param>
        public static void ArmShipSpawnGrace(int frames = DefaultShipSpawnGraceFrames)
        {
            if (frames < 0)
                frames = 0;
            if (frames > s_RemainingFrames)
                s_RemainingFrames = frames;
        }

        /// <summary>
        /// Called once per server tick by GhostSend tune: returns whether elevated send should stay
        /// on from this latch, then decrements by one.
        /// </summary>
        public static bool ConsumeTick()
        {
            if (s_RemainingFrames <= 0)
                return false;

            s_RemainingFrames--;
            return true;
        }

        /// <summary>Clears grace (session leave / play-mode reset).</summary>
        public static void Clear()
        {
            s_RemainingFrames = 0;
        }
    }
}
