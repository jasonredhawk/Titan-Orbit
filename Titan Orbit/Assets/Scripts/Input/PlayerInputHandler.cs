using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace TitanOrbit.Input
{
    /// <summary>
    /// [UNITY] Cross-platform player input — New Input System actions plus keyboard/mouse fallbacks.
    /// Feeds ShipInputBridge with move, shoot, aim world position, and toggle flags (space brakes, gem expel).
    /// Client only — server has no player input handler.
    ///
    /// [TITAN-ORBIT] Left mouse is both "fire weapon" and "click UI". When the pointer sits over a
    /// raycastable HUD control (e.g. the bottom ship ability upgrade bar), we clear ShootPressed so
    /// buying an upgrade does not also shoot. Paired with ShipInputBridge / ClientLocalBulletVfxBridge,
    /// which both read ShootPressed.
    /// </summary>
    public class PlayerInputHandler : MonoBehaviour
    {
        [Header("Input Settings")]
        /// <summary>InputActionAsset reference — gameplay action map bound in Awake.</summary>
        [SerializeField] private InputActionAsset inputActions;
        
        private InputActionMap gameplayMap;
        private InputAction moveAction;
        private InputAction shootAction;
        private InputAction lookAction;
        private InputAction rocketAction;
        private InputAction mineAction;
        private InputAction cycleBulletAction;

        // Input values
        private bool shootPressed;

        /// <summary>
        /// Reused each frame for UI hit-tests so Update does not allocate a new List.
        /// EventSystem.RaycastAll fills this with every Graphic under the pointer that has raycastTarget.
        /// </summary>
        private readonly List<RaycastResult> _uiRaycastHits = new List<RaycastResult>(8);

        /// <summary>
        /// Reused PointerEventData for the UI raycast. Created lazily once EventSystem exists.
        /// </summary>
        private PointerEventData _uiPointerEventData;
        private bool moveForwardPressed;
        private bool rocketPressed;
        private bool minePressed;
        /// <summary>When true, ship decelerates when not holding move. When false, ship floats (no auto-slow). Toggled by CTRL.</summary>
        private bool spaceBrakesEnabled = true;

        /// <summary>Same as Left Ctrl: toggles whether the ship auto-slows when not thrusting.</summary>
        public void ToggleSpaceBrakes() => spaceBrakesEnabled = !spaceBrakesEnabled;

        public bool ShootPressed => shootPressed;
        public bool RocketPressed => rocketPressed;
        public bool MinePressed => minePressed;
        /// <summary>True while V is held to voluntarily expel carried gems forward at 2 shots/sec.</summary>
        public bool ExpelGemsHeld
        {
            get
            {
                var k = Keyboard.current;
                if (k == null)
                {
                    foreach (var d in InputSystem.devices)
                    {
                        if (d is Keyboard kb) { k = kb; break; }
                    }
                }
                return k != null && k.vKey.isPressed;
            }
        }

        /// <summary>True the frame the player presses B (or CycleBullet action) to cycle bullet prefab.</summary>
        public bool CycleBulletPressed
        {
            get
            {
                if (cycleBulletAction != null && cycleBulletAction.WasPressedThisFrame()) return true;
                var k = Keyboard.current;
                if (k == null)
                {
                    foreach (var d in InputSystem.devices)
                    {
                        if (d is Keyboard kb) { k = kb; break; }
                    }
                }
                return k != null && k.bKey.wasPressedThisFrame;
            }
        }
        /// <summary>True when right mouse is held - move in facing direction</summary>
        public bool MoveForwardPressed => moveForwardPressed;
        /// <summary>True when space brakes are on (ship slows when not holding move). False = float endlessly. Toggle with CTRL.</summary>
        public bool SpaceBrakesEnabled => spaceBrakesEnabled;
        public bool IsMobile => Application.isMobilePlatform;

        /// <summary>WASD / Move action planar direction (x = world X, y = world Z).</summary>
        public Vector2 GetMoveInput()
        {
            // --- Compute value ---
            Vector2 move = Vector2.zero;
            if (moveAction != null)
                move = moveAction.ReadValue<Vector2>();

            var k = Keyboard.current;
            if (k != null)
            {
                if (k.wKey.isPressed || k.upArrowKey.isPressed) move.y += 1f;
                if (k.sKey.isPressed || k.downArrowKey.isPressed) move.y -= 1f;
                if (k.aKey.isPressed || k.leftArrowKey.isPressed) move.x -= 1f;
                if (k.dKey.isPressed || k.rightArrowKey.isPressed) move.x += 1f;
            }

            if (move.sqrMagnitude > 1f)
                move.Normalize();
            return move;
        }

        /// <summary>[UNITY] Enables gameplay action map and caches Move/Shoot/Look actions.</summary>
        private void Awake()
        {
            // --- Unity lifecycle ---
            if (inputActions != null)
            {
                gameplayMap = inputActions.FindActionMap("Gameplay");
                
                if (gameplayMap != null)
                {
                    moveAction = gameplayMap.FindAction("Move");
                    shootAction = gameplayMap.FindAction("Shoot");
                    lookAction = gameplayMap.FindAction("Look");
                    rocketAction = gameplayMap.FindAction("FireRocket");
                    mineAction = gameplayMap.FindAction("PlaceMine");
                    cycleBulletAction = gameplayMap.FindAction("CycleBullet");
                }
            }
        }

        private void OnEnable()
        {
            // --- Unity lifecycle ---
            if (moveAction != null) moveAction.Enable();
            if (shootAction != null) shootAction.Enable();
            if (lookAction != null) lookAction.Enable();
            if (rocketAction != null) rocketAction.Enable();
            if (mineAction != null) mineAction.Enable();
            if (cycleBulletAction != null) cycleBulletAction.Enable();
        }

        private void OnDisable()
        {
            // --- Unity lifecycle ---
            if (moveAction != null) moveAction.Disable();
            if (shootAction != null) shootAction.Disable();
            if (lookAction != null) lookAction.Disable();
            if (rocketAction != null) rocketAction.Disable();
            if (mineAction != null) mineAction.Disable();
            if (cycleBulletAction != null) cycleBulletAction.Disable();
        }

        /// <summary>
        /// Samples shoot / thrust / brakes / rocket / mine every frame.
        /// After raw shoot is computed, mouse-origin fire is cleared when the pointer is over UI
        /// so HUD clicks (ability upgrades, etc.) do not fire the weapon.
        /// </summary>
        private void Update()
        {
            // --- Resolve touch vs desktop input path ---
            // TouchUiActive means MobileInputHandler owns shoot zones; desktop uses LMB / Shoot action.
            MobileInputHandler mobile = MobileInputHandler.Resolve();
            bool useTouchUi = mobile != null && mobile.TouchUiActive;

            if (useTouchUi)
            {
                // --- Touch / forced-touch shoot ---
                // Dedicated on-screen shoot button must keep working even though it is UI.
                // Mouse/action fire (editor hybrid, right-half LMB) still respects the UI gate below.
                bool actionShoot = shootAction != null && shootAction.IsPressed();
                bool editorRightHalfMouseShoot = false;
                if (MobileInputHandler.ForceTouchSteer && Mouse.current != null && Mouse.current.leftButton.isPressed)
                {
                    float edge = Screen.width * mobile.RightScreenSplit;
                    editorRightHalfMouseShoot = Mouse.current.position.ReadValue().x >= edge;
                }
                bool dedicatedShootButton = mobile.ShootButtonPressed;
                shootPressed = dedicatedShootButton || actionShoot || editorRightHalfMouseShoot;

                // Drop mouse/action fire when over raycastable UI; never silence the dedicated shoot button.
                if (shootPressed && !dedicatedShootButton && IsPointerOverUi())
                    shootPressed = false;

                // Phones: thrust only in outer left-drag zone; desktop: legacy on-screen joystick deflection.
                bool anchorThrust = mobile.LeftThrustFromAnchor;
                bool legacyJoyThrust = !Application.isMobilePlatform && !MobileInputHandler.ForceTouchSteer
                    && mobile.JoystickDeflectedBeyondDeadZone();
                bool joyThrust = anchorThrust || legacyJoyThrust;
                moveForwardPressed = joyThrust
                    || (Mouse.current != null && Mouse.current.rightButton.isPressed);
            }
            else
            {
                // --- Desktop shoot (LMB / Shoot action) ---
                // Left mouse is shared with UGUI buttons — see IsPointerOverUi gate after this block.
                if (shootAction != null)
                    shootPressed = shootAction.IsPressed();
                else if (Mouse.current != null)
                    shootPressed = Mouse.current.leftButton.isPressed;
                else
                    shootPressed = false;

                // [TITAN-ORBIT] Upgrade bar / any raycastTarget Graphic under the cursor blocks fire.
                if (shootPressed && IsPointerOverUi())
                    shootPressed = false;

                moveForwardPressed = Mouse.current != null && Mouse.current.rightButton.isPressed;
            }

            // --- Space brakes toggle (Left Ctrl) ---
            // When on: ship slows when not holding move; when off: ship floats.
            if (Keyboard.current != null && Keyboard.current.leftCtrlKey.wasPressedThisFrame)
                spaceBrakesEnabled = !spaceBrakesEnabled;

            // --- Optional secondary actions ---
            // FireRocket / PlaceMine from the action map; Starship may also use Q / E fallbacks elsewhere.
            rocketPressed = rocketAction != null && rocketAction.IsPressed();
            minePressed = mineAction != null && mineAction.IsPressed();
        }

        /// <summary>
        /// True when the mouse pointer is over a UGUI element that accepts raycasts
        /// (Button Image with raycastTarget, etc.).
        ///
        /// Used to share left-click between gameplay fire and HUD without firing through buttons.
        /// Uses EventSystem.RaycastAll at the mouse position — more reliable with the New Input System
        /// than the no-arg IsPointerOverGameObject() overload (which often returns false incorrectly).
        /// </summary>
        /// <returns>True if at least one UI graphic is under the mouse; false if no mouse, no EventSystem, or clear sky.</returns>
        private bool IsPointerOverUi()
        {
            // --- Guards ---
            // EventSystem — Unity's UI hit-test hub (created by scene or EnsureEventSystem helpers).
            var eventSystem = EventSystem.current;
            if (eventSystem == null || Mouse.current == null)
                return false;

            // --- Build pointer sample at current mouse screen position ---
            // PointerEventData — the payload EventSystem raycasts expect (screen pos in pixels).
            if (_uiPointerEventData == null)
                _uiPointerEventData = new PointerEventData(eventSystem);
            else
                _uiPointerEventData.Reset();

            _uiPointerEventData.position = Mouse.current.position.ReadValue();

            // --- Raycast all UI under the pointer ---
            // Any hit means a Graphic with raycastTarget=true is under the cursor (upgrade buttons, etc.).
            _uiRaycastHits.Clear();
            eventSystem.RaycastAll(_uiPointerEventData, _uiRaycastHits);
            return _uiRaycastHits.Count > 0;
        }

        /// <summary>
        /// Get mouse cursor world position (for ship rotation - ship faces toward cursor)
        /// </summary>
        public Vector3 GetMouseWorldPosition(UnityEngine.Camera cam)
        {
            // --- Compute value ---
            if (cam == null) return transform.position;

            MobileInputHandler mobile = MobileInputHandler.Resolve();
            bool useTouchUi = mobile != null && mobile.TouchUiActive;

            if (useTouchUi)
            {
                if (mobile.TryGetLeftDragAimWorldPoint(cam, transform, out Vector3 leftAim))
                    return leftAim;

                if (mobile.TryGetAimScreenPosition(cam, out Vector2 aimScreen))
                {
                    Ray aimRay = cam.ScreenPointToRay(new Vector3(aimScreen.x, aimScreen.y, 0f));
                    Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
                    if (groundPlane.Raycast(aimRay, out float aimDist))
                        return aimRay.GetPoint(aimDist);
                }

                if (mobile.JoystickDeflectedBeyondDeadZone())
                {
                    Vector2 joy = mobile.JoystickInput;
                    Vector3 f = cam.transform.forward;
                    f.y = 0f;
                    if (f.sqrMagnitude < 0.0001f)
                        f = Vector3.forward;
                    else
                        f.Normalize();
                    Vector3 r = cam.transform.right;
                    r.y = 0f;
                    r.Normalize();
                    Vector3 flat = (r * joy.x + f * joy.y).normalized;
                    return transform.position + flat * 10f;
                }

                // Editor / hybrid: keep mouse aim when touch HUD is forced on but pointer is mouse.
                if (Mouse.current != null)
                {
                    Vector2 hybridMouse = Mouse.current.position.ReadValue();
                    Ray hybridRay = cam.ScreenPointToRay(new Vector3(hybridMouse.x, hybridMouse.y, 0f));
                    Plane hybridPlane = new Plane(Vector3.up, Vector3.zero);
                    if (hybridPlane.Raycast(hybridRay, out float hybridDist))
                        return hybridRay.GetPoint(hybridDist);
                }

                return transform.position;
            }

            if (Mouse.current == null) return transform.position;

            Vector2 mousePos = Mouse.current.position.ReadValue();
            Ray ray = cam.ScreenPointToRay(new Vector3(mousePos.x, mousePos.y, 0));
            Plane plane = new Plane(Vector3.up, Vector3.zero);
            if (plane.Raycast(ray, out float distance))
                return ray.GetPoint(distance);
            return transform.position;
        }

    }
}
