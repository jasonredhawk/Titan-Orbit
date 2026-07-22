using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Client singleton that tracks late-join Instantiates settle.
    /// While <see cref="Settling"/> is non-zero, hybrid/UI code must skip full map-body
    /// <c>ToEntityArray</c> scans. GameObject Instantiates stay rate-limited via Pending drain.
    /// <para>
    /// Player.log proved <see cref="Unity.Transforms.TransformSystemGroup"/> RE-ENABLED after
    /// Instantiates hundreds of asteroids → immediate Burst <c>Crash!!!</c>. So while in-game the
    /// transform group stays off (<see cref="ClientJoinSettleCache.TransformQuarantine"/>) and
    /// ships render as hybrid GameObject proxies instead of Entities Graphics.
    /// </para>
    /// Written by <see cref="TitanOrbitClientJoinTransformGateSystem"/>.
    /// </summary>
    public struct ClientJoinSettleState : IComponentData
    {
        /// <summary>1 while join Instantiates / GhostSpawn backlog is still draining.</summary>
        public byte Settling;

        /// <summary>Consecutive frames with empty GhostSpawnBuffer and zero PendingSpawnPlaceholder.</summary>
        public int IdleClearFrames;

        /// <summary>Frames since NetworkStreamInGame became true this session.</summary>
        public int InGameFrames;

        /// <summary>1 after we observed any spawn-buffer or placeholder activity this join.</summary>
        public byte SawSpawnActivity;

        /// <summary>
        /// 1 after Settling has exited once this session. Prevents re-entering Settling for
        /// post-team ship Instantiates (Player.log Crash!!! after TeamChoice).
        /// </summary>
        public byte JoinSettleCompleted;
    }

    /// <summary>
    /// [HYBRID] Managed mirror of settle / transform quarantine for MonoBehaviours.
    /// </summary>
    public static class ClientJoinSettleCache
    {
        /// <summary>True while GhostSpawn Instantiates backlog is active.</summary>
        public static bool Settling { get; private set; }

        /// <summary>
        /// True while in-game with TransformSystemGroup forced off (Windows late-join safety).
        /// When true, ships must use hybrid GO proxies — Entities Graphics needs Parent/LTW.
        /// </summary>
        public static bool TransformQuarantine { get; private set; }

        /// <summary>Frames in-game this session (diagnostic).</summary>
        public static int InGameFrames { get; private set; }

        /// <summary>
        /// True after the initial join Instantiates settle finished — ship Instantiates after
        /// Join Team must not flip Settling back on.
        /// </summary>
        public static bool JoinSettleCompleted { get; private set; }

        /// <summary>
        /// True while GhostSpawnBuffer or PendingSpawnPlaceholder is non-empty — including the
        /// brief ship Instantiates window after Join Team when Settling stays OFF.
        /// Ship WithEntityAccess / EnsureShipProxies must skip while this is true
        /// (Player.log 2026-07-19 TeamChoiceResult → Crash!!!).
        /// </summary>
        public static bool GhostSpawnBacklog { get; private set; }

        /// <summary>
        /// [TITAN-ORBIT] True when ship <c>ToEntityArray</c> / <c>WithEntityAccess</c> must not run.
        /// Covers Settling and the post–Join Team Instantiates window (Settling stays OFF).
        /// </summary>
        public static bool ShouldSkipShipEntityQueries => Settling || GhostSpawnBacklog;

        /// <summary>
        /// Extra frames to keep <see cref="GhostSpawnBacklog"/> true after a successful Instantiates
        /// even when GhostSpawnBuffer / PendingSpawnPlaceholder are already empty.
        /// TeamChoice ship Instantiates clears the placeholder the same frame — without this hold,
        /// ship systems fail-open immediately (Player.log 2026-07-22 TeamChoiceResult → Crash!!!).
        /// </summary>
        const int PostInstantiateHoldFrames = 5;

        /// <summary>Last <see cref="TitanOrbitJoinLoadCounters.InstantiatesSession"/> we observed.</summary>
        static int s_LastSeenInstantiatesSession;

        /// <summary>Remaining frames of Instantiates hold (counts down once per Unity frame).</summary>
        static int s_PostInstantiateHoldRemaining;

        /// <summary><see cref="UnityEngine.Time.frameCount"/> of the last hold tick (dedupe dual callers).</summary>
        static int s_PostInstantiateHoldTickFrame = -1;

        /// <summary>Updates settle + quarantine flags from the join gate system.</summary>
        public static void Set(
            bool settling,
            bool transformQuarantine,
            int inGameFrames,
            bool joinSettleCompleted,
            bool ghostSpawnBacklog)
        {
            Settling = settling;
            TransformQuarantine = transformQuarantine;
            InGameFrames = inGameFrames;
            JoinSettleCompleted = joinSettleCompleted;
            // [TITAN-ORBIT] Always fold Instantiates hold into the published backlog bit.
            GhostSpawnBacklog = ComputeGhostSpawnBacklog(ghostSpawnBacklog);
        }

        /// <summary>
        /// Refreshes only <see cref="GhostSpawnBacklog"/> after GhostSpawn runs mid-frame.
        /// The join gate publishes backlog in InitializationSystemGroup — before GhostSpawn —
        /// so MonoBehaviours in LateUpdate would otherwise see a stale false on the arrival frame.
        /// </summary>
        public static void SetGhostSpawnBacklog(bool ghostSpawnBacklog)
        {
            GhostSpawnBacklog = ComputeGhostSpawnBacklog(ghostSpawnBacklog);
        }

        /// <summary>
        /// Queue/placeholder non-empty <b>or</b> recent Instantiates hold.
        /// Call from the join gate and from <c>TitanOrbitGhostSpawnBacklogRefreshSystem</c>.
        /// Hold decrements at most once per Unity frame even when both callers run.
        /// </summary>
        /// <param name="queueOrPlaceholdersNonEmpty">True while GhostSpawn still has work queued.</param>
        /// <returns>Effective backlog flag for ship / Instantiates-sensitive presentation.</returns>
        public static bool ComputeGhostSpawnBacklog(bool queueOrPlaceholdersNonEmpty)
        {
            // --- Detect Instantiates that cleared placeholders this frame ---
            // [NETCODE] TitanOrbitJoinLoadCounters.InstantiatesSession increments inside GhostSpawn
            // after each successful delayed Instantiates (1/frame). TeamChoice ship arrival bumps it
            // while the placeholder query is already empty — queue-only backlog would flip false.
            int session = TitanOrbitJoinLoadCounters.InstantiatesSession;
            if (session > s_LastSeenInstantiatesSession)
            {
                s_LastSeenInstantiatesSession = session;
                s_PostInstantiateHoldRemaining = PostInstantiateHoldFrames;
            }

            // --- One hold tick per rendered frame ---
            int frame = UnityEngine.Time.frameCount;
            if (s_PostInstantiateHoldTickFrame != frame)
            {
                s_PostInstantiateHoldTickFrame = frame;
                if (s_PostInstantiateHoldRemaining > 0)
                    s_PostInstantiateHoldRemaining--;
            }

            return queueOrPlaceholdersNonEmpty || s_PostInstantiateHoldRemaining > 0;
        }

        /// <summary>Clears when leaving a session / not in-game.</summary>
        public static void Clear()
        {
            Settling = false;
            TransformQuarantine = false;
            InGameFrames = 0;
            JoinSettleCompleted = false;
            GhostSpawnBacklog = false;
            s_LastSeenInstantiatesSession = 0;
            s_PostInstantiateHoldRemaining = 0;
            s_PostInstantiateHoldTickFrame = -1;
            // [NETCODE] GhostSpawn join counters — next Relay join starts from zero.
            TitanOrbitJoinLoadCounters.Reset();
        }
    }
}
