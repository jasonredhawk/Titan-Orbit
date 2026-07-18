using Unity.Entities;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Client singleton that tracks late-join Instantiates settle.
    /// While <see cref="Settling"/> is non-zero, hybrid/UI code must skip full map-body
    /// <c>ToEntityArray</c> scans (minimap, MarkFromQuery, DrawAsteroids). GameObject Instantiates
    /// stay rate-limited via the Pending drain.
    /// <para>
    /// Do NOT disable <c>TransformSystemGroup</c> from this flag — re-enabling after Instantiates
    /// hundreds of asteroids hard-crashes Burst LocalToWorld on Windows (Player.log 2026-07-18).
    /// Instantiates safety is GhostSpawn Instantiates=1/frame with transforms always on.
    /// </para>
    /// <para>
    /// Written by <see cref="TitanOrbitClientJoinTransformGateSystem"/>.
    /// Read by moon collider ensure and <c>EcsWorldVisualizer</c> (via <see cref="ClientJoinSettleCache"/>).
    /// </para>
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
    }

    /// <summary>
    /// [HYBRID] Managed mirror of <see cref="ClientJoinSettleState.Settling"/> for MonoBehaviours
    /// that should not query ECS every draw path. Updated each frame by the settle system.
    /// </summary>
    public static class ClientJoinSettleCache
    {
        /// <summary>True while GhostSpawn Instantiates backlog is active — gate hybrid/UI map scans.</summary>
        public static bool Settling { get; private set; }

        /// <summary>Frames in-game this settle session (diagnostic).</summary>
        public static int InGameFrames { get; private set; }

        /// <summary>
        /// Called by <see cref="TitanOrbitClientJoinTransformGateSystem"/> after updating the singleton.
        /// </summary>
        /// <param name="settling">Whether join settle is active.</param>
        /// <param name="inGameFrames">In-game frame counter.</param>
        public static void Set(bool settling, int inGameFrames)
        {
            Settling = settling;
            InGameFrames = inGameFrames;
        }

        /// <summary>Clears the cache when leaving a session / not in-game.</summary>
        public static void Clear()
        {
            Settling = false;
            InGameFrames = 0;
        }
    }
}
