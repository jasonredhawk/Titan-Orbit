using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Publishes <see cref="JoinWorldReadyCache"/> from client ghost Instantiates
    /// counts (Unity <see cref="GhostCount"/>) plus seed-hydrate / occupancy / hybrid proxy flags.
    /// World: ClientSimulation. After occupancy apply.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AsteroidOccupancyClientSystem))]
    public partial struct JoinWorldReadyPublishSystem : ISystem
    {
        EntityQuery _inGameQuery;
        EntityQuery _planetGhostQuery;
        EntityQuery _shipGhostQuery;
        float _lastSnapAckLogRealtime;

        /// <summary>Caches InGame / planet / ship ghost queries.</summary>
        public void OnCreate(ref SystemState state)
        {
            _inGameQuery = state.GetEntityQuery(ComponentType.ReadOnly<NetworkStreamInGame>());
            _planetGhostQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<PlanetTag>(),
                ComponentType.ReadOnly<GhostInstance>());
            _shipGhostQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostInstance>());
        }

        /// <summary>Writes join predicates for the loading screen / Join Team gate.</summary>
        public void OnUpdate(ref SystemState state)
        {
            bool inGame = !_inGameQuery.IsEmptyIgnoreFilter;
            int planets = _planetGhostQuery.CalculateEntityCount();
            int ships = _shipGhostQuery.CalculateEntityCount();

            int ghostServer = 0;
            int ghostReceived = 0;
            int ghostInst = 0;
            if (SystemAPI.TryGetSingleton<GhostCount>(out var ghostCount) && ghostCount.IsCreated)
            {
                ghostServer = ghostCount.GhostCountOnServer;
                ghostReceived = ghostCount.GhostCountReceivedOnClient;
                ghostInst = ghostCount.GhostCountInstantiatedOnClient;
            }

            JoinWorldReadyCache.Publish(
                inGame,
                planets,
                ships,
                ghostServer,
                ghostReceived,
                ghostInst,
                ClientJoinSettleCache.MapProxyBuildReady);

            // #region agent log
            if (inGame && planets == 0)
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now - _lastSnapAckLogRealtime >= 5f)
                {
                    _lastSnapAckLogRealtime = now;
                    uint snapTick = 0;
                    ulong snapRecv = 0;
                    if (SystemAPI.TryGetSingleton<NetworkSnapshotAck>(out var ack))
                    {
                        if (ack.LastReceivedSnapshotByLocal.IsValid)
                            snapTick = ack.LastReceivedSnapshotByLocal.TickIndexForValidTick;
                        snapRecv = ack.SnapshotPacketLoss.NumPacketsReceived;
                    }

                    TitanOrbit.Diagnostics.TitanOrbitDebugSessionLog.Write(
                        "A",
                        "JoinWorldReadyPublishSystem.OnUpdate",
                        "snap-ack",
                        "{\"planets\":" + planets +
                        ",\"recv\":" + ghostReceived +
                        ",\"ghostOnServer\":" + ghostServer +
                        ",\"snapTick\":" + snapTick +
                        ",\"snapRecv\":" + snapRecv + "}");
                }
            }
            // #endregion
        }
    }
}
