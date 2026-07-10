using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only: re-applies ship-family chassis stats when ShipLevel or branch index changes.
    /// Compares current level/branch against ShipChassisState (last applied) and calls
    /// ShipStatApplyLogic.ApplyToShip when they differ. Runs after TeamManagementSystem so
    /// team assignment is known before resolving home-planet chassis ladder.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(TeamManagementSystem))]
    public partial struct ShipStatApplySystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var em = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (ship, loadout, entity) in SystemAPI.Query<RefRW<ShipState>, RefRO<ShipLoadoutState>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;

                int branch = loadout.ValueRO.BranchIndex;
                // [STANDARD] Apply stats on first spawn or when level/branch changes.
                bool needsApply = !em.HasComponent<ShipChassisState>(entity);
                if (!needsApply)
                {
                    var chassis = em.GetComponentData<ShipChassisState>(entity);
                    needsApply = chassis.AppliedShipLevel != ship.ValueRO.ShipLevel
                        || chassis.AppliedBranchIndex != branch;
                }

                if (!needsApply)
                    continue;

                ShipStatApplyLogic.ApplyToShip(
                    em, entity, ship.ValueRO.Team, ship.ValueRO.ShipLevel, branch, ecb, queueStructuralChanges: true);
            }

            // Ships without loadout state (legacy prefabs) — default branch 0.
            foreach (var (ship, entity) in SystemAPI.Query<RefRW<ShipState>>()
                         .WithAll<ShipTag>()
                         .WithNone<ShipLoadoutState>()
                         .WithEntityAccess())
            {
                if (ship.ValueRO.IsDead || ship.ValueRO.AwaitingTeamSelection)
                    continue;
                if (em.HasComponent<ShipChassisState>(entity))
                {
                    var chassis = em.GetComponentData<ShipChassisState>(entity);
                    if (chassis.AppliedShipLevel == ship.ValueRO.ShipLevel && chassis.AppliedBranchIndex == 0)
                        continue;
                }

                ShipStatApplyLogic.ApplyToShip(
                    em, entity, ship.ValueRO.Team, ship.ValueRO.ShipLevel, branchIndex: 0, ecb, queueStructuralChanges: true);
            }

            ecb.Playback(em);
            ecb.Dispose();
        }
    }
}
