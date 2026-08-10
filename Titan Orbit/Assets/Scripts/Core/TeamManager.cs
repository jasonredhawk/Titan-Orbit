using TitanOrbit.Core;

namespace TitanOrbit.Core
{
    // --- Type members ---
    /// <summary>
    /// Legacy team enum shim kept for Orbit-station UI and older data assets that predate
    /// <see cref="TeamId"/>. New ECS and NetCode code should use <see cref="TeamId"/> directly;
    /// this type exists only to avoid a large mechanical rename across UI and ScriptableObjects.
    /// Client and editor only — server sim stores team as <see cref="TeamId"/> on ship ghosts.
    /// </summary>
    public static class TeamManager
    {
        /// <summary>
        /// [LEGACY] Integer-backed team slots (None + five playable teams). Values match
        /// <see cref="TeamId"/> byte encoding so casts are lossless.
        /// </summary>
        public enum Team
        {
            None = 0,
            TeamA = 1,
            TeamB = 2,
            TeamC = 3,
            TeamD = 4,
            TeamE = 5
        }

        /// <summary>
        /// Converts legacy <see cref="Team"/> to ECS-safe <see cref="TeamId"/>.
        /// Called from UI when sending team-choice RPCs or coloring panels.
        /// </summary>
        public static TeamId ToTeamId(Team team) => (TeamId)(byte)team;

        /// <summary>
        /// Converts replicated <see cref="TeamId"/> back to legacy enum for UI bindings.
        /// </summary>
        public static Team FromTeamId(TeamId teamId) => (Team)(byte)teamId;
    }
}
