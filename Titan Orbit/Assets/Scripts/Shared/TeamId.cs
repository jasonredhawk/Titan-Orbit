namespace TitanOrbit.Core
{
    /// <summary>Team assignment used across ECS ghosts and UI.</summary>
    public enum TeamId : byte
    {
        None = 0,
        TeamA = 1,
        TeamB = 2,
        TeamC = 3,
        TeamD = 4,
        TeamE = 5
    }

    public static class TeamIdExtensions
    {
        public static UnityEngine.Color ToColor(this TeamId team)
        {
            switch (team)
            {
                case TeamId.TeamA: return new UnityEngine.Color(0.9f, 0.25f, 0.25f);
                case TeamId.TeamB: return new UnityEngine.Color(0.25f, 0.4f, 0.9f);
                case TeamId.TeamC: return new UnityEngine.Color(0.2f, 0.7f, 0.28f);
                case TeamId.TeamD: return new UnityEngine.Color(0.95f, 0.55f, 0.12f);
                case TeamId.TeamE: return new UnityEngine.Color(0.65f, 0.25f, 0.85f);
                default: return UnityEngine.Color.white;
            }
        }

        public static int ToMaskBit(this TeamId team) =>
            team == TeamId.None ? 0 : 1 << ((int)team - 1);
    }
}
