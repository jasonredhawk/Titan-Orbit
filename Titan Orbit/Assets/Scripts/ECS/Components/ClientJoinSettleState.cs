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
            GhostSpawnBacklog = ghostSpawnBacklog;
        }

        /// <summary>Clears when leaving a session / not in-game.</summary>
        public static void Clear()
        {
            Settling = false;
            TransformQuarantine = false;
            InGameFrames = 0;
            JoinSettleCompleted = false;
            GhostSpawnBacklog = false;
            // [NETCODE] GhostSpawn join counters — next Relay join starts from zero.
            TitanOrbitJoinLoadCounters.Reset();
        }
    }
}
