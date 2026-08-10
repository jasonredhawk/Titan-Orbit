using TitanOrbit;
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
    /// <para>
    /// [TITAN-ORBIT] Debug 1af271: <c>MinDistanceScaledSendImportance=1</c> starved Local Host join —
    /// far Importance=1 tiles scaled to priority 0 while <c>GhostConnectionPosition</c> sat at origin
    /// (no ship), Instantiates froze mid-bar (~189/358). During no-ship join we raise iterate /
    /// first-send bias so MaxSendChunks=1 still walks the whole map. <see cref="TitanOrbitGhostSendGrace"/>
    /// keeps elevated send after TeamChoice Instantiates until the hull snapshot can leave.
    /// Client Instantiates stays 1/frame (Crash!!! safety).
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
        /// </summary>
        public const int MaxSendChunksPerSnapshot = 1;

        /// <summary>
        /// During post–TeamChoice grace, allow more chunks so the OwnerPredicted ship tile is not
        /// starved by nearby transport/planet resends. Client Instantiates stays 1/frame.
        /// </summary>
        public const int MaxSendChunksDuringShipGrace = 8;

        /// <summary>
        /// How many chunks GhostSend may scan while filling the packet (steady-state).
        /// NetCode recommends ≥ 2× <see cref="MaxSendChunksPerSnapshot"/>.
        /// </summary>
        public const int MaxIterateChunksPerSnapshot = 4;

        /// <summary>
        /// Scan more tiles while loading or during ship-send grace.
        /// Keeps MaxSendChunks low (Crash!!! safety) but finds never-sent chunks.
        /// </summary>
        public const int MaxIterateChunksDuringJoin = 16;

        /// <summary>
        /// After distance scaling, skip chunks whose priority is below this.
        /// NetCode default is 0 (off). Value 1 starved join when map Importance=1 tiles scaled to 0.
        /// </summary>
        public const int MinDistanceScaledSendImportance = 0;

        /// <summary>
        /// Bias never-sent chunks during join / ship-grace so MaxSendChunks=1 walks the map
        /// and the new ship ghost wins the first packet after TeamChoice.
        /// </summary>
        public const uint FirstSendImportanceMultiplierJoin = 100;

        /// <summary>After grace, milder first-send bias for steady-state combat.</summary>
        public const uint FirstSendImportanceMultiplierInGame = 10;

        /// <summary>
        /// Frames of elevated send after every in-game connection has a CommandTarget ship
        /// (and/or after <see cref="TitanOrbitGhostSendGrace.ArmShipSpawnGrace"/>).
        /// </summary>
        public const int PostCommandTargetSendGraceFrames =
            TitanOrbitGhostSendGrace.DefaultShipSpawnGraceFrames;

        /// <summary>True after the one-time diagnostic log.</summary>
        bool _loggedOnce;

        /// <summary>Remaining grace frames from CommandTarget becoming non-null (local countdown).</summary>
        int _shipSendGraceRemaining;

        /// <summary>
        /// Requires <see cref="GhostSendSystemData"/> — created by NetCode's GhostSend bootstrap.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            // --- Dependencies ---
            // [NETCODE] GhostSendSystemData singleton owns snapshot packing limits.
            state.RequireForUpdate<GhostSendSystemData>();
            _shipSendGraceRemaining = 0;
            TitanOrbitGhostSendGrace.Clear();
        }

        /// <summary>
        /// Re-applies chunk send caps every frame so package defaults cannot restore an unbounded flood.
        /// Widens iterate/first-send while any connection still lacks a ship, during CommandTarget
        /// grace, and while <see cref="TitanOrbitGhostSendGrace"/> is armed from ship spawn.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Detect connections that still need map / ship first-send ---
            // [NETCODE] GhostConnectionPosition alone is not enough — it is added at (0,0,0)
            // before CommandTarget points at a hull.
            int inGameConnections = 0;
            int nullCommandTargets = 0;
            foreach (var cmd in SystemAPI.Query<RefRO<CommandTarget>>().WithAll<NetworkStreamInGame>())
            {
                inGameConnections++;
                if (cmd.ValueRO.targetEntity == Entity.Null)
                    nullCommandTargets++;
            }

            foreach (var _ in SystemAPI.Query<RefRO<NetworkStreamInGame>>()
                         .WithAll<NetworkId>()
                         .WithNone<CommandTarget>())
            {
                inGameConnections++;
                nullCommandTargets++;
            }

            // --- Grace after all CommandTargets become non-null ---
            // [TITAN-ORBIT] Server sets CommandTarget the same tick it Instantiates the ship.
            // Ending elevated send that frame left clients at Instantiates=meta-N with no hull.
            bool needsFullMapStream = nullCommandTargets > 0 || inGameConnections == 0;
            if (needsFullMapStream)
            {
                _shipSendGraceRemaining = PostCommandTargetSendGraceFrames;
            }
            else if (_shipSendGraceRemaining > 0)
            {
                _shipSendGraceRemaining--;
            }

            // --- Explicit spawn arm (TeamManagementSystem) — independent of CommandTarget race ---
            bool spawnGraceActive = TitanOrbitGhostSendGrace.ConsumeTick();

            bool elevatedSend = needsFullMapStream ||
                                _shipSendGraceRemaining > 0 ||
                                spawnGraceActive;

            // --- Cap snapshot packing ---
            ref var sendData = ref SystemAPI.GetSingletonRW<GhostSendSystemData>().ValueRW;
            sendData.MaxSendChunks = elevatedSend
                ? MaxSendChunksDuringShipGrace
                : MaxSendChunksPerSnapshot;
            sendData.MaxIterateChunks = elevatedSend
                ? MaxIterateChunksDuringJoin
                : MaxIterateChunksPerSnapshot;
            sendData.MinDistanceScaledSendImportance = MinDistanceScaledSendImportance;
            sendData.FirstSendImportanceMultiplier = elevatedSend
                ? FirstSendImportanceMultiplierJoin
                : FirstSendImportanceMultiplierInGame;

            // --- One-time log ---
            if (_loggedOnce)
                return;

            _loggedOnce = true;
            UnityEngine.Debug.Log(
                "[TitanOrbitGhostSend] Snapshot chunk caps applied: MaxSendChunks(play/grace)=" +
                MaxSendChunksPerSnapshot + "/" + MaxSendChunksDuringShipGrace +
                ", MaxIterateChunks(play/join)=" + MaxIterateChunksPerSnapshot + "/" +
                MaxIterateChunksDuringJoin +
                ", MinDistanceScaledSendImportance=0" +
                ", FirstSendImportanceMultiplier(play/join)=" + FirstSendImportanceMultiplierInGame +
                "/" + FirstSendImportanceMultiplierJoin +
                ", shipGraceFrames=" + PostCommandTargetSendGraceFrames +
                " (join + post-TeamChoice ship snapshot; Instantiates stays 1/frame on client).");
        }
    }
}
