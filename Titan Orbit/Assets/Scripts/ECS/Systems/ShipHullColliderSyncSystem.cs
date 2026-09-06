using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Physics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Ensures each ship keeps a single covering ellipsoid that fits every chassis box after
    /// attribute grow (X/Y/Z radii independent). Cached until chassis / attributes change.
    /// Tier / MEGA size is <c>LocalTransform.Scale</c>.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ShipStatApplySystem))]
    [UpdateAfter(typeof(ShipChassisCatalogApplySystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct ShipHullColliderSyncSystem : ISystem
    {
        /// <summary>Ship queued for a hull collider rebuild after the read-only query pass.</summary>
        struct PendingHullSync
        {
            public Entity Entity;
            public int BranchIndex;
        }

        /// <summary>
        /// Finds ships whose hull bake inputs changed, then swaps PhysicsCollider (and re-pins
        /// docked moons on the server).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (TitanOrbitPresentationConfig.UseEntitiesGraphicsForShips)
                return;

            // Dedicated still syncs: covering-sphere bake Instantiates the chassis once
            // per upgrade so attribute grow and MEGA nested boxes are visible.

            // [TITAN-ORBIT] Client: ship WithEntityAccess + collider structural swap during
            // GhostSpawn Instantiates Crash!!!. Server always syncs. Gate with IsClient().
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipEntityQueries)
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

                int branch = ship.ValueRO.BranchIndex;
                if (NeedsHullSync(em, entity, ship.ValueRO, branch))
                {
                    pending.Add(new PendingHullSync
                    {
                        Entity = entity,
                        BranchIndex = branch,
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

            // Server-only moon re-pin after collider swap (client TransformQuarantine forbids planet gather).
            bool serverReattach = state.World.IsServer();
            float mapW = 0f;
            float mapH = 0f;
            double moonElapsed = SystemAPI.Time.ElapsedTime;
            bool haveMapSize = false;
            if (serverReattach)
            {
                haveMapSize = ShipMoonDockAttachLogic.TryGetMapSize(em, out mapW, out mapH);
                int hz = 0;
                if (SystemAPI.TryGetSingleton<ClientServerTickRate>(out var tickRate))
                    hz = tickRate.SimulationTickRate;
                if (SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime))
                    moonElapsed = PlanetGemMoonOrbitClock.GetElapsedSeconds(
                        networkTime, hz, includeTickFraction: false);
            }

            for (int i = 0; i < pending.Length; i++)
            {
                var work = pending[i];
                if (!em.Exists(work.Entity) || !em.HasComponent<ShipState>(work.Entity))
                    continue;

                var ship = em.GetComponentData<ShipState>(work.Entity);
                TrySyncHull(work.Entity, em, config, ship, work.BranchIndex);

                // [TITAN-ORBIT] New hull collider while docked can Physics-eject the ship — re-pin.
                // Skip when map size is missing (do not invent a period for moon attach math).
                if (serverReattach && haveMapSize)
                {
                    ShipMoonDockAttachLogic.TryReattachFullyDockedShip(
                        em, work.Entity, mapW, mapH, moonElapsed);
                }
            }

            pending.Dispose();
        }

        /// <summary>
        /// True when covering-sphere inputs or the team shield filter differ from the last bake.
        /// Level-only growth uses <c>LocalTransform.Scale</c> and does not re-walk the prefab.
        /// </summary>
        static bool NeedsHullSync(EntityManager em, Entity entity, in ShipState ship, int branchIndex)
        {
            if (!ShipStatApplyLogic.TryResolveChassisId(
                    em,
                    entity,
                    ship.Team,
                    ship.ShipLevel,
                    branchIndex,
                    out string chassisId,
                    allowFallback: true))
                return false;

            int attributeSum = 0;
            if (em.HasComponent<ShipAttributeUpgradeState>(entity))
                attributeSum = ShipStatApplyLogic.SumAttributeLevels(
                    em.GetComponentData<ShipAttributeUpgradeState>(entity));

            if (!em.HasComponent<ShipHullColliderState>(entity))
                return true;

            var applied = em.GetComponentData<ShipHullColliderState>(entity);
            var chassisKey = new FixedString64Bytes(chassisId);
            bool isMega = em.HasComponent<MegaShipState>(entity)
                && em.GetComponentData<MegaShipState>(entity).IsMega;
            return ShipHullColliderLogic.NeedsCoveringRecompute(
                       applied, chassisKey, branchIndex, attributeSum, isMega)
                || applied.AppliedTeam != (byte)ship.Team;
        }

        /// <summary>
        /// Measures (or reuses) the covering sphere and stamps <see cref="ShipHullColliderState"/>.
        /// </summary>
        static void TrySyncHull(
            Entity entity,
            EntityManager em,
            PlanetShipFamilyConfig config,
            in ShipState ship,
            int branchIndex)
        {
            if (!ShipStatApplyLogic.TryResolveChassisId(
                    em,
                    entity,
                    ship.Team,
                    ship.ShipLevel,
                    branchIndex,
                    out string chassisId,
                    allowFallback: true))
                return;

            float motorMass = 1f;
            if (em.HasComponent<ShipMotorConfig>(entity))
                motorMass = em.GetComponentData<ShipMotorConfig>(entity).Mass;

            var attrs = default(ShipAttributeUpgradeState);
            int attributeSum = 0;
            if (em.HasComponent<ShipAttributeUpgradeState>(entity))
            {
                attrs = em.GetComponentData<ShipAttributeUpgradeState>(entity);
                attributeSum = ShipStatApplyLogic.SumAttributeLevels(attrs);
            }

            bool isMega = em.HasComponent<MegaShipState>(entity)
                && em.GetComponentData<MegaShipState>(entity).IsMega;
            if (isMega)
                motorMass = math.max(motorMass, MegaShipCatalog.DefaultHullCollisionMass);

            var chassisKey = new FixedString64Bytes(chassisId);
            bool recompute = true;
            float3 cachedExtents = new float3(-1f);
            float3 cachedCenter = float3.zero;
            int megaRevision = 0;
            if (em.HasComponent<ShipHullColliderState>(entity))
            {
                var prev = em.GetComponentData<ShipHullColliderState>(entity);
                megaRevision = prev.AppliedMegaColliderRevision;
                recompute = ShipHullColliderLogic.NeedsCoveringRecompute(
                    prev, chassisKey, branchIndex, attributeSum, isMega);
                if (!recompute)
                {
                    cachedExtents = ShipHullColliderLogic.GetCachedCoveringExtents(prev);
                    cachedCenter = ShipHullColliderLogic.GetCachedCoveringCenter(prev);
                }
            }

            GameObject chassisPrefab = recompute ? ResolveChassisPrefab(config, chassisId) : null;
            string familyPrefix = ResolveFamilyPrefix(chassisId);
            ShipHullColliderLogic.TryApplyCoveringHull(
                em, entity, chassisPrefab, motorMass, attrs, familyPrefix, isMega,
                cachedExtents, cachedCenter, out float3 usedCenter, out float3 usedExtents);

            var hullState = new ShipHullColliderState
            {
                ChassisId = chassisKey,
                AppliedShipLevel = ship.ShipLevel,
                AppliedBranchIndex = branchIndex,
                AppliedAttributeSum = attributeSum,
                AppliedMegaColliderRevision = isMega
                    ? MegaShipCatalog.HullColliderRevision
                    : megaRevision,
                AppliedHullMaterialRevision = ShipHullColliderLogic.HullMaterialRevision,
                AppliedTeam = (byte)ship.Team,
                AppliedCoveringRadius = math.cmax(usedExtents),
                AppliedCoveringExtentX = usedExtents.x,
                AppliedCoveringExtentY = usedExtents.y,
                AppliedCoveringExtentZ = usedExtents.z,
                AppliedCoveringCenterX = usedCenter.x,
                AppliedCoveringCenterY = usedCenter.y,
                AppliedCoveringCenterZ = usedCenter.z,
            };

            if (em.HasComponent<ShipHullColliderState>(entity))
                em.SetComponentData(entity, hullState);
            else
                em.AddComponentData(entity, hullState);
        }

        static GameObject ResolveChassisPrefab(PlanetShipFamilyConfig config, string chassisId)
        {
            var tier = config != null ? config.GetTierEntryForChassisId(chassisId) : null;
            if (tier != null && tier.prefab != null)
                return tier.prefab;

            if (MegaShipCatalog.IsMegaChassisId(chassisId))
            {
                var mega = MegaShipCatalog.Load();
                if (mega != null)
                    return mega.GetPrefabByChassisId(chassisId);
            }

            return null;
        }

        static string ResolveFamilyPrefix(string chassisId)
        {
            if (string.IsNullOrEmpty(chassisId))
                return "AstroEagle";
            int underscore = chassisId.IndexOf('_');
            if (underscore > 0)
                return chassisId.Substring(0, underscore);
            return chassisId;
        }
    }
}
