using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using TMPro;
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
        [SerializeField] private float edgeMarkerSize = 36f; // Base size of edge markers for planets outside visible area
        [SerializeField] private float edgeMarkerMinSize = 20f; // Minimum size for farthest planets
        [SerializeField] private float edgeMarkerMaxSize = 48f; // Maximum size for closest planets
        [SerializeField] private float maxPlanetDistance = 150f; // Maximum distance to consider for scaling (beyond minimap radius)
        
        [Header("Expand Settings")]
        [SerializeField] private float expandedSizePercent = 0.85f; // Percentage of screen to fill (85%)
        [SerializeField] private float fullMapRadius = 212f; // Radius to show when expanded (covers full 300x300 map including corners: sqrt(150^2 + 150^2) ≈ 212)
        [SerializeField] private float markerHeight = 1f; // Height above ground for markers
        
        private RectTransform minimapRect;
        private RectTransform edgeMarkerContainer; // Container for edge markers (outside mask)
        private Image borderImage; // Reference to the border image
        private Button expandButton;
        private bool isExpanded = false;
        private Vector2 originalAnchoredPosition;
        private Vector2 originalSizeDelta;
        private Vector2 originalAnchorMin;
        private Vector2 originalAnchorMax;
        
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
        [SerializeField] private Color teamCColor = new Color(0.3f, 1f, 0.4f);
        [SerializeField] private Color planetColor = new Color(0.6f, 0.6f, 0.6f);
        [SerializeField] private Color homePlanetColor = new Color(1f, 0.9f, 0.2f);
        [SerializeField] private Color asteroidColor = new Color(0.8f, 0.8f, 0.8f); // Light grey for better visibility

        private Starship playerShip;
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

        private enum BlipType
        {
            Circle,      // Planets, Gems
            Capsule,     // Ships
            Irregular,   // Asteroids
            Bullseye     // Markers (attack/defend)
        }

        private void Start()
        {
            minimapRect = GetComponent<RectTransform>();
            if (minimapRect != null)
            {
                // Update display size to match actual minimap size
                displaySize = minimapRect.sizeDelta.x; // Square, so x = y
            }
            
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
            
            // Setup marker placement menu
            SetupMarkerMenu();
            
            // Store original minimap position and size for collapse
            StoreOriginalMinimapState();
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
        }
        
        private void SetupExpandButton()
        {
            // Create expand button in bottom-right corner of minimap
            GameObject buttonObj = new GameObject("ExpandButton");
            buttonObj.transform.SetParent(minimapRect, false);
            
            RectTransform buttonRect = buttonObj.AddComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(1, 0);
            buttonRect.anchorMax = new Vector2(1, 0);
            buttonRect.pivot = new Vector2(1, 0);
            buttonRect.anchoredPosition = new Vector2(-8, 8);
            buttonRect.sizeDelta = new Vector2(36, 36); // Bigger button
            
            Image buttonImage = buttonObj.AddComponent<Image>();
            buttonImage.color = new Color(0.7f, 0.7f, 0.8f, 0.95f); // Brighter
            buttonImage.sprite = CreateExpandButtonSprite(36);
            buttonImage.type = Image.Type.Simple;
            
            expandButton = buttonObj.AddComponent<Button>();
            expandButton.onClick.AddListener(ToggleExpand);
            
            // Add hover effect
            var colors = expandButton.colors;
            colors.highlightedColor = new Color(0.9f, 0.9f, 1f, 1f); // Brighter on hover
            colors.pressedColor = new Color(0.8f, 0.8f, 0.9f, 1f);
            expandButton.colors = colors;
        }
        
        private Sprite CreateExpandButtonSprite(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            
            Color[] pixels = new Color[size * size];
            
            // Create a simple expand icon (corner arrows pointing outward)
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool isInside = false;
                    
                    // Draw corner arrows in bottom-right
                    // Horizontal arrow line (pointing right) - thicker
                    if (y >= size * 0.35f && y <= size * 0.45f && x >= size * 0.25f && x <= size * 0.7f)
                        isInside = true;
                    
                    // Vertical arrow line (pointing down) - thicker
                    if (x >= size * 0.35f && x <= size * 0.45f && y >= size * 0.05f && y <= size * 0.55f)
                        isInside = true;
                    
                    // Arrow head pointing right - bigger
                    if (x >= size * 0.65f && x <= size * 0.9f && y >= size * 0.25f && y <= size * 0.55f)
                    {
                        float arrowX = (x - size * 0.65f) / (size * 0.25f);
                        float arrowY = Mathf.Abs((y - size * 0.4f) / (size * 0.15f));
                        if (arrowX + arrowY <= 1f)
                            isInside = true;
                    }
                    
                    // Arrow head pointing down - bigger
                    if (x >= size * 0.25f && x <= size * 0.55f && y >= 0 && y <= size * 0.35f)
                    {
                        float arrowX = Mathf.Abs((x - size * 0.4f) / (size * 0.15f));
                        float arrowY = (size * 0.35f - y) / (size * 0.35f);
                        if (arrowX + arrowY <= 1f)
                            isInside = true;
                    }
                    
                    pixels[y * size + x] = isInside ? Color.white : Color.clear;
                }
            }
            
            texture.SetPixels(pixels);
            texture.Apply();
            
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(1, 0), 100f);
            sprite.name = "ExpandButton";
            return sprite;
        }
        
        private Sprite CreateCollapseButtonSprite(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            
            Color[] pixels = new Color[size * size];
            
            // Create a collapse icon (X or close icon for top-middle position)
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool isInside = false;
                    
                    // Create an X icon (better for top-middle position)
                    float centerX = size / 2f;
                    float centerY = size / 2f;
                    float lineWidth = size * 0.15f;
                    
                    // Diagonal line from top-left to bottom-right
                    float dx1 = (x - centerX) + (y - centerY);
                    float dy1 = (x - centerX) - (y - centerY);
                    if (Mathf.Abs(dx1) < lineWidth && Mathf.Abs(dy1) < size * 0.6f)
                        isInside = true;
                    
                    // Diagonal line from top-right to bottom-left
                    float dx2 = (x - centerX) - (y - centerY);
                    float dy2 = (x - centerX) + (y - centerY);
                    if (Mathf.Abs(dx2) < lineWidth && Mathf.Abs(dy2) < size * 0.6f)
                        isInside = true;
                    
                    pixels[y * size + x] = isInside ? Color.white : Color.clear;
                }
            }
            
            texture.SetPixels(pixels);
            texture.Apply();
            
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 1f), 100f);
            sprite.name = "CollapseButton";
            return sprite;
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
            
            // Move to center of screen and expand
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
                
                // Update display size for calculations
                displaySize = calculatedExpandedSize;
                
                // Temporarily increase minimap radius to show full map
                minimapRadius = fullMapRadius;
                
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
                        buttonRect.sizeDelta = new Vector2(36, 36);
                    }
                    
                    Image buttonImg = expandButton.GetComponent<Image>();
                    if (buttonImg != null)
                    {
                        buttonImg.sprite = CreateCollapseButtonSprite(36);
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
            }
        }
        
        private void CollapseMinimap()
        {
            if (minimapRect == null) return;
            
            // Restore original position and size
            minimapRect.anchorMin = originalAnchorMin;
            minimapRect.anchorMax = originalAnchorMax;
            minimapRect.pivot = new Vector2(1, 0);
            minimapRect.anchoredPosition = originalAnchoredPosition;
            minimapRect.sizeDelta = originalSizeDelta;
            
            // Restore display size
            displaySize = originalSizeDelta.x;
            
            // Restore minimap radius
            minimapRadius = 40f;
            
            // Update mask and background
            SetupCircularBackground();
            SetupMask();
            SetupCircularBorder();
            
            // Update button icon back to expand icon and reposition to bottom-right
            if (expandButton != null)
            {
                RectTransform buttonRect = expandButton.GetComponent<RectTransform>();
                if (buttonRect != null)
                {
                    buttonRect.anchorMin = new Vector2(1, 0);
                    buttonRect.anchorMax = new Vector2(1, 0);
                    buttonRect.pivot = new Vector2(1, 0);
                    buttonRect.anchoredPosition = new Vector2(-8, 8);
                    buttonRect.sizeDelta = new Vector2(36, 36);
                }
                
                Image buttonImg = expandButton.GetComponent<Image>();
                if (buttonImg != null)
                {
                    buttonImg.sprite = CreateExpandButtonSprite(36);
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
            bgImage.sprite = CreateCircularBackgroundSprite((int)displaySize);
            bgImage.type = Image.Type.Simple;
        }
        
        private Sprite CreateCircularBackgroundSprite(int size)
        {
            int textureSize = size;
            Texture2D texture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            
            Color[] pixels = new Color[textureSize * textureSize];
            float centerX = textureSize / 2f;
            float centerY = textureSize / 2f;
            float radius = textureSize / 2f;
            
            // Create circular background with semi-transparent black
            Color bgColor = new Color(0, 0, 0, 0.4f); // Semi-transparent black
            
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
        
        private void PlaceMarker(Vector2 minimapLocalPos, MinimapMarker.MarkerType markerType)
        {
            if (playerTransform == null)
            {
                Debug.LogWarning("PlaceMarker: playerTransform is null!");
                return;
            }
            
            // Use actual minimap rect size instead of constant displaySize for accurate positioning
            float actualMinimapSize = minimapRect != null ? minimapRect.sizeDelta.x : displaySize;
            
            Debug.Log($"PlaceMarker called: minimapLocalPos={minimapLocalPos}, markerType={markerType}, actualMinimapSize={actualMinimapSize}, displaySize={displaySize}, isExpanded={isExpanded}");
            
            // Convert minimap local position to normalized position (-1 to 1)
            // minimapLocalPos is relative to minimap center, so divide by radius (half of actual minimap size)
            float normalizedX = (minimapLocalPos.x / (actualMinimapSize / 2f));
            float normalizedZ = (minimapLocalPos.y / (actualMinimapSize / 2f));
            
            // Clamp to circle (minimap is circular)
            float dist = Mathf.Sqrt(normalizedX * normalizedX + normalizedZ * normalizedZ);
            if (dist > 1f)
            {
                normalizedX /= dist;
                normalizedZ /= dist;
            }
            
            // Convert to world position - use current minimap radius (changes when expanded)
            Vector3 playerPos = playerTransform.position;
            float currentRadius = isExpanded ? fullMapRadius : minimapRadius;
            
            // The normalized position represents the direction from player, scaled by minimap radius
            // So multiply normalized direction by radius to get world offset
            float worldX = playerPos.x + normalizedX * currentRadius;
            float worldZ = playerPos.z + normalizedZ * currentRadius;
            Vector3 worldPosition = new Vector3(worldX, markerHeight, worldZ);
            
            Debug.Log($"Normalized: ({normalizedX}, {normalizedZ}), currentRadius: {currentRadius}, playerPos: {playerPos}, worldPosition: {worldPosition}");
            
            Debug.Log($"Player pos: {playerPos}, currentRadius: {currentRadius}, normalized: ({normalizedX}, {normalizedZ}), worldPosition: {worldPosition}");
            
            // Get player's team
            TeamManager.Team playerTeam = TeamManager.Team.None;
            if (playerShip != null)
            {
                playerTeam = playerShip.ShipTeam;
            }
            
            Debug.Log($"Creating marker: worldPosition={worldPosition}, markerType={markerType}, team={playerTeam}");
            
            // Create marker via network
            if (MinimapMarkerManager.Instance != null)
            {
                MinimapMarkerManager.Instance.CreateMarkerServerRpc(worldPosition, markerType, playerTeam);
                Debug.Log("Marker creation ServerRpc called");
            }
            else
            {
                Debug.LogError("MinimapMarkerManager.Instance is null! Cannot create marker.");
            }
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
                bullseyePulseTime.Remove(t); // Clean up pulse time tracking
                
                // Also remove edge markers
                if (edgeMarkers.TryGetValue(t, out var edgeRt) && edgeRt != null) Destroy(edgeRt.gameObject);
                edgeMarkers.Remove(t);
                edgeMarkerImages.Remove(t);
                edgeMarkerIsHomePlanet.Remove(t);
            }
            
            // Clean up edge markers for planets that no longer exist
            var edgeMarkersToRemove = new List<Transform>();
            foreach (var kv in edgeMarkers)
            {
                if (kv.Key == null || !kv.Key.gameObject.activeInHierarchy)
                {
                    edgeMarkersToRemove.Add(kv.Key);
                }
            }
            foreach (var t in edgeMarkersToRemove)
            {
                if (edgeMarkers.TryGetValue(t, out var rt) && rt != null) Destroy(rt.gameObject);
                edgeMarkers.Remove(t);
                edgeMarkerImages.Remove(t);
                edgeMarkerIsHomePlanet.Remove(t);
            }
            
            // Clean up edge markers for attack/defend markers that no longer exist
            var markerEdgeMarkersToRemove = new List<Transform>();
            foreach (var kv in markerEdgeMarkers)
            {
                if (kv.Key == null || !kv.Key.gameObject.activeInHierarchy)
                {
                    markerEdgeMarkersToRemove.Add(kv.Key);
                }
            }
            foreach (var t in markerEdgeMarkersToRemove)
            {
                if (markerEdgeMarkers.TryGetValue(t, out var rt) && rt != null) Destroy(rt.gameObject);
                markerEdgeMarkers.Remove(t);
                markerEdgeMarkerImages.Remove(t);
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
                
                Vector3 worldPos = p.transform.position;
                float dx = worldPos.x - playerPos.x;
                float dz = worldPos.z - playerPos.z;
                
                // Toroidal distance (clamp to nearest wrap)
                float mapW = 300f, mapH = 300f;
                if (dx > mapW / 2) dx -= mapW;
                if (dx < -mapW / 2) dx += mapW;
                if (dz > mapH / 2) dz -= mapH;
                if (dz < -mapH / 2) dz += mapH;
                
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                bool isOutsideVisibleArea = dist > minimapRadius;
                
                if (isOutsideVisibleArea)
                {
                    // Hide the blip and show edge marker instead
                    if (blips.ContainsKey(p.transform))
                    {
                        blips[p.transform].gameObject.SetActive(false);
                    }
                    UpdateEdgeMarker(p.transform, dx, dz, dist, false, p.TeamOwnership);
                }
                else
                {
                    // Show the blip and hide edge marker
                    if (edgeMarkers.ContainsKey(p.transform))
                    {
                        edgeMarkers[p.transform].gameObject.SetActive(false);
                    }
                    
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
                        blips[p.transform].gameObject.SetActive(true);
                        UpdateBlip(p.transform, planetBlipColor, planetBlipSize);
                    }
                    else
                    {
                        EnsureBlip(p.transform, () => CreateBlip(planetBlipColor, planetBlipSize, BlipType.Circle));
                    }
                }
            }

            foreach (var hp in FindObjectsOfType<HomePlanet>())
            {
                Vector3 worldPos = hp.transform.position;
                float dx = worldPos.x - playerPos.x;
                float dz = worldPos.z - playerPos.z;
                
                // Toroidal distance (clamp to nearest wrap)
                float mapW = 300f, mapH = 300f;
                if (dx > mapW / 2) dx -= mapW;
                if (dx < -mapW / 2) dx += mapW;
                if (dz > mapH / 2) dz -= mapH;
                if (dz < -mapH / 2) dz += mapH;
                
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                bool isOutsideVisibleArea = dist > minimapRadius;
                
                if (isOutsideVisibleArea)
                {
                    // Hide the blip and show edge marker instead
                    if (blips.ContainsKey(hp.transform))
                    {
                        blips[hp.transform].gameObject.SetActive(false);
                    }
                    UpdateEdgeMarker(hp.transform, dx, dz, dist, true, hp.TeamOwnership);
                }
                else
                {
                    // Show the blip and hide edge marker
                    if (edgeMarkers.ContainsKey(hp.transform))
                    {
                        edgeMarkers[hp.transform].gameObject.SetActive(false);
                    }
                    
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
                        blips[hp.transform].gameObject.SetActive(true);
                        UpdateBlip(hp.transform, homeBlipColor, homeBlipSize);
                    }
                    else
                    {
                        EnsureBlip(hp.transform, () => CreateBlip(homeBlipColor, homeBlipSize, BlipType.Circle));
                    }
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
            
            // Update minimap markers
            var allMarkers = FindObjectsOfType<MinimapMarker>();
            float currentRadius = isExpanded ? fullMapRadius : minimapRadius;
            
            foreach (var marker in allMarkers)
            {
                if (!marker.IsSpawned) continue;
                
                Vector3 worldPos = marker.transform.position;
                float dx = worldPos.x - playerPos.x;
                float dz = worldPos.z - playerPos.z;
                
                // Toroidal distance (clamp to nearest wrap)
                float mapW = 300f, mapH = 300f;
                if (dx > mapW / 2) dx -= mapW;
                if (dx < -mapW / 2) dx += mapW;
                if (dz > mapH / 2) dz -= mapH;
                if (dz < -mapH / 2) dz += mapH;
                
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                bool isOutsideVisibleArea = dist > currentRadius;
                
                // Get marker color based on type and team
                Color markerColor = marker.Type == MinimapMarker.MarkerType.Defend
                    ? new Color(0.2f, 0.8f, 0.2f, 1f) // Green for defend
                    : new Color(0.8f, 0.2f, 0.2f, 1f); // Red for attack
                
                // Blend with team color
                TeamManager.Team markerTeam = marker.Team;
                if (markerTeam != TeamManager.Team.None)
                {
                    Color teamColor = GetTeamColor(markerTeam);
                    markerColor = Color.Lerp(markerColor, teamColor, 0.3f);
                }
                
                if (isOutsideVisibleArea)
                {
                    // Hide the blip and show edge marker instead
                    if (blips.ContainsKey(marker.transform))
                    {
                        blips[marker.transform].gameObject.SetActive(false);
                    }
                    UpdateMarkerEdgeMarker(marker.transform, dx, dz, dist, markerColor, marker.Type);
                }
                else
                {
                    // Show the blip and hide edge marker
                    if (markerEdgeMarkers.ContainsKey(marker.transform))
                    {
                        markerEdgeMarkers[marker.transform].gameObject.SetActive(false);
                    }
                    
                    float markerBlipSize = 8f;
                    float baseBlipSize = markerBlipSize;
                    
                    // Add pulsing animation for bullseye markers
                    if (blips.ContainsKey(marker.transform))
                    {
                        // Initialize pulse time if not already set
                        if (!bullseyePulseTime.ContainsKey(marker.transform))
                        {
                            bullseyePulseTime[marker.transform] = 0f;
                        }
                        
                        // Update pulse animation
                        float pulseTime = bullseyePulseTime[marker.transform];
                        pulseTime += Time.deltaTime * 8f; // Pulse speed (8 pulses per second - much faster)
                        if (pulseTime > Mathf.PI * 2f) pulseTime -= Mathf.PI * 2f;
                        bullseyePulseTime[marker.transform] = pulseTime;
                        
                        // Pulse size: base size to 3x base size, using sine wave for dramatic effect
                        // Using (1 + sin) / 2 to map sine from [-1,1] to [0,1], then scale to [1, 3]
                        float pulseScale = 1f + (Mathf.Sin(pulseTime) + 1f) * 1f; // Pulse between 1.0 and 3.0 (200% increase)
                        float pulsedSize = baseBlipSize * pulseScale;
                        
                        UpdateBlip(marker.transform, markerColor, pulsedSize);
                    }
                    else
                    {
                        EnsureBlip(marker.transform, () => CreateBlip(markerColor, markerBlipSize, BlipType.Bullseye));
                        // Initialize pulse time for new bullseye
                        bullseyePulseTime[marker.transform] = 0f;
                    }
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
                case BlipType.Irregular: spriteName = "Irregular"; break;
                case BlipType.Bullseye: spriteName = "Bullseye"; break;
            }
            
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
        
        private void UpdateEdgeMarker(Transform planetTransform, float dx, float dz, float distance, bool isHomePlanet, TeamManager.Team team)
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
                ? (team == TeamManager.Team.None ? homePlanetColor : GetTeamColor(team))
                : (team == TeamManager.Team.None ? planetColor : GetTeamColor(team));
            
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
        
        private void UpdateMarkerEdgeMarker(Transform markerTransform, float dx, float dz, float distance, Color markerColor, MinimapMarker.MarkerType markerType)
        {
            if (edgeMarkerContainer == null) return;
            
            float currentRadius = isExpanded ? fullMapRadius : minimapRadius;
            
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
        
        private void CreateMarkerEdgeMarker(Transform markerTransform, float x, float z, float angle, Color color, MinimapMarker.MarkerType markerType, float size)
        {
            GameObject markerObj = new GameObject(markerType == MinimapMarker.MarkerType.Defend ? "DefendMarkerEdge" : "AttackMarkerEdge");
            markerObj.transform.SetParent(edgeMarkerContainer, false);
            
            Image img = markerObj.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false; // Don't block clicks
            
            // Create bullseye sprite for attack/defend markers
            Sprite markerSprite = CreateBullseyeSprite(markerType == MinimapMarker.MarkerType.Defend, (int)edgeMarkerSize);
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
