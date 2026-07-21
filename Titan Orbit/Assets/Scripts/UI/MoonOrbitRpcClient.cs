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
            // --- RequestContributedGems ---
            if (homePlanetId <= 0)
                return;

            // Local host creates RPCs on the server world, but MoonOrbitStoreSystem only handles
            // ReceiveRpcCommandRequest from clients — read the ledger directly instead.
            if (EcsGameBridge.TryGetContributedGems(homePlanetId, out float amount))
            {
                MoonOrbitClientState.SetContributedGems(amount);
                return;
            }

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new RequestContributedGemsCommand { HomePlanetId = homePlanetId });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        public static void SetWantDepositGems(bool wantDeposit)
        {
            // --- SetWantDepositGems ---
            MoonOrbitClientState.SetWantDepositGems(wantDeposit);
            ApplyWantDepositOnServer(wantDeposit);

            if (EcsGameBridge.IsLocalHost() || !EcsGameBridge.IsNetworkInGame())
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
            // --- Apply changes ---
            if (TitanOrbit.NetCode.TitanOrbitSessionManager.IsDedicatedOnlineClient)
                return;

            var server = EcsGameBridge.ServerWorld;
            if (server == null || !server.IsCreated)
                return;

            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(server, out var shipEntity))
                return;

            var em = server.EntityManager;
            var input = em.GetComponentData<ShipInput>(shipEntity);
            input.WantDepositGems = wantDeposit;
            em.SetComponentData(shipEntity, input);

            if (em.HasComponent<ShipDepositIntent>(shipEntity))
            {
                em.SetComponentData(shipEntity, new ShipDepositIntent { WantDepositGems = wantDeposit });
            }
            else
            {
                em.AddComponentData(shipEntity, new ShipDepositIntent { WantDepositGems = wantDeposit });
            }
        }

        /// <summary>
        /// Requests a ship upgrade-tree purchase (or debug-free hull select).
        /// <para>
        /// [NETCODE] Local Host applies directly on the server world — creating
        /// <see cref="SendRpcCommandRequest"/> on ServerWorld never yields
        /// <see cref="ReceiveRpcCommandRequest"/> for <see cref="MoonOrbitStoreSystem"/>, so Free
        /// tree clicks previously did nothing. Dedicated clients still SendRpc from ClientWorld.
        /// </para>
        /// </summary>
        public static void PurchaseShipUpgrade(int storePlanetId, int targetLevel, int targetBranchIndex)
        {
            // --- Validate store planet ---
            if (storePlanetId <= 0)
            {
                Debug.LogWarning(
                    "[MoonOrbit] PurchaseShipUpgrade ignored — StorePlanetId is 0 (orbit context not set).");
                return;
            }

            // --- Local Host: apply on ServerWorld immediately (mirrors PurchaseAttributeUpgrade) ---
            if (EcsGameBridge.IsLocalHost())
            {
                // Keep Shared debug flags in sync with the Inspector toggle (server reads Shared only).
                GameManager.EnsureExists();

                var server = EcsGameBridge.ServerWorld;
                if (server == null || !server.IsCreated)
                    return;

                int networkId = EcsGameBridge.GetLocalNetworkId();
                if (networkId <= 0)
                {
                    Debug.LogWarning("[MoonOrbit] PurchaseShipUpgrade ignored — local NetworkId not ready.");
                    return;
                }

                bool ok = MoonOrbitStoreSystem.TryPurchaseShipUpgradeForNetworkId(
                    server.EntityManager,
                    networkId,
                    storePlanetId,
                    targetLevel,
                    targetBranchIndex,
                    out var message);
                if (!ok && !message.IsEmpty)
                {
                    MoonOrbitClientState.SetStoreMessage(message.ToString());
                    Debug.LogWarning($"[MoonOrbit] Ship upgrade failed: {message}");
                }
                return;
            }

            // --- Dedicated / remote client: SendRpc from ClientWorld only ---
            var world = EcsGameBridge.ClientWorld;
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

        public static void PurchaseAttributeUpgrade(int attributeIndex)
        {
            // --- PurchaseAttributeUpgrade ---
            if (attributeIndex < 0 || attributeIndex > 9)
                return;

            if (EcsGameBridge.IsLocalHost())
            {
                var server = EcsGameBridge.ServerWorld;
                if (server != null && server.IsCreated)
                {
                    int networkId = EcsGameBridge.GetLocalNetworkId();
                    if (ShipAttributeUpgradeLogic.TryPurchaseForNetworkId(
                            server.EntityManager, networkId, attributeIndex, out _))
                        return;
                }
            }

            var world = EcsGameBridge.ServerWorld ?? EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new PurchaseAttributeUpgradeCommand { AttributeIndex = attributeIndex });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        public static void PurchaseStoreItem(int homePlanetId, StoreItemType itemType)
        {
            // --- PurchaseStoreItem ---
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
