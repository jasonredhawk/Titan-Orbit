using TitanOrbit.Generation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] When a player's camera rectangle moves, send <see cref="GemCatchUpRpc"/> for gems
    /// that are inside the new view and were outside the previous one. Gems they already have are
    /// ignored by the client. Gems that left the view are dropped locally, so this is how they
    /// come back. Map size comes from <see cref="MapStateSingleton"/>.
    /// World: ServerSimulation. Runs after <see cref="ViewInterestServerSystem"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ViewInterestServerSystem))]
    public partial struct GemViewCatchUpSystem : ISystem
    {
        struct GemRow
        {
            public GemCatchUpRpc Rpc;
        }

        /// <summary>
        /// Scans gems only on ticks where at least one camera moved. Idle ticks do not copy gem arrays.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            bool anyMoved = false;
            foreach (var view in SystemAPI.Query<RefRO<ConnectionViewInterest>>().WithAll<NetworkStreamInGame>())
            {
                if (view.ValueRO.ViewMoved != 0)
                    anyMoved = true;
            }

            if (!anyMoved)
                return;

            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var map) ||
                !ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
                return;

            float mapW = map.MapWidth;
            float mapH = map.MapHeight;
            var gems = new NativeList<GemRow>(64, Allocator.Temp);
            foreach (var (gem, xf, kin, motion) in SystemAPI
                         .Query<RefRO<GemState>, RefRO<LocalTransform>, RefRO<GemKinematics>, RefRO<GemMotionState>>()
                         .WithAll<GemTag>())
            {
                if (gem.ValueRO.SpawnId == 0 || gem.ValueRO.IsConsumed || gem.ValueRO.Value <= 0f)
                    continue;

                float3 pos = xf.ValueRO.Position;
                pos.y = 0f;
                gems.Add(new GemRow
                {
                    Rpc = new GemCatchUpRpc
                    {
                        SpawnId = gem.ValueRO.SpawnId,
                        Position = pos,
                        Velocity = kin.ValueRO.Velocity,
                        AngularVelocity = kin.ValueRO.AngularVelocity,
                        Value = gem.ValueRO.Value,
                        Size = gem.ValueRO.Size,
                        SpawnServerTime = gem.ValueRO.SpawnServerTime,
                        Phase = motion.ValueRO.Phase,
                        BurstIndex = motion.ValueRO.BurstIndex,
                        IsBonusGem = (byte)gem.ValueRO.Tint,
                        ExcludePickupNetworkId = gem.ValueRO.ExcludePickupNetworkId,
                        ExcludePickupUntilServerTime = gem.ValueRO.ExcludePickupUntilServerTime,
                        TractorShipId = motion.ValueRO.TractorShipId,
                        TractorWingIndex = motion.ValueRO.TractorWingIndex,
                        TractorLockTick = motion.ValueRO.TractorLockTick,
                        TractorExtendDuration = motion.ValueRO.TractorExtendDuration,
                    },
                });
            }

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (view, entity) in SystemAPI
                         .Query<RefRW<ConnectionViewInterest>>()
                         .WithAll<NetworkStreamInGame>()
                         .WithEntityAccess())
            {
                ref ConnectionViewInterest v = ref view.ValueRW;
                if (v.ViewMoved == 0)
                    continue;

                for (int i = 0; i < gems.Length; i++)
                {
                    float3 pos = gems[i].Rpc.Position;
                    bool nowIn = ViewInterestMath.PointInView(
                        v.CenterX, v.CenterZ, v.HalfW, v.HalfH, pos, mapW, mapH, ViewInterestTuning.GemKeepMargin);
                    bool wasIn = v.HasPrev != 0 && ViewInterestMath.PointInView(
                        v.PrevCenterX, v.PrevCenterZ, v.PrevHalfW, v.PrevHalfH, pos, mapW, mapH, ViewInterestTuning.GemKeepMargin);
                    if (!nowIn || wasIn)
                        continue;

                    GemNetNotify.SendCatchUp(ref ecb, entity, gems[i].Rpc);
                }

                v.ViewMoved = 0;
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            gems.Dispose();
        }
    }
}
