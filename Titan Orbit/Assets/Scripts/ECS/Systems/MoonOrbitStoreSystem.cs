using TitanOrbit.Core;
using TitanOrbit.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>Server RPC handlers for moon orbit store: contributed gems, deposits, purchases.</summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MoonOrbitStoreSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (cmd, req, entity) in SystemAPI
                         .Query<RefRO<RequestContributedGemsCommand>, RefRO<ReceiveRpcCommandRequest>>()
                         .WithEntityAccess())
            {
                int networkId = GetSenderNetworkId(state.EntityManager, req.ValueRO.SourceConnection);
                float amount = GetContributedGemsForTeam(state.EntityManager, networkId, cmd.ValueRO.HomePlanetId);
                SendContributedGemsResult(ref ecb, req.ValueRO.SourceConnection, amount);
                ecb.DestroyEntity(entity);
            }

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
                }

                ecb.DestroyEntity(entity);
            }

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

        static int GetSenderNetworkId(EntityManager em, Entity connection)
        {
            if (connection == Entity.Null || !em.HasComponent<NetworkId>(connection))
                return -1;
            return em.GetComponentData<NetworkId>(connection).Value;
        }

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

            if (!TryFindHomePlanet(em, ship.Team, out var homeEntity, out _))
            {
                message = "Home planet not found.";
                return false;
            }

            float cost = MoonOrbitStorePricing.GetShipUpgradeCost(targetLevel);
            if (!ContributedGemsLogic.TrySpend(em, homeEntity, networkId, cost))
            {
                message = "Not enough contributed gems.";
                return false;
            }

            ship.ShipLevel = targetLevel;
            em.SetComponentData(shipEntity, ship);

            if (em.HasComponent<ShipLoadoutState>(shipEntity))
            {
                var loadout = em.GetComponentData<ShipLoadoutState>(shipEntity);
                loadout.BranchIndex = targetBranchIndex;
                loadout.ChassisIndex = targetBranchIndex;
                em.SetComponentData(shipEntity, loadout);
            }

            message = "Ship upgraded.";
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
