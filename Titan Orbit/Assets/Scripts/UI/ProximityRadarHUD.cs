using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
using TitanOrbit.Entities;
using TitanOrbit.Core;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Large circular HUD element around the starship showing planets and home planets as edge markers.
    /// Marker size indicates proximity (closer = bigger). Same style as minimap edge markers.
    /// Size matches the expanded minimap (~85% of screen).
    /// </summary>
    public class ProximityRadarHUD : MonoBehaviour
    {
        [Header("Size & Display")]
        [SerializeField] private float sizePercent = 0.85f; // Match expanded minimap - 85% of screen
        [SerializeField] private float maxDisplayDistance = 212f; // Same as full map radius - show all entities

        [Header("Marker Sizing (proximity-based)")]
        [SerializeField] private float markerMinSize = 24f;   // Farthest planets
        [SerializeField] private float markerMaxSize = 64f;  // Closest planets

        [Header("Colors")]
        [SerializeField] private Color planetColor = new Color(0.6f, 0.6f, 0.6f);
        [SerializeField] private Color homePlanetColor = new Color(1f, 0.9f, 0.2f);
        [SerializeField] private Color teamAColor = new Color(1f, 0.3f, 0.3f);
        [SerializeField] private Color teamBColor = new Color(0.3f, 0.5f, 1f);
        [SerializeField] private Color teamCColor = new Color(0.3f, 1f, 0.4f);

        private RectTransform radarRect;
        private RectTransform markerContainer;
        private Starship playerShip;
        private UnityEngine.Camera gameCamera;
        private Dictionary<Transform, RectTransform> markers = new Dictionary<Transform, RectTransform>();
        private Dictionary<Transform, Image> markerImages = new Dictionary<Transform, Image>();
        private Dictionary<Transform, bool> markerIsHomePlanet = new Dictionary<Transform, bool>();
        
        // Attack/defend markers
        private Dictionary<Transform, RectTransform> attackDefendMarkers = new Dictionary<Transform, RectTransform>();
        private Dictionary<Transform, Image> attackDefendMarkerImages = new Dictionary<Transform, Image>();

        private const float MapWidth = 300f;
        private const float MapHeight = 300f;
        private const int ArrowSpriteSize = 48;

        private void Start()
        {
            radarRect = GetComponent<RectTransform>();
            if (radarRect == null)
                radarRect = gameObject.AddComponent<RectTransform>();

            SetupRadar();
            SetupMarkerContainer();
        }

        private void SetupRadar()
        {
            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return;

            RectTransform canvasRect = canvas.GetComponent<RectTransform>();
            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();

            float screenWidth, screenHeight;
            if (scaler != null)
            {
                screenWidth = scaler.referenceResolution.x;
                screenHeight = scaler.referenceResolution.y;
            }
            else
            {
                screenWidth = canvasRect.sizeDelta.x > 0 ? canvasRect.sizeDelta.x : Screen.width;
                screenHeight = canvasRect.sizeDelta.y > 0 ? canvasRect.sizeDelta.y : Screen.height;
            }

            float minDimension = Mathf.Min(screenWidth, screenHeight);
            float size = minDimension * sizePercent;

            radarRect.anchorMin = new Vector2(0.5f, 0.5f);
            radarRect.anchorMax = new Vector2(0.5f, 0.5f);
            radarRect.pivot = new Vector2(0.5f, 0.5f);
            radarRect.anchoredPosition = Vector2.zero;
            radarRect.sizeDelta = new Vector2(size, size);
        }

        private void SetupMarkerContainer()
        {
            GameObject containerObj = new GameObject("ProximityMarkers");
            containerObj.transform.SetParent(transform, false);
            markerContainer = containerObj.AddComponent<RectTransform>();
            markerContainer.anchorMin = Vector2.zero;
            markerContainer.anchorMax = Vector2.one;
            markerContainer.offsetMin = Vector2.zero;
            markerContainer.offsetMax = Vector2.zero;
        }

        private void Update()
        {
            if (gameCamera == null)
                gameCamera = UnityEngine.Camera.main;

            if (playerShip == null || !playerShip.IsOwner)
            {
                foreach (var ship in FindObjectsOfType<Starship>())
                {
                    if (ship.IsOwner) { playerShip = ship; break; }
                }
                if (playerShip == null) return;
            }
            if (playerShip.IsDead) return;

            UpdateMarkers();
        }

        private void UpdateMarkers()
        {
            Vector3 playerPos = playerShip.transform.position;
            float radius = (radarRect != null ? radarRect.sizeDelta.x : 400f) / 2f;

            // Track which transforms we've seen this frame
            var seen = new HashSet<Transform>();

            // Planets
            foreach (var p in FindObjectsOfType<Planet>())
            {
                if (p is HomePlanet) continue;
                UpdateEntityMarker(p.transform, playerPos, radius, false, p.TeamOwnership);
                seen.Add(p.transform);
            }

            // Home planets
            foreach (var hp in FindObjectsOfType<HomePlanet>())
            {
                UpdateEntityMarker(hp.transform, playerPos, radius, true, hp.TeamOwnership);
                seen.Add(hp.transform);
            }
            
            // Attack/defend markers
            var allMarkers = FindObjectsOfType<MinimapMarker>();
            foreach (var marker in allMarkers)
            {
                if (!marker.IsSpawned) continue;
                UpdateAttackDefendMarker(marker.transform, playerPos, radius, marker.Type, marker.Team);
                seen.Add(marker.transform);
            }

            // Remove markers for destroyed entities
            var toRemove = new List<Transform>();
            foreach (var t in markers.Keys)
            {
                if (t == null || !t.gameObject.activeInHierarchy || !seen.Contains(t))
                    toRemove.Add(t);
            }
            foreach (var t in toRemove)
            {
                if (markers.TryGetValue(t, out var rt) && rt != null)
                    Destroy(rt.gameObject);
                markers.Remove(t);
                markerImages.Remove(t);
                markerIsHomePlanet.Remove(t);
            }
            
            // Remove attack/defend markers for destroyed markers
            var attackDefendToRemove = new List<Transform>();
            foreach (var t in attackDefendMarkers.Keys)
            {
                if (t == null || !t.gameObject.activeInHierarchy || !seen.Contains(t))
                    attackDefendToRemove.Add(t);
            }
            foreach (var t in attackDefendToRemove)
            {
                if (attackDefendMarkers.TryGetValue(t, out var rt) && rt != null)
                    Destroy(rt.gameObject);
                attackDefendMarkers.Remove(t);
                attackDefendMarkerImages.Remove(t);
            }
        }

        private void UpdateEntityMarker(Transform entityTransform, Vector3 playerPos, float radius, bool isHomePlanet, TeamManager.Team team)
        {
            Vector3 worldPos = entityTransform.position;

            // Hide marker when planet is in view (on screen)
            if (IsEntityInView(worldPos))
            {
                if (markers.TryGetValue(entityTransform, out var existingMarker) && existingMarker != null)
                    existingMarker.gameObject.SetActive(false);
                return;
            }
            float dx = worldPos.x - playerPos.x;
            float dz = worldPos.z - playerPos.z;

            // Toroidal distance
            if (dx > MapWidth / 2) dx -= MapWidth;
            if (dx < -MapWidth / 2) dx += MapWidth;
            if (dz > MapHeight / 2) dz -= MapHeight;
            if (dz < -MapHeight / 2) dz += MapHeight;

            float distance = Mathf.Sqrt(dx * dx + dz * dz);

            // Direction (angle) from player to entity
            float angle = Mathf.Atan2(dz, dx);

            // Position on edge of circle
            float edgeX = Mathf.Cos(angle) * radius;
            float edgeZ = Mathf.Sin(angle) * radius;

            // Marker size based on proximity: closer = bigger
            float normalizedDistance = Mathf.Clamp01(distance / maxDisplayDistance);
            float markerSize = Mathf.Lerp(markerMaxSize, markerMinSize, normalizedDistance);

            Color markerColor = isHomePlanet
                ? (team == TeamManager.Team.None ? homePlanetColor : GetTeamColor(team))
                : (team == TeamManager.Team.None ? planetColor : GetTeamColor(team));

            if (!markers.ContainsKey(entityTransform))
            {
                CreateMarker(entityTransform, edgeX, edgeZ, angle, markerColor, isHomePlanet, markerSize);
            }
            else
            {
                RectTransform markerRect = markers[entityTransform];
                if (markerRect != null)
                {
                    markerRect.gameObject.SetActive(true);
                    markerRect.anchoredPosition = new Vector2(edgeX, edgeZ);
                    markerRect.localEulerAngles = new Vector3(0, 0, angle * Mathf.Rad2Deg);
                    markerRect.sizeDelta = new Vector2(markerSize, markerSize);

                    if (markerImages.TryGetValue(entityTransform, out var img) && img != null)
                        img.color = markerColor;
                }
            }
        }

        private bool IsEntityInView(Vector3 worldPos)
        {
            if (gameCamera == null) return false;
            Vector3 viewportPos = gameCamera.WorldToViewportPoint(worldPos);
            // In front of camera and within viewport (with small margin to avoid edge flicker)
            return viewportPos.z > 0 && viewportPos.x >= -0.05f && viewportPos.x <= 1.05f && viewportPos.y >= -0.05f && viewportPos.y <= 1.05f;
        }

        private void CreateMarker(Transform entityTransform, float x, float z, float angle, Color color, bool isHomePlanet, float size)
        {
            GameObject markerObj = new GameObject(isHomePlanet ? "HomePlanetMarker" : "PlanetMarker");
            markerObj.transform.SetParent(markerContainer, false);

            Image img = markerObj.AddComponent<Image>();
            img.color = color;
            img.sprite = CreateArrowSprite(isHomePlanet);
            img.raycastTarget = false;

            RectTransform rt = markerObj.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(size, size);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, z);
            rt.localEulerAngles = new Vector3(0, 0, angle * Mathf.Rad2Deg);

            markers[entityTransform] = rt;
            markerImages[entityTransform] = img;
            markerIsHomePlanet[entityTransform] = isHomePlanet;
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

        private Sprite CreateArrowSprite(bool isHomePlanet)
        {
            int textureSize = ArrowSpriteSize;
            Texture2D texture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;

            Color[] pixels = new Color[textureSize * textureSize];
            float centerX = textureSize / 2f;
            float centerY = textureSize / 2f;

            for (int y = 0; y < textureSize; y++)
            {
                for (int x = 0; x < textureSize; x++)
                {
                    float dx = x - centerX;
                    float dy = y - centerY;

                    bool isInside = false;

                    if (isHomePlanet)
                    {
                        float circleRadius = textureSize * 0.25f;
                        float circleCenterX = -textureSize * 0.15f;
                        float distFromCircle = Mathf.Sqrt((x - (centerX + circleCenterX)) * (x - (centerX + circleCenterX)) + dy * dy);
                        if (distFromCircle < circleRadius)
                        {
                            isInside = true;
                        }
                        else if (dx > -textureSize * 0.05f)
                        {
                            float tipX = textureSize * 0.4f;
                            float tipY = centerY;
                            float baseLeft = -textureSize * 0.05f;
                            float baseWidth = textureSize * 0.3f;
                            Vector2 p1 = new Vector2(tipX, tipY);
                            Vector2 p2 = new Vector2(baseLeft, tipY - baseWidth / 2f);
                            Vector2 p3 = new Vector2(baseLeft, tipY + baseWidth / 2f);
                            Vector2 p = new Vector2(x, y);
                            isInside = PointInTriangle(p, p1, p2, p3);
                        }
                    }
                    else
                    {
                        float tipX = textureSize * 0.4f;
                        float tipY = centerY;
                        float baseLeft = -textureSize * 0.2f;
                        float baseWidth = textureSize * 0.35f;
                        Vector2 p1 = new Vector2(tipX, tipY);
                        Vector2 p2 = new Vector2(baseLeft, tipY - baseWidth / 2f);
                        Vector2 p3 = new Vector2(baseLeft, tipY + baseWidth / 2f);
                        Vector2 p = new Vector2(x, y);
                        isInside = PointInTriangle(p, p1, p2, p3);
                    }

                    pixels[y * textureSize + x] = isInside ? Color.white : Color.clear;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, textureSize, textureSize), new Vector2(0.5f, 0.5f), 100f);
        }

        private static bool PointInTriangle(Vector2 p, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            float d1 = Sign(p, p1, p2);
            float d2 = Sign(p, p2, p3);
            float d3 = Sign(p, p3, p1);
            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }

        private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
        {
            return (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
        }
        
        private void UpdateAttackDefendMarker(Transform markerTransform, Vector3 playerPos, float radius, MinimapMarker.MarkerType markerType, TeamManager.Team team)
        {
            Vector3 worldPos = markerTransform.position;
            
            // Hide marker when it's in view (on screen) - markers are always shown as edge markers
            // Actually, let's always show them as edge markers since they're temporary signals
            
            float dx = worldPos.x - playerPos.x;
            float dz = worldPos.z - playerPos.z;
            
            // Toroidal distance
            if (dx > MapWidth / 2) dx -= MapWidth;
            if (dx < -MapWidth / 2) dx += MapWidth;
            if (dz > MapHeight / 2) dz -= MapHeight;
            if (dz < -MapHeight / 2) dz += MapHeight;
            
            float distance = Mathf.Sqrt(dx * dx + dz * dz);
            
            // Direction (angle) from player to marker
            float angle = Mathf.Atan2(dz, dx);
            
            // Position on edge of circle
            float edgeX = Mathf.Cos(angle) * radius;
            float edgeZ = Mathf.Sin(angle) * radius;
            
            // Marker size based on proximity: closer = bigger
            float normalizedDistance = Mathf.Clamp01(distance / maxDisplayDistance);
            float markerSize = Mathf.Lerp(markerMaxSize, markerMinSize, normalizedDistance);
            
            // Get marker color based on type and team
            Color markerColor = markerType == MinimapMarker.MarkerType.Defend
                ? new Color(0.2f, 0.8f, 0.2f, 1f) // Green for defend
                : new Color(0.8f, 0.2f, 0.2f, 1f); // Red for attack
            
            // Blend with team color
            if (team != TeamManager.Team.None)
            {
                Color teamColor = GetTeamColor(team);
                markerColor = Color.Lerp(markerColor, teamColor, 0.3f);
            }
            
            if (!attackDefendMarkers.ContainsKey(markerTransform))
            {
                CreateAttackDefendMarker(markerTransform, edgeX, edgeZ, angle, markerColor, markerType, markerSize);
            }
            else
            {
                RectTransform markerRect = attackDefendMarkers[markerTransform];
                if (markerRect != null)
                {
                    markerRect.gameObject.SetActive(true);
                    markerRect.anchoredPosition = new Vector2(edgeX, edgeZ);
                    markerRect.localEulerAngles = new Vector3(0, 0, angle * Mathf.Rad2Deg);
                    markerRect.sizeDelta = new Vector2(markerSize, markerSize);
                    
                    if (attackDefendMarkerImages.TryGetValue(markerTransform, out var img) && img != null)
                        img.color = markerColor;
                }
            }
        }
        
        private void CreateAttackDefendMarker(Transform markerTransform, float x, float z, float angle, Color color, MinimapMarker.MarkerType markerType, float size)
        {
            GameObject markerObj = new GameObject(markerType == MinimapMarker.MarkerType.Defend ? "DefendMarker" : "AttackMarker");
            markerObj.transform.SetParent(markerContainer, false);
            
            Image img = markerObj.AddComponent<Image>();
            img.color = color;
            img.sprite = CreateBullseyeSprite(markerType == MinimapMarker.MarkerType.Defend);
            img.raycastTarget = false;
            
            RectTransform rt = markerObj.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(size, size);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, z);
            rt.localEulerAngles = new Vector3(0, 0, angle * Mathf.Rad2Deg);
            
            attackDefendMarkers[markerTransform] = rt;
            attackDefendMarkerImages[markerTransform] = img;
        }
        
        private Sprite CreateBullseyeSprite(bool isDefend)
        {
            int textureSize = ArrowSpriteSize;
            Texture2D texture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            
            Color[] pixels = new Color[textureSize * textureSize];
            float centerX = textureSize / 2f;
            float centerY = textureSize / 2f;
            
            // Use same bullseye/target shape for both attack and defend
            // Color will differentiate them (red for attack, green for defend)
            for (int y = 0; y < textureSize; y++)
            {
                for (int x = 0; x < textureSize; x++)
                {
                    float dx = x - centerX;
                    float dy = y - centerY;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    
                    bool isInside = false;
                    
                    // Bullseye/target shape - concentric circles
                    float outerRadius = textureSize * 0.45f;
                    float middleRadius = textureSize * 0.3f;
                    float innerRadius = textureSize * 0.15f;
                    
                    if (dist <= outerRadius && dist > middleRadius)
                        isInside = true; // Outer ring
                    else if (dist <= middleRadius && dist > innerRadius)
                        isInside = false; // Gap
                    else if (dist <= innerRadius)
                        isInside = true; // Inner circle
                    
                    pixels[y * textureSize + x] = isInside ? Color.white : Color.clear;
                }
            }
            
            texture.SetPixels(pixels);
            texture.Apply();
            
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, textureSize, textureSize), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = isDefend ? "DefendBullseye" : "AttackBullseye";
            return sprite;
        }
    }
}
