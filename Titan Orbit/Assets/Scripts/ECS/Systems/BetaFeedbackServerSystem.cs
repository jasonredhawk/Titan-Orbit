using System.Collections.Generic;
using TitanOrbit;
using TitanOrbit.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Status bytes on <see cref="BetaFeedbackMutationResultRpc"/>. Shared so the server
    /// writer and the client inbox use the same numbers.
    /// </summary>
    public static class BetaFeedbackStatus
    {
        /// <summary>The line was appended.</summary>
        public const byte Saved = 1;

        /// <summary>The line was removed.</summary>
        public const byte Deleted = 2;

        /// <summary>This connection does not own the beta-tester item.</summary>
        public const byte Disabled = 3;

        /// <summary>Delete named an id that is not in the file.</summary>
        public const byte Missing = 4;

        /// <summary>The log is already at its size cap.</summary>
        public const byte Full = 5;
    }

    /// <summary>
    /// Server: stores beta field notes in <see cref="BetaFeedbackLog"/> and answers list /
    /// delete requests. Runs only when a client actually sends an RPC — it does not read
    /// the file every tick. World: ServerSimulation.
    /// <para>
    /// Writes are refused unless the sending connection owns the beta-tester item
    /// (<see cref="BetaTesterMatch"/>). A public player still has these RPC types so
    /// the protocol matches, but the server stores nothing for them.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct BetaFeedbackServerSystem : ISystem
    {
        /// <summary>Reused for list replies so a refresh does not allocate a new list.</summary>
        static readonly List<BetaFeedbackEntry> Scratch = new List<BetaFeedbackEntry>(64);

        /// <summary>Submit RPCs waiting this tick. Empty almost every tick.</summary>
        EntityQuery _submit;

        /// <summary>Delete RPCs waiting this tick.</summary>
        EntityQuery _delete;

        /// <summary>List RPCs waiting this tick.</summary>
        EntityQuery _list;

        /// <summary>
        /// Caches the three RPC queries so an idle public server does not allocate a
        /// command buffer every simulation tick.
        /// </summary>
        public void OnCreate(ref SystemState state)
        {
            _submit = state.GetEntityQuery(
                ComponentType.ReadOnly<SubmitBetaFeedbackCommand>(),
                ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
            _delete = state.GetEntityQuery(
                ComponentType.ReadOnly<DeleteBetaFeedbackCommand>(),
                ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
            _list = state.GetEntityQuery(
                ComponentType.ReadOnly<RequestBetaFeedbackListCommand>(),
                ComponentType.ReadOnly<ReceiveRpcCommandRequest>());
        }

        /// <summary>
        /// Drains submit, delete, and list RPCs. Returns immediately when none arrived.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // [ECS/DOTS] No file IO and no command buffer on the ticks with no note RPC.
            if (_submit.IsEmptyIgnoreFilter && _delete.IsEmptyIgnoreFilter && _list.IsEmptyIgnoreFilter)
                return;
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            var em = state.EntityManager;

            // --- Submit ---
            // [NETCODE] SourceConnection is who asked. We store the snapshot they sent;
            // the server does not re-read their ship (the stamp is from when they opened NOTES).
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<SubmitBetaFeedbackCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
                Entity connection = req.ValueRO.SourceConnection;
                if (!BetaTesterMatch.IsOwned(em, connection))
                {
                    Reply(ecb, connection, BetaFeedbackStatus.Disabled, 0);
                    continue;
                }

                SubmitBetaFeedbackCommand row = cmd.ValueRO;
                int id = BetaFeedbackLog.TryAppend(new BetaFeedbackEntry
                {
                    Player = row.PlayerName.ToString(),
                    Ship = row.ShipName.ToString(),
                    Gun = row.GunName.ToString(),
                    ShipLevel = row.ShipLevel,
                    Branch = row.BranchIndex,
                    Abilities = row.Abilities.ToString(),
                    Note = row.Note.ToString(),
                });

                if (id > 0)
                {
                    Debug.Log("[BetaFeedback] Stored note id=" + id + " player=" + row.PlayerName);
                    Reply(ecb, connection, BetaFeedbackStatus.Saved, id);
                }
                else if (id < 0)
                {
                    Reply(ecb, connection, BetaFeedbackStatus.Full, 0);
                }
                else
                {
                    // id 0 is an IO failure, not the public-off switch.
                    Reply(ecb, connection, 0, 0);
                }
            }

            // --- Delete one line ---
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<DeleteBetaFeedbackCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
                Entity connection = req.ValueRO.SourceConnection;
                if (!BetaTesterMatch.IsOwned(em, connection))
                {
                    Reply(ecb, connection, BetaFeedbackStatus.Disabled, cmd.ValueRO.Id);
                    continue;
                }

                bool removed = BetaFeedbackLog.TryDelete(cmd.ValueRO.Id);
                Reply(ecb, connection, removed ? BetaFeedbackStatus.Deleted : BetaFeedbackStatus.Missing,
                    cmd.ValueRO.Id);
            }

            // --- List ---
            // Newest MaxListed rows only. The file keeps anything older for a later read.
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<RequestBetaFeedbackListCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
                Entity connection = req.ValueRO.SourceConnection;
                byte requestId = cmd.ValueRO.RequestId;
                if (!BetaTesterMatch.IsOwned(em, connection))
                {
                    ReplyListDone(ecb, connection, requestId, shown: 0, total: -1);
                    continue;
                }

                BetaFeedbackLog.CopyAll(Scratch);
                int total = Scratch.Count;
                int shown = total > BetaFeedbackLog.MaxListed ? BetaFeedbackLog.MaxListed : total;
                for (int n = 0; n < shown; n++)
                {
                    // Newest first: index 0 is the last line in the file.
                    BetaFeedbackEntry row = Scratch[total - 1 - n];
                    Entity reply = ecb.CreateEntity();
                    ecb.AddComponent(reply, new BetaFeedbackEntryRpc
                    {
                        RequestId = requestId,
                        Index = n,
                        Count = shown,
                        Id = row.Id,
                        PlayerName = ToFixed64(row.Player),
                        ShipName = ToFixed128(row.Ship),
                        GunName = ToFixed64(row.Gun),
                        ShipLevel = row.ShipLevel,
                        BranchIndex = row.Branch,
                        Abilities = ToFixed512(row.Abilities),
                        Note = ToFixed512(row.Note),
                    });
                    ecb.AddComponent(reply, new SendRpcCommandRequest { TargetConnection = connection });
                }

                ReplyListDone(ecb, connection, requestId, shown, total);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>Sends a save / delete result back to the connection that asked.</summary>
        static void Reply(EntityCommandBuffer ecb, Entity connection, byte status, int id)
        {
            if (connection == Entity.Null)
                return;

            Entity reply = ecb.CreateEntity();
            ecb.AddComponent(reply, new BetaFeedbackMutationResultRpc { Status = status, Id = id });
            ecb.AddComponent(reply, new SendRpcCommandRequest { TargetConnection = connection });
        }

        /// <summary>Sends the end-of-list marker. <paramref name="total"/> −1 means the checkbox is off.</summary>
        static void ReplyListDone(EntityCommandBuffer ecb, Entity connection, byte requestId, int shown, int total)
        {
            if (connection == Entity.Null)
                return;

            Entity reply = ecb.CreateEntity();
            ecb.AddComponent(reply, new BetaFeedbackListDoneRpc
            {
                RequestId = requestId,
                Shown = shown,
                Total = total,
            });
            ecb.AddComponent(reply, new SendRpcCommandRequest { TargetConnection = connection });
        }

        /// <summary>Packs text into a 64-byte NetCode string, truncating if it does not fit.</summary>
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
}
