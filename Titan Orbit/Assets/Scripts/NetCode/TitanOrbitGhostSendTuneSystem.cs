using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Caps how many ghost chunks the dedicated / host server packs into each snapshot per connection.
    /// <para>
    /// Without this, a Relay late-join can deliver hundreds of asteroid/planet ghosts in one client
    /// frame. Paired with <see cref="TitanOrbitGhostDistanceImportanceBootstrapSystem"/> (spatial
    /// tiles) and map-ghost <c>MaxSendRate</c> on prefabs so join streams instead of Instantiates floods.
    /// </para>
    /// World: ServerSimulation. Group: InitializationSystemGroup (after tick-rate setup).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(TitanOrbitServerTickRateSystem))]
    public partial struct TitanOrbitGhostSendTuneSystem : ISystem
    {
        /// <summary>
        /// Max chunks written into one snapshot for one connection.
        /// Asteroids share archetypes so one chunk can still hold many entities — keep this at 1
        /// so late-join map floods create placeholders gradually instead of hundreds per tick.
        /// Paired with <see cref="TitanOrbitGhostDistanceImportanceBootstrapSystem"/> which
        /// fragments dense fields into spatial tiles (smaller chunks).
        /// </summary>
        public const int MaxSendChunksPerSnapshot = 1;

        /// <summary>
        /// How many chunks GhostSend may scan while filling the packet.
        /// NetCode recommends ≥ 2× <see cref="MaxSendChunksPerSnapshot"/>.
        /// </summary>
        public const int MaxIterateChunksPerSnapshot = 4;

        /// <summary>
        /// After distance scaling, skip chunks whose priority is below this.
        /// Helps far static map tiles wait while the player's ship / nearby tiles fill the packet.
        /// 0 = off. Tuned low so join still progresses; raise if join Instantiates stay too bursty.
        /// </summary>
        public const int MinDistanceScaledSendImportance = 1;

        /// <summary>True after the one-time diagnostic log.</summary>
        bool _loggedOnce;

        /// <summary>
        /// Requires <see cref="GhostSendSystemData"/> — created by NetCode's GhostSend bootstrap.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            // --- Dependencies ---
            // [NETCODE] GhostSendSystemData singleton owns snapshot packing limits.
            state.RequireForUpdate<GhostSendSystemData>();
        }

        /// <summary>
        /// Re-applies chunk send caps every frame so package defaults cannot restore an unbounded flood.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Cap snapshot packing ---
            // [NETCODE] MaxSendChunks — hard cap on chunks added per connection per network tick.
            // [TITAN-ORBIT] Spreads map-ghost Instantiates on the client across many frames.
            ref var sendData = ref SystemAPI.GetSingletonRW<GhostSendSystemData>().ValueRW;
            sendData.MaxSendChunks = MaxSendChunksPerSnapshot;
            sendData.MaxIterateChunks = MaxIterateChunksPerSnapshot;
            sendData.MinDistanceScaledSendImportance = MinDistanceScaledSendImportance;

            // --- One-time log ---
            if (_loggedOnce)
                return;

            _loggedOnce = true;
            UnityEngine.Debug.Log(
                "[TitanOrbitGhostSend] Snapshot chunk caps applied: MaxSendChunks=" +
                MaxSendChunksPerSnapshot + ", MaxIterateChunks=" + MaxIterateChunksPerSnapshot +
                ", MinDistanceScaledSendImportance=" + MinDistanceScaledSendImportance +
                " (spreads map ghost spawn on join).");
        }
    }
}
