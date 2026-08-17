using System;
using System.Collections.Generic;
using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server RPC handlers for moon orbit store: contributed gem balance queries, deposit intent,
    /// ship level upgrades, drones/support items, extra components, card spin/take, and loadout remove.
    /// Validates team, planet id, and contributed gem balances before mutating ship/planet state.
    /// [TITAN-ORBIT] Drones, extra components, and card spins sell at
    /// <c>min(ship level, docked planet level)</c> — a high-level ship on a low-level moon
    /// cannot buy max-tier gear there.
    /// Local Host also calls the public <c>Try*ForNetworkId</c> helpers directly (SendRpc on
    /// ServerWorld never becomes <see cref="ReceiveRpcCommandRequest"/>).
    /// Paired with <see cref="MoonOrbitRpcClientSystem"/> on the client.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MoonOrbitStoreSystem : ISystem
    {
        /// <summary>
        /// [TITAN-ORBIT] Server-only pending card spin offers keyed by GhostOwner.NetworkId.
        /// Client never trusts this — take-card RPCs must match these ids.
        /// </summary>
        static readonly Dictionary<int, PendingCardSpinOffer> s_pendingCardSpins =
            new Dictionary<int, PendingCardSpinOffer>();

        /// <summary>Three stable card ids waiting for the player to pick one after a paid spin.</summary>
        sealed class PendingCardSpinOffer
        {
            public string[] CardIds = new string[3];
        }

        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // --- Contributed gems balance query ---
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<RequestContributedGemsCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(state.EntityManager, req.ValueRO.SourceConnection);
                float amount = GetContributedGemsForTeam(state.EntityManager, networkId, cmd.ValueRO.HomePlanetId);
                SendContributedGemsResult(ref ecb, req.ValueRO.SourceConnection, amount);
                ecb.DestroyEntity(entity);
            }

            // --- Deposit toggle RPC (orbit station UI) ---
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<SetWantDepositGemsCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(state.EntityManager, req.ValueRO.SourceConnection);
                if (TryGetOwnedShip(state.EntityManager, networkId, out var shipEntity))
                {
                    var input = state.EntityManager.GetComponentData<ShipInput>(shipEntity);
                    input.WantDepositGems = cmd.ValueRO.WantDeposit;
                    state.EntityManager.SetComponentData(shipEntity, input);

                    if (state.EntityManager.HasComponent<ShipDepositIntent>(shipEntity))
                    {
                        state.EntityManager.SetComponentData(shipEntity, new ShipDepositIntent
                        {
                            WantDepositGems = cmd.ValueRO.WantDeposit,
                        });
                    }
                    else
                    {
                        state.EntityManager.AddComponentData(shipEntity, new ShipDepositIntent
                        {
                            WantDepositGems = cmd.ValueRO.WantDeposit,
                        });
                    }
                }

                ecb.DestroyEntity(entity);
            }

            // --- Damage vs Heal toggle (orbit station UI) ---
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<SetHealingBulletsCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(state.EntityManager, req.ValueRO.SourceConnection);
                TrySetHealingBulletsForNetworkId(state.EntityManager, networkId, cmd.ValueRO.HealingActive);
                ecb.DestroyEntity(entity);
            }

            // --- Ship level / branch upgrade purchase ---
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<PurchaseShipUpgradeCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(state.EntityManager, req.ValueRO.SourceConnection);
                bool ok = TryPurchaseShipUpgradeForNetworkId(
                    state.EntityManager,
                    networkId,
                    cmd.ValueRO.StorePlanetId,
                    cmd.ValueRO.TargetLevel,
                    cmd.ValueRO.TargetBranchIndex,
                    out var message);
                SendStoreResult(ref ecb, req.ValueRO.SourceConnection, ok, message);
                ecb.DestroyEntity(entity);
            }

            // --- Equipment / consumable store purchase (drones, rockets, mines) ---
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<PurchaseStoreItemCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(state.EntityManager, req.ValueRO.SourceConnection);
                bool ok = TryPurchaseStoreItemForNetworkId(
                    state.EntityManager,
                    networkId,
                    cmd.ValueRO.HomePlanetId,
                    cmd.ValueRO.ItemType,
                    out var message);
                SendStoreResult(ref ecb, req.ValueRO.SourceConnection, ok, message);
                ecb.DestroyEntity(entity);
            }

            // --- Extra ship-family component purchase ---
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<PurchaseStoreComponentCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(state.EntityManager, req.ValueRO.SourceConnection);
                bool ok = TryPurchaseStoreComponentForNetworkId(
                    state.EntityManager,
                    networkId,
                    cmd.ValueRO.HomePlanetId,
                    cmd.ValueRO.ComponentId.ToString(),
                    out var message);
                SendStoreResult(ref ecb, req.ValueRO.SourceConnection, ok, message);
                ecb.DestroyEntity(entity);
            }

            // --- Card spin (pay + roll three offers) ---
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<CardSpinCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(state.EntityManager, req.ValueRO.SourceConnection);
                bool ok = TryCardSpinForNetworkId(
                    state.EntityManager,
                    networkId,
                    cmd.ValueRO.StorePlanetId,
                    out var offer0,
                    out var offer1,
                    out var offer2,
                    out var message);
                SendCardSpinOffer(ref ecb, req.ValueRO.SourceConnection, ok, offer0, offer1, offer2);
                if (!ok)
                    SendStoreResult(ref ecb, req.ValueRO.SourceConnection, false, message);
                ecb.DestroyEntity(entity);
            }

            // --- Take one card from the current spin offer ---
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<TakeSpinCardCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(state.EntityManager, req.ValueRO.SourceConnection);
                bool ok = TryTakeSpinCardForNetworkId(
                    state.EntityManager,
                    networkId,
                    cmd.ValueRO.StorePlanetId,
                    cmd.ValueRO.CardId.ToString(),
                    out var message);
                SendStoreResult(ref ecb, req.ValueRO.SourceConnection, ok, message);
                if (ok)
                    SendCardSpinOffer(ref ecb, req.ValueRO.SourceConnection, false, default, default, default);
                ecb.DestroyEntity(entity);
            }

            // --- Remove equipped card ---
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<RemoveEquippedCardCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(state.EntityManager, req.ValueRO.SourceConnection);
                bool ok = TryRemoveEquippedCardForNetworkId(
                    state.EntityManager, networkId, cmd.ValueRO.SlotIndex, out var message);
                SendStoreResult(ref ecb, req.ValueRO.SourceConnection, ok, message);
                ecb.DestroyEntity(entity);
            }

            // --- Remove equipped equipment / component ---
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<RemoveEquippedEquipmentCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(state.EntityManager, req.ValueRO.SourceConnection);
                bool ok = TryRemoveEquippedEquipmentForNetworkId(
                    state.EntityManager, networkId, cmd.ValueRO.SlotIndex, out var message);
                SendStoreResult(ref ecb, req.ValueRO.SourceConnection, ok, message);
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>Reads <see cref="NetworkId"/> from the NetCode connection that sent the store RPC.</summary>
        static int GetSenderNetworkId(EntityManager em, Entity connection)
        {
            if (connection == Entity.Null || !em.HasComponent<NetworkId>(connection))
                return -1;
            return em.GetComponentData<NetworkId>(connection).Value;
        }

        /// <summary>Finds the ship ghost owned by this client's <see cref="GhostOwner.NetworkId"/>.</summary>
        static bool TryGetOwnedShip(EntityManager em, int networkId, out Entity shipEntity)
        {
            shipEntity = Entity.Null;
            if (networkId <= 0)
                return false;

            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner));
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < owners.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                shipEntity = entities[i];
                return true;
            }

            return false;
        }

        /// <summary>Locates the home planet entity for a team (store purchases debit its contributed gems).</summary>
        static bool TryFindHomePlanet(EntityManager em, TeamId team, out Entity homeEntity, out PlanetState homeState)
        {
            homeEntity = Entity.Null;
            homeState = default;

            using var query = em.CreateEntityQuery(typeof(HomePlanetTag), typeof(PlanetState));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].Ownership != team)
                    continue;
                homeEntity = entities[i];
                homeState = states[i];
                return true;
            }

            return false;
        }

        static bool TryFindPlanetById(EntityManager em, int planetId, out Entity planetEntity, out PlanetState planetState)
        {
            planetEntity = Entity.Null;
            planetState = default;
            using var query = em.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetState));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].PlanetId != planetId)
                    continue;
                planetEntity = entities[i];
                planetState = states[i];
                return true;
            }

            return false;
        }

        static float GetContributedGemsForTeam(EntityManager em, int networkId, int homePlanetId)
        {
            if (networkId <= 0)
                return 0f;

            using var query = em.CreateEntityQuery(typeof(HomePlanetTag), typeof(PlanetState));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            using var entities = query.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (homePlanetId > 0 && states[i].PlanetId != homePlanetId)
                    continue;
                return ContributedGemsLogic.Get(em, entities[i], networkId);
            }

            return 0f;
        }

        /// <summary>Writes <see cref="ShipLoadoutState.HealingBulletsActive"/> for the owning ship.</summary>
        public static bool TrySetHealingBulletsForNetworkId(EntityManager em, int networkId, bool healingActive)
        {
            if (!TryGetOwnedShip(em, networkId, out var shipEntity))
                return false;
            if (!em.HasComponent<ShipLoadoutState>(shipEntity))
                return false;

            var loadout = em.GetComponentData<ShipLoadoutState>(shipEntity);
            loadout.HealingBulletsActive = healingActive;
            em.SetComponentData(shipEntity, loadout);
            return true;
        }

        /// <summary>
        /// Server-authoritative ship upgrade / debug-free hull select.
        /// Also called directly from <see cref="UI.MoonOrbitRpcClient"/> on Local Host.
        /// </summary>
        public static bool TryPurchaseShipUpgradeForNetworkId(
            EntityManager em,
            int networkId,
            int storePlanetId,
            int targetLevel,
            int targetBranchIndex,
            out FixedString128Bytes message)
        {
            message = default;
            if (!TryGetOwnedShip(em, networkId, out var shipEntity))
            {
                message = "Ship not found.";
                return false;
            }

            var ship = em.GetComponentData<ShipState>(shipEntity);
            if (ship.Team == TeamId.None)
            {
                message = "No team.";
                return false;
            }

            if (!TryFindPlanetById(em, storePlanetId, out _, out var storePlanet))
            {
                message = "Planet not found.";
                return false;
            }

            if (storePlanet.Ownership != ship.Team)
            {
                message = "Planet not owned.";
                return false;
            }

            // [TITAN-ORBIT] Local Editor / development convenience — GameManager Inspector toggle
            // "Debug Free Ship Upgrade Tree" publishes into TitanOrbitDebugFlags (Shared).
            bool debugFree = TitanOrbitDebugFlags.FreeShipUpgradeTree;

            if (!debugFree)
            {
                int nextLevel = ship.ShipLevel + 1;
                if (targetLevel != nextLevel)
                {
                    message = "Invalid upgrade level.";
                    return false;
                }

                if (!UpgradeTree.IsValidUpgradeStep(ship.ShipLevel, ship.BranchIndex, targetLevel, targetBranchIndex))
                {
                    message = "Invalid upgrade path.";
                    return false;
                }

                // Planets cap at 6; L7 MEGAs use the moon-full gate below, not planet level 7.
                if (targetLevel < 7 && targetLevel > storePlanet.PlanetLevel)
                {
                    message = "Planet level too low.";
                    return false;
                }
            }
            else
            {
                if (targetLevel < 1 || targetLevel > 7)
                {
                    message = "Invalid debug ship level.";
                    return false;
                }

                int branchCount = UpgradeTree.GetShipCountForLevel(targetLevel);
                if (targetBranchIndex < 0 || targetBranchIndex >= branchCount)
                {
                    message = "Invalid debug ship branch.";
                    return false;
                }
            }

            if (!TryFindHomePlanet(em, ship.Team, out var homeEntity, out _))
            {
                message = "Home planet not found.";
                return false;
            }

            // --- Adopt store planet's ship family ---
            // [TITAN-ORBIT] Home → AstroEagle (index 0). Captured neutrals keep the family rolled at
            // spawn; buying here switches the ship onto that family's upgrade tree (not AstroEagle).
            byte storeFamilyIndex = ResolveStoreFamilyConfigIndex(storePlanet);

            bool buyingMega = targetLevel == 7;
            ushort megaCatalogIndex = 0;
            if (buyingMega)
            {
                if (!TryFindPlanetById(em, storePlanetId, out var storePlanetEntity, out _))
                {
                    message = "Planet not found.";
                    return false;
                }

                if (!debugFree)
                {
                    var moon = em.HasComponent<PlanetGemMoonState>(storePlanetEntity)
                        ? em.GetComponentData<PlanetGemMoonState>(storePlanetEntity)
                        : default;
                    if (!MegaShipPlanetLogic.IsMegaPurchaseUnlocked(
                            storePlanet.PlanetLevel, moon.CurrentMoonGems, moon.MaxMoonGems))
                    {
                        message = "MEGA locked — planet must be level 6 with a full gem moon.";
                        return false;
                    }
                }

                if (!MegaShipPlanetLogic.TryGetSlot(
                        em, storePlanetEntity, targetBranchIndex, out var megaSlot))
                {
                    message = "No MEGA assigned to that slot.";
                    return false;
                }

                if (megaSlot.OccupiedByNetworkId != 0 && megaSlot.OccupiedByNetworkId != networkId)
                {
                    message = "That MEGA is already in service.";
                    return false;
                }

                megaCatalogIndex = megaSlot.CatalogIndex;

                // --- Unarmed hulls stay in the catalog, never in a match ---
                // [TITAN-ORBIT] Match roll already skips firepower-0, but a stale slot or
                // debug click must not spend gems or spawn an unarmed MEGA.
                var megaCatalog = MegaShipCatalog.Load();
                if (megaCatalog == null || !megaCatalog.IsEligibleForMatch(megaCatalogIndex))
                {
                    message = "That MEGA has no weapons.";
                    return false;
                }
            }
            else if (!ShipStatApplyLogic.TryResolveChassisId(
                    ship.Team,
                    targetLevel,
                    targetBranchIndex,
                    out _,
                    allowFallback: false,
                    shipFamilyConfigIndex: storeFamilyIndex))
            {
                message = "No chassis for that upgrade slot.";
                return false;
            }

            if (!debugFree)
            {
                float cost = buyingMega
                    ? (MegaShipCatalog.Load() != null
                        ? MegaShipCatalog.Load().GetPurchaseGemCost()
                        : MoonOrbitStorePricing.GetShipUpgradeCost(7))
                    : MoonOrbitStorePricing.GetShipUpgradeCost(targetLevel);
                if (!ContributedGemsLogic.TrySpend(em, homeEntity, networkId, cost))
                {
                    message = "Not enough contributed gems.";
                    return false;
                }
            }

            if (buyingMega)
            {
                if (!MegaShipPlanetLogic.TryOccupySlot(em, storePlanetId, targetBranchIndex, networkId))
                {
                    message = "That MEGA is already in service.";
                    return false;
                }

                byte prevFamily = ship.ShipFamilyConfigIndex;
                int prevLevel = math.max(1, ship.ShipLevel);
                int prevBranch = math.max(0, ship.BranchIndex);
                if (em.HasComponent<MegaShipState>(shipEntity))
                {
                    var existingMega = em.GetComponentData<MegaShipState>(shipEntity);
                    if (existingMega.IsMega)
                    {
                        prevFamily = existingMega.PreviousFamilyIndex;
                        prevLevel = math.max(1, existingMega.PreviousLevel);
                        prevBranch = math.max(0, existingMega.PreviousBranch);
                        MegaShipPlanetLogic.FreeSlot(em, existingMega.StorePlanetId, existingMega.MegaSlotIndex);
                    }

                    em.SetComponentData(shipEntity, new MegaShipState
                    {
                        IsMega = true,
                        CatalogIndex = megaCatalogIndex,
                        StorePlanetId = storePlanetId,
                        MegaSlotIndex = (byte)math.clamp(targetBranchIndex, 0, 2),
                        PreviousFamilyIndex = prevFamily,
                        PreviousLevel = prevLevel,
                        PreviousBranch = prevBranch,
                        GunsLocked = false,
                    });
                }
            }

            ship.ShipLevel = targetLevel;
            ship.BranchIndex = targetBranchIndex;
            ship.ShipFamilyConfigIndex = storeFamilyIndex;
            em.SetComponentData(shipEntity, ship);

            if (em.HasComponent<ShipAttributeUpgradeState>(shipEntity))
            {
                var attrs = em.GetComponentData<ShipAttributeUpgradeState>(shipEntity);
                ShipAttributeUpgradeLogic.Reset(ref attrs);
                em.SetComponentData(shipEntity, attrs);
            }

            if (em.HasComponent<ShipLoadoutState>(shipEntity))
            {
                var loadout = em.GetComponentData<ShipLoadoutState>(shipEntity);
                loadout.BranchIndex = targetBranchIndex;
                loadout.ChassisIndex = targetBranchIndex;
                em.SetComponentData(shipEntity, loadout);
            }
            else
            {
                em.AddComponentData(shipEntity, new ShipLoadoutState
                {
                    BranchIndex = targetBranchIndex,
                    ChassisIndex = targetBranchIndex,
                });
            }

            ShipStatApplyLogic.ApplyToShip(
                em,
                shipEntity,
                ship.Team,
                targetLevel,
                targetBranchIndex);

            // Re-pin docked ship after upgrade — skip when map size is missing (never invent 1000).
            if (ShipMoonDockAttachLogic.TryGetMapSize(em, out float mapW, out float mapH))
            {
                double moonElapsed = 0.0;
                using (var tickQuery = em.CreateEntityQuery(ComponentType.ReadOnly<NetworkTime>()))
                using (var rateQuery = em.CreateEntityQuery(ComponentType.ReadOnly<ClientServerTickRate>()))
                {
                    int hz = 0;
                    if (rateQuery.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                        hz = tickRate.SimulationTickRate;
                    if (tickQuery.TryGetSingleton<NetworkTime>(out var networkTime))
                        moonElapsed = PlanetGemMoonOrbitClock.GetElapsedSeconds(
                            networkTime, hz, includeTickFraction: false);
                }

                ShipMoonDockAttachLogic.TryReattachFullyDockedShip(
                    em, shipEntity, mapW, mapH, moonElapsed);
            }

            message = debugFree ? "Debug ship selected." : "Ship upgraded.";
            return true;
        }

        /// <summary>
        /// Buys a drone / rocket / mine pack into an empty equipment slot.
        /// Drones stamp <c>ItemLevel = min(ship, docked planet)</c>.
        /// Local Host calls this directly — do not SendRpc on ServerWorld.
        /// </summary>
        public static bool TryPurchaseStoreItemForNetworkId(
            EntityManager em,
            int networkId,
            int homePlanetId,
            int itemTypeInt,
            out FixedString128Bytes message)
        {
            message = default;
            if (!TryGetOwnedShip(em, networkId, out var shipEntity))
            {
                message = "Ship not found.";
                return false;
            }

            var ship = em.GetComponentData<ShipState>(shipEntity);
            if (ship.Team == TeamId.None)
            {
                message = "No team.";
                return false;
            }

            if (!TryFindHomePlanet(em, ship.Team, out var homeEntity, out var homeState))
            {
                message = "Home planet not found.";
                return false;
            }

            if (homePlanetId > 0 && homeState.PlanetId != homePlanetId)
            {
                message = "Wrong home planet.";
                return false;
            }

            var itemType = (StoreItemType)itemTypeInt;
            if (StoreItemData.IsShipComponent(itemType))
            {
                message = "Use component purchase for ship parts.";
                return false;
            }

            // [TITAN-ORBIT] Drones lock ItemLevel to min(ship, docked planet). A level-6 ship
            // on a level-3 moon can only buy a level-3 drone (price, HP, and damage).
            int purchaseLevel = ResolveStorePurchaseLevel(em, shipEntity, ship, storePlanetIdHint: 0);
            float cost = StoreItemData.GetPrice(itemType, purchaseLevel);
            if (!ContributedGemsLogic.TrySpend(em, homeEntity, networkId, cost))
            {
                message = "Not enough contributed gems.";
                return false;
            }

            byte sourceFamilyIndex = ResolveDroneSourceFamilyIndex(em, shipEntity);
            if (!TryAddEquipmentItem(em, shipEntity, itemType, ship.ShipLevel, purchaseLevel, sourceFamilyIndex, out message))
            {
                ContributedGemsLogic.Refund(em, homeEntity, networkId, cost);
                return false;
            }

            // [TITAN-ORBIT] Support items do not change chassis stats, but fingerprint still updates
            // so UI/systems that watch loadout see a consistent apply bookkeeping path.
            ReapplyShipStats(em, shipEntity, ship);

            message = "Purchased.";
            return true;
        }

        /// <summary>
        /// Buys a ship-family extra component by stable id into an empty equipment slot.
        /// Price and stamped ItemLevel use <c>min(ship, docked planet)</c>.
        /// </summary>
        public static bool TryPurchaseStoreComponentForNetworkId(
            EntityManager em,
            int networkId,
            int homePlanetId,
            string componentId,
            out FixedString128Bytes message)
        {
            message = default;
            if (string.IsNullOrWhiteSpace(componentId))
            {
                message = "Invalid component.";
                return false;
            }

            if (!TryGetOwnedShip(em, networkId, out var shipEntity))
            {
                message = "Ship not found.";
                return false;
            }

            var ship = em.GetComponentData<ShipState>(shipEntity);
            if (ship.Team == TeamId.None)
            {
                message = "No team.";
                return false;
            }

            if (!TryFindHomePlanet(em, ship.Team, out var homeEntity, out var homeState))
            {
                message = "Home planet not found.";
                return false;
            }

            if (homePlanetId > 0 && homeState.PlanetId != homePlanetId)
            {
                message = "Wrong home planet.";
                return false;
            }

            if (!TryResolveFamilyForShip(em, shipEntity, ship, out ShipFamilyDefinition family) || family == null)
            {
                message = "Ship family not found.";
                return false;
            }

            if (!family.TryGetComponentEntry(componentId, out ShipFamilyComponentEntry entry) || entry == null)
            {
                if (!BulletBankProfileUtility.TryFindComponentInAnyFamily(componentId, out entry) || entry == null)
                {
                    message = "Component not in catalog.";
                    return false;
                }
            }

            if (HasComponentEquipped(em, shipEntity, componentId))
            {
                message = "Already equipped.";
                return false;
            }

            // [TITAN-ORBIT] Same planet cap as drones: price and stamped ItemLevel use
            // min(ship, docked planet) so a high-level hull cannot buy max-tier parts on a weak world.
            int purchaseLevel = ResolveStorePurchaseLevel(em, shipEntity, ship, storePlanetIdHint: 0);
            float cost = ShipComponentStoreData.GetComponentGemPrice(entry, purchaseLevel);
            if (!ContributedGemsLogic.TrySpend(em, homeEntity, networkId, cost))
            {
                message = "Not enough contributed gems.";
                return false;
            }

            if (!TryAddShipComponentItem(em, shipEntity, componentId, ship.ShipLevel, purchaseLevel, out message))
            {
                ContributedGemsLogic.Refund(em, homeEntity, networkId, cost);
                return false;
            }

            ReapplyShipStats(em, shipEntity, ship);
            message = "Component purchased.";
            return true;
        }

        /// <summary>
        /// Pays spin cost, rolls three weighted cards, stores a pending offer for take-card.
        /// Spin tier is <c>min(ship, store planet)</c> so a high-level ship on a low-level moon
        /// only sees that planet's card tier. On Local Host the caller also mirrors offer ids
        /// into <see cref="MoonOrbitClientState"/>.
        /// </summary>
        public static bool TryCardSpinForNetworkId(
            EntityManager em,
            int networkId,
            int storePlanetId,
            out FixedString64Bytes offer0,
            out FixedString64Bytes offer1,
            out FixedString64Bytes offer2,
            out FixedString128Bytes message)
        {
            offer0 = default;
            offer1 = default;
            offer2 = default;
            message = default;

            if (!TryGetOwnedShip(em, networkId, out var shipEntity))
            {
                message = "Ship not found.";
                return false;
            }

            var ship = em.GetComponentData<ShipState>(shipEntity);
            if (ship.Team == TeamId.None)
            {
                message = "No team.";
                return false;
            }

            if (!TryFindPlanetById(em, storePlanetId, out _, out var storePlanet))
            {
                message = "Planet not found.";
                return false;
            }

            if (storePlanet.Ownership != ship.Team)
            {
                message = "Planet not owned.";
                return false;
            }

            if (!TryFindHomePlanet(em, ship.Team, out var homeEntity, out var homeState))
            {
                message = "Home planet not found.";
                return false;
            }

            if (!HasEmptyLoadoutSlot(em, shipEntity, ship.ShipLevel))
            {
                message = "No empty loadout slot.";
                return false;
            }

            if (!TryResolveFamilyForShip(em, shipEntity, ship, out ShipFamilyDefinition family) || family == null)
            {
                message = "Ship family not found.";
                return false;
            }

            // [TITAN-ORBIT] Spin tier is min(ship, docked store planet) — not home.
            // Landing on a level-3 moon with a level-6 ship rolls level-3 cards only.
            int storeLevel = math.max(1, storePlanet.PlanetLevel);
            int homeLevel = math.max(1, homeState.PlanetLevel);
            int spinTier = StoreItemData.GetStorePurchaseLevel(ship.ShipLevel, storeLevel);
            var pool = BuildCardPoolForSpin(family, spinTier, homeLevel);
            if (pool.Count == 0)
            {
                message = "No cards available.";
                return false;
            }

            float spinCost = GetCardSpinCost(spinTier);
            if (!ContributedGemsLogic.TrySpend(em, homeEntity, networkId, spinCost))
            {
                message = "Not enough contributed gems.";
                return false;
            }

            // [STANDARD] Weighted rarity roll — same distribution as pre-ECS CardShopSystem.
            var rng = new System.Random(
                unchecked((int)(DateTime.UtcNow.Ticks ^ (long)networkId ^ (long)storePlanetId)));
            string a = PickOneWeighted(pool, rng)?.GetStableCardId() ?? string.Empty;
            string b = PickOneWeighted(pool, rng)?.GetStableCardId() ?? string.Empty;
            string c = PickOneWeighted(pool, rng)?.GetStableCardId() ?? string.Empty;

            if (!s_pendingCardSpins.TryGetValue(networkId, out var pend) || pend == null)
            {
                pend = new PendingCardSpinOffer();
                s_pendingCardSpins[networkId] = pend;
            }

            pend.CardIds[0] = a;
            pend.CardIds[1] = b;
            pend.CardIds[2] = c;

            offer0 = a;
            offer1 = b;
            offer2 = c;
            message = "Spin ready.";
            return true;
        }

        /// <summary>
        /// Equips one card from the pending spin offer (spin already paid). Clears the offer.
        /// </summary>
        public static bool TryTakeSpinCardForNetworkId(
            EntityManager em,
            int networkId,
            int storePlanetId,
            string cardId,
            out FixedString128Bytes message)
        {
            message = default;
            if (string.IsNullOrEmpty(cardId))
            {
                message = "Invalid card.";
                return false;
            }

            if (!TryGetOwnedShip(em, networkId, out var shipEntity))
            {
                message = "Ship not found.";
                return false;
            }

            var ship = em.GetComponentData<ShipState>(shipEntity);
            if (ship.Team == TeamId.None)
            {
                message = "No team.";
                return false;
            }

            if (!TryFindPlanetById(em, storePlanetId, out _, out var storePlanet))
            {
                message = "Planet not found.";
                return false;
            }

            if (storePlanet.Ownership != ship.Team)
            {
                message = "Planet not owned.";
                return false;
            }

            if (!TryFindHomePlanet(em, ship.Team, out _, out var homeState))
            {
                message = "Home planet not found.";
                return false;
            }

            if (!s_pendingCardSpins.TryGetValue(networkId, out var pend) || pend?.CardIds == null)
            {
                message = "No spin offer.";
                return false;
            }

            bool inOffer = false;
            for (int i = 0; i < pend.CardIds.Length; i++)
            {
                if (!string.IsNullOrEmpty(pend.CardIds[i]) &&
                    string.Equals(pend.CardIds[i], cardId, StringComparison.Ordinal))
                {
                    inOffer = true;
                    break;
                }
            }

            if (!inOffer)
            {
                message = "Card not in offer.";
                return false;
            }

            CardData card = ShipStatApplyLogic.FindCardAnywhere(cardId);
            if (card == null && TryResolveFamilyForShip(em, shipEntity, ship, out var family))
                card = ShipStatApplyLogic.FindCardInFamily(family, cardId);
            if (card == null)
            {
                message = "Card not found.";
                return false;
            }

            int homeLevel = math.max(1, homeState.PlanetLevel);
            if (homeLevel < card.minHomePlanetLevel)
            {
                message = "Home level too low.";
                return false;
            }

            // [TITAN-ORBIT] Card tier cannot exceed min(ship, this moon's planet).
            int purchaseLevel = StoreItemData.GetStorePurchaseLevel(ship.ShipLevel, storePlanet.PlanetLevel);
            int cardLvl = math.max(1, card.cardLevel);
            if (cardLvl > purchaseLevel)
            {
                message = cardLvl > ship.ShipLevel ? "Ship level too low." : "Planet level too low.";
                return false;
            }

            if (!TryAddCard(em, shipEntity, cardId, ship.ShipLevel, out message))
                return false;

            s_pendingCardSpins.Remove(networkId);
            ReapplyShipStats(em, shipEntity, ship);
            message = "Card equipped.";
            return true;
        }

        /// <summary>Removes an equipped upgrade card at <paramref name="slotIndex"/> (free discard).</summary>
        public static bool TryRemoveEquippedCardForNetworkId(
            EntityManager em,
            int networkId,
            int slotIndex,
            out FixedString128Bytes message)
        {
            message = default;
            if (!TryGetOwnedShip(em, networkId, out var shipEntity))
            {
                message = "Ship not found.";
                return false;
            }

            if (!em.HasBuffer<EquippedCardElement>(shipEntity))
            {
                message = "No cards.";
                return false;
            }

            var buf = em.GetBuffer<EquippedCardElement>(shipEntity);
            if (slotIndex < 0 || slotIndex >= buf.Length)
            {
                message = "Invalid card slot.";
                return false;
            }

            buf.RemoveAt(slotIndex);
            var ship = em.GetComponentData<ShipState>(shipEntity);
            ReapplyShipStats(em, shipEntity, ship);
            message = "Card removed.";
            return true;
        }

        /// <summary>Removes an equipped store item / component at <paramref name="slotIndex"/> (free discard).</summary>
        public static bool TryRemoveEquippedEquipmentForNetworkId(
            EntityManager em,
            int networkId,
            int slotIndex,
            out FixedString128Bytes message)
        {
            message = default;
            if (!TryGetOwnedShip(em, networkId, out var shipEntity))
            {
                message = "Ship not found.";
                return false;
            }

            if (!em.HasBuffer<EquippedEquipmentElement>(shipEntity))
            {
                message = "No equipment.";
                return false;
            }

            var buf = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
            if (slotIndex < 0 || slotIndex >= buf.Length)
            {
                message = "Invalid equipment slot.";
                return false;
            }

            buf.RemoveAt(slotIndex);
            var ship = em.GetComponentData<ShipState>(shipEntity);
            ReapplyShipStats(em, shipEntity, ship);
            message = "Equipment removed.";
            return true;
        }

        /// <summary>Copies the current pending spin offer for Local Host UI (no network round-trip).</summary>
        public static bool TryGetPendingSpinOffer(int networkId, out string a, out string b, out string c)
        {
            a = b = c = string.Empty;
            if (!s_pendingCardSpins.TryGetValue(networkId, out var pend) || pend?.CardIds == null)
                return false;
            a = pend.CardIds[0] ?? string.Empty;
            b = pend.CardIds[1] ?? string.Empty;
            c = pend.CardIds[2] ?? string.Empty;
            return true;
        }

        /// <summary>Clears pending spin offer (Local Host take-card success).</summary>
        public static void ClearPendingSpinOffer(int networkId) => s_pendingCardSpins.Remove(networkId);

        static void ReapplyShipStats(EntityManager em, Entity shipEntity, in ShipState ship)
        {
            int branch = ship.BranchIndex;
            ShipStatApplyLogic.ApplyToShip(em, shipEntity, ship.Team, ship.ShipLevel, branch);
        }

        static bool TryResolveFamilyForShip(
            EntityManager em,
            Entity shipEntity,
            in ShipState ship,
            out ShipFamilyDefinition family)
        {
            family = null;
            int branch = ship.BranchIndex;
            if (!ShipStatApplyLogic.TryResolveChassisId(
                    em, shipEntity, ship.Team, ship.ShipLevel, branch, out string chassisId))
                return false;
            return ShipStatApplyLogic.TryResolveFamilyForChassisId(chassisId, out family) && family != null;
        }

        static bool HasComponentEquipped(EntityManager em, Entity shipEntity, string componentId)
        {
            if (!em.HasBuffer<EquippedEquipmentElement>(shipEntity))
                return false;

            var buf = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
            for (int i = 0; i < buf.Length; i++)
            {
                var e = buf[i];
                if ((StoreItemType)e.ItemType != StoreItemType.ShipComponent)
                    continue;
                if (string.Equals(e.ComponentId.ToString(), componentId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Family of the moon the ship is docked at (home family if undocked / unknown).
        /// Stamped onto purchased drones so each drone keeps that planet's bullet bank.
        /// </summary>
        static byte ResolveDroneSourceFamilyIndex(EntityManager em, Entity shipEntity)
        {
            if (em.HasComponent<ShipMoonDockState>(shipEntity))
            {
                int planetId = em.GetComponentData<ShipMoonDockState>(shipEntity).MoonPlanetId;
                if (planetId > 0 && TryFindPlanetById(em, planetId, out _, out var storePlanet))
                    return ResolveStoreFamilyConfigIndex(storePlanet);
            }

            return PlanetShipFamilyAssignment.HomeFamilyConfigIndex;
        }

        static byte ResolveStoreFamilyConfigIndex(in PlanetState storePlanet)
        {
            return storePlanet.IsHomePlanet
                ? PlanetShipFamilyAssignment.HomeFamilyConfigIndex
                : (storePlanet.ShipFamilyConfigIndex > 0
                    ? storePlanet.ShipFamilyConfigIndex
                    : PlanetShipFamilyAssignment.HomeFamilyConfigIndex);
        }

        /// <summary>
        /// Cards and gear share one LOADOUT pool: used = card buffer + equipment buffer, cap = ship level.
        /// </summary>
        static bool HasEmptyLoadoutSlot(EntityManager em, Entity shipEntity, int shipLevel)
        {
            int cap = math.max(1, shipLevel);
            int used = 0;
            if (em.HasBuffer<EquippedCardElement>(shipEntity))
                used += em.GetBuffer<EquippedCardElement>(shipEntity).Length;
            if (em.HasBuffer<EquippedEquipmentElement>(shipEntity))
                used += em.GetBuffer<EquippedEquipmentElement>(shipEntity).Length;
            return used < cap;
        }

        /// <summary>
        /// Level the docked moon store may sell: <c>min(ship, that planet)</c>.
        /// Prefers the moon the ship is actually docked at (authoritative, not client-sent).
        /// Falls back to <paramref name="storePlanetIdHint"/> then the team's home planet.
        /// </summary>
        static int ResolveStorePurchaseLevel(
            EntityManager em,
            Entity shipEntity,
            in ShipState ship,
            int storePlanetIdHint)
        {
            int shipLevel = math.max(1, ship.ShipLevel);
            int planetLevel = 0;

            // --- Docked moon first (where the Orbit Menu is open) ---
            if (em.HasComponent<ShipMoonDockState>(shipEntity))
            {
                int dockedId = em.GetComponentData<ShipMoonDockState>(shipEntity).MoonPlanetId;
                if (dockedId > 0 && TryFindPlanetById(em, dockedId, out _, out var docked))
                    planetLevel = math.max(1, docked.PlanetLevel);
            }

            // --- Explicit store planet (card RPCs already send this) ---
            if (planetLevel <= 0 && storePlanetIdHint > 0 &&
                TryFindPlanetById(em, storePlanetIdHint, out _, out var hinted))
                planetLevel = math.max(1, hinted.PlanetLevel);

            // --- Home planet if the ship is somehow undocked ---
            if (planetLevel <= 0 && TryFindHomePlanet(em, ship.Team, out _, out var home))
                planetLevel = math.max(1, home.PlanetLevel);

            if (planetLevel <= 0)
                planetLevel = 1;

            return StoreItemData.GetStorePurchaseLevel(shipLevel, planetLevel);
        }

        /// <summary>
        /// Appends a drone / rocket / mine into the equipment buffer.
        /// Slot count uses <paramref name="shipLevel"/>; drone ItemLevel uses
        /// <paramref name="purchaseLevel"/> (already capped to the docked planet).
        /// </summary>
        static bool TryAddEquipmentItem(
            EntityManager em,
            Entity shipEntity,
            StoreItemType itemType,
            int shipLevel,
            int purchaseLevel,
            byte sourceFamilyConfigIndex,
            out FixedString128Bytes message)
        {
            message = default;
            if (!em.HasBuffer<EquippedEquipmentElement>(shipEntity))
                em.AddBuffer<EquippedEquipmentElement>(shipEntity);

            var buffer = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
            // [TITAN-ORBIT] Cards and gear share one LOADOUT pool capped at ship level.
            if (!HasEmptyLoadoutSlot(em, shipEntity, shipLevel))
            {
                message = "No empty loadout slot.";
                return false;
            }

            int lockedLevel = math.max(1, purchaseLevel);
            int charges = StoreItemData.IsDrone(itemType)
                ? StoreItemData.GetDroneMaxHp(itemType, lockedLevel)
                : StoreItemData.GetPackSize(itemType);
            if (StoreItemData.IsDrone(itemType))
            {
                float hpMul = CardEffectQuery.GetMul(em, shipEntity, CardEffectKind.DroneHitPointsMul);
                charges = math.max(1, (int)math.round(charges * hpMul));
            }
            else if (itemType == StoreItemType.SmallRockets || itemType == StoreItemType.LargeRockets)
                charges += (int)math.round(CardEffectQuery.GetValue(em, shipEntity, CardEffectKind.RocketPackSizeAdd));
            else if (itemType == StoreItemType.SmallMines || itemType == StoreItemType.LargeMines)
                charges += (int)math.round(CardEffectQuery.GetValue(em, shipEntity, CardEffectKind.MinePackSizeAdd));
            // [TITAN-ORBIT] Drones, rockets, and mines lock ItemLevel to the store purchase
            // level (min of ship and planet). Damage/HP/cost already used this level; store
            // it so stats stay fixed after buy.
            int itemLevel = StoreItemData.IsLeveledStoreGood(itemType)
                ? lockedLevel
                : 0;
            FixedString64Bytes componentId = default;
            if (StoreItemData.IsDrone(itemType))
                componentId = BulletBankProfileUtility.FormatDroneSourceFamilyId(sourceFamilyConfigIndex);
            buffer.Add(new EquippedEquipmentElement
            {
                ItemType = (int)itemType,
                RemainingCharges = math.max(1, charges),
                ItemLevel = itemLevel,
                ComponentId = componentId,
            });
            return true;
        }

        /// <summary>
        /// Appends a ship-family extra component. Slot count uses the ship;
        /// ItemLevel stores the planet-capped purchase level for Orbit Menu labels.
        /// </summary>
        static bool TryAddShipComponentItem(
            EntityManager em,
            Entity shipEntity,
            string componentId,
            int shipLevel,
            int purchaseLevel,
            out FixedString128Bytes message)
        {
            message = default;
            if (!em.HasBuffer<EquippedEquipmentElement>(shipEntity))
                em.AddBuffer<EquippedEquipmentElement>(shipEntity);

            var buffer = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
            if (!HasEmptyLoadoutSlot(em, shipEntity, shipLevel))
            {
                message = "No empty loadout slot.";
                return false;
            }

            buffer.Add(new EquippedEquipmentElement
            {
                ItemType = (int)StoreItemType.ShipComponent,
                RemainingCharges = 1,
                // [TITAN-ORBIT] Stamp the planet-capped purchase level so Orbit Menu loadout
                // can show "Lv 3" for a part bought on a level-3 moon.
                ItemLevel = math.max(1, purchaseLevel),
                ComponentId = componentId,
            });
            return true;
        }

        static bool TryAddCard(
            EntityManager em,
            Entity shipEntity,
            string cardId,
            int shipLevel,
            out FixedString128Bytes message)
        {
            message = default;
            if (!em.HasBuffer<EquippedCardElement>(shipEntity))
                em.AddBuffer<EquippedCardElement>(shipEntity);

            var buffer = em.GetBuffer<EquippedCardElement>(shipEntity);
            if (!HasEmptyLoadoutSlot(em, shipEntity, shipLevel))
            {
                message = "No empty loadout slot.";
                return false;
            }

            buffer.Add(new EquippedCardElement { CardId = cardId });
            return true;
        }

        static float GetCardSpinCost(int spinCardTier)
        {
            int gemLevel = math.clamp(math.max(1, spinCardTier), 1, 24);
            return math.max(15f, 20f * gemLevel * gemLevel);
        }

        static List<CardData> BuildCardPoolForSpin(ShipFamilyDefinition family, int spinCardTier, int homePlanetLevel)
        {
            var pool = new List<CardData>();
            int tier = math.max(1, spinCardTier);
            foreach (var card in family.GetUpgradeCards())
            {
                if (card == null)
                    continue;
                if (math.max(1, card.cardLevel) != tier)
                    continue;
                if (homePlanetLevel < card.minHomePlanetLevel)
                    continue;
                pool.Add(card);
            }

            return pool;
        }

        static int GetRarityWeight(int rarity)
        {
            // Common 50%, Uncommon 27%, Rare 14%, Epic 7%, Legendary 2%.
            if (rarity <= 1) return 50;
            if (rarity == 2) return 27;
            if (rarity == 3) return 14;
            if (rarity == 4) return 7;
            return 2;
        }

        static CardData PickOneWeighted(List<CardData> pool, System.Random rng)
        {
            if (pool == null || pool.Count == 0)
                return null;

            int totalRarityWeight = 0;
            for (int rarity = 1; rarity <= 5; rarity++)
                totalRarityWeight += GetRarityWeight(rarity);

            int rarityRoll = rng.Next(0, math.max(1, totalRarityWeight));
            int selectedRarity = 1;
            int rarityAcc = 0;
            for (int rarity = 1; rarity <= 5; rarity++)
            {
                rarityAcc += GetRarityWeight(rarity);
                if (rarityRoll < rarityAcc)
                {
                    selectedRarity = rarity;
                    break;
                }
            }

            int matches = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                if ((int)pool[i].rarity == selectedRarity)
                    matches++;
            }

            if (matches > 0)
            {
                int pick = rng.Next(matches);
                int seen = 0;
                for (int i = 0; i < pool.Count; i++)
                {
                    if ((int)pool[i].rarity != selectedRarity)
                        continue;
                    if (seen == pick)
                        return pool[i];
                    seen++;
                }
            }

            return pool[rng.Next(pool.Count)];
        }

        static void SendContributedGemsResult(ref EntityCommandBuffer ecb, Entity connection, float amount)
        {
            var resultEntity = ecb.CreateEntity();
            ecb.AddComponent(resultEntity, new ContributedGemsResultRpc { Amount = amount });
            ecb.AddComponent(resultEntity, new SendRpcCommandRequest { TargetConnection = connection });
        }

        static void SendStoreResult(ref EntityCommandBuffer ecb, Entity connection, bool success, FixedString128Bytes message)
        {
            var resultEntity = ecb.CreateEntity();
            ecb.AddComponent(resultEntity, new OrbitStoreResultRpc
            {
                Success = (byte)(success ? 1 : 0),
                Message = message,
            });
            ecb.AddComponent(resultEntity, new SendRpcCommandRequest { TargetConnection = connection });
        }

        static void SendCardSpinOffer(
            ref EntityCommandBuffer ecb,
            Entity connection,
            bool success,
            FixedString64Bytes a,
            FixedString64Bytes b,
            FixedString64Bytes c)
        {
            var resultEntity = ecb.CreateEntity();
            ecb.AddComponent(resultEntity, new CardSpinOfferRpc
            {
                CardId0 = a,
                CardId1 = b,
                CardId2 = c,
                Success = (byte)(success ? 1 : 0),
            });
            ecb.AddComponent(resultEntity, new SendRpcCommandRequest { TargetConnection = connection });
        }
    }

    /// <summary>Simple ship upgrade pricing until full CardShop parity.</summary>
    public static class MoonOrbitStorePricing
    {
        public static float GetShipUpgradeCost(int targetLevel)
        {
            switch (targetLevel)
            {
                case 2: return 100f;
                case 3: return 150f;
                case 4: return 250f;
                case 5: return 400f;
                case 6: return 650f;
                case 7: return 1200f;
                default: return 9999f;
            }
        }
    }
}
