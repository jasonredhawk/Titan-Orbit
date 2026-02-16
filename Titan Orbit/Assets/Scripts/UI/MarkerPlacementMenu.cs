using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using TitanOrbit.Core;

namespace TitanOrbit.UI
{
    /// <summary>
    /// Popup menu for placing attack/defend markers on the minimap
    /// </summary>
    public class MarkerPlacementMenu : MonoBehaviour
    {
        [Header("UI References")]
        public Button attackButton;
        public Button defendButton;
        public RectTransform menuRect;
        public Image backgroundImage;
        
        private System.Action<MinimapMarker.MarkerType> onMarkerSelected;
        private Vector2 targetPosition;
        private bool justShown = false;
        private float showTime = 0f;
        private const float CLICK_IGNORE_DURATION = 0.2f; // Ignore clicks for 0.2 seconds after showing
        
        private void Start()
        {
            if (attackButton != null)
            {
                attackButton.onClick.RemoveAllListeners(); // Clear any existing listeners
                attackButton.onClick.AddListener(() => {
                    Debug.Log("Attack button clicked!");
                    OnMarkerSelected(MinimapMarker.MarkerType.Attack);
                });
            }
            else
            {
                Debug.LogError("Attack button is null in MarkerPlacementMenu!");
            }
            
            if (defendButton != null)
            {
                defendButton.onClick.RemoveAllListeners(); // Clear any existing listeners
                defendButton.onClick.AddListener(() => {
                    Debug.Log("Defend button clicked!");
                    OnMarkerSelected(MinimapMarker.MarkerType.Defend);
                });
            }
            else
            {
                Debug.LogError("Defend button is null in MarkerPlacementMenu!");
            }
            
            // Hide by default
            gameObject.SetActive(false);
        }
        
