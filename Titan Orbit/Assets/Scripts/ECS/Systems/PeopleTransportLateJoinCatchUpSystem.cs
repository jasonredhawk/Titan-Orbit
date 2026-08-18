using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: once a connection is InGame, send <see cref="PeopleTransportSpawnRpc"/> for every
    /// in-flight capsule so late joiners Instantiates VFX (SpawnRpc is otherwise one-shot at launch).
    /// World: ServerSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct PeopleTransportLateJoinCatchUpSystem : ISystem
    {
        EntityQuery _pendingConnQuery;
        EntityQuery _transportQuery;

        /// <summary>Caches InGame connections missing catch-up and living transports.</summary>
        public void OnCreate(ref SystemState state)
        {
            _pendingConnQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NetworkStreamInGame>(),
                ComponentType.ReadOnly<NetworkId>(),
                ComponentType.Exclude<PeopleTransportCatchUpSent>());
            _transportQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<PeopleTransportTag>(),
                ComponentType.ReadOnly<PeopleTransportState>(),
                ComponentType.ReadOnly<LocalTransform>());
        }

        /// <summary>Dumps one targeted SpawnRpc per living transport, then tags the connection.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (_pendingConnQuery.IsEmptyIgnoreFilter)
                return;

            var connections = _pendingConnQuery.ToEntityArray(Allocator.Temp);
            var states = _transportQuery.ToComponentDataArray<PeopleTransportState>(Allocator.Temp);
            var xf = _transportQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            for (int c = 0; c < connections.Length; c++)
            {
                Entity connection = connections[c];
                for (int t = 0; t < states.Length; t++)
                {
                    var s = states[t];
                    if (s.Sequence == 0)
                        continue;

                    float3 pos = xf[t].Position;
                    pos.y = 0f;
                    float3 target = pos + s.Velocity;
                    target.y = 0f;

                    Entity rpcEntity = ecb.CreateEntity();
                    ecb.AddComponent(rpcEntity, new PeopleTransportSpawnRpc
                    {
                        Sequence = s.Sequence,
                        SpawnPosition = pos,
                        TargetPosition = target,
                        Velocity = s.Velocity,
                        CruiseSpeed = s.CruiseSpeed,
                        Amount = s.Amount,
                        TargetShipNetworkId = s.TargetShipNetworkId != 0
                            ? s.TargetShipNetworkId
                            : s.SourceShipNetworkId,
                        SourcePlanetId = s.SourcePlanetId,
                        TargetPlanetId = s.TargetPlanetId,
                        IsLoad = s.IsLoad,
                        Team = s.Team,
                    });
                    ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = connection });
                }

                ecb.AddComponent<PeopleTransportCatchUpSent>(connection);
            }

            ecb.Playback(state.EntityManager);
            connections.Dispose();
            states.Dispose();
            xf.Dispose();
            ecb.Dispose();
        }
    }
}
