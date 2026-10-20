using TitanOrbit.Diagnostics;
using TitanOrbit.ECS;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client glue that sends field-note RPCs. Dedicated clients use SendRpc on the
    /// client world. A local host writes the same command straight onto the server
    /// world — the same pattern as <see cref="PlayerNameRpcClient"/>, because a
    /// client→server send can vanish during join load.
    /// <para>
    /// The screen calls this on open (list), SEND, and DELETE. It does not run every frame.
    /// </para>
    /// </summary>
    public static class BetaFeedbackRpcClient
    {
        /// <summary>
        /// Asks the server for the newest saved notes.
        /// </summary>
        /// <returns>False when this client is not in a match yet.</returns>
        public static bool RequestList()
        {
            if (!EcsGameBridge.IsNetworkInGame())
                return false;

            byte requestId = BetaFeedbackClientInbox.BeginRequest();
            return Dispatch(new RequestBetaFeedbackListCommand { RequestId = requestId });
        }

        /// <summary>
        /// Sends the stamp from when NOTES opened, plus the typed note.
        /// An empty note is allowed — the snapshot alone is still stored.
        /// </summary>
        /// <param name="stamp">Captured by <see cref="BetaFeedbackSnapshot.Capture"/>.</param>
        /// <param name="note">Raw field text. Newlines are flattened so the log stays one line per note.</param>
        /// <returns>False when this client is not in a match yet.</returns>
        public static bool Submit(in BetaFeedbackStamp stamp, string note)
        {
            if (!EcsGameBridge.IsNetworkInGame())
                return false;

            var command = new SubmitBetaFeedbackCommand
            {
                PlayerName = PlayerDisplayNameUtil.ToFixed(stamp.Player),
                ShipName = ToFixed128(string.IsNullOrEmpty(stamp.Ship) ? "No hull" : stamp.Ship),
                GunName = ToFixed64(string.IsNullOrEmpty(stamp.Gun) ? "None" : stamp.Gun),
                ShipLevel = stamp.ShipLevel,
                BranchIndex = stamp.Branch,
                Abilities = ToFixed512(string.IsNullOrEmpty(stamp.Abilities) ? "none" : stamp.Abilities),
                Note = ToFixed512(BetaFeedbackLog.CleanField(note, BetaFeedbackLog.MaxNoteLength)),
            };
            return Dispatch(command);
        }

        /// <summary>
        /// Asks the server to remove one saved note.
        /// </summary>
        /// <param name="id">Id shown on the row.</param>
        /// <returns>False when this client is not in a match yet.</returns>
        public static bool Delete(int id)
        {
            if (!EcsGameBridge.IsNetworkInGame() || id <= 0)
                return false;
            return Dispatch(new DeleteBetaFeedbackCommand { Id = id });
        }

        /// <summary>
        /// Local host first, then a normal client send. One path only — a note is not stored twice.
        /// </summary>
        static bool Dispatch<T>(T command) where T : unmanaged, IComponentData
        {
            if (TryEnqueueLocalHost(command))
                return true;
            return TrySendDedicated(command);
        }

        /// <summary>
        /// [TITAN-ORBIT] Editor / MPPM host: create the RPC on the server world with
        /// <see cref="ReceiveRpcCommandRequest"/> already set, so
        /// <see cref="BetaFeedbackServerSystem"/> sees it next tick.
        /// </summary>
        static bool TryEnqueueLocalHost<T>(T command) where T : unmanaged, IComponentData
        {
            if (!EcsGameBridge.IsLocalHost())
                return false;

            int networkId = EcsGameBridge.GetLocalNetworkId();
            if (networkId <= 0)
                return false;

            World server = EcsGameBridge.ServerWorld;
            if (server == null || !server.IsCreated)
                return false;

            EntityManager em = server.EntityManager;
            Entity connection = FindServerConnection(em, networkId);
            if (connection == Entity.Null)
                return false;

            Entity rpcEntity = em.CreateEntity();
            em.AddComponentData(rpcEntity, command);
            em.AddComponentData(rpcEntity, new ReceiveRpcCommandRequest { SourceConnection = connection });
            return true;
        }

        /// <summary>
        /// [NETCODE] Dedicated / Relay client. TargetConnection Null means "the server
        /// this client is connected to."
        /// </summary>
        static bool TrySendDedicated<T>(T command) where T : unmanaged, IComponentData
        {
            World world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return false;

            EntityManager em = world.EntityManager;
            Entity entity = em.CreateEntity();
            em.AddComponentData(entity, command);
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
            return true;
        }

        /// <summary>
        /// The local host has one in-game connection. We match it by NetworkId and do not
        /// gather ships. Same rule as <see cref="PlayerNameRpcClient"/>.
        /// </summary>
        static Entity FindServerConnection(EntityManager em, int networkId)
        {
            using EntityQuery query = em.CreateEntityQuery(
                ComponentType.ReadOnly<NetworkId>(),
                ComponentType.ReadOnly<NetworkStreamInGame>());
            if (query.IsEmptyIgnoreFilter || query.CalculateEntityCount() != 1)
                return Entity.Null;

            Entity connection = query.GetSingletonEntity();
            if (em.GetComponentData<NetworkId>(connection).Value != networkId)
                return Entity.Null;
            return connection;
        }

        /// <summary>Packs a short label into a 64-byte NetCode string.</summary>
        static FixedString64Bytes ToFixed64(string value)
        {
            FixedString64Bytes fs = default;
            if (!string.IsNullOrEmpty(value))
                fs.CopyFromTruncated(value);
            return fs;
        }

        /// <summary>Packs a hull name into a 128-byte NetCode string.</summary>
        static FixedString128Bytes ToFixed128(string value)
        {
            FixedString128Bytes fs = default;
            if (!string.IsNullOrEmpty(value))
                fs.CopyFromTruncated(value);
            return fs;
        }

        /// <summary>Packs a note or ability list into a 512-byte NetCode string.</summary>
        static FixedString512Bytes ToFixed512(string value)
        {
            FixedString512Bytes fs = default;
            if (!string.IsNullOrEmpty(value))
                fs.CopyFromTruncated(value);
            return fs;
        }
    }

    /// <summary>
    /// Lets the pause menu and the NOTES card agree about Escape without the menu
    /// assembly-referencing the UI class. The card sets <see cref="NotesCardOpen"/>
    /// while it is showing. Escape sets <see cref="CloseRequested"/> and does not open pause.
    /// </summary>
    public static class BetaFeedbackHotkeyGate
    {
        /// <summary>True while the field-notes card is on screen.</summary>
        public static bool NotesCardOpen;

        /// <summary>Set by the pause menu when Escape should close NOTES instead of opening pause.</summary>
        public static bool CloseRequested;
    }
}
