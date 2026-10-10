using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace TitanOrbit.Input
{
    /// <summary>
    /// [UNITY] Cross-platform player input — New Input System actions plus keyboard/mouse fallbacks.
    /// Feeds ShipInputBridge with move, shoot, aim world position, Q (focused
    /// rocket or mine), and toggle flags (space brakes, gem expel). Hold-C is
    /// sampled as <see cref="CommsHeld"/> for the keyword comms matrix.
    /// SPACE holds fire the same way left mouse does.
    /// Client only — server has no player input handler.
    ///
    /// [TITAN-ORBIT] Left mouse is both "fire weapon" and "click UI". When the pointer sits over a
    /// raycastable HUD control (e.g. the bottom ship ability upgrade bar), we clear ShootPressed so
    /// buying an upgrade does not also shoot. Paired with ShipInputBridge / ClientLocalBulletVfxBridge,
    /// which both read ShootPressed.
    /// <para>
    /// Gameplay always unprojects the cursor so a still mouse still turns the hull.
    /// Theatrical orbit freezes aim in <see cref="TitanOrbit.Game.ShipInputBridge"/>.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(-10050)]
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
        private InputAction cycleBulletAction;

        // Input values
        private bool shootPressed;

        /// <summary>
        /// Reused each frame for UI hit-tests so Update does not allocate a new List.
        /// EventSystem.RaycastAll fills this with every Graphic under the pointer that has raycastTarget.
        /// </summary>
        private readonly List<RaycastResult> _uiRaycastHits = new List<RaycastResult>(8);
        bool _fireHeld;
        bool _fireStartedOverUi;

        /// <summary>
        /// Reused PointerEventData for the UI raycast. Created lazily once EventSystem exists.
        /// </summary>
        private PointerEventData _uiPointerEventData;
        private bool moveForwardPressed;
        private bool rocketPressed;
        /// <summary>When true, ship decelerates when not holding move. When false, ship floats (no auto-slow). Toggled by CTRL.</summary>
        private bool spaceBrakesEnabled = true;
        /// <summary>Shift held this frame — OVERDRIVE intent (latch); burst also needs RMB thrust.</summary>
        private bool overdriveHeld;

        /// <summary>Same as Left Ctrl: toggles whether the ship auto-slows when not thrusting.</summary>
        public void ToggleSpaceBrakes() => spaceBrakesEnabled = !spaceBrakesEnabled;

        public bool ShootPressed => shootPressed;
        /// <summary>
        /// True the frame Q is pressed. <c>ShipInputBridge</c> fires the focused
        /// rocket pack, or places the focused mine pack. One-shot — holding Q
        /// does not dump the pack. Up/Down on the loadout HUD moves that focus.
        /// </summary>
        public bool RocketPressed => rocketPressed;
        /// <summary>
        /// True while V is held to dump cargo as world gems. Server pulses every
        /// <c>GemEconomyConstants.VoluntaryGemExpelIntervalSeconds</c> (0.5s = 2 dumps/sec).
        /// Chunk size is ship level + gem-capacity upgrades.
        /// </summary>
        public bool ExpelGemsHeld =>
            TryResolveKeyboard(out var keyboard) && keyboard.vKey.isPressed;

        /// <summary>
        /// True the frame the player presses T to cycle debug thruster VFX
        /// (GameManager Cycle All Thruster VFX).
        /// </summary>
        public bool CycleThrusterVfxPressed
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
                return k != null && k.tKey.wasPressedThisFrame;
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
        /// <summary>
        /// [TITAN-ORBIT] Left or Right Shift held — OVERDRIVE when combined with thrust (RMB).
        /// Desktop only; mobile has no overdrive control yet.
        /// </summary>
        public bool OverdriveHeld => overdriveHeld;

        /// <summary>
        /// [TITAN-ORBIT] C held — open the keyword comms matrix. Gameplay never reads WASD
        /// for thrust (RMB does that). C is the comms key (S used to be). Desktop only;
        /// mobile has no mapping yet. <c>ShipCommsPanel</c> owns show/send; this property
        /// is the raw key sample. The on-screen COMMS button is a separate sticky path.
        /// </summary>
        public bool CommsHeld
        {
            get
            {
                if (Application.isMobilePlatform)
                    return false;
                return TryResolveKeyboard(out var keyboard) && keyboard.cKey.isPressed;
            }
        }

        public bool IsMobile => Application.isMobilePlatform;

        /// <summary>
        /// Planar stick (x = world X, y = world Z) from the Move action plus W A D
        /// and the arrow keys. S is not included — that key toggles [S]TATS.
        /// </summary>
        public Vector2 GetMoveInput()
        {
            // --- Compute value ---
            // S is the [S]TATS toggle on the ability bar, so it is not backward thrust.
            // Down Arrow still is. Nothing in the match reads this vector for flight
            // (RMB thrusts); it stays for any caller that wants a planar stick.
            Vector2 move = Vector2.zero;
            if (moveAction != null)
                move = moveAction.ReadValue<Vector2>();

            var k = Keyboard.current;
            if (k != null)
            {
                if (k.wKey.isPressed) move.y += 1f;
                if (k.aKey.isPressed || k.leftArrowKey.isPressed) move.x -= 1f;
                if (k.dKey.isPressed || k.rightArrowKey.isPressed) move.x += 1f;
                if (k.downArrowKey.isPressed) move.y -= 1f;
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
            if (cycleBulletAction != null) cycleBulletAction.Enable();
        }

        private void OnDisable()
        {
            // --- Unity lifecycle ---
            if (moveAction != null) moveAction.Disable();
            if (shootAction != null) shootAction.Disable();
            if (lookAction != null) lookAction.Disable();
            if (rocketAction != null) rocketAction.Disable();
            if (cycleBulletAction != null) cycleBulletAction.Disable();
        }

        /// <summary>
        /// Samples shoot / thrust / brakes / Q (focused rocket or mine) every frame.
        /// After raw shoot is computed, mouse-origin fire is cleared when the pointer is over UI
        /// so HUD clicks (ability upgrades, etc.) do not fire the weapon. SPACE is a second
        /// fire hold and is not cleared by that pointer test.
        /// </summary>
        private void Update()
        {
            // --- Resolve touch vs desktop input path ---
            // TouchUiActive means MobileInputHandler owns shoot zones; desktop uses LMB / Shoot action.
            // SPACE is hold-to-fire on every path (same sustained ShootPressed as left mouse).
            MobileInputHandler mobile = MobileInputHandler.Resolve();
            bool useTouchUi = mobile != null && mobile.TouchUiActive;
            bool spaceShoot = IsSpaceHeld();

            if (useTouchUi)
            {
                // --- Touch / forced-touch shoot ---
                // Dedicated on-screen shoot button must keep working even though it is UI.
                // Mouse/action fire (editor hybrid, right-half LMB) still respects the UI gate below.
                bool actionShoot = shootAction != null && shootAction.IsPressed();
                bool editorRightHalfMouseShoot = false;
                if (MobileInputHandler.ForceTouchSteer &&
                    Mouse.current != null &&
                    Mouse.current.leftButton.isPressed &&
                    TryReadFiniteMouseScreenPosition(out Vector2 shootMouse))
                {
                    // Right half of the Game view = fire (left half owns stick / aim).
                    float edge = Screen.width * mobile.RightScreenSplit;
                    editorRightHalfMouseShoot = shootMouse.x >= edge;
                }
                bool dedicatedShootButton = mobile.ShootButtonPressed;
                bool pointerShoot = actionShoot || editorRightHalfMouseShoot;

                // Drop mouse/action fire when the press started over UI; never silence the dedicated shoot button.
                if (!dedicatedShootButton)
                    ApplyUiFireHoldGate(ref pointerShoot);

                shootPressed = dedicatedShootButton || pointerShoot || spaceShoot;

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
                // --- Desktop shoot (LMB / Shoot action, or SPACE) ---
                // Left mouse is shared with UGUI buttons — see IsPointerOverUi gate after this block.
                // SPACE stays live over those buttons; it is not a click.
                bool pointerShoot;
                if (shootAction != null)
                    pointerShoot = shootAction.IsPressed();
                else if (Mouse.current != null)
                    pointerShoot = Mouse.current.leftButton.isPressed;
                else
                    pointerShoot = false;

                // [TITAN-ORBIT] Upgrade bar blocks the press that started on it — not every hold frame.
                ApplyUiFireHoldGate(ref pointerShoot);
                shootPressed = pointerShoot || spaceShoot;

                moveForwardPressed = Mouse.current != null && Mouse.current.rightButton.isPressed;
            }

            // --- Space brakes toggle (Left or Right Ctrl) ---
            // Default ON. One Input System edge only — do not also poll legacy GetKeyDown
            // or a single press can flip twice and leave brakes off.
            if (WasCtrlPressedThisFrame())
                spaceBrakesEnabled = !spaceBrakesEnabled;

            // --- Shift modifier (Left/Right Shift) ---
            // [TITAN-ORBIT] ShipInput.Overdrive = Shift alone. Regular ships: motor latch
            // re-engages at ≥25% energy while Shift stays held; burst speed applies when
            // RMB thrust is also held. MEGAs: no overdrive — same bit locks heading and
            // aims unoccupied auto-guns at the mouse. Desktop only; mobile leaves this false.
            overdriveHeld = false;
            if (!Application.isMobilePlatform && Keyboard.current != null)
            {
                overdriveHeld = Keyboard.current.leftShiftKey.isPressed
                    || Keyboard.current.rightShiftKey.isPressed;
            }

            // --- Q activates the focused loadout pack ---
            // [TITAN-ORBIT] One key for the whole left-side list. Up/Down (and clicks)
            // move the caret. WasPressedThisFrame so holding Q does not dump the pack.
            // Q is also the FireRocket binding; the keyboard read covers a missing action.
            bool actionRocket = rocketAction != null && rocketAction.WasPressedThisFrame();
            bool keyRocket = TryResolveKeyboard(out var keys) && keys.qKey.wasPressedThisFrame;
            rocketPressed = actionRocket || keyRocket;
        }

        /// <summary>
        /// Left or Right Ctrl this frame. Uses the same keyboard resolve as B / V so a missing
        /// <c>Keyboard.current</c> (unfocused Game view) does not drop the toggle.
        /// </summary>
        static bool WasCtrlPressedThisFrame()
        {
            return TryResolveKeyboard(out var k) &&
                   (k.leftCtrlKey.wasPressedThisFrame || k.rightCtrlKey.wasPressedThisFrame);
        }

        /// <summary>
        /// SPACE held — same sustained fire as left mouse. Ignored while a text field
        /// has focus so a name or password can still contain spaces.
        /// </summary>
        static bool IsSpaceHeld()
        {
            if (!TryResolveKeyboard(out var keyboard) || !keyboard.spaceKey.isPressed)
                return false;
            return !IsTextFieldFocused();
        }

        /// <summary>
        /// Cached <c>TMPro.TMP_InputField</c>. Resolved by name so this assembly
        /// does not take a TextMesh Pro reference.
        /// </summary>
        static Type s_tmpInputFieldType;
        static bool s_tmpInputFieldResolved;

        /// <summary>True when the selected UI control is a text entry field.</summary>
        static bool IsTextFieldFocused()
        {
            var selected = EventSystem.current != null
                ? EventSystem.current.currentSelectedGameObject
                : null;
            if (selected == null)
                return false;
            if (selected.GetComponent<UnityEngine.UI.InputField>() != null)
                return true;

            if (!s_tmpInputFieldResolved)
            {
                s_tmpInputFieldResolved = true;
                s_tmpInputFieldType = Type.GetType("TMPro.TMP_InputField, Unity.TextMeshPro");
            }

            return s_tmpInputFieldType != null
                && selected.GetComponent(s_tmpInputFieldType) != null;
        }

        /// <summary>Keyboard.current, or the first Keyboard device if current is unset.</summary>
        static bool TryResolveKeyboard(out Keyboard keyboard)
        {
            keyboard = Keyboard.current;
            if (keyboard != null)
                return true;

            foreach (var d in InputSystem.devices)
            {
                if (d is Keyboard kb)
                {
                    keyboard = kb;
                    return true;
                }
            }

            return false;
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
        /// <summary>
        /// UI hit-test once when fire starts. Holding Fire (MEGA volley) must not RaycastAll
        /// every frame — that was ~3ms in the profiler.
        /// </summary>
        void ApplyUiFireHoldGate(ref bool shootPressed)
        {
            if (!shootPressed)
            {
                _fireHeld = false;
                _fireStartedOverUi = false;
                return;
            }

            if (!_fireHeld)
            {
                _fireHeld = true;
                _fireStartedOverUi = IsPointerOverUi();
            }

            if (_fireStartedOverUi)
                shootPressed = false;
        }

        private bool IsPointerOverUi()
        {
            // --- Guards ---
            // EventSystem — Unity's UI hit-test hub (created by scene or EnsureEventSystem helpers).
            var eventSystem = EventSystem.current;
            // [TITAN-ORBIT] Skip UI hit-test when MPPM reports NaN mouse (unfocused Player 2 Game view).
            if (eventSystem == null || !TryReadFiniteMouseScreenPosition(out Vector2 pointerScreen))
                return false;

            // --- Build pointer sample at current mouse screen position ---
            // PointerEventData — the payload EventSystem raycasts expect (screen pos in pixels).
            if (_uiPointerEventData == null)
                _uiPointerEventData = new PointerEventData(eventSystem);
            else
                _uiPointerEventData.Reset();

            _uiPointerEventData.position = pointerScreen;

            // --- Raycast all UI under the pointer ---
            // Any hit means a Graphic with raycastTarget=true is under the cursor (upgrade buttons, etc.).
            _uiRaycastHits.Clear();
            eventSystem.RaycastAll(_uiPointerEventData, _uiRaycastHits);
            return _uiRaycastHits.Count > 0;
        }

        /// <summary>
        /// Get mouse cursor world position (for ship rotation — ship faces toward cursor).
        /// When the pointer is unavailable (common on MPPM Player 2 while that Game view is
        /// unfocused), returns this handler's transform position so callers can fall back to
        /// "keep current facing" instead of spamming ScreenPointToRay with NaNs.
        /// </summary>
        /// <param name="cam">Gameplay camera used to unproject screen pixels onto the XZ plane.</param>
        /// <returns>World point on the play plane under the cursor, or <see cref="Transform.position"/> if aim is unavailable.</returns>
        public Vector3 GetMouseWorldPosition(UnityEngine.Camera cam)
        {
            // Prefer the bool API so call sites can leave AimPlanarDir at zero (keep ship facing).
            if (TryGetMouseWorldPosition(cam, out Vector3 worldPos))
                return worldPos;
            return transform.position;
        }

        /// <summary>
        /// Unprojects a valid pointer onto the Y=0 play plane for ship aim.
        /// Returns false when there is no usable camera/pointer — callers should keep prior aim
        /// (zero AimPlanarDir → motor uses current forward; see ShipPhysicsDriveLogic.AimWorldPoint).
        ///
        /// [TITAN-ORBIT] Multiplayer Play Mode Player 2 often reports Mouse.position as NaN while
        /// that virtual player's Game view is not focused. ScreenPointToRay then logs
        /// "Screen position out of view frustum (screen pos -nan(ind), -nan(ind))" every frame.
        /// We refuse the raycast until the sample is finite.
        /// </summary>
        /// <param name="cam">Gameplay camera (must have a finite transform / projection).</param>
        /// <param name="worldPos">Hit point on the ground plane when true; undefined when false.</param>
        /// <returns>True when <paramref name="worldPos"/> is a usable aim point.</returns>
        public bool TryGetMouseWorldPosition(UnityEngine.Camera cam, out Vector3 worldPos)
        {
            worldPos = default;

            // --- Guards: camera must exist and be numerically usable ---
            // [UNITY] A broken camera matrix (NaN position from a bad follow target) also breaks
            // ScreenPointToRay; skip until CameraFollowEcs has a real ship pose.
            if (cam == null || !IsFiniteVec3(cam.transform.position))
                return false;

            MobileInputHandler mobile = MobileInputHandler.Resolve();
            bool useTouchUi = mobile != null && mobile.TouchUiActive;

            if (useTouchUi)
            {
                // --- Touch / left-drag aim (phones + ForceTouchSteer in Editor) ---
                if (mobile.TryGetLeftDragAimWorldPoint(cam, transform, out Vector3 leftAim))
                {
                    worldPos = leftAim;
                    return true;
                }

                if (mobile.TryGetAimScreenPosition(cam, out Vector2 aimScreen) &&
                    TryScreenToPlayPlane(cam, aimScreen, out worldPos))
                    return true;

                if (mobile.JoystickDeflectedBeyondDeadZone())
                {
                    // Aim along camera-relative stick on the XZ plane (no screen unproject needed).
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
                    worldPos = transform.position + flat * 10f;
                    return true;
                }

                // Editor / hybrid: mouse aim while touch HUD is forced on.
                if (TryReadFiniteMouseScreenPosition(out Vector2 hybridMouse) &&
                    TryScreenToPlayPlane(cam, hybridMouse, out worldPos))
                    return true;

                return false;
            }

            // --- Desktop mouse aim ---
            if (!TryReadFiniteMouseScreenPosition(out Vector2 mousePos))
                return false;

            return TryScreenToPlayPlane(cam, mousePos, out worldPos);
        }

        /// <summary>
        /// Reads Mouse.current.position only when both axes are finite numbers.
        /// [TITAN-ORBIT] MPPM virtual players report NaN until their Game view owns the pointer.
        /// </summary>
        /// <param name="screenPos">Pixel coordinates in the Game view when true.</param>
        /// <returns>False when there is no mouse device or the sample is NaN/Infinity.</returns>
        static bool TryReadFiniteMouseScreenPosition(out Vector2 screenPos)
        {
            screenPos = default;
            if (Mouse.current == null)
                return false;

            Vector2 raw = Mouse.current.position.ReadValue();
            if (!IsFiniteVec2(raw))
                return false;

            screenPos = raw;
            return true;
        }

        /// <summary>
        /// Casts a screen-pixel ray onto the Y=0 play plane (top-down flight ground).
        /// Never calls ScreenPointToRay with non-finite screen coords.
        /// </summary>
        /// <param name="cam">Camera that owns the pixel space.</param>
        /// <param name="screenPos">Pixel position (origin bottom-left in Input System space).</param>
        /// <param name="worldPos">Intersection with Y=0 when the ray hits the plane.</param>
        /// <returns>True when the ray hits the ground plane.</returns>
        static bool TryScreenToPlayPlane(UnityEngine.Camera cam, Vector2 screenPos, out Vector3 worldPos)
        {
            worldPos = default;
            if (!IsFiniteVec2(screenPos))
                return false;

            // [UNITY] ScreenPointToRay logs "out of view frustum" if x/y are NaN — guard above.
            Ray ray = cam.ScreenPointToRay(new Vector3(screenPos.x, screenPos.y, 0f));
            Plane plane = new Plane(Vector3.up, Vector3.zero);
            if (!plane.Raycast(ray, out float distance))
                return false;

            worldPos = ray.GetPoint(distance);
            return IsFiniteVec3(worldPos);
        }

        /// <summary>[STANDARD] True when both components are real numbers (not NaN / Infinity).</summary>
        static bool IsFiniteVec2(Vector2 v) =>
            float.IsFinite(v.x) && float.IsFinite(v.y);

        /// <summary>[STANDARD] True when all three components are real numbers (not NaN / Infinity).</summary>
        static bool IsFiniteVec3(Vector3 v) =>
            float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    }
}
