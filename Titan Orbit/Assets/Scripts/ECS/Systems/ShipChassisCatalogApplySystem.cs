using TitanOrbit.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Applies baked weapon mounts, wing tractor beams, and hull colliders from
    /// <see cref="ShipChassisVisualCatalog"/> when chassis/level/team changes. Runs on server
    /// and client so sim buffers match without GameObject hull proxies.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ShipStatApplySystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipChassisCatalogApplySystem : ISystem
    {
        /// <summary>Ship that needs catalog data applied after the query pass completes.</summary>
        struct PendingCatalogApply
        {
            public Entity Entity;
            public int BranchIndex;
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!TitanOrbitPresentationConfig.UseEntitiesGraphicsForShips)
                return;

            var catalog = ShipChassisVisualCatalog.Instance;
            var config = ShipStatApplyLogic.Config;
            if (catalog == null || config == null)
                return;

            var em = state.EntityManager;
            var pending = new NativeList<PendingCatalogApply>(Allocator.Temp);

            // --- Pass 1: read-only query — collect ships that need catalog rebuild ---
            foreach (var (ship, loadout, entity) in SystemAPI
                         .Query<RefRO<ShipState>, RefRO<ShipLoadoutState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;

                if (NeedsCatalogApply(em, catalog, entity, ship.ValueRO, loadout.ValueRO.BranchIndex))
                {
                    pending.Add(new PendingCatalogApply
                    {
                        Entity = entity,
                        BranchIndex = loadout.ValueRO.BranchIndex,
                    });
                }
            }

            foreach (var (ship, entity) in SystemAPI
                         .Query<RefRO<ShipState>>()
                         .WithAll<ShipTag>()
                         .WithNone<ShipLoadoutState>()
                         .WithEntityAccess())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;

                if (NeedsCatalogApply(em, catalog, entity, ship.ValueRO, branchIndex: 0))
                {
                    pending.Add(new PendingCatalogApply
                    {
                        Entity = entity,
                        BranchIndex = 0,
                    });
                }
            }

            // --- Pass 2: structural changes (buffers, colliders, tracking component) ---
            for (int i = 0; i < pending.Length; i++)
            {
                var work = pending[i];
                if (!em.Exists(work.Entity) || !em.HasComponent<ShipState>(work.Entity))
                    continue;

                var ship = em.GetComponentData<ShipState>(work.Entity);
                TryApplyCatalogData(em, config, catalog, work.Entity, ship, work.BranchIndex);
            }

            pending.Dispose();
        }

        static bool NeedsCatalogApply(
            EntityManager em,
            ShipChassisVisualCatalog catalog,
            Entity entity,
            in ShipState ship,
            int branchIndex)
        {
            if (!ShipStatApplyLogic.TryResolveChassisId(ship.Team, ship.ShipLevel, branchIndex, out string chassisId))
                return false;

            if (!catalog.TryGetEntry(chassisId, out _))
                return false;

            if (!em.HasComponent<ShipHullColliderState>(entity))
                return true;

            var applied = em.GetComponentData<ShipHullColliderState>(entity);
            var chassisKey = new FixedString64Bytes(chassisId);
            return !applied.ChassisId.Equals(chassisKey)
                || applied.AppliedShipLevel != ship.ShipLevel
                || applied.AppliedBranchIndex != branchIndex;
        }

        static void TryApplyCatalogData(
            EntityManager em,
            PlanetShipFamilyConfig config,
            ShipChassisVisualCatalog catalog,
            Entity entity,
            in ShipState ship,
            int branchIndex)
        {
            if (!ShipStatApplyLogic.TryResolveChassisId(ship.Team, ship.ShipLevel, branchIndex, out string chassisId))
                return;

            if (!catalog.TryGetEntry(chassisId, out var entry))
                return;

            ApplyWeaponMounts(em, entity, entry);
            ApplyWingTractorBeams(em, entity, entry);

            var tier = config.GetTierEntryForChassisId(chassisId);
            if (tier?.prefab != null)
            {
                float motorMass = 1f;
                if (em.HasComponent<ShipMotorConfig>(entity))
                    motorMass = em.GetComponentData<ShipMotorConfig>(entity).Mass;

                ShipHullColliderLogic.TryApplyChassisCollider(em, entity, tier.prefab, motorMass);
            }

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

        static void ApplyWeaponMounts(EntityManager em, Entity entity, ShipChassisVisualEntry entry)
        {
            if (!em.HasBuffer<ShipWeaponMountElement>(entity))
                em.AddBuffer<ShipWeaponMountElement>(entity);

            var buffer = em.GetBuffer<ShipWeaponMountElement>(entity);
            buffer.Clear();

            for (int i = 0; i < entry.WeaponMounts.Count; i++)
            {
                var mount = entry.WeaponMounts[i];
                buffer.Add(new ShipWeaponMountElement
                {
                    LocalPosition = mount.LocalPosition,
                    LocalRotation = mount.LocalRotation,
                    DirectionAngleDeg = mount.DirectionAngleDeg,
                    CannonIndex = mount.CannonIndex,
                });
            }
        }

        static void ApplyWingTractorBeams(EntityManager em, Entity entity, ShipChassisVisualEntry entry)
        {
            if (!em.HasBuffer<ShipWingTractorBeamElement>(entity))
                em.AddBuffer<ShipWingTractorBeamElement>(entity);

            var buffer = em.GetBuffer<ShipWingTractorBeamElement>(entity);
            buffer.Clear();

            for (int i = 0; i < entry.WingTractorBeams.Count; i++)
            {
                var wing = entry.WingTractorBeams[i];
                buffer.Add(new ShipWingTractorBeamElement
                {
                    LocalPosition = wing.LocalPosition,
                    TractorBeamDistance = wing.TractorBeamDistance,
                    TractorBeamDistancePerLevel = wing.TractorBeamDistancePerLevel,
                    TractorBeamPower = wing.TractorBeamPower,
                    TractorBeamPowerPerLevel = wing.TractorBeamPowerPerLevel,
                    MaxGems = wing.MaxGems,
                    MaxGemsPerLevel = wing.MaxGemsPerLevel,
                });
            }
        }
    }
}
