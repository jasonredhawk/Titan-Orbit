using TitanOrbit.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: accepts <see cref="ShipCommsCommand"/>, checks the speaker has a living ship,
    /// rate-limits the connection, then broadcasts <see cref="ShipCommsRpc"/> to every client.
    /// <para>
    /// World: ServerSimulation. Group: SimulationSystemGroup. Not Burst-compiled — we load the
    /// managed <see cref="ShipCommsKeywordCatalog"/> to validate indices.
    /// </para>
    /// <para>
    /// [NETCODE] Owner comes from <see cref="ReceiveRpcCommandRequest.SourceConnection"/> →
    /// <see cref="NetworkId"/>. The command has no client-supplied id, so a player cannot put
    /// chips above someone else's hull. Local Host injects the command with
    /// <c>ReceiveRpcCommandRequest</c> already set (see <c>ShipCommsRpcClient</c>) because
    /// client→server SendRpc can drop under Instantiates load.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ShipCommsServerSystem : ISystem
    {
        /// <summary>
        /// [TITAN-ORBIT] Minimum seconds between accepted callouts on one connection.
        /// Cheap anti-spam — comms are social, not a combat input.
        /// </summary>
        const double RateLimitSeconds = 1.5d;

        /// <summary>
        /// Drains inbound comms commands and broadcasts accepted sentences.
        /// <para>
        /// [ECS/DOTS] ISystem structs must stay unmanaged — do not store the ScriptableObject
        /// catalog as a field (that made TypeManager try to new this as a class system and
        /// threw "does not inherit from ComponentSystemBase").
        /// </para>
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // ScriptableObject catalog — static-cached inside LoadDefault, safe to call here.
            ShipCommsKeywordCatalog catalog = ShipCommsKeywordCatalog.LoadDefault();

            var em = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            double now = SystemAPI.Time.ElapsedTime;

            // --- Drain client → server commands ---
            // [NETCODE] ReceiveRpcCommandRequest pairs the RPC with the sending connection.
            foreach (var (cmd, req, rpcEntity) in SystemAPI
                         .Query<RefRO<ShipCommsCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                // Always consume the RPC entity — a rejected send must not linger forever.
                ecb.DestroyEntity(rpcEntity);

                Entity connection = req.ValueRO.SourceConnection;
                if (!em.HasComponent<NetworkId>(connection))
                    continue;

                int networkId = em.GetComponentData<NetworkId>(connection).Value;
                if (networkId <= 0)
                    continue;

                ShipCommsCommand sentence = cmd.ValueRO;
                if (!catalog.IsValidSequence(sentence.Count, sentence.K0, sentence.K1, sentence.K2))
                    continue;

                // --- Living ship ---
                // Ghost — NetCode replica. IsDead means hull+cargo emptied; AwaitingTeamSelection
                // is the join-team plaque before the player has a flying hull.
                // Use EntityManager (not a nested SystemAPI.Query) — we are already iterating RPCs.
                if (!SpeakerHasLivingShip(em, networkId))
                    continue;

                // --- Rate limit ---
                // [TITAN-ORBIT] Cooldown lives on the connection entity (not the ship) so a
                // respawn cannot reset the timer by spawning a new hull. Applied only after
                // the ship check so a dead speaker does not burn the cooldown.
                if (em.HasComponent<ShipCommsCooldown>(connection))
                {
                    double last = em.GetComponentData<ShipCommsCooldown>(connection).LastSendElapsed;
                    if (now - last < RateLimitSeconds)
                        continue;

                    ecb.SetComponent(connection, new ShipCommsCooldown { LastSendElapsed = now });
                }
                else
                {
                    ecb.AddComponent(connection, new ShipCommsCooldown { LastSendElapsed = now });
                }

                Broadcast(ecb, networkId, sentence);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>
        /// True when a ship ghost owned by <paramref name="networkId"/> is alive and in play.
        /// Ships are few — a linear query on an RPC (not every tick) is cheap.
        /// </summary>
        static bool SpeakerHasLivingShip(EntityManager em, int networkId)
        {
            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner), typeof(ShipState));
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var states = query.ToComponentDataArray<ShipState>(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;

                return !states[i].IsDead && !states[i].AwaitingTeamSelection;
            }

            return false;
        }

        /// <summary>
        /// [NETCODE] TargetConnection = Null means every connected client, including the sender.
        /// Dedicated clients apply this in <see cref="ShipCommsRpcClientSystem"/>. The sender
        /// also paints an optimistic local bubble so they do not wait on RTT.
        /// </summary>
        static void Broadcast(EntityCommandBuffer ecb, int networkId, in ShipCommsCommand sentence)
        {
            Entity announce = ecb.CreateEntity();
            ecb.AddComponent(announce, new ShipCommsRpc
            {
                NetworkId = networkId,
                Count = sentence.Count,
                K0 = sentence.K0,
                K1 = sentence.K1,
                K2 = sentence.K2,
            });
            ecb.AddComponent(announce, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }
    }
}
