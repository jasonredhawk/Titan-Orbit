using UnityEngine;
using TitanOrbit.Core;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Colors the ship based on team for easy identification
    /// </summary>
    [RequireComponent(typeof(Starship))]
    public class ShipTeamColor : MonoBehaviour
    {
        [Header("Team Colors")]
        [SerializeField] private Color teamAColor = new Color(1f, 0.35f, 0.35f);
        [SerializeField] private Color teamBColor = new Color(0.35f, 0.55f, 1f);
        [SerializeField] private Color teamCColor = new Color(0.35f, 1f, 0.45f);

        private Starship starship;
        private Renderer[] renderers;
        private MaterialPropertyBlock propBlock;

        private void Awake()
        {
            starship = GetComponent<Starship>();
            renderers = GetComponentsInChildren<Renderer>();
            propBlock = new MaterialPropertyBlock();
        }

        private void Update()
        {
            Color c = GetTeamColor(starship.ShipTeam);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(propBlock);
                propBlock.SetColor("_BaseColor", c);
                r.SetPropertyBlock(propBlock);
            }
        }

        private Color GetTeamColor(TeamManager.Team team)
        {
            switch (team)
            {
                case TeamManager.Team.TeamA: return teamAColor;
                case TeamManager.Team.TeamB: return teamBColor;
                case TeamManager.Team.TeamC: return teamCColor;
                default: return Color.gray;
            }
        }
    }
}
