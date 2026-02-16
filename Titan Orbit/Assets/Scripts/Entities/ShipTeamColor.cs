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

        [Tooltip("If set, only these renderers get team color (two-tone: body stays dark grey). Leave empty to color entire ship.")]
        [SerializeField] private Renderer[] accentRenderers;

        private Starship starship;
        private Renderer[] allRenderers;
        private MaterialPropertyBlock propBlock;

        private void Awake()
        {
            starship = GetComponent<Starship>();
            allRenderers = GetComponentsInChildren<Renderer>();
            propBlock = new MaterialPropertyBlock();
        }

        private void Update()
        {
            Color c = GetTeamColor(starship.ShipTeam);
            var targetRenderers = GetAccentRenderers();

            foreach (var r in targetRenderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(propBlock);
                propBlock.SetColor("_BaseColor", c);
                r.SetPropertyBlock(propBlock);
            }
        }

        private Renderer[] GetAccentRenderers()
        {
            if (accentRenderers != null)
            {
                var valid = System.Array.FindAll(accentRenderers, r => r != null);
                if (valid.Length > 0) return valid;
            }
            var list = new System.Collections.Generic.List<Renderer>();
            foreach (var r in allRenderers)
            {
                if (r == null) continue;
                string n = r.gameObject.name;
                if (n == "Cockpit" || n.StartsWith("Engine") || n.StartsWith("Wing"))
                    list.Add(r);
            }
            return list.Count > 0 ? list.ToArray() : allRenderers;
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
