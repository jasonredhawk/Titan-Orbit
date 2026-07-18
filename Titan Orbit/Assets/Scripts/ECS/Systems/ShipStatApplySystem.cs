using TitanOrbit.Core;
using TitanOrbit.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Re-applies ship-family chassis stats when ShipLevel, branch index, or attribute upgrades change.
    /// Runs on <b>server</b> (writes ghosted ShipState + motor) and <b>client</b> (motor/weapon/vitals only)
    /// so owner prediction matches authoritative MaxSpeed / thrust.
    /// <para>
    /// [NETCODE] ShipMotorConfig is not a ghost field. Without this on the client, prediction keeps
    /// StarshipGhostAuthoring bake defaults (MaxSpeed≈35) while the server uses chassis moveSpeed≈13.5 —
    /// that mismatch caused HUD bars scaled to 35 and constant reconcile chop on the local ship.
    /// </para>
    /// Runs after TeamManagementSystem so team assignment is known before resolving home-planet chassis.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(TeamManagementSystem))]
    public partial struct ShipStatApplySystem : ISystem
    {
        /// <summary>One-shot debug: prove client applied chassis MaxSpeed (not bake 35).</summary>
        static bool s_loggedClientMotorOnce;

        public void OnUpdate(ref SystemState state)
        {
            // --- System OnUpdate ---
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

                int branch = loadout.ValueRO.BranchIndex;
                int attrSum = 0;
                if (em.HasComponent<ShipAttributeUpgradeState>(entity))
                    attrSum = ShipStatApplyLogic.SumAttributeLevels(em.GetComponentData<ShipAttributeUpgradeState>(entity));

                // [STANDARD] Apply stats on first spawn or when level/branch/attrs change.
                bool needsApply = !em.HasComponent<ShipChassisState>(entity);
                if (!needsApply)
                {
                    var chassis = em.GetComponentData<ShipChassisState>(entity);
                    needsApply = chassis.AppliedShipLevel != ship.ValueRO.ShipLevel
                        || chassis.AppliedBranchIndex != branch
                        || chassis.AppliedAttributeSum != attrSum;
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

                // #region agent log
                LogClientMotorApplyOnce(ref state, em, entity, writeGhostedShipState);
                // #endregion
            }

            // Ships without loadout state (legacy prefabs) — default branch 0.
            foreach (var (ship, entity) in SystemAPI.Query<RefRW<ShipState>>()
                         .WithAll<ShipTag>()
                         .WithNone<ShipLoadoutState>()
                         .WithEntityAccess())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;

                int attrSum = 0;
                if (em.HasComponent<ShipAttributeUpgradeState>(entity))
                    attrSum = ShipStatApplyLogic.SumAttributeLevels(em.GetComponentData<ShipAttributeUpgradeState>(entity));

                if (em.HasComponent<ShipChassisState>(entity))
                {
                    var chassis = em.GetComponentData<ShipChassisState>(entity);
                    if (chassis.AppliedShipLevel == ship.ValueRO.ShipLevel
                        && chassis.AppliedBranchIndex == 0
                        && chassis.AppliedAttributeSum == attrSum)
                        continue;
                }

                ShipStatApplyLogic.ApplyToShip(
                    em,
                    entity,
                    ship.ValueRO.Team,
                    ship.ValueRO.ShipLevel,
                    branchIndex: 0,
                    ecb,
                    queueStructuralChanges: true,
                    writeGhostedShipState: writeGhostedShipState);

                // #region agent log
                LogClientMotorApplyOnce(ref state, em, entity, writeGhostedShipState);
                // #endregion
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        // #region agent log
        /// <summary>H75: log first client motor apply so logs prove MaxSpeed left bake default 35.</summary>
        static void LogClientMotorApplyOnce(ref SystemState state, EntityManager em, Entity entity, bool writeGhostedShipState)
        {
            if (writeGhostedShipState || s_loggedClientMotorOnce)
                return;
            if (!em.HasComponent<ShipMotorConfig>(entity))
                return;

            s_loggedClientMotorOnce = true;
            var motor = em.GetComponentData<ShipMotorConfig>(entity);
            var ship = em.GetComponentData<ShipState>(entity);
            ShipFlightSmoothDebugLog.Write(
                "H75",
                "ShipStatApplySystem.OnUpdate",
                "client motor apply",
                "{\"maxSpeed\":" + motor.MaxSpeed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"thrust\":" + motor.EngineThrust.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                ",\"level\":" + ship.ShipLevel +
                ",\"world\":\"" + state.WorldUnmanaged.Name.ToString() + "\"}");
        }
        // #endregion
    }
}
