using UnityEngine;
using UnityEngine.InputSystem;

namespace TitanOrbit.Input
{
    /// <summary>
    /// Handles player input abstraction for cross-platform support
    /// </summary>
    public class PlayerInputHandler : MonoBehaviour
    {
        [Header("Input Settings")]
        [SerializeField] private InputActionAsset inputActions;
        
        private InputActionMap gameplayMap;
        private InputAction moveAction;
        private InputAction shootAction;
        private InputAction lookAction;
        private InputAction rocketAction;
        private InputAction mineAction;
        private InputAction cycleBulletAction;
        private MobileInputHandler mobileInputHandler;

        // Input values
        private bool shootPressed;
        private bool moveForwardPressed;
        private Vector2 moveInput;
        private bool rocketPressed;
        private bool minePressed;
        /// <summary>When true, ship decelerates when not holding move. When false, ship floats (no auto-slow). Toggled by CTRL.</summary>
        private bool spaceBrakesEnabled = true;

        public bool ShootPressed => shootPressed;
        public bool RocketPressed => rocketPressed;
        public bool MinePressed => minePressed;
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
        /// <summary>Normalized planar move input. X = world right, Y = world forward.</summary>
        public Vector2 MoveInput => moveInput;
        /// <summary>True when space brakes are on (ship slows when not holding move). False = float endlessly. Toggle with CTRL.</summary>
        public bool SpaceBrakesEnabled => spaceBrakesEnabled;
        public bool IsMobile => Application.isMobilePlatform;
        public bool IsUsingMobileControls => mobileInputHandler != null && (Application.isMobilePlatform || UnityEngine.Input.touchSupported);
        public bool IsShootingFromMobileButton => IsUsingMobileControls && mobileInputHandler.ShootButtonPressed;

        private void Awake()
        {
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
            if (moveAction != null) moveAction.Enable();
            if (shootAction != null) shootAction.Enable();
            if (lookAction != null) lookAction.Enable();
            if (rocketAction != null) rocketAction.Enable();
            if (mineAction != null) mineAction.Enable();
            if (cycleBulletAction != null) cycleBulletAction.Enable();
        }

        private void OnDisable()
        {
            if (moveAction != null) moveAction.Disable();
            if (shootAction != null) shootAction.Disable();
            if (lookAction != null) lookAction.Disable();
            if (rocketAction != null) rocketAction.Disable();
            if (mineAction != null) mineAction.Disable();
            if (cycleBulletAction != null) cycleBulletAction.Disable();
        }

        private void Update()
        {
            if (mobileInputHandler == null)
            {
                mobileInputHandler = FindFirstObjectByType<MobileInputHandler>();
            }

            // Left-click = shoot (prefer new Input System; no legacy Input when Input System package is active)
            if (shootAction != null)
                shootPressed = shootAction.IsPressed();
            else if (Mouse.current != null)
                shootPressed = Mouse.current.leftButton.isPressed;
            else
                shootPressed = false;

            // Keyboard/gamepad movement from Input System "Move" action (if bound)
            moveInput = moveAction != null ? moveAction.ReadValue<Vector2>() : Vector2.zero;
            moveForwardPressed = moveInput.sqrMagnitude > 0.001f;

            // Right-click = move in facing direction (fallback desktop behavior)
            if (!moveForwardPressed)
            {
                moveForwardPressed = Mouse.current != null && Mouse.current.rightButton.isPressed;
            }

            if (mobileInputHandler != null && mobileInputHandler.isActiveAndEnabled)
            {
                Vector2 joystick = mobileInputHandler.JoystickInput;
                if (joystick.sqrMagnitude > 0.001f)
                {
                    moveInput = joystick;
                    moveForwardPressed = true;
                }
                else if (UnityEngine.Input.touchCount > 0)
                {
                    moveForwardPressed = false;
                    moveInput = Vector2.zero;
                }

                shootPressed = shootPressed || mobileInputHandler.ShootButtonPressed;
            }

            // CTRL toggles space brakes (when on: ship slows when not holding move; when off: ship floats)
            if (Keyboard.current != null && Keyboard.current.leftCtrlKey.wasPressedThisFrame)
                spaceBrakesEnabled = !spaceBrakesEnabled;

            // Optional: FireRocket / PlaceMine actions; fallback is Q / E in Starship
            rocketPressed = rocketAction != null && rocketAction.IsPressed();
            minePressed = mineAction != null && mineAction.IsPressed();
        }

        /// <summary>
        /// Get mouse cursor world position (for ship rotation - ship faces toward cursor)
        /// </summary>
        public Vector3 GetMouseWorldPosition(UnityEngine.Camera cam)
        {
            if (cam == null || Mouse.current == null) return transform.position;

            Vector2 mousePos = Mouse.current.position.ReadValue();
            Ray ray = cam.ScreenPointToRay(new Vector3(mousePos.x, mousePos.y, 0));
            Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
            if (groundPlane.Raycast(ray, out float distance))
            {
                return ray.GetPoint(distance);
            }
            return transform.position;
        }

        /// <summary>Returns desired movement direction in world space.</summary>
        public Vector3 GetDesiredMoveDirection(Transform actorTransform)
        {
            if (actorTransform == null) return Vector3.zero;
            if (moveInput.sqrMagnitude > 0.001f)
            {
                Vector3 worldDir = new Vector3(moveInput.x, 0f, moveInput.y);
                if (worldDir.sqrMagnitude > 0.001f)
                    return worldDir.normalized;
            }

            if (moveForwardPressed)
            {
                Vector3 fallbackForward = actorTransform.forward;
                fallbackForward.y = 0f;
                if (fallbackForward.sqrMagnitude > 0.001f)
                    return fallbackForward.normalized;
            }
            return Vector3.zero;
        }

        /// <summary>Returns look direction from touch joystick when available, else from mouse cursor.</summary>
        public bool TryGetLookDirection(UnityEngine.Camera cam, Transform actorTransform, out Vector3 direction)
        {
            direction = Vector3.zero;

            if (moveInput.sqrMagnitude > 0.001f)
            {
                direction = new Vector3(moveInput.x, 0f, moveInput.y);
                if (direction.sqrMagnitude > 0.001f)
                {
                    direction.Normalize();
                    return true;
                }
            }

            if (cam == null || actorTransform == null || Mouse.current == null)
                return false;

            Vector3 mouseWorldPos = GetMouseWorldPosition(cam);
            direction = mouseWorldPos - actorTransform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.001f)
                return false;

            direction.Normalize();
            return true;
        }

    }
}
