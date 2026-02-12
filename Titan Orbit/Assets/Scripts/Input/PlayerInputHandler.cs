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

        // Input values
        private Vector2 moveInput;
        private Vector2 lookInput;
        private bool shootPressed;

        public Vector2 MoveInput => moveInput;
        public Vector2 LookInput => lookInput;
        public bool ShootPressed => shootPressed;
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
                }
            }
        }

        private void OnEnable()
        {
            if (moveAction != null) moveAction.Enable();
            if (shootAction != null) shootAction.Enable();
            if (lookAction != null) lookAction.Enable();
        }

        private void OnDisable()
        {
            if (moveAction != null) moveAction.Disable();
            if (shootAction != null) shootAction.Disable();
            if (lookAction != null) lookAction.Disable();
        }

        private void Update()
        {
            // Read input values
            if (moveAction != null)
            {
                moveInput = moveAction.ReadValue<Vector2>();
            }

            if (lookAction != null)
            {
                lookInput = lookAction.ReadValue<Vector2>();
            }

            if (shootAction != null)
            {
                shootPressed = shootAction.IsPressed();
            }
        }

        /// <summary>
        /// Get world position for movement (for mouse/touch input)
        /// </summary>
        public Vector3 GetMoveWorldPosition(UnityEngine.Camera cam)
        {
            if (cam == null) return Vector3.zero;

            Vector3 screenPos = new Vector3(lookInput.x, lookInput.y, cam.nearClipPlane);
            return cam.ScreenToWorldPoint(screenPos);
        }

        /// <summary>
        /// Get direction vector for movement
        /// </summary>
        public Vector2 GetMoveDirection()
        {
            return moveInput.normalized;
        }
    }
}
