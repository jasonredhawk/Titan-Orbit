using System.Collections.Generic;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server diagnostic: after TeamChoice Instantiates, confirm <see cref="GhostInstance"/>.ghostId
    /// becomes non-zero within a few ticks. Without a ghost id the hull never enters GhostSend —
    /// clients stay at Instantiates=map-meta with no ship (debug 1af271).
    /// <para>
    /// World: ServerSimulation. Runs after GhostSend so SpawnGhostJob has had a chance to assign ids.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(GhostSimulationSystemGroup))]
    [UpdateAfter(typeof(GhostSendSystem))]
    public partial class TitanOrbitServerShipGhostVerifySystem : SystemBase
    {
        /// <summary>One pending hull to verify after TeamChoice Instantiates.</summary>
        struct PendingVerify
        {
            public Entity Ship;
            public int NetworkId;
            public int FramesWaited;
            public bool LoggedOk;
        }

        /// <summary>Max ticks to wait for a non-zero ghostId before logging failure.</summary>
        const int MaxWaitFrames = 8;

        static readonly List<PendingVerify> s_Pending = new List<PendingVerify>(4);

        /// <summary>Queues a ship Instantiated by <see cref="TeamManagementSystem"/> for ghost-id verify.</summary>
        public static void Enqueue(Entity ship, int networkId)
        {
            if (ship == Entity.Null || networkId <= 0)
                return;

            s_Pending.Add(new PendingVerify
            {
                Ship = ship,
                NetworkId = networkId,
                FramesWaited = 0,
                LoggedOk = false,
            });
        }

        /// <summary>Clears pending verifies on play-mode domain reload.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_Pending.Clear();

        /// <summary>Checks queued ships for GhostInstance.ghostId assignment.</summary>
        protected override void OnUpdate()
        {
            if (s_Pending.Count == 0)
                return;

            var em = EntityManager;
            for (int i = s_Pending.Count - 1; i >= 0; i--)
            {
                var entry = s_Pending[i];
                entry.FramesWaited++;

                if (!em.Exists(entry.Ship))
                {
                    Debug.LogError(
                        "[ShipGhostVerify] TeamChoice ship entity destroyed before ghostId assign " +
                        $"(networkId={entry.NetworkId}).");
                    s_Pending.RemoveAt(i);
                    continue;
                }

                int ghostId = 0;
                bool hasGhost = em.HasComponent<GhostInstance>(entry.Ship);
                if (hasGhost)
                    ghostId = em.GetComponentData<GhostInstance>(entry.Ship).ghostId;

                if (ghostId != 0)
                {
                    if (!entry.LoggedOk)
                    {
                        Debug.Log(
                            "[ShipGhostVerify] Ship ghost ready " +
                            $"(networkId={entry.NetworkId}, entity={entry.Ship.Index}, ghostId={ghostId}, " +
                            $"frames={entry.FramesWaited}).");
                    }

                    s_Pending.RemoveAt(i);
                    continue;
                }

                if (entry.FramesWaited >= MaxWaitFrames)
                {
                    Debug.LogError(
                        "[ShipGhostVerify] Ship ghostId still 0 after " + MaxWaitFrames +
                        $" ticks — GhostSend will not serialize this hull " +
                        $"(networkId={entry.NetworkId}, entity={entry.Ship.Index}, hasGhostInstance={hasGhost}). " +
                        "Check GhostCollection ship prefab + OwnerPredicted bake.");
                    s_Pending.RemoveAt(i);
                    continue;
                }

                s_Pending[i] = entry;
            }
        }
    }
}
