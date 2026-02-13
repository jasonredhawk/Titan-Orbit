using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using TitanOrbit.Entities;
using TitanOrbit.Core;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Minimap showing a larger region around the player (not full map).
    /// Displays: player ship, friendly ships, enemy ships (different colors), planets, home planets, asteroids.
    /// Each team has its own color.
    /// </summary>
    public class MinimapController : MonoBehaviour
    {
        [Header("Minimap Settings")]
        [SerializeField] private float minimapRadius = 80f;
        [SerializeField] private float displaySize = 150f;
        [SerializeField] private RectTransform minimapContent;
        
        private RectTransform minimapRect;

        [Header("Entity Prefabs")]
        [SerializeField] private GameObject playerBlipPrefab;
        [SerializeField] private GameObject shipBlipPrefab;
        [SerializeField] private GameObject planetBlipPrefab;
        [SerializeField] private GameObject homePlanetBlipPrefab;
        [SerializeField] private GameObject asteroidBlipPrefab;

        [Header("Team Colors")]
        [SerializeField] private Color teamAColor = new Color(1f, 0.3f, 0.3f);
        [SerializeField] private Color teamBColor = new Color(0.3f, 0.5f, 1f);
        [SerializeField] private Color teamCColor = new Color(0.3f, 1f, 0.4f);
        [SerializeField] private Color planetColor = new Color(0.6f, 0.6f, 0.6f);
        [SerializeField] private Color homePlanetColor = new Color(1f, 0.9f, 0.2f);
        [SerializeField] private Color asteroidColor = new Color(0.6f, 0.45f, 0.3f);

        private Starship playerShip;
        private Transform playerTransform;
        private Dictionary<Transform, RectTransform> blips = new Dictionary<Transform, RectTransform>();
        private Dictionary<Transform, Image> blipImages = new Dictionary<Transform, Image>();

        private void Start()
        {
            minimapRect = GetComponent<RectTransform>();
            if (minimapContent == null) minimapContent = minimapRect;
            if (minimapRect != null)
            {
                // Update display size to match actual minimap size
                displaySize = minimapRect.sizeDelta.x; // Square, so x = y
            }
        }

        private void Update()
        {
            // Update display size if minimap size changed
            if (minimapRect != null)
            {
                float newSize = minimapRect.sizeDelta.x;
                if (Mathf.Abs(newSize - displaySize) > 1f)
                {
                    displaySize = newSize;
                }
            }
            
            if (playerShip == null || !playerShip.IsOwner)
            {
                foreach (var ship in FindObjectsOfType<Starship>())
                {
                    if (ship.IsOwner) { playerShip = ship; playerTransform = ship.transform; break; }
                }
                if (playerShip == null) return;
            }

            UpdateBlips();
        }

        private void UpdateBlips()
        {
            Vector3 playerPos = playerTransform.position;
            var toRemove = new List<Transform>();

            foreach (var kv in blips)
            {
                if (kv.Key == null) { toRemove.Add(kv.Key); continue; }
                if (!kv.Key.gameObject.activeInHierarchy) { toRemove.Add(kv.Key); continue; }

                Vector3 worldPos = kv.Key.position;
                float dx = worldPos.x - playerPos.x;
                float dz = worldPos.z - playerPos.z;

                // Toroidal distance (clamp to nearest wrap)
                float mapW = 300f, mapH = 300f;
                if (dx > mapW / 2) dx -= mapW;
                if (dx < -mapW / 2) dx += mapW;
                if (dz > mapH / 2) dz -= mapH;
                if (dz < -mapH / 2) dz += mapH;

                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist > minimapRadius) { kv.Value.gameObject.SetActive(false); continue; }
                kv.Value.gameObject.SetActive(true);

                float normX = dx / minimapRadius;
                float normZ = dz / minimapRadius;
                kv.Value.anchoredPosition = new Vector2(normX * displaySize / 2f, normZ * displaySize / 2f);
            }

            foreach (var t in toRemove)
            {
                if (blips.TryGetValue(t, out var rt) && rt != null) Destroy(rt.gameObject);
                blips.Remove(t);
                blipImages.Remove(t);
            }

            // Add new entities
            EnsureBlip(playerTransform, () => CreateBlip(Color.white, 8f), true);

            foreach (var ship in FindObjectsOfType<Starship>())
            {
                if (ship == playerShip || ship.IsDead) continue;
                bool friendly = ship.ShipTeam == playerShip.ShipTeam && ship.ShipTeam != TeamManager.Team.None;
                Color c = friendly ? GetTeamColor(playerShip.ShipTeam) : GetEnemyColor(ship.ShipTeam);
                EnsureBlip(ship.transform, () => CreateBlip(c, 5f));
            }

            foreach (var p in FindObjectsOfType<Planet>())
            {
                if (p is HomePlanet) continue;
                EnsureBlip(p.transform, () => CreateBlip(planetColor, 4f));
            }

            foreach (var hp in FindObjectsOfType<HomePlanet>())
            {
                EnsureBlip(hp.transform, () => CreateBlip(homePlanetColor, 6f));
            }

            foreach (var a in FindObjectsOfType<Asteroid>())
            {
                if (a.IsDestroyed) continue;
                EnsureBlip(a.transform, () => CreateBlip(asteroidColor, 3f));
            }
        }

        private void EnsureBlip(Transform t, System.Func<RectTransform> create, bool isPlayer = false)
        {
            if (blips.ContainsKey(t)) return;
            var rt = create();
            if (rt != null)
            {
                blips[t] = rt;
                var img = rt.GetComponent<Image>();
                if (img != null) blipImages[t] = img;
            }
        }

        private RectTransform CreateBlip(Color color, float size)
        {
            if (minimapContent == null) return null;

            var go = new GameObject("Blip");
            go.transform.SetParent(minimapContent, false);

            var img = go.AddComponent<Image>();
            img.color = color;

            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(size, size);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);

            return rt;
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

        private Color GetEnemyColor(TeamManager.Team team)
        {
            Color c = GetTeamColor(team);
            return new Color(c.r * 0.7f, c.g * 0.7f, c.b * 0.7f);
        }
    }
}
