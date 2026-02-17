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

        // Input values
        private bool shootPressed;
        private bool moveForwardPressed;
        private bool rocketPressed;
        private bool minePressed;

        public bool ShootPressed => shootPressed;
        public bool RocketPressed => rocketPressed;
        public bool MinePressed => minePressed;
        /// <summary>True when right mouse is held - move in facing direction</summary>
        public bool MoveForwardPressed => moveForwardPressed;
        public bool IsMobile => Application.isMobilePlatform;

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
        }

        private void OnDisable()
        {
            if (moveAction != null) moveAction.Disable();
            if (shootAction != null) shootAction.Disable();
            if (lookAction != null) lookAction.Disable();
            if (rocketAction != null) rocketAction.Disable();
            if (mineAction != null) mineAction.Disable();
        }

        private void Update()
        {
            // Left-click = shoot
            if (shootAction != null)
            {
                shootPressed = shootAction.IsPressed();
            }
            else
            {
                shootPressed = Mouse.current != null && Mouse.current.leftButton.isPressed;
            }

            // Right-click = move in facing direction
            moveForwardPressed = Mouse.current != null && Mouse.current.rightButton.isPressed;

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

    }
}
