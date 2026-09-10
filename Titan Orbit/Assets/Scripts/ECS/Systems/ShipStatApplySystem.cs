using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Re-applies ship-family chassis stats when ShipLevel, branch, attributes, or (local client) equipped gear change.
    /// Runs on <b>server</b> (writes ghosted ShipState + motor) and <b>client</b> (motor/weapon/vitals only)
    /// so owner prediction matches authoritative MaxSpeed / thrust.
    /// <para>
    /// [NETCODE] ShipMotorConfig is not a ghost field. Without this on the client, prediction keeps
    /// StarshipGhostAuthoring bake defaults (MaxSpeed≈35) while the server uses chassis moveSpeed≈13.5 —
    /// that mismatch caused HUD bars scaled to 35 and constant reconcile chop on the local ship.
    /// </para>
    /// Team comes from ghosted <see cref="ShipState.Team"/> (client) or TeamManagementSystem (server).
    /// No UpdateAfter(TeamManagement) — that system is server-only and triggers invalid-attribute
    /// warnings when sorting the ClientWorld.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ShipStatApplySystem : ISystem
    {
        /// <summary>
        /// Each sim step: re-apply chassis stats when level, branch, or attribute sum changes.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- System OnUpdate ---
            // [TITAN-ORBIT] Client ship WithEntityAccess + structural ApplyToShip during post–Join
            // Team Instantiates → Crash!!!. Server always applies. Gate with IsClient().
            if (state.World.IsClient() && ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            var em = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // [NETCODE] Server owns ghosted ShipState caps; client only mirrors motor/weapon/vitals.
            bool writeGhostedShipState = state.World.IsServer();

            foreach (var (ship, loadout, entity) in SystemAPI.Query<RefRW<ShipState>, RefRO<ShipLoadoutState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;

                // [NETCODE] ShipState.BranchIndex is the ghosted source of truth for chassis branch.
                int branch = ship.ValueRO.BranchIndex;
                int attrSum = 0;
                if (em.HasComponent<ShipAttributeUpgradeState>(entity))
                    attrSum = ShipStatApplyLogic.SumAttributeLevels(em.GetComponentData<ShipAttributeUpgradeState>(entity));

                // [STANDARD] Apply stats on first spawn or when level/branch/attrs change.
                // [TITAN-ORBIT] Do NOT poll EquippedEquipment on every ship — that lagged the
                // server. Orbit purchases call ApplyToShip on the server immediately. Dedicated
                // clients still need a local-only fingerprint so ghosted gear updates the motor
                // (ShipMotorConfig is not replicated).
                bool needsApply = !em.HasComponent<ShipChassisState>(entity);
                if (!needsApply)
                {
                    var chassis = em.GetComponentData<ShipChassisState>(entity);
                    needsApply = chassis.AppliedShipLevel != ship.ValueRO.ShipLevel
                        || chassis.AppliedBranchIndex != branch
                        || chassis.AppliedShipFamilyConfigIndex != ship.ValueRO.ShipFamilyConfigIndex
                        || chassis.AppliedAttributeSum != attrSum;
                    if (!needsApply && state.World.IsClient() && IsLocalOwnedShip(em, entity))
                    {
                        int fp = ShipStatApplyLogic.ComputeEquippedLoadoutFingerprint(em, entity);
                        needsApply = chassis.AppliedEquipmentFingerprint != fp;
                    }
                }

                if (!needsApply)
                    continue;

                ShipStatApplyLogic.ApplyToShip(
                    em,
                    entity,
                    ship.ValueRO.Team,
                    ship.ValueRO.ShipLevel,
                    branch,
                    ecb,
                    queueStructuralChanges: true,
                    writeGhostedShipState: writeGhostedShipState);
            }

            // Ships without loadout state — still use ghosted ShipState.BranchIndex.
            foreach (var (ship, entity) in SystemAPI.Query<RefRW<ShipState>>()
                         .WithAll<ShipTag>()
                         .WithNone<ShipLoadoutState>()
                         .WithEntityAccess())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;

                int branch = ship.ValueRO.BranchIndex;
                int attrSum = 0;
                if (em.HasComponent<ShipAttributeUpgradeState>(entity))
                    attrSum = ShipStatApplyLogic.SumAttributeLevels(em.GetComponentData<ShipAttributeUpgradeState>(entity));

                if (em.HasComponent<ShipChassisState>(entity))
                {
                    var chassis = em.GetComponentData<ShipChassisState>(entity);
                    bool sameIdentity = chassis.AppliedShipLevel == ship.ValueRO.ShipLevel
                        && chassis.AppliedBranchIndex == branch
                        && chassis.AppliedShipFamilyConfigIndex == ship.ValueRO.ShipFamilyConfigIndex
                        && chassis.AppliedAttributeSum == attrSum;
                    if (sameIdentity
                        && !(state.World.IsClient()
                             && IsLocalOwnedShip(em, entity)
                             && chassis.AppliedEquipmentFingerprint
                                != ShipStatApplyLogic.ComputeEquippedLoadoutFingerprint(em, entity)))
                        continue;
                }

                ShipStatApplyLogic.ApplyToShip(
                    em,
                    entity,
                    ship.ValueRO.Team,
                    ship.ValueRO.ShipLevel,
                    branch,
                    ecb,
                    queueStructuralChanges: true,
                    writeGhostedShipState: writeGhostedShipState);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        /// <summary>
        /// True for the client’s own predicted hull. Used so we only hash one equipment buffer
        /// per tick (not every ship in the match).
        /// </summary>
        static bool IsLocalOwnedShip(EntityManager em, Entity entity)
        {
            if (entity == Entity.Null || !em.Exists(entity))
                return false;

            if (LocalShipEntitySeed.TryGetSeededShip(em, out Entity seeded) && seeded == entity)
                return true;

            return em.HasComponent<GhostOwnerIsLocal>(entity)
                && em.IsComponentEnabled<GhostOwnerIsLocal>(entity);
        }
    }
}
