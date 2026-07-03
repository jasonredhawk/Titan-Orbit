using TitanOrbit.Core;

namespace TitanOrbit.Core
{
    /// <summary>Legacy team enum shim for OrbitStationUI and data assets (maps to <see cref="TeamId"/>).</summary>
    public static class TeamManager
    {
        public enum Team
        {
            None = 0,
            TeamA = 1,
            TeamB = 2,
            TeamC = 3,
            TeamD = 4,
            TeamE = 5
        }

        public static TeamId ToTeamId(Team team) => (TeamId)(byte)team;

        public static Team FromTeamId(TeamId teamId) => (Team)(byte)teamId;
    }
}
