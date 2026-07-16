using TitanOrbit.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Physics;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Rebuilds each ship's <see cref="PhysicsCollider"/> from its chassis visual prefab when level,
    /// branch, or chassis id changes. Runs on server and client so prediction matches authority.
    /// Paired with <see cref="ShipStatApplySystem"/> (stats) and hybrid hull proxies when EG is off.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ShipStatApplySystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipHullColliderSyncSystem : ISystem
    {
        struct PendingHullSync
        {
            public Entity Entity;
            public int BranchIndex;
        }

        public void OnUpdate(ref SystemState state)
        {
            if (TitanOrbitPresentationConfig.UseEntitiesGraphicsForShips)
                return;

            var em = state.EntityManager;
            var config = ShipStatApplyLogic.Config;
            if (config == null)
                return;

            var pending = new NativeList<PendingHullSync>(Allocator.Temp);

            foreach (var (ship, loadout, entity) in SystemAPI
                         .Query<RefRO<ShipState>, RefRO<ShipLoadoutState>>()
                         .WithAll<ShipTag, PhysicsCollider>()
                         .WithEntityAccess())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;

                if (NeedsHullSync(em, entity, ship.ValueRO, loadout.ValueRO.BranchIndex))
                {
                    pending.Add(new PendingHullSync
                    {
                        Entity = entity,
                        BranchIndex = loadout.ValueRO.BranchIndex,
                    });
                }
            }

            foreach (var (ship, entity) in SystemAPI
                         .Query<RefRO<ShipState>>()
                         .WithAll<ShipTag, PhysicsCollider>()
                         .WithNone<ShipLoadoutState>()
                         .WithEntityAccess())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;

                if (NeedsHullSync(em, entity, ship.ValueRO, branchIndex: 0))
                {
                    pending.Add(new PendingHullSync
                    {
                        Entity = entity,
                        BranchIndex = 0,
                    });
                }
            }

            for (int i = 0; i < pending.Length; i++)
            {
                var work = pending[i];
                if (!em.Exists(work.Entity) || !em.HasComponent<ShipState>(work.Entity))
                    continue;

                var ship = em.GetComponentData<ShipState>(work.Entity);
                TrySyncHull(work.Entity, em, config, ship, work.BranchIndex);
            }

            pending.Dispose();
        }

        static bool NeedsHullSync(EntityManager em, Entity entity, in ShipState ship, int branchIndex)
        {
            if (!ShipStatApplyLogic.TryResolveChassisId(ship.Team, ship.ShipLevel, branchIndex, out string chassisId))
                return false;

            if (!em.HasComponent<ShipHullColliderState>(entity))
                return true;

            var applied = em.GetComponentData<ShipHullColliderState>(entity);
            var chassisKey = new FixedString64Bytes(chassisId);
            return !applied.ChassisId.Equals(chassisKey)
                || applied.AppliedShipLevel != ship.ShipLevel
                || applied.AppliedBranchIndex != branchIndex;
        }

        static void TrySyncHull(
            Entity entity,
            EntityManager em,
            PlanetShipFamilyConfig config,
            in ShipState ship,
            int branchIndex)
        {
            if (!ShipStatApplyLogic.TryResolveChassisId(ship.Team, ship.ShipLevel, branchIndex, out string chassisId))
                return;

            var tier = config.GetTierEntryForChassisId(chassisId);
            if (tier?.prefab == null)
                return;

            float motorMass = 1f;
            if (em.HasComponent<ShipMotorConfig>(entity))
                motorMass = em.GetComponentData<ShipMotorConfig>(entity).Mass;

            if (!ShipHullColliderLogic.TryApplyChassisCollider(em, entity, tier.prefab, motorMass))
                return;

            var hullState = new ShipHullColliderState
            {
                ChassisId = new FixedString64Bytes(chassisId),
                AppliedShipLevel = ship.ShipLevel,
                AppliedBranchIndex = branchIndex,
            };

            if (em.HasComponent<ShipHullColliderState>(entity))
                em.SetComponentData(entity, hullState);
            else
                em.AddComponentData(entity, hullState);
        }
    }
}
