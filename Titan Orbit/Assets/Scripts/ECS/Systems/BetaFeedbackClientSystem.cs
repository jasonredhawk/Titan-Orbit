using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// What the NOTES screen reads after the server answers. Filled by
    /// <see cref="BetaFeedbackClientSystem"/> on the client world. The UI polls the
    /// serial numbers when the card is open — nothing here runs per frame by itself.
    /// </summary>
    public static class BetaFeedbackClientInbox
    {
        /// <summary>One row as the screen shows it. Strings are copies of the RPC bytes.</summary>
        public struct Row
        {
            /// <summary>File id. Delete sends this back.</summary>
            public int Id;

            /// <summary>Who wrote it.</summary>
            public string Player;

            /// <summary>Hull they owned.</summary>
            public string Ship;

            /// <summary>Gun they were firing.</summary>
            public string Gun;

            /// <summary>Ship level in the stamp.</summary>
            public int ShipLevel;

            /// <summary>Branch in the stamp.</summary>
            public int Branch;

            /// <summary>Added ability levels.</summary>
            public string Abilities;

            /// <summary>Typed note.</summary>
            public string Note;
        }

        /// <summary>Newest-first rows from the last finished list.</summary>
        public static readonly List<Row> Rows = new List<Row>(64);

        /// <summary>Bumps when <see cref="Rows"/> is replaced. The screen rebuilds when this changes.</summary>
        public static int ListSerial;

        /// <summary>How many rows were sent (may be less than <see cref="Total"/>).</summary>
        public static int Shown;

        /// <summary>Lines in the file, or −1 when the server checkbox is off.</summary>
        public static int Total;

        /// <summary>Last save / delete / refusal. 0 until the first result.</summary>
        public static byte LastStatus;

        /// <summary>Id from the last result.</summary>
        public static int StatusId;

        /// <summary>Short line for the card ("Sent.", "Removed.", …).</summary>
        public static string StatusMessage;

        /// <summary>Bumps when <see cref="StatusMessage"/> changes.</summary>
        public static int StatusSerial;

        /// <summary>Request id the screen is waiting on. Older replies are ignored.</summary>
        public static byte ActiveRequest;

        /// <summary>Rows collected for <see cref="ActiveRequest"/> before the done RPC.</summary>
        internal static readonly List<Row> Pending = new List<Row>(64);

        /// <summary>
        /// [UNITY] Domain Reload off would otherwise show the previous Play session's notes
        /// before the server answers. Clear on every enter-play.
        /// </summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetForPlayMode()
        {
            Rows.Clear();
            Pending.Clear();
            ListSerial = 0;
            Shown = 0;
            Total = 0;
            LastStatus = 0;
            StatusId = 0;
            StatusMessage = null;
            StatusSerial = 0;
            ActiveRequest = 0;
        }

        /// <summary>
        /// Starts a new list request. Replies that still carry the previous id are dropped.
        /// </summary>
        /// <returns>The id to put on <see cref="RequestBetaFeedbackListCommand"/>.</returns>
        public static byte BeginRequest()
        {
            ActiveRequest++;
            if (ActiveRequest == 0)
                ActiveRequest = 1;
            Pending.Clear();
            return ActiveRequest;
        }

        /// <summary>Stores a result line and bumps <see cref="StatusSerial"/>.</summary>
        internal static void PublishStatus(byte status, int id)
        {
            LastStatus = status;
            StatusId = id;
            if (status == BetaFeedbackStatus.Saved)
                StatusMessage = "Sent.";
            else if (status == BetaFeedbackStatus.Deleted)
                StatusMessage = "Removed.";
            else if (status == BetaFeedbackStatus.Disabled)
                StatusMessage = "This server has field notes turned off.";
            else if (status == BetaFeedbackStatus.Missing)
                StatusMessage = "That note is already gone.";
            else if (status == BetaFeedbackStatus.Full)
                StatusMessage = "The notes file is full. Delete one, then try again.";
            else
                StatusMessage = "Could not save the note.";
            StatusSerial++;
        }
    }

    /// <summary>
    /// Client: copies beta-note RPCs into <see cref="BetaFeedbackClientInbox"/>.
    /// World: ClientSimulation. Paired with <see cref="BetaFeedbackServerSystem"/>.
    /// Does not query ships — only the RPC entities NetCode delivered.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct BetaFeedbackClientSystem : ISystem
    {
        EntityQuery _entries;
        EntityQuery _done;
        EntityQuery _results;

        /// <summary>Caches inbound RPC queries so a quiet match does not allocate every tick.</summary>
        public void OnCreate(ref SystemState state)
        {
            _entries = state.GetEntityQuery(
                ComponentType.ReadOnly<BetaFeedbackEntryRpc>(),
                ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
            _done = state.GetEntityQuery(
                ComponentType.ReadOnly<BetaFeedbackListDoneRpc>(),
                ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
            _results = state.GetEntityQuery(
                ComponentType.ReadOnly<BetaFeedbackMutationResultRpc>(),
                ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
        }

        /// <summary>Drains entry, list-done, and save/delete result RPCs.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (_entries.IsEmptyIgnoreFilter && _done.IsEmptyIgnoreFilter && _results.IsEmptyIgnoreFilter)
                return;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- One saved row ---
            foreach (var (rpc, entity) in SystemAPI
                         .Query<RefRO<BetaFeedbackEntryRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
                BetaFeedbackEntryRpc row = rpc.ValueRO;
                if (row.RequestId != BetaFeedbackClientInbox.ActiveRequest)
                    continue;

                BetaFeedbackClientInbox.Pending.Add(new BetaFeedbackClientInbox.Row
                {
                    Id = row.Id,
                    Player = row.PlayerName.ToString(),
                    Ship = row.ShipName.ToString(),
                    Gun = row.GunName.ToString(),
                    ShipLevel = row.ShipLevel,
                    Branch = row.BranchIndex,
                    Abilities = row.Abilities.ToString(),
                    Note = row.Note.ToString(),
                });
            }

            // --- Batch finished: publish, newest first ---
            foreach (var (rpc, entity) in SystemAPI
                         .Query<RefRO<BetaFeedbackListDoneRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
                BetaFeedbackListDoneRpc done = rpc.ValueRO;
                if (done.RequestId != BetaFeedbackClientInbox.ActiveRequest)
                    continue;

                BetaFeedbackClientInbox.Rows.Clear();
                List<BetaFeedbackClientInbox.Row> pending = BetaFeedbackClientInbox.Pending;
                for (int i = 0; i < pending.Count; i++)
                    BetaFeedbackClientInbox.Rows.Add(pending[i]);
                pending.Clear();
                BetaFeedbackClientInbox.Shown = done.Shown;
                BetaFeedbackClientInbox.Total = done.Total;
                BetaFeedbackClientInbox.ListSerial++;
                if (done.Total < 0)
                    BetaFeedbackClientInbox.PublishStatus(BetaFeedbackStatus.Disabled, 0);
            }

            // --- Save / delete result ---
            foreach (var (rpc, entity) in SystemAPI
                         .Query<RefRO<BetaFeedbackMutationResultRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
                BetaFeedbackMutationResultRpc result = rpc.ValueRO;
                BetaFeedbackClientInbox.PublishStatus(result.Status, result.Id);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
