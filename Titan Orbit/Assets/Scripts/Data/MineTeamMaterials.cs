using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Maps each <see cref="TeamId"/> to a Bomb_4 material so deployed mines match the owner's team.
    /// One asset at <c>Resources/MineTeamMaterials</c>.
    /// [TITAN-ORBIT] Same color order as ships: A red, B blue, C green, D orange, E purple.
    /// Paired with <c>MineVisualDriver</c> (client mesh tint).
    /// </summary>
    [CreateAssetMenu(
        fileName = "MineTeamMaterials",
        menuName = "Titan Orbit/Mine Team Materials")]
    public class MineTeamMaterials : ScriptableObject
    {
        /// <summary>Resources path used at runtime (<c>Resources.Load</c>).</summary>
        public const string ResourcesPath = "MineTeamMaterials";

        /// <summary>[UNITY] Sole asset path for Editor fallback loads.</summary>
        public const string ResourcesAssetPath = "Assets/Resources/MineTeamMaterials.asset";

        /// <summary>TeamA — Bomb_4_Red.</summary>
        public Material Red;

        /// <summary>TeamB — Bomb_4_Blue.</summary>
        public Material Blue;

        /// <summary>TeamC — Bomb_4_Green.</summary>
        public Material Green;

        /// <summary>TeamD — Bomb_4_Orange.</summary>
        public Material Orange;

        /// <summary>TeamE — Bomb_4_Purple.</summary>
        public Material Purple;

        static MineTeamMaterials _cached;

        /// <summary>Loads the Resources asset once per domain.</summary>
        public static MineTeamMaterials LoadDefault()
        {
            // --- Cache ---
            if (_cached != null)
                return _cached;

            _cached = Resources.Load<MineTeamMaterials>(ResourcesPath);
#if UNITY_EDITOR
            if (_cached == null)
                _cached = UnityEditor.AssetDatabase.LoadAssetAtPath<MineTeamMaterials>(ResourcesAssetPath);
#endif
            return _cached;
        }

        /// <summary>Returns the Bomb_4 material for <paramref name="team"/> (Red if unknown).</summary>
        public Material GetMaterialForTeam(TeamId team)
        {
            switch (team)
            {
                case TeamId.TeamA: return Red != null ? Red : Blue;
                case TeamId.TeamB: return Blue != null ? Blue : Red;
                case TeamId.TeamC: return Green != null ? Green : Red;
                case TeamId.TeamD: return Orange != null ? Orange : Red;
                case TeamId.TeamE: return Purple != null ? Purple : Red;
                default: return Red;
            }
        }
    }
}
