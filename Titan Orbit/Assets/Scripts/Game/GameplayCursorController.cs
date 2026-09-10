using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Input;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-only OS mouse pointer swap for combat. Replaces the default arrow with one of
    /// three aim reticles: a general crosshair, a MEGA-ship reticle, or a tighter Shift
    /// manual-aim reticle. Menus, death, match-end, and touch UI restore the system arrow.
    /// <para>
    /// [TITAN-ORBIT] There is no world-space reticle. Aim already uses the OS pointer
    /// (<see cref="PlayerInputHandler"/> unprojects it onto the play plane). This component
    /// only changes the <em>shape</em> of that pointer so MEGA auto-aim and MEGA+Shift
    /// manual gun converge look different from a normal hull.
    /// </para>
    /// Presentation only — no ECS writes, no ghosts, no RPCs. Lives on NceGameRoot next to
    /// <see cref="ShipInputBridge"/>. Dedicated-server processes disable immediately.
    /// Paired with <see cref="EcsGameBridge.TryGetLocalMegaShipState"/> and
    /// <see cref="PlayerInputHandler.OverdriveHeld"/>.
    /// </summary>
    [DefaultExecutionOrder(67000)]
    public class GameplayCursorController : MonoBehaviour
    {
        /// <summary>
        /// Which pointer is showing. We only call <see cref="Cursor.SetCursor"/> when this
        /// changes so LateUpdate stays cheap (no texture upload every frame).
        /// </summary>
        enum GameplayCursorMode
        {
            /// <summary>OS arrow — menus, death, match-end, touch, or no local ship.</summary>
            SystemDefault = 0,

            /// <summary>Flying a normal L1–L6 family hull.</summary>
            General = 1,

            /// <summary>Flying a MEGA with Shift up (sticky auto-aim).</summary>
            Mega = 2,

            /// <summary>Flying a MEGA with Shift held (heading lock + mouse gun aim).</summary>
            MegaManual = 3,
        }

        /// <summary>
        /// Editor / scene paths for the three copied CleanFlatIcon line-aim textures.
        /// Used when Inspector slots are empty (runtime <c>AddComponent</c> without wiring).
        /// </summary>
        public const string GeneralCursorAssetPath = "Assets/Art/Cursors/cursor_general.png";

        /// <summary>MEGA auto-aim reticle path. See <see cref="GeneralCursorAssetPath"/>.</summary>
        public const string MegaCursorAssetPath = "Assets/Art/Cursors/cursor_mega.png";

        /// <summary>MEGA+Shift manual-aim reticle path. See <see cref="GeneralCursorAssetPath"/>.</summary>
        public const string MegaManualCursorAssetPath = "Assets/Art/Cursors/cursor_mega_manual.png";

        [Header("Cursor textures")]
        /// <summary>
        /// Simple line crosshair for a normal family hull. Texture Type must be Cursor and
        /// Read/Write enabled — <see cref="Cursor.SetCursor"/> reads the pixels on the CPU.
        /// </summary>
        [SerializeField] Texture2D generalCursor;

        /// <summary>Heavier capital-ship reticle while flying a MEGA without Shift.</summary>
        [SerializeField] Texture2D megaCursor;

        /// <summary>Tighter lock / bullseye while MEGA Shift manual-aim is held.</summary>
        [SerializeField] Texture2D megaManualCursor;

        /// <summary>
        /// Last mode we applied. Starts as an unused sentinel so the first LateUpdate
        /// always applies (we never assume the OS arrow is already showing).
        /// </summary>
        GameplayCursorMode _appliedMode = (GameplayCursorMode)(-1);

        /// <summary>Same NceGameRoot input reader — Shift maps to <see cref="PlayerInputHandler.OverdriveHeld"/>.</summary>
        PlayerInputHandler _input;

        /// <summary>Join Game overlay on this root (null if the browser was never added).</summary>
        JoinGameBrowserController _joinBrowser;

        /// <summary>Main-menu / loading flow on this root (null if missing).</summary>
        NceGameFlowController _flow;

        /// <summary>
        /// [UNITY] Domain Reload off leaves static overlay flags hot. Clear them so a leftover
        /// death / match-end plaque from the last Play Mode session cannot pin the OS arrow.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ResetStaticsBeforeSceneLoad()
        {
            DeathScreenController.ClearShowingFlag();
            MatchEndScreenController.ClearShowingFlag();
        }

        /// <summary>
        /// [UNITY] Awake runs when NceGameRoot wakes. Dedicated-server processes have no
        /// hardware cursor, so we disable and leave the OS pointer alone.
        /// </summary>
        void Awake()
        {
#if UNITY_SERVER
            enabled = false;
            return;
#endif
            // --- Dedicated server / headless ---
            // [TITAN-ORBIT] Editor Play Mode can host a server world on the same process.
            // ShouldRunClientPresentation is false only on a true dedicated-server binary.
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
            {
                enabled = false;
                return;
            }

            // --- Cache neighbors on this GameObject ---
            // GetComponent is fine once in Awake. LateUpdate must stay allocation-free.
            _input = GetComponent<PlayerInputHandler>();
            _joinBrowser = GetComponent<JoinGameBrowserController>();
            _flow = GetComponent<NceGameFlowController>();

            TryLoadDefaultTexturesIfMissing();
        }

        /// <summary>
        /// After every gameplay Update (input + overlay show/hide). Resolves the mode and
        /// uploads a new OS cursor only when it changed.
        /// </summary>
        void LateUpdate()
        {
            GameplayCursorMode next = ResolveMode();
            if (next == _appliedMode)
                return;

            ApplyMode(next);
            _appliedMode = next;
        }

        /// <summary>
        /// [UNITY] Component disabled (leave Play Mode, destroy root). Restore the OS arrow
        /// so the Editor and Main Menu do not keep a leftover combat reticle.
        /// </summary>
        void OnDisable()
        {
            RestoreSystemCursor();
            _appliedMode = (GameplayCursorMode)(-1);
        }

        /// <summary>
        /// Picks the pointer for this frame from cheap static / cached flags.
        /// No ECS gathers — <see cref="EcsGameBridge.TryGetLocalMegaShipState"/> uses the
        /// cached local-player entity.
        /// </summary>
        /// <returns>Mode to show. Overlays and "no ship" always win over combat reticles.</returns>
        GameplayCursorMode ResolveMode()
        {
            // --- Overlay / no-pointer gates ---
            // [TITAN-ORBIT] We do NOT restore the arrow on every HUD hover. Upgrade-bar
            // raycasts would flicker the cursor every time the mouse crossed a chip.
            if (UsesSystemCursorOverlay())
                return GameplayCursorMode.SystemDefault;

            // Touch UI has no hardware pointer — leave the OS default (often hidden on device).
            MobileInputHandler mobile = MobileInputHandler.Instance;
            if (mobile != null && mobile.TouchUiActive)
                return GameplayCursorMode.SystemDefault;

            // No local hull (Main Menu, connecting, join warmup without a ship).
            if (!EcsGameBridge.HasLocalPlayerShip() && !ShipDisplayPose.HasLocalPose)
                return GameplayCursorMode.SystemDefault;

            // --- Combat reticles ---
            // MEGA + Shift is manual mouse gun aim (Overdrive is reused; it is NOT OVERDRIVE
            // on a MEGA). Regular-ship Shift stays General — that key is the speed burst.
            if (EcsGameBridge.TryGetLocalMegaShipState(out MegaShipState mega) && mega.IsMega)
            {
                bool shiftHeld = _input != null && _input.OverdriveHeld;
                return shiftHeld ? GameplayCursorMode.MegaManual : GameplayCursorMode.Mega;
            }

            return GameplayCursorMode.General;
        }

        /// <summary>
        /// True when a full-screen / modal overlay should show the OS arrow so buttons
        /// feel like UI, not combat aim.
        /// </summary>
        bool UsesSystemCursorOverlay()
        {
            // --- Title / command / store overlays ---
            if (_flow != null && _flow.IsMainMenuVisible)
                return true;
            if (InGameEscapeMenuController.IsOpen)
                return true;
            if (MoonOrbitClientState.IsOrbitMenuVisible)
                return true;
            if (_joinBrowser != null && _joinBrowser.IsVisible)
                return true;

            // --- Match-flow plaques ---
            if (DeathScreenController.IsShowing)
                return true;
            if (MatchEndScreenController.IsShowing)
                return true;

            return false;
        }

        /// <summary>
        /// Pushes one texture (or null) into the OS cursor. Hotspot is the texture centre
        /// so a crosshair sits on the aim point, not the top-left corner like an arrow.
        /// </summary>
        /// <param name="mode">Mode resolved this frame.</param>
        void ApplyMode(GameplayCursorMode mode)
        {
            // --- System arrow ---
            // Cursor.SetCursor(null, …) is the Unity API for "give the OS pointer back".
            if (mode == GameplayCursorMode.SystemDefault)
            {
                RestoreSystemCursor();
                return;
            }

            Texture2D tex = TextureForMode(mode);
            if (tex == null)
            {
                RestoreSystemCursor();
                return;
            }

            // Unity hotspot is pixels from the top-left of the texture.
            Vector2 hotspot = new Vector2(tex.width * 0.5f, tex.height * 0.5f);
            Cursor.SetCursor(tex, hotspot, UnityEngine.CursorMode.Auto);
        }

        /// <summary>Serialized (or Editor-loaded) texture for a combat mode. Null if unwired.</summary>
        /// <param name="mode">Combat mode only — SystemDefault has no texture.</param>
        Texture2D TextureForMode(GameplayCursorMode mode)
        {
            switch (mode)
            {
                case GameplayCursorMode.Mega:
                    return megaCursor;
                case GameplayCursorMode.MegaManual:
                    return megaManualCursor;
                default:
                    return generalCursor;
            }
        }

        /// <summary>Hands the pointer back to the operating system.</summary>
        static void RestoreSystemCursor()
        {
            Cursor.SetCursor(null, Vector2.zero, UnityEngine.CursorMode.Auto);
        }

        /// <summary>
        /// Fills empty Inspector slots from the known Art/Cursors paths.
        /// [EDITOR] Player builds rely on scene-serialized refs from NetCodeGameSetup;
        /// AssetDatabase is stripped from those binaries.
        /// </summary>
        void TryLoadDefaultTexturesIfMissing()
        {
#if UNITY_EDITOR
            if (generalCursor == null)
                generalCursor = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(GeneralCursorAssetPath);
            if (megaCursor == null)
                megaCursor = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(MegaCursorAssetPath);
            if (megaManualCursor == null)
                megaManualCursor = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(MegaManualCursorAssetPath);
#endif
        }
    }
}
