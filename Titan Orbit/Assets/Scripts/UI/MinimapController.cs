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
        [SerializeField] private float minimapRadius = 40f; // Reduced from 80f - zoomed in to show less map area
        [SerializeField] private float displaySize = 150f;
        [SerializeField] private RectTransform minimapContent;
        [SerializeField] private float sizeScaleFactor = 1.2f; // Increased from 0.5f - makes entities more visible when zoomed in
        [SerializeField] private float asteroidBlipScaleFactor = 1f; // Asteroids use physical scale (0.3-1.5); keeps blips small vs old AsteroidSize (1-20)
        
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
        [SerializeField] private Color asteroidColor = new Color(0.8f, 0.8f, 0.8f); // Light grey for better visibility

        private Starship playerShip;
        private Transform playerTransform;
        private Dictionary<Transform, RectTransform> blips = new Dictionary<Transform, RectTransform>();
        private Dictionary<Transform, Image> blipImages = new Dictionary<Transform, Image>();
        private Dictionary<Transform, BlipType> blipTypes = new Dictionary<Transform, BlipType>();

        private enum BlipType
        {
            Circle,      // Planets, Gems
            Capsule,     // Ships
            Irregular    // Asteroids
        }

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
                blipTypes.Remove(t);
            }

            // Add new entities
            EnsureBlip(playerTransform, () => CreateBlip(Color.white, 8f, BlipType.Capsule), true);

            foreach (var ship in FindObjectsOfType<Starship>())
            {
                if (ship == playerShip || ship.IsDead) continue;
                bool friendly = ship.ShipTeam == playerShip.ShipTeam && ship.ShipTeam != TeamManager.Team.None;
                Color c = friendly ? GetTeamColor(playerShip.ShipTeam) : GetEnemyColor(ship.ShipTeam);
                EnsureBlip(ship.transform, () => CreateBlip(c, 5f, BlipType.Capsule));
            }

            // Calculate consistent scale factor: world units to minimap pixels
            // The minimap shows minimapRadius * 2 world units across displaySize pixels
            // So 1 world unit = displaySize / (minimapRadius * 2) pixels
            float worldToMinimapScale = displaySize / (minimapRadius * 2f);
            
            foreach (var p in FindObjectsOfType<Planet>())
            {
                if (p is HomePlanet) continue;
                // Use team color if captured, otherwise grey
                Color planetBlipColor = p.TeamOwnership == TeamManager.Team.None 
                    ? planetColor 
                    : GetTeamColor(p.TeamOwnership);
                // Get actual planet size from transform scale (fallback to PlanetSize property)
                float actualPlanetSize = (p.transform.localScale.x + p.transform.localScale.y + p.transform.localScale.z) / 3f;
                if (actualPlanetSize < 0.1f) actualPlanetSize = p.PlanetSize;
                // Use same scale factor for all entities - directly proportional to world size
                float planetBlipSize = actualPlanetSize * worldToMinimapScale * sizeScaleFactor;
                if (blips.ContainsKey(p.transform))
                {
                    UpdateBlip(p.transform, planetBlipColor, planetBlipSize);
                }
                else
                {
                    EnsureBlip(p.transform, () => CreateBlip(planetBlipColor, planetBlipSize, BlipType.Circle));
                }
            }

            foreach (var hp in FindObjectsOfType<HomePlanet>())
            {
                // Use team color for home planets
                Color homeBlipColor = hp.TeamOwnership == TeamManager.Team.None 
                    ? homePlanetColor 
                    : GetTeamColor(hp.TeamOwnership);
                // Get actual home planet size from transform scale (fallback to PlanetSize property)
                float actualHomeSize = (hp.transform.localScale.x + hp.transform.localScale.y + hp.transform.localScale.z) / 3f;
                if (actualHomeSize < 0.1f) actualHomeSize = hp.PlanetSize;
                // Use same scale factor for all entities - directly proportional to world size
                float homeBlipSize = actualHomeSize * worldToMinimapScale * sizeScaleFactor;
                if (blips.ContainsKey(hp.transform))
                {
                    UpdateBlip(hp.transform, homeBlipColor, homeBlipSize);
                }
                else
                {
                    EnsureBlip(hp.transform, () => CreateBlip(homeBlipColor, homeBlipSize, BlipType.Circle));
                }
            }

            foreach (var a in FindObjectsOfType<Asteroid>())
            {
                if (a.IsDestroyed) continue;
                // Use physical scale (transform) for minimap size, not normalized AsteroidSize (1-20)
                // Raw asteroid scale is ~0.3 to 1.5 world units
                float physicalSize = (a.transform.localScale.x + a.transform.localScale.y + a.transform.localScale.z) / 3f;
                // Keep asteroid blips small: physical size 0.3–1.5 → blip ~2–5 px (no sizeScaleFactor so they don’t match planet scale)
                float asteroidBlipSize = Mathf.Clamp(physicalSize * worldToMinimapScale * asteroidBlipScaleFactor, 2f, 6f);
                if (blips.ContainsKey(a.transform))
                {
                    UpdateBlip(a.transform, asteroidColor, asteroidBlipSize);
                }
                else
                {
                    EnsureBlip(a.transform, () => CreateBlip(asteroidColor, asteroidBlipSize, BlipType.Irregular));
                }
            }
        }

        private void EnsureBlip(Transform t, System.Func<RectTransform> create, bool isPlayer = false)
        {
            if (blips.ContainsKey(t))
            {
                // Update existing blip color and size if needed
                if (blipImages.TryGetValue(t, out var img) && img != null)
                {
                    var blipRect = blips[t];
                    if (blipRect != null)
                    {
                        // Get the new blip info by creating a temporary one
                        var tempRt = create();
                        if (tempRt != null)
                        {
                            var tempImg = tempRt.GetComponent<Image>();
                            if (tempImg != null)
                            {
                                img.color = tempImg.color;
                                blipRect.sizeDelta = tempRt.sizeDelta;
                                // Update sprite if type changed
                                if (tempImg.sprite != null)
                                {
                                    img.sprite = tempImg.sprite;
                                }
                            }
                            Destroy(tempRt.gameObject);
                        }
                    }
                }
                return;
            }
            var newBlipRect = create();
            if (newBlipRect != null)
            {
                blips[t] = newBlipRect;
                var img = newBlipRect.GetComponent<Image>();
                if (img != null)
                {
                    blipImages[t] = img;
                    // Store the blip type for reference
                    if (img.sprite != null && img.sprite.name.Contains("Circle")) blipTypes[t] = BlipType.Circle;
                    else if (img.sprite != null && img.sprite.name.Contains("Capsule")) blipTypes[t] = BlipType.Capsule;
                    else if (img.sprite != null && img.sprite.name.Contains("Irregular")) blipTypes[t] = BlipType.Irregular;
                }
            }
        }
        
        private void UpdateBlip(Transform t, Color color, float size)
        {
            if (blips.TryGetValue(t, out var rt) && rt != null)
            {
                rt.sizeDelta = new Vector2(size, size);
                if (blipImages.TryGetValue(t, out var img) && img != null)
                {
                    img.color = color;
                }
            }
        }

        private RectTransform CreateBlip(Color color, float size, BlipType blipType)
        {
            if (minimapContent == null) return null;

            var go = new GameObject("Blip");
            go.transform.SetParent(minimapContent, false);

            var img = go.AddComponent<Image>();
            img.color = color;
            
            // Create sprite based on blip type
            Sprite sprite = CreateBlipSprite((int)size, blipType);
            img.sprite = sprite;

            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(size, size);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);

            return rt;
        }

        private Sprite CreateBlipSprite(int size, BlipType blipType)
        {
            int textureSize = Mathf.Max(size, 32); // Minimum size for quality
            Texture2D texture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            
            Color[] pixels = new Color[textureSize * textureSize];
            float centerX = textureSize / 2f;
            float centerY = textureSize / 2f;
            
            switch (blipType)
            {
                case BlipType.Circle:
                {
                    // Create a circle sprite
                    float radius = textureSize / 2f - 1f;
                    for (int y = 0; y < textureSize; y++)
                    {
                        for (int x = 0; x < textureSize; x++)
                        {
                            float dx = x - centerX;
                            float dy = y - centerY;
                            float dist = Mathf.Sqrt(dx * dx + dy * dy);
                            if (dist <= radius)
                            {
                                pixels[y * textureSize + x] = Color.white;
                            }
                            else
                            {
                                pixels[y * textureSize + x] = Color.clear;
                            }
                        }
                    }
                    break;
                }
                    
                case BlipType.Capsule:
                {
                    // Create a capsule/ellipse sprite (wider than tall)
                    float radiusX = textureSize / 2f - 1f;
                    float radiusY = radiusX * 0.5f; // Make it half as tall as wide
                    for (int y = 0; y < textureSize; y++)
                    {
                        for (int x = 0; x < textureSize; x++)
                        {
                            float dx = (x - centerX) / radiusX;
                            float dy = (y - centerY) / radiusY;
                            float dist = Mathf.Sqrt(dx * dx + dy * dy);
                            if (dist <= 1f)
                            {
                                pixels[y * textureSize + x] = Color.white;
                            }
                            else
                            {
                                pixels[y * textureSize + x] = Color.clear;
                            }
                        }
                    }
                    break;
                }
                    
                case BlipType.Irregular:
                {
                    // Diamond/rhombus shape for asteroids - clear and compact on minimap
                    float halfW = textureSize / 2f - 0.5f;
                    float halfH = textureSize / 2f - 0.5f;
                    for (int y = 0; y < textureSize; y++)
                    {
                        for (int x = 0; x < textureSize; x++)
                        {
                            float nx = (x - centerX) / halfW;
                            float ny = (y - centerY) / halfH;
                            // Diamond: |nx| + |ny| <= 1
                            if (Mathf.Abs(nx) + Mathf.Abs(ny) <= 1f)
                            {
                                pixels[y * textureSize + x] = Color.white;
                            }
                            else
                            {
                                pixels[y * textureSize + x] = Color.clear;
                            }
                        }
                    }
                    break;
                }
            }
            
            texture.SetPixels(pixels);
            texture.Apply();
            
            string spriteName = $"MinimapBlip_{blipType}_{size}";
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, textureSize, textureSize), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = spriteName;
            
            return sprite;
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
