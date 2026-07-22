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
    /// <summary>
    /// Client glue for moon-orbit store actions: contributed-gems queries, deposit toggle,
    /// and ship upgrade-tree purchases. UI calls these static helpers; they either write the
    /// Local Host <c>ServerWorld</c> directly or send NetCode RPCs from <c>ClientWorld</c>.
    /// Deposit toggle also updates <see cref="MoonOrbitClientState"/> immediately so local SFX
    /// can metronome without waiting for ghost replication.
    /// </summary>
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

        /// <summary>
        /// Toggles gem auto-deposit for the local ship.
        /// Updates the immediate client mirror (<see cref="MoonOrbitClientState"/>), writes the
        /// local ClientWorld ghost so UI/SFX do not wait on an RPC round-trip, applies on the
        /// server world for Local Host, and sends <see cref="SetWantDepositGemsCommand"/> for
        /// dedicated online clients.
        /// </summary>
        /// <param name="wantDeposit">True to drain ship cargo into the docked moon's planet pool.</param>
        public static void SetWantDepositGems(bool wantDeposit)
        {
            // --- SetWantDepositGems ---
            // [TITAN-ORBIT] Client mirror is the authoritative "I want to deposit" signal for local
            // SFX/UI this frame — ghost ShipDepositIntent can lag a full RPC + snapshot.
            MoonOrbitClientState.SetWantDepositGems(wantDeposit);

            // Predict on the local ClientWorld hull so readers of ShipDepositIntent see the toggle now.
            ApplyWantDepositOnWorld(EcsGameBridge.ClientWorld, wantDeposit);

            // Local Host also owns ServerWorld — write intent there so GemDepositSystem runs immediately.
            ApplyWantDepositOnServer(wantDeposit);

            // Dedicated online client: tell the remote server via RPC (Local Host already wrote server).
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

        /// <summary>
        /// Writes deposit intent onto the Local Host server ship. No-op for dedicated online clients
        /// (they have no ServerWorld gameplay authority).
        /// </summary>
        static void ApplyWantDepositOnServer(bool wantDeposit)
        {
            // --- Apply on server world (Local Host only) ---
            if (TitanOrbit.NetCode.TitanOrbitSessionManager.IsDedicatedOnlineClient)
                return;

            ApplyWantDepositOnWorld(EcsGameBridge.ServerWorld, wantDeposit);
        }

        /// <summary>
        /// Sets <see cref="ShipInput.WantDepositGems"/> and <see cref="ShipDepositIntent"/> on the
        /// local player's ship in the given world (ClientWorld prediction and/or ServerWorld host).
        /// </summary>
        static void ApplyWantDepositOnWorld(World world, bool wantDeposit)
        {
            // --- Apply deposit flag on one ECS world ---
            if (world == null || !world.IsCreated)
                return;

            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out var shipEntity))
                return;

            var em = world.EntityManager;

            // Keep ShipInput in sync — GemDepositSystem may read it before intent on some paths.
            if (em.HasComponent<ShipInput>(shipEntity))
            {
                var input = em.GetComponentData<ShipInput>(shipEntity);
                input.WantDepositGems = wantDeposit;
                em.SetComponentData(shipEntity, input);
            }

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
