using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server RPC handlers for moon orbit store: contributed gem balance queries, deposit intent,
    /// ship level upgrades, and equipment purchases. Validates team, planet id, and contributed
    /// gem balances before mutating ship/planet state. Paired with
    /// <see cref="MoonOrbitRpcClientSystem"/> on the client.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MoonOrbitStoreSystem : ISystem
    {
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

            // --- Ship level / branch upgrade purchase ---
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<PurchaseShipUpgradeCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(state.EntityManager, req.ValueRO.SourceConnection);
                bool ok = TryPurchaseShipUpgrade(
                    state.EntityManager,
                    networkId,
                    cmd.ValueRO.StorePlanetId,
                    cmd.ValueRO.TargetLevel,
                    cmd.ValueRO.TargetBranchIndex,
                    out var message);
                SendStoreResult(ref ecb, req.ValueRO.SourceConnection, ok, message);
                ecb.DestroyEntity(entity);
            }

            // --- Equipment / consumable store purchase ---
            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<PurchaseStoreItemCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(state.EntityManager, req.ValueRO.SourceConnection);
                bool ok = TryPurchaseStoreItem(
                    state.EntityManager,
                    networkId,
                    cmd.ValueRO.HomePlanetId,
                    cmd.ValueRO.ItemType,
                    out var message);
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

        static bool TryFindPlanetById(EntityManager em, int planetId, out PlanetState planetState)
        {
            planetState = default;
            using var query = em.CreateEntityQuery(typeof(PlanetTag), typeof(PlanetState));
            using var states = query.ToComponentDataArray<PlanetState>(Allocator.Temp);
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].PlanetId != planetId)
                    continue;
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

        static bool TryPurchaseShipUpgrade(
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

            if (!TryFindPlanetById(em, storePlanetId, out var storePlanet))
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
            // "Debug Free Ship Upgrade Tree" publishes into TitanOrbitDebugFlags (Shared) because
            // TitanOrbit.ECS cannot reference TitanOrbit.Core. Dedicated servers leave this false.
            bool debugFree = TitanOrbitDebugFlags.FreeShipUpgradeTree;

            if (!debugFree)
            {
                // --- Normal ladder: only ShipLevel + 1 ---
                int nextLevel = ship.ShipLevel + 1;
                if (targetLevel != nextLevel)
                {
                    message = "Invalid upgrade level.";
                    return false;
                }

                if (targetLevel > storePlanet.PlanetLevel)
                {
                    message = "Planet level too low.";
                    return false;
                }
            }
            else
            {
                // --- Debug free: any authored tier 1–7 with a valid branch index ---
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

            // --- Validate ladder slot exists in PlanetShipFamilyConfig before mutating ---
            // AstroEagle currently has L1–L6 only; UI still draws L7 MEGA columns from UpgradeTree counts.
            if (!ShipStatApplyLogic.TryResolveChassisId(
                    ship.Team, targetLevel, targetBranchIndex, out _, allowFallback: false))
            {
                message = "No chassis for that upgrade slot.";
                return false;
            }

            // Debug mode skips gem spend so you can try every hull without depositing.
            if (!debugFree)
            {
                float cost = MoonOrbitStorePricing.GetShipUpgradeCost(targetLevel);
                if (!ContributedGemsLogic.TrySpend(em, homeEntity, networkId, cost))
                {
                    message = "Not enough contributed gems.";
                    return false;
                }
            }

            // --- Apply chassis tier + branch (both must ghost to clients) ---
            // [NETCODE] ShipState.BranchIndex is the authoritative replicated branch for visuals.
            // ShipLoadoutState.BranchIndex is kept in sync for systems that still read loadout.
            ship.ShipLevel = targetLevel;
            ship.BranchIndex = targetBranchIndex;
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

            // Catalog/hull sync re-pins on the next tick; also re-pin here so the purchase frame
            // itself does not leave a Physics gap while the old collider is still swapping.
            ShipMoonDockAttachLogic.GetMapSize(em, out float mapW, out float mapH);
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

            message = debugFree ? "Debug ship selected." : "Ship upgraded.";
            return true;
        }

        static bool TryPurchaseStoreItem(
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
                message = "Component purchases are not available yet.";
                return false;
            }

            float cost = StoreItemData.GetPrice(itemType);
            if (!ContributedGemsLogic.TrySpend(em, homeEntity, networkId, cost))
            {
                message = "Not enough contributed gems.";
                return false;
            }

            if (!TryAddEquipmentItem(em, shipEntity, itemType, ship.ShipLevel, out message))
            {
                ContributedGemsLogic.Refund(em, homeEntity, networkId, cost);
                return false;
            }

            message = "Purchased.";
            return true;
        }

        static bool TryAddEquipmentItem(
            EntityManager em,
            Entity shipEntity,
            StoreItemType itemType,
            int shipLevel,
            out FixedString128Bytes message)
        {
            message = default;
            if (!em.HasBuffer<EquippedEquipmentElement>(shipEntity))
                em.AddBuffer<EquippedEquipmentElement>(shipEntity);

            var buffer = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
            int maxSlots = math.max(1, shipLevel);
            if (buffer.Length >= maxSlots)
            {
                message = "No empty equipment slot.";
                return false;
            }

            int charges = StoreItemData.IsDrone(itemType)
                ? StoreItemData.GetDroneMaxHp(itemType)
                : StoreItemData.GetPackSize(itemType);
            buffer.Add(new EquippedEquipmentElement
            {
                ItemType = (int)itemType,
                RemainingCharges = math.max(1, charges),
            });
            return true;
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
