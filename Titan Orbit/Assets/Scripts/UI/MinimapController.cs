using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using TMPro;
using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Game;
using Shapes;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Minimap showing a larger region around the player (not full map).
    /// Displays: player ship (cross blip), friendly/enemy ships (cross, team colors), planets, home planets, gem moons, asteroids.
    /// Each team has its own color.
    /// </summary>
    public class MinimapController : MonoBehaviour
    {
        [Header("Minimap Settings")]
        [SerializeField] private float minimapRadius = 40f;
        [SerializeField] private float collapsedZoomOutMultiplier = 1.5f;
        [SerializeField] private float displaySize = 150f;
        [SerializeField] private RectTransform minimapContent;
        [SerializeField] private float sizeScaleFactor = 1.2f; // Increased from 0.5f - makes entities more visible when zoomed in
        [SerializeField] private float playerBlipSize = 10f; // Cross blip for local player (other ships use 12f)
        [SerializeField] private float asteroidBlipScaleFactor = 1f; // Asteroids use physical scale for blip size
        [SerializeField] private float moonBlipScaleFactor = 0.85f;
        [SerializeField] private float moonBlipMinSize = 5f;
        [SerializeField] private float edgeMarkerSize = 36f; // Base size of edge markers for planets outside visible area
        [SerializeField] private float edgeMarkerMinSize = 20f; // Minimum size for farthest planets
        [SerializeField] private float edgeMarkerMaxSize = 48f; // Maximum size for closest planets
        [SerializeField] private float maxPlanetDistance = 150f; // Maximum distance to consider for scaling (beyond minimap radius)
        
        [Header("Expand Settings")]
        [SerializeField] private float expandedSizePercent = 0.85f; // Percentage of screen to fill (85%)
        [SerializeField] private float expandedBackgroundAlpha = 0.9f;
        [Tooltip("World-space radius when expanded. Leave at 0 to auto-fit the full toroidal map.")]
        [SerializeField] private float fullMapRadius = 0f;
        [SerializeField] private float expandedMapFitPadding = 1.03f;
        [SerializeField] private float markerHeight = 1f; // Height above ground for markers

        [Header("Map size label")]
        [Tooltip("Optional; if null, a label is created on Start. Shows ToroidalMap width x height.")]
        [SerializeField] private TextMeshProUGUI mapSizeLabel;
        private float _lastMapSizeLabelW = float.NaN;
        private float _lastMapSizeLabelH = float.NaN;
        
        private RectTransform minimapRect;
        private RectTransform edgeMarkerContainer; // Container for edge markers (outside mask)
        private Image borderImage; // Reference to the border image
        private CanvasGroup canvasGroup; // Used to hide minimap until team chosen (keeps Update running so we can show again)
        private Button expandButton;
        private TextMeshProUGUI expandButtonLabel;
        private static Sprite _whiteUiSprite;
        private bool isExpanded = false;
        private Vector2 originalAnchoredPosition;
        private Vector2 originalSizeDelta;
        private Vector2 originalAnchorMin;
        private Vector2 originalAnchorMax;
        /// <summary>World radius shown when minimap is collapsed; restored after fullscreen so zoom matches pre-expand.</summary>
        private float originalMinimapRadius;

        /// <summary>When expanded, other UI roots are faded via CanvasGroup; we restore previous values on collapse.</summary>
        private struct NonMinimapUiRestoreState
        {
            public CanvasGroup Group;
            public bool AddedByMinimap;
            public float Alpha;
            public bool Interactable;
            public bool BlocksRaycasts;
        }

        private readonly List<NonMinimapUiRestoreState> _nonMinimapUiRestore = new List<NonMinimapUiRestoreState>(24);
        
        // Marker system
        private MarkerPlacementMenu markerMenu;

        [Header("Entity Prefabs")]
        [SerializeField] private GameObject playerBlipPrefab;
        [SerializeField] private GameObject shipBlipPrefab;
        [SerializeField] private GameObject planetBlipPrefab;
        [SerializeField] private GameObject homePlanetBlipPrefab;
        [SerializeField] private GameObject asteroidBlipPrefab;

        [Header("Team Colors")]
        [SerializeField] private Color teamAColor = new Color(1f, 0.3f, 0.3f);
        [SerializeField] private Color teamBColor = new Color(0.3f, 0.5f, 1f);
        [SerializeField] private Color teamCColor = new Color(0.2f, 0.7f, 0.28f);
        [SerializeField] private Color teamDColor = new Color(0.95f, 0.55f, 0.12f);
        [SerializeField] private Color teamEColor = new Color(0.65f, 0.25f, 0.85f);
        [SerializeField] private Color planetColor = new Color(0.6f, 0.6f, 0.6f);
        [SerializeField] private Color homePlanetColor = new Color(1f, 0.9f, 0.2f);
        [SerializeField] private Color asteroidColor = new Color(0.8f, 0.8f, 0.8f); // Light grey for better visibility
        [SerializeField] private Color moonColor = new Color(0.55f, 0.88f, 1f); // Neutral gem-moon tint when planet is uncaptured

        private MinimapBlipAnchor playerAnchor;
        private Transform playerTransform;
        private Dictionary<Transform, RectTransform> blips = new Dictionary<Transform, RectTransform>();
        private Dictionary<Transform, Image> blipImages = new Dictionary<Transform, Image>();
        private Dictionary<Transform, BlipType> blipTypes = new Dictionary<Transform, BlipType>();
        private Dictionary<Transform, float> bullseyePulseTime = new Dictionary<Transform, float>(); // Track pulse animation time for bullseye blips
        
        // Edge markers for planets outside visible area
        private Dictionary<Transform, RectTransform> edgeMarkers = new Dictionary<Transform, RectTransform>();
        private Dictionary<Transform, Image> edgeMarkerImages = new Dictionary<Transform, Image>();
        private Dictionary<Transform, bool> edgeMarkerIsHomePlanet = new Dictionary<Transform, bool>();
        
        // Edge markers for attack/defend markers outside visible area
        private Dictionary<Transform, RectTransform> markerEdgeMarkers = new Dictionary<Transform, RectTransform>();
        private Dictionary<Transform, Image> markerEdgeMarkerImages = new Dictionary<Transform, Image>();
        private float lastEntityCacheRefreshTime = -999f;
        // Refresh minimap entity cache less frequently to avoid repeated FindObjectsByType spikes (seen growing to 8–15 ms in logs).
        private const float EntityCacheRefreshInterval = 6f;
        /// <summary>While dead asteroid ghosts exist, only rescan asteroids on this interval (full RefreshEntityCache(true) every blip tick was very expensive).</summary>
        private float nextGhostAsteroidRescanTime = -999f;
        private const float GhostAsteroidRescanInterval = 0.25f;
        private MinimapBlipAnchor[] cachedShips = System.Array.Empty<MinimapBlipAnchor>();
        private MinimapBlipAnchor[] cachedPlanets = System.Array.Empty<MinimapBlipAnchor>();
        private MinimapBlipAnchor[] cachedHomePlanets = System.Array.Empty<MinimapBlipAnchor>();
        private MinimapBlipAnchor[] cachedAsteroids = System.Array.Empty<MinimapBlipAnchor>();
        private MinimapBlipAnchor[] cachedGemMoons = System.Array.Empty<MinimapBlipAnchor>();
        private int skippedNullShips = 0;
        private int skippedNullPlanets = 0;
        private int skippedNullHomePlanets = 0;
        private int skippedNullAsteroids = 0;
        private int skippedNullMarkers = 0;
        private const int MaxAsteroidBlips = 80;

        private readonly List<Transform> blipsToRemove = new List<Transform>();
        private readonly List<Transform> edgeMarkersToRemoveList = new List<Transform>();
        private readonly List<Transform> markerEdgeMarkersToRemoveList = new List<Transform>();

        /// <summary>Destroyed asteroids despawn (transform gone); we keep a faded blip at last known position until a new asteroid spawns there (then full-color blip again).</summary>
        private const float DeadAsteroidBlipAlpha = 0.2f;
        /// <summary>Toroidal distance under which a dim ghost is cleared because a live asteroid respawned at that site.</summary>
        private const float DeadAsteroidGhostClearMatchRadius = 2f;
        private readonly HashSet<int> lastFrameAsteroidInstanceIds = new HashSet<int>();
        private readonly HashSet<int> currentAsteroidInstanceIds = new HashSet<int>();
        private readonly Dictionary<int, Vector3> asteroidLastWorldPosByInstanceId = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, float> asteroidBlipPixelSizeByInstanceId = new Dictionary<int, float>();

        /// <summary>Avoids per-frame planet blip relayout from floating-point jitter in computed pixel size.</summary>
        private struct PlanetBlipLayoutState
        {
            public float QuantizedSize;
            public int Population;
            public int Level;
            public Color32 Color;
        }

        private readonly Dictionary<Transform, PlanetBlipLayoutState> planetBlipLayoutState = new Dictionary<Transform, PlanetBlipLayoutState>();

        private struct DeadAsteroidGhost
        {
            public Vector3 worldPos;
            public RectTransform rt;
        }

        private readonly List<DeadAsteroidGhost> deadAsteroidGhosts = new List<DeadAsteroidGhost>();

        private enum BlipType
        {
            Circle,      // Planets, Gems
            Capsule,     // (legacy sprite shape)
            Triangle,    // (legacy directional blip)
            Cross,       // Ships (player + others)
            Irregular,   // Asteroids
            Bullseye     // Markers (attack/defend)
        }

        private static void GetToroidalDelta(Vector3 from, Vector3 to, out float dx, out float dz)
        {
            float mapW = Mathf.Max(1f, ToroidalMap.GetMapWidth());
            float mapH = Mathf.Max(1f, ToroidalMap.GetMapHeight());
            dx = to.x - from.x;
            dz = to.z - from.z;
            dx -= mapW * Mathf.Round(dx / mapW);
            dz -= mapH * Mathf.Round(dz / mapH);
        }

        /// <summary>Half-diagonal of the toroidal map — circle radius that fits every world point around the player.</summary>
        private float GetFullMapToroidalRadius()
        {
            float w = ToroidalMap.GetMapWidth();
            float h = ToroidalMap.GetMapHeight();
            if (w > 1f && h > 1f)
            {
                float hw = w * 0.5f;
                float hh = h * 0.5f;
                return Mathf.Sqrt(hw * hw + hh * hh);
            }

            return fullMapRadius > 0f ? fullMapRadius : 212f;
        }

        private float GetMaxCachedEntityToroidalDistance(Vector3 playerPos)
        {
            float maxDist = 0f;
            AccumulateMaxToroidalDistance(playerPos, cachedShips, ref maxDist);
            AccumulateMaxToroidalDistance(playerPos, cachedPlanets, ref maxDist);
            AccumulateMaxToroidalDistance(playerPos, cachedHomePlanets, ref maxDist);
            AccumulateMaxToroidalDistance(playerPos, cachedAsteroids, ref maxDist);
            AccumulateMaxToroidalDistance(playerPos, cachedGemMoons, ref maxDist);
            return maxDist;
        }

        static void AccumulateMaxToroidalDistance(Vector3 playerPos, MinimapBlipAnchor[] entities, ref float maxDist)
        {
            if (entities == null)
                return;

            for (int i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (entity == null)
                    continue;

                GetToroidalDelta(playerPos, entity.transform.position, out float dx, out float dz);
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist > maxDist)
                    maxDist = dist;
            }
        }

        /// <summary>World-space radius used for expanded minimap zoom (player-centered, fits entire map).</summary>
        private float GetExpandedWorldRadius(Vector3 playerPos)
        {
            float radius = GetFullMapToroidalRadius();
            float entityRadius = GetMaxCachedEntityToroidalDistance(playerPos);
            if (entityRadius > radius)
                radius = entityRadius;

            return Mathf.Max(1f, radius * expandedMapFitPadding);
        }

        private void OnValidate()
        {
            if (!Application.isPlaying || !isExpanded)
                return;
            minimapRadius = GetExpandedWorldRadius(PlayerPosition);
        }

        // Exposed read‑only helpers so other systems (like Shapes panels) can match minimap math.
        public float MinimapRadius => minimapRadius;
        public float DisplaySize => displaySize;
        public Vector3 PlayerPosition => playerTransform != null ? playerTransform.position : Vector3.zero;

        public void GetToroidalDeltaForMinimap(Vector3 from, Vector3 to, out float dx, out float dz)
        {
            GetToroidalDelta(from, to, out dx, out dz);
        }

        private void RefreshEntityCache(bool force = false)
        {
            if (!Application.isPlaying) return;
            if (!force && Time.time - lastEntityCacheRefreshTime < EntityCacheRefreshInterval) return;

            var sync = MinimapEcsEntitySync.Instance;
            if (sync == null)
                return;

            cachedShips = ToArray(sync.Ships);
            cachedPlanets = ToArray(sync.Planets);
            cachedHomePlanets = ToArray(sync.HomePlanets);
            cachedAsteroids = ToArray(sync.Asteroids);
            cachedGemMoons = ToArray(sync.GemMoons);
            lastEntityCacheRefreshTime = Time.time;
        }

        static MinimapBlipAnchor[] ToArray(IReadOnlyList<MinimapBlipAnchor> list)
        {
            if (list == null || list.Count == 0)
                return System.Array.Empty<MinimapBlipAnchor>();
            var arr = new MinimapBlipAnchor[list.Count];
            for (int i = 0; i < list.Count; i++)
                arr[i] = list[i];
            return arr;
        }

        private void Start()
        {
            if (MinimapEcsEntitySync.Instance == null)
                gameObject.AddComponent<MinimapEcsEntitySync>();

            minimapRect = GetComponent<RectTransform>();
            if (minimapRect != null)
            {
                // Update display size to match actual minimap size
                displaySize = minimapRect.sizeDelta.x; // Square, so x = y
            }

            if (collapsedZoomOutMultiplier > 0f && !Mathf.Approximately(collapsedZoomOutMultiplier, 1f))
                minimapRadius *= collapsedZoomOutMultiplier;
            
            // Setup circular background
            SetupCircularBackground();
            
            // Setup circular border
            SetupCircularBorder();
            
            // Create content container if it doesn't exist
            if (minimapContent == null)
            {
                GameObject contentObj = new GameObject("MinimapContent");
                contentObj.transform.SetParent(minimapRect, false);
                minimapContent = contentObj.AddComponent<RectTransform>();
                minimapContent.anchorMin = Vector2.zero;
                minimapContent.anchorMax = Vector2.one;
                minimapContent.offsetMin = Vector2.zero;
                minimapContent.offsetMax = Vector2.zero;
            }
            
            // Setup mask for minimap content
            SetupMask();
            
            // Setup edge marker container (outside mask)
            SetupEdgeMarkerContainer();
            
            // Setup expand button
            SetupExpandButton();

            SetupMapSizeLabel();
            
            // Setup marker placement menu
            SetupMarkerMenu();
            
            // Store original minimap position and size for collapse
            StoreOriginalMinimapState();

            // CanvasGroup: hide minimap visually until player has a team (don't SetActive(false) or Update would stop and we'd never show again)
            canvasGroup = GetComponent<CanvasGroup>();
            if (canvasGroup == null)
                canvasGroup = gameObject.AddComponent<CanvasGroup>();

            // Ensure the parent canvas has an ImmediateModeCanvas (TitanOrbitShapesCanvas) so Shapes panels render.
            var canvas = GetComponentInParent<Canvas>();
            if (canvas != null && canvas.GetComponent<ImmediateModeCanvas>() == null)
            {
                canvas.gameObject.AddComponent<TitanOrbit.UI.TitanOrbitShapesCanvas>();
            }

            // Ensure a panel exists for drawing planet connection lines/triangles on the minimap.
            // Parent under minimapContent so the circular Mask clips triangles/lines to the circle.
            var connectionsUI = GetComponentInChildren<MinimapConnectionsUI>(true);
            if (connectionsUI == null)
            {
                GameObject panelObj = new GameObject("MinimapConnectionsUI");
                panelObj.transform.SetParent(minimapContent != null ? minimapContent : minimapRect, false);
                panelObj.transform.SetAsLastSibling(); // Draw on top of content/blips so lines and triangles are visible
                var rt = panelObj.AddComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                connectionsUI = panelObj.AddComponent<MinimapConnectionsUI>();
            }
            else
            {
                if (minimapContent != null && connectionsUI.transform.parent != minimapContent)
                    connectionsUI.transform.SetParent(minimapContent, false);
                connectionsUI.transform.SetAsLastSibling(); // Ensure it draws on top (in case it was created with old order)
            }
        }
        
        private void SetupMarkerMenu()
        {
            // Create marker menu UI
            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return;
            
            GameObject menuObj = new GameObject("MarkerPlacementMenu");
            menuObj.transform.SetParent(canvas.transform, false);
            
            RectTransform menuRect = menuObj.AddComponent<RectTransform>();
            menuRect.sizeDelta = new Vector2(120, 80);
            menuRect.anchorMin = new Vector2(0.5f, 0.5f);
            menuRect.anchorMax = new Vector2(0.5f, 0.5f);
            menuRect.pivot = new Vector2(0.5f, 0.5f);
            
            // Background
            Image bgImage = menuObj.AddComponent<Image>();
            bgImage.color = new Color(0.15f, 0.15f, 0.2f, 0.95f);
            Sprite bgSprite = CreateRoundedRectSprite(120, 80);
            bgImage.sprite = bgSprite;
            bgImage.type = Image.Type.Simple; // Changed from Sliced to Simple for better rendering
            
            // Add border
            GameObject borderObj = new GameObject("Border");
            borderObj.transform.SetParent(menuObj.transform, false);
            RectTransform borderRect = borderObj.AddComponent<RectTransform>();
            borderRect.anchorMin = Vector2.zero;
            borderRect.anchorMax = Vector2.one;
            borderRect.offsetMin = Vector2.zero;
            borderRect.offsetMax = Vector2.zero;
            Image borderImage = borderObj.AddComponent<Image>();
            borderImage.color = new Color(0.4f, 0.4f, 0.5f, 1f);
            Sprite borderSprite = CreateRoundedBorderSprite(120, 80);
            borderImage.sprite = borderSprite;
            borderImage.type = Image.Type.Sliced;
            borderRect.SetAsFirstSibling();
            
            // Attack button
            GameObject attackBtnObj = CreateMarkerButton("AttackButton", "ATTACK", new Color(0.8f, 0.2f, 0.2f, 1f));
            attackBtnObj.transform.SetParent(menuObj.transform, false);
            RectTransform attackRect = attackBtnObj.GetComponent<RectTransform>();
            attackRect.anchorMin = new Vector2(0.5f, 0.5f);
            attackRect.anchorMax = new Vector2(0.5f, 0.5f);
            attackRect.pivot = new Vector2(0.5f, 0.5f);
            attackRect.anchoredPosition = new Vector2(0, 15);
            attackRect.sizeDelta = new Vector2(100, 30);
            
            // Defend button
            GameObject defendBtnObj = CreateMarkerButton("DefendButton", "DEFEND", new Color(0.2f, 0.8f, 0.2f, 1f));
            defendBtnObj.transform.SetParent(menuObj.transform, false);
            RectTransform defendRect = defendBtnObj.GetComponent<RectTransform>();
            defendRect.anchorMin = new Vector2(0.5f, 0.5f);
            defendRect.anchorMax = new Vector2(0.5f, 0.5f);
            defendRect.pivot = new Vector2(0.5f, 0.5f);
            defendRect.anchoredPosition = new Vector2(0, -15);
            defendRect.sizeDelta = new Vector2(100, 30);
            
            // Add MarkerPlacementMenu component
            markerMenu = menuObj.AddComponent<MarkerPlacementMenu>();
            
            // Set references directly
            markerMenu.attackButton = attackBtnObj.GetComponent<Button>();
            markerMenu.defendButton = defendBtnObj.GetComponent<Button>();
            markerMenu.menuRect = menuRect;
            markerMenu.backgroundImage = bgImage;
        }
        
        private GameObject CreateMarkerButton(string name, string label, Color color)
        {
            GameObject btnObj = new GameObject(name);
            
            Image btnImage = btnObj.AddComponent<Image>();
            btnImage.color = color;
            Sprite btnSprite = CreateRoundedRectSprite(100, 30);
            btnImage.sprite = btnSprite;
            btnImage.type = Image.Type.Simple; // Changed from Sliced to Simple for better rendering
            
            Button button = btnObj.AddComponent<Button>();
            var colors = button.colors;
            colors.normalColor = color;
            colors.highlightedColor = new Color(Mathf.Min(color.r * 1.2f, 1f), Mathf.Min(color.g * 1.2f, 1f), Mathf.Min(color.b * 1.2f, 1f), 1f);
            colors.pressedColor = new Color(color.r * 0.8f, color.g * 0.8f, color.b * 0.8f, 1f);
            button.colors = colors;
            
            // Label text
            GameObject textObj = new GameObject("Label");
            textObj.transform.SetParent(btnObj.transform, false);
            TextMeshProUGUI text = textObj.AddComponent<TextMeshProUGUI>();
            text.text = label;
            text.fontSize = 14;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            text.raycastTarget = false; // Don't block clicks on text
            RectTransform textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            
            return btnObj;
        }
        
        private Sprite CreateRoundedRectSprite(int width, int height)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            
            Color[] pixels = new Color[width * height];
            float cornerRadius = Mathf.Min(width, height) * 0.2f;
            
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool isInside = true;
                    
                    // Check corners
                    float distFromCorner = 0f;
                    
                    // Top-left corner
                    if (x < cornerRadius && y > height - cornerRadius)
                    {
                        distFromCorner = Mathf.Sqrt((x - cornerRadius) * (x - cornerRadius) + 
                                                   (y - (height - cornerRadius)) * (y - (height - cornerRadius)));
                        if (distFromCorner > cornerRadius) isInside = false;
                    }
                    // Top-right corner
                    else if (x > width - cornerRadius && y > height - cornerRadius)
                    {
                        distFromCorner = Mathf.Sqrt((x - (width - cornerRadius)) * (x - (width - cornerRadius)) + 
                                                   (y - (height - cornerRadius)) * (y - (height - cornerRadius)));
                        if (distFromCorner > cornerRadius) isInside = false;
                    }
                    // Bottom-left corner
                    else if (x < cornerRadius && y < cornerRadius)
                    {
                        distFromCorner = Mathf.Sqrt((x - cornerRadius) * (x - cornerRadius) + 
                                                   (y - cornerRadius) * (y - cornerRadius));
                        if (distFromCorner > cornerRadius) isInside = false;
                    }
                    // Bottom-right corner
                    else if (x > width - cornerRadius && y < cornerRadius)
                    {
                        distFromCorner = Mathf.Sqrt((x - (width - cornerRadius)) * (x - (width - cornerRadius)) + 
                                                   (y - cornerRadius) * (y - cornerRadius));
                        if (distFromCorner > cornerRadius) isInside = false;
                    }
                    
                    pixels[y * width + x] = isInside ? Color.white : Color.clear;
                }
            }
            
            texture.SetPixels(pixels);
            texture.Apply();
            
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = "RoundedRect";
            return sprite;
        }
        
        private Sprite CreateRoundedBorderSprite(int width, int height)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            
            Color[] pixels = new Color[width * height];
            float cornerRadius = Mathf.Min(width, height) * 0.2f;
            float borderWidth = 2f;
            
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool isInside = false;
                    
                    // Create border by checking distance from edges
                    float minDist = Mathf.Min(x, width - x, y, height - y);
                    
                    // Handle rounded corners
                    float distFromCorner = float.MaxValue;
                    
                    // Top-left
                    if (x < cornerRadius && y > height - cornerRadius)
                        distFromCorner = Mathf.Sqrt((x - cornerRadius) * (x - cornerRadius) + (y - (height - cornerRadius)) * (y - (height - cornerRadius)));
                    // Top-right
                    else if (x > width - cornerRadius && y > height - cornerRadius)
                        distFromCorner = Mathf.Sqrt((x - (width - cornerRadius)) * (x - (width - cornerRadius)) + (y - (height - cornerRadius)) * (y - (height - cornerRadius)));
                    // Bottom-left
                    else if (x < cornerRadius && y < cornerRadius)
                        distFromCorner = Mathf.Sqrt((x - cornerRadius) * (x - cornerRadius) + (y - cornerRadius) * (y - cornerRadius));
                    // Bottom-right
                    else if (x > width - cornerRadius && y < cornerRadius)
                        distFromCorner = Mathf.Sqrt((x - (width - cornerRadius)) * (x - (width - cornerRadius)) + (y - cornerRadius) * (y - cornerRadius));
                    
                    if (distFromCorner < float.MaxValue)
                    {
                        minDist = Mathf.Min(minDist, distFromCorner);
                    }
                    
                    isInside = minDist < borderWidth;
                    
                    pixels[y * width + x] = isInside ? Color.white : Color.clear;
                }
            }
            
            texture.SetPixels(pixels);
            texture.Apply();
            
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = "RoundedBorder";
            return sprite;
        }
        
        private void StoreOriginalMinimapState()
        {
            if (minimapRect != null)
            {
                originalAnchoredPosition = minimapRect.anchoredPosition;
                originalSizeDelta = minimapRect.sizeDelta;
                originalAnchorMin = minimapRect.anchorMin;
                originalAnchorMax = minimapRect.anchorMax;
            }

            originalMinimapRadius = minimapRadius;
        }

        /// <summary>Show or hide minimap using CanvasGroup so Update() keeps running and we can show again after team is chosen.</summary>
        private void SetMinimapVisible(bool visible)
        {
            if (canvasGroup == null)
            {
                canvasGroup = GetComponent<CanvasGroup>();
                if (canvasGroup == null)
                    canvasGroup = gameObject.AddComponent<CanvasGroup>();
            }
            canvasGroup.alpha = visible ? 1f : 0f;
            canvasGroup.interactable = visible;
            canvasGroup.blocksRaycasts = visible;

            if (!visible)
                RestoreNonMinimapUi();
            else if (isExpanded)
                ApplyHideNonMinimapUi();
        }
        
        private void SetupMapSizeLabel()
        {
            if (mapSizeLabel == null)
            {
                Transform existing = minimapRect != null ? minimapRect.Find("MapSizeLabel") : null;
                if (existing != null)
                    mapSizeLabel = existing.GetComponent<TextMeshProUGUI>();
            }

            if (mapSizeLabel == null)
            {
                GameObject go = new GameObject("MapSizeLabel");
                go.transform.SetParent(minimapRect, false);
                RectTransform rt = go.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(0f, 0f);
                rt.pivot = new Vector2(0f, 0f);
                rt.anchoredPosition = new Vector2(10f, 10f);
                rt.sizeDelta = new Vector2(220f, 26f);

                mapSizeLabel = go.AddComponent<TextMeshProUGUI>();
                mapSizeLabel.alignment = TextAlignmentOptions.BottomLeft;
                mapSizeLabel.color = new Color(0.88f, 0.9f, 0.95f, 0.92f);
                mapSizeLabel.raycastTarget = false;
                mapSizeLabel.enableWordWrapping = false;
                mapSizeLabel.overflowMode = TextOverflowModes.Overflow;
                mapSizeLabel.fontSize = 12;
            }

            _lastMapSizeLabelW = float.NaN;
            _lastMapSizeLabelH = float.NaN;
            RefreshMapSizeLabelText(force: true);
        }

        private void RefreshMapSizeLabelText(bool force = false)
        {
            if (mapSizeLabel == null) return;

            float w = ToroidalMap.GetMapWidth();
            float h = ToroidalMap.GetMapHeight();
            if (!force && w == _lastMapSizeLabelW && h == _lastMapSizeLabelH) return;

            _lastMapSizeLabelW = w;
            _lastMapSizeLabelH = h;

            if (w <= 1f || h <= 1f)
                mapSizeLabel.text = "";
            else
                mapSizeLabel.text = $"{Mathf.RoundToInt(w)} \u00d7 {Mathf.RoundToInt(h)}";
        }

        private static Sprite GetOrCreateWhiteUiSprite()
        {
            if (_whiteUiSprite != null)
                return _whiteUiSprite;
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, Color.white);
            texture.Apply();
            _whiteUiSprite = Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 100f);
            _whiteUiSprite.name = "MinimapExpandButtonWhite";
            return _whiteUiSprite;
        }

        private void SetupExpandButton()
        {
            // Top-left of minimap (easier thumb reach on phones than bottom-right screen corner)
            GameObject buttonObj = new GameObject("ExpandButton");
            buttonObj.transform.SetParent(minimapRect, false);
            
            RectTransform buttonRect = buttonObj.AddComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(0, 1);
            buttonRect.anchorMax = new Vector2(0, 1);
            buttonRect.pivot = new Vector2(0, 1);
            buttonRect.anchoredPosition = new Vector2(8, -8);
            buttonRect.sizeDelta = new Vector2(46f, 20f);
            
            Image buttonImage = buttonObj.AddComponent<Image>();
            buttonImage.color = new Color(0.7f, 0.7f, 0.8f, 0.95f);
            buttonImage.sprite = GetOrCreateWhiteUiSprite();
            buttonImage.type = Image.Type.Simple;
            
            expandButton = buttonObj.AddComponent<Button>();
            expandButton.onClick.AddListener(ToggleExpand);
            
            var colors = expandButton.colors;
            colors.highlightedColor = new Color(0.9f, 0.9f, 1f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.9f, 1f);
            expandButton.colors = colors;

            GameObject labelGo = new GameObject("Label");
            labelGo.transform.SetParent(buttonObj.transform, false);
            RectTransform labelRt = labelGo.AddComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;

            expandButtonLabel = labelGo.AddComponent<TextMeshProUGUI>();
            expandButtonLabel.text = "[M]ap";
            expandButtonLabel.alignment = TextAlignmentOptions.Center;
            expandButtonLabel.fontSize = 11f;
            expandButtonLabel.color = new Color(0.12f, 0.12f, 0.18f, 1f);
            expandButtonLabel.raycastTarget = false;
            expandButtonLabel.enableWordWrapping = false;
            expandButtonLabel.overflowMode = TextOverflowModes.Overflow;
        }
        
        private void ToggleExpand()
        {
            isExpanded = !isExpanded;
            
            if (isExpanded)
            {
                ExpandMinimap();
            }
            else
            {
                CollapseMinimap();
            }
        }
        
        private void ExpandMinimap()
        {
            if (minimapRect == null) return;

            originalMinimapRadius = minimapRadius;
            
            // Same minimap: only change zoom (visible radius) and circle size. Content and coordinate system unchanged.
            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                RectTransform canvasRect = canvas.GetComponent<RectTransform>();
                // Calculate size based on canvas dimensions
                // CanvasScaler uses reference resolution, so we use that for consistent sizing
                CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
                float screenWidth, screenHeight;
                
                if (scaler != null)
                {
                    // Use reference resolution from CanvasScaler
                    screenWidth = scaler.referenceResolution.x;
                    screenHeight = scaler.referenceResolution.y;
                }
                else
                {
                    // Fallback to actual canvas size or screen size
                    screenWidth = canvasRect.sizeDelta.x > 0 ? canvasRect.sizeDelta.x : Screen.width;
                    screenHeight = canvasRect.sizeDelta.y > 0 ? canvasRect.sizeDelta.y : Screen.height;
                }
                
                // Use smaller dimension to ensure it fits on screen
                float minDimension = Mathf.Min(screenWidth, screenHeight);
                float calculatedExpandedSize = minDimension * expandedSizePercent;
                
                minimapRect.anchorMin = new Vector2(0.5f, 0.5f);
                minimapRect.anchorMax = new Vector2(0.5f, 0.5f);
                minimapRect.pivot = new Vector2(0.5f, 0.5f);
                minimapRect.anchoredPosition = Vector2.zero;
                minimapRect.sizeDelta = new Vector2(calculatedExpandedSize, calculatedExpandedSize);
                
                // Same minimap: larger circle (displaySize) and zoom to fit full map
                displaySize = calculatedExpandedSize;
                minimapRadius = GetExpandedWorldRadius(playerTransform != null ? playerTransform.position : Vector3.zero);
                
                // Update mask and background
                SetupCircularBackground();
                SetupMask();
                SetupCircularBorder();
                
                // Update button icon to collapse icon and reposition to top-middle
                if (expandButton != null)
                {
                    RectTransform buttonRect = expandButton.GetComponent<RectTransform>();
                    if (buttonRect != null)
                    {
                        buttonRect.anchorMin = new Vector2(0.5f, 1f);
                        buttonRect.anchorMax = new Vector2(0.5f, 1f);
                        buttonRect.pivot = new Vector2(0.5f, 1f);
                        buttonRect.anchoredPosition = new Vector2(0, -8);
                        buttonRect.sizeDelta = new Vector2(28f, 24f);
                    }

                    if (expandButtonLabel != null)
                    {
                        expandButtonLabel.text = "\u00d7";
                        expandButtonLabel.fontSize = 12f;
                    }
                }
                
                // Keep raycastTarget enabled so clicks are detected for marker placement
                Image minimapBg = GetComponent<Image>();
                if (minimapBg != null)
                {
                    minimapBg.raycastTarget = true;
                }
                
                // Keep raycast enabled on content and border
                if (minimapContent != null)
                {
                    Image contentImg = minimapContent.GetComponent<Image>();
                    if (contentImg != null)
                    {
                        contentImg.raycastTarget = true;
                    }
                }
                
                if (borderImage != null)
                {
                    borderImage.raycastTarget = true;
                }
                
                // Hide edge markers when expanded (showing full map)
                foreach (var marker in edgeMarkers.Values)
                {
                    if (marker != null) marker.gameObject.SetActive(false);
                }

                ApplyHideNonMinimapUi();
                transform.SetAsLastSibling();
            }
        }

        /// <summary>Hide every UI branch that is a sibling of the minimap on the path up to the HUD canvas.</summary>
        private void ApplyHideNonMinimapUi()
        {
            if (_nonMinimapUiRestore.Count > 0)
                return;

            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
                return;

            Transform canvasT = canvas.transform;
            Transform t = transform;
            while (t != null && t != canvasT)
            {
                Transform parent = t.parent;
                if (parent == null)
                    break;

                for (int i = 0; i < parent.childCount; i++)
                {
                    Transform sibling = parent.GetChild(i);
                    if (sibling == t)
                        continue;
                    PushCanvasGroupHide(sibling.gameObject);
                }

                t = parent;
            }
        }

        private void PushCanvasGroupHide(GameObject root)
        {
            if (root == null)
                return;

            CanvasGroup cg = root.GetComponent<CanvasGroup>();
            bool added = cg == null;
            if (cg == null)
                cg = root.AddComponent<CanvasGroup>();

            _nonMinimapUiRestore.Add(new NonMinimapUiRestoreState
            {
                Group = cg,
                AddedByMinimap = added,
                Alpha = cg.alpha,
                Interactable = cg.interactable,
                BlocksRaycasts = cg.blocksRaycasts
            });

            cg.alpha = 0f;
            cg.interactable = false;
            cg.blocksRaycasts = false;
        }

        private void RestoreNonMinimapUi()
        {
            for (int i = 0; i < _nonMinimapUiRestore.Count; i++)
            {
                NonMinimapUiRestoreState s = _nonMinimapUiRestore[i];
                if (s.Group == null)
                    continue;

                s.Group.alpha = s.Alpha;
                s.Group.interactable = s.Interactable;
                s.Group.blocksRaycasts = s.BlocksRaycasts;
                if (s.AddedByMinimap)
                    Destroy(s.Group);
            }

            _nonMinimapUiRestore.Clear();
        }

        private void OnDisable()
        {
            RestoreNonMinimapUi();
        }

        private void OnEnable()
        {
            if (isExpanded)
                ApplyHideNonMinimapUi();
        }
        
        private void CollapseMinimap()
        {
            if (minimapRect == null) return;

            RestoreNonMinimapUi();
            
            // Same minimap: restore smaller circle and zoomed-in radius
            minimapRect.anchorMin = originalAnchorMin;
            minimapRect.anchorMax = originalAnchorMax;
            minimapRect.pivot = new Vector2(1, 0);
            minimapRect.anchoredPosition = originalAnchoredPosition;
            minimapRect.sizeDelta = originalSizeDelta;
            
            // Restore display size
            displaySize = originalSizeDelta.x;
            
            // Restore minimap radius (must match pre-expand; was incorrectly hardcoded to 40f)
            minimapRadius = originalMinimapRadius;
            
            // Update mask and background
            SetupCircularBackground();
            SetupMask();
            SetupCircularBorder();
            
            // Update button icon back to expand icon and reposition to top-left of minimap
            if (expandButton != null)
            {
                RectTransform buttonRect = expandButton.GetComponent<RectTransform>();
                if (buttonRect != null)
                {
                    buttonRect.anchorMin = new Vector2(0, 1);
                    buttonRect.anchorMax = new Vector2(0, 1);
                    buttonRect.pivot = new Vector2(0, 1);
                    buttonRect.anchoredPosition = new Vector2(8, -8);
                    buttonRect.sizeDelta = new Vector2(46f, 20f);
                }

                if (expandButtonLabel != null)
                {
                    expandButtonLabel.text = "[M]ap";
                    expandButtonLabel.fontSize = 11f;
                }
            }
            
            // Re-enable raycast blocking on minimap background when collapsed
            Image minimapBg = GetComponent<Image>();
            if (minimapBg != null)
            {
                minimapBg.raycastTarget = true; // Keep enabled so clicks are detected
            }
            
            // Disable raycast on content and border so clicks pass through to minimap background
            // This ensures the entire minimap area is clickable, not just empty spaces
            if (minimapContent != null)
            {
                Image contentImg = minimapContent.GetComponent<Image>();
                if (contentImg != null)
                {
                    contentImg.raycastTarget = false; // Disable so clicks pass through to parent
                }
            }
            
            if (borderImage != null)
            {
                borderImage.raycastTarget = false; // Disable so clicks pass through to parent minimap
            }
            
            // Show edge markers again when collapsed
            foreach (var marker in edgeMarkers.Values)
            {
                if (marker != null) marker.gameObject.SetActive(true);
            }
        }
        
        private void SetupCircularBorder()
        {
            // Find the border object (it's a child named "Border")
            Transform borderTransform = transform.Find("Border");
            if (borderTransform != null)
            {
                borderImage = borderTransform.GetComponent<Image>();
                if (borderImage != null)
                {
                    // Get the actual border size (accounting for the offset)
                    RectTransform borderRect = borderTransform.GetComponent<RectTransform>();
                    float borderSize = displaySize; // Border spans the full minimap area
                    
                    // Create a circular border sprite (ring shape)
                    borderImage.sprite = CreateCircularBorderSprite((int)borderSize);
                    borderImage.type = Image.Type.Simple;
                }
            }
        }
        
        private Sprite CreateCircularBorderSprite(int size)
        {
            int textureSize = size;
            Texture2D texture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            
            Color[] pixels = new Color[textureSize * textureSize];
            float centerX = textureSize / 2f;
            float centerY = textureSize / 2f;
            float outerRadius = textureSize / 2f;
            float borderWidth = 5f; // Border thickness (matches the offset in GameSetup)
            float innerRadius = outerRadius - borderWidth;
            
            // Border color - lighter grey for better visibility
            Color borderColor = new Color(0.75f, 0.75f, 0.8f, 0.95f);
            
            for (int y = 0; y < textureSize; y++)
            {
                for (int x = 0; x < textureSize; x++)
                {
                    float dx = x - centerX;
                    float dy = y - centerY;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    
                    // Create a ring shape (circle with inner circle cut out)
                    if (dist <= outerRadius && dist >= innerRadius)
                    {
                        pixels[y * textureSize + x] = borderColor;
                    }
                    else
                    {
                        pixels[y * textureSize + x] = Color.clear;
                    }
                }
            }
            
            texture.SetPixels(pixels);
            texture.Apply();
            
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, textureSize, textureSize), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = "MinimapBorder";
            return sprite;
        }
        
        private void SetupCircularBackground()
        {
            // Get or add Image component to the minimap background
            Image bgImage = GetComponent<Image>();
            if (bgImage == null)
            {
                bgImage = gameObject.AddComponent<Image>();
            }
            
            // Set the background to use a circular sprite
            float backgroundAlpha = isExpanded ? expandedBackgroundAlpha : 0.4f;
            bgImage.sprite = CreateCircularBackgroundSprite((int)displaySize, backgroundAlpha);
            bgImage.type = Image.Type.Simple;
            // Scene Image may ship with alpha 0.4; use white so sprite alpha is not multiplied down.
            bgImage.color = Color.white;
        }
        
        private Sprite CreateCircularBackgroundSprite(int size, float alpha)
        {
            int textureSize = size;
            Texture2D texture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            
            Color[] pixels = new Color[textureSize * textureSize];
            float centerX = textureSize / 2f;
            float centerY = textureSize / 2f;
            float radius = textureSize / 2f;
            
            // Create circular background with semi-transparent black
            Color bgColor = new Color(0, 0, 0, alpha);
            
            for (int y = 0; y < textureSize; y++)
            {
                for (int x = 0; x < textureSize; x++)
                {
                    float dx = x - centerX;
                    float dy = y - centerY;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist <= radius)
                    {
                        pixels[y * textureSize + x] = bgColor;
                    }
                    else
                    {
                        pixels[y * textureSize + x] = Color.clear;
                    }
                }
            }
            
            texture.SetPixels(pixels);
            texture.Apply();
            
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, textureSize, textureSize), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = "MinimapBackground";
            return sprite;
        }
        
        private void SetupMask()
        {
            if (minimapContent == null) return;
            
            // Add Mask component to minimap content if it doesn't have one
            Mask mask = minimapContent.GetComponent<Mask>();
            if (mask == null)
            {
                mask = minimapContent.gameObject.AddComponent<Mask>();
            }
            mask.showMaskGraphic = false; // Don't show the mask graphic itself
            
            // Add Image component for the mask (required by Mask component)
            Image maskImage = minimapContent.GetComponent<Image>();
            if (maskImage == null)
            {
                maskImage = minimapContent.gameObject.AddComponent<Image>();
            }
            
            // Create circular mask sprite
            maskImage.sprite = CreateCircularMaskSprite((int)displaySize);
            maskImage.type = Image.Type.Simple;
        }
        
        private void SetupEdgeMarkerContainer()
        {
            // Create a container for edge markers that's outside the mask
            GameObject edgeContainerObj = new GameObject("EdgeMarkers");
            edgeContainerObj.transform.SetParent(minimapRect, false);
            edgeMarkerContainer = edgeContainerObj.AddComponent<RectTransform>();
            edgeMarkerContainer.anchorMin = Vector2.zero;
            edgeMarkerContainer.anchorMax = Vector2.one;
            edgeMarkerContainer.offsetMin = Vector2.zero;
            edgeMarkerContainer.offsetMax = Vector2.zero;
        }
        
        private Sprite CreateCircularMaskSprite(int size)
        {
            int textureSize = size;
            Texture2D texture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            
            Color[] pixels = new Color[textureSize * textureSize];
            float centerX = textureSize / 2f;
            float centerY = textureSize / 2f;
            float radius = textureSize / 2f;
            
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
            
            texture.SetPixels(pixels);
            texture.Apply();
            
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, textureSize, textureSize), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = "MinimapMask";
            return sprite;
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
                    // Regenerate circular sprites at the new resolution so the minimap stays crisp when resized/expanded.
                    SetupCircularBackground();
                    SetupCircularBorder();
                    SetupMask();
                }
            }

            RefreshMapSizeLabelText();

            // Clear stale reference if player ship was destroyed
            if (playerAnchor == null)
                playerTransform = null;

            bool needResolvePlayer = playerAnchor == null || playerTransform == null;
            if (needResolvePlayer)
            {
                RefreshEntityCache(true);
                playerAnchor = null;
                playerTransform = null;

                var sync = MinimapEcsEntitySync.Instance;
                if (sync != null && sync.TryGetLocalPlayer(out playerAnchor) && playerAnchor != null)
                    playerTransform = playerAnchor.transform;

                if (playerAnchor == null)
                {
                    foreach (var ship in cachedShips)
                    {
                        if (ship == null || !ship.IsLocalPlayer)
                            continue;
                        playerAnchor = ship;
                        playerTransform = ship.transform;
                        break;
                    }
                }

                if (playerAnchor == null)
                {
                    SetMinimapVisible(false);
                    return;
                }
            }

            if (playerAnchor.AwaitingTeamSelection || playerAnchor.Team == TeamId.None)
            {
                SetMinimapVisible(false);
                return;
            }

            // Toggle minimap expanded/minimized with M key
            if (Keyboard.current != null && Keyboard.current.mKey.wasPressedThisFrame)
                ToggleExpand();

            SetMinimapVisible(true);

            // Run every frame so blip motion stays smooth; heavy work inside UpdateBlips is throttled separately.
            UpdateBlips();
            
            // Handle minimap clicks for markers
            HandleMinimapClicks();
        }
        
        private void HandleMinimapClicks()
        {
            // Allow marker placement on both minimized and expanded minimap
            if (markerMenu == null)
            {
                Debug.LogWarning("HandleMinimapClicks: markerMenu is null!");
                return;
            }
            
            // Check for clicks/touches using new Input System
            bool clicked = false;
            Vector2 clickPos = Vector2.zero;
            
            // Mouse input
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            {
                clicked = true;
                clickPos = Mouse.current.position.ReadValue();
                Debug.Log($"Mouse click detected at: {clickPos}");
            }
            // Touch input (mobile)
            else if (Touchscreen.current != null && Touchscreen.current.touches.Count > 0)
            {
                var touch = Touchscreen.current.touches[0];
                if (touch.press.wasPressedThisFrame)
                {
                    clicked = true;
                    clickPos = touch.position.ReadValue();
                    Debug.Log($"Touch detected at: {clickPos}");
                }
            }
            
            if (clicked)
            {
                Debug.Log($"Click detected! Checking minimap bounds...");
                
                // Check if click is over minimap using direct bounds checking
                Canvas canvas = GetComponentInParent<Canvas>();
                if (canvas == null)
                {
                    Debug.LogWarning("Canvas is null!");
                    return;
                }
                if (minimapRect == null)
                {
                    Debug.LogWarning("minimapRect is null!");
                    return;
                }
                
                // Check canvas render mode - Screen Space - Overlay doesn't use a camera
                UnityEngine.Camera uiCamera = null;
                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    uiCamera = null; // Overlay mode doesn't use camera
                    Debug.Log($"Canvas is Screen Space Overlay, no camera needed");
                }
                else
                {
                    uiCamera = canvas.worldCamera ?? UnityEngine.Camera.main;
                    Debug.Log($"Using camera: {uiCamera?.name ?? "null"}");
                }
                
                Debug.Log($"Canvas render mode: {canvas.renderMode}, minimapRect size: {minimapRect.sizeDelta}, position: {minimapRect.anchoredPosition}");
                
                // Convert screen point to local point in minimap rect
                Vector2 localPoint;
                bool converted = RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    minimapRect, clickPos, uiCamera, out localPoint);
                
                Debug.Log($"ScreenPointToLocalPointInRectangle result: {converted}, clickPos: {clickPos}, localPoint: {localPoint}");
                
                Vector2 localPointToUse = localPoint;
                bool useLocalPoint = converted;
                
                // Fallback: if conversion failed, try using world position
                if (!converted)
                {
                    Debug.LogWarning($"Failed to convert screen point to local point! Trying alternative method...");
                    
                    // Try using world position of minimap rect
                    Vector3[] worldCorners = new Vector3[4];
                    minimapRect.GetWorldCorners(worldCorners);
                    
                    // Get world position of click
                    Vector3 clickWorldPos;
                    if (uiCamera != null)
                    {
                        clickWorldPos = uiCamera.ScreenToWorldPoint(new Vector3(clickPos.x, clickPos.y, uiCamera.nearClipPlane));
                    }
                    else
                    {
                        // For overlay canvas, use screen coordinates directly
                        clickWorldPos = new Vector3(clickPos.x, clickPos.y, 0);
                    }
                    
                    // Convert to local space manually
                    Vector3 localPos = minimapRect.InverseTransformPoint(clickWorldPos);
                    localPointToUse = new Vector2(localPos.x, localPos.y);
                    useLocalPoint = true;
                    
                    Debug.Log($"Using fallback conversion: clickWorldPos={clickWorldPos}, localPointToUse={localPointToUse}");
                }
                
                if (useLocalPoint)
                {
                    Debug.Log($"Using local point: {localPointToUse}");
                    
                    // Convert local point to be relative to center, not pivot
                    // When minimized, pivot is at (1,0) so center is offset
                    // When expanded, pivot is at (0.5,0.5) so center is at (0,0)
                    Vector2 centerOffset = Vector2.zero;
                    if (!isExpanded)
                    {
                        // Pivot is at bottom-right (1,0), so center is at (-width/2, height/2) in local space
                        centerOffset = new Vector2(-minimapRect.sizeDelta.x / 2f, minimapRect.sizeDelta.y / 2f);
                    }
                    Vector2 centerRelativePoint = localPointToUse - centerOffset;
                    
                    // Check if point is within minimap bounds (circular check)
                    float radius = minimapRect.sizeDelta.x / 2f;
                    float dist = Mathf.Sqrt(centerRelativePoint.x * centerRelativePoint.x + centerRelativePoint.y * centerRelativePoint.y);
                    Debug.Log($"Distance from center: {dist}, radius: {radius}, centerRelativePoint: {centerRelativePoint}, centerOffset: {centerOffset}");
                    
                    if (dist <= radius)
                    {
                        Debug.Log("Click is within minimap bounds!");
                        
                        // Check if we're clicking on the button (don't show menu)
                        if (expandButton != null)
                        {
                            RectTransform buttonRect = expandButton.GetComponent<RectTransform>();
                            if (RectTransformUtility.RectangleContainsScreenPoint(buttonRect, clickPos, uiCamera))
                            {
                                Debug.Log("Click is on expand button, ignoring");
                                return; // Don't show menu if clicking button
                            }
                        }
                        
                        // Don't show menu if clicking on the menu itself
                        if (markerMenu != null && markerMenu.gameObject.activeSelf && markerMenu.menuRect != null)
                        {
                            bool clickedOnMenu = false;
                            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                            {
                                // For overlay, check world corners
                                Vector3[] menuCorners = new Vector3[4];
                                markerMenu.menuRect.GetWorldCorners(menuCorners);
                                if (clickPos.x >= menuCorners[0].x && clickPos.x <= menuCorners[2].x &&
                                    clickPos.y >= menuCorners[0].y && clickPos.y <= menuCorners[2].y)
                                {
                                    clickedOnMenu = true;
                                }
                            }
                            else
                            {
                                clickedOnMenu = RectTransformUtility.RectangleContainsScreenPoint(markerMenu.menuRect, clickPos, uiCamera);
                            }
                            
                            if (clickedOnMenu)
                            {
                                Debug.Log("Click is on menu itself, ignoring");
                                return; // Menu will handle its own clicks
                            }
                        }
                        
                        // Store the click position for marker placement
                        Vector2 storedClickPos = clickPos;
                        Vector2 storedLocalPoint = centerRelativePoint; // Use center-relative point for marker placement
                        
                        // Show marker placement menu at click position
                        Debug.Log($"Showing marker menu at screen pos: {storedClickPos}, center-relative point: {storedLocalPoint}");
                        markerMenu.Show(storedClickPos, (markerType) => {
                            Debug.Log($"Marker menu callback invoked with type: {markerType}");
                            // Use stored center-relative point for accurate marker placement
                            PlaceMarker(storedLocalPoint, markerType);
                        });
                    }
                    else
                    {
                        Debug.Log($"Click is outside minimap circle (dist: {dist} > radius: {radius})");
                    }
                }
                else
                {
                    Debug.LogError($"Completely failed to convert screen point! Cannot show menu.");
                }
            }
        }
        
        private void PlaceMarker(Vector2 minimapLocalPos, MinimapMarkerKind markerType)
        {
            // Attack/defend markers are not wired to NetCode for Entities yet.
            Debug.Log($"Minimap marker placement ({markerType}) is not available in the ECS build yet.");
        }

        private void UpdateBlips()
        {
            if (playerTransform == null || playerAnchor == null)
                return;
            // Normal 6s full refresh. Do NOT force full FindObjects every tick while ghosts exist — that was a major hitch.
            RefreshEntityCache(false);
            Vector3 playerPos = playerTransform.position;
            if (isExpanded)
                minimapRadius = GetExpandedWorldRadius(playerPos);

            if (deadAsteroidGhosts.Count > 0 && Time.time >= nextGhostAsteroidRescanTime)
            {
                nextGhostAsteroidRescanTime = Time.time + GhostAsteroidRescanInterval;
                var sync = MinimapEcsEntitySync.Instance;
                if (sync != null)
                    cachedAsteroids = ToArray(sync.Asteroids);
            }
            // 1 world unit → minimap pixels (used for blip sizing and asteroid scale updates every frame)
            float worldToMinimapScale = displaySize / (minimapRadius * 2f);
            blipsToRemove.Clear();

            BuildCurrentAsteroidInstanceIdsFromBlips();
            foreach (var id in lastFrameAsteroidInstanceIds)
            {
                if (currentAsteroidInstanceIds.Contains(id))
                    continue;
                if (!asteroidLastWorldPosByInstanceId.TryGetValue(id, out var lastPos))
                    continue;
                if (!asteroidBlipPixelSizeByInstanceId.TryGetValue(id, out float pixSize))
                    pixSize = 8f;
                AddDeadAsteroidGhost(lastPos, pixSize);
                asteroidLastWorldPosByInstanceId.Remove(id);
                asteroidBlipPixelSizeByInstanceId.Remove(id);
            }

            foreach (var kv in blips)
            {
                if (kv.Key == null) { blipsToRemove.Add(kv.Key); continue; }
                if (!kv.Key.gameObject.activeInHierarchy) { blipsToRemove.Add(kv.Key); continue; }

                Vector3 worldPos = kv.Key.position;
                GetToroidalDelta(playerPos, worldPos, out float dx, out float dz);

                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist > minimapRadius) { kv.Value.gameObject.SetActive(false); continue; }
                kv.Value.gameObject.SetActive(true);

                float normX = dx / minimapRadius;
                float normZ = dz / minimapRadius;
                kv.Value.anchoredPosition = new Vector2(normX * displaySize * 0.5f, normZ * displaySize * 0.5f);

                if (blipTypes.TryGetValue(kv.Key, out var bt) && bt == BlipType.Irregular)
                {
                    // Asteroids can animate scale (e.g. respawn grow); keep blip size in sync — not only at first create.
                    float physicalSize = (kv.Key.localScale.x + kv.Key.localScale.y + kv.Key.localScale.z) / 3f;
                    float asteroidBlipSize = physicalSize * worldToMinimapScale * sizeScaleFactor * asteroidBlipScaleFactor;
                    UpdateBlip(kv.Key, asteroidColor, asteroidBlipSize);

                    int instanceId = kv.Key.GetInstanceID();
                    asteroidLastWorldPosByInstanceId[instanceId] = worldPos;
                    asteroidBlipPixelSizeByInstanceId[instanceId] = asteroidBlipSize;
                }
            }

            foreach (var t in blipsToRemove)
            {
                if (blips.TryGetValue(t, out var rt) && rt != null) Destroy(rt.gameObject);
                blips.Remove(t);
                blipImages.Remove(t);
                blipTypes.Remove(t);
                bullseyePulseTime.Remove(t); // Clean up pulse time tracking
                planetBlipLayoutState.Remove(t);
                
                // Also remove edge markers
                if (edgeMarkers.TryGetValue(t, out var edgeRt) && edgeRt != null) Destroy(edgeRt.gameObject);
                edgeMarkers.Remove(t);
                edgeMarkerImages.Remove(t);
                edgeMarkerIsHomePlanet.Remove(t);
            }
            
            edgeMarkersToRemoveList.Clear();
            foreach (var kv in edgeMarkers)
            {
                if (kv.Key == null || !kv.Key.gameObject.activeInHierarchy)
                {
                    edgeMarkersToRemoveList.Add(kv.Key);
                }
            }
            foreach (var t in edgeMarkersToRemoveList)
            {
                if (edgeMarkers.TryGetValue(t, out var rt) && rt != null) Destroy(rt.gameObject);
                edgeMarkers.Remove(t);
                edgeMarkerImages.Remove(t);
                edgeMarkerIsHomePlanet.Remove(t);
            }
            
            markerEdgeMarkersToRemoveList.Clear();
            foreach (var kv in markerEdgeMarkers)
            {
                if (kv.Key == null || !kv.Key.gameObject.activeInHierarchy)
                {
                    markerEdgeMarkersToRemoveList.Add(kv.Key);
                }
            }
            foreach (var t in markerEdgeMarkersToRemoveList)
            {
                if (markerEdgeMarkers.TryGetValue(t, out var rt) && rt != null) Destroy(rt.gameObject);
                markerEdgeMarkers.Remove(t);
                markerEdgeMarkerImages.Remove(t);
            }

            // Add new entities
            EnsureBlip(playerTransform, () => CreateBlip(Color.white, playerBlipSize, BlipType.Cross), true);
            if (blips.TryGetValue(playerTransform, out var playerRt) && playerRt != null)
            {
                playerRt.localEulerAngles = Vector3.zero;

                TeamId playerTeam = playerAnchor.Team;
                Color playerColor = playerTeam == TeamId.None ? Color.white : GetTeamColor(playerTeam);
                UpdateBlip(playerTransform, playerColor, playerBlipSize);
            }

            // Show all ships (friendly and enemy, including AI) on the minimap
            float currentRadius = minimapRadius;
            foreach (var ship in cachedShips)
            {
                if (ship == null)
                {
                    skippedNullShips++;
                    continue;
                }
                if (ship == playerAnchor || ship.IsDead) continue;
                
                // Calculate distance to check if ship is within visible area
                Vector3 worldPos = ship.transform.position;
                GetToroidalDelta(playerPos, worldPos, out float dx, out float dz);
                
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                bool friendly = ship.Team == playerAnchor.Team && ship.Team != TeamId.None;
                Color shipColor = friendly ? GetTeamColor(playerAnchor.Team) : GetEnemyColor(ship.Team);
                
                if (dist <= currentRadius)
                {
                    // Show blip when within visible area
                    EnsureBlip(ship.transform, () => CreateBlip(shipColor, 12f, BlipType.Cross));
                    // Remove any old ship edge marker (markers only for planets)
                    RemoveShipEdgeMarker(ship.transform);
                }
                else
                {
                    // Hide blip when outside visible area - ships don't get edge markers
                    if (blips.ContainsKey(ship.transform))
                    {
                        blips[ship.transform].gameObject.SetActive(false);
                    }
                    RemoveShipEdgeMarker(ship.transform);
                }
            }

            // Asteroid blip creation scans cached asteroids; throttle so huge asteroid counts don't cost every frame.
            if ((Time.frameCount & 7) == 0)
                EnsureAsteroidBlips(worldToMinimapScale);
            if (deadAsteroidGhosts.Count == 0 || (Time.frameCount & 3) == 0)
                RemoveDeadAsteroidGhostsOverlappingLiveAsteroids();
            
            foreach (var p in cachedPlanets)
            {
                if (p == null)
                {
                    skippedNullPlanets++;
                    continue;
                }
                
                Vector3 worldPos = p.transform.position;
                GetToroidalDelta(playerPos, worldPos, out float dx, out float dz);
                
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                bool isOutsideVisibleArea = dist > minimapRadius;
                
                if (isOutsideVisibleArea)
                {
                    // Hide the blip and show edge marker instead
                    if (blips.ContainsKey(p.transform))
                    {
                        blips[p.transform].gameObject.SetActive(false);
                    }
                    UpdateEdgeMarker(p.transform, dx, dz, dist, false, p.Team);
                }
                else
                {
                    // Show the blip and hide edge marker
                    if (edgeMarkers.ContainsKey(p.transform))
                    {
                        edgeMarkers[p.transform].gameObject.SetActive(false);
                    }
                    
                    // Use team color if captured, otherwise grey
                    Color planetBlipColor = p.Team == TeamId.None 
                        ? planetColor 
                        : GetTeamColor(p.Team);
                    // Get actual planet size from transform scale (fallback to BodySize property)
                    float actualPlanetSize = (p.transform.localScale.x + p.transform.localScale.y + p.transform.localScale.z) / 3f;
                    if (actualPlanetSize < 0.1f) actualPlanetSize = p.BodySize;
                    // Use same scale factor for all entities - directly proportional to world size
                    float planetBlipSize = actualPlanetSize * worldToMinimapScale * sizeScaleFactor;
                    if (blips.ContainsKey(p.transform))
                    {
                        blips[p.transform].gameObject.SetActive(true);
                        UpdatePlanetBlip(blips[p.transform], p, planetBlipColor, planetBlipSize);
                    }
                    else
                    {
                        EnsureBlip(p.transform, () => CreatePlanetBlip(p, planetBlipColor, planetBlipSize));
                    }
                }
            }

            foreach (var hp in cachedHomePlanets)
            {
                if (hp == null)
                {
                    skippedNullHomePlanets++;
                    continue;
                }
                Vector3 worldPos = hp.transform.position;
                GetToroidalDelta(playerPos, worldPos, out float dx, out float dz);
                
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                bool isOutsideVisibleArea = dist > minimapRadius;
                
                if (isOutsideVisibleArea)
                {
                    // Hide the blip and show edge marker instead
                    if (blips.ContainsKey(hp.transform))
                    {
                        blips[hp.transform].gameObject.SetActive(false);
                    }
                    UpdateEdgeMarker(hp.transform, dx, dz, dist, true, hp.Team);
                }
                else
                {
                    // Show the blip and hide edge marker
                    if (edgeMarkers.ContainsKey(hp.transform))
                    {
                        edgeMarkers[hp.transform].gameObject.SetActive(false);
                    }
                    
                    // Use team color for home planets; same blip treatment as planets (rings + population text)
                    Color homeBlipColor = hp.Team == TeamId.None 
                        ? homePlanetColor 
                        : GetTeamColor(hp.Team);
                    float actualHomeSize = (hp.transform.localScale.x + hp.transform.localScale.y + hp.transform.localScale.z) / 3f;
                    if (actualHomeSize < 0.1f) actualHomeSize = hp.BodySize;
                    float homeBlipSize = actualHomeSize * worldToMinimapScale * sizeScaleFactor;
                    if (blips.ContainsKey(hp.transform))
                    {
                        blips[hp.transform].gameObject.SetActive(true);
                        UpdatePlanetBlip(blips[hp.transform], hp, homeBlipColor, homeBlipSize);
                    }
                    else
                    {
                        EnsureBlip(hp.transform, () => CreatePlanetBlip(hp, homeBlipColor, homeBlipSize));
                    }
                }
            }

            UpdateGemMoonBlips(playerPos, worldToMinimapScale);

            RebuildLastFrameAsteroidInstanceIds();
            UpdateDeadAsteroidGhosts(playerPos);
        }

        private void BuildCurrentAsteroidInstanceIdsFromBlips()
        {
            currentAsteroidInstanceIds.Clear();
            foreach (var kv in blips)
            {
                if (kv.Key == null || !kv.Key.gameObject.activeInHierarchy)
                    continue;
                if (!blipTypes.TryGetValue(kv.Key, out var bt) || bt != BlipType.Irregular)
                    continue;
                currentAsteroidInstanceIds.Add(kv.Key.GetInstanceID());
            }
        }

        private void RebuildLastFrameAsteroidInstanceIds()
        {
            lastFrameAsteroidInstanceIds.Clear();
            foreach (var kv in blips)
            {
                if (kv.Key == null || !kv.Key.gameObject.activeInHierarchy)
                    continue;
                if (!blipTypes.TryGetValue(kv.Key, out var bt) || bt != BlipType.Irregular)
                    continue;
                lastFrameAsteroidInstanceIds.Add(kv.Key.GetInstanceID());
            }
        }

        private void AddDeadAsteroidGhost(Vector3 worldPos, float blipPixelSize)
        {
            Color c = asteroidColor;
            c.a = DeadAsteroidBlipAlpha;
            var rt = CreateBlip(c, blipPixelSize, BlipType.Irregular);
            if (rt == null)
                return;
            deadAsteroidGhosts.Add(new DeadAsteroidGhost
            {
                worldPos = worldPos,
                rt = rt
            });
        }

        private void RemoveDeadAsteroidGhostsOverlappingLiveAsteroids()
        {
            if (cachedAsteroids == null || cachedAsteroids.Length == 0 || deadAsteroidGhosts.Count == 0)
                return;
            for (int i = deadAsteroidGhosts.Count - 1; i >= 0; i--)
            {
                var g = deadAsteroidGhosts[i];
                foreach (var a in cachedAsteroids)
                {
                    if (a == null || a.IsDestroyed)
                        continue;
                    if (!blips.ContainsKey(a.transform))
                        continue;
                    GetToroidalDelta(g.worldPos, a.transform.position, out float dx, out float dz);
                    float dist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (dist < DeadAsteroidGhostClearMatchRadius)
                    {
                        if (g.rt != null)
                            Destroy(g.rt.gameObject);
                        deadAsteroidGhosts.RemoveAt(i);
                        break;
                    }
                }
            }
        }

        private void UpdateDeadAsteroidGhosts(Vector3 playerPos)
        {
            for (int i = deadAsteroidGhosts.Count - 1; i >= 0; i--)
            {
                var g = deadAsteroidGhosts[i];
                if (g.rt == null)
                {
                    deadAsteroidGhosts.RemoveAt(i);
                    continue;
                }
                GetToroidalDelta(playerPos, g.worldPos, out float dx, out float dz);
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist > minimapRadius)
                {
                    g.rt.gameObject.SetActive(false);
                    deadAsteroidGhosts[i] = g;
                    continue;
                }
                g.rt.gameObject.SetActive(true);
                float normX = dx / minimapRadius;
                float normZ = dz / minimapRadius;
                g.rt.anchoredPosition = new Vector2(normX * displaySize * 0.5f, normZ * displaySize * 0.5f);
                deadAsteroidGhosts[i] = g;
            }
        }

        private void EnsureBlip(Transform t, System.Func<RectTransform> create, bool isPlayer = false)
        {
            if (blips.ContainsKey(t))
            {
                return;
            }
            var newBlipRect = create();
            if (newBlipRect != null)
            {
                blips[t] = newBlipRect;
                var img = newBlipRect.GetComponent<Image>();
                if (img == null)
                {
                    var planetFill = newBlipRect.Find("PlanetFill");
                    if (planetFill != null)
                        img = planetFill.GetComponent<Image>();
                }
                if (img != null)
                {
                    blipImages[t] = img;
                    // Store the blip type for reference
                    if (img.sprite != null && img.sprite.name.Contains("Circle")) blipTypes[t] = BlipType.Circle;
                    else if (img.sprite != null && img.sprite.name.Contains("Capsule")) blipTypes[t] = BlipType.Capsule;
                    else if (img.sprite != null && img.sprite.name.Contains("Cross")) blipTypes[t] = BlipType.Cross;
                    else if (img.sprite != null && img.sprite.name.Contains("Irregular")) blipTypes[t] = BlipType.Irregular;
                    else if (img.sprite != null && img.sprite.name.Contains("Bullseye")) blipTypes[t] = BlipType.Bullseye;
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
            img.raycastTarget = false; // Don't block clicks - let them pass through to minimap background
            
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

        private RectTransform CreatePlanetBlip(MinimapBlipAnchor p, Color color, float size)
        {
            if (minimapContent == null || p == null) return null;

            var go = new GameObject("Blip", typeof(RectTransform));
            go.transform.SetParent(minimapContent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(size, size);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);

            // Level dots (small circles around the planet, behind fill)
            var dotsGo = new GameObject("LevelDots", typeof(RectTransform));
            dotsGo.transform.SetParent(rt, false);
            var dotsRect = dotsGo.GetComponent<RectTransform>();
            dotsRect.anchorMin = Vector2.zero;
            dotsRect.anchorMax = Vector2.one;
            dotsRect.offsetMin = Vector2.zero;
            dotsRect.offsetMax = Vector2.zero;
            AddLevelDotsToContainer(dotsRect, p.PlanetLevel, size, color);

            // Planet circle on top of lines
            var fillGo = new GameObject("PlanetFill", typeof(RectTransform));
            fillGo.transform.SetParent(rt, false);
            var fillRt = fillGo.GetComponent<RectTransform>();
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;
            var fillImg = fillGo.AddComponent<Image>();
            fillImg.sprite = CreateBlipSprite((int)size, BlipType.Circle);
            fillImg.color = color;
            fillImg.raycastTarget = false;

            // Population text on top; auto-sized to stay inside the circle (no wrap)
            var textGo = new GameObject("PopulationText", typeof(RectTransform));
            textGo.transform.SetParent(rt, false);
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0.5f, 0.5f);
            textRect.anchorMax = new Vector2(0.5f, 0.5f);
            textRect.pivot = new Vector2(0.5f, 0.5f);
            textRect.anchoredPosition = Vector2.zero;
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = p.Population.ToString();
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
            ApplyPlanetPopulationTextLayout(tmp, size);

            return rt;
        }

        /// <summary>Single-line population label: shrinks font to fit inside the planet, no wrapping.</summary>
        private static void ApplyPlanetPopulationTextLayout(TextMeshProUGUI tmp, float size)
        {
            if (tmp == null) return;
            float box = Mathf.Max(8f, size * 0.82f);
            tmp.rectTransform.sizeDelta = new Vector2(box, box);
            tmp.enableWordWrapping = false;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 6f;
            tmp.fontSizeMax = Mathf.Max(8f, size * 0.52f);
            tmp.overflowMode = TextOverflowModes.Overflow;
        }

        private Sprite _minimapLevelDotSpriteCache;

        private Sprite GetMinimapLevelDotSprite()
        {
            if (_minimapLevelDotSpriteCache != null) return _minimapLevelDotSpriteCache;
            _minimapLevelDotSpriteCache = CreateBlipSprite(24, BlipType.Circle);
            if (_minimapLevelDotSpriteCache != null)
                _minimapLevelDotSpriteCache.name = "MinimapLevelDot";
            return _minimapLevelDotSpriteCache;
        }

        private void AddLevelDotsToContainer(RectTransform container, int level, float blipSize, Color teamColor)
        {
            if (container == null || level < 1) return;
            Sprite dotSprite = GetMinimapLevelDotSprite();
            if (dotSprite == null) return;

            for (int i = 0; i < level; i++)
            {
                var dotGo = new GameObject("LevelDot" + i, typeof(RectTransform));
                dotGo.transform.SetParent(container, false);
                var dotRect = dotGo.GetComponent<RectTransform>();
                dotRect.anchorMin = new Vector2(0.5f, 0.5f);
                dotRect.anchorMax = new Vector2(0.5f, 0.5f);
                dotRect.pivot = new Vector2(0.5f, 0.5f);
                var img = dotGo.AddComponent<Image>();
                img.sprite = dotSprite;
                img.type = Image.Type.Simple;
                img.color = teamColor;
                img.raycastTarget = false;
            }

            LayoutLevelDots(container, level, blipSize, teamColor);
        }

        private void LayoutLevelDots(RectTransform container, int level, float blipSize, Color teamColor)
        {
            if (container == null || level < 1) return;
            float half = blipSize * 0.5f;
            float dotSize = Mathf.Max(3f, Mathf.Round(blipSize * 0.12f));
            float orbitR = half + dotSize * 0.5f + 1f;

            for (int i = 0; i < level; i++)
            {
                var dotRect = container.GetChild(i) as RectTransform;
                if (dotRect == null) continue;
                dotRect.sizeDelta = new Vector2(dotSize, dotSize);
                // Evenly around the planet; first dot at top (+y), then CCW
                float angle = Mathf.PI * 0.5f + (Mathf.PI * 2f * i) / level;
                float x = orbitR * Mathf.Cos(angle);
                float y = orbitR * Mathf.Sin(angle);
                dotRect.anchoredPosition = new Vector2(Mathf.Round(x), Mathf.Round(y));
                if (dotRect.GetComponent<Image>() is Image img)
                    img.color = teamColor;
            }
        }

        private void UpdatePlanetBlip(RectTransform blipRt, MinimapBlipAnchor p, Color color, float size)
        {
            if (blipRt == null || p == null) return;

            const float sizeQuantStep = 0.5f;
            float qSize = Mathf.Round(size / sizeQuantStep) * sizeQuantStep;
            int pop = p.Population;
            int level = p.PlanetLevel;
            Color32 c32 = color;

            if (planetBlipLayoutState.TryGetValue(p.transform, out var prev) &&
                Mathf.Approximately(prev.QuantizedSize, qSize) &&
                prev.Population == pop &&
                prev.Level == level &&
                prev.Color.r == c32.r && prev.Color.g == c32.g && prev.Color.b == c32.b && prev.Color.a == c32.a)
                return;

            planetBlipLayoutState[p.transform] = new PlanetBlipLayoutState
            {
                QuantizedSize = qSize,
                Population = pop,
                Level = level,
                Color = c32
            };

            blipRt.sizeDelta = new Vector2(qSize, qSize);
            Image planetImg = null;
            var fillTf = blipRt.Find("PlanetFill");
            if (fillTf != null)
                planetImg = fillTf.GetComponent<Image>();
            if (planetImg == null)
                planetImg = blipRt.GetComponent<Image>();
            if (planetImg != null)
                planetImg.color = color;

            var textGo = blipRt.Find("PopulationText");
            if (textGo != null && textGo.GetComponent<TextMeshProUGUI>() is TextMeshProUGUI tmp)
            {
                tmp.text = pop.ToString();
                ApplyPlanetPopulationTextLayout(tmp, qSize);
            }

            var dotsGo = blipRt.Find("LevelDots");
            if (dotsGo != null)
            {
                int needed = level;
                var dotsRect = dotsGo.GetComponent<RectTransform>();
                if (needed < 1)
                {
                    ClearLevelDotsChildrenImmediate(dotsGo.transform);
                }
                else if (dotsGo.childCount != needed)
                {
                    ClearLevelDotsChildrenImmediate(dotsGo.transform);
                    AddLevelDotsToContainer(dotsRect, needed, qSize, color);
                }
                else
                    LayoutLevelDots(dotsRect, needed, qSize, color);
            }
        }

        /// <summary>Deferred Destroy would leave stale children until end of frame and duplicate dots on rebuild.</summary>
        private static void ClearLevelDotsChildrenImmediate(Transform dotsGo)
        {
            if (dotsGo == null) return;
            while (dotsGo.childCount > 0)
                Object.DestroyImmediate(dotsGo.GetChild(0).gameObject);
        }

        /// <summary>
        /// Ensure asteroid blips exist. Size is updated every frame in the main blip loop when scale changes (respawn, etc.).
        /// </summary>
        private void UpdateGemMoonBlips(Vector3 playerPos, float worldToMinimapScale)
        {
            foreach (var moon in cachedGemMoons)
            {
                if (moon == null) continue;

                Transform moonTransform = moon.transform;
                Vector3 worldPos = moonTransform.position;
                GetToroidalDelta(playerPos, worldPos, out float dx, out float dz);

                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                bool isOutsideVisibleArea = dist > minimapRadius;
                bool isHome = moon.IsHomePlanet;
                TeamId team = moon.Team;

                Color moonBlipColor = team == TeamId.None
                    ? (isHome ? Color.Lerp(moonColor, homePlanetColor, 0.35f) : moonColor)
                    : GetTeamColor(team);
                float moonBlipSize = GetGemMoonBlipSize(moon, worldToMinimapScale);

                if (isOutsideVisibleArea)
                {
                    if (blips.ContainsKey(moonTransform))
                        blips[moonTransform].gameObject.SetActive(false);
                    RemoveShipEdgeMarker(moonTransform);
                }
                else
                {
                    if (blips.ContainsKey(moonTransform))
                    {
                        blips[moonTransform].gameObject.SetActive(true);
                        UpdateBlip(moonTransform, moonBlipColor, moonBlipSize);
                    }
                    else
                    {
                        EnsureBlip(moonTransform, () => CreateBlip(moonBlipColor, moonBlipSize, BlipType.Circle));
                    }
                }
            }
        }

        private float GetGemMoonBlipSize(MinimapBlipAnchor moon, float worldToMinimapScale)
        {
            float physicalSize = moon.MoonVisualSize > 0f
                ? moon.MoonVisualSize
                : (moon.transform.localScale.x + moon.transform.localScale.y + moon.transform.localScale.z) / 3f;

            return Mathf.Max(
                moonBlipMinSize,
                physicalSize * worldToMinimapScale * sizeScaleFactor * moonBlipScaleFactor);
        }

        private void EnsureAsteroidBlips(float worldToMinimapScale)
        {
            if (cachedAsteroids == null || cachedAsteroids.Length == 0)
                return;

            int maxAsteroids = isExpanded ? int.MaxValue : MaxAsteroidBlips;
            int created = 0;

            foreach (var a in cachedAsteroids)
            {
                if (a == null || a.IsDestroyed)
                    continue;

                if (blips.ContainsKey(a.transform))
                    continue;

                // Respect the same cap as before, but only when initially creating blips.
                if (created >= maxAsteroids)
                    break;

                created++;

                // Match blip size to world size: same mapping as planets/home (world units → pixels).
                float physicalSize = (a.transform.localScale.x + a.transform.localScale.y + a.transform.localScale.z) / 3f;
                float asteroidBlipSize = physicalSize * worldToMinimapScale * sizeScaleFactor * asteroidBlipScaleFactor;

                EnsureBlip(a.transform, () => CreateBlip(asteroidColor, asteroidBlipSize, BlipType.Irregular));
            }
        }

        private Sprite CreateBlipSprite(int size, BlipType blipType)
        {
            int textureSize = Mathf.Max(size, 32); // Minimum size for quality
            if (blipType == BlipType.Circle)
                textureSize = Mathf.Max(textureSize, 64); // Planet discs: extra resolution for smooth edges when scaled
            Texture2D texture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            
            Color[] pixels = new Color[textureSize * textureSize];
            float centerX = textureSize / 2f;
            float centerY = textureSize / 2f;
            
            switch (blipType)
            {
                case BlipType.Circle:
                {
                    // Anti-aliased disc (soft edge)
                    float radius = textureSize / 2f - 1f;
                    float aa = Mathf.Max(1f, textureSize * 0.02f);
                    for (int y = 0; y < textureSize; y++)
                    {
                        for (int x = 0; x < textureSize; x++)
                        {
                            float dx = x - centerX;
                            float dy = y - centerY;
                            float dist = Mathf.Sqrt(dx * dx + dy * dy);
                            float alpha;
                            if (dist <= radius - aa)
                                alpha = 1f;
                            else if (dist < radius + aa)
                                alpha = 1f - Mathf.SmoothStep(radius - aa, radius + aa, dist);
                            else
                                alpha = 0f;
                            pixels[y * textureSize + x] = new Color(1f, 1f, 1f, alpha);
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
                    
                case BlipType.Triangle:
                {
                    // Slightly tall, directional triangle so the tip indicates forward
                    float height = textureSize * 0.8f;
                    float halfBase = textureSize * 0.24f;
                    Vector2 p1 = new Vector2(centerX, centerY + height * 0.5f);          // Sharp tip
                    Vector2 p2 = new Vector2(centerX - halfBase, centerY - height * 0.5f); // Left base
                    Vector2 p3 = new Vector2(centerX + halfBase, centerY - height * 0.5f); // Right base
                    for (int y = 0; y < textureSize; y++)
                    {
                        for (int x = 0; x < textureSize; x++)
                        {
                            Vector2 p = new Vector2(x, y);
                            float d1 = Sign(p, p1, p2);
                            float d2 = Sign(p, p2, p3);
                            float d3 = Sign(p, p3, p1);
                            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
                            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
                            bool inside = !(hasNeg && hasPos);
                            pixels[y * textureSize + x] = inside ? Color.white : Color.clear;
                        }
                    }
                    break;
                }

                case BlipType.Cross:
                {
                    float halfStroke = Mathf.Max(1.25f, textureSize * 0.13f);
                    float invSqrt2 = 0.70710678f;
                    for (int y = 0; y < textureSize; y++)
                    {
                        for (int x = 0; x < textureSize; x++)
                        {
                            float dx = x - centerX;
                            float dy = y - centerY;
                            float d1 = Mathf.Abs(dx - dy) * invSqrt2;
                            float d2 = Mathf.Abs(dx + dy) * invSqrt2;
                            bool inside = d1 < halfStroke || d2 < halfStroke;
                            pixels[y * textureSize + x] = inside ? Color.white : Color.clear;
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
                    
                case BlipType.Bullseye:
                {
                    // Bullseye: circle within a circle, two-tone
                    float outerRadius = textureSize / 2f - 1f;
                    float innerRadius = outerRadius * 0.5f; // Inner circle is half the size
                    
                    for (int y = 0; y < textureSize; y++)
                    {
                        for (int x = 0; x < textureSize; x++)
                        {
                            float dx = x - centerX;
                            float dy = y - centerY;
                            float dist = Mathf.Sqrt(dx * dx + dy * dy);
                            
                            if (dist <= outerRadius)
                            {
                                // Outer circle - use white (will be tinted by color)
                                if (dist <= innerRadius)
                                {
                                    // Inner circle - slightly brighter
                                    pixels[y * textureSize + x] = Color.white;
                                }
                                else
                                {
                                    // Outer ring - slightly dimmer for contrast
                                    pixels[y * textureSize + x] = new Color(0.7f, 0.7f, 0.7f, 1f);
                                }
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
            
            string spriteName = "BlipSprite";
            switch (blipType)
            {
                case BlipType.Circle: spriteName = "Circle"; break;
                case BlipType.Capsule: spriteName = "Capsule"; break;
                case BlipType.Triangle: spriteName = "Triangle"; break;
                case BlipType.Cross: spriteName = "Cross"; break;
                case BlipType.Irregular: spriteName = "Irregular"; break;
                case BlipType.Bullseye: spriteName = "Bullseye"; break;
            }
            
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, textureSize, textureSize), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = spriteName;
            
            return sprite;
        }

        private Color GetTeamColor(TeamId team) => team.ToColor();

        private Color GetEnemyColor(TeamId team)
        {
            Color c = GetTeamColor(team);
            return new Color(c.r * 0.7f, c.g * 0.7f, c.b * 0.7f);
        }
        
        private void RemoveShipEdgeMarker(Transform shipTransform)
        {
            if (shipTransform == null || !edgeMarkers.ContainsKey(shipTransform)) return;
            var rt = edgeMarkers[shipTransform];
            if (rt != null) Destroy(rt.gameObject);
            edgeMarkers.Remove(shipTransform);
            edgeMarkerImages.Remove(shipTransform);
            edgeMarkerIsHomePlanet.Remove(shipTransform);
        }

        private void UpdateEdgeMarker(Transform planetTransform, float dx, float dz, float distance, bool isHomePlanet, TeamId team)
        {
            if (edgeMarkerContainer == null) return;
            
            // Calculate angle and position on edge
            float angle = Mathf.Atan2(dz, dx);
            float radius = displaySize / 2f;
            
            // Position on the edge of the circular minimap
            float edgeX = Mathf.Cos(angle) * radius;
            float edgeZ = Mathf.Sin(angle) * radius;
            
            // Get color
            Color markerColor = isHomePlanet 
                ? (team == TeamId.None ? homePlanetColor : GetTeamColor(team))
                : (team == TeamId.None ? planetColor : GetTeamColor(team));
            
            // Calculate marker size based on distance (closer = bigger, farther = smaller)
            // Distance ranges from minimapRadius to maxPlanetDistance
            float normalizedDistance = Mathf.Clamp01((distance - minimapRadius) / (maxPlanetDistance - minimapRadius));
            float markerSize = Mathf.Lerp(edgeMarkerMaxSize, edgeMarkerMinSize, normalizedDistance);
            
            // Create or update edge marker
            if (!edgeMarkers.ContainsKey(planetTransform))
            {
                CreateEdgeMarker(planetTransform, edgeX, edgeZ, angle, markerColor, isHomePlanet, markerSize);
            }
            else
            {
                RectTransform markerRect = edgeMarkers[planetTransform];
                if (markerRect != null)
                {
                    markerRect.gameObject.SetActive(true);
                    markerRect.anchoredPosition = new Vector2(edgeX, edgeZ);
                    markerRect.localEulerAngles = new Vector3(0, 0, angle * Mathf.Rad2Deg);
                    markerRect.sizeDelta = new Vector2(markerSize, markerSize);
                    
                    // Update color if team ownership changed
                    if (edgeMarkerImages.TryGetValue(planetTransform, out var img) && img != null)
                    {
                        img.color = markerColor;
                    }
                }
            }
        }
        
        private void UpdateMarkerEdgeMarker(Transform markerTransform, float dx, float dz, float distance, Color markerColor, MinimapMarkerKind markerType)
        {
            if (edgeMarkerContainer == null) return;
            
            float currentRadius = minimapRadius;
            
            // Calculate angle and position on edge
            float angle = Mathf.Atan2(dz, dx);
            float radius = displaySize / 2f;
            
            // Position on the edge of the circular minimap
            float edgeX = Mathf.Cos(angle) * radius;
            float edgeZ = Mathf.Sin(angle) * radius;
            
            // Calculate marker size based on distance (closer = bigger, farther = smaller)
            // Distance ranges from currentRadius to maxPlanetDistance
            float normalizedDistance = Mathf.Clamp01((distance - currentRadius) / (maxPlanetDistance - currentRadius));
            float markerSize = Mathf.Lerp(edgeMarkerMaxSize, edgeMarkerMinSize, normalizedDistance);
            
            // Create or update edge marker
            if (!markerEdgeMarkers.ContainsKey(markerTransform))
            {
                CreateMarkerEdgeMarker(markerTransform, edgeX, edgeZ, angle, markerColor, markerType, markerSize);
            }
            else
            {
                RectTransform markerRect = markerEdgeMarkers[markerTransform];
                if (markerRect != null)
                {
                    markerRect.gameObject.SetActive(true);
                    markerRect.anchoredPosition = new Vector2(edgeX, edgeZ);
                    markerRect.localEulerAngles = new Vector3(0, 0, angle * Mathf.Rad2Deg);
                    markerRect.sizeDelta = new Vector2(markerSize, markerSize);
                    
                    // Update color
                    if (markerEdgeMarkerImages.TryGetValue(markerTransform, out var img) && img != null)
                    {
                        img.color = markerColor;
                    }
                }
            }
        }
        
        private void CreateMarkerEdgeMarker(Transform markerTransform, float x, float z, float angle, Color color, MinimapMarkerKind markerType, float size)
        {
            GameObject markerObj = new GameObject(markerType == MinimapMarkerKind.Defend ? "DefendMarkerEdge" : "AttackMarkerEdge");
            markerObj.transform.SetParent(edgeMarkerContainer, false);
            
            Image img = markerObj.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false; // Don't block clicks
            
            // Create bullseye sprite for attack/defend markers
            Sprite markerSprite = CreateBullseyeSprite(markerType == MinimapMarkerKind.Defend, (int)edgeMarkerSize);
            img.sprite = markerSprite;
            
            RectTransform rt = markerObj.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(size, size);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, z);
            rt.localEulerAngles = new Vector3(0, 0, angle * Mathf.Rad2Deg);
            
            markerEdgeMarkers[markerTransform] = rt;
            markerEdgeMarkerImages[markerTransform] = img;
        }
        
        private Sprite CreateBullseyeSprite(bool isDefend, int textureSize)
        {
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
        
        private void CreateEdgeMarker(Transform planetTransform, float x, float z, float angle, Color color, bool isHomePlanet, float size)
        {
            GameObject markerObj = new GameObject(isHomePlanet ? "HomePlanetEdgeMarker" : "PlanetEdgeMarker");
            markerObj.transform.SetParent(edgeMarkerContainer, false);
            
            Image img = markerObj.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false; // Don't block clicks - let them pass through to minimap background
            
            // Create arrow/pointer sprite (use base size for sprite quality, but scale the rect transform)
            Sprite arrowSprite = CreateArrowSprite(isHomePlanet, (int)edgeMarkerSize);
            img.sprite = arrowSprite;
            
            RectTransform rt = markerObj.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(size, size);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, z);
            rt.localEulerAngles = new Vector3(0, 0, angle * Mathf.Rad2Deg);
            
            edgeMarkers[planetTransform] = rt;
            edgeMarkerImages[planetTransform] = img;
            edgeMarkerIsHomePlanet[planetTransform] = isHomePlanet;
        }
        
        private Sprite CreateArrowSprite(bool isHomePlanet, int textureSize)
        {
            Texture2D texture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            
            Color[] pixels = new Color[textureSize * textureSize];
            float centerX = textureSize / 2f;
            float centerY = textureSize / 2f;
            
            // Create arrow shape pointing right (will be rotated)
            // Arrow: triangle pointing right with a small circle/hexagon base for home planets
            for (int y = 0; y < textureSize; y++)
            {
                for (int x = 0; x < textureSize; x++)
                {
                    float dx = x - centerX;
                    float dy = y - centerY;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    
                    bool isInside = false;
                    
                    if (isHomePlanet)
                    {
                        // Home planet: circle base with arrow
                        // Circle base (left side)
                        float circleRadius = textureSize * 0.25f;
                        float circleCenterX = -textureSize * 0.15f;
                        float distFromCircleCenter = Mathf.Sqrt((x - (centerX + circleCenterX)) * (x - (centerX + circleCenterX)) + dy * dy);
                        if (distFromCircleCenter < circleRadius)
                        {
                            isInside = true;
                        }
                        // Arrow tip (right side)
                        else if (dx > -textureSize * 0.05f)
                        {
                            // Triangle pointing right
                            float tipX = textureSize * 0.4f;
                            float tipY = centerY;
                            float baseLeft = -textureSize * 0.05f;
                            float baseWidth = textureSize * 0.3f;
                            
                            // Check if point is inside triangle
                            Vector2 p1 = new Vector2(tipX, tipY);
                            Vector2 p2 = new Vector2(baseLeft, tipY - baseWidth / 2f);
                            Vector2 p3 = new Vector2(baseLeft, tipY + baseWidth / 2f);
                            Vector2 p = new Vector2(x, y);
                            
                            float d1 = Sign(p, p1, p2);
                            float d2 = Sign(p, p2, p3);
                            float d3 = Sign(p, p3, p1);
                            
                            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
                            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
                            
                            isInside = !(hasNeg && hasPos);
                        }
                    }
                    else
                    {
                        // Regular planet: simple arrow/triangle
                        float tipX = textureSize * 0.4f;
                        float tipY = centerY;
                        float baseLeft = -textureSize * 0.2f;
                        float baseWidth = textureSize * 0.35f;
                        
                        // Check if point is inside triangle
                        Vector2 p1 = new Vector2(tipX, tipY);
                        Vector2 p2 = new Vector2(baseLeft, tipY - baseWidth / 2f);
                        Vector2 p3 = new Vector2(baseLeft, tipY + baseWidth / 2f);
                        Vector2 p = new Vector2(x, y);
                        
                        float d1 = Sign(p, p1, p2);
                        float d2 = Sign(p, p2, p3);
                        float d3 = Sign(p, p3, p1);
                        
                        bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
                        bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
                        
                        isInside = !(hasNeg && hasPos);
                    }
                    
                    pixels[y * textureSize + x] = isInside ? Color.white : Color.clear;
                }
            }
            
            texture.SetPixels(pixels);
            texture.Apply();
            
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, textureSize, textureSize), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = isHomePlanet ? "HomePlanetArrow" : "PlanetArrow";
            return sprite;
        }
        
        private float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
        {
            return (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
        }
    }
}
