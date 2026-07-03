using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Game;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.UI
{
    /// <summary>Sends moon orbit store RPCs from UI to the ECS server.</summary>
    public static class MoonOrbitRpcClient
    {
        public static void RequestContributedGems(int homePlanetId)
        {
            var world = EcsGameBridge.ServerWorld ?? EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new RequestContributedGemsCommand { HomePlanetId = homePlanetId });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        public static void SetWantDepositGems(bool wantDeposit)
        {
            MoonOrbitClientState.SetWantDepositGems(wantDeposit);
            ApplyWantDepositOnServer(wantDeposit);

            if (EcsGameBridge.IsLocalHost())
                return;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new SetWantDepositGemsCommand { WantDeposit = wantDeposit });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        static void ApplyWantDepositOnServer(bool wantDeposit)
        {
            var server = EcsGameBridge.ServerWorld;
            if (server == null || !server.IsCreated)
                return;

            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(server, out var shipEntity))
                return;

            var em = server.EntityManager;
            var input = em.GetComponentData<ShipInput>(shipEntity);
            input.WantDepositGems = wantDeposit;
            em.SetComponentData(shipEntity, input);
        }

        public static void PurchaseShipUpgrade(int storePlanetId, int targetLevel, int targetBranchIndex)
        {
            var world = EcsGameBridge.ServerWorld ?? EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new PurchaseShipUpgradeCommand
            {
                StorePlanetId = storePlanetId,
                TargetLevel = targetLevel,
                TargetBranchIndex = targetBranchIndex,
            });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        public static void PurchaseStoreItem(int homePlanetId, StoreItemType itemType)
        {
            var world = EcsGameBridge.ServerWorld ?? EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new PurchaseStoreItemCommand
            {
                HomePlanetId = homePlanetId,
                ItemType = (int)itemType,
            });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }
    }
}
