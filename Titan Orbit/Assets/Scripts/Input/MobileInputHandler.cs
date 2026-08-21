using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace TitanOrbit.Input
{
    /// <summary>
    /// Mobile touch input: left-half anchor steering (rotate / thrust zones) + fire on the right
    /// screen half (or optional shoot rect). Feeds <see cref="PlayerInputHandler"/> and
    /// <see cref="Game.ShipInputBridge"/> with joystick vector and shoot hold. Legacy on-screen
    /// joystick visuals are optional (editor / fallback). Client-only — not used on dedicated server.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class MobileInputHandler : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler
    {
        public static MobileInputHandler Instance { get; private set; }
        /// <summary>When true, anchor steering runs in the editor (see <see cref="TitanOrbit.UI.MobileControls"/> Force Mobile).</summary>
        public static bool ForceTouchSteer { get; private set; }

        public static void SetForceTouchSteer(bool value) => ForceTouchSteer = value;

        [Header("References")]
        [Tooltip("Optional; if set, only this rect counts as fire instead of the whole right half.")]
        [SerializeField] private RectTransform shootButtonArea;
        [Tooltip("When true and Shoot Button Area is null, any touch on the right half (past Left Screen Portion) fires.")]
        [SerializeField] private bool useRightHalfScreenForShoot = true;
        [Tooltip("Rects that block firing (e.g. air-brake buttons on the right).")]
        [SerializeField] private RectTransform[] shootZoneExclusions;
        [SerializeField] private RectTransform joystickBackground;
        [SerializeField] private RectTransform joystickHandle;
        [SerializeField] private float joystickRadius = 72f;

        [Header("Left-half anchor steering")]
        [Tooltip("Fraction of screen width [0,1] treated as the left steering half (not the fire side).")]
        [SerializeField, Range(0.25f, 0.85f)] private float leftScreenPortion = 0.5f;
        [Tooltip("Ignore tiny jitter from anchor in pixels (no rotate until exceeded).")]
        [SerializeField] private float microDragDeadzonePixels = 10f;
        [Tooltip("Distance from anchor (px) at or beyond which thrust engages (inner band is rotate-only).")]
        [SerializeField] private float thrustZoneMinPixels = 168f;

        private Vector2 joystickInput;
        private bool isJoystickActive;
        private bool shootButtonHeld;
        /// <summary>Hold state from <see cref="TitanOrbit.UI.MobileShootHoldRelay"/>; merged with touch-in-rect so rect tests cannot drop fire.</summary>
        private bool relayShootHeld;
        private bool touchUiActive;
        private Canvas rootCanvas;

        private int anchorFingerId = -1;
        private Vector2 anchorScreen;
        private Vector2 leftDragDeltaScreen;
        private float leftDragDistancePixels;
        private bool leftAnchorActive;

        private static readonly List<Vector2> s_scratchScreenPoints = new List<Vector2>(8);

        public Vector2 JoystickInput => joystickInput;
        public bool ShootButtonPressed => shootButtonHeld;
        public bool TouchUiActive => touchUiActive;
        /// <summary>Same as left screen portion: x &gt;= Screen.width * this is the right (fire) side.</summary>
        public float RightScreenSplit => leftScreenPortion;

        public bool LeftAnchorActive => leftAnchorActive;
        public Vector2 LeftDragDeltaScreen => leftDragDeltaScreen;
        public float LeftDragDistancePixels => leftDragDistancePixels;
        /// <summary>True when drag is far enough to apply thrust (outer zone).</summary>
        public bool LeftThrustFromAnchor => leftAnchorActive && leftDragDistancePixels >= thrustZoneMinPixels;
        /// <summary>True when drag is enough to aim / rotate (past micro deadzone).</summary>
        public bool LeftRotationFromAnchor => leftAnchorActive && leftDragDistancePixels >= microDragDeadzonePixels;

        /// <summary>Screen-space anchor for steer UI (pixels, bottom-left origin).</summary>
        public Vector2 SteerAnchorScreenPx => anchorScreen;
        /// <summary>Current finger position in screen pixels (anchor + drag).</summary>
        public Vector2 SteerFingerScreenPx => anchorScreen + leftDragDeltaScreen;
        /// <summary>Radius in pixels at which thrust engages; use for UI ring.</summary>
        public float SteerThrustRingRadiusPx => thrustZoneMinPixels;
        /// <summary>Micro deadzone radius in pixels for rotation start.</summary>
        public float SteerMicroDeadzonePx => microDragDeadzonePixels;

        public static MobileInputHandler Resolve()
        {
            // --- Resolve value ---
            if (Instance != null && Instance.isActiveAndEnabled && Instance.touchUiActive)
                return Instance;
            var handlers = Object.FindObjectsByType<MobileInputHandler>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < handlers.Length; i++)
            {
                MobileInputHandler h = handlers[i];
                if (h != null && h.isActiveAndEnabled && h.touchUiActive)
                    return h;
            }
            return null;
        }

        private void Awake()
        {
            RegisterSingleton();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        /// <summary>Wire from <see cref="TitanOrbit.UI.MobileControls"/>. Shoot rect optional when using right-half firing.</summary>
        public void Initialize(RectTransform shootRect, RectTransform joyBackground, RectTransform joyHandle, float legacyJoystickRadius, RectTransform[] shootExclusions = null)
        {
            // --- Initialize ---
            shootButtonArea = shootRect;
            if (shootExclusions != null)
                shootZoneExclusions = shootExclusions;
            joystickBackground = joyBackground;
            joystickHandle = joyHandle;
            if (legacyJoystickRadius > 1f)
                joystickRadius = legacyJoystickRadius;
            touchUiActive = true;
            if (shootButtonArea != null)
                rootCanvas = shootButtonArea.GetComponentInParent<Canvas>();
            else if (joystickBackground != null)
                rootCanvas = joystickBackground.GetComponentInParent<Canvas>();
            RegisterSingleton();
        }

        /// <summary>Assign canvas root used for camera resolution when no shoot rect (e.g. mobile button canvas only).</summary>
        public void SetRootCanvasForUiTests(Canvas canvas)
        {
            if (canvas != null)
                rootCanvas = canvas;
        }

        private void RegisterSingleton()
        {
            // --- RegisterSingleton ---
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        public void SetShootHeld(bool held) => relayShootHeld = held;

        private UnityEngine.Camera GetUiEventCamera(PointerEventData eventData)
        {
            // --- Compute value ---
            if (eventData != null && eventData.pressEventCamera != null)
                return eventData.pressEventCamera;
            if (eventData != null && eventData.enterEventCamera != null)
                return eventData.enterEventCamera;
            return GetUiCameraForRectTests();
        }

        private UnityEngine.Camera GetUiCameraForRectTests()
        {
            // --- Compute value ---
            if (rootCanvas == null && shootButtonArea != null)
                rootCanvas = shootButtonArea.GetComponentInParent<Canvas>();
            if (rootCanvas == null && joystickBackground != null)
                rootCanvas = joystickBackground.GetComponentInParent<Canvas>();
            if (rootCanvas == null)
                return null;

            if (rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay)
                return null;
            if (rootCanvas.renderMode == RenderMode.ScreenSpaceCamera || rootCanvas.renderMode == RenderMode.WorldSpace)
                return rootCanvas.worldCamera != null ? rootCanvas.worldCamera : UnityEngine.Camera.main;
            return null;
        }

        private bool ScreenPointInRect(RectTransform rect, Vector2 screenPoint)
        {
            // --- ScreenPointInRect ---
            if (rect == null) return false;
            UnityEngine.Camera cam = GetUiCameraForRectTests();
            if (RectTransformUtility.RectangleContainsScreenPoint(rect, screenPoint, null))
                return true;
            if (cam != null && RectTransformUtility.RectangleContainsScreenPoint(rect, screenPoint, cam))
                return true;
            return false;
        }

        private bool IsInShootExclusion(Vector2 screenPoint)
        {
            // --- IsInShootExclusion ---
            if (shootZoneExclusions == null) return false;
            for (int i = 0; i < shootZoneExclusions.Length; i++)
            {
                if (shootZoneExclusions[i] != null && ScreenPointInRect(shootZoneExclusions[i], screenPoint))
                    return true;
            }
            return false;
        }

        /// <summary>True when this screen position should count as hold-to-fire (right half or optional shoot rect).</summary>
        private bool IsInShootFireZone(Vector2 screenPoint)
        {
            // --- IsInShootFireZone ---
            if (IsInShootExclusion(screenPoint))
                return false;
            if (shootButtonArea != null)
                return ScreenPointInRect(shootButtonArea, screenPoint);
            return useRightHalfScreenForShoot && screenPoint.x >= Screen.width * leftScreenPortion;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            // --- OnPointerDown ---
            if (joystickBackground == null) return;
            isJoystickActive = true;
            UpdateJoystick(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (isJoystickActive)
                UpdateJoystick(eventData);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            // --- OnPointerUp ---
            if (!isJoystickActive) return;
            isJoystickActive = false;
            if (!Application.isMobilePlatform && !ForceTouchSteer)
                ClearJoystickVisual();
        }

        private void Update()
        {
            // --- Per-frame refresh ---
            if (!touchUiActive)
                return;
            if (!(Application.isMobilePlatform || ForceTouchSteer))
                return;

            ApplyPhysicalTouchState();
        }

        private void ApplyPhysicalTouchState()
        {
            GatherActiveTouchesDetailed();
        }

        private struct TouchInfo
        {
            public int fingerId;
            public Vector2 position;
            public UnityEngine.TouchPhase phase;
        }

        private static readonly List<TouchInfo> s_touches = new List<TouchInfo>(8);

        private void GatherActiveTouchesDetailed()
        {
            // --- GatherActiveTouchesDetailed ---
            s_touches.Clear();
            int legacy = UnityEngine.Input.touchCount;
            for (int i = 0; i < legacy; i++)
            {
                UnityEngine.Touch t = UnityEngine.Input.GetTouch(i);
                s_touches.Add(new TouchInfo
                {
                    fingerId = t.fingerId,
                    position = t.position,
                    phase = t.phase
                });
            }

            if (s_touches.Count == 0 && Touchscreen.current != null)
            {
                Touchscreen ts = Touchscreen.current;
                var touches = ts.touches;
                for (int i = 0; i < touches.Count; i++)
                {
                    var touch = touches[i];
                    bool pressed = touch.press.isPressed;
                    bool releasedThisFrame = touch.press.wasReleasedThisFrame;
                    if (!pressed && !releasedThisFrame) continue;
                    int fid = 1000 + i;
                    UnityEngine.TouchPhase phase;
                    if (releasedThisFrame && !pressed)
                        phase = UnityEngine.TouchPhase.Ended;
                    else if (touch.press.wasPressedThisFrame)
                        phase = UnityEngine.TouchPhase.Began;
                    else
                        phase = UnityEngine.TouchPhase.Moved;

                    s_touches.Add(new TouchInfo
                    {
                        fingerId = fid,
                        position = touch.position.ReadValue(),
                        phase = phase
                    });
                }
                if (s_touches.Count == 0)
                {
                    var pt = ts.primaryTouch;
                    bool pPressed = pt.press.isPressed;
                    bool pReleased = pt.press.wasReleasedThisFrame;
                    if (pPressed || pReleased)
                    {
                        UnityEngine.TouchPhase pPhase;
                        if (pReleased && !pPressed)
                            pPhase = UnityEngine.TouchPhase.Ended;
                        else if (pt.press.wasPressedThisFrame)
                            pPhase = UnityEngine.TouchPhase.Began;
                        else
                            pPhase = UnityEngine.TouchPhase.Moved;
                        s_touches.Add(new TouchInfo
                        {
                            fingerId = 9999,
                            position = pt.position.ReadValue(),
                            phase = pPhase
                        });
                    }
                }
            }

            float leftEdge = Screen.width * leftScreenPortion;
            bool shoot = false;

            for (int i = 0; i < s_touches.Count; i++)
            {
                if (IsInShootFireZone(s_touches[i].position))
                    shoot = true;
            }
            if (ForceTouchSteer && Mouse.current != null && Mouse.current.leftButton.isPressed)
            {
                Vector2 mp = Mouse.current.position.ReadValue();
                if (IsInShootFireZone(mp))
                    shoot = true;
            }
            shootButtonHeld = shoot || relayShootHeld;

            bool sawAnchorFinger = false;
            if (anchorFingerId >= 0)
            {
                for (int i = 0; i < s_touches.Count; i++)
                {
                    if (s_touches[i].fingerId != anchorFingerId) continue;
                    var ti = s_touches[i];
                    if (ti.phase == UnityEngine.TouchPhase.Ended || ti.phase == UnityEngine.TouchPhase.Canceled)
                    {
                        ClearLeftAnchor();
                        break;
                    }
                    sawAnchorFinger = true;
                    leftDragDeltaScreen = ti.position - anchorScreen;
                    leftDragDistancePixels = leftDragDeltaScreen.magnitude;
                    UpdateLegacyJoystickVisualFromDelta();
                    break;
                }
                if (anchorFingerId >= 0 && !sawAnchorFinger)
                    ClearLeftAnchor();
            }

            if (anchorFingerId < 0)
            {
                for (int i = 0; i < s_touches.Count; i++)
                {
                    var ti = s_touches[i];
                    if (ti.phase != UnityEngine.TouchPhase.Began)
                        continue;
                    if (IsInShootExclusion(ti.position))
                        continue;
                    if (shootButtonArea != null && ScreenPointInRect(shootButtonArea, ti.position))
                        continue;
                    if (useRightHalfScreenForShoot && shootButtonArea == null && ti.position.x >= leftEdge)
                        continue;
                    anchorFingerId = ti.fingerId;
                    anchorScreen = ti.position;
                    leftAnchorActive = true;
                    leftDragDeltaScreen = Vector2.zero;
                    leftDragDistancePixels = 0f;
                    break;
                }
            }

            if (!leftAnchorActive)
            {
                leftDragDeltaScreen = Vector2.zero;
                leftDragDistancePixels = 0f;
            }

            if (!leftAnchorActive && joystickBackground == null)
            {
                joystickInput = Vector2.zero;
                return;
            }

            if (leftAnchorActive && LeftRotationFromAnchor)
            {
                Vector2 n = leftDragDeltaScreen.normalized;
                joystickInput = new Vector2(
                    Mathf.Clamp(n.x, -1f, 1f),
                    Mathf.Clamp(n.y, -1f, 1f));
            }
            else if (joystickBackground != null)
            {
                ProcessLegacyJoystickFromScratchPoints();
            }
            else
            {
                joystickInput = Vector2.zero;
            }

            if (!leftAnchorActive && s_touches.Count == 0)
            {
                ClearJoystickVisual();
                isJoystickActive = false;
            }
        }

        private void ClearLeftAnchor()
        {
            // --- Clear state ---
            anchorFingerId = -1;
            leftAnchorActive = false;
            leftDragDeltaScreen = Vector2.zero;
            leftDragDistancePixels = 0f;
            if (joystickHandle != null)
                joystickHandle.anchoredPosition = Vector2.zero;
        }

        private void UpdateLegacyJoystickVisualFromDelta()
        {
            // --- Per-frame refresh ---
            if (joystickHandle == null)
                return;
            float r = Mathf.Min(leftDragDistancePixels, joystickRadius);
            Vector2 dir = leftDragDistancePixels > 0.01f ? leftDragDeltaScreen.normalized : Vector2.zero;
            joystickHandle.anchoredPosition = dir * r;
        }

        private void ProcessLegacyJoystickFromScratchPoints()
        {
            // --- ProcessLegacyJoystickFromScratchPoints ---
            UnityEngine.Camera uiCam = GetUiCameraForRectTests();
            bool joy = false;
            Vector2 joyNorm = Vector2.zero;
            Vector2 joyAnchored = Vector2.zero;

            s_scratchScreenPoints.Clear();
            for (int i = 0; i < s_touches.Count; i++)
            {
                if (s_touches[i].phase == UnityEngine.TouchPhase.Ended || s_touches[i].phase == UnityEngine.TouchPhase.Canceled)
                    continue;
                s_scratchScreenPoints.Add(s_touches[i].position);
            }

            for (int i = 0; i < s_scratchScreenPoints.Count; i++)
            {
                Vector2 sp = s_scratchScreenPoints[i];
                if (IsInShootFireZone(sp))
                    continue;
                if (joystickBackground != null && ScreenPointInRect(joystickBackground, sp))
                {
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        joystickBackground, sp, uiCam, out Vector2 localPoint);
                    Vector2 clamped = Vector2.ClampMagnitude(localPoint, joystickRadius);
                    joyNorm = clamped / joystickRadius;
                    joyAnchored = clamped;
                    joy = true;
                }
            }

            if (joy)
            {
                joystickInput = joyNorm;
                if (joystickHandle != null)
                    joystickHandle.anchoredPosition = joyAnchored;
            }
            else if (!leftAnchorActive)
            {
                // --- if ---
                joystickInput = Vector2.zero;
                if (joystickHandle != null)
                    joystickHandle.anchoredPosition = Vector2.zero;
            }
        }

        private void ClearJoystickVisual()
        {
            // --- Clear state ---
            joystickInput = Vector2.zero;
            if (joystickHandle != null)
                joystickHandle.anchoredPosition = Vector2.zero;
        }

        private void UpdateJoystick(PointerEventData eventData)
        {
            // --- Per-frame refresh ---
            if (joystickBackground == null || joystickHandle == null) return;

            UnityEngine.Camera uiCam = GetUiEventCamera(eventData);

            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                joystickBackground,
                eventData.position,
                uiCam,
                out Vector2 localPoint);

            Vector2 clamped = Vector2.ClampMagnitude(localPoint, joystickRadius);
            joystickInput = clamped / joystickRadius;

            joystickHandle.anchoredPosition = clamped;
        }

        /// <summary>World point used to aim the ship from left drag (camera-relative on the tangent).</summary>
        public bool TryGetLeftDragAimWorldPoint(UnityEngine.Camera cam, Transform ship, out Vector3 worldPoint)
        {
            // --- Attempt resolution ---
            worldPoint = ship != null ? ship.position : Vector3.zero;
            if (!LeftRotationFromAnchor || cam == null || ship == null)
                return false;

            Vector2 d = leftDragDeltaScreen.normalized;
            if (d.sqrMagnitude < 0.0001f)
                return false;

            Vector3 up = ship.position.sqrMagnitude > 0.01f ? ship.position.normalized : Vector3.up;
            Vector3 f = Vector3.ProjectOnPlane(cam.transform.up, up);
            if (f.sqrMagnitude < 0.0001f)
                f = Vector3.ProjectOnPlane(cam.transform.forward, up);
            if (f.sqrMagnitude < 0.0001f)
                f = cam.transform.up;
            f.Normalize();
            Vector3 r = Vector3.ProjectOnPlane(cam.transform.right, up);
            if (r.sqrMagnitude < 0.0001f)
                r = Vector3.Cross(up, f);
            r.Normalize();
            Vector3 flat = (r * d.x + f * d.y).normalized;
            worldPoint = ship.position + flat * 10f;
            return true;
        }

        public bool TryGetAimScreenPosition(UnityEngine.Camera gameCamera, out Vector2 screenPosition)
        {
            // --- Attempt resolution ---
            screenPosition = default;
            if (!touchUiActive)
                return false;

            UnityEngine.Camera uiCam = GetUiCameraForRectTests();

            if (TryPickAimFromLegacyTouches(uiCam, out screenPosition))
                return true;
            return TryPickAimFromInputSystemTouches(uiCam, out screenPosition);
        }

        private bool TryPickAimFromLegacyTouches(UnityEngine.Camera uiCam, out Vector2 screenPosition)
        {
            // --- Attempt resolution ---
            screenPosition = default;
            int count = UnityEngine.Input.touchCount;
            for (int i = 0; i < count; i++)
            {
                UnityEngine.Touch t = UnityEngine.Input.GetTouch(i);
                if (t.phase == UnityEngine.TouchPhase.Ended || t.phase == UnityEngine.TouchPhase.Canceled)
                    continue;
                Vector2 pos = t.position;
                if (t.fingerId == anchorFingerId)
                    continue;
                if (IsInShootFireZone(pos))
                    continue;
                if (IsOnJoystick(pos, uiCam))
                    continue;
                float leftEdgeAim = Screen.width * leftScreenPortion;
                if (pos.x < leftEdgeAim)
                    continue;
                screenPosition = pos;
                return true;
            }
            return false;
        }

        private bool TryPickAimFromInputSystemTouches(UnityEngine.Camera uiCam, out Vector2 screenPosition)
        {
            // --- Attempt resolution ---
            screenPosition = default;
            Touchscreen ts = Touchscreen.current;
            if (ts == null)
                return false;

            float leftEdge = Screen.width * leftScreenPortion;
            var touches = ts.touches;
            for (int i = 0; i < touches.Count; i++)
            {
                var touch = touches[i];
                if (!touch.press.isPressed)
                    continue;
                Vector2 pos = touch.position.ReadValue();
                if (IsInShootFireZone(pos))
                    continue;
                if (IsOnJoystick(pos, uiCam))
                    continue;
                if (pos.x < leftEdge)
                    continue;
                screenPosition = pos;
                return true;
            }

            if (ts.primaryTouch.press.isPressed)
            {
                Vector2 pos = ts.primaryTouch.position.ReadValue();
                if (!IsOnJoystick(pos, uiCam) && !IsInShootFireZone(pos) && pos.x >= leftEdge)
                {
                    screenPosition = pos;
                    return true;
                }
            }

            return false;
        }

        public bool JoystickDeflectedBeyondDeadZone()
        {
            // --- JoystickDeflectedBeyondDeadZone ---
            if (leftAnchorActive && LeftRotationFromAnchor)
                return true;
            float dz = 0.12f;
            return joystickInput.sqrMagnitude >= dz * dz;
        }

        private bool IsOnJoystick(Vector2 screenPos, UnityEngine.Camera uiCam)
        {
            return joystickBackground != null && RectHitFlexible(joystickBackground, screenPos, uiCam);
        }

        private bool RectHitFlexible(RectTransform rect, Vector2 screenPos, UnityEngine.Camera uiCam)
        {
            // --- RectHitFlexible ---
            if (RectTransformUtility.RectangleContainsScreenPoint(rect, screenPos, null))
                return true;
            if (uiCam != null && RectTransformUtility.RectangleContainsScreenPoint(rect, screenPos, uiCam))
                return true;
            return false;
        }
    }
}
