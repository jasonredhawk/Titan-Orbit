using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Sends the cheap off-screen feeds. Position packets go only to connections that
    /// have the full map open, a few times a second. Scoreboard packets go to every connection
    /// on change, with a slow heartbeat, and contain no positions.
    /// Neither path is a 60 Hz ghost. Map size is not required — positions are absolute XZ.
    /// World: ServerSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ViewInterestServerSystem))]
    public partial struct ShipInterestServerSystem : ISystem
    {
        struct ShipRow
        {
            public ShipWireBlip Blip;
            public ShipWireRoster Roster;
        }

        /// <summary>Next time we are allowed to hash the scoreboard. Stops a per-tick ship walk.</summary>
        double _nextRosterScan;

        /// <summary>
        /// Builds one ship table when any connection is due, then fans packets out.
        /// Ticks with nothing due do not touch ship components.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            double now = SystemAPI.Time.ElapsedTime;
            bool needBlip = false;
            foreach (var view in SystemAPI.Query<RefRO<ConnectionViewInterest>>().WithAll<NetworkStreamInGame>())
            {
                ConnectionViewInterest v = view.ValueRO;
                if (v.FullMap != 0 && now - v.LastBlipSendTime >= ViewInterestTuning.BlipInterval)
                    needBlip = true;
            }

            bool rosterWindow = now >= _nextRosterScan;
            if (!needBlip && !rosterWindow)
                return;

            if (rosterWindow)
                _nextRosterScan = now + ViewInterestTuning.RosterMinInterval;

            var ships = new NativeList<ShipRow>(32, Allocator.Temp);
            foreach (var (owner, transform, ship, stats, mega) in SystemAPI
                         .Query<RefRO<GhostOwner>, RefRO<LocalTransform>, RefRO<ShipState>, RefRO<ShipMatchStats>, RefRO<MegaShipState>>()
                         .WithAll<ShipTag>())
            {
                int id = owner.ValueRO.NetworkId;
                if (id == 0)
                    continue;

                byte flags = 0;
                if (mega.ValueRO.IsMega)
                    flags |= ViewInterestTuning.FlagMega;
                if (ship.ValueRO.IsDead)
                    flags |= ViewInterestTuning.FlagDead;
                if (ship.ValueRO.AwaitingTeamSelection)
                    flags |= ViewInterestTuning.FlagAwaiting;

                byte team = (byte)ship.ValueRO.Team;
                byte level = (byte)math.clamp(ship.ValueRO.ShipLevel, 0, 255);
                float3 pos = transform.ValueRO.Position;
                ships.Add(new ShipRow
                {
                    Blip = new ShipWireBlip
                    {
                        NetworkId = id,
                        X = pos.x,
                        Z = pos.z,
                        Team = team,
                        Level = level,
                        Flags = flags,
                    },
                    Roster = new ShipWireRoster
                    {
                        NetworkId = id,
                        Team = team,
                        Level = level,
                        Flags = flags,
                        Kills = stats.ValueRO.Kills,
                        GemsDeposited = stats.ValueRO.GemsDeposited,
                        PeopleDelivered = stats.ValueRO.PeopleDelivered,
                        Score = stats.ValueRO.Score,
                    },
                });
            }

            int rosterHash = HashRoster(ships);
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (view, entity) in SystemAPI
                         .Query<RefRW<ConnectionViewInterest>>()
                         .WithAll<NetworkStreamInGame>()
                         .WithEntityAccess())
            {
                ref ConnectionViewInterest v = ref view.ValueRW;
                if (needBlip && v.FullMap != 0 && now - v.LastBlipSendTime >= ViewInterestTuning.BlipInterval)
                {
                    v.BlipGeneration++;
                    EmitBlips(ref ecb, entity, ships, v.BlipGeneration);
                    v.LastBlipSendTime = now;
                }

                bool rosterDue = rosterWindow && (
                    now - v.LastRosterSendTime >= ViewInterestTuning.RosterHeartbeat
                    || rosterHash != v.LastRosterHash);
                if (rosterDue)
                {
                    v.RosterGeneration++;
                    EmitRoster(ref ecb, entity, ships, v.RosterGeneration);
                    v.LastRosterSendTime = now;
                    v.LastRosterHash = rosterHash;
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            ships.Dispose();
        }

        /// <summary>Cheap mix of ids and scores so an unchanged board can wait for the heartbeat.</summary>
        static int HashRoster(NativeList<ShipRow> ships)
        {
            int hash = ships.Length;
            for (int i = 0; i < ships.Length; i++)
            {
                ShipWireRoster row = ships[i].Roster;
                hash = hash * 31 + row.NetworkId;
                hash = hash * 31 + row.Kills;
                hash = hash * 31 + row.GemsDeposited;
                hash = hash * 31 + row.PeopleDelivered;
                hash = hash * 31 + row.Score;
                hash = hash * 31 + row.Flags;
                hash = hash * 31 + row.Team;
                hash = hash * 31 + row.Level;
            }

            return hash;
        }

        /// <summary>One or more position packets. An empty match still sends a clear packet.</summary>
        static void EmitBlips(ref EntityCommandBuffer ecb, Entity connection, NativeList<ShipRow> ships, byte generation)
        {
            int packetCount = math.max(1, (ships.Length + ViewInterestTuning.BlipsPerPacket - 1) / ViewInterestTuning.BlipsPerPacket);
            if (packetCount > 255)
                packetCount = 255;

            for (int p = 0; p < packetCount; p++)
            {
                var rpc = new ShipBlipSnapshotRpc
                {
                    Generation = generation,
                    PacketIndex = (byte)p,
                    PacketCount = (byte)packetCount,
                };
                int start = p * ViewInterestTuning.BlipsPerPacket;
                int n = math.min(ViewInterestTuning.BlipsPerPacket, ships.Length - start);
                if (n < 0)
                    n = 0;
                rpc.Count = (byte)n;
                for (int i = 0; i < n; i++)
                    ShipInterestCodec.WriteBlip(ref rpc, i, ships[start + i].Blip);

                Entity rpcEntity = ecb.CreateEntity();
                ecb.AddComponent(rpcEntity, rpc);
                ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = connection });
            }
        }

        /// <summary>One or more scoreboard packets. An empty roster still sends a clear packet.</summary>
        static void EmitRoster(ref EntityCommandBuffer ecb, Entity connection, NativeList<ShipRow> ships, byte generation)
        {
            int packetCount = math.max(1, (ships.Length + ViewInterestTuning.RosterPerPacket - 1) / ViewInterestTuning.RosterPerPacket);
            if (packetCount > 255)
                packetCount = 255;

            for (int p = 0; p < packetCount; p++)
            {
                var rpc = new ShipRosterSnapshotRpc
                {
                    Generation = generation,
                    PacketIndex = (byte)p,
                    PacketCount = (byte)packetCount,
                };
                int start = p * ViewInterestTuning.RosterPerPacket;
                int n = math.min(ViewInterestTuning.RosterPerPacket, ships.Length - start);
                if (n < 0)
                    n = 0;
                rpc.Count = (byte)n;
                for (int i = 0; i < n; i++)
                    ShipInterestCodec.WriteRoster(ref rpc, i, ships[start + i].Roster);

                Entity rpcEntity = ecb.CreateEntity();
                ecb.AddComponent(rpcEntity, rpc);
                ecb.AddComponent(rpcEntity, new SendRpcCommandRequest { TargetConnection = connection });
            }
        }
    }

    /// <summary>
    /// [NETCODE] Client: copies blip and roster RPCs into <see cref="ShipInterestClientCache"/>
    /// and destroys the RPC entities. The minimap and leaderboard read the cache; this system
    /// does not spawn GameObjects.
    /// World: ClientSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ShipInterestClientSystem : ISystem
    {
        EntityQuery _blipQuery;
        EntityQuery _rosterQuery;

        /// <summary>Caches the two RPC queries so idle frames do not build a command buffer.</summary>
        public void OnCreate(ref SystemState state)
        {
            _blipQuery = state.GetEntityQuery(ComponentType.ReadOnly<ShipBlipSnapshotRpc>());
            _rosterQuery = state.GetEntityQuery(ComponentType.ReadOnly<ShipRosterSnapshotRpc>());
        }

        /// <summary>Drains both packet types. RPC entities are destroyed after the query so the iterator stays valid.</summary>
        public void OnUpdate(ref SystemState state)
        {
            bool blips = !_blipQuery.IsEmptyIgnoreFilter;
            bool roster = !_rosterQuery.IsEmptyIgnoreFilter;
            if (!blips && !roster)
                return;

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            if (blips)
            {
                foreach (var (rpc, entity) in SystemAPI.Query<RefRO<ShipBlipSnapshotRpc>>().WithEntityAccess())
                {
                    ShipInterestClientCache.PushBlipPacket(rpc.ValueRO);
                    ecb.DestroyEntity(entity);
                }
            }

            if (roster)
            {
                foreach (var (rpc, entity) in SystemAPI.Query<RefRO<ShipRosterSnapshotRpc>>().WithEntityAccess())
                {
                    ShipInterestClientCache.PushRosterPacket(rpc.ValueRO);
                    ecb.DestroyEntity(entity);
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
