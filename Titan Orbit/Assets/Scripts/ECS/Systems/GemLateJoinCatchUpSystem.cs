using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: once a connection is InGame, send <see cref="GemCatchUpRpc"/> for every live gem
    /// so late joiners hydrate crystals that spawned before they connected.
    /// World: ServerSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct GemLateJoinCatchUpSystem : ISystem
    {
        EntityQuery _pendingConnQuery;
        EntityQuery _gemQuery;

        /// <summary>Caches InGame connections missing gem catch-up and living gems.</summary>
        public void OnCreate(ref SystemState state)
        {
            _pendingConnQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NetworkStreamInGame>(),
                ComponentType.ReadOnly<NetworkId>(),
                ComponentType.Exclude<GemCatchUpSent>());
            _gemQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<GemTag>(),
                ComponentType.ReadOnly<GemState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<GemKinematics>(),
                ComponentType.ReadOnly<GemMotionState>());
        }

        /// <summary>Dumps one targeted CatchUpRpc per live gem, then tags the connection.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (_pendingConnQuery.IsEmptyIgnoreFilter)
                return;

            var connections = _pendingConnQuery.ToEntityArray(Allocator.Temp);
            var gemEntities = _gemQuery.ToEntityArray(Allocator.Temp);
            var gemStates = _gemQuery.ToComponentDataArray<GemState>(Allocator.Temp);
            var xf = _gemQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var kin = _gemQuery.ToComponentDataArray<GemKinematics>(Allocator.Temp);
            var motion = _gemQuery.ToComponentDataArray<GemMotionState>(Allocator.Temp);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            for (int c = 0; c < connections.Length; c++)
            {
                Entity connection = connections[c];
                for (int g = 0; g < gemStates.Length; g++)
                {
                    var s = gemStates[g];
                    if (s.SpawnId == 0 || s.IsConsumed)
                        continue;

                    float3 pos = xf[g].Position;
                    pos.y = 0f;
                    GemNetNotify.SendCatchUp(ref ecb, connection, new GemCatchUpRpc
                    {
                        SpawnId = s.SpawnId,
                        Position = pos,
                        Velocity = kin[g].Velocity,
                        AngularVelocity = kin[g].AngularVelocity,
                        Value = s.Value,
                        Size = s.Size,
                        SpawnServerTime = s.SpawnServerTime,
                        Phase = motion[g].Phase,
                        BurstIndex = motion[g].BurstIndex,
                        IsBonusGem = s.IsBonusGem ? (byte)1 : (byte)0,
                        ExcludePickupNetworkId = s.ExcludePickupNetworkId,
                        ExcludePickupUntilServerTime = s.ExcludePickupUntilServerTime,
                        TractorShipId = motion[g].TractorShipId,
                        TractorWingIndex = motion[g].TractorWingIndex,
                        TractorLockTick = motion[g].TractorLockTick,
                        TractorExtendDuration = motion[g].TractorExtendDuration,
                    });
                }

                ecb.AddComponent<GemCatchUpSent>(connection);
            }

            ecb.Playback(state.EntityManager);
            connections.Dispose();
            gemEntities.Dispose();
            gemStates.Dispose();
            xf.Dispose();
            kin.Dispose();
            motion.Dispose();
            ecb.Dispose();
        }
    }
}
