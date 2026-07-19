using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Maps each <see cref="TeamId"/> to a GenericSpaceships1-8 material for people-transport ship meshes.
    /// Loaded from Resources by <c>PeopleTransportVisualApplier</c> so player builds include the mats
    /// without duplicating asset files (refs point at UltimateSpaceshipsCreator materials).
    /// </summary>
    [CreateAssetMenu(
        fileName = "PeopleTransportTeamMaterials",
        menuName = "Titan Orbit/People Transport Team Materials")]
    public class PeopleTransportTeamMaterials : ScriptableObject
    {
        /// <summary>Resources path used at runtime (<c>Resources.Load</c>).</summary>
        public const string ResourcesPath = "PeopleTransportTeamMaterials";

        /// <summary>TeamA — red faction material.</summary>
        public Material Red;

        /// <summary>TeamB — blue faction material.</summary>
        public Material Blue;

        /// <summary>TeamC — green faction material.</summary>
        public Material Green;

        /// <summary>TeamD — green-yellow (closest pack match to orange/yellow team UI).</summary>
        public Material GreenYellow;

        /// <summary>TeamE — violet faction material.</summary>
        public Material Violet;

        /// <summary>None / unknown — grey neutral material.</summary>
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
