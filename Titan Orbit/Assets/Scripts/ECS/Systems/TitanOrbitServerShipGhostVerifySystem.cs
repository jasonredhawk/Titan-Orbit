using System.Collections.Generic;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server diagnostic: after a hull Instantiates, confirm <see cref="GhostInstance"/>.ghostId
    /// becomes non-zero within a few sim ticks. Without a ghost id GhostSend will not replicate it.
    /// <para>
    /// World: ServerSimulation, inside <see cref="GhostSimulationSystemGroup"/> (before GhostSend,
    /// which is last in <see cref="SimulationSystemGroup"/>). An id written at the end of a tick
    /// shows up on the next tick. Off-frames are skipped — GhostSend does not assign ids then.
    /// Ticks before <see cref="GhostCollection.IsInGame"/> are skipped too: AI hulls spawn during
    /// map build, and GhostSend refuses to assign ids until a connection is in game.
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

            // Host SimulationSystemGroup also runs on off-frames. GhostSend returns before
            // it assigns ids on those frames (NumPredictedTicksExpected == 0).
            if (!SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime) ||
                networkTime.NumPredictedTicksExpected <= 0)
                return;

            // Map build spawns AI hulls many ticks before GoInGame. GhostSend bails while
            // GhostCollection.IsInGame is false, so those ticks are not a missing prefab.
            if (!SystemAPI.TryGetSingleton<GhostCollection>(out var ghostCollection) ||
                !ghostCollection.IsInGame)
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
