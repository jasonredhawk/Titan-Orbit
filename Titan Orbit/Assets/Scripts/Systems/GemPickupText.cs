using UnityEngine;
using TitanOrbit.Core;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Simple floating text for gem pickups: shows "+N" in team color, moves up and fades in/out over a short duration.
    /// </summary>
    public class GemPickupText : MonoBehaviour
    {
        [SerializeField] private float riseSpeed = 1.5f;

        private TextMesh textMesh;
        private float lifetime;
        private float elapsed;
        private Color baseColor;

        public void Initialize(int amount, TeamManager.Team team, float duration)
        {
            lifetime = Mathf.Max(0.1f, duration);
            textMesh = GetComponentInChildren<TextMesh>();
            if (textMesh == null)
                textMesh = gameObject.AddComponent<TextMesh>();

            textMesh.text = $"+{amount}";
            baseColor = GetTeamColor(team);
            baseColor.a = 0f;
            textMesh.color = baseColor;
            textMesh.anchor = TextAnchor.MiddleCenter;
            textMesh.alignment = TextAlignment.Center;
        }

        private void Update()
        {
            if (lifetime <= 0f)
            {
                Destroy(gameObject);
                return;
            }

            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / lifetime);

            // Move up over time
            transform.position += Vector3.up * riseSpeed * Time.deltaTime;

            if (textMesh != null)
            {
                // Fade in first half, fade out second half
                float alpha = t <= 0.5f ? (t * 2f) : (1f - (t - 0.5f) * 2f);
                Color c = baseColor;
                c.a = Mathf.Clamp01(alpha);
                textMesh.color = c;
            }

            if (elapsed >= lifetime)
                Destroy(gameObject);
        }

        private static Color GetTeamColor(TeamManager.Team team)
        {
            switch (team)
            {
                case TeamManager.Team.TeamA: return new Color(1f, 0.3f, 0.3f);
                case TeamManager.Team.TeamB: return new Color(0.3f, 0.5f, 1f);
                case TeamManager.Team.TeamC: return new Color(0.3f, 1f, 0.4f);
                default: return new Color(0.9f, 0.95f, 1f);
            }
        }
    }
}

