using System.Collections.Generic;
using TitanOrbit.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [NETCODE] Bit helpers for <see cref="AsteroidOccupancyRpc"/> (1024 slots).
    /// 1 = asteroid at that blueprint index is currently alive on the server.
    /// </summary>
    public static class AsteroidOccupancyBits
    {
        /// <summary>Max slots packed in the RPC (16 ulongs).</summary>
        public const int MaxSlots = 1024;

        /// <summary>Pose epsilon matching respawn slot identity.</summary>
        public const float SlotEpsilon = 0.75f;

        /// <summary>Sets bit <paramref name="slot"/> alive (1) or dead (0).</summary>
        public static void SetAlive(ref AsteroidOccupancyRpc rpc, int slot, bool alive)
        {
            if (slot < 0 || slot >= MaxSlots)
                return;
            int word = slot / 64;
            int bit = slot % 64;
            ulong mask = 1UL << bit;
            ref ulong w = ref Word(ref rpc, word);
            if (alive)
                w |= mask;
            else
                w &= ~mask;
        }

        /// <summary>True when the slot is alive, or out of range (treat as alive).</summary>
        public static bool IsAlive(in AsteroidOccupancyRpc rpc, int slot)
        {
            if (slot < 0 || slot >= rpc.SlotCount)
                return true;
            if (slot >= MaxSlots)
                return true;
            int word = slot / 64;
            int bit = slot % 64;
            ulong mask = 1UL << bit;
            return (ReadWord(in rpc, word) & mask) != 0;
        }

        /// <summary>Counts zero-bits in [0, SlotCount).</summary>
        public static int CountDead(in AsteroidOccupancyRpc rpc)
        {
            int dead = 0;
            int n = math.min(rpc.SlotCount, MaxSlots);
            for (int i = 0; i < n; i++)
            {
                if (!IsAlive(in rpc, i))
                    dead++;
            }

            return dead;
        }

        static ref ulong Word(ref AsteroidOccupancyRpc rpc, int word)
        {
            switch (word)
            {
                case 0: return ref rpc.Bits0;
                case 1: return ref rpc.Bits1;
                case 2: return ref rpc.Bits2;
                case 3: return ref rpc.Bits3;
                case 4: return ref rpc.Bits4;
                case 5: return ref rpc.Bits5;
                case 6: return ref rpc.Bits6;
                case 7: return ref rpc.Bits7;
                case 8: return ref rpc.Bits8;
                case 9: return ref rpc.Bits9;
                case 10: return ref rpc.Bits10;
                case 11: return ref rpc.Bits11;
                case 12: return ref rpc.Bits12;
                case 13: return ref rpc.Bits13;
                case 14: return ref rpc.Bits14;
                default: return ref rpc.Bits15;
            }
        }

        static ulong ReadWord(in AsteroidOccupancyRpc rpc, int word)
        {
            switch (word)
            {
                case 0: return rpc.Bits0;
                case 1: return rpc.Bits1;
                case 2: return rpc.Bits2;
                case 3: return rpc.Bits3;
                case 4: return rpc.Bits4;
                case 5: return rpc.Bits5;
                case 6: return rpc.Bits6;
                case 7: return rpc.Bits7;
                case 8: return rpc.Bits8;
                case 9: return rpc.Bits9;
                case 10: return rpc.Bits10;
                case 11: return rpc.Bits11;
                case 12: return rpc.Bits12;
                case 13: return rpc.Bits13;
                case 14: return rpc.Bits14;
                default: return rpc.Bits15;
            }
        }
    }

    /// <summary>Inbound occupancy waiting for seed-hydrate to finish Instantiates.</summary>
    static class AsteroidOccupancyPending
    {
        public static bool Has;
        public static AsteroidOccupancyRpc Rpc;

        public static void Clear()
        {
            Has = false;
            Rpc = default;
        }
    }

    /// <summary>
    /// Server: packs live-asteroid occupancy from the same seed blueprint the client hydrates,
    /// and sends <see cref="AsteroidOccupancyRpc"/> once per connection (pre-InGame OK).
    /// World: ServerSimulation.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct AsteroidOccupancyServerCatchUpSystem : ISystem
    {
        EntityQuery _liveAsteroidQuery;
        EntityQuery _pendingConnQuery;
        AsteroidOccupancyRpc _cachedRpc;
        bool _hasCachedRpc;
        double _cachedAtElapsed;

        /// <summary>Caches live-asteroid and untagged-connection queries.</summary>
        public void OnCreate(ref SystemState state)
        {
            _liveAsteroidQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<AsteroidTag>(),
                ComponentType.ReadOnly<AsteroidState>(),
                ComponentType.ReadOnly<LocalTransform>());
            _pendingConnQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<NetworkId>(),
                ComponentType.ReadOnly<NetworkStreamConnection>(),
                ComponentType.Exclude<AsteroidOccupancySent>());
            state.RequireForUpdate<MapStateSingleton>();
        }

        /// <summary>Queues occupancy to each new connection that can receive gameplay RPCs.</summary>
        public void OnUpdate(ref SystemState state)
        {
            if (_pendingConnQuery.IsEmptyIgnoreFilter)
                return;

            double now = SystemAPI.Time.ElapsedTime;
            if (!_hasCachedRpc || now - _cachedAtElapsed > 1.0)
            {
                if (!TryBuildRpc(ref state, out _cachedRpc))
                    return;
                _hasCachedRpc = true;
                _cachedAtElapsed = now;
            }

            var rpc = _cachedRpc;

            var connections = _pendingConnQuery.ToEntityArray(Allocator.Temp);
            var connData = _pendingConnQuery.ToComponentDataArray<NetworkStreamConnection>(Allocator.Temp);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            for (int i = 0; i < connections.Length; i++)
            {
                if (!ConnectionCanReceiveGameplayRpc(connData[i]))
                    continue;

                Entity send = ecb.CreateEntity();
                ecb.AddComponent(send, rpc);
                ecb.AddComponent(send, new SendRpcCommandRequest { TargetConnection = connections[i] });
                ecb.AddComponent<AsteroidOccupancySent>(connections[i]);
            }

            ecb.Playback(state.EntityManager);
            connections.Dispose();
            connData.Dispose();
            ecb.Dispose();
        }

        /// <summary>
        /// Rebuilds the seed blueprint and marks slots whose pose still has a live server rock.
        /// </summary>
        bool TryBuildRpc(ref SystemState state, out AsteroidOccupancyRpc rpc)
        {
            rpc = default;
            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var map) || !map.LoadingComplete)
                return false;

            uint seed = map.BlueprintSeed != 0 ? (uint)map.BlueprintSeed : 0;
            if (seed == 0)
                return false;

            MapGenerationConfig config = MapGenerationConfigUtility.Default();
            if (SystemAPI.TryGetSingleton<MapGenerationConfig>(out var cfg))
                config = cfg;
            config.Seed = (int)seed;

            var settings = AsteroidSettingsCache.ResolveOrDefault();
            settings.ClampValues();
            var asteroidBody = new MapGenerationLogic.AsteroidBodyTuning
            {
                MinSize = settings.MinSize,
                MaxSize = settings.MaxSize,
                HealthPerSize = settings.HealthPerSize,
                GemsPerSize = settings.GemsPerSize,
                VisualScaleAtMinSize = settings.VisualScaleAtMinSize,
                VisualScaleAtMaxSize = settings.VisualScaleAtMaxSize,
            };

            MapLayoutBlueprint.Build(
                config,
                seed,
                asteroidBody,
                Allocator.Temp,
                out _,
                out var bodies,
                out var claims);

            var livePos = new NativeList<float3>(64, Allocator.Temp);
            var liveStates = _liveAsteroidQuery.ToComponentDataArray<AsteroidState>(Allocator.Temp);
            var liveXf = _liveAsteroidQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            for (int i = 0; i < liveStates.Length; i++)
            {
                if (liveStates[i].IsDestroyed)
                    continue;
                float3 p = liveXf[i].Position;
                p.y = 0f;
                livePos.Add(p);
            }

            rpc.MatchSeed = seed;
            int slot = 0;
            int dead = 0;
            float epsSq = AsteroidOccupancyBits.SlotEpsilon * AsteroidOccupancyBits.SlotEpsilon;
            for (int i = 0; i < bodies.Length && slot < AsteroidOccupancyBits.MaxSlots; i++)
            {
                if (bodies[i].EntityKind != 3)
                    continue;

                float3 want = bodies[i].Position;
                want.y = 0f;
                bool alive = false;
                for (int p = 0; p < livePos.Length; p++)
                {
                    if (math.distancesq(livePos[p], want) <= epsSq)
                    {
                        alive = true;
                        break;
                    }
                }

                AsteroidOccupancyBits.SetAlive(ref rpc, slot, alive);
                if (!alive)
                    dead++;
                slot++;
            }

            rpc.SlotCount = slot;
            livePos.Dispose();
            liveStates.Dispose();
            liveXf.Dispose();
            bodies.Dispose();
            claims.Dispose();

            if (slot <= 0)
                return false;

            Debug.Log("[AsteroidOccupancy] Server packed slots=" + slot + " dead=" + dead);
            return true;
        }

        /// <summary>
        /// Mirrors MapSessionMetaCache.ConnectionCanReceiveGameplayRpc (ECS cannot reference NetCode).
        /// </summary>
        static bool ConnectionCanReceiveGameplayRpc(in NetworkStreamConnection conn)
        {
            var connectionState = conn.CurrentState;
            if (connectionState == ConnectionState.State.Handshake ||
                connectionState == ConnectionState.State.Approval)
                return false;
            return connectionState == ConnectionState.State.Connected;
        }
    }

    /// <summary>
    /// Client: consumes occupancy RPCs, then SoftDestroys seed-hydrated rocks whose live bit is 0.
    /// World: ClientSimulation. After <see cref="ClientMapHydrateSystem"/>.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ClientMapHydrateSystem))]
    public partial struct AsteroidOccupancyClientSystem : ISystem
    {
        static readonly List<Entity> RegistryScratch = new List<Entity>(512);

        /// <summary>No RequireForUpdate — occupancy may arrive before hydrate finishes.</summary>
        public void OnCreate(ref SystemState state)
        {
        }

        /// <summary>Latches inbound RPCs, then applies once local asteroids exist.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var destroyEcb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (rpc, reqEntity) in SystemAPI.Query<RefRO<AsteroidOccupancyRpc>>()
                         .WithAll<ReceiveRpcCommandRequest>().WithEntityAccess())
            {
                var payload = rpc.ValueRO;
                destroyEcb.DestroyEntity(reqEntity);

                if (ClientMapHydrateCache.HasFullRecipe &&
                    payload.MatchSeed != 0 &&
                    payload.MatchSeed != ClientMapHydrateCache.MatchSeed)
                {
                    Debug.LogWarning(
                        "[AsteroidOccupancy] Ignored occupancy seed=" + payload.MatchSeed +
                        " (latched " + ClientMapHydrateCache.MatchSeed + ")");
                    continue;
                }

                AsteroidOccupancyPending.Has = true;
                AsteroidOccupancyPending.Rpc = payload;
                int dead = AsteroidOccupancyBits.CountDead(in payload);
                JoinWorldReadyCache.MarkOccupancyReceived(payload.SlotCount, dead);
                Debug.Log(
                    "[AsteroidOccupancy] Client received slots=" + payload.SlotCount +
                    " dead=" + dead);
            }

            destroyEcb.Playback(em);
            destroyEcb.Dispose();

            if (JoinWorldReadyCache.OccupancyApplied)
                return;
            if (!AsteroidOccupancyPending.Has)
                return;
            if (!ClientMapHydrateCache.IsComplete)
                return;
            if (ClientJoinSettleCache.ShouldSkipMapBodyQueries)
                return;

            ApplyPending(em);
        }

        /// <summary>SoftDestroys local rocks whose occupancy bit is 0. Registry walk — no asteroid ToEntityArray.</summary>
        static void ApplyPending(EntityManager em)
        {
            var rpc = AsteroidOccupancyPending.Rpc;
            AsteroidClientEntityRegistry.CopyLive(RegistryScratch);
            int culled = 0;
            for (int i = 0; i < RegistryScratch.Count; i++)
            {
                Entity e = RegistryScratch[i];
                if (!em.Exists(e) || !em.HasComponent<ClientAsteroidLayoutSlot>(e))
                    continue;

                int slot = em.GetComponentData<ClientAsteroidLayoutSlot>(e).Slot;
                if (AsteroidOccupancyBits.IsAlive(in rpc, slot))
                    continue;

                ClientLocalAsteroidCombatSync.SoftDestroyLocalAsteroidEntity(em, e);
                culled++;
            }

            AsteroidOccupancyPending.Clear();
            JoinWorldReadyCache.MarkOccupancyApplied();
            Debug.Log("[AsteroidOccupancy] Applied dead culls=" + culled +
                      " / slots=" + rpc.SlotCount);
        }
    }
}
