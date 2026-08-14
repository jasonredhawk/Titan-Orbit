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
    /// ship upgrade-tree purchases, drones/support items, extra components, card spin/take,
    /// and loadout remove. UI calls these static helpers; they either write the Local Host
    /// <c>ServerWorld</c> directly or send NetCode RPCs from <c>ClientWorld</c>.
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

        /// <summary>Orbit Menu Damage vs Heal. Writes ghosted loadout on Local Host and RPCs dedicated clients.</summary>
        public static void SetHealingBullets(bool healingActive)
        {
            ApplyHealingBulletsOnWorld(EcsGameBridge.ClientWorld, healingActive);
            if (!TitanOrbit.NetCode.TitanOrbitSessionManager.IsDedicatedOnlineClient)
                ApplyHealingBulletsOnWorld(EcsGameBridge.ServerWorld, healingActive);

            if (EcsGameBridge.IsLocalHost() || !EcsGameBridge.IsNetworkInGame())
                return;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new SetHealingBulletsCommand { HealingActive = healingActive });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        static void ApplyHealingBulletsOnWorld(World world, bool healingActive)
        {
            if (world == null || !world.IsCreated)
                return;
            if (!EcsGameBridge.TryGetLocalShipEntityTagged(world, out var shipEntity))
                return;
            var em = world.EntityManager;
            if (!em.HasComponent<ShipLoadoutState>(shipEntity))
                return;
            var loadout = em.GetComponentData<ShipLoadoutState>(shipEntity);
            loadout.HealingBulletsActive = healingActive;
            em.SetComponentData(shipEntity, loadout);
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
        /// Uses tagged ship lookup so Instantiates backlog cannot skip the intent write.
        /// </summary>
        static void ApplyWantDepositOnWorld(World world, bool wantDeposit)
        {
            // --- Apply deposit flag on one ECS world ---
            if (world == null || !world.IsCreated)
                return;

            // [TITAN-ORBIT] Prefer LocalPlayerShipTag — TryGetLocalShipEntity is gated off during
            // GhostSpawnBacklog and previously left server ShipDepositIntent stuck false on Local Host.
            if (!EcsGameBridge.TryGetLocalShipEntityTagged(world, out var shipEntity))
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

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new PurchaseAttributeUpgradeCommand { AttributeIndex = attributeIndex });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>
        /// Purchases a drone / rocket / mine pack at the home planet store.
        /// Local Host applies on ServerWorld; dedicated clients SendRpc from ClientWorld only.
        /// </summary>
        public static void PurchaseStoreItem(int homePlanetId, StoreItemType itemType)
        {
            // --- PurchaseStoreItem ---
            if (homePlanetId <= 0)
                return;

            if (EcsGameBridge.IsLocalHost())
            {
                var server = EcsGameBridge.ServerWorld;
                if (server == null || !server.IsCreated)
                    return;

                int networkId = EcsGameBridge.GetLocalNetworkId();
                if (networkId <= 0)
                    return;

                bool ok = MoonOrbitStoreSystem.TryPurchaseStoreItemForNetworkId(
                    server.EntityManager, networkId, homePlanetId, (int)itemType, out var message);
                if (!message.IsEmpty)
                    MoonOrbitClientState.SetStoreMessage(message.ToString());
                if (!ok)
                    Debug.LogWarning($"[MoonOrbit] Store item failed: {message}");
                RequestContributedGems(homePlanetId);
                return;
            }

            var world = EcsGameBridge.ClientWorld;
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

        /// <summary>
        /// Purchases a ship-family extra component by stable id into an empty equipment slot.
        /// </summary>
        public static void PurchaseStoreComponent(int homePlanetId, string componentId)
        {
            // --- PurchaseStoreComponent ---
            if (homePlanetId <= 0 || string.IsNullOrWhiteSpace(componentId))
                return;

            if (EcsGameBridge.IsLocalHost())
            {
                var server = EcsGameBridge.ServerWorld;
                if (server == null || !server.IsCreated)
                    return;

                int networkId = EcsGameBridge.GetLocalNetworkId();
                if (networkId <= 0)
                    return;

                bool ok = MoonOrbitStoreSystem.TryPurchaseStoreComponentForNetworkId(
                    server.EntityManager, networkId, homePlanetId, componentId, out var message);
                if (!message.IsEmpty)
                    MoonOrbitClientState.SetStoreMessage(message.ToString());
                if (!ok)
                    Debug.LogWarning($"[MoonOrbit] Component purchase failed: {message}");
                RequestContributedGems(homePlanetId);
                return;
            }

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new PurchaseStoreComponentCommand
            {
                HomePlanetId = homePlanetId,
                ComponentId = componentId,
            });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>
        /// Pays for a card spin at the docked store planet and fills three offer slots.
        /// </summary>
        public static void CardSpin(int storePlanetId)
        {
            // --- CardSpin ---
            if (storePlanetId <= 0)
                return;

            if (EcsGameBridge.IsLocalHost())
            {
                var server = EcsGameBridge.ServerWorld;
                if (server == null || !server.IsCreated)
                    return;

                int networkId = EcsGameBridge.GetLocalNetworkId();
                if (networkId <= 0)
                    return;

                bool ok = MoonOrbitStoreSystem.TryCardSpinForNetworkId(
                    server.EntityManager,
                    networkId,
                    storePlanetId,
                    out var a,
                    out var b,
                    out var c,
                    out var message);
                if (!message.IsEmpty)
                    MoonOrbitClientState.SetStoreMessage(message.ToString());
                if (ok)
                {
                    MoonOrbitClientState.SetSpinOffer(
                        a.ToString(), b.ToString(), c.ToString(), success: true);
                }
                else
                    Debug.LogWarning($"[MoonOrbit] Card spin failed: {message}");
                RequestContributedGems(OrbitStationEcsContext.HomePlanetId);
                return;
            }

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new CardSpinCommand { StorePlanetId = storePlanetId });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>
        /// Takes one card from the current spin offer into an empty card slot (spin already paid).
        /// </summary>
        public static void TakeSpinCard(int storePlanetId, string cardId)
        {
            // --- TakeSpinCard ---
            if (storePlanetId <= 0 || string.IsNullOrEmpty(cardId))
                return;

            if (EcsGameBridge.IsLocalHost())
            {
                var server = EcsGameBridge.ServerWorld;
                if (server == null || !server.IsCreated)
                    return;

                int networkId = EcsGameBridge.GetLocalNetworkId();
                if (networkId <= 0)
                    return;

                bool ok = MoonOrbitStoreSystem.TryTakeSpinCardForNetworkId(
                    server.EntityManager, networkId, storePlanetId, cardId, out var message);
                if (!message.IsEmpty)
                    MoonOrbitClientState.SetStoreMessage(message.ToString());
                if (ok)
                    MoonOrbitClientState.SetSpinOffer(string.Empty, string.Empty, string.Empty, success: false);
                if (!ok)
                    Debug.LogWarning($"[MoonOrbit] Take card failed: {message}");
                return;
            }

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new TakeSpinCardCommand
            {
                StorePlanetId = storePlanetId,
                CardId = cardId,
            });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>Removes an equipped upgrade card at the given buffer index (free discard).</summary>
        public static void RemoveEquippedCard(int slotIndex)
        {
            // --- RemoveEquippedCard ---
            if (slotIndex < 0)
                return;

            if (EcsGameBridge.IsLocalHost())
            {
                var server = EcsGameBridge.ServerWorld;
                if (server == null || !server.IsCreated)
                    return;

                int networkId = EcsGameBridge.GetLocalNetworkId();
                if (networkId <= 0)
                    return;

                bool ok = MoonOrbitStoreSystem.TryRemoveEquippedCardForNetworkId(
                    server.EntityManager, networkId, slotIndex, out var message);
                if (!message.IsEmpty)
                    MoonOrbitClientState.SetStoreMessage(message.ToString());
                if (!ok)
                    Debug.LogWarning($"[MoonOrbit] Remove card failed: {message}");
                return;
            }

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new RemoveEquippedCardCommand { SlotIndex = slotIndex });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }

        /// <summary>Removes an equipped store item / component at the given buffer index (free discard).</summary>
        public static void RemoveEquippedEquipment(int slotIndex)
        {
            // --- RemoveEquippedEquipment ---
            if (slotIndex < 0)
                return;

            if (EcsGameBridge.IsLocalHost())
            {
                var server = EcsGameBridge.ServerWorld;
                if (server == null || !server.IsCreated)
                    return;

                int networkId = EcsGameBridge.GetLocalNetworkId();
                if (networkId <= 0)
                    return;

                bool ok = MoonOrbitStoreSystem.TryRemoveEquippedEquipmentForNetworkId(
                    server.EntityManager, networkId, slotIndex, out var message);
                if (!message.IsEmpty)
                    MoonOrbitClientState.SetStoreMessage(message.ToString());
                if (!ok)
                    Debug.LogWarning($"[MoonOrbit] Remove equipment failed: {message}");
                return;
            }

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;
            var entity = em.CreateEntity();
            em.AddComponentData(entity, new RemoveEquippedEquipmentCommand { SlotIndex = slotIndex });
            em.AddComponentData(entity, new SendRpcCommandRequest { TargetConnection = Entity.Null });
        }
    }
}