        public void Show(Vector2 screenPosition, System.Action<MinimapMarker.MarkerType> callback)
        {
            Debug.Log($"MarkerPlacementMenu.Show called at screen pos: {screenPosition}");
            
            onMarkerSelected = callback;
            targetPosition = screenPosition;
            justShown = true;
            showTime = Time.time;
            
            // Position menu at click location
            if (menuRect != null)
            {
                Canvas canvas = GetComponentInParent<Canvas>();
                if (canvas != null)
                {
                    if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                    {
                        // For Screen Space Overlay, convert screen position to canvas local space
                        RectTransform canvasRect = canvas.GetComponent<RectTransform>();
                        Vector2 localPoint;
                        
                        // Use RectTransformUtility with null camera for overlay
                        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                            canvasRect, screenPosition, null, out localPoint))
                        {
                            menuRect.anchoredPosition = localPoint;
                            
                            // Clamp to canvas edges
                            float menuWidth = menuRect.sizeDelta.x;
                            float menuHeight = menuRect.sizeDelta.y;
                            float canvasWidth = canvasRect.sizeDelta.x;
                            float canvasHeight = canvasRect.sizeDelta.y;
                            
                            // If canvas size is 0, use screen size
                            if (canvasWidth <= 0) canvasWidth = Screen.width;
                            if (canvasHeight <= 0) canvasHeight = Screen.height;
                            
                            // Clamp X
                            if (localPoint.x + menuWidth / 2 > canvasWidth / 2)
                                localPoint.x = canvasWidth / 2 - menuWidth / 2;
                            if (localPoint.x - menuWidth / 2 < -canvasWidth / 2)
                                localPoint.x = -canvasWidth / 2 + menuWidth / 2;
                            
                            // Clamp Y
                            if (localPoint.y + menuHeight / 2 > canvasHeight / 2)
                                localPoint.y = canvasHeight / 2 - menuHeight / 2;
                            if (localPoint.y - menuHeight / 2 < -canvasHeight / 2)
                                localPoint.y = -canvasHeight / 2 + menuHeight / 2;
                            
                            menuRect.anchoredPosition = localPoint;
                            Debug.Log($"Menu positioned at: {menuRect.anchoredPosition} (screen pos was {screenPosition})");
                        }
                        else
                        {
                            // Fallback: use world position
                            menuRect.position = screenPosition;
                            Debug.LogWarning($"Failed to convert menu position, using world position: {menuRect.position}");
                        }
                    }
                    else
                    {
                        // For other render modes, use RectTransformUtility
                        RectTransform canvasRect = canvas.GetComponent<RectTransform>();
                        Vector2 localPoint;
                        UnityEngine.Camera uiCamera = canvas.worldCamera ?? UnityEngine.Camera.main;
                        
                        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                            canvasRect, screenPosition, uiCamera, out localPoint))
                        {
                            menuRect.anchoredPosition = localPoint;
                            
                            // Clamp to canvas edges
                            float menuWidth = menuRect.sizeDelta.x;
                            float menuHeight = menuRect.sizeDelta.y;
                            float canvasWidth = canvasRect.sizeDelta.x;
                            float canvasHeight = canvasRect.sizeDelta.y;
                            
                            // Clamp X
                            if (localPoint.x + menuWidth / 2 > canvasWidth / 2)
                                localPoint.x = canvasWidth / 2 - menuWidth / 2;
                            if (localPoint.x - menuWidth / 2 < -canvasWidth / 2)
                                localPoint.x = -canvasWidth / 2 + menuWidth / 2;
                            
                            // Clamp Y
                            if (localPoint.y + menuHeight / 2 > canvasHeight / 2)
                                localPoint.y = canvasHeight / 2 - menuHeight / 2;
                            if (localPoint.y - menuHeight / 2 < -canvasHeight / 2)
                                localPoint.y = -canvasHeight / 2 + menuHeight / 2;
                            
                            menuRect.anchoredPosition = localPoint;
                            Debug.Log($"Menu positioned at: {menuRect.anchoredPosition}");
                        }
                        else
                        {
                            Debug.LogWarning("Failed to convert menu position");
                        }
                    }
                }
                else
                {
                    Debug.LogError("Canvas is null in MarkerPlacementMenu.Show!");
                }
            }
            
            // Ensure menu is on top and visible
            if (menuRect != null)
            {
                // Move menu to canvas root to ensure it's on top of everything
                Canvas canvas = GetComponentInParent<Canvas>();
                if (canvas != null && menuRect.parent != canvas.transform)
                {
                    menuRect.SetParent(canvas.transform, false);
                    Debug.Log("Moved menu to canvas root for proper z-ordering");
                }
                
                menuRect.SetAsLastSibling();
                
                // Ensure it's visible
                CanvasGroup canvasGroup = GetComponent<CanvasGroup>();
                if (canvasGroup == null)
                {
                    canvasGroup = gameObject.AddComponent<CanvasGroup>();
                }
                canvasGroup.alpha = 1f;
                canvasGroup.interactable = true;
                canvasGroup.blocksRaycasts = true;
                
                // Ensure scale is correct
                menuRect.localScale = Vector3.one;
                
                // Ensure it's enabled and visible
                gameObject.SetActive(true);
            }
            
            // Ensure buttons are interactable
            if (attackButton != null)
            {
                attackButton.interactable = true;
                attackButton.gameObject.SetActive(true);
            }
            if (defendButton != null)
            {
                defendButton.interactable = true;
                defendButton.gameObject.SetActive(true);
            }
            
            // Make sure the GameObject itself is active
            gameObject.SetActive(true);
            
            // Force update
            Canvas.ForceUpdateCanvases();
            
            Debug.Log($"MarkerPlacementMenu activated. Active: {gameObject.activeSelf}, Position: {menuRect?.anchoredPosition}, Scale: {menuRect?.localScale}, ActiveInHierarchy: {gameObject.activeInHierarchy}");
        }
        
        public void Hide()
        {
            gameObject.SetActive(false);
            onMarkerSelected = null;
        }
        
        private void OnMarkerSelected(MinimapMarker.MarkerType markerType)
        {
            Debug.Log($"OnMarkerSelected called with type: {markerType}");
            if (onMarkerSelected != null)
            {
                Debug.Log("Invoking callback");
                onMarkerSelected.Invoke(markerType);
            }
            else
            {
                Debug.LogWarning("onMarkerSelected callback is null!");
            }
            Hide();
        }
        
        private void Update()
        {
            if (!gameObject.activeSelf) return; // Don't process if menu is hidden
            
            // Don't hide immediately after showing (prevent immediate dismissal from the click that opened it)
            if (justShown)
            {
                if (Time.time - showTime < CLICK_IGNORE_DURATION)
                {
                    return; // Still in ignore period
                }
                else
                {
                    justShown = false; // Ignore period expired
                }
            }
            
            // Hide menu if clicking/touching outside of it
            bool clickedOutside = false;
            Vector2 clickPos = Vector2.zero;
            
            // Mouse input (new Input System)
            if (Mouse.current != null && (Mouse.current.leftButton.wasPressedThisFrame || Mouse.current.rightButton.wasPressedThisFrame))
            {
                clickedOutside = true;
                clickPos = Mouse.current.position.ReadValue();
            }
            // Touch input (mobile) - new Input System
            else if (Touchscreen.current != null && Touchscreen.current.touches.Count > 0)
            {
                var touch = Touchscreen.current.touches[0];
                if (touch.press.wasPressedThisFrame)
                {
                    clickedOutside = true;
                    clickPos = touch.position.ReadValue();
                }
            }
            
            if (clickedOutside && menuRect != null && !justShown)
            {
                Canvas canvas = GetComponentInParent<Canvas>();
                if (canvas != null)
                {
                    UnityEngine.Camera uiCamera = null;
                    if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                    {
                        uiCamera = null;
                    }
                    else
                    {
                        uiCamera = canvas.worldCamera ?? UnityEngine.Camera.main;
                    }
                    
                    // Check if click is on a button (don't hide if clicking buttons)
                    bool clickedOnButton = false;
                    if (attackButton != null)
                    {
                        RectTransform buttonRect = attackButton.GetComponent<RectTransform>();
                        if (buttonRect != null)
                        {
                            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                            {
                                // For overlay, check if click is within button's world bounds
                                Vector3[] corners = new Vector3[4];
                                buttonRect.GetWorldCorners(corners);
                                if (clickPos.x >= corners[0].x && clickPos.x <= corners[2].x &&
                                    clickPos.y >= corners[0].y && clickPos.y <= corners[2].y)
                                {
                                    clickedOnButton = true;
                                    Debug.Log("Click is on attack button");
                                }
                            }
                            else
                            {
                                if (RectTransformUtility.RectangleContainsScreenPoint(buttonRect, clickPos, uiCamera))
                                {
                                    clickedOnButton = true;
                                    Debug.Log("Click is on attack button");
                                }
                            }
                        }
                    }
                    if (defendButton != null && !clickedOnButton)
                    {
                        RectTransform buttonRect = defendButton.GetComponent<RectTransform>();
                        if (buttonRect != null)
                        {
                            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                            {
                                // For overlay, check if click is within button's world bounds
                                Vector3[] corners = new Vector3[4];
                                buttonRect.GetWorldCorners(corners);
                                if (clickPos.x >= corners[0].x && clickPos.x <= corners[2].x &&
                                    clickPos.y >= corners[0].y && clickPos.y <= corners[2].y)
                                {
                                    clickedOnButton = true;
                                    Debug.Log("Click is on defend button");
                                }
                            }
                            else
                            {
                                if (RectTransformUtility.RectangleContainsScreenPoint(buttonRect, clickPos, uiCamera))
                                {
                                    clickedOnButton = true;
                                    Debug.Log("Click is on defend button");
                                }
                            }
                        }
                    }
                    
                    // Check if click is on menu background
                    bool clickedOnMenu = false;
                    if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                    {
                        Vector3[] menuCorners = new Vector3[4];
                        menuRect.GetWorldCorners(menuCorners);
                        if (clickPos.x >= menuCorners[0].x && clickPos.x <= menuCorners[2].x &&
                            clickPos.y >= menuCorners[0].y && clickPos.y <= menuCorners[2].y)
                        {
                            clickedOnMenu = true;
                        }
                    }
                    else
                    {
                        clickedOnMenu = RectTransformUtility.RectangleContainsScreenPoint(menuRect, clickPos, uiCamera);
                    }
                    
                    if (!clickedOnButton && !clickedOnMenu)
                    {
                        Debug.Log("Hiding menu - clicked outside");
                        Hide();
                    }
                }
            }
        }
    }
}
