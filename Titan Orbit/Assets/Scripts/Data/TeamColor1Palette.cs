using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Designer-owned Colorize <c>_Color1</c> for each playable team.
    /// Players never write these values — hull paint only changes Color2 / Color3 / Glow.
    /// One Colorize material is shared; this asset supplies the team swatch at apply time.
    /// <para>
    /// Edit <c>Assets/Resources/TeamColor1Palette.asset</c> in the Inspector.
    /// Create via <b>Titan Orbit → Team Color 1 Palette</b>.
    /// </para>
    /// </summary>
    [CreateAssetMenu(
        fileName = "TeamColor1Palette",
        menuName = "Titan Orbit/Team Color 1 Palette",
        order = 65)]
    public class TeamColor1Palette : ScriptableObject
    {
        /// <summary>Name passed to <see cref="Resources.Load"/> (no folder / extension).</summary>
        public const string ResourcesLoadName = "TeamColor1Palette";

        [Header("Colorize Color1 (players cannot change)")]
        [Tooltip("Team A Color1 — usually red.")]
        public Color teamA = new Color(0.9f, 0.25f, 0.25f, 1f);

        [Tooltip("Team B Color1 — usually blue.")]
        public Color teamB = new Color(0.25f, 0.4f, 0.9f, 1f);

        [Tooltip("Team C Color1 — usually green.")]
        public Color teamC = new Color(0.2f, 0.7f, 0.28f, 1f);

        [Tooltip("Team D Color1 — usually orange.")]
        public Color teamD = new Color(0.95f, 0.55f, 0.12f, 1f);

        [Tooltip("Team E Color1 — usually purple.")]
        public Color teamE = new Color(0.65f, 0.25f, 0.85f, 1f);

        static TeamColor1Palette s_Cached;

        /// <summary>[UNITY] Domain reload: drop the Resources cache.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_Cached = null;

        /// <summary>Loads the Resources palette once per domain, or null if the asset is missing.</summary>
        public static TeamColor1Palette LoadDefault()
        {
            if (s_Cached != null)
                return s_Cached;
            s_Cached = Resources.Load<TeamColor1Palette>(ResourcesLoadName);
            return s_Cached;
        }

        /// <summary>
        /// Colorize Color1 for <paramref name="team"/>. Falls back to
        /// <see cref="TeamIdExtensions.ToColor"/> when the asset is missing
        /// so hulls still tint in a fresh checkout.
        /// </summary>
        public static Color GetColor1(TeamId team)
        {
            TeamColor1Palette palette = LoadDefault();
            if (palette != null)
                return palette.GetColor1Instance(team);
            return team.ToColor();
        }

        /// <summary>Instance lookup used by the Inspector-authored fields.</summary>
        public Color GetColor1Instance(TeamId team)
        {
            switch (team)
            {
                case TeamId.TeamA: return Opaque(teamA);
                case TeamId.TeamB: return Opaque(teamB);
                case TeamId.TeamC: return Opaque(teamC);
                case TeamId.TeamD: return Opaque(teamD);
                case TeamId.TeamE: return Opaque(teamE);
                default: return Color.white;
            }
        }

        static Color Opaque(Color color)
        {
            color.a = 1f;
            return color;
        }
    }
}
