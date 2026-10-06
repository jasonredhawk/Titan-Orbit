using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Rebuilds <see cref="ShipCommandRoleSnapshot"/> from living ships' match scores.
    /// Runs first in SimulationSystemGroup on both ServerSimulation and ClientSimulation
    /// so the same tick's bullets, mining, and troop hops see this frame's winners.
    /// <para>
    /// [NETCODE] Snapshot is local — not a ghost. Both worlds read the same ghosted
    /// <see cref="ShipMatchStats"/>, so predicted fire-power and server gems agree.
    /// Ships are a handful; one ToComponentDataArray per tick is cheaper than a
    /// per-shot scan.
    /// </para>
    /// Rules match nameplates: dead / unteamed / awaiting-pick hulls never win;
    /// a zero score never wins; ties → lowest NetworkId
    /// (<see cref="TeamCommandRoleRules.IsBetterTop"/>).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial struct ShipCommandRoleRefreshSystem : ISystem
    {
        EntityQuery _ships;

        /// <summary>Caches the ship query and ensures the singleton exists.</summary>
        public void OnCreate(ref SystemState state)
        {
            _ships = state.GetEntityQuery(
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<GhostOwner>(),
                ComponentType.ReadOnly<ShipState>(),
                ComponentType.ReadOnly<ShipMatchStats>());

            if (!SystemAPI.HasSingleton<ShipCommandRoleSnapshot>())
            {
                Entity singleton = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(singleton, new ShipCommandRoleSnapshot());
            }

            state.RequireForUpdate<ShipCommandRoleSnapshot>();
        }

        /// <summary>Replaces last tick's winners from the current living scoreboard.</summary>
        public void OnUpdate(ref SystemState state)
        {
            var snap = SystemAPI.GetSingleton<ShipCommandRoleSnapshot>();
            snap.Clear();

            using var owners = _ships.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var ships = _ships.ToComponentDataArray<ShipState>(Allocator.Temp);
            using var stats = _ships.ToComponentDataArray<ShipMatchStats>(Allocator.Temp);

            // Per-team running best — five playable factions, index = (int)TeamId.
            var bestKill = new NativeArray<int>(6, Allocator.Temp);
            var bestGem = new NativeArray<int>(6, Allocator.Temp);
            var bestPeople = new NativeArray<int>(6, Allocator.Temp);
            var killerId = new NativeArray<int>(6, Allocator.Temp);
            var minerId = new NativeArray<int>(6, Allocator.Temp);
            var troopId = new NativeArray<int>(6, Allocator.Temp);

            for (int i = 0; i < owners.Length; i++)
            {
                ShipState ship = ships[i];
                if (ship.IsDead || ship.AwaitingTeamSelection || ship.Team == TeamId.None)
                    continue;

                int id = owners[i].NetworkId;
                if (id <= 0)
                    continue;

                int team = (int)ship.Team;
                if (team < 1 || team > 5)
                    continue;

                ShipMatchStats row = stats[i];

                // --- Top killer ---
                if (row.Kills > 0 &&
                    TeamCommandRoleRules.IsBetterTop(row.Kills, id, bestKill[team], killerId[team]))
                {
                    bestKill[team] = row.Kills;
                    killerId[team] = id;
                }

                // --- Top miner ---
                if (row.GemsDeposited > 0 &&
                    TeamCommandRoleRules.IsBetterTop(
                        row.GemsDeposited, id, bestGem[team], minerId[team]))
                {
                    bestGem[team] = row.GemsDeposited;
                    minerId[team] = id;
                }

                // --- Top troops ---
                if (row.PeopleDelivered > 0 &&
                    TeamCommandRoleRules.IsBetterTop(
                        row.PeopleDelivered, id, bestPeople[team], troopId[team]))
                {
                    bestPeople[team] = row.PeopleDelivered;
                    troopId[team] = id;
                }
            }

            for (int team = 1; team <= 5; team++)
            {
                snap.Set(
                    (TeamId)team,
                    killerId[team],
                    minerId[team],
                    troopId[team]);
            }

            SystemAPI.SetSingleton(snap);

            bestKill.Dispose();
            bestGem.Dispose();
            bestPeople.Dispose();
            killerId.Dispose();
            minerId.Dispose();
            troopId.Dispose();
        }
    }
}
