using TitanOrbit.Core;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [ECS/DOTS] Server win-condition check: if one team owns every planet, declare match won.
    /// [TITAN-ORBIT] Domination victory — neutral planets block a win. Runs after people transport
    /// sim so capture from population transfer is visible first. Writes
    /// <see cref="MatchStateSingleton.WinningTeam"/> — [NETCODE] ghost-replicated to clients.
    /// World: ServerSimulation. Group: SimulationSystemGroup, after PeopleTransportSimulationSystem.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PeopleTransportSimulationSystem))]
    public partial struct CaptureSystem : ISystem
    {
        /// <summary>
        /// [ECS/DOTS] Each server tick: scan all planets; if one team owns every non-neutral planet,
        /// set WinningTeam and GameState = 2 (won).
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            // --- Singleton guards ---
            if (!SystemAPI.TryGetSingletonRW<MatchStateSingleton>(out var match))
                return;
            // [STANDARD] Early exit — winner already decided this match.
            if (match.ValueRO.WinningTeam != TeamId.None)
                return;

            int activeTeams = SystemAPI.GetSingleton<TeamStateSingleton>().ActiveTeamCount;
            if (activeTeams <= 0)
                return;

            // --- Scan planets: all must share one non-None owner ---
            TeamId owner = TeamId.None;
            int ownedCount = 0;

            foreach (var planet in SystemAPI.Query<RefRO<PlanetState>>().WithAll<PlanetTag>())
            {
                var team = planet.ValueRO.Ownership;
                // Neutral planet blocks domination win.
                if (team == TeamId.None)
                    return;

                if (owner == TeamId.None)
                {
                    owner = team;
                    ownedCount = 1;
                    continue;
                }

                if (team != owner)
                    return;

                ownedCount++;
            }

            int totalPlanets = 0;
            foreach (var _ in SystemAPI.Query<RefRO<PlanetState>>().WithAll<PlanetTag>())
                totalPlanets++;

            if (totalPlanets <= 0 || ownedCount < totalPlanets)
                return;

            match.ValueRW.WinningTeam = owner;
            match.ValueRW.GameState = 2;
            LogMatchWon(owner);
        }

        [Unity.Burst.BurstDiscard]
        static void LogMatchWon(TeamId team)
        {
            UnityEngine.Debug.Log($"[CaptureSystem] Match won by {team} — all planets captured.");
        }
    }
}
