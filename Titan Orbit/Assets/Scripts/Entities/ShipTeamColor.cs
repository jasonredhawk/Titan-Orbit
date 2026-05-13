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
        [SerializeField] private Color teamCColor = new Color(0.2f, 0.7f, 0.28f);

        [Tooltip("If set, only these renderers get team color (two-tone: body stays dark grey). Leave empty to color entire ship.")]
        [SerializeField] private Renderer[] accentRenderers;
        [Tooltip("Legacy mode: apply runtime _BaseColor tint each frame. Disable when using team-specific materials from ShipFamilyDefinition.")]
        [SerializeField] private bool enableLegacyTint = false;

        private Starship starship;
        private MaterialPropertyBlock propBlock;
        private Renderer[] cachedRenderers;
        private float lastCacheTime = -999f;
        private const float CacheRefreshInterval = 1f;

        private void Awake()
        {
            starship = GetComponent<Starship>();
            propBlock = new MaterialPropertyBlock();
        }

        private void OnEnable()
        {
            cachedRenderers = null;
            lastCacheTime = -999f;
        }

        private void Update()
        {
            if (!enableLegacyTint) return;
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
            // Cache fallback result to avoid GetComponentsInChildren every frame
            if (cachedRenderers != null && Time.time - lastCacheTime < CacheRefreshInterval)
                return cachedRenderers;
            lastCacheTime = Time.time;
            var list = new System.Collections.Generic.List<Renderer>();
            foreach (var r in GetComponentsInChildren<Renderer>())
            {
                if (r == null) continue;
                if (r.GetComponentInParent<EnemyShipWorldStatsPanel>() != null) continue;
                string n = r.gameObject.name;
                if (n == "Cockpit" || n.StartsWith("Engine") || n.StartsWith("Wing"))
                    list.Add(r);
            }
            if (list.Count > 0)
            {
                cachedRenderers = list.ToArray();
                return cachedRenderers;
            }

            var all = GetComponentsInChildren<Renderer>();
            var filtered = new System.Collections.Generic.List<Renderer>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null) continue;
                if (r.GetComponentInParent<EnemyShipWorldStatsPanel>() != null) continue;
                filtered.Add(r);
            }
            cachedRenderers = filtered.ToArray();
            return cachedRenderers;
        }

        private Color GetTeamColor(TeamManager.Team team)
        {
            if (TeamManager.Instance != null)
                return TeamManager.GetTeamColor(team);
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
