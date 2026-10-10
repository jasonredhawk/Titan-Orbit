using TitanOrbit.Generation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Sends a one-shot <see cref="MineVisibleRpc"/> when a mine sits in a player's view
    /// but the owner ship does not (so the ship ghost is not streaming the mine buffer).
    /// Sends a hide when the mine leaves that view or is gone. The ship itself is not pulled
    /// into relevancy, which would also stream its movement.
    /// Map size comes from <see cref="MapStateSingleton"/>.
    /// World: ServerSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ViewInterestServerSystem))]
    public partial struct MineViewServerSystem : ISystem
    {
        struct MineKey : System.IEquatable<MineKey>
        {
            public int ViewerId;
            public int OwnerId;
            public uint Sequence;

            public bool Equals(MineKey other) =>
                ViewerId == other.ViewerId && OwnerId == other.OwnerId && Sequence == other.Sequence;

            public override int GetHashCode() => ViewerId * 397 ^ OwnerId * 31 ^ (int)Sequence;
        }

        struct Desired
        {
            public MineKey Key;
            public Entity Connection;
            public DeployedMineElement Mine;
        }

        NativeParallelHashMap<MineKey, double> _sent;
        NativeList<Desired> _desired;
        NativeList<MineKey> _drop;

        /// <summary>Allocates the sent-set and the per-tick desired list.</summary>
        public void OnCreate(ref SystemState state)
        {
            _sent = new NativeParallelHashMap<MineKey, double>(64, Allocator.Persistent);
            _desired = new NativeList<Desired>(64, Allocator.Persistent);
            _drop = new NativeList<MineKey>(32, Allocator.Persistent);
        }

        /// <summary>Releases the persistent containers.</summary>
        public void OnDestroy(ref SystemState state)
        {
            if (_sent.IsCreated)
                _sent.Dispose();
            if (_desired.IsCreated)
                _desired.Dispose();
            if (_drop.IsCreated)
                _drop.Dispose();
        }

        /// <summary>Diffs mines-in-view against what each connection was already told.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var map) ||
                !ToroidalMapEcs.IsValidMapSize(map.MapWidth, map.MapHeight))
                return;

            if (ViewInterestLookup.Count == 0)
                return;

            float mapW = map.MapWidth;
            float mapH = map.MapHeight;
            _desired.Clear();

            foreach (var (mines, owner, transform) in SystemAPI
                         .Query<DynamicBuffer<DeployedMineElement>, RefRO<GhostOwner>, RefRO<LocalTransform>>()
                         .WithAll<ShipTag>())
            {
                float3 shipPos = transform.ValueRO.Position;
                int ownerId = owner.ValueRO.NetworkId;
                for (int m = 0; m < mines.Length; m++)
                {
                    DeployedMineElement mine = mines[m];
                    if (mine.Sequence == 0)
                        continue;

                    int viewers = ViewInterestLookup.Count;
                    for (int v = 0; v < viewers; v++)
                    {
                        ViewInterestSlot slot = ViewInterestLookup.At(v);
                        // The ship ghost already carries this buffer when the hull is inside the enter margin.
                        bool shipInView = ViewInterestMath.PointInView(
                            slot.CenterX, slot.CenterZ, slot.HalfW, slot.HalfH,
                            shipPos, mapW, mapH, ViewInterestTuning.EnterMargin);
                        if (shipInView)
                            continue;

                        if (!ViewInterestMath.PointInView(
                                slot.CenterX, slot.CenterZ, slot.HalfW, slot.HalfH,
                                mine.Position, mapW, mapH, 4f))
                            continue;

                        _desired.Add(new Desired
                        {
                            Key = new MineKey
                            {
                                ViewerId = slot.NetworkId,
                                OwnerId = ownerId,
                                Sequence = mine.Sequence,
                            },
                            Connection = slot.Connection,
                            Mine = mine,
                        });
                    }
                }
            }

            if (_desired.Length == 0 && _sent.IsEmpty)
                return;

            int fresh = 0;
            for (int i = 0; i < _desired.Length; i++)
            {
                if (!_sent.ContainsKey(_desired[i].Key))
                    fresh++;
            }

            _drop.Clear();
            foreach (var kv in _sent)
            {
                bool still = false;
                for (int i = 0; i < _desired.Length; i++)
                {
                    if (!_desired[i].Key.Equals(kv.Key))
                        continue;
                    still = true;
                    break;
                }

                if (!still)
                    _drop.Add(kv.Key);
            }

            if (fresh == 0 && _drop.Length == 0)
                return;

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            for (int i = 0; i < _desired.Length; i++)
            {
                Desired row = _desired[i];
                if (_sent.ContainsKey(row.Key))
                    continue;

                int ownerId = row.Mine.OwnerNetworkId != 0 ? row.Mine.OwnerNetworkId : row.Key.OwnerId;
                Entity rpcEntity = ecb.CreateEntity();
                ecb.AddComponent(rpcEntity, new MineVisibleRpc
                {
                    Visible = 1,
                    Sequence = row.Mine.Sequence,
                    OwnerNetworkId = ownerId,
                    Position = row.Mine.Position,
                    OwnerTeam = row.Mine.OwnerTeam,
                    ItemLevel = row.Mine.ItemLevel,
                    ExpireTime = row.Mine.ExpireTime,
                    PlaceTime = row.Mine.PlaceTime,
                    MaxHealth = row.Mine.MaxHealth,
                    Damage = row.Mine.Damage,
                    VisualScale = row.Mine.VisualScale,
                    ExplosionVfxScale = row.Mine.ExplosionVfxScale,
                });
                ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = row.Connection });
                _sent.TryAdd(row.Key, row.Mine.PlaceTime);
            }

            for (int i = 0; i < _drop.Length; i++)
            {
                MineKey key = _drop[i];
                double placeTime = 0d;
                if (_sent.TryGetValue(key, out double stored))
                    placeTime = stored;
                _sent.Remove(key);

                Entity connection = Entity.Null;
                int viewers = ViewInterestLookup.Count;
                for (int v = 0; v < viewers; v++)
                {
                    ViewInterestSlot slot = ViewInterestLookup.At(v);
                    if (slot.NetworkId != key.ViewerId)
                        continue;
                    connection = slot.Connection;
                    break;
                }

                if (connection == Entity.Null)
                    continue;

                Entity rpcEntity = ecb.CreateEntity();
                ecb.AddComponent(rpcEntity, new MineVisibleRpc
                {
                    Visible = 0,
                    Sequence = key.Sequence,
                    OwnerNetworkId = key.OwnerId,
                    PlaceTime = placeTime,
                });
                ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = connection });
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
