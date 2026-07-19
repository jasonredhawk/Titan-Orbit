using TitanOrbit.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client: receives <see cref="PeopleTransportSpawnRpc"/> and feeds
    /// <see cref="PeopleTransportVfxBridge"/> for <c>PeopleTransportVfxDriver</c> GameObjects.
    /// Does not create ECS presentation entities — hybrid GO VFX is owned by the MonoBehaviour driver
    /// (same pattern as <c>ClientLocalBulletVfxBridge</c>).
    /// World: ClientSimulation. Group: SimulationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct PeopleTransportSpawnRpcClientSystem : ISystem
    {
        /// <summary>
        /// Re-queues broadcast RPCs into the VFX bridge (deduped with host in-process enqueue).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // [NETCODE] ReceiveRpcCommandRequest marks inbound RPC entities from the network.
            foreach (var (rpc, entity) in SystemAPI
                         .Query<RefRO<PeopleTransportSpawnRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                var r = rpc.ValueRO;
                // #region agent log
                AgentDebugSessionLog.Write("post-fix", "A", "PeopleTransportSpawnRpcClientSystem.OnUpdate",
                    "client_received_people_transport_rpc",
                    "{\"seq\":" + r.Sequence + ",\"isLoad\":" + r.IsLoad +
                    ",\"shipNetId\":" + r.TargetShipNetworkId + ",\"amount\":" + r.Amount + "}");
                // #endregion

                float3 spawn = r.SpawnPosition;
                spawn.y = 0f;
                float3 target = r.TargetPosition;
                target.y = 0f;

                PeopleTransportVfxBridge.TryEnqueue(new PeopleTransportVfxBridge.SpawnRequest
                {
                    Sequence = r.Sequence,
                    SpawnPosition = spawn,
                    TargetPosition = target,
                    Velocity = r.Velocity,
                    CruiseSpeed = r.CruiseSpeed,
                    Amount = r.Amount,
                    TargetShipNetworkId = r.TargetShipNetworkId,
                    IsLoad = r.IsLoad,
                    Team = r.Team,
                });
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
