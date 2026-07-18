using System.Collections.Generic;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client: turns server people-transport spawn notifications into local presentation entities.
    /// <para>
    /// Short-lived transport ghosts rarely Instantiates under MaxSendChunks=1 + Instantiates=1/frame
    /// before the server destroys them — so VFX must not wait on GhostSpawn. This system drains the
    /// in-process <see cref="PeopleTransportVfxBridge"/> (local host) and
    /// <see cref="PeopleTransportSpawnRpc"/> (dedicated clients), creating
    /// <see cref="PeopleTransportPresentationTag"/> entities for hybrid draw.
    /// </para>
    /// World: ClientSimulation. Group: SimulationSystemGroup.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct PeopleTransportSpawnRpcClientSystem : ISystem
    {
        /// <summary>Recent sequences already spawned — avoids double create on host (queue + RPC).</summary>
        static readonly HashSet<uint> s_SeenSequences = new HashSet<uint>();

        /// <summary>Ring of sequences for bounded prune (oldest first).</summary>
        static readonly Queue<uint> s_SeenOrder = new Queue<uint>(64);

        /// <summary>Max remembered sequences before prune.</summary>
        const int MaxSeenSequences = 128;

        /// <summary>
        /// Drains host queue and RPC entities, creating presentation floats.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Local host: in-process queue (no RTT, no GhostSpawn) ---
            while (PeopleTransportVfxBridge.TryDequeue(out var req))
                TrySpawnPresentation(ref ecb, in req);

            // --- Dedicated / remote clients: reliable spawn RPC broadcast ---
            // [NETCODE] Match MoonOrbitRpcClientSystem — do not require ReceiveRpcCommandRequest
            // (some NetCode versions strip it before SimulationSystemGroup consumers run).
            foreach (var (rpc, entity) in SystemAPI
                         .Query<RefRO<PeopleTransportSpawnRpc>>()
                         .WithEntityAccess())
            {
                var r = rpc.ValueRO;
                var req = new PeopleTransportVfxBridge.SpawnRequest
                {
                    Sequence = r.Sequence,
                    SpawnPosition = r.SpawnPosition,
                    Velocity = r.Velocity,
                    CruiseSpeed = r.CruiseSpeed,
                    Amount = r.Amount,
                    TargetShipNetworkId = r.TargetShipNetworkId,
                    SourcePlanetId = r.SourcePlanetId,
                    TargetPlanetId = r.TargetPlanetId,
                    IsLoad = r.IsLoad,
                    Team = r.Team,
                };
                TrySpawnPresentation(ref ecb, in req);
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>Creates one presentation entity if this sequence was not already handled.</summary>
        static void TrySpawnPresentation(ref EntityCommandBuffer ecb, in PeopleTransportVfxBridge.SpawnRequest req)
        {
            if (req.Sequence != 0 && !RememberSequence(req.Sequence))
                return;

            float3 pos = req.SpawnPosition;
            pos.y = 0f;
            float scale = math.max(0.2f, PeopleTransportMath.GetVisualScaleMultiplier(math.max(1f, req.Amount)) * 0.35f);
            float lifetime = PeopleTransportMath.EffectiveVisualTravelSeconds + 1.25f;

            Entity e = ecb.CreateEntity();
            ecb.AddComponent<PeopleTransportPresentationTag>(e);
            ecb.AddComponent(e, LocalTransform.FromPositionRotationScale(pos, quaternion.identity, scale));
            ecb.AddComponent(e, new PeopleTransportPresentation
            {
                Amount = req.Amount,
                Velocity = req.Velocity,
                SpawnPosition = pos,
                CruiseSpeed = req.CruiseSpeed,
                TargetShipNetworkId = req.TargetShipNetworkId,
                SourcePlanetId = req.SourcePlanetId,
                TargetPlanetId = req.TargetPlanetId,
                IsLoad = req.IsLoad,
                Team = req.Team,
                RemainingLifetime = lifetime,
                Sequence = req.Sequence,
            });
        }

        /// <summary>Returns false if sequence was already seen; otherwise records it.</summary>
        static bool RememberSequence(uint sequence)
        {
            if (!s_SeenSequences.Add(sequence))
                return false;

            s_SeenOrder.Enqueue(sequence);
            while (s_SeenOrder.Count > MaxSeenSequences)
            {
                uint old = s_SeenOrder.Dequeue();
                s_SeenSequences.Remove(old);
            }

            return true;
        }
    }
}
