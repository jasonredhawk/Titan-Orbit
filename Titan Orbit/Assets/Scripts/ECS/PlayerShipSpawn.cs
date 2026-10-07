using TitanOrbit.Core;
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
    /// Instantiates one owned starship ghost from the GhostCollection prefab.
    /// Shared by Join Team and session-ship resume so both hulls replicate the same way.
    /// </summary>
    public static class PlayerShipSpawn
    {
        /// <summary>
        /// Spawns a fresh level-1 hull at the team home rings and points input at it.
        /// Caller overwrites vitals when restoring a saved ship.
        /// </summary>
        public static bool TrySpawn(
            EntityManager em,
            Entity connection,
            int networkId,
            TeamId team,
            double orbitElapsed,
            out Entity ship,
            out float3 spawnPos)
        {
            ship = Entity.Null;
            spawnPos = float3.zero;
            if (connection == Entity.Null || networkId <= 0 || team == TeamId.None)
                return false;

            using var prefabQuery = em.CreateEntityQuery(ComponentType.ReadOnly<GamePrefabs>());
            if (prefabQuery.CalculateEntityCount() != 1)
                return false;

            GamePrefabs prefabs = prefabQuery.GetSingleton<GamePrefabs>();
            if (prefabs.Ship == Entity.Null)
                return false;

            Entity shipPrefab = ResolveGhostCollectionShipPrefab(em, prefabs.Ship, out bool usedCollection);
            if (shipPrefab == Entity.Null || !em.Exists(shipPrefab))
                return false;

            spawnPos = ShipHomeSpawnLogic.FindHomeSpawnPosition(em, team, orbitElapsed);

            // [NETCODE] ECB Instantiates would leave the hull invisible to GhostSend until playback.
            ship = em.Instantiate(shipPrefab);
            em.SetComponentData(ship, new ShipState
            {
                Health = 100f,
                MaxHealth = 100f,
                Team = team,
                ShipLevel = 1,
                ShipFamilyConfigIndex = PlanetShipFamilyAssignment.HomeFamilyConfigIndex,
                HullBulletBankIndex = PlanetShipFamilyAssignment.DefaultBulletBankIndex,
                GemCapacity = 50f,
                CurrentEnergy = 50f,
                MaxEnergy = 50f,
                PeopleCapacity = 10,
                AwaitingTeamSelection = false,
            });
            em.SetComponentData(ship, LocalTransform.FromPosition(spawnPos));

            if (em.HasComponent<GhostOwner>(ship))
                em.SetComponentData(ship, new GhostOwner { NetworkId = networkId });
            else
                em.AddComponentData(ship, new GhostOwner { NetworkId = networkId });

            if (!em.HasComponent<ShipAttributeUpgradeState>(ship))
                em.AddComponentData(ship, new ShipAttributeUpgradeState());

            var commandTarget = new CommandTarget { targetEntity = ship };
            if (em.HasComponent<CommandTarget>(connection))
                em.SetComponentData(connection, commandTarget);
            else
                em.AddComponentData(connection, commandTarget);

            // GhostConnectionPosition often stays at origin until the next tick — first
            // ship snapshot can lose to far map resends even with FirstSend bias.
            if (em.HasComponent<GhostConnectionPosition>(connection))
            {
                em.SetComponentData(connection, new GhostConnectionPosition
                {
                    Position = spawnPos,
                    Rotation = quaternion.identity,
                });
            }
            else
            {
                em.AddComponentData(connection, new GhostConnectionPosition
                {
                    Position = spawnPos,
                    Rotation = quaternion.identity,
                });
            }

            TitanOrbitGhostSendGrace.ArmShipSpawnGrace();
            TitanOrbitServerShipGhostVerifySystem.Enqueue(ship, networkId);

            int ghostId = 0;
            if (em.HasComponent<GhostInstance>(ship))
                ghostId = em.GetComponentData<GhostInstance>(ship).ghostId;

            Debug.Log(
                $"[PlayerShipSpawn] Spawned ship for networkId={networkId} team={team} at {spawnPos} " +
                $"(collectionPrefab={usedCollection}, ghostId={ghostId}).");
            return true;
        }

        /// <summary>
        /// Spawns an AI hull from the same ghost prefab as a player ship.
        /// Does not attach a connection or <see cref="CommandTarget"/> — the server brain
        /// writes <see cref="ShipInput"/> itself.
        /// </summary>
        public static bool TrySpawnBot(
            EntityManager em,
            int networkId,
            TeamId team,
            double orbitElapsed,
            out Entity ship,
            out float3 spawnPos)
        {
            ship = Entity.Null;
            spawnPos = float3.zero;
            if (!BotShipIds.IsBot(networkId) || team == TeamId.None)
                return false;

            using var prefabQuery = em.CreateEntityQuery(ComponentType.ReadOnly<GamePrefabs>());
            if (prefabQuery.CalculateEntityCount() != 1)
                return false;

            GamePrefabs prefabs = prefabQuery.GetSingleton<GamePrefabs>();
            if (prefabs.Ship == Entity.Null)
                return false;

            Entity shipPrefab = ResolveGhostCollectionShipPrefab(em, prefabs.Ship, out bool usedCollection);
            if (shipPrefab == Entity.Null || !em.Exists(shipPrefab))
                return false;

            if (!ShipHomeSpawnLogic.TryFindHomeSpawnPosition(em, team, orbitElapsed, out spawnPos))
                return false;

            ship = em.Instantiate(shipPrefab);
            em.SetComponentData(ship, new ShipState
            {
                Health = 100f,
                MaxHealth = 100f,
                Team = team,
                ShipLevel = 1,
                ShipFamilyConfigIndex = PlanetShipFamilyAssignment.HomeFamilyConfigIndex,
                HullBulletBankIndex = PlanetShipFamilyAssignment.DefaultBulletBankIndex,
                GemCapacity = 50f,
                CurrentEnergy = 50f,
                MaxEnergy = 50f,
                PeopleCapacity = 10,
                AwaitingTeamSelection = false,
            });
            em.SetComponentData(ship, LocalTransform.FromPosition(spawnPos));

            if (em.HasComponent<GhostOwner>(ship))
                em.SetComponentData(ship, new GhostOwner { NetworkId = networkId });
            else
                em.AddComponentData(ship, new GhostOwner { NetworkId = networkId });

            if (!em.HasComponent<ShipAttributeUpgradeState>(ship))
                em.AddComponentData(ship, new ShipAttributeUpgradeState());

            if (!em.HasComponent<BotShipTag>(ship))
                em.AddComponentData(ship, new BotShipTag());
            if (!em.HasComponent<BotShipBrain>(ship))
                em.AddComponentData(ship, new BotShipBrain());

            TitanOrbitGhostSendGrace.ArmShipSpawnGrace();
            TitanOrbitServerShipGhostVerifySystem.Enqueue(ship, networkId);

            int ghostId = 0;
            if (em.HasComponent<GhostInstance>(ship))
                ghostId = em.GetComponentData<GhostInstance>(ship).ghostId;

            Debug.Log(
                $"[BotShipSpawn] Spawned AI ship networkId={networkId} team={team} at {spawnPos} " +
                $"(collectionPrefab={usedCollection}, ghostId={ghostId}).");
            return true;
        }

        /// <summary>
        /// Finds the GhostCollection entry for the ship prefab so SpawnGhostJob can assign a ghost id.
        /// Prefers entity match, then <see cref="GhostType"/> match, else first <see cref="ShipTag"/>.
        /// </summary>
        static Entity ResolveGhostCollectionShipPrefab(
            EntityManager em,
            Entity gamePrefabsShip,
            out bool usedCollection)
        {
            usedCollection = false;
            Entity shipTagFallback = Entity.Null;

            using var collectionQuery = em.CreateEntityQuery(ComponentType.ReadOnly<GhostCollection>());
            if (collectionQuery.IsEmptyIgnoreFilter)
                return gamePrefabsShip;

            Entity collectionEntity = collectionQuery.GetSingletonEntity();
            if (!em.HasBuffer<GhostCollectionPrefab>(collectionEntity))
                return gamePrefabsShip;

            GhostType targetType = default;
            bool hasTargetType = gamePrefabsShip != Entity.Null && em.HasComponent<GhostType>(gamePrefabsShip);
            if (hasTargetType)
                targetType = em.GetComponentData<GhostType>(gamePrefabsShip);

            var buffer = em.GetBuffer<GhostCollectionPrefab>(collectionEntity, isReadOnly: true);
            for (int i = 0; i < buffer.Length; i++)
            {
                Entity candidate = buffer[i].GhostPrefab;
                if (candidate == Entity.Null || !em.Exists(candidate))
                    continue;

                if (candidate == gamePrefabsShip)
                {
                    usedCollection = true;
                    return candidate;
                }

                if (hasTargetType &&
                    em.HasComponent<GhostType>(candidate) &&
                    em.GetComponentData<GhostType>(candidate) == targetType)
                {
                    usedCollection = true;
                    return candidate;
                }

                if (shipTagFallback == Entity.Null && em.HasComponent<ShipTag>(candidate))
                    shipTagFallback = candidate;
            }

            if (shipTagFallback != Entity.Null)
            {
                usedCollection = true;
                return shipTagFallback;
            }

            return gamePrefabsShip;
        }
    }
}
