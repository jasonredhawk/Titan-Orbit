using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
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

        static bool NeedsHullSync(EntityManager em, Entity entity, in ShipState ship, int branchIndex)
        {
            if (!ShipStatApplyLogic.TryResolveChassisId(
                    ship.Team,
                    ship.ShipLevel,
                    branchIndex,
                    out string chassisId,
                    allowFallback: true,
                    shipFamilyConfigIndex: ship.ShipFamilyConfigIndex))
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
            if (!ShipStatApplyLogic.TryResolveChassisId(
                    ship.Team,
                    ship.ShipLevel,
                    branchIndex,
                    out string chassisId,
                    allowFallback: true,
                    shipFamilyConfigIndex: ship.ShipFamilyConfigIndex))
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
