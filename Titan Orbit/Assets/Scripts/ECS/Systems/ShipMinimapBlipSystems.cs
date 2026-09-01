using System.Collections.Generic;
using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server: pack every ship pose at ~4 Hz so clients can draw far hulls on the minimap
    /// without streaming full combat ghosts. Each IRpcCommand holds at most
    /// <see cref="ShipMinimapBlipRpc.MaxBlips"/> flattened hulls.
    /// World: ServerSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ShipMinimapBlipServerSystem : ISystem
    {
        public const float SendIntervalSeconds = 0.25f;
        const int MaxShipsPerTick = 64;

        struct PackedBlip
        {
            public int NetworkId;
            public int Xz;
            public int Meta;
        }

        double _nextSendElapsed;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkTime>();
        }

        public void OnUpdate(ref SystemState state)
        {
            double now = SystemAPI.Time.ElapsedTime;
            if (now < _nextSendElapsed)
                return;
            _nextSendElapsed = now + SendIntervalSeconds;

            int connCount = 0;
            foreach (var _ in SystemAPI.Query<RefRO<NetworkId>>().WithAll<NetworkStreamInGame>())
                connCount++;
            if (connCount <= 0)
                return;

            var packed = new NativeList<PackedBlip>(MaxShipsPerTick, Allocator.Temp);
            foreach (var (transform, ship, owner, entity) in SystemAPI
                         .Query<RefRO<LocalTransform>, RefRO<ShipState>, RefRO<GhostOwner>>()
                         .WithAll<ShipTag, GhostInstance>()
                         .WithEntityAccess())
            {
                if (packed.Length >= MaxShipsPerTick)
                    break;
                if (owner.ValueRO.NetworkId <= 0)
                    continue;

                byte flags = 0;
                if (ship.ValueRO.IsDead)
                    flags |= ShipMinimapBlipRpc.FlagDead;
                if (state.EntityManager.HasComponent<MegaShipState>(entity) &&
                    state.EntityManager.GetComponentData<MegaShipState>(entity).IsMega)
                    flags |= ShipMinimapBlipRpc.FlagMega;

                packed.Add(new PackedBlip
                {
                    NetworkId = owner.ValueRO.NetworkId,
                    Xz = PackXz(transform.ValueRO.Position.x, transform.ValueRO.Position.z),
                    Meta = (byte)ship.ValueRO.Team | (ship.ValueRO.ShipLevel << 8) | (flags << 16),
                });
            }

            if (packed.Length == 0)
            {
                packed.Dispose();
                return;
            }

            int chunkCount = (packed.Length + ShipMinimapBlipRpc.MaxBlips - 1) / ShipMinimapBlipRpc.MaxBlips;
            uint sequence = (uint)math.max(1, (int)(now / SendIntervalSeconds));
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            for (int chunk = 0; chunk < chunkCount; chunk++)
            {
                int start = chunk * ShipMinimapBlipRpc.MaxBlips;
                int count = math.min(ShipMinimapBlipRpc.MaxBlips, packed.Length - start);
                var rpc = new ShipMinimapBlipRpc
                {
                    Count = (byte)count,
                    ChunkIndex = (byte)chunk,
                    ChunkCount = (byte)chunkCount,
                    Sequence = sequence,
                };
                for (int i = 0; i < count; i++)
                {
                    var b = packed[start + i];
                    rpc.SetSlot(i, b.NetworkId, b.Xz, b.Meta);
                }

                foreach (var (_, connEntity) in SystemAPI
                             .Query<RefRO<NetworkId>>()
                             .WithAll<NetworkStreamInGame>()
                             .WithEntityAccess())
                {
                    Entity req = ecb.CreateEntity();
                    ecb.AddComponent(req, rpc);
                    ecb.AddComponent(req, new SendRpcCommandRequest { TargetConnection = connEntity });
                }
            }

            packed.Dispose();
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        static int PackXz(float x, float z)
        {
            short sx = (short)math.clamp((int)math.round(x * 10f), short.MinValue, short.MaxValue);
            short sz = (short)math.clamp((int)math.round(z * 10f), short.MinValue, short.MaxValue);
            return ((int)(ushort)sx) | ((int)(ushort)sz << 16);
        }
    }

    /// <summary>
    /// Client: apply far-ship blip RPCs to <see cref="ShipMinimapBlipCache"/>.
    /// World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ShipMinimapBlipClientSystem : ISystem
    {
        static readonly List<ShipMinimapBlipCache.Entry> s_Scratch = new List<ShipMinimapBlipCache.Entry>(8);

        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (rpc, entity) in SystemAPI.Query<RefRO<ShipMinimapBlipRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>()
                         .WithEntityAccess())
            {
                Unpack(rpc.ValueRO, s_Scratch);
                ShipMinimapBlipCache.ApplyChunk(
                    rpc.ValueRO.Sequence,
                    rpc.ValueRO.ChunkIndex,
                    rpc.ValueRO.ChunkCount,
                    s_Scratch);
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        static void Unpack(in ShipMinimapBlipRpc rpc, List<ShipMinimapBlipCache.Entry> dst)
        {
            dst.Clear();
            int count = math.min((int)rpc.Count, ShipMinimapBlipRpc.MaxBlips);
            for (int i = 0; i < count; i++)
            {
                if (!rpc.TryGetSlot(i, out int networkId, out int xz, out int meta))
                    break;
                byte flags = (byte)((meta >> 16) & 0xff);
                dst.Add(new ShipMinimapBlipCache.Entry
                {
                    NetworkId = networkId,
                    X = ((short)(xz & 0xffff)) / 10f,
                    Z = ((short)((xz >> 16) & 0xffff)) / 10f,
                    Team = (TeamId)(byte)(meta & 0xff),
                    Level = (byte)((meta >> 8) & 0xff),
                    IsDead = (flags & ShipMinimapBlipRpc.FlagDead) != 0,
                    IsMega = (flags & ShipMinimapBlipRpc.FlagMega) != 0,
                });
            }
        }
    }
}
