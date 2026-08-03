using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Maps each <see cref="TeamId"/> to a GenericSpaceships1-8 team skin for people transports,
    /// attack/mining/shield drones, and planetary defense turrets.
    /// Albedos are the pack atlases (<c>GenericSpaceships_Red/Blue/Green/…</c>).
    /// TeamB uses a tinted mat (<c>Resources/TeamSkins/GenericSpaceships1-8_TeamB_Blue</c>)
    /// because the pack’s “Blue” albedo is authored teal/olive and reads green without a tint.
    /// </summary>
    [CreateAssetMenu(
        fileName = "PeopleTransportTeamMaterials",
        menuName = "Titan Orbit/People Transport Team Materials")]
    public class PeopleTransportTeamMaterials : ScriptableObject
    {
        /// <summary>Resources path used at runtime (<c>Resources.Load</c>).</summary>
        public const string ResourcesPath = "PeopleTransportTeamMaterials";

        /// <summary>TeamA — <c>GenericSpaceships_Red.png</c>.</summary>
        public Material Red;

        /// <summary>
        /// TeamB — <c>GenericSpaceships_Blue.png</c> with a blue BaseColor multiply
        /// (<c>GenericSpaceships1-8_TeamB_Blue</c>) so it reads as team blue, not teal.
        /// </summary>
        public Material Blue;

        /// <summary>TeamC — <c>GenericSpaceships_Green.png</c>.</summary>
        public Material Green;

        /// <summary>TeamD — <c>GenericSpaceships_GreenYellow.png</c> (orange/yellow stand-in).</summary>
        public Material GreenYellow;

        /// <summary>TeamE — <c>GenericSpaceships_Violet.png</c> (indigo + magenta).</summary>
        public Material Violet;

        /// <summary>None / unknown — <c>GenericSpaceships_Grey.png</c>.</summary>
        public Material Grey;

        /// <summary>Returns the pack material for <paramref name="team"/> (never creates a tinted clone).</summary>
        public Material GetMaterialForTeam(TeamId team)
        {
            switch (team)
            {
                case TeamId.TeamA: return Red != null ? Red : Grey;
                case TeamId.TeamB: return Blue != null ? Blue : Grey;
                case TeamId.TeamC: return Green != null ? Green : Grey;
                case TeamId.TeamD: return GreenYellow != null ? GreenYellow : Grey;
                case TeamId.TeamE: return Violet != null ? Violet : Grey;
                default: return Grey;
            }
        }
    }
}
