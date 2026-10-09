using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Makes TextMeshPro input fields show a blinking caret and a lit plate when clicked.
    ///
    /// Two separate Unity issues hide that feedback in this project:
    /// 1. Player Settings use the new Input System only. While drawing the caret,
    ///    <see cref="TMP_InputField"/> asks the EventSystem for the IME (input method editor)
    ///    cursor. The default <see cref="BaseInput"/> forwards that to the old
    ///    <c>UnityEngine.Input</c> class, which throws, so the caret mesh is never submitted
    ///    and the field never finishes focusing.
    /// 2. Fields built in code assign <see cref="TMP_InputField.textComponent"/> after the
    ///    first <c>OnEnable</c>. TMP only creates the child named "Caret" during that enable,
    ///    and only when the text component is already assigned — so the blink object is missing.
    ///
    /// Client UI only. Does not touch ship simulation.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TmpInputFieldFocus : MonoBehaviour
    {
        /// <summary>Ice caret so it reads on white text and on the dark field plate.</summary>
        static readonly Color CaretColor = new Color(0.75f, 0.93f, 1f, 1f);

        /// <summary>Drag-selection wash. Shown instead of the caret while a range is selected.</summary>
        static readonly Color SelectionColor = new Color(0.35f, 0.72f, 1f, 0.45f);

        /// <summary>How fast the caret toggles, in blinks per second. TMP's default is 0.85.</summary>
        const float CaretBlinkRate = 1.15f;

        /// <summary>
        /// Caret thickness in TMP's width units. The mesh width is this times the font line height,
        /// so 2 is a readable bar on both the large name field and the smaller sign-in fields.
        /// </summary>
        const int CaretWidth = 2;

        TMP_InputField _input;
        Image _plate;
        Color _restPlate;
        bool _hasRestPlate;
        bool _focused;

        /// <summary>
        /// Patches every EventSystem so TMP can finish drawing the caret.
        /// Safe to call more than once. No-ops when no EventSystem exists yet.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void InstallCaretBridge()
        {
            // --- Find EventSystems, including ones on inactive menus ---
            var systems = Object.FindObjectsByType<EventSystem>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            for (int i = 0; i < systems.Length; i++)
                InstallCaretBridge(systems[i]);
        }

        /// <summary>
        /// Puts <see cref="TmpCaretInputBridge"/> on <paramref name="eventSystem"/> and points
        /// each input module at it. TMP reads IME state from <see cref="BaseInputModule.input"/>.
        /// </summary>
        public static void InstallCaretBridge(EventSystem eventSystem)
        {
            if (eventSystem == null)
                return;

            var bridge = eventSystem.GetComponent<TmpCaretInputBridge>();
            if (bridge == null)
                bridge = eventSystem.gameObject.AddComponent<TmpCaretInputBridge>();

            // Every module on this object. The active one is what TMP queries.
            var modules = eventSystem.GetComponents<BaseInputModule>();
            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i] != null && modules[i].inputOverride != bridge)
                    modules[i].inputOverride = bridge;
            }
        }

        /// <summary>
        /// Adds this behaviour to <paramref name="input"/> and applies caret + plate styling.
        /// Call after the text component, placeholder, and background color are assigned.
        /// </summary>
        public static void Attach(TMP_InputField input)
        {
            if (input == null)
                return;

            // Bridge first so the click that focuses this field can finish activation.
            InstallCaretBridge();

            var focus = input.GetComponent<TmpInputFieldFocus>();
            if (focus == null)
                focus = input.gameObject.AddComponent<TmpInputFieldFocus>();

            focus.ApplyPresentation();
        }

        /// <summary>Unity enable. Subscribes to TMP focus events and patches the EventSystem.</summary>
        void OnEnable()
        {
            _input = GetComponent<TMP_InputField>();
            _plate = GetComponent<Image>();
            InstallCaretBridge();

            if (_input == null)
                return;

            // TMP invokes these when the EventSystem selects or clears this field.
            _input.onSelect.AddListener(OnFieldSelected);
            _input.onDeselect.AddListener(OnFieldDeselected);
        }

        /// <summary>Unity disable. Drops listeners and puts the plate back to its resting color.</summary>
        void OnDisable()
        {
            if (_input != null)
            {
                _input.onSelect.RemoveListener(OnFieldSelected);
                _input.onDeselect.RemoveListener(OnFieldDeselected);
            }

            _focused = false;
            if (_plate != null && _hasRestPlate)
                _plate.color = _restPlate;
        }

        /// <summary>
        /// First frame this object is active. Creates the missing Caret child if TMP's first
        /// enable ran before the text component existed.
        /// </summary>
        void Start()
        {
            EnsureCaretChild();
            RaiseCaretAboveText();
        }

        /// <summary>
        /// Caret color, blink, and the resting plate color. The menu presenter sets the plate
        /// image color just before <see cref="Attach"/>, so that color is the resting fill.
        /// </summary>
        void ApplyPresentation()
        {
            if (_input == null)
                _input = GetComponent<TMP_InputField>();
            if (_plate == null)
                _plate = GetComponent<Image>();

            if (_input == null)
                return;

            // --- Caret ---
            // customCaretColor false would copy the text color (white), which disappears
            // against the glyphs. An ice bar stays visible beside and on top of the letters.
            _input.customCaretColor = true;
            _input.caretColor = CaretColor;
            _input.caretWidth = CaretWidth;
            _input.caretBlinkRate = CaretBlinkRate;
            _input.selectionColor = SelectionColor;

            // Select-all on focus draws a highlight and hides the caret until the selection
            // collapses. A click should show the blink at the pointer instead.
            _input.onFocusSelectAll = false;

            // ColorTint would multiply our plate color every state change and fight the
            // focus fill below. None leaves the Image color under our control.
            _input.transition = Selectable.Transition.None;
            if (_plate != null)
                _input.targetGraphic = _plate;

            // --- Plate ---
            // Callers set the resting fill on the Image just before Attach. If this field
            // is already focused, put the lit color back on top of that resting fill.
            if (_plate != null)
            {
                _restPlate = _plate.color;
                _hasRestPlate = true;
                if (_focused)
                    _plate.color = FocusedPlate(_restPlate);
            }
        }

        /// <summary>TMP onSelect. Lights the plate and keeps the caret drawn above the text.</summary>
        void OnFieldSelected(string _)
        {
            // Same frame as the click, before TMP's LateUpdate builds the caret mesh.
            InstallCaretBridge();
            _focused = true;
            if (_plate != null && _hasRestPlate)
                _plate.color = FocusedPlate(_restPlate);

            // Character metrics have to exist or TMP skips the caret quad entirely.
            if (_input != null && _input.textComponent != null)
                _input.textComponent.ForceMeshUpdate();

            RaiseCaretAboveText();
        }

        /// <summary>TMP onDeselect. Restores the resting plate color.</summary>
        void OnFieldDeselected(string _)
        {
            _focused = false;
            if (_plate != null && _hasRestPlate)
                _plate.color = _restPlate;
        }

        /// <summary>
        /// TMP creates a child named "Caret" only inside OnEnable, and only when
        /// <see cref="TMP_InputField.textComponent"/> is already set. Cycling the component
        /// now runs that path again. Disabling the input component does not disable this one.
        /// </summary>
        void EnsureCaretChild()
        {
            if (_input == null || _input.textComponent == null)
                return;
            if (!isActiveAndEnabled)
                return;
            if (transform.Find("Caret") != null)
                return;

            _input.enabled = false;
            _input.enabled = true;
        }

        /// <summary>
        /// TMP parents the caret as the first sibling, under the placeholder and the text.
        /// An empty field's placeholder then covers the blink. Drawing it last keeps the bar visible.
        /// The caret graphic must not take clicks — the field background is the hit target.
        /// </summary>
        void RaiseCaretAboveText()
        {
            Transform caret = transform.Find("Caret");
            if (caret == null)
                return;

            caret.SetAsLastSibling();
            var graphic = caret.GetComponent<Graphic>();
            if (graphic != null)
                graphic.raycastTarget = false;
        }

        /// <summary>Resting fill pushed toward a lit navy so the focused box is obvious.</summary>
        static Color FocusedPlate(Color rest)
        {
            return new Color(
                Mathf.Clamp01(rest.r + 0.07f),
                Mathf.Clamp01(rest.g + 0.12f),
                Mathf.Clamp01(rest.b + 0.18f),
                Mathf.Clamp01(rest.a + 0.25f));
        }
    }

    /// <summary>
    /// Stand-in <see cref="BaseInput"/> for EventSystems that use the new Input System.
    ///
    /// TMP's caret draw calls <see cref="compositionCursorPos"/> and
    /// <see cref="imeCompositionMode"/> before it uploads the caret mesh. The stock
    /// implementation talks to <c>UnityEngine.Input</c>. With Active Input Handling set to
    /// Input System Package only, those calls throw and the blink is dropped.
    /// When the old input manager is also enabled, we forward so IME composition still works.
    /// Public so Unity can add it with <c>AddComponent</c> at runtime.
    /// </summary>
    public sealed class TmpCaretInputBridge : BaseInput
    {
            /// <summary>Characters currently being composed by an IME. Empty when we cannot ask the old input manager.</summary>
            public override string compositionString
            {
                get
                {
#if ENABLE_LEGACY_INPUT_MANAGER
                    return base.compositionString;
#else
                    return string.Empty;
#endif
                }
            }

            /// <summary>IME on/off. The setter is what aborts input-field focus when it throws.</summary>
            public override IMECompositionMode imeCompositionMode
            {
                get
                {
#if ENABLE_LEGACY_INPUT_MANAGER
                    return base.imeCompositionMode;
#else
                    return IMECompositionMode.Auto;
#endif
                }
                set
                {
#if ENABLE_LEGACY_INPUT_MANAGER
                    base.imeCompositionMode = value;
#endif
                }
            }

            /// <summary>Screen position of the IME window. TMP sets this while placing the caret.</summary>
            public override Vector2 compositionCursorPos
            {
                get
                {
#if ENABLE_LEGACY_INPUT_MANAGER
                    return base.compositionCursorPos;
#else
                    return Vector2.zero;
#endif
                }
                set
                {
#if ENABLE_LEGACY_INPUT_MANAGER
                    base.compositionCursorPos = value;
#endif
                }
            }
    }
}
